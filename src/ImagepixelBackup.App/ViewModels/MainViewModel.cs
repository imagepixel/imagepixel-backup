using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ImagepixelBackup.Core;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>
/// The window: asks once to confirm the notes on use and liability, then shows the
/// setup until a storage is connected, and the overview from then on.
/// </summary>
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

        if (manager.HasAcceptedTerms(Texts.TermsVersion))
        {
            CurrentPage = StartPage;
        }
        else
        {
            // A backup that is already set up keeps its schedule while the notes wait
            // to be confirmed: the overview, which runs the schedule, exists from the start.
            if (manager.IsConnected)
            {
                _ = Dashboard;
            }

            CurrentPage = new TermsViewModel(this);
        }
    }

    /// <summary>Where the application starts: the overview, or the setup while no storage is connected.</summary>
    private ViewModelBase StartPage => _manager.IsConnected ? Dashboard : new SetupViewModel(_manager, this);

    /// <summary>Remembers that the user confirmed the notes on use and liability, and goes on.</summary>
    public void AcceptTerms()
    {
        _manager.AcceptTerms(Texts.TermsVersion);
        CurrentPage = StartPage;
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

    /// <summary>
    /// Starts the application when the user logs in. macOS and Linux have this; on
    /// Windows the setup program takes care of it. Replaceable for tests.
    /// </summary>
    public ILoginItem? LoginItem { get; set; } = Core.LoginItem.Create();

    /// <summary>The program to start at login. Replaceable for tests.</summary>
    public Func<string?> ExecutablePath { get; set; } = () => Environment.ProcessPath;

    /// <summary>Turns starting at login on. Returns what keeps it from working, or null.</summary>
    public string? EnableStartAtLogin()
    {
        if (LoginItem is null || ExecutablePath() is not { Length: > 0 } executable)
        {
            return "Das automatische Starten lässt sich auf diesem Rechner nicht einrichten.";
        }

        if (!LoginItem.IsPermanentLocation(executable))
        {
            return LoginItem is MacLoginItem ? Texts.MoveToApplications : Texts.InstallFirst;
        }

        try
        {
            LoginItem.Enable(executable, Program.MinimizedArgument);
            return null;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            return "Das automatische Starten konnte nicht eingerichtet werden: " + e.Message;
        }
    }

    /// <summary>Keeps starting at login working after the application was moved.</summary>
    public void RefreshStartAtLogin()
    {
        try
        {
            if (LoginItem is not null && ExecutablePath() is { Length: > 0 } executable)
            {
                LoginItem.Refresh(executable, Program.MinimizedArgument);
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            // starting at login is a convenience, the application itself still works
        }
    }

    public DashboardViewModel Dashboard => _dashboard ??= new DashboardViewModel(_manager, this);

    /// <summary>Whether the overview exists, and with it the schedule that runs the backups.</summary>
    public bool IsScheduleRunning => _dashboard is not null;

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
