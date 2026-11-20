using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace ImagepixelBackup.Core;

/// <summary>A request to the storage service failed.</summary>
public sealed class S3Exception(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>
/// The few storage operations the application needs besides restic: reading and
/// writing a small object and listing what a bucket contains. Requests are signed
/// with AWS Signature Version 4, which the storage service expects.
/// </summary>
public sealed class S3Client(
    HttpClient http,
    string endpoint,
    string region,
    string bucket,
    string accessKeyId,
    string secretAccessKey)
{
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>The current time. Replaceable for tests.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public static S3Client For(HttpClient http, StorageSettings storage, string secretAccessKey) =>
        new(http, ImagepixelStorage.Endpoint(storage.Region), storage.Region, storage.Bucket, storage.AccessKeyId, secretAccessKey);

    /// <summary>Reads an object. Returns null if it does not exist.</summary>
    public async Task<byte[]?> GetObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, ObjectPath(key), "", null, null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task PutObjectAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Put, ObjectPath(key), "", content, contentType, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Deletes an object. An object that does not exist counts as deleted.</summary>
    public async Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, ObjectPath(key), "", null, null, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }
    }

    /// <summary>Lists the objects of the bucket whose keys start with a prefix.</summary>
    public async IAsyncEnumerable<(string Key, long Size)> ListObjectsAsync(
        string prefix = "",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var page in ListPagesAsync(prefix, null, cancellationToken))
        {
            var ns = page.Name.Namespace;
            foreach (var item in page.Elements(ns + "Contents"))
            {
                yield return (
                    item.Element(ns + "Key")!.Value,
                    long.Parse(item.Element(ns + "Size")!.Value, CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// Lists the folders at the top of the bucket, or inside the folder <paramref name="prefix"/>
    /// names with a slash at its end: what the keys there start with up to the next slash.
    /// The names come without the prefix and without the slash.
    /// </summary>
    public async IAsyncEnumerable<string> ListFoldersAsync(
        string prefix = "",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var page in ListPagesAsync(prefix, "/", cancellationToken))
        {
            var ns = page.Name.Namespace;
            foreach (var item in page.Elements(ns + "CommonPrefixes"))
            {
                if (item.Element(ns + "Prefix")?.Value is { } full
                    && full.StartsWith(prefix, StringComparison.Ordinal)
                    && full[prefix.Length..].TrimEnd('/') is { Length: > 0 } name)
                {
                    yield return name;
                }
            }
        }
    }

    // A listing comes in pages of at most a thousand entries; each page names the next one.
    private async IAsyncEnumerable<XElement> ListPagesAsync(
        string prefix,
        string? delimiter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? continuation = null;
        do
        {
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["list-type"] = "2" };
            if (prefix.Length > 0)
            {
                query["prefix"] = prefix;
            }

            if (delimiter is not null)
            {
                query["delimiter"] = delimiter;
            }

            if (continuation is not null)
            {
                query["continuation-token"] = continuation;
            }

            var queryString = string.Join("&", query.Select(p => $"{Encode(p.Key)}={Encode(p.Value)}"));
            using var response = await SendAsync(HttpMethod.Get, "/" + bucket, queryString, null, null, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            var root = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).Root!;
            yield return root;

            var ns = root.Name.Namespace;
            continuation = string.Equals(root.Element(ns + "IsTruncated")?.Value, "true", StringComparison.OrdinalIgnoreCase)
                ? root.Element(ns + "NextContinuationToken")?.Value
                : null;
        }
        while (continuation is not null);
    }

    private string ObjectPath(string key) =>
        "/" + bucket + "/" + string.Join("/", key.Split('/').Select(Encode));

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string query,
        byte[]? content,
        string? contentType,
        CancellationToken cancellationToken)
    {
        var now = Clock().UtcDateTime;
        var payloadHash = content is null ? EmptyPayloadHash : Hex(SHA256.HashData(content));
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = endpoint,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };

        var uri = new UriBuilder("https", endpoint) { Path = path, Query = query }.Uri;
        var request = new HttpRequestMessage(method, uri);
        if (content is not null)
        {
            request.Content = new ByteArrayContent(content);
            if (contentType is not null)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            Authorization(method.Method, path, query, headers, payloadHash, accessKeyId, secretAccessKey, region, "s3", now));

        return await http.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Computes the Authorization header of AWS Signature Version 4. The path must be
    /// URI-encoded already and the query sorted by key.
    /// </summary>
    internal static string Authorization(
        string method,
        string path,
        string query,
        SortedDictionary<string, string> headers,
        string payloadHash,
        string accessKeyId,
        string secretAccessKey,
        string region,
        string service,
        DateTime utcNow)
    {
        var date = utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var amzDate = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var scope = $"{date}/{region}/{service}/aws4_request";
        var signedHeaders = string.Join(";", headers.Keys);

        var canonicalRequest = string.Join(
            "\n",
            method,
            path,
            query,
            string.Concat(headers.Select(h => $"{h.Key}:{h.Value.Trim()}\n")),
            signedHeaders,
            payloadHash);

        var stringToSign = string.Join(
            "\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var key = Encoding.UTF8.GetBytes("AWS4" + secretAccessKey);
        foreach (var part in new[] { date, region, service, "aws4_request" })
        {
            key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(part));
        }

        var signature = Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
        return $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope},SignedHeaders={signedHeaders},Signature={signature}";
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        try
        {
            var error = XDocument.Parse(body).Root;
            if (error?.Element("Code")?.Value is { } code)
            {
                message = $"{code}: {error.Element("Message")?.Value}";
            }
        }
        catch (System.Xml.XmlException)
        {
            // not an error document of the storage service
        }

        throw new S3Exception(response.StatusCode, message);
    }

    // RFC 3986: everything except unreserved characters is percent-encoded
    private static string Encode(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
