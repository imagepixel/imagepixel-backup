using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagepixelBackup.Core;

/// <summary>Where the figures about the storage came from.</summary>
public enum StorageStatusSource
{
    /// <summary>The service of Imagepixel answered.</summary>
    Api,

    /// <summary>The status file in the storage of the customer was read.</summary>
    StatusFile,
}

/// <summary>How much storage a customer has booked and how much of it is used.</summary>
public sealed record StorageStatus(
    long BookedBytes,
    long UsedBytes,
    DateTimeOffset MeasuredAt,
    StorageStatusSource Source)
{
    /// <summary>From this share of the booked size on, the customer is warned.</summary>
    public const double WarningThreshold = 0.8;

    public double Fraction => BookedBytes > 0 ? (double)UsedBytes / BookedBytes : 0;

    public bool IsWarning => Fraction >= WarningThreshold;

    public bool IsFull => Fraction >= 1;
}

/// <summary>
/// The document that describes the storage of a customer. The service of Imagepixel
/// returns it, and the same document is kept as a file in the storage itself, so that
/// the application still has figures when the service cannot be reached.
/// </summary>
public sealed class StorageStatusDocument
{
    /// <summary>The key of the status file in the bucket of the customer.</summary>
    public const string ObjectKey = "imagepixel/storage-status.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("bookedBytes")]
    public long BookedBytes { get; set; }

    [JsonPropertyName("usedBytes")]
    public long UsedBytes { get; set; }

    [JsonPropertyName("measuredAt")]
    public DateTimeOffset MeasuredAt { get; set; }

    /// <summary>
    /// Where the service of Imagepixel can be reached. A status file can carry it, so
    /// that applications learn about the service without being set up again.
    /// </summary>
    [JsonPropertyName("apiUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiUrl { get; set; }

    /// <summary>The token to send to the service, see <see cref="ApiUrl"/>.</summary>
    [JsonPropertyName("apiToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiToken { get; set; }

    public static StorageStatusDocument? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<StorageStatusDocument>(json, JsonOptions);
            return document is { BookedBytes: > 0, UsedBytes: >= 0 } ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public StorageStatus ToStatus(StorageStatusSource source) => new(BookedBytes, UsedBytes, MeasuredAt, source);
}

/// <summary>What a look at the storage status brought back.</summary>
/// <param name="Status">The figures, or null if neither the service nor the status file had any.</param>
/// <param name="ApiUrl">The address of the service as named by the status file, if it named one.</param>
/// <param name="ApiToken">The token for the service as named by the status file, if it named one.</param>
public sealed record StorageStatusResult(StorageStatus? Status, string? ApiUrl = null, string? ApiToken = null)
{
    public static StorageStatusResult None { get; } = new((StorageStatus?)null);
}

/// <summary>
/// Finds out how much storage is booked and used. The service of Imagepixel is asked
/// first. If it is not set up, not reachable or answers with an error, the status
/// file in the storage is read instead.
/// </summary>
public sealed class StorageStatusService(HttpClient http)
{
    /// <summary>The path of the status below the address of the service.</summary>
    public const string ApiPath = "storage-status";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(8);

    public async Task<StorageStatusResult> GetAsync(
        StorageSettings storage,
        string? secretAccessKey,
        string? apiToken,
        CancellationToken cancellationToken = default)
    {
        if (!storage.IsS3)
        {
            return StorageStatusResult.None;
        }

        if (await AskServiceAsync(storage.ApiUrl, apiToken, cancellationToken) is { } fromService)
        {
            return new StorageStatusResult(fromService.ToStatus(StorageStatusSource.Api));
        }

        if (string.IsNullOrEmpty(secretAccessKey))
        {
            return StorageStatusResult.None;
        }

        try
        {
            var bytes = await S3Client.For(http, storage, secretAccessKey)
                .GetObjectAsync(StorageStatusDocument.ObjectKey, cancellationToken);
            if (bytes is null || StorageStatusDocument.Parse(bytes) is not { } fromFile)
            {
                return StorageStatusResult.None;
            }

            return new StorageStatusResult(
                fromFile.ToStatus(StorageStatusSource.StatusFile),
                IsUsableApiUrl(fromFile.ApiUrl) ? fromFile.ApiUrl : null,
                fromFile.ApiToken);
        }
        catch (Exception e) when (e is HttpRequestException or S3Exception or TaskCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StorageStatusResult.None;
        }
    }

    /// <summary>The service is only contacted over an encrypted connection.</summary>
    public static bool IsUsableApiUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private async Task<StorageStatusDocument?> AskServiceAsync(string? apiUrl, string? apiToken, CancellationToken cancellationToken)
    {
        if (!IsUsableApiUrl(apiUrl) || string.IsNullOrEmpty(apiToken))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl!.TrimEnd('/') + "/" + ApiPath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return StorageStatusDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // not reachable or too slow: the status file is the fallback
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}
