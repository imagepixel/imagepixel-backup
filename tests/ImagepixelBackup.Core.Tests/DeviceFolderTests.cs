using System.Net;
using System.Text;
using System.Text.Json;

namespace ImagepixelBackup.Core.Tests;

/// <summary>
/// A setup code whose access key reaches only the folder of one computer in the bucket,
/// as the control centre makes them for each device: the folder comes with the code
/// (<c>p</c>), and everything the application does with the storage stays inside it.
/// </summary>
public sealed class DeviceFolderTests
{
    private const string Root = "geraete/imagepixel-backup-geraet-3f6c2a1e";

    // a code as the control centre makes it, with the folder in the form it writes
    private static SetupCode Made(string root = Root + "/", string deviceName = "Notebook Anna") =>
        new("kunde-beispiel", "EXAMPLEACCESSKEY0001", "beispiel-geheim", "eu-central-2", Root: root, DeviceName: deviceName);

    // the same code as the application reads it after it was pasted
    private static SetupCode Code(string root = Root + "/") =>
        SetupCode.TryDecode(Made(root).Encode(), out var decoded) ? decoded! : throw new InvalidOperationException("The code is turned down.");

    // what the setup makes of a code: the storage of the code, with backups going to folder inside it
    private static StorageSettings Storage(SetupCode code, string folder = "") => new()
    {
        Kind = StorageKind.ImagepixelStorage,
        Bucket = code.Bucket,
        Region = code.Region,
        AccessKeyId = code.AccessKeyId,
        Root = code.Root,
        DeviceName = code.DeviceName,
        Folder = folder,
    };

    [Fact]
    public void The_folder_and_the_name_of_the_computer_come_with_the_setup_code()
    {
        Assert.True(SetupCode.TryDecode(Made().Encode(), out var decoded));
        Assert.Equal(Root, decoded!.Root);
        Assert.Equal("Notebook Anna", decoded.DeviceName);

        // and it goes out again in the same form
        Assert.True(SetupCode.TryDecode(decoded.Encode(), out var again));
        Assert.Equal(decoded, again);

        // the way the control centre writes it: base64url of the JSON, without padding
        var json = $$"""{"b":"kunde-beispiel","k":"KEY","s":"geheim","r":"eu-central-2","p":"{{Root}}/","n":" Notebook Anna "}""";
        var text = "IPB1-" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(SetupCode.TryDecode(text, out var written));
        Assert.Equal((Root, "Notebook Anna"), (written!.Root, written.DeviceName));

        // a code without them reaches the whole bucket, as before
        Assert.True(SetupCode.TryDecode(new SetupCode("kunde-beispiel", "KEY", "geheim", "eu-central-2").Encode(), out var plain));
        Assert.Equal(("", ""), (plain!.Root, plain.DeviceName));
    }

    [Theory]
    [InlineData("geraete/../server/")]
    [InlineData("imagepixel/")]
    [InlineData("data/")]
    [InlineData("geraete/<name>/")]
    public void A_setup_code_with_a_folder_the_storage_cannot_hold_is_turned_down(string root)
    {
        Assert.False(SetupCode.TryDecode(Made(root).Encode(), out _));
    }

    [Fact]
    public void The_secret_of_a_setup_code_never_shows_when_it_is_written_out()
    {
        var code = new SetupCode("kunde-beispiel", "EXAMPLEACCESSKEY0001", "beispiel-geheim", "eu-central-2", "https://status.example.test", "beispiel-token", Root);

        Assert.Contains("EXAMPLEACCESSKEY0001", code.ToString());
        Assert.DoesNotContain("beispiel-geheim", code.ToString());
        Assert.DoesNotContain("beispiel-token", code.ToString());
    }

    [Fact]
    public void Every_backup_of_the_computer_lies_in_its_own_folder()
    {
        var storage = Storage(Code());

        Assert.Equal($"s3:https://s3.eu-central-2.wasabisys.com/kunde-beispiel/{Root}", storage.RepositoryUrl);
        Assert.Equal(Root, storage.PathOf(""));

        // destinations lie inside it, and so does everything that reaches the storage itself
        var notebook = storage.WithFolder("Büro/Notebook");
        Assert.Equal($"s3:https://s3.eu-central-2.wasabisys.com/kunde-beispiel/{Root}/Büro/Notebook", notebook.RepositoryUrl);
        Assert.Equal((Root, "Notebook Anna"), (notebook.Root, notebook.DeviceName));
        Assert.Equal($"{Root}/Fotos", notebook.PathOf("Fotos"));

        var arguments = EncryptedView.CreateStartInfo("rclone", storage, "geheim", "", "S:", "Backup").ArgumentList.ToList();
        Assert.Contains($"imagepixel:kunde-beispiel/{Root}", arguments);
        Assert.DoesNotContain("--exclude", arguments);
        Assert.Contains($"imagepixel:kunde-beispiel/{Root}/Fotos",
            EncryptedView.CreateStartInfo("rclone", storage, "geheim", "Fotos", "S:", "Backup").ArgumentList);

        // the settings come back as they were saved
        var config = new AppConfig { Storage = notebook };
        var saved = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!.Storage!;
        Assert.Equal(notebook.RepositoryUrl, saved.RepositoryUrl);
    }

    [Fact]
    public void Another_folder_of_the_bucket_is_another_storage()
    {
        var device = Storage(Code());
        var wholeBucket = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde-beispiel", AccessKeyId = "OTHER" };

        Assert.False(BackupManager.IsSameStorage(wholeBucket, device));
        Assert.False(BackupManager.IsSameStorage(device, Storage(Code("geraete/anderer/"))));

        // the same computer with a new key and another destination
        var renewed = device.WithFolder("Fotos");
        renewed.AccessKeyId = "NEW";
        Assert.True(BackupManager.IsSameStorage(device, renewed));

        // a folder on a disk has no such part
        var disk = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Path.GetTempPath(), Root = "geraete/x" };
        Assert.Equal("", disk.PathOf(""));
    }

    [Fact]
    public async Task The_backups_of_the_computer_are_looked_for_in_its_own_folder()
    {
        var asked = new List<string>();
        var network = new FakeNetwork(request =>
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            asked.Add(path + Uri.UnescapeDataString(request.RequestUri.Query));
            if (request.RequestUri.Query.Contains("list-type"))
            {
                return Xml($"""
                    <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                      <IsTruncated>false</IsTruncated>
                      <CommonPrefixes><Prefix>{Root}/Fotos/</Prefix></CommonPrefixes>
                      <CommonPrefixes><Prefix>{Root}/Notebook/</Prefix></CommonPrefixes>
                      <CommonPrefixes><Prefix>{Root}/data/</Prefix></CommonPrefixes>
                    </ListBucketResult>
                    """);
            }

            return path == $"/kunde-beispiel/{Root}/Notebook/config" || path == $"/kunde-beispiel/{Root}/config"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("config") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var found = await new BackupFinder(new HttpClient(network)).FindAsync(Storage(Code()), "geheim");

        Assert.Equal(new[] { "", "Notebook" }, found);

        // nothing outside the folder of the computer is asked for
        var listing = Assert.Single(asked, request => request.Contains("list-type"));
        Assert.Contains($"prefix={Root}/", listing);
        Assert.Contains("delimiter=/", listing);
        Assert.All(asked.Where(request => !request.Contains("list-type")), request => Assert.StartsWith($"/kunde-beispiel/{Root}/", request));
    }

    [Fact]
    public async Task The_computer_measures_only_its_own_folder()
    {
        var listings = new List<string>();
        var network = new FakeNetwork(request =>
        {
            if (request.RequestUri!.Query.Contains("list-type"))
            {
                listings.Add(Uri.UnescapeDataString(request.RequestUri.Query));
                return Xml($"""
                    <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                      <IsTruncated>false</IsTruncated>
                      <Contents><Key>{Root}/data/aa</Key><Size>5000</Size></Contents>
                    </ListBucketResult>
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new StorageStatusDocument { BookedBytes = 10_000, UsedBytes = 1000, MeasuredAt = DateTimeOffset.UtcNow }.ToJson()),
            };
        });

        var result = await new StorageStatusService(new HttpClient(network)).GetAsync(Storage(Code()), "geheim", null);

        Assert.Equal(5000, result.Status!.UsedBytes);
        Assert.Contains($"prefix={Root}/", Assert.Single(listings));
    }

    private static HttpResponseMessage Xml(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    private sealed class FakeNetwork(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
