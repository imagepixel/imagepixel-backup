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
    ICloudFileSystem? cloudFiles = null)
{
    private readonly ICloudFileSystem? _cloudFiles =
        cloudFiles ?? (OperatingSystem.IsWindows() ? new WindowsCloudFileSystem() : null);

    private readonly Lock _saveLock = new();
    private readonly SemaphoreSlim _backupGate = new(1, 1);

    public AppConfig Config { get; } = store.Load();

    /// <summary>Whether a storage has been set up and its password is available.</summary>
    public bool IsConnected =>
        Config.Storage is not null && secrets.Get(SecretNames.RepositoryPassword) is not null;

    /// <summary>
    /// Checks the storage and remembers it if the password opens the repository.
    /// A missing repository is only created when <paramref name="createIfMissing"/> is set.
    /// </summary>
    public async Task<RepositoryState> ConnectAsync(
        StorageSettings storage,
        string password,
        string? secretAccessKey,
        bool createIfMissing,
        CancellationToken cancellationToken = default)
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
            secrets.Set(SecretNames.RepositoryPassword, password);
            secrets.Set(SecretNames.SecretAccessKey, storage.IsS3 ? secretAccessKey : null);
            Config.Storage = storage;
            Save();
        }

        return state;
    }

    /// <summary>Forgets the storage and its secrets. The backup itself is not touched.</summary>
    public void Disconnect()
    {
        Config.Storage = null;
        secrets.Set(SecretNames.RepositoryPassword, null);
        secrets.Set(SecretNames.SecretAccessKey, null);
        Save();
    }

    /// <summary>Adds a folder to back up. Returns the existing entry if the folder was added before.</summary>
    public BackupFolder AddFolder(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var existing = Config.Folders.FirstOrDefault(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase));
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
                var summary = await CreateClient().BackupAsync(
                    folder.Path, progress, cancellationToken, guard is null ? null : guard.FileSaved);

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

    public Task<IReadOnlyList<Snapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default) =>
        CreateClient().GetSnapshotsAsync(cancellationToken);

    public Task RestoreAsync(
        string snapshotId,
        string targetFolder,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        CreateClient().RestoreAsync(snapshotId, targetFolder, progress, cancellationToken);

    /// <summary>Mounts the repository read-only at the first drive letter that is free.</summary>
    public async Task<MountSession> MountAsync(string volumeName, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Mounting is only implemented for Windows so far.");
        }

        var client = CreateClient();
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

    private ResticClient CreateClient()
    {
        var storage = Config.Storage ?? throw new InvalidOperationException("No storage has been set up.");
        var password = secrets.Get(SecretNames.RepositoryPassword)
            ?? throw new InvalidOperationException("The backup password is not available.");

        return new ResticClient(
            resticPath,
            RepositoryAccess.From(storage, password, secrets.Get(SecretNames.SecretAccessKey)));
    }
}
