using System;
using System.Collections.Generic;
using System.Linq;
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
        FindBackupFolders = (storage, secret) => manager.FindBackupFoldersAsync(storage, secret);
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

    /// <summary>
    /// Finds the folders of an Imagepixel Storage that hold a backup: of the storage and
    /// key given, or of the one that is connected when both are null. Replaceable for tests.
    /// </summary>
    public Func<StorageSettings?, string?, Task<IReadOnlyList<string>>> FindBackupFolders { get; set; }

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

    /// <summary>Hides the window while the application goes on. Set by the application, which owns the window.</summary>
    public Action HideWindow { get; set; } = () => { };

    /// <summary>Ends the application. Set by the application.</summary>
    public Func<Task> Quit { get; set; } = () => Task.CompletedTask;

    /// <summary>Whether a backup is running at this moment.</summary>
    public bool IsBackupRunning => _dashboard?.Folders.Any(folder => folder.IsRunning) ?? false;

    /// <summary>
    /// Called when the customer closes the window. Returns true if the window may be
    /// hidden without a word. While a backup runs, and the first time ever, a page
    /// explains instead that the application and its backups go on in the background.
    /// </summary>
    public bool MayCloseWindowSilently()
    {
        if (CurrentPage is LeaveViewModel)
        {
            return false;
        }

        var running = IsBackupRunning;
        if (!running && _manager.Config.BackgroundExplained)
        {
            return true;
        }

        _manager.MarkBackgroundExplained();
        CurrentPage = new LeaveViewModel(this, CurrentPage, quitting: false, backupRunning: running);
        return false;
    }

    /// <summary>
    /// Called when the customer asks to quit. Returns true if the application may end
    /// at once. While a backup runs, a page asks first, because quitting stops it.
    /// </summary>
    public bool MayQuitSilently()
    {
        if (!IsBackupRunning)
        {
            return true;
        }

        if (CurrentPage is not LeaveViewModel { IsQuitting: true })
        {
            var previous = CurrentPage is LeaveViewModel ? Dashboard : CurrentPage;
            CurrentPage = new LeaveViewModel(this, previous, quitting: true, backupRunning: true);
        }

        return false;
    }

    public DashboardViewModel Dashboard => _dashboard ??= new DashboardViewModel(_manager, this);

    /// <summary>Whether a storage is set up and can be opened.</summary>
    public bool HasStorage => _manager.IsConnected;

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

    public void ShowExclusions(FolderItemViewModel folder) => CurrentPage = new ExclusionsViewModel(this, _manager, folder);

    public void ShowDestination(FolderItemViewModel folder) => CurrentPage = new DestinationViewModel(this, _manager, folder);

    /// <summary>Shows what the look through a folder suggests leaving out, for the customer to answer.</summary>
    public void ShowSuggestion(FolderItemViewModel folder)
    {
        if (folder.Suggestion is { } scan)
        {
            CurrentPage = new ExclusionSuggestionViewModel(this, folder, scan);
        }
    }

    /// <summary>
    /// The same, but only if the customer is looking at the overview: a suggestion that
    /// comes up by itself does not push another page away. The folder keeps offering it.
    /// </summary>
    public void OfferSuggestion(FolderItemViewModel folder)
    {
        if (ReferenceEquals(CurrentPage, _dashboard))
        {
            ShowSuggestion(folder);
        }
    }

    public void ShowAbout() => CurrentPage = new AboutViewModel(this, CurrentPage);

    /// <summary>
    /// Shows the setup for moving to another storage. Nothing is forgotten or stopped
    /// by this: the overview keeps running behind it, and the customer can go back.
    /// </summary>
    public void ShowStorageChange() => CurrentPage = new SetupViewModel(_manager, this, switching: true);

    /// <summary>
    /// Called once another storage has been connected: what ran for the previous one
    /// is stopped, and the overview starts anew for the new one.
    /// </summary>
    public async Task RestartDashboardAsync()
    {
        if (_dashboard is not null)
        {
            await _dashboard.ShutdownAsync();
            _dashboard = null;
        }
    }

    /// <summary>Stops everything that runs in the background before the application exits.</summary>
    public Task ShutdownAsync() => _dashboard?.ShutdownAsync() ?? Task.CompletedTask;
}
