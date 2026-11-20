using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagepixelBackup.Core;

/// <summary>Something about the pairing with the customer portal cannot be used.</summary>
public sealed class PairingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The code that pairs a computer with the customer portal. The application shows it,
/// the customer types it into the portal. It has 8 characters from an alphabet without
/// letters and digits that look alike, shown as two groups of four.
/// </summary>
public static class PairingCode
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public const int Length = 8;

    /// <summary>The code without spaces and hyphens, in capitals: the way it is compared and encrypted with.</summary>
    public static string Normalize(string? code) =>
        new((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

    /// <summary>The code the way it is shown, such as "K7QM-4TZP".</summary>
    public static string Format(string? code)
    {
        var normalized = Normalize(code);
        return normalized.Length == Length ? $"{normalized[..4]}-{normalized[4..]}" : normalized;
    }

    public static bool IsValid(string? code)
    {
        var normalized = Normalize(code);
        return normalized.Length == Length && normalized.All(c => Alphabet.Contains(c));
    }
}

/// <summary>
/// How the access data travel from the customer portal to the computer: encrypted for
/// a key pair that is made on the computer and whose private key never leaves it.
/// </summary>
/// <remarks>
/// The sender makes a key pair of its own for each message (P-256), agrees on a secret
/// with the public key of the computer (ECDH, the x coordinate, 32 bytes), derives the
/// key from it with HKDF-SHA256 (16 random bytes of salt, the code as information) and
/// encrypts with AES-256-GCM (12 random bytes of nonce, the information as additional
/// data, a tag of 16 bytes). The message is the JSON object
/// <c>{"v":1,"epk":…,"salt":…,"nonce":…,"ct":…}</c> with the public key of the sender
/// as SubjectPublicKeyInfo in DER form, and the encrypted text followed by the tag, all
/// in Base64. Taking the code into the information ties the message to this one pairing.
/// </remarks>
public static class PairingCipher
{
    /// <summary>What the information for the key starts with; the code follows.</summary>
    public const string InfoPrefix = "imagepixel-backup-kopplung-v1|";

    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;

    // the object identifier of the curve P-256
    private const string P256 = "1.2.840.10045.3.1.7";

    /// <summary>The information the key is derived with: the prefix and the code without hyphens, in capitals.</summary>
    public static byte[] Info(string code) => Encoding.UTF8.GetBytes(InfoPrefix + PairingCode.Normalize(code));

    /// <summary>
    /// Makes the key pair of a computer: the private key as PKCS#8, the public key as
    /// SubjectPublicKeyInfo, both in DER form. The public key has 91 bytes.
    /// </summary>
    public static (byte[] PrivateKey, byte[] PublicKey) CreateDeviceKey()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKey(), key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The SHA-256 of the secret in lower-case hex: what the portal keeps of it.</summary>
    public static string SecretHash(byte[] secret) => Convert.ToHexStringLower(SHA256.HashData(secret));

    /// <summary>Opens a message with the private key of the computer.</summary>
    /// <exception cref="PairingException">The message is damaged, made for another key or another code.</exception>
    public static byte[] Decrypt(string message, byte[] devicePrivateKey, string code)
    {
        Sealed parts;
        byte[] senderKey, salt, nonce, encrypted;
        try
        {
            parts = JsonSerializer.Deserialize<Sealed>(message) ?? throw new PairingException("The access data are empty.");
            senderKey = Convert.FromBase64String(parts.SenderKey ?? "");
            salt = Convert.FromBase64String(parts.Salt ?? "");
            nonce = Convert.FromBase64String(parts.Nonce ?? "");
            encrypted = Convert.FromBase64String(parts.Encrypted ?? "");
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            throw new PairingException("The access data are not in the expected form.", e);
        }

        if (parts.Version != 1)
        {
            throw new PairingException($"The access data come in version {parts.Version}, which this application does not know.");
        }

        if (salt.Length == 0 || nonce.Length != NonceLength || encrypted.Length < TagLength)
        {
            throw new PairingException("The access data are not in the expected form.");
        }

        var info = Info(code);
        byte[]? shared = null;
        byte[]? key = null;
        try
        {
            using var device = ECDiffieHellman.Create();
            device.ImportPkcs8PrivateKey(devicePrivateKey, out _);
            using var sender = ECDiffieHellman.Create();
            sender.ImportSubjectPublicKeyInfo(senderKey, out _);
            if (sender.ExportParameters(false).Curve.Oid?.Value != P256)
            {
                throw new PairingException("The access data were encrypted with a key of another kind.");
            }

            shared = device.DeriveRawSecretAgreement(sender.PublicKey);
            key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, KeyLength, salt, info);

            var plain = new byte[encrypted.Length - TagLength];
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, encrypted.AsSpan(0, plain.Length), encrypted.AsSpan(plain.Length), plain, info);
            return plain;
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException or ArgumentException)
        {
            // Windows turns down a point that is not on the curve as an unsupported key
            throw new PairingException("The access data cannot be decrypted with the key of this computer.", e);
        }
        finally
        {
            if (shared is not null)
            {
                CryptographicOperations.ZeroMemory(shared);
            }

            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    /// <summary>
    /// Encrypts a message for the public key of a computer, the way the portal side does.
    /// The application itself only decrypts; this is for tests and tools.
    /// </summary>
    public static string Encrypt(byte[] plain, byte[] devicePublicKey, string code)
    {
        var info = Info(code);
        using var device = ECDiffieHellman.Create();
        device.ImportSubjectPublicKeyInfo(devicePublicKey, out _);
        using var sender = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var shared = sender.DeriveRawSecretAgreement(device.PublicKey);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, KeyLength, salt, info);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);

        var encrypted = new byte[plain.Length + TagLength];
        using (var aes = new AesGcm(key, TagLength))
        {
            aes.Encrypt(nonce, plain, encrypted.AsSpan(0, plain.Length), encrypted.AsSpan(plain.Length), info);
        }

        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(key);

        return JsonSerializer.Serialize(new Sealed(
            1,
            Convert.ToBase64String(sender.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(encrypted)));
    }

    private sealed record Sealed(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("epk")] string? SenderKey,
        [property: JsonPropertyName("salt")] string? Salt,
        [property: JsonPropertyName("nonce")] string? Nonce,
        [property: JsonPropertyName("ct")] string? Encrypted);
}

/// <summary>
/// The access data of a computer, as the customer portal hands them over when the
/// computer has been paired: its own key for its own folder in the bucket of the
/// customer. The short names are those of the setup code.
/// </summary>
/// <param name="DeviceId">The number the portal knows the computer by.</param>
/// <param name="DeviceName">The name the customer gave the computer in the portal.</param>
/// <param name="Bucket">The bucket of the customer.</param>
/// <param name="Region">The region the bucket is in.</param>
/// <param name="AccessKeyId">The access key of the computer.</param>
/// <param name="SecretAccessKey">The secret that belongs to it.</param>
/// <param name="Root">The folder of the bucket the key is limited to, in the form <see cref="StorageFolder.Normalize"/> gives it.</param>
/// <param name="ApiUrl">The address of the service that tells how much storage is booked and used, if there is one.</param>
/// <param name="ApiToken">The token to send to that service.</param>
public sealed record DeviceAccess(
    string DeviceId,
    string DeviceName,
    string Bucket,
    string Region,
    string AccessKeyId,
    string SecretAccessKey,
    string Root,
    string? ApiUrl = null,
    string? ApiToken = null)
{
    /// <summary>Reads the decrypted access data. Fields this version does not use are left aside.</summary>
    /// <exception cref="PairingException">Something the application needs is missing.</exception>
    public static DeviceAccess Parse(ReadOnlySpan<byte> json)
    {
        Payload payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json) ?? throw new PairingException("The access data are empty.");
        }
        catch (JsonException e)
        {
            throw new PairingException("The access data are not in the expected form.", e);
        }

        if (payload.Version != 1)
        {
            throw new PairingException($"The access data come in version {payload.Version}, which this application does not know.");
        }

        if (string.IsNullOrWhiteSpace(payload.Bucket)
            || string.IsNullOrWhiteSpace(payload.AccessKeyId)
            || string.IsNullOrWhiteSpace(payload.SecretAccessKey))
        {
            throw new PairingException("The access data lack the bucket or the key.");
        }

        // every backup of the computer lies in this folder, so it must be one
        var root = StorageFolder.Normalize(payload.Root);
        if (root.Length == 0 || StorageFolder.Check(root) != StorageFolderProblem.None)
        {
            throw new PairingException("The access data name no usable folder for the backups of this computer.");
        }

        return new DeviceAccess(
            payload.DeviceId?.Trim() ?? "",
            payload.DeviceName?.Trim() ?? "",
            payload.Bucket.Trim(),
            string.IsNullOrWhiteSpace(payload.Region) ? ImagepixelStorage.DefaultRegion : payload.Region.Trim(),
            payload.AccessKeyId.Trim(),
            payload.SecretAccessKey.Trim(),
            root,
            string.IsNullOrWhiteSpace(payload.ApiUrl) ? null : payload.ApiUrl.Trim(),
            string.IsNullOrWhiteSpace(payload.ApiToken) ? null : payload.ApiToken.Trim());
    }

    /// <summary>Names the access data without the secret and the token, so that no log or message shows them.</summary>
    public override string ToString() =>
        $"DeviceAccess {{ DeviceId = {DeviceId}, DeviceName = {DeviceName}, Bucket = {Bucket}, Region = {Region}, AccessKeyId = {AccessKeyId}, Root = {Root} }}";

    /// <summary>The storage these access data reach, with backups going to <paramref name="folder"/> inside the folder of the computer.</summary>
    public StorageSettings ToStorage(string folder = "") => new()
    {
        Kind = StorageKind.ImagepixelStorage,
        Bucket = Bucket,
        Region = Region,
        AccessKeyId = AccessKeyId,
        ApiUrl = ApiUrl ?? "",
        Root = Root,
        DeviceName = DeviceName,
        DeviceId = DeviceId,
        Folder = folder,
    };

    private sealed record Payload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("g")] string? DeviceId,
        [property: JsonPropertyName("n")] string? DeviceName,
        [property: JsonPropertyName("b")] string? Bucket,
        [property: JsonPropertyName("r")] string? Region,
        [property: JsonPropertyName("k")] string? AccessKeyId,
        [property: JsonPropertyName("s")] string? SecretAccessKey,
        [property: JsonPropertyName("p")] string? Root,
        [property: JsonPropertyName("a")] string? ApiUrl,
        [property: JsonPropertyName("t")] string? ApiToken);
}
