using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagepixelBackup.Core;

/// <summary>
/// A single line of text that carries everything a customer needs to connect to an
/// Imagepixel Storage bucket. It is meant to be copied and pasted, not to be secret
/// on its own terms: it contains the access key in readable form.
/// </summary>
public sealed record SetupCode(string Bucket, string AccessKeyId, string SecretAccessKey, string Region)
{
    private const string Prefix = "IPB1-";

    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Payload(Bucket, AccessKeyId, SecretAccessKey, Region));
        return Prefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

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

            code = new SetupCode(
                payload.Bucket,
                payload.AccessKeyId,
                payload.SecretAccessKey,
                string.IsNullOrWhiteSpace(payload.Region) ? ImagepixelStorage.DefaultRegion : payload.Region);
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
        [property: JsonPropertyName("r")] string? Region);
}
