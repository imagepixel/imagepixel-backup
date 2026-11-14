using System;
using System.Collections.Generic;
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
    private CancellationTokenSource? _cancellation;

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
        Name = cloud is not null && string.Equals(cloud.Path, folder.Path, StringComparison.OrdinalIgnoreCase)
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
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsRunning { get; set; }

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

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
                last += $" ({Folder.LastWarnings} Dateien nicht lesbar)";
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

        IsRunning = true;
        ProgressPercent = 0;
        ProgressText = "Sicherung wird vorbereitet …";
        _cancellation = new CancellationTokenSource();

        var progress = new Progress<BackupProgress>(p =>
        {
            ProgressPercent = p.Fraction * 100;
            ProgressText = p.TotalBytes > 0
                ? $"{Texts.Bytes(p.BytesDone)} von {Texts.Bytes(p.TotalBytes)} · {p.FilesDone} von {p.TotalFiles} Dateien"
                : "Dateien werden gezählt …";
        });

        try
        {
            var summary = await Task.Run(() => _manager.BackupAsync(Folder, progress, _cancellation.Token));
            var freed = summary.FilesFreedUp > 0
                ? $" {summary.FilesFreedUp} Dateien liegen wieder nur online."
                : "";
            _dashboard.StatusLine = $"„{Name}“ gesichert: {summary.FilesNew + summary.FilesChanged} neue oder geänderte Dateien, {Texts.Bytes(summary.BytesUploaded)} übertragen.{freed}";
            _ = _dashboard.RefreshStorageStatusAsync();
        }
        catch (OperationCanceledException)
        {
            _dashboard.StatusLine = $"Sicherung von „{Name}“ abgebrochen.";
        }
        catch (Exception e)
        {
            Folder.LastError = Texts.Error(e);
            _manager.Save();
            _dashboard.StatusLine = $"Sicherung von „{Name}“ fehlgeschlagen.";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
            RefreshStatus();
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private void Remove()
    {
        _cancellation?.Cancel();
        _dashboard.RemoveFolder(this);
    }

    public void CancelBackup() => _cancellation?.Cancel();
}
