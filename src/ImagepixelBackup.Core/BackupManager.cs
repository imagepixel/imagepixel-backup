using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.Core;

/// <summary>
/// The operations of the application: connecting to a storage, managing the folders
/// to back up, and running backups, restores and mounts against the repository.
/// </summary>
public sealed class BackupManager(
    ConfigStore store,
    ISecretStore secrets,
    string resticPath,
    ICloudFileSystem? cloudFiles = null,
    StorageStatusService? storageStatus = null,
    BackupFinder? finder = null,
    HttpClient? http = null)
{
    // what the application asks the storage itself, besides what restic does
    private static readonly HttpClient SharedHttp = new();

    private readonly HttpClient _http = http ?? SharedHttp;

    private readonly ICloudFileSystem? _cloudFiles =
        cloudFiles ?? (OperatingSystem.IsWindows() ? new WindowsCloudFileSystem() : null);

    private readonly StorageStatusService _storageStatus = storageStatus ?? new StorageStatusService(http ?? SharedHttp);

    private readonly BackupFinder _finder = finder ?? new BackupFinder(http ?? SharedHttp);

    private readonly Lock _saveLock = new();
    private readonly SemaphoreSlim _backupGate = new(1, 1);

    public AppConfig Config { get; } = store.Load();

    /// <summary>Whether a storage has been set up and its password is available.</summary>
    public bool IsConnected
    {
        get
        {
            try
            {
                return Config.Storage is not null && secrets.Get(SecretNames.RepositoryPassword) is not null;
            }
            catch (System.Security.SecurityException)
            {
                // The keyring cannot be reached. The application then asks for the
                // storage and the password again instead of failing to start.
                return false;
            }
        }
    }

    /// <summary>
    /// Checks the storage and remembers it if the password opens the repository.
    /// A missing repository is only created when <paramref name="createIfMissing"/> is set.
    /// </summary>
    public async Task<RepositoryState> ConnectAsync(
        StorageSettings storage,
        string password,
        string? secretAccessKey,
        bool createIfMissing,
        CancellationToken cancellationToken = default,
        string? apiToken = null)
    {
        if (storage.Kind == StorageKind.LocalFolder
            && Config.Folders.FirstOrDefault(f => FolderRules.Overlap(f.Path, storage.LocalPath)) is { } overlapping)
        {
            throw new FolderOverlapException(overlapping.Path, storage.LocalPath);
        }

        var client = new ResticClient(resticPath, RepositoryAccess.From(storage, password, secretAccessKey));

        var state = await client.ProbeAsync(cancellationToken);
        if (state == RepositoryState.NotFound && createIfMissing)
        {
            await client.InitAsync(cancellationToken);
            state = RepositoryState.Ready;
        }

        if (state == RepositoryState.Ready)
        {
            // Another place holds other backups, or none yet. What the folders remember
            // of their runs belongs to the place they ran against.
            if (Config.Storage is { } previous && previous.RepositoryUrl != storage.RepositoryUrl)
            {
                if (IsSameStorage(previous, storage))
                {
                    // The same storage with another destination for folders added from now on:
                    // the folders that are backed up keep going where they went.
                    foreach (var folder in Config.Folders.Where(folder => folder.Destination is null))
                    {
                        folder.Destination = previous.Folder;
                    }
                }
                else
                {
                    Config.Folders = Config.Folders.Select(folder => folder.ForAnotherPlace()).ToList();

                    // Deletions that waited belong to the previous storage. In the new one a
                    // destination of the same name holds other backups, which must not be touched.
                    Config.Cleanups.Clear();
                }
            }

            secrets.Set(SecretNames.RepositoryPassword, password);
            secrets.Set(SecretNames.SecretAccessKey, storage.IsS3 ? secretAccessKey : null);
            secrets.Set(SecretNames.ApiToken, storage.IsS3 ? apiToken : null);
            Config.Storage = storage;
            Save();
        }

        return state;
    }

    /// <summary>
    /// Finds the folders of an Imagepixel Storage that hold a backup, see
    /// <see cref="BackupFinder"/>. Without arguments it looks into the storage that is
    /// connected, with the access data the application holds.
    /// </summary>
    public Task<IReadOnlyList<string>> FindBackupFoldersAsync(
        StorageSettings? storage = null,
        string? secretAccessKey = null,
        CancellationToken cancellationToken = default)
    {
        if (storage is null)
        {
            if (Config.Storage is not { } current)
            {
                return Task.FromResult<IReadOnlyList<string>>([]);
            }

            storage = current;
            secretAccessKey = secrets.Get(SecretNames.SecretAccessKey);
        }

        return _finder.FindAsync(storage, secretAccessKey, cancellationToken);
    }

    /// <summary>The destination in the storage the backups of a folder go to.</summary>
    public string DestinationOf(BackupFolder folder) => folder.Destination ?? Config.Storage?.Folder ?? "";

    /// <summary>
    /// The destinations that may hold backups of this computer: the one the storage was
    /// set up with, those of the folders, and those whose backups wait to be deleted.
    /// </summary>
    public IReadOnlyList<string> KnownDestinations =>
    [
        .. new[] { Config.Storage?.Folder ?? "" }
            .Concat(Config.Folders.Select(DestinationOf))
            .Concat(Config.Cleanups.Select(cleanup => cleanup.Destination))
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Lets the backups of a folder go to another destination in the storage from now on.
    /// A destination that holds no backup yet is created with the password of the
    /// application. One that holds a backup the password does not open is refused, and
    /// nothing changes. What was saved at the previous destination stays there; if
    /// <paramref name="deleteOldAfterDays"/> is given, the backups of this folder there are
    /// deleted once that many days have passed (see <see cref="RunDueCleanupsAsync"/>).
    /// </summary>
    /// <param name="folder">A folder that is backed up. Its backup must not be running.</param>
    /// <param name="destination">The new destination, as the user typed it.</param>
    /// <param name="deleteOldAfterDays">After how many days to delete the old backups, or null to keep them.</param>
    /// <param name="cancellationToken">Cancels the change before anything is saved.</param>
    public async Task ChangeDestinationAsync(
        BackupFolder folder,
        string destination,
        int? deleteOldAfterDays,
        CancellationToken cancellationToken = default)
    {
        var normalized = StorageFolder.Normalize(destination);
        if (StorageFolder.Check(normalized) != StorageFolderProblem.None)
        {
            throw new ArgumentException($"\"{destination}\" cannot be a destination.", nameof(destination));
        }

        var previous = DestinationOf(folder);
        if (normalized == previous)
        {
            return;
        }

        var client = CreateClient(normalized);
        switch (await client.ProbeAsync(cancellationToken))
        {
            case RepositoryState.NotFound:
                await client.InitAsync(cancellationToken);
                break;
            case RepositoryState.WrongPassword:
                throw new DestinationPasswordException(normalized);
        }

        var now = DateTimeOffset.Now;
        folder.Destination = normalized;
        folder.ForgetRuns();

        // Going back to a destination whose backups were about to be deleted keeps them,
        // and an answer given before for the previous destination gives way to this one.
        Config.Cleanups.RemoveAll(cleanup =>
            FolderRules.IsSameFolder(cleanup.FolderPath, folder.Path)
            && (cleanup.Destination == normalized || cleanup.Destination == previous));

        if (deleteOldAfterDays is { } days)
        {
            Config.Cleanups.Add(new PendingCleanup
            {
                Destination = previous,
                FolderPath = folder.Path,
                CreatedAt = now,
                DueAt = now.AddDays(Math.Max(0, days)),
            });
        }

        Save();
    }

    /// <summary>The deletions of old backups of a folder that are waiting.</summary>
    public IReadOnlyList<PendingCleanup> CleanupsOf(BackupFolder folder) =>
        [.. Config.Cleanups.Where(cleanup => FolderRules.IsSameFolder(cleanup.FolderPath, folder.Path)).OrderBy(cleanup => cleanup.DueAt)];

    /// <summary>Keeps the old backups of a folder after all: the deletions that wait for them are dropped.</summary>
    public void KeepOldBackups(BackupFolder folder)
    {
        if (Config.Cleanups.RemoveAll(cleanup => FolderRules.IsSameFolder(cleanup.FolderPath, folder.Path)) > 0)
        {
            Save();
        }
    }

    /// <summary>
    /// Deletes the old backups whose time has come: in each destination, the snapshots of
    /// the folder and the data only they used. Backups of other folders there stay. A
    /// destination left without any snapshot, that no folder goes to and that is not the
    /// one of the storage, is removed altogether. Runs between backups, never during one.
    /// A deletion that fails is tried again an hour later.
    /// </summary>
    public async Task<IReadOnlyList<CleanupResult>> RunDueCleanupsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CleanupResult>();
        var due = Config.Cleanups
            .Where(cleanup => cleanup.DueAt <= now && (cleanup.RetryAt is null || cleanup.RetryAt <= now))
            .ToList();

        foreach (var cleanup in due)
        {
            await _backupGate.WaitAsync(cancellationToken);
            try
            {
                results.Add(await RunCleanupAsync(cleanup, now, cancellationToken));
            }
            finally
            {
                _backupGate.Release();
            }
        }

        return results;
    }

    private async Task<CleanupResult> RunCleanupAsync(PendingCleanup cleanup, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // the folder goes there again: its backups there are in use and stay
        if (Config.Folders.Any(folder =>
                FolderRules.IsSameFolder(folder.Path, cleanup.FolderPath) && DestinationOf(folder) == cleanup.Destination))
        {
            Config.Cleanups.Remove(cleanup);
            Save();
            return new CleanupResult(cleanup.Destination, cleanup.FolderPath, 0, false, null);
        }

        try
        {
            var client = CreateClient(cleanup.Destination);
            var exists = await client.ProbeAsync(cancellationToken) != RepositoryState.NotFound;
            var snapshots = exists ? await client.GetSnapshotsAsync(cancellationToken) : [];

            var ofFolder = snapshots
                .Where(snapshot => snapshot.Paths.Any(path => FolderRules.IsSameFolder(path, cleanup.FolderPath)))
                .Select(snapshot => snapshot.Id)
                .ToList();
            await client.ForgetAsync(ofFolder, cancellationToken);

            var removed = exists && ofFolder.Count == snapshots.Count
                && cleanup.Destination != (Config.Storage?.Folder ?? "")
                && !Config.Folders.Any(folder => DestinationOf(folder) == cleanup.Destination);
            if (removed)
            {
                await DeleteRepositoryAsync(cleanup.Destination, cancellationToken);
            }

            Config.Cleanups.Remove(cleanup);
            Save();
            return new CleanupResult(cleanup.Destination, cleanup.FolderPath, ofFolder.Count, removed, null);
        }
        catch (Exception e) when (e is ResticException or IOException or UnauthorizedAccessException
                                      or HttpRequestException or S3Exception or System.Xml.XmlException)
        {
            cleanup.LastError = e.Message;
            cleanup.RetryAt = now.AddHours(1);
            Save();
            return new CleanupResult(cleanup.Destination, cleanup.FolderPath, 0, false, e.Message);
        }
    }

    // The parts of a repository that restic keeps at its top. Only they are removed, so
    // that nothing else is touched, not even at the top of a bucket: the files of
    // Imagepixel and the folders of other destinations stay.
    private static readonly string[] RepositoryFolders = ["keys", "data", "index", "snapshots", "locks"];

    internal async Task DeleteRepositoryAsync(string destination, CancellationToken cancellationToken = default)
    {
        var storage = Config.Storage ?? throw new InvalidOperationException("No storage has been set up.");
        if (storage.IsS3)
        {
            var client = S3Client.For(_http, storage, secrets.Get(SecretNames.SecretAccessKey) ?? "");
            var prefix = destination.Length > 0 ? destination + "/" : "";
            foreach (var folder in RepositoryFolders)
            {
                var keys = new List<string>();
                await foreach (var (key, _) in client.ListObjectsAsync(prefix + folder + "/", cancellationToken))
                {
                    keys.Add(key);
                }

                foreach (var key in keys)
                {
                    await client.DeleteObjectAsync(key, cancellationToken);
                }
            }

            await client.DeleteObjectAsync(prefix + "config", cancellationToken);
            return;
        }

        var root = storage.WithFolder(destination).RepositoryUrl;
        foreach (var folder in RepositoryFolders)
        {
            var path = Path.Combine(root, folder);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        File.Delete(Path.Combine(root, "config"));
        if (destination.Length > 0 && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
        {
            Directory.Delete(root);
        }
    }

    /// <summary>
    /// Whether two settings describe the same storage: the same bucket in the same region,
    /// or the same folder on a disk. The access key and the destination may differ.
    /// </summary>
    public static bool IsSameStorage(StorageSettings first, StorageSettings second)
    {
        if (first.Kind != second.Kind)
        {
            return false;
        }

        if (first.IsS3)
        {
            return first.Bucket == second.Bucket && first.Region == second.Region;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first.LocalPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second.LocalPath)),
                FolderRules.PathComparison);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Forgets the storage and its secrets. The backup itself is not touched.</summary>
    public void Disconnect()
    {
        Config.Storage = null;
        secrets.Set(SecretNames.RepositoryPassword, null);
        secrets.Set(SecretNames.SecretAccessKey, null);
        secrets.Set(SecretNames.ApiToken, null);
        Save();
    }

    /// <summary>
    /// Finds out how much storage is booked and used: from the service of Imagepixel,
    /// or from the status file in the storage if the service gives no answer. Returns
    /// null when there are no figures, which is always the case for a local folder.
    /// </summary>
    public async Task<StorageStatus?> GetStorageStatusAsync(CancellationToken cancellationToken = default)
    {
        if (Config.Storage is not { IsS3: true } storage)
        {
            return null;
        }

        var result = await _storageStatus.GetAsync(
            storage,
            secrets.Get(SecretNames.SecretAccessKey),
            secrets.Get(SecretNames.ApiToken),
            cancellationToken);

        // The status file can announce the service. From then on the service is asked first.
        if (result.ApiUrl is { } apiUrl && result.ApiToken is { } apiToken
            && (apiUrl != storage.ApiUrl || apiToken != secrets.Get(SecretNames.ApiToken)))
        {
            storage.ApiUrl = apiUrl;
            secrets.Set(SecretNames.ApiToken, apiToken);
            Save();
        }

        return result.Status;
    }

    /// <summary>Adds a folder to back up. Returns the existing entry if the folder was added before.</summary>
    public BackupFolder AddFolder(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var existing = Config.Folders.FirstOrDefault(f => string.Equals(f.Path, full, FolderRules.PathComparison));
        if (existing is not null)
        {
            return existing;
        }

        // a backup stored inside the folder it backs up would back up itself
        if (Config.Storage is { Kind: StorageKind.LocalFolder } storage && FolderRules.Overlap(full, storage.LocalPath))
        {
            throw new FolderOverlapException(full, storage.LocalPath);
        }

        var folder = new BackupFolder { Path = full };
        Config.Folders.Add(folder);
        Save();
        return folder;
    }

    /// <summary>Stops backing up a folder. Existing snapshots of it are kept.</summary>
    public void RemoveFolder(BackupFolder folder)
    {
        Config.Folders.Remove(folder);
        Save();
    }

    public void Save()
    {
        lock (_saveLock)
        {
            store.Save(Config);
        }
    }

    /// <summary>Remembers that the user was told the application keeps running when its window is closed.</summary>
    public void MarkBackgroundExplained()
    {
        if (!Config.BackgroundExplained)
        {
            Config.BackgroundExplained = true;
            Save();
        }
    }

    /// <summary>Whether the user confirmed this version of the notes on use and liability.</summary>
    public bool HasAcceptedTerms(string version) => Config.TermsAccepted == version;

    /// <summary>Remembers that the user confirmed this version of the notes, and when.</summary>
    public void AcceptTerms(string version)
    {
        Config.TermsAccepted = version;
        Config.TermsAcceptedAt = DateTimeOffset.Now;
        Save();
    }

    /// <summary>Backs up one folder and records the outcome. Backups run one at a time.</summary>
    public async Task<BackupSummary> BackupAsync(
        BackupFolder folder,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _backupGate.WaitAsync(cancellationToken);
        try
        {
            folder.LastAttempt = DateTimeOffset.Now;
            Save();

            // Files of a cloud service that are only stored online are downloaded when
            // restic reads them. The guard frees each of them up again once it is saved,
            // so that the backup of a large cloud folder does not fill the disk.
            var guard = FindOnlineOnlyFiles(folder.Path);

            try
            {
                var client = CreateClient(DestinationOf(folder));
                Task<BackupSummary> Run() => client.BackupAsync(
                    folder.Path, progress, cancellationToken, guard is null ? null : guard.FileSaved,
                    folder.Exclusions.ToResticArguments());

                BackupSummary summary;
                try
                {
                    summary = await Run();
                }
                catch (ResticException e) when (e.ExitCode == ResticException.ExitRepositoryNotFound && Config.Storage is { IsS3: true })
                {
                    // A destination in Imagepixel Storage that nothing has been saved to yet
                    // is created with the password of the others. A folder on a disk is not:
                    // a missing one more likely means the disk is not connected.
                    await client.InitAsync(cancellationToken);
                    summary = await Run();
                }

                guard?.Finish();
                summary.FilesFreedUp = guard?.Freed ?? 0;

                folder.LastSuccess = DateTimeOffset.Now;
                folder.LastError = null;
                folder.LastWarnings = summary.Warnings.Count;
                return summary;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                folder.LastError = e.Message;
                throw;
            }
            finally
            {
                // also after a failed or cancelled backup: what was downloaded is freed up
                guard?.Finish();
                Save();
            }
        }
        finally
        {
            _backupGate.Release();
        }
    }

    /// <summary>
    /// Lists the snapshots of every destination that may hold backups of this computer,
    /// oldest first. Each one says which destination it lies in.
    /// </summary>
    public async Task<IReadOnlyList<Snapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<Snapshot>();
        foreach (var destination in KnownDestinations)
        {
            IReadOnlyList<Snapshot> found;
            try
            {
                found = await CreateClient(destination).GetSnapshotsAsync(cancellationToken);
            }
            catch (ResticException e) when (e.ExitCode == ResticException.ExitRepositoryNotFound)
            {
                // nothing has been saved there yet
                continue;
            }

            foreach (var snapshot in found)
            {
                snapshot.Destination = destination;
                all.Add(snapshot);
            }
        }

        return [.. all.OrderBy(snapshot => snapshot.Time)];
    }

    /// <param name="snapshotId">The snapshot to restore.</param>
    /// <param name="targetFolder">The folder it is put into.</param>
    /// <param name="progress">Receives the progress.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <param name="destination">Where the snapshot lies (<see cref="Snapshot.Destination"/>); the destination of the storage if null.</param>
    public Task RestoreAsync(
        string snapshotId,
        string targetFolder,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? destination = null) =>
        CreateClient(destination).RestoreAsync(snapshotId, targetFolder, progress, cancellationToken);

    /// <summary>
    /// Whether the backup can be opened as a drive on this computer. On macOS this
    /// would need additional software the user has to install and approve, which the
    /// application does not ask for yet.
    /// </summary>
    public static bool IsMountSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>
    /// Mounts the repository read-only: on Windows at the drive letter that is asked
    /// for, or at the first one that is free, on Linux at a folder of the user.
    /// </summary>
    /// <param name="volumeName">The name the drive shows.</param>
    /// <param name="cancellationToken">Cancels opening the drive.</param>
    /// <param name="driveLetter">
    /// Windows only: the letter to use, such as "R:". If something else uses it, a
    /// <see cref="DriveLetterInUseException"/> is thrown instead of taking another one.
    /// </param>
    /// <param name="destination">The destination whose backups are opened; the one of the storage if null.</param>
    public async Task<MountSession> MountAsync(
        string volumeName, CancellationToken cancellationToken = default, string? driveLetter = null, string? destination = null)
    {
        if (!IsMountSupported)
        {
            throw new PlatformNotSupportedException("Mounting is only implemented for Windows and Linux so far.");
        }

        if (OperatingSystem.IsWindows() && driveLetter is not null
            && !MountSession.FreeDriveLetters().Contains(driveLetter, StringComparer.OrdinalIgnoreCase))
        {
            throw new DriveLetterInUseException(driveLetter);
        }

        var client = CreateClient(destination);
        if (OperatingSystem.IsLinux())
        {
            var mountpoint = MountSession.LinuxMountpoint();
            if (MountSession.IsMounted(mountpoint))
            {
                MountSession.ReleaseStale(mountpoint);
            }

            return await client.MountAsync(mountpoint, volumeName, cancellationToken);
        }

        if (driveLetter is not null)
        {
            try
            {
                return await client.MountAsync(driveLetter.ToUpperInvariant(), volumeName, cancellationToken);
            }
            catch (ResticException e) when (e.Message.Contains("already in use", StringComparison.OrdinalIgnoreCase))
            {
                // taken by something Windows does not list as a drive
                throw new DriveLetterInUseException(driveLetter);
            }
        }

        foreach (var letter in MountSession.FreeDriveLetters())
        {
            try
            {
                return await client.MountAsync(letter, volumeName, cancellationToken);
            }
            catch (ResticException e) when (e.Message.Contains("already in use", StringComparison.OrdinalIgnoreCase))
            {
                // taken by something Windows does not list as a drive, try the next letter
            }
        }

        throw new InvalidOperationException("There is no free drive letter.");
    }

    /// <summary>
    /// Shows a destination of Imagepixel Storage read-only as it is, with the encrypted
    /// files of the backup: on Windows under a drive letter, on Linux at a folder. A
    /// backup in a folder on a disk needs no such help; its files lie in
    /// <see cref="EncryptedFolderOf"/>.
    /// </summary>
    /// <param name="volumeName">The name the drive shows.</param>
    /// <param name="cancellationToken">Cancels opening the drive.</param>
    /// <param name="driveLetter">Windows only: the letter to use, or null for the first free one.</param>
    /// <param name="destination">The destination to show; the one of the storage if null.</param>
    public async Task<MountSession> MountEncryptedAsync(
        string volumeName, CancellationToken cancellationToken = default, string? driveLetter = null, string? destination = null)
    {
        if (!IsMountSupported)
        {
            throw new PlatformNotSupportedException("Mounting is only implemented for Windows and Linux so far.");
        }

        var storage = Config.Storage is { IsS3: true } s3
            ? s3
            : throw new InvalidOperationException("Only Imagepixel Storage is shown through rclone.");
        var rclone = EncryptedView.FindRclone() ?? throw new FileNotFoundException("rclone was not found.", "rclone");
        EncryptedView.StopLeftovers(rclone);

        string mountpoint;
        if (OperatingSystem.IsWindows())
        {
            var free = MountSession.FreeDriveLetters();
            if (driveLetter is not null && !free.Contains(driveLetter, StringComparer.OrdinalIgnoreCase))
            {
                throw new DriveLetterInUseException(driveLetter);
            }

            mountpoint = (driveLetter ?? free.FirstOrDefault() ?? throw new InvalidOperationException("There is no free drive letter."))
                .ToUpperInvariant();
        }
        else
        {
            mountpoint = EncryptedView.LinuxMountpoint();
            if (MountSession.IsMounted(mountpoint))
            {
                MountSession.ReleaseStale(mountpoint);
            }
        }

        var startInfo = EncryptedView.CreateStartInfo(
            rclone, storage, secrets.Get(SecretNames.SecretAccessKey) ?? "", destination ?? storage.Folder, mountpoint, volumeName);
        return await MountSession.StartViewAsync(startInfo, mountpoint, cancellationToken);
    }

    /// <summary>Where the encrypted files of a destination lie, for a backup in a folder on a disk.</summary>
    public string EncryptedFolderOf(string? destination)
    {
        var storage = Config.Storage ?? throw new InvalidOperationException("No storage has been set up.");
        return storage.WithFolder(destination ?? storage.Folder).RepositoryUrl;
    }

    private OnlineOnlyGuard? FindOnlineOnlyFiles(string folder)
    {
        if (_cloudFiles is null)
        {
            return null;
        }

        try
        {
            return OnlineOnlyGuard.TryCreate(folder, _cloudFiles);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the backup itself reports a folder that cannot be read
            return null;
        }
    }

    // a destination of null is the one the storage was set up with
    private ResticClient CreateClient(string? destination = null)
    {
        var storage = Config.Storage ?? throw new InvalidOperationException("No storage has been set up.");
        var password = secrets.Get(SecretNames.RepositoryPassword)
            ?? throw new InvalidOperationException("The backup password is not available.");

        return new ResticClient(
            resticPath,
            RepositoryAccess.From(destination is null ? storage : storage.WithFolder(destination), password, secrets.Get(SecretNames.SecretAccessKey)));
    }
}

/// <summary>What became of the old backups of a folder at a destination it no longer goes to.</summary>
/// <param name="Destination">The destination they lay in.</param>
/// <param name="FolderPath">The folder they were backups of.</param>
/// <param name="SnapshotsDeleted">How many saved states were deleted.</param>
/// <param name="DestinationRemoved">Whether the destination was left empty and removed altogether.</param>
/// <param name="Error">Why the deletion failed, or null. It is tried again later.</param>
public sealed record CleanupResult(
    string Destination,
    string FolderPath,
    int SnapshotsDeleted,
    bool DestinationRemoved,
    string? Error);
