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

    /// <summary>
    /// The folder inside the storage that backups go to unless a folder that is backed up
    /// has a destination of its own (<see cref="BackupFolder.Destination"/>), in the form
    /// <see cref="StorageFolder.Normalize"/> gives it. Empty for directly in the bucket or
    /// in the folder of the backup, which is where every backup made before folders
    /// existed is.
    /// </summary>
    public string Folder { get; set; } = "";

    public bool IsS3 => Kind == StorageKind.ImagepixelStorage;

    /// <summary>The repository location in the syntax restic expects.</summary>
    public string RepositoryUrl => IsS3
        ? $"s3:https://{ImagepixelStorage.Endpoint(Region)}/{Bucket}" + (Folder.Length > 0 ? "/" + Folder : "")
        : Folder.Length > 0 ? Path.Combine(LocalPath, Folder.Replace('/', Path.DirectorySeparatorChar)) : LocalPath;

    /// <summary>The same storage with the same access, but another folder inside it.</summary>
    public StorageSettings WithFolder(string folder) => new()
    {
        Kind = Kind,
        Bucket = Bucket,
        Region = Region,
        AccessKeyId = AccessKeyId,
        LocalPath = LocalPath,
        ApiUrl = ApiUrl,
        Folder = folder,
    };
}

/// <summary>What keeps a name from being used as the folder of a backup inside a storage.</summary>
public enum StorageFolderProblem
{
    None,

    /// <summary>A character that is not allowed, or a part of the name that says nothing.</summary>
    Characters,

    /// <summary>The storage itself uses the name.</summary>
    Reserved,

    TooLong,
}

/// <summary>
/// The folder inside an Imagepixel Storage that a backup is kept in. With a folder each,
/// several computers can share one storage without their backups getting mixed up.
/// </summary>
public static class StorageFolder
{
    public const int MaxLength = 100;

    // What lies at the top of a storage already: the files of Imagepixel, and the parts
    // of a backup that is kept without a folder. A folder of the same name would be
    // mixed up with them.
    private static readonly string[] ReservedNames = ["imagepixel", "config", "data", "index", "keys", "locks", "snapshots"];

    /// <summary>
    /// Brings what was typed into the form it is stored in: parts separated by single
    /// forward slashes, none at the ends, no spaces around the parts.
    /// </summary>
    public static string Normalize(string? value)
    {
        var parts = (value ?? "")
            .Normalize(System.Text.NormalizationForm.FormC)
            .Split('/', '\\')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0);

        return string.Join('/', parts);
    }

    /// <summary>Says what is wrong with a folder that <see cref="Normalize"/> returned. No folder at all is fine.</summary>
    public static StorageFolderProblem Check(string folder)
    {
        if (folder.Length == 0)
        {
            return StorageFolderProblem.None;
        }

        if (folder.Length > MaxLength)
        {
            return StorageFolderProblem.TooLong;
        }

        var parts = folder.Split('/');

        // Letters and digits of any language, and the few signs that mean nothing in an
        // address. A part of dots alone would point at the folder itself or the one above.
        if (parts.Any(part => part.Length == 0 || part.All(c => c == '.') || !part.All(IsAllowed)))
        {
            return StorageFolderProblem.Characters;
        }

        return IsReserved(parts[0]) ? StorageFolderProblem.Reserved : StorageFolderProblem.None;
    }

    /// <summary>Whether the storage itself uses a name at its top.</summary>
    public static bool IsReserved(string name) => ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsAllowed(char c) => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.';
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
        return a.StartsWith(b, PathComparison) || b.StartsWith(a, PathComparison);
    }

    /// <summary>
    /// How paths are compared on this computer: Linux tells "Daten" and "daten" apart,
    /// Windows and macOS take them for the same folder.
    /// </summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Compares paths as <see cref="PathComparison"/> does.</summary>
    public static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Whether a path restic recorded for a snapshot is the folder. restic writes the
    /// path as it got it, on Windows sometimes in the form "/C/Users/...". On macOS and
    /// Linux it writes the real path where the way to the folder leads through a link,
    /// as "/var" does on macOS, which is "/private/var".
    /// </summary>
    public static bool IsSameFolder(string recorded, string folder)
    {
        static string Key(string path)
        {
            var key = path.Replace('\\', '/').TrimEnd('/');
            return key.Length >= 3 && key[0] == '/' && char.IsAsciiLetter(key[1]) && key[2] == '/'
                ? key[1] + ":" + key[2..]
                : key;
        }

        if (string.Equals(Key(recorded), Key(folder), PathComparison))
        {
            return true;
        }

        return !OperatingSystem.IsWindows()
            && string.Equals(Key(RealPath(recorded)), Key(RealPath(folder)), PathComparison);
    }

    /// <summary>
    /// The path with every link on the way to it followed, as the system resolves it.
    /// A part that cannot be looked at is taken as it is.
    /// </summary>
    internal static string RealPath(string path) => RealPath(path, 0);

    // A link may point at a path that leads through links itself; a circle of links ends after some rounds.
    private static string RealPath(string path, int depth)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            try
            {
                if (depth < 32 && new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    next = RealPath(target.FullName, depth + 1);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // taken as it is
            }

            current = next;
        }

        return current;
    }

    // "C:\Daten" must not match "C:\Daten alt", so both paths end with a separator
    private static string WithSeparator(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
}

/// <summary>The backup cannot be opened under a drive letter because something else uses that letter.</summary>
public sealed class DriveLetterInUseException(string letter)
    : InvalidOperationException($"The drive letter {letter} is in use.")
{
    public string Letter { get; } = letter;
}

/// <summary>A destination holds a backup that the password of the application does not open.</summary>
public sealed class DestinationPasswordException(string destination)
    : InvalidOperationException($"The backup at the destination \"{destination}\" has another password.")
{
    public string Destination { get; } = destination;
}

/// <summary>A folder cannot be backed up because the backup is stored inside or around it.</summary>
public sealed class FolderOverlapException(string folder, string storage)
    : InvalidOperationException($"The folder {folder} and the storage {storage} lie inside each other.")
{
    public string Folder { get; } = folder;

    public string Storage { get; } = storage;
}
