using System.Net;
using System.Text;

namespace ImagepixelBackup.Core.Tests;

public sealed class StorageStatusTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string ApiUrl = "https://portal.example.test/api/v1";

    private static readonly StorageSettings Storage = new()
    {
        Kind = StorageKind.ImagepixelStorage,
        Bucket = "kunde-4711",
        AccessKeyId = "ACCESSKEY",
        ApiUrl = ApiUrl,
    };

    /// <summary>Answers requests the way the test says and remembers what was asked.</summary>
    private sealed class FakeNetwork(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(StorageStatusDocument document) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(document.ToJson()) };

    private static bool IsApi(HttpRequestMessage request) => request.RequestUri!.Host == "portal.example.test";

    // After the status file the application lists the bucket to see for itself how much lies in it.
    private static bool IsListing(HttpRequestMessage request) => request.RequestUri!.Query.Contains("list-type");

    private static StorageStatusDocument Document(long usedGb, string? apiUrl = null, string? apiToken = null) => new()
    {
        BookedBytes = 100 * Gb,
        UsedBytes = usedGb * Gb,
        MeasuredAt = new DateTimeOffset(2026, 11, 14, 6, 0, 0, TimeSpan.Zero),
        ApiUrl = apiUrl,
        ApiToken = apiToken,
    };

    [Fact]
    public async Task The_service_is_asked_first()
    {
        var network = new FakeNetwork(request => IsApi(request) ? Json(Document(62)) : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await new StorageStatusService(new HttpClient(network)).GetAsync(Storage, "secret", "token-123");

        Assert.Equal(StorageStatusSource.Api, result.Status!.Source);
        Assert.Equal(100 * Gb, result.Status.BookedBytes);
        Assert.Equal(0.62, result.Status.Fraction, 3);
        Assert.False(result.Status.IsWarning);

        var request = Assert.Single(network.Requests);
        Assert.Equal("https://portal.example.test/api/v1/storage-status", request.RequestUri!.ToString());
        Assert.Equal("Bearer token-123", request.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(null)]
    public async Task The_status_file_is_read_when_the_service_gives_no_answer(HttpStatusCode? apiAnswer)
    {
        var network = new FakeNetwork(request =>
        {
            if (!IsApi(request))
            {
                return Json(Document(86));
            }

            return apiAnswer is { } code ? new HttpResponseMessage(code) : throw new HttpRequestException("no route to host");
        });

        var result = await new StorageStatusService(new HttpClient(network)).GetAsync(Storage, "secret", "token-123");

        Assert.Equal(StorageStatusSource.StatusFile, result.Status!.Source);
        Assert.True(result.Status.IsWarning);
        Assert.False(result.Status.IsFull);

        // the file is fetched from the bucket of the customer with a signed request
        var file = network.Requests.Last(request => !IsListing(request));
        Assert.Equal("https://s3.eu-central-2.wasabisys.com/kunde-4711/imagepixel/storage-status.json", file.RequestUri!.ToString());
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=ACCESSKEY/", file.Headers.GetValues("Authorization").Single());
        Assert.Contains("/eu-central-2/s3/aws4_request", file.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Without_a_service_only_the_status_file_is_read()
    {
        var storage = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde-4711", AccessKeyId = "ACCESSKEY" };
        var network = new FakeNetwork(_ => Json(Document(104)));

        var result = await new StorageStatusService(new HttpClient(network)).GetAsync(storage, "secret", null);

        Assert.Single(network.Requests, request => !IsListing(request));
        Assert.DoesNotContain(network.Requests, IsApi);
        Assert.Equal(StorageStatusSource.StatusFile, result.Status!.Source);
        Assert.True(result.Status.IsFull);
    }

    [Fact]
    public async Task There_are_no_figures_when_neither_the_service_nor_the_file_has_any()
    {
        var network = new FakeNetwork(request => new HttpResponseMessage(IsApi(request) ? HttpStatusCode.BadGateway : HttpStatusCode.NotFound));
        var service = new StorageStatusService(new HttpClient(network));

        Assert.Null((await service.GetAsync(Storage, "secret", "token-123")).Status);

        // a file that is damaged or says nothing is treated the same
        var damaged = new FakeNetwork(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{ \"bookedBytes\": 0 ") });
        Assert.Null((await new StorageStatusService(new HttpClient(damaged)).GetAsync(Storage, "secret", null)).Status);

        // and a local folder has no booked size at all
        var folder = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = @"E:\Backup" };
        Assert.Null((await service.GetAsync(folder, null, null)).Status);
    }

    [Fact]
    public async Task The_status_file_can_announce_the_service()
    {
        var storage = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde-4711", AccessKeyId = "ACCESSKEY" };
        var network = new FakeNetwork(_ => Json(Document(10, ApiUrl, "neues-token")));

        var result = await new StorageStatusService(new HttpClient(network)).GetAsync(storage, "secret", null);

        Assert.Equal(ApiUrl, result.ApiUrl);
        Assert.Equal("neues-token", result.ApiToken);

        // an address without encryption is not taken over
        var plain = new FakeNetwork(_ => Json(Document(10, "http://portal.example.test/api", "token")));
        Assert.Null((await new StorageStatusService(new HttpClient(plain)).GetAsync(storage, "secret", null)).ApiUrl);
    }

    [Fact]
    public async Task The_application_remembers_a_service_the_status_file_announced()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipb-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var asked = new List<string>();
            var network = new FakeNetwork(request =>
            {
                if (IsListing(request))
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                asked.Add(IsApi(request) ? "service" : "file");
                return IsApi(request) ? Json(Document(70)) : Json(Document(60, ApiUrl, "neues-token"));
            });
            var secrets = new MemorySecretStore();
            secrets.Set(SecretNames.RepositoryPassword, "password");
            secrets.Set(SecretNames.SecretAccessKey, "secret");
            var manager = new BackupManager(new ConfigStore(directory), secrets, "restic", null, new StorageStatusService(new HttpClient(network)));
            manager.Config.Storage = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde-4711", AccessKeyId = "ACCESSKEY" };

            var first = await manager.GetStorageStatusAsync();
            var second = await manager.GetStorageStatusAsync();

            Assert.Equal(StorageStatusSource.StatusFile, first!.Source);
            Assert.Equal(StorageStatusSource.Api, second!.Source);
            Assert.Equal(["file", "service"], asked);
            Assert.Equal(ApiUrl, new ConfigStore(directory).Load().Storage!.ApiUrl);
            Assert.DoesNotContain("neues-token", File.ReadAllText(Path.Combine(directory, "config.json")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Requests_are_signed_as_the_storage_service_expects()
    {
        // example "GET Object" from the documentation of AWS Signature Version 4
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ["x-amz-date"] = "20130524T000000Z",
        };

        var authorization = S3Client.Authorization(
            "GET", "/test.txt", "", headers,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            "us-east-1", "s3", new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request,"
            + "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date,"
            + "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            authorization);
    }

    [Theory]
    [InlineData(0, 16777371L)] // the file was written before the first backup: what lies in the bucket counts
    [InlineData(62, 62L * 1024 * 1024 * 1024)] // the file counts more than this computer can see: its figure stays
    public async Task What_lies_in_the_bucket_is_measured_when_the_file_is_behind(long usedGbInFile, long expectedBytes)
    {
        const string listing = """
            <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <IsTruncated>false</IsTruncated>
              <Contents><Key>config</Key><Size>155</Size></Contents>
              <Contents><Key>data/00/abc</Key><Size>16777216</Size></Contents>
              <Contents><Key>imagepixel/storage-status.json</Key><Size>120</Size></Contents>
            </ListBucketResult>
            """;
        var network = new FakeNetwork(request => request.RequestUri!.Query.Contains("list-type")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listing, Encoding.UTF8, "application/xml") }
            : Json(Document(usedGbInFile)));
        var storage = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde-4711", AccessKeyId = "ACCESSKEY" };

        var result = await new StorageStatusService(new HttpClient(network)).GetAsync(storage, "secret", null);

        Assert.Equal(StorageStatusSource.StatusFile, result.Status!.Source);
        Assert.Equal(100 * Gb, result.Status.BookedBytes);
        Assert.Equal(expectedBytes, result.Status.UsedBytes);

        // a fresh measurement carries the time it was made, the file's figure the time of the file
        var fresh = usedGbInFile == 0;
        Assert.Equal(fresh, result.Status.MeasuredAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task The_figures_of_the_file_stay_when_the_bucket_cannot_be_listed()
    {
        var network = new FakeNetwork(request => request.RequestUri!.Query.Contains("list-type")
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Json(Document(7)));
        var storage = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde-4711", AccessKeyId = "ACCESSKEY" };

        var result = await new StorageStatusService(new HttpClient(network)).GetAsync(storage, "secret", null);

        Assert.Equal(7 * Gb, result.Status!.UsedBytes);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 6, 0, 0, TimeSpan.Zero), result.Status.MeasuredAt);
    }

    [Fact]
    public async Task Listing_a_bucket_follows_the_pages_of_the_answer()
    {
        const string first = """
            <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <IsTruncated>true</IsTruncated><NextContinuationToken>weiter+/=</NextContinuationToken>
              <Contents><Key>config</Key><Size>155</Size></Contents>
              <Contents><Key>data/00/abc</Key><Size>16777216</Size></Contents>
            </ListBucketResult>
            """;
        const string second = """
            <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <IsTruncated>false</IsTruncated>
              <Contents><Key>imagepixel/storage-status.json</Key><Size>120</Size></Contents>
            </ListBucketResult>
            """;
        var network = new FakeNetwork(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Query.Contains("continuation-token") ? second : first, Encoding.UTF8, "application/xml"),
        });
        var client = S3Client.For(new HttpClient(network), Storage, "secret");

        var objects = new List<(string Key, long Size)>();
        await foreach (var item in client.ListObjectsAsync())
        {
            objects.Add(item);
        }

        Assert.Equal([("config", 155L), ("data/00/abc", 16777216L), ("imagepixel/storage-status.json", 120L)], objects);
        Assert.Equal("?list-type=2", network.Requests[0].RequestUri!.Query);
        Assert.Equal("?continuation-token=weiter%2B%2F%3D&list-type=2", network.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public void Setup_code_can_carry_the_address_of_the_service()
    {
        var code = new SetupCode("kunde-4711", "KEY", "secret", "eu-central-2", ApiUrl, "token-123");

        Assert.True(SetupCode.TryDecode(code.Encode(), out var decoded));
        Assert.Equal(code, decoded);

        // a code without a service stays as short as before and still works
        var plain = new SetupCode("kunde-4711", "KEY", "secret", "eu-central-2");
        Assert.True(SetupCode.TryDecode(plain.Encode(), out var decodedPlain));
        Assert.Null(decodedPlain!.ApiUrl);
        Assert.True(plain.Encode().Length < code.Encode().Length);
    }
}
