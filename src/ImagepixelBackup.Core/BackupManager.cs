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
    StorageStatusService? storageStatus = null)
{
    private readonly ICloudFileSystem? _cloudFiles =
        cloudFiles ?? (OperatingSystem.IsWindows() ? new WindowsCloudFileSystem() : null);

    private readonly StorageStatusService _storageStatus = storageStatus ?? new StorageStatusService(new HttpClient());

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
            secrets.Set(SecretNames.RepositoryPassword, password);
            secrets.Set(SecretNames.SecretAccessKey, storage.IsS3 ? secretAccessKey : null);
            secrets.Set(SecretNames.ApiToken, storage.IsS3 ? apiToken : null);
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
                var summary = await CreateClient().BackupAsync(
                    folder.Path, progress, cancellationToken, guard is null ? null : guard.FileSaved,
                    folder.Exclusions.ToResticArguments());

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
    public async Task<MountSession> MountAsync(
        string volumeName, CancellationToken cancellationToken = default, string? driveLetter = null)
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

        var client = CreateClient();
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
