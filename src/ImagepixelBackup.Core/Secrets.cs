using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImagepixelBackup.Core;

/// <summary>Stores secrets such as the backup password, protected for the current user.</summary>
public interface ISecretStore
{
    string? Get(string name);

    /// <summary>Stores a secret. A null value removes it.</summary>
    void Set(string name, string? value);
}

/// <summary>The names of the secrets the application stores.</summary>
public static class SecretNames
{
    public const string RepositoryPassword = "repository-password";
    public const string SecretAccessKey = "secret-access-key";
}

public static class SecretStore
{
    /// <summary>Creates the secret store for the current operating system.</summary>
    public static ISecretStore Create(string? directory = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiSecretStore(directory ?? ConfigStore.DefaultDirectory);
        }

        throw new PlatformNotSupportedException(
            "Storing secrets is only implemented for Windows so far. macOS needs a Keychain based store.");
    }
}

/// <summary>
/// Keeps secrets in a file encrypted with the Windows Data Protection API. Only the
/// Windows user who stored them can read them again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string directory) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ImagepixelBackup.Secrets.v1");
    private readonly Lock _lock = new();

    private string FilePath => Path.Combine(directory, "secrets.dat");

    public string? Get(string name)
    {
        lock (_lock)
        {
            return Read().GetValueOrDefault(name);
        }
    }

    public void Set(string name, string? value)
    {
        lock (_lock)
        {
            var secrets = Read();
            if (value is null)
            {
                secrets.Remove(name);
            }
            else
            {
                secrets[name] = value;
            }

            Directory.CreateDirectory(directory);
            var plain = JsonSerializer.SerializeToUtf8Bytes(secrets);
            var temporary = FilePath + ".tmp";
            File.WriteAllBytes(temporary, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            File.Move(temporary, FilePath, overwrite: true);
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
    }
}

/// <summary>Keeps secrets in memory only. Used by tests.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public string? Get(string name) => _secrets.GetValueOrDefault(name);

    public void Set(string name, string? value)
    {
        if (value is null)
        {
            _secrets.Remove(name);
        }
        else
        {
            _secrets[name] = value;
        }
    }
}
