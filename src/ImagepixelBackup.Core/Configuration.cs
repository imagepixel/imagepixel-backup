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
}

/// <summary>The settings of the application. Secrets are not part of it.</summary>
public sealed class AppConfig
{
    public StorageSettings? Storage { get; set; }

    public List<BackupFolder> Folders { get; set; } = [];

    /// <summary>
    /// The version of the notes on use and liability the user confirmed, or null if
    /// they have not confirmed any yet.
    /// </summary>
    public string? TermsAccepted { get; set; }

    /// <summary>When <see cref="TermsAccepted"/> was confirmed.</summary>
    public DateTimeOffset? TermsAcceptedAt { get; set; }
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
