namespace ImagepixelBackup.Core;

/// <summary>
/// Looks into an Imagepixel Storage for the backups it holds. A backup lies either
/// directly in the bucket or in a folder at its top, see <see cref="StorageSettings.Folder"/>;
/// for access data limited to the folder of one computer, in that folder or a folder
/// inside it (<see cref="StorageSettings.Root"/>).
/// Knowing them, the application can offer the folders instead of asking the customer
/// to remember how one was spelled.
/// </summary>
public sealed class BackupFinder(HttpClient http)
{
    // restic keeps a small file of this name at the top of every repository
    private const string Marker = "config";

    // A storage holds a handful of computers. More folders than this are not looked into.
    private const int MaxFolders = 50;

    /// <summary>
    /// Returns the folders that hold a backup, with an empty name for a backup that
    /// lies directly in the bucket. The list is empty if there is none, and also if the
    /// storage cannot be read: this is a help, the customer can always type the folder.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindAsync(
        StorageSettings storage,
        string? secretAccessKey,
        CancellationToken cancellationToken = default)
    {
        if (!storage.IsS3)
        {
            return FindInFolder(storage.LocalPath);
        }

        if (string.IsNullOrEmpty(secretAccessKey))
        {
            return [];
        }

        try
        {
            // access data limited to the folder of one computer reach nothing else of the bucket
            var top = storage.PathOf("");
            var prefix = top.Length > 0 ? top + "/" : "";

            var client = S3Client.For(http, storage, secretAccessKey);
            var found = new List<string>();
            if (await client.GetObjectAsync(prefix + Marker, cancellationToken) is not null)
            {
                found.Add("");
            }

            var looked = 0;
            await foreach (var folder in client.ListFoldersAsync(prefix, cancellationToken))
            {
                // what the storage itself keeps at its top is no backup of its own
                if (StorageFolder.Check(folder) != StorageFolderProblem.None)
                {
                    continue;
                }

                if (++looked > MaxFolders)
                {
                    break;
                }

                if (await client.GetObjectAsync($"{prefix}{folder}/{Marker}", cancellationToken) is not null)
                {
                    found.Add(folder);
                }
            }

            return found;
        }
        catch (Exception e) when (e is HttpRequestException or S3Exception or TaskCanceledException or System.Xml.XmlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return [];
        }
    }

    // The same for the folder of a backup on a disk: its subfolders that hold a backup.
    private static IReadOnlyList<string> FindInFolder(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return [];
            }

            var found = new List<string>();
            if (File.Exists(Path.Combine(root, Marker)))
            {
                found.Add("");
            }

            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.CurrentCultureIgnoreCase).Take(MaxFolders))
            {
                var name = Path.GetFileName(directory);
                if (StorageFolder.Check(name) == StorageFolderProblem.None && File.Exists(Path.Combine(directory, Marker)))
                {
                    found.Add(name);
                }
            }

            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
