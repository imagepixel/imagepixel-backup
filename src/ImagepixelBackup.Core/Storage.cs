namespace ImagepixelBackup.Core;

/// <summary>The kinds of storage a backup can be written to.</summary>
public enum StorageKind
{
    /// <summary>A bucket provided by Imagepixel. The customer only enters the data they were given.</summary>
    ImagepixelStorage,

    /// <summary>A folder, for example on a USB drive or a network share.</summary>
    LocalFolder,
}

/// <summary>
/// Imagepixel Storage: S3 compatible buckets that Imagepixel sets up for its customers.
/// They are hosted by Wasabi, which is why the endpoints carry its name.
/// </summary>
public static class ImagepixelStorage
{
    /// <summary>The region buckets are created in unless a setup code says otherwise.</summary>
    public const string DefaultRegion = "eu-central-2";

    /// <summary>The host name of the storage service for a region.</summary>
    public static string Endpoint(string region) =>
        string.IsNullOrWhiteSpace(region) || region == "us-east-1"
            ? "s3.wasabisys.com"
            : $"s3.{region.Trim()}.wasabisys.com";
}

/// <summary>Where the backup is stored. Secrets are kept separately in an <see cref="ISecretStore"/>.</summary>
public sealed class StorageSettings
{
    public StorageKind Kind { get; set; }

    /// <summary>Bucket name, for <see cref="StorageKind.ImagepixelStorage"/>.</summary>
    public string Bucket { get; set; } = "";

    /// <summary>Region of the bucket, for <see cref="StorageKind.ImagepixelStorage"/>.</summary>
    public string Region { get; set; } = ImagepixelStorage.DefaultRegion;

    public string AccessKeyId { get; set; } = "";

    /// <summary>Folder that holds the repository, for <see cref="StorageKind.LocalFolder"/>.</summary>
    public string LocalPath { get; set; } = "";

    /// <summary>
    /// Address of the service of Imagepixel that tells how much storage is booked and
    /// used. Empty as long as the application has not been told about one.
    /// </summary>
    public string ApiUrl { get; set; } = "";

    public bool IsS3 => Kind == StorageKind.ImagepixelStorage;

    /// <summary>The repository location in the syntax restic expects.</summary>
    public string RepositoryUrl => IsS3
        ? $"s3:https://{ImagepixelStorage.Endpoint(Region)}/{Bucket}"
        : LocalPath;
}

/// <summary>Everything restic needs to open a repository.</summary>
public sealed record RepositoryAccess(
    string RepositoryUrl,
    string Password,
    string? AccessKeyId = null,
    string? SecretAccessKey = null,
    string? Region = null)
{
    public static RepositoryAccess From(StorageSettings storage, string password, string? secretAccessKey) =>
        storage.IsS3
            ? new RepositoryAccess(storage.RepositoryUrl, password, storage.AccessKeyId, secretAccessKey, storage.Region)
            : new RepositoryAccess(storage.RepositoryUrl, password);

    /// <summary>
    /// Passes the access data to a restic process through its environment, so that
    /// nothing secret appears on the command line.
    /// </summary>
    public void ApplyTo(IDictionary<string, string?> environment)
    {
        environment["RESTIC_REPOSITORY"] = RepositoryUrl;
        environment["RESTIC_PASSWORD"] = Password;
        environment.Remove("RESTIC_PASSWORD_FILE");
        environment.Remove("RESTIC_PASSWORD_COMMAND");
        environment.Remove("RESTIC_REPOSITORY_FILE");

        if (AccessKeyId is not null)
        {
            environment["AWS_ACCESS_KEY_ID"] = AccessKeyId;
            environment["AWS_SECRET_ACCESS_KEY"] = SecretAccessKey ?? "";
            environment["AWS_DEFAULT_REGION"] = Region ?? "";
            environment.Remove("AWS_SESSION_TOKEN");
            environment.Remove("AWS_PROFILE");
        }
    }
}

/// <summary>Rules about how the folders of a backup may relate to each other.</summary>
public static class FolderRules
{
    /// <summary>
    /// Returns whether one folder is the other or lies inside it. A backup must not be
    /// stored inside a folder it backs up: it would back up itself and grow with every run.
    /// </summary>
    public static bool Overlap(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        var a = WithSeparator(first);
        var b = WithSeparator(second);
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    // "C:\Daten" must not match "C:\Daten alt", so both paths end with a separator
    private static string WithSeparator(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
}

/// <summary>A folder cannot be backed up because the backup is stored inside or around it.</summary>
public sealed class FolderOverlapException(string folder, string storage)
    : InvalidOperationException($"The folder {folder} and the storage {storage} lie inside each other.")
{
    public string Folder { get; } = folder;

    public string Storage { get; } = storage;
}
