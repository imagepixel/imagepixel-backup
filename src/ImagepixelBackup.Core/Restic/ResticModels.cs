using System.Text.Json.Serialization;

namespace ImagepixelBackup.Core.Restic;

/// <summary>What was found at a repository location.</summary>
public enum RepositoryState
{
    /// <summary>There is a repository and the password opens it.</summary>
    Ready,

    /// <summary>There is no repository yet. It can be created.</summary>
    NotFound,

    /// <summary>There is a repository, but the password is wrong.</summary>
    WrongPassword,
}

/// <summary>A restic call failed.</summary>
public sealed class ResticException(string message, int exitCode) : Exception(message)
{
    public const int ExitRepositoryNotFound = 10;
    public const int ExitRepositoryLocked = 11;
    public const int ExitWrongPassword = 12;

    public int ExitCode { get; } = exitCode;
}

/// <summary>One backup of one folder at one point in time.</summary>
public sealed class Snapshot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("short_id")]
    public string ShortId { get; set; } = "";

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; set; }

    [JsonPropertyName("paths")]
    public List<string> Paths { get; set; } = [];

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = "";

    [JsonPropertyName("summary")]
    public SnapshotSummary? Summary { get; set; }

    /// <summary>The destination in the storage that holds the snapshot, see <see cref="BackupFolder.Destination"/>.</summary>
    [JsonIgnore]
    public string Destination { get; set; } = "";
}

public sealed class SnapshotSummary
{
    [JsonPropertyName("total_files_processed")]
    public long TotalFiles { get; set; }

    [JsonPropertyName("total_bytes_processed")]
    public long TotalBytes { get; set; }
}

/// <summary>Progress of a running backup.</summary>
public sealed class BackupProgress
{
    [JsonPropertyName("percent_done")]
    public double Fraction { get; set; }

    [JsonPropertyName("total_files")]
    public long TotalFiles { get; set; }

    [JsonPropertyName("files_done")]
    public long FilesDone { get; set; }

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("bytes_done")]
    public long BytesDone { get; set; }

    [JsonPropertyName("current_files")]
    public List<string> CurrentFiles { get; set; } = [];
}

/// <summary>Result of a finished backup.</summary>
public sealed class BackupSummary
{
    [JsonPropertyName("snapshot_id")]
    public string SnapshotId { get; set; } = "";

    [JsonPropertyName("files_new")]
    public long FilesNew { get; set; }

    [JsonPropertyName("files_changed")]
    public long FilesChanged { get; set; }

    [JsonPropertyName("files_unmodified")]
    public long FilesUnmodified { get; set; }

    [JsonPropertyName("data_added_packed")]
    public long BytesUploaded { get; set; }

    [JsonPropertyName("total_files_processed")]
    public long TotalFiles { get; set; }

    [JsonPropertyName("total_bytes_processed")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("total_duration")]
    public double DurationSeconds { get; set; }

    /// <summary>Files or folders that could not be read. The backup contains everything else.</summary>
    [JsonIgnore]
    public List<string> Warnings { get; } = [];

    /// <summary>
    /// Files of a cloud service that were only stored online, had to be downloaded
    /// for the backup and were made online-only again afterwards.
    /// </summary>
    [JsonIgnore]
    public int FilesFreedUp { get; set; }
}

/// <summary>Progress of a running restore.</summary>
public sealed class RestoreProgress
{
    [JsonPropertyName("percent_done")]
    public double Fraction { get; set; }

    [JsonPropertyName("total_files")]
    public long TotalFiles { get; set; }

    [JsonPropertyName("files_restored")]
    public long FilesRestored { get; set; }

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("bytes_restored")]
    public long BytesRestored { get; set; }
}
