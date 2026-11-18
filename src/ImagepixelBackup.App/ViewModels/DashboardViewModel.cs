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
    private static readonly TimeSpan StorageStatusInterval = TimeSpan.FromMinutes(30);

    private readonly BackupManager _manager;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _scheduler;
    private MountSession? _mount;
    private bool _schedulerBusy;
    private DateTimeOffset _storageStatusLoaded;

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
        _scheduler.Tick += async (_, _) =>
        {
            RefreshDriveLetters();
            await RunDueBackupsAsync();
            if (DateTimeOffset.Now - _storageStatusLoaded >= StorageStatusInterval)
            {
                await RefreshStorageStatusAsync();
            }
        };
        _scheduler.Start();

        RefreshDriveLetters();
        _ = RefreshStorageStatusAsync();
    }

    /// <summary>How much storage is booked and used, or null if there are no figures.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasStorageStatus), nameof(StoragePercent), nameof(StorageText), nameof(StorageDetail),
        nameof(StorageWarning), nameof(HasStorageWarning))]
    public partial StorageStatus? StorageStatus { get; set; }

    public bool HasStorageStatus => StorageStatus is not null;

    public double StoragePercent => Math.Min(100, (StorageStatus?.Fraction ?? 0) * 100);

    public string StorageText => StorageStatus is { } status
        ? $"{Texts.Bytes(status.UsedBytes)} von {Texts.Bytes(status.BookedBytes)} belegt ({Texts.Percent(status.Fraction)})"
        : "";

    public string StorageDetail => StorageStatus is { } status
        ? "Stand: " + Texts.Day(status.MeasuredAt, DateTimeOffset.Now)
        : "";

    /// <summary>The warning shown from 80 % on. Backups keep running when the storage is full.</summary>
    public string? StorageWarning => StorageStatus switch
    {
        { IsFull: true } status =>
            $"Dein gebuchter Speicher ist voll ({Texts.Percent(status.Fraction)}). Die Sicherungen laufen weiter. Imagepixel meldet sich bei dir wegen der nächsten Größe.",
        { IsWarning: true } status =>
            $"Dein Speicher ist zu {Texts.Percent(status.Fraction)} belegt. Die Sicherungen laufen weiter. Imagepixel meldet sich bei dir wegen der nächsten Größe.",
        _ => null,
    };

    public bool HasStorageWarning => StorageWarning is not null;

    /// <summary>Asks again how much storage is booked and used. Keeps the last figures if that fails.</summary>
    public async Task RefreshStorageStatusAsync()
    {
        _storageStatusLoaded = DateTimeOffset.Now;
        try
        {
            StorageStatus = await _main.LoadStorageStatus();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the figures are a convenience, a failure must not disturb the backups
        }
    }

    /// <summary>
    /// Whether to point out that the application does not start by itself yet. The
    /// schedule only runs while the application does, and on macOS and Linux nothing
    /// starts it at login until the user agrees to that.
    /// </summary>
    public bool ShowStartAtLoginNote => _main.LoginItem is { IsEnabled: false };

    /// <summary>Looks again whether the application starts at login.</summary>
    public void RefreshStartAtLogin() => OnPropertyChanged(nameof(ShowStartAtLoginNote));

    [RelayCommand]
    private void StartAtLogin()
    {
        StatusLine = _main.EnableStartAtLogin() ?? Texts.StartsAtLoginNow;
        RefreshStartAtLogin();
    }

    public ObservableCollection<FolderItemViewModel> Folders { get; } = [];

    public bool HasNoFolders => Folders.Count == 0;

    /// <summary>Whether this computer can show the backup as a drive.</summary>
    public bool IsMountSupported => BackupManager.IsMountSupported;

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

    public bool CanMount => !IsMounting && (!OffersDriveLetter || SelectedDriveLetter is not null);

    /// <summary>Windows opens the backup under a drive letter, which the customer may choose.</summary>
    public bool OffersDriveLetter => OperatingSystem.IsWindows() && IsMountSupported;

    /// <summary>The drive letters that are free at the moment, in the order of the alphabet.</summary>
    public ObservableCollection<string> DriveLetters { get; } = [];

    /// <summary>The letter the backup will be opened under: a suggestion until the customer picks another one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMount), nameof(HasNoDriveLetter))]
    public partial string? SelectedDriveLetter { get; set; }

    public bool HasNoDriveLetter => OffersDriveLetter && SelectedDriveLetter is null;

    /// <summary>Finds the free drive letters. Replaced in tests.</summary>
    public Func<IReadOnlyList<string>> FindFreeDriveLetters { get; set; } = MountSession.FreeDriveLetters;

    private bool _refreshingDriveLetters;

    /// <summary>
    /// Brings the list of free drive letters up to date. The letter the customer chose
    /// stays selected while it is free; if something else took it, another one is
    /// suggested for now and theirs comes back once it is free again.
    /// </summary>
    public void RefreshDriveLetters()
    {
        if (!OffersDriveLetter || IsMounted || IsMounting)
        {
            return;
        }

        var free = FindFreeDriveLetters();
        var suggestion = MountSession.SuggestDriveLetter(free, _manager.Config.DriveLetter);
        var offered = free.Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (suggestion == SelectedDriveLetter && offered.SequenceEqual(DriveLetters))
        {
            return;
        }

        _refreshingDriveLetters = true;
        try
        {
            DriveLetters.Clear();
            foreach (var letter in offered)
            {
                DriveLetters.Add(letter);
            }

            SelectedDriveLetter = suggestion;
        }
        finally
        {
            _refreshingDriveLetters = false;
        }
    }

    // What the customer picks is remembered; what is only suggested is not.
    partial void OnSelectedDriveLetterChanged(string? value)
    {
        if (!_refreshingDriveLetters && value is not null && value != _manager.Config.DriveLetter)
        {
            _manager.Config.DriveLetter = value;
            _manager.Save();
        }
    }

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
            if (!Folders.Any(f => string.Equals(f.Path, cloud.Path, FolderRules.PathComparison)))
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

        // the letter the customer sees is the one that is used
        var letter = OffersDriveLetter ? SelectedDriveLetter : null;
        IsMounting = true;
        StatusLine = "Laufwerk wird geöffnet …";
        try
        {
            _mount = await Task.Run(() => _manager.MountAsync(Texts.ProductName, driveLetter: letter));
            _mount.Exited += (_, _) => Dispatcher.UIThread.Post(OnMountEnded);
            IsMounted = true;
            MountText = Texts.DriveOpen(_mount.Mountpoint);
            StatusLine = Texts.DriveReady(_mount.Mountpoint, _mount.RootPath);
            _main.OpenInFileManager(_mount.RootPath);
        }
        catch (Exception e)
        {
            StatusLine = "Das Laufwerk konnte nicht geöffnet werden: " + Texts.Error(e);
        }
        finally
        {
            IsMounting = false;
            RefreshDriveLetters();
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
        RefreshDriveLetters();
    }

    [RelayCommand]
    private void Restore() => _main.ShowRestore();

    [RelayCommand]
    private void ShowAbout() => _main.ShowAbout();

    /// <summary>Opens the page where the customer says what the backup of a folder leaves out.</summary>
    public void ShowExclusions(FolderItemViewModel folder) => _main.ShowExclusions(folder);

    public void ShowSuggestion(FolderItemViewModel folder) => _main.ShowSuggestion(folder);

    public void OfferSuggestion(FolderItemViewModel folder) => _main.OfferSuggestion(folder);

    // Only opens the setup. The storage, its password and the running backups stay as
    // they are until the customer has connected another storage there.
    [RelayCommand]
    private void ChangeStorage() => _main.ShowStorageChange();

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
