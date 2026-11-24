using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagepixelBackup.Core;

/// <summary>
/// A single line of text that carries everything a customer needs to connect to an
/// Imagepixel Storage bucket. It is meant to be copied and pasted, not to be secret
/// on its own terms: it contains the access key in readable form.
/// </summary>
/// <remarks>
/// The text is <c>IPB1-</c> followed by the JSON object <c>{"b","k","s","r","p","n","a","t"}</c>
/// in Base64 for addresses, without padding. <c>b</c>, <c>k</c> and <c>s</c> are needed;
/// the others are optional. Fields this version does not know are left aside.
/// </remarks>
/// <param name="Bucket">The bucket of the customer.</param>
/// <param name="AccessKeyId">The access key of the customer.</param>
/// <param name="SecretAccessKey">The secret that belongs to the access key.</param>
/// <param name="Region">The region the bucket is in.</param>
/// <param name="ApiUrl">The address of the service that tells how much storage is in use, if there is one.</param>
/// <param name="ApiToken">The token to send to that service.</param>
/// <param name="Root">
/// The folder of the bucket the access key is limited to, such as the folder of one
/// computer, in the form <see cref="StorageFolder.Normalize"/> gives it; empty for a key
/// that reaches the whole bucket. Every backup made with the code lies inside it.
/// </param>
/// <param name="DeviceName">The name of the computer the code was made for, or empty.</param>
public sealed record SetupCode(
    string Bucket,
    string AccessKeyId,
    string SecretAccessKey,
    string Region,
    string? ApiUrl = null,
    string? ApiToken = null,
    string Root = "",
    string DeviceName = "")
{
    private const string Prefix = "IPB1-";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(
                Bucket,
                AccessKeyId,
                SecretAccessKey,
                Region,
                ApiUrl,
                ApiToken,
                Root.Length > 0 ? Root + "/" : null,
                DeviceName.Length > 0 ? DeviceName : null),
            JsonOptions);
        return Prefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Names the code without the secret and the token, so that no log or message shows them.</summary>
    public override string ToString() =>
        $"SetupCode {{ Bucket = {Bucket}, AccessKeyId = {AccessKeyId}, Region = {Region}, Root = {Root}, DeviceName = {DeviceName} }}";

    public static bool TryDecode(string? text, out SetupCode? code)
    {
        code = null;
        var trimmed = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var base64 = trimmed[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var payload = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.Bucket)
                || string.IsNullOrWhiteSpace(payload.AccessKeyId)
                || string.IsNullOrWhiteSpace(payload.SecretAccessKey))
            {
                return false;
            }

            // every backup made with the code lies in this folder, so it must be one the storage can hold
            var root = StorageFolder.Normalize(payload.Root);
            if (StorageFolder.Check(root) != StorageFolderProblem.None)
            {
                return false;
            }

            code = new SetupCode(
                payload.Bucket,
                payload.AccessKeyId,
                payload.SecretAccessKey,
                string.IsNullOrWhiteSpace(payload.Region) ? ImagepixelStorage.DefaultRegion : payload.Region,
                string.IsNullOrWhiteSpace(payload.ApiUrl) ? null : payload.ApiUrl,
                string.IsNullOrWhiteSpace(payload.ApiToken) ? null : payload.ApiToken,
                root,
                payload.DeviceName?.Trim() ?? "");
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(
        [property: JsonPropertyName("b")] string Bucket,
        [property: JsonPropertyName("k")] string AccessKeyId,
        [property: JsonPropertyName("s")] string SecretAccessKey,
        [property: JsonPropertyName("r")] string? Region,
        [property: JsonPropertyName("a")] string? ApiUrl = null,
        [property: JsonPropertyName("t")] string? ApiToken = null,
        [property: JsonPropertyName("p")] string? Root = null,
        [property: JsonPropertyName("n")] string? DeviceName = null);
}
