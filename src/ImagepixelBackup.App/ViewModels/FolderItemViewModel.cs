using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImagepixelBackup.Core;
using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>One folder in the overview, with its schedule and the state of its backup.</summary>
public partial class FolderItemViewModel : ViewModelBase
{
    private readonly BackupManager _manager;
    private readonly DashboardViewModel _dashboard;
    private readonly TransferSpeed _speed = new();
    private CancellationTokenSource? _cancellation;
    private DateTimeOffset _lastDetailAt;
    private string? _lastDetailFile;
    private BackupProgress? _lastProgress;
    private bool _pauseRequested;
    private DateTimeOffset? _attemptBefore;
    private bool _stoppedForLeaving;

    public FolderItemViewModel(BackupManager manager, DashboardViewModel dashboard, BackupFolder folder)
    {
        _manager = manager;
        _dashboard = dashboard;
        Folder = folder;
        ScheduleIndex = (int)folder.Schedule;
        DailyTime = folder.DailyAt.ToTimeSpan();
        var cloud = CloudFolderDetector.Containing(folder.Path, dashboard.CloudFolders);
        IsCloudFolder = cloud is not null;

        // the folder of a cloud service is shown under the name of the service
        Name = cloud is not null && string.Equals(cloud.Path, folder.Path, FolderRules.PathComparison)
            ? cloud.Name
            : System.IO.Path.GetFileName(folder.Path) is { Length: > 0 } name ? name : folder.Path;

        RefreshStatus();
    }

    public BackupFolder Folder { get; }

    /// <summary>Whether the folder belongs to a cloud service, which is explained below it.</summary>
    public bool IsCloudFolder { get; }

    public string CloudHint => Texts.CloudHint;

    public string Name { get; }

    public string Path => Folder.Path;

    public IReadOnlyList<string> ScheduleOptions { get; } =
        ["Automatisch, jede Stunde", "Täglich um …", "Nur wenn ich es starte"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDaily))]
    public partial int ScheduleIndex { get; set; }

    public bool IsDaily => ScheduleIndex == (int)ScheduleKind.Daily;

    [ObservableProperty]
    public partial TimeSpan? DailyTime { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasProblem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanStart), nameof(CanResume))]
    public partial bool IsRunning { get; set; }

    public bool IsIdle => !IsRunning;

    /// <summary>Whether the user paused the backup of this folder. It then waits until they let it go on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanResume))]
    public partial bool IsPaused { get; set; }

    public bool CanStart => IsIdle && !IsPaused;

    public bool CanResume => IsIdle && IsPaused;

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    // How many characters of a path fit into a line of the details next to the time.
    private const int MaxDetailWidth = 74;

    /// <summary>How many lines the details keep: what fits into their box.</summary>
    public const int MaxDetailLines = 12;

    // New lines come at most this often, so that they can be read while they pass.
    private static readonly TimeSpan DetailDistance = TimeSpan.FromMilliseconds(120);

    /// <summary>Whether the lines below the progress are shown that tell what the backup is working on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsButtonText))]
    public partial bool ShowDetails { get; set; }

    public string DetailsButtonText => ShowDetails ? Texts.DetailsHide : Texts.DetailsShow;

    /// <summary>The details are offered while a backup runs, and afterwards for as long as they are open.</summary>
    public bool CanShowDetails => IsRunning || ShowDetails;

    /// <summary>The last files the backup dealt with, the newest at the bottom, like the lines of a terminal.</summary>
    public ObservableCollection<string> DetailLines { get; } = [];

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanShowDetails));
        OnPropertyChanged(nameof(HasSuggestion));
    }

    partial void OnShowDetailsChanged(bool value) => OnPropertyChanged(nameof(CanShowDetails));

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    /// <summary>Says in a few words that the backup of this folder leaves something out, or nothing if it does not.</summary>
    public string ExclusionsText => Folder.Exclusions.Count() is > 0 and var count ? Texts.Exclusions(count) : "";

    public bool HasExclusions => Folder.Exclusions.Count() > 0;

    public void RefreshExclusions()
    {
        OnPropertyChanged(nameof(ExclusionsText));
        OnPropertyChanged(nameof(HasExclusions));
    }

    /// <summary>Where in the storage the backups of this folder go.</summary>
    public string DestinationText => Texts.DestinationLine(_manager.Config.Storage, _manager.DestinationOf(Folder));

    /// <summary>What waits to be deleted at destinations the folder no longer goes to, or nothing.</summary>
    public string CleanupText => string.Join(
        " ", _manager.CleanupsOf(Folder).Select(cleanup => Texts.CleanupPending(_manager.Config.Storage, cleanup, DateTimeOffset.Now)));

    public bool HasCleanup => _manager.CleanupsOf(Folder).Count > 0;

    public void RefreshDestination()
    {
        OnPropertyChanged(nameof(DestinationText));
        OnPropertyChanged(nameof(CleanupText));
        OnPropertyChanged(nameof(HasCleanup));
        RefreshStatus();
    }

    /// <summary>Opens the page where the customer chooses where in the storage this folder goes.</summary>
    [RelayCommand]
    private void ChangeDestination() => _dashboard.ShowDestination(this);

    /// <summary>Keeps the old backups after all, which were to be deleted.</summary>
    [RelayCommand]
    private void KeepOldBackups()
    {
        _manager.KeepOldBackups(Folder);
        RefreshDestination();
        _dashboard.StatusLine = Texts.OldBackupsKept(Name);
    }

    [RelayCommand]
    private void EditExclusions() => _dashboard.ShowExclusions(this);

    /// <summary>
    /// What the look through the folder found before its first backup, for as long as
    /// the customer has not answered. The backup of the folder waits for the answer.
    /// </summary>
    public ExclusionScan? Suggestion { get; private set; }

    public bool HasSuggestion => Suggestion is not null && IsIdle;

    public string SuggestionText => Suggestion is { } scan
        ? Texts.SuggestionWaiting(scan.Findings.Where(f => ExclusionScan.SuggestedPresets.Contains(f.Preset)).Sum(f => f.Bytes))
        : "";

    // The first backup of a folder asks once what to leave out, unless the customer has said so already.
    private bool NeedsExclusionDecision =>
        !Folder.ExclusionsDecided && Folder.LastSuccess is null && Folder.Exclusions.Count() == 0;

    [RelayCommand]
    private void ShowSuggestion() => _dashboard.ShowSuggestion(this);

    /// <summary>Forgets the suggestion, because the customer has set up the exceptions another way.</summary>
    public void DropSuggestion()
    {
        Suggestion = null;
        OnPropertyChanged(nameof(HasSuggestion));
        OnPropertyChanged(nameof(SuggestionText));
    }

    /// <summary>
    /// The answer to the suggestion: the groups to leave out, or none. The backup that
    /// waited for it starts right away.
    /// </summary>
    public async Task DecideExclusionsAsync(IReadOnlyList<string> presets)
    {
        Folder.Exclusions.Presets = [.. presets];
        Folder.ExclusionsDecided = true;
        _manager.Save();
        DropSuggestion();
        RefreshExclusions();
        await BackupNowAsync();
    }

    // Looks through the folder for typical things worth leaving out. Returns false if the customer stopped it.
    private async Task<bool> LookForExclusionsAsync()
    {
        IsRunning = true;
        _attemptBefore = Folder.LastAttempt;
        _pauseRequested = false;
        _stoppedForLeaving = false;
        ProgressPercent = 0;
        ProgressText = "Ordner wird geprüft …";
        _cancellation = new CancellationTokenSource();
        try
        {
            var token = _cancellation.Token;
            var scan = await Task.Run(() => ExclusionScanner.Scan(Folder.Path, token));

            // stopped just when the look was through: the backup must not start after all
            token.ThrowIfCancellationRequested();
            if (scan.IsWorthSuggesting)
            {
                Suggestion = scan;
            }
            else
            {
                // nothing typical in here: the backup starts without a question
                Folder.ExclusionsDecided = true;
                _manager.Save();
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // pausing counts from the first moment on, not only once restic runs
            if (_pauseRequested)
            {
                MarkPaused();
            }

            return false;
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
            OnPropertyChanged(nameof(HasSuggestion));
            OnPropertyChanged(nameof(SuggestionText));
            RefreshStatus();
        }
    }

    /// <summary>Shows where a running backup stands: amounts, speed and, in the details, the file it is at.</summary>
    public void ShowProgress(BackupProgress progress, DateTimeOffset now)
    {
        ProgressPercent = progress.Fraction * 100;
        if (progress.TotalBytes <= 0)
        {
            ProgressText = "Dateien werden gezählt …";
            return;
        }

        _lastProgress = progress;
        _speed.Add(now, progress.BytesDone);
        var speed = _speed.BytesPerSecond is { } bytesPerSecond ? " · " + Texts.Speed(bytesPerSecond) : "";
        ProgressText = $"{Texts.Bytes(progress.BytesDone)} von {Texts.Bytes(progress.TotalBytes)} · {progress.FilesDone} von {Texts.Files(progress.TotalFiles)}{speed}";

        // restic names the files it is reading at the moment; with many small files only some of them pass by here
        if (ShowDetails
            && now - _lastDetailAt >= DetailDistance
            && progress.CurrentFiles.Find(file => file != _lastDetailFile) is { Length: > 0 } file)
        {
            _lastDetailAt = now;
            _lastDetailFile = file;
            AddDetail(now, Texts.ShortenPath(file.Replace('/', System.IO.Path.DirectorySeparatorChar), MaxDetailWidth));
        }
    }

    private void AddDetail(DateTimeOffset now, string text)
    {
        DetailLines.Add($"{now.ToLocalTime():HH:mm:ss}  {text}");
        while (DetailLines.Count > MaxDetailLines)
        {
            DetailLines.RemoveAt(0);
        }
    }

    partial void OnScheduleIndexChanged(int value)
    {
        if (value < 0)
        {
            return;
        }

        Folder.Schedule = (ScheduleKind)value;
        _manager.Save();
        RefreshStatus();
    }

    partial void OnDailyTimeChanged(TimeSpan? value)
    {
        if (value is { } time)
        {
            Folder.DailyAt = TimeOnly.FromTimeSpan(time);
            _manager.Save();
            RefreshStatus();
        }
    }

    /// <summary>Describes the last and the next backup in one line.</summary>
    public void RefreshStatus()
    {
        var now = DateTimeOffset.Now;
        IsPaused = Folder.PausedAt is not null;
        if (Folder.PausedAt is { } pausedAt)
        {
            HasProblem = false;
            var done = Folder.PausedBytesTotal > 0
                ? $" · {Texts.Bytes(Folder.PausedBytesDone)} von {Texts.Bytes(Folder.PausedBytesTotal)} waren geschafft"
                : "";
            StatusText = $"Pausiert seit {Texts.Day(pausedAt, now)}{done}. {Texts.PauseHint}";
            return;
        }

        HasProblem = Folder.LastError is not null;

        string last;
        if (Folder.LastError is not null)
        {
            last = "Letzte Sicherung fehlgeschlagen: " + Folder.LastError;
        }
        else if (Folder.LastSuccess is { } success)
        {
            last = "Zuletzt gesichert: " + Texts.Day(success, now);
            if (Folder.LastWarnings > 0)
            {
                last += $" ({Texts.Files(Folder.LastWarnings)} nicht lesbar)";
            }
        }
        else
        {
            last = "Noch nicht gesichert";
        }

        var next = BackupSchedule.NextRun(Folder, now) switch
        {
            null => "",
            { } time when time <= now => " · nächste Sicherung: gleich",
            { } time => " · nächste Sicherung: " + Texts.Day(time, now),
        };

        StatusText = last + next;
    }

    [RelayCommand]
    public async Task BackupNowAsync()
    {
        if (IsRunning)
        {
            return;
        }

        // before the very first backup: look for typical things to leave out and let the customer answer
        if (NeedsExclusionDecision)
        {
            if (Suggestion is null && !await LookForExclusionsAsync())
            {
                return;
            }

            if (Suggestion is not null)
            {
                _dashboard.OfferSuggestion(this);
                return;
            }
        }

        IsRunning = true;
        _attemptBefore = Folder.LastAttempt;
        _stoppedForLeaving = false;
        ProgressPercent = 0;
        ProgressText = "Sicherung wird vorbereitet …";
        _cancellation = new CancellationTokenSource();
        _speed.Reset();
        _lastDetailFile = null;
        _lastProgress = null;
        _pauseRequested = false;
        DetailLines.Clear();
        AddDetail(DateTimeOffset.Now, $"Sicherung von {Path} gestartet");

        var progress = new Progress<BackupProgress>(p => ShowProgress(p, DateTimeOffset.Now));

        try
        {
            var summary = await Task.Run(() => _manager.BackupAsync(Folder, progress, _cancellation.Token));
            var freed = summary.FilesFreedUp switch
            {
                0 => "",
                1 => " 1 Datei liegt wieder nur online.",
                _ => $" {summary.FilesFreedUp} Dateien liegen wieder nur online.",
            };
            _dashboard.StatusLine = $"„{Name}“ gesichert: {Texts.ChangedFiles(summary.FilesNew + summary.FilesChanged)}, {Texts.Bytes(summary.BytesUploaded)} übertragen.{freed}";
            AddDetail(DateTimeOffset.Now, $"Fertig: {Texts.ChangedFiles(summary.FilesNew + summary.FilesChanged)}, {Texts.Bytes(summary.BytesUploaded)} übertragen");
            _ = _dashboard.RefreshStorageStatusAsync();
        }
        catch (OperationCanceledException)
        {
            if (_stoppedForLeaving)
            {
                // said once more here, in case the backup had only just begun when it was stopped
                Folder.LastAttempt = _attemptBefore;
                _manager.Save();
            }

            if (_pauseRequested)
            {
                MarkPaused();
            }
            else
            {
                _dashboard.StatusLine = $"Sicherung von „{Name}“ abgebrochen.";
                AddDetail(DateTimeOffset.Now, "Abgebrochen");
            }
        }
        catch (Exception e)
        {
            Folder.LastError = Texts.Error(e);
            _manager.Save();
            _dashboard.StatusLine = $"Sicherung von „{Name}“ fehlgeschlagen.";
            AddDetail(DateTimeOffset.Now, "Fehlgeschlagen: " + Folder.LastError);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
            RefreshStatus();
        }
    }

    private void MarkPaused()
    {
        Folder.PausedAt = DateTimeOffset.Now;
        Folder.PausedBytesDone = _lastProgress?.BytesDone ?? 0;
        Folder.PausedBytesTotal = _lastProgress?.TotalBytes ?? 0;
        _manager.Save();
        _dashboard.StatusLine = $"Sicherung von „{Name}“ pausiert.";
        AddDetail(DateTimeOffset.Now, "Pausiert");
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    /// <summary>
    /// Stops the running backup and keeps the folder waiting until the user lets it go
    /// on, also across a restart or an update of the application. What is already in the
    /// backup is not sent again afterwards.
    /// </summary>
    [RelayCommand]
    private void Pause()
    {
        if (IsRunning)
        {
            _pauseRequested = true;
            _cancellation?.Cancel();
        }
    }

    /// <summary>Ends the pause and backs the folder up at once.</summary>
    [RelayCommand]
    private async Task ResumeAsync()
    {
        Folder.PausedAt = null;
        _manager.Save();
        RefreshStatus();
        await BackupNowAsync();
    }

    [RelayCommand]
    private void Remove()
    {
        _cancellation?.Cancel();
        _dashboard.RemoveFolder(this);
    }

    /// <summary>
    /// Stops a running backup because the application ends or moves to another storage.
    /// The folder counts as not attempted, so that the backup starts again by itself
    /// soon after the next start instead of waiting for its next turn.
    /// </summary>
    public void CancelBackup()
    {
        if (IsRunning && _cancellation is { } cancellation)
        {
            _stoppedForLeaving = true;
            Folder.LastAttempt = _attemptBefore;
            _manager.Save();
            cancellation.Cancel();
        }
    }
}
