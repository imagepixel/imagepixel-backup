using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagepixelBackup.Core;

/// <summary>When a folder is backed up.</summary>
public enum ScheduleKind
{
    /// <summary>Automatically in the background, once per hour.</summary>
    Hourly,

    /// <summary>Once per day at <see cref="BackupFolder.DailyAt"/>.</summary>
    Daily,

    /// <summary>Only when the user starts the backup.</summary>
    Manual,
}

/// <summary>A folder the user has chosen to back up.</summary>
public sealed class BackupFolder
{
    public string Path { get; set; } = "";

    public ScheduleKind Schedule { get; set; } = ScheduleKind.Hourly;

    /// <summary>Time of day for <see cref="ScheduleKind.Daily"/>.</summary>
    public TimeOnly DailyAt { get; set; } = new(12, 0);

    /// <summary>When the last backup was started, successful or not.</summary>
    public DateTimeOffset? LastAttempt { get; set; }

    /// <summary>When the last backup completed successfully.</summary>
    public DateTimeOffset? LastSuccess { get; set; }

    /// <summary>The error of the last backup, or null if it succeeded.</summary>
    public string? LastError { get; set; }

    /// <summary>How many files the last successful backup could not read.</summary>
    public int LastWarnings { get; set; }

    /// <summary>What the backup of the folder leaves out. Empty unless the user asked for it.</summary>
    public FolderExclusions Exclusions { get; set; } = new();

    /// <summary>
    /// The folder inside the storage that the backups of this folder go to, in the form
    /// <see cref="StorageFolder.Normalize"/> gives it, empty for directly in the storage.
    /// Null means the folder the storage was set up with (<see cref="StorageSettings.Folder"/>),
    /// which is where every folder went before folders had a destination of their own.
    /// </summary>
    public string? Destination { get; set; }

    /// <summary>
    /// Whether the user has said what to leave out, by taking over or turning down the
    /// suggestion before the first backup or by setting up exceptions themselves. Also
    /// set when the folder holds nothing worth suggesting.
    /// </summary>
    public bool ExclusionsDecided { get; set; }

    /// <summary>
    /// Since when the backup of the folder is paused, or null if it is not. A paused
    /// folder is left alone by the schedule until the user lets it go on. What was
    /// saved before the pause stays in the backup and is not sent again.
    /// </summary>
    public DateTimeOffset? PausedAt { get; set; }

    /// <summary>How far the backup had got when it was paused, for the user to read.</summary>
    public long PausedBytesDone { get; set; }

    /// <summary>How much there was to back up when the backup was paused.</summary>
    public long PausedBytesTotal { get; set; }

    /// <summary>
    /// Forgets how the backups of the folder went, when they go to another destination
    /// from now on: nothing has been saved there yet, so the folder is due at once.
    /// What the user set up stays: schedule, exceptions, and a pause.
    /// </summary>
    public void ForgetRuns()
    {
        LastAttempt = null;
        LastSuccess = null;
        LastError = null;
        LastWarnings = 0;
        PausedBytesDone = 0;
        PausedBytesTotal = 0;
    }

    /// <summary>
    /// The same folder for a backup at another place. What the user set up comes along:
    /// schedule, exceptions, and a pause. How the backups went does not, because nothing
    /// has been saved at the new place yet, so the folder is due there at once. Nor does
    /// its destination, a folder of the previous storage: it goes where the new storage
    /// was set up to take backups. It is a new object, so that a backup still running
    /// against the old place cannot write its outcome into it.
    /// </summary>
    public BackupFolder ForAnotherPlace() => new()
    {
        Path = Path,
        Schedule = Schedule,
        DailyAt = DailyAt,
        Exclusions = Exclusions,
        ExclusionsDecided = ExclusionsDecided,
        PausedAt = PausedAt,
    };
}

/// <summary>
/// The backups of a folder at a destination it no longer goes to, which the user asked
/// to have deleted once some days have passed. Until then they can still be restored.
/// </summary>
public sealed class PendingCleanup
{
    /// <summary>The destination the backups lie in, see <see cref="BackupFolder.Destination"/>.</summary>
    public string Destination { get; set; } = "";

    /// <summary>The folder whose backups are deleted there. Backups of other folders stay.</summary>
    public string FolderPath { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>From when on the backups are deleted, the next time the application runs.</summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>Why the last attempt failed, or null.</summary>
    public string? LastError { get; set; }

    /// <summary>When to try again after a failed attempt.</summary>
    public DateTimeOffset? RetryAt { get; set; }
}

/// <summary>The settings of the application. Secrets are not part of it.</summary>
public sealed class AppConfig
{
    public StorageSettings? Storage { get; set; }

    public List<BackupFolder> Folders { get; set; } = [];

    /// <summary>Backups at destinations that are no longer used, waiting to be deleted.</summary>
    public List<PendingCleanup> Cleanups { get; set; } = [];

    /// <summary>
    /// The version of the notes on use and liability the user confirmed, or null if
    /// they have not confirmed any yet.
    /// </summary>
    public string? TermsAccepted { get; set; }

    /// <summary>When <see cref="TermsAccepted"/> was confirmed.</summary>
    public DateTimeOffset? TermsAcceptedAt { get; set; }

    /// <summary>
    /// Whether the user has been told that closing the window leaves the application
    /// running in the background. It is said once, the first time they close it.
    /// </summary>
    public bool BackgroundExplained { get; set; }

    /// <summary>
    /// The drive letter the user chose for opening the backup as a drive on Windows,
    /// such as "R:", or null if they never changed the one that was suggested.
    /// </summary>
    public string? DriveLetter { get; set; }
}

/// <summary>Loads and saves <see cref="AppConfig"/> as a JSON file in the user's profile.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ConfigStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
    }

    /// <summary>The folder that holds the settings of the current user.</summary>
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("IMAGEPIXEL_BACKUP_DATA") is { Length: > 0 } custom
            ? custom
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImagepixelBackup");

    public string Directory { get; }

    private string FilePath => System.IO.Path.Combine(Directory, "config.json");

    public AppConfig Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppConfig();
        }

        using var stream = File.OpenRead(FilePath);
        return JsonSerializer.Deserialize<AppConfig>(stream, JsonOptions) ?? new AppConfig();
    }

    public void Save(AppConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);

        // Write to a temporary file first so that a crash never leaves a damaged file behind.
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
