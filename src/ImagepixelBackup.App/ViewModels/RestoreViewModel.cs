using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImagepixelBackup.Core;
using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>One saved state of a folder, as shown in the restore list.</summary>
public sealed class SnapshotItemViewModel(Snapshot snapshot, string destination = "")
{
    public Snapshot Snapshot { get; } = snapshot;

    /// <summary>Where in the storage the state lies, when the backups are spread over more than one place.</summary>
    public string Destination { get; } = destination;

    public bool HasDestination => Destination.Length > 0;

    public string FolderName { get; } =
        snapshot.Paths.Count > 0 && System.IO.Path.GetFileName(snapshot.Paths[0].TrimEnd('\\', '/')) is { Length: > 0 } name
            ? name
            : string.Join(", ", snapshot.Paths);

    public string Time { get; } = snapshot.Time.ToLocalTime().ToString("dd.MM.yyyy, HH:mm") + " Uhr";

    public string Details { get; } = snapshot.Summary is { } summary
        ? $"{Texts.Files(summary.TotalFiles)} · {Texts.Bytes(summary.TotalBytes)}"
        : "";
}

/// <summary>Restores a folder from an earlier state into a folder the user chooses.</summary>
public partial class RestoreViewModel : ViewModelBase
{
    private readonly BackupManager _manager;
    private readonly MainViewModel _main;

    public RestoreViewModel(BackupManager manager, MainViewModel main)
    {
        _manager = manager;
        _main = main;
        _ = LoadAsync();
    }

    public ObservableCollection<SnapshotItemViewModel> Snapshots { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestore))]
    public partial SnapshotItemViewModel? SelectedSnapshot { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestore))]
    public partial string TargetFolder { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestore))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial bool HasProblem { get; set; }

    [ObservableProperty]
    public partial bool IsDone { get; set; }

    public bool CanRestore => SelectedSnapshot is not null && !string.IsNullOrWhiteSpace(TargetFolder) && !IsBusy;

    /// <summary>Whether this computer can show the backup as a drive.</summary>
    public bool CanOpenAsDrive => BackupManager.IsMountSupported;

    public string Hint => Texts.RestoreHint;

    private async Task LoadAsync()
    {
        IsLoading = true;
        Message = "Sicherungen werden geladen …";
        try
        {
            var snapshots = await Task.Run(() => _manager.GetSnapshotsAsync());

            // with backups in more than one place, each line says where it lies
            var spread = snapshots.Select(snapshot => snapshot.Destination).Distinct().Count() > 1;
            foreach (var snapshot in snapshots.Reverse())
            {
                var place = spread ? Texts.DestinationLabel(_manager.Config.Storage, snapshot.Destination) : "";
                Snapshots.Add(new SnapshotItemViewModel(snapshot, place));
            }

            Message = Snapshots.Count == 0 ? "Es gibt noch keine Sicherungen." : "";
        }
        catch (Exception e)
        {
            HasProblem = true;
            Message = Texts.Error(e);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Back() => _main.ShowDashboard();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _main.PickFolder("Ordner auswählen, in den wiederhergestellt wird") is { } path)
        {
            TargetFolder = path;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (SelectedSnapshot is not { } item || !CanRestore)
        {
            return;
        }

        IsBusy = true;
        IsDone = false;
        HasProblem = false;
        ProgressPercent = 0;
        Message = "Wiederherstellung läuft …";

        var progress = new Progress<RestoreProgress>(p =>
        {
            ProgressPercent = p.Fraction * 100;
            Message = $"Wiederherstellung läuft: {Texts.Bytes(p.BytesRestored)} von {Texts.Bytes(p.TotalBytes)}";
        });

        var target = TargetFolder.Trim();
        try
        {
            await Task.Run(() => _manager.RestoreAsync(item.Snapshot.Id, target, progress, destination: item.Snapshot.Destination));
            ProgressPercent = 100;
            IsDone = true;
            Message = $"Fertig. „{item.FolderName}“ vom {item.Time} liegt jetzt in {target}.";
        }
        catch (Exception e)
        {
            HasProblem = true;
            Message = "Die Wiederherstellung ist fehlgeschlagen: " + Texts.Error(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenTarget() => _main.OpenInFileManager(TargetFolder.Trim());

    /// <summary>
    /// Opens the backup as a read-only drive, for someone who only needs a few files
    /// and wants to pick them in the file manager.
    /// </summary>
    [RelayCommand]
    private async Task OpenAsDriveAsync()
    {
        var dashboard = _main.Dashboard;
        Message = "Laufwerk wird geöffnet …";
        HasProblem = false;

        // the drive shows the place of the state that is selected
        await dashboard.OpenDriveAsync(SelectedSnapshot?.Snapshot.Destination ?? dashboard.SelectedMountDestination?.Folder);

        HasProblem = !dashboard.IsMounted;
        Message = dashboard.StatusLine;
    }
}
