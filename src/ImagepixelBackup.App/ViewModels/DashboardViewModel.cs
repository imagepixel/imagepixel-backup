using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImagepixelBackup.Core;
using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>The overview: the folders being backed up and the ways to get data back.</summary>
public partial class DashboardViewModel : ViewModelBase
{
    private static readonly TimeSpan SchedulerInterval = TimeSpan.FromSeconds(30);

    private readonly BackupManager _manager;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _scheduler;
    private MountSession? _mount;
    private bool _schedulerBusy;

    public DashboardViewModel(BackupManager manager, MainViewModel main)
    {
        _manager = manager;
        _main = main;

        foreach (var folder in manager.Config.Folders)
        {
            Folders.Add(new FolderItemViewModel(manager, this, folder));
        }

        Folders.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoFolders));
            RefreshAvailableCloudFolders();
        };
        RefreshAvailableCloudFolders();

        _scheduler = new DispatcherTimer { Interval = SchedulerInterval };
        _scheduler.Tick += async (_, _) => await RunDueBackupsAsync();
        _scheduler.Start();
    }

    public ObservableCollection<FolderItemViewModel> Folders { get; } = [];

    public bool HasNoFolders => Folders.Count == 0;

    /// <summary>The cloud folders found on this computer.</summary>
    public IReadOnlyList<CloudFolder> CloudFolders => _main.CloudFolders;

    /// <summary>Cloud folders that are not backed up yet, offered with one click.</summary>
    public ObservableCollection<CloudFolder> AvailableCloudFolders { get; } = [];

    public bool HasAvailableCloudFolders => AvailableCloudFolders.Count > 0;

    public string StorageName => _manager.Config.Storage is { } storage ? Texts.StorageName(storage) : "";

    [ObservableProperty]
    public partial string StatusLine { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotMounted))]
    public partial bool IsMounted { get; set; }

    public bool IsNotMounted => !IsMounted;

    [ObservableProperty]
    public partial string MountText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMount))]
    public partial bool IsMounting { get; set; }

    public bool CanMount => !IsMounting;

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (await _main.PickFolder("Ordner auswählen, der gesichert werden soll") is { } path)
        {
            await AddAndBackupAsync(path);
        }
    }

    [RelayCommand]
    private Task AddCloudFolderAsync(CloudFolder folder) => AddAndBackupAsync(folder.Path);

    private async Task AddAndBackupAsync(string path)
    {
        BackupFolder folder;
        try
        {
            folder = _manager.AddFolder(path);
        }
        catch (FolderOverlapException e)
        {
            StatusLine = Texts.Error(e);
            return;
        }

        var item = Folders.FirstOrDefault(f => f.Folder == folder);
        if (item is null)
        {
            item = new FolderItemViewModel(_manager, this, folder);
            Folders.Add(item);
        }

        // a folder that was just chosen is backed up right away
        if (folder.Schedule != ScheduleKind.Manual)
        {
            await item.BackupNowAsync();
        }
    }

    private void RefreshAvailableCloudFolders()
    {
        AvailableCloudFolders.Clear();
        foreach (var cloud in CloudFolders)
        {
            if (!Folders.Any(f => string.Equals(f.Path, cloud.Path, StringComparison.OrdinalIgnoreCase)))
            {
                AvailableCloudFolders.Add(cloud);
            }
        }

        OnPropertyChanged(nameof(HasAvailableCloudFolders));
    }

    public void RemoveFolder(FolderItemViewModel item)
    {
        _manager.RemoveFolder(item.Folder);
        Folders.Remove(item);
        StatusLine = $"„{item.Name}“ wird nicht mehr gesichert. Die bisherigen Sicherungen bleiben erhalten.";
    }

    /// <summary>Starts the backups that are due according to their schedule, one after the other.</summary>
    public async Task RunDueBackupsAsync()
    {
        if (_schedulerBusy)
        {
            return;
        }

        _schedulerBusy = true;
        try
        {
            foreach (var item in Folders.ToList())
            {
                item.RefreshStatus();
                if (!item.IsRunning && BackupSchedule.IsDue(item.Folder, DateTimeOffset.Now))
                {
                    await item.BackupNowAsync();
                }
            }
        }
        finally
        {
            _schedulerBusy = false;
        }
    }

    [RelayCommand]
    private async Task MountAsync()
    {
        if (_mount is { IsRunning: true })
        {
            _main.OpenInFileManager(_mount.RootPath);
            return;
        }

        IsMounting = true;
        StatusLine = "Laufwerk wird geöffnet …";
        try
        {
            _mount = await Task.Run(() => _manager.MountAsync(Texts.ProductName));
            _mount.Exited += (_, _) => Dispatcher.UIThread.Post(OnMountEnded);
            IsMounted = true;
            MountText = $"Laufwerk {_mount.Mountpoint} ist geöffnet";
            StatusLine = $"Deine Sicherungen stehen als Laufwerk {_mount.Mountpoint} bereit – nur zum Lesen.";
            _main.OpenInFileManager(_mount.RootPath);
        }
        catch (Exception e)
        {
            StatusLine = "Das Laufwerk konnte nicht geöffnet werden: " + Texts.Error(e);
        }
        finally
        {
            IsMounting = false;
        }
    }

    [RelayCommand]
    private void ShowDrive()
    {
        if (_mount is { IsRunning: true })
        {
            _main.OpenInFileManager(_mount.RootPath);
        }
    }

    [RelayCommand]
    private async Task UnmountAsync()
    {
        if (_mount is { } mount)
        {
            StatusLine = "Laufwerk wird getrennt …";
            await mount.StopAsync();
            OnMountEnded();
        }
    }

    private void OnMountEnded()
    {
        if (_mount is null)
        {
            return;
        }

        _mount = null;
        IsMounted = false;
        MountText = "";
        StatusLine = "Das Laufwerk wurde getrennt.";
    }

    [RelayCommand]
    private void Restore() => _main.ShowRestore();

    [RelayCommand]
    private void ShowAbout() => _main.ShowAbout();

    [RelayCommand]
    private async Task ChangeStorageAsync()
    {
        await ShutdownAsync();
        _manager.Disconnect();
        await _main.ShowSetupAsync();
    }

    /// <summary>Stops the schedule, running backups and the mounted drive.</summary>
    public async Task ShutdownAsync()
    {
        _scheduler.Stop();
        foreach (var item in Folders)
        {
            item.CancelBackup();
        }

        if (_mount is { } mount)
        {
            _mount = null;
            await mount.DisposeAsync();
            IsMounted = false;
        }
    }
}
