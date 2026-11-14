using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ImagepixelBackup.Core;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>The window: shows the setup until a storage is connected, then the overview.</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly BackupManager _manager;
    private DashboardViewModel? _dashboard;

    /// <param name="manager">The operations of the application.</param>
    /// <param name="cloudFolders">
    /// The cloud folders to offer as a source. Looked up on this computer when omitted.
    /// </param>
    public MainViewModel(BackupManager manager, IReadOnlyList<CloudFolder>? cloudFolders = null)
    {
        _manager = manager;
        LoadStorageStatus = () => manager.GetStorageStatusAsync();
        CloudFolders = cloudFolders ?? CloudFolderDetector.Detect();
        CurrentPage = manager.IsConnected ? Dashboard : new SetupViewModel(manager, this);
    }

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; set; }

    /// <summary>The folders of cloud storage services found on this computer.</summary>
    public IReadOnlyList<CloudFolder> CloudFolders { get; }

    /// <summary>Lets the user choose a folder. Set by the window, which owns the dialog.</summary>
    public Func<string, Task<string?>> PickFolder { get; set; } = _ => Task.FromResult<string?>(null);

    /// <summary>Finds out how much storage is booked and used. Replaceable for tests.</summary>
    public Func<Task<StorageStatus?>> LoadStorageStatus { get; set; }

    /// <summary>Opens a folder or drive in the file manager. Replaceable for tests.</summary>
    public Action<string> OpenInFileManager { get; set; } = path =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    public DashboardViewModel Dashboard => _dashboard ??= new DashboardViewModel(_manager, this);

    /// <summary>Shows the overview, optionally starting the backups that are due without waiting for the schedule.</summary>
    public void ShowDashboard(bool startDueBackups = false)
    {
        CurrentPage = Dashboard;
        if (startDueBackups)
        {
            _ = Dashboard.RunDueBackupsAsync();
        }
    }

    public void ShowRestore() => CurrentPage = new RestoreViewModel(_manager, this);

    public void ShowAbout() => CurrentPage = new AboutViewModel(this, CurrentPage);

    public async Task ShowSetupAsync()
    {
        if (_dashboard is not null)
        {
            await _dashboard.ShutdownAsync();
            _dashboard = null;
        }

        CurrentPage = new SetupViewModel(_manager, this);
    }

    /// <summary>Stops everything that runs in the background before the application exits.</summary>
    public Task ShutdownAsync() => _dashboard?.ShutdownAsync() ?? Task.CompletedTask;
}
