using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImagepixelBackup.Core;
using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>What the customer wants to do on the first screen.</summary>
public enum SetupMode
{
    /// <summary>Nothing has been chosen yet.</summary>
    None,

    /// <summary>Set up a backup: choose what to back up, where to, and a password.</summary>
    Backup,

    /// <summary>Get data back from an existing backup, for example on a new computer.</summary>
    Restore,
}

/// <summary>A backup the storage already holds, offered for one click instead of typing its folder.</summary>
public sealed record FoundBackup(string Folder, string Label);

/// <summary>
/// First start. The customer chooses between backing up and restoring. Backing up asks
/// what to back up, where to, and for a new password. Restoring asks where the backup
/// is and for its password, and then shows the saved states.
/// </summary>
public partial class SetupViewModel : ViewModelBase
{
    private const int MinimumPasswordLength = 8;

    private readonly BackupManager _manager;
    private readonly MainViewModel _main;
    private readonly DevicePairing? _pairing;
    private string _region = ImagepixelStorage.DefaultRegion;
    private string? _apiUrl;
    private string? _apiToken;
    private string _root = "";
    private string _deviceName = "";
    private string _deviceId = "";
    private int _search;

    // the access data the pairing with the customer portal brought, once they are there
    private DeviceAccess? _paired;

    // what the portal said when the pairing failed, or null if the reason lies here
    private string? _pairingMessage;

    // counts up whenever asking the portal about the pairing is to stop or start anew
    private int _pairingRun;

    public SetupViewModel(BackupManager manager, MainViewModel main, bool switching = false)
    {
        _manager = manager;
        _main = main;
        _pairing = main.Pairing;
        UsePairing = _pairing is not null;
        IsSwitching = switching && manager.IsConnected;
        FoundBackups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFoundBackups));
        Sources.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSources));
            OnPropertyChanged(nameof(HasCloudSource));
            OnPropertyChanged(nameof(CanChooseDestination));
            OnPropertyChanged(nameof(ShowImagepixelFields));
            OnPropertyChanged(nameof(ShowLocalFields));
            OnPropertyChanged(nameof(ShowKeptFoldersNote));
            OnPropertyChanged(nameof(ShowAccessChoice));
            OnPropertyChanged(nameof(ShowPairingFields));
            OnPropertyChanged(nameof(ShowSetupCodeFields));
        };

        // renewing the access data or moving: the folders that are backed up come along,
        // and the destination for new folders stays as it was unless the customer changes it
        if (IsSwitching)
        {
            foreach (var folder in manager.Config.Folders)
            {
                Sources.Add(folder.Path);
            }

            Mode = SetupMode.Backup;
            Folder = manager.Config.Storage!.Folder;
        }

        // a pairing begun earlier goes on where it stopped, also after a restart
        if (CurrentPairing() is { } pending)
        {
            ShowPairing(pending);
            if (pending.Stage != Core.PairingStage.Done)
            {
                PairingWatch = WatchPairingAsync(++_pairingRun, checkAtOnce: true);
            }
        }
    }

    public string Title => IsSwitching ? Texts.ChangeStorage : "Willkommen bei Imagepixel Backup";

    /// <summary>Whether step 1 explains that the folders backed up so far keep their settings.</summary>
    public bool ShowKeptFoldersNote => IsSwitching && HasSources;

    /// <summary>
    /// Whether what was entered points at the storage that is connected: the same bucket,
    /// or the same folder on a disk. Then nothing changes but the access data.
    /// </summary>
    public bool IsSameStorage =>
        IsSwitching && IsBackup && EnteredStorage() is { } entered && BackupManager.IsSameStorage(_manager.Config.Storage!, entered);

    /// <summary>Whether what was entered points at another storage, where the folders start anew.</summary>
    public bool IsOtherStorage => IsSwitching && IsBackup && EnteredStorage() is not null && !IsSameStorage;

    public string StorageChangeNote => IsSameStorage ? Texts.SameStorageNote : IsOtherStorage ? Texts.OtherStorageNote : "";

    public bool HasStorageChangeNote => IsSameStorage || IsOtherStorage;

    // The storage the fields describe so far, or null while they are not filled in.
    private StorageSettings? EnteredStorage()
    {
        if (IsImagepixel)
        {
            return HasAccess ? StorageFromFields("") : null;
        }

        return string.IsNullOrWhiteSpace(LocalPath) ? null : StorageFromFields("");
    }

    private void NotifyStorageChange()
    {
        OnPropertyChanged(nameof(IsSameStorage));
        OnPropertyChanged(nameof(IsOtherStorage));
        OnPropertyChanged(nameof(StorageChangeNote));
        OnPropertyChanged(nameof(HasStorageChangeNote));
        OnPropertyChanged(nameof(ConnectText));
    }

    partial void OnKindChanged(StorageKind value) => NotifyStorageChange();

    partial void OnLocalPathChanged(string value) => NotifyStorageChange();

    /// <summary>
    /// Whether the customer came here from the overview to move to another storage.
    /// The storage they have stays connected until another one is set up here, and
    /// they can go back without changing anything.
    /// </summary>
    public bool IsSwitching { get; }

    public string SwitchNote => _manager.Config.Storage is { } storage ? Texts.SwitchNote(Texts.StorageName(storage), _pairing is not null) : "";

    [RelayCommand]
    private void Back()
    {
        // a pairing under way is kept and goes on when the setup is opened again
        _pairingRun++;
        _main.ShowDashboard();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasMode), nameof(IsBackup), nameof(IsRestore), nameof(Subtitle),
        nameof(CanChooseDestination), nameof(ShowImagepixelFields), nameof(ShowLocalFields),
        nameof(DestinationHeading), nameof(PasswordHeading), nameof(PasswordLabel), nameof(ConnectText),
        nameof(FolderHint), nameof(FoundBackupsNote), nameof(CanPair), nameof(UsesPairing), nameof(UsesSetupCode),
        nameof(ShowAccessChoice), nameof(ShowPairingFields), nameof(ShowSetupCodeFields))]
    public partial SetupMode Mode { get; set; }

    public bool HasMode => Mode != SetupMode.None;

    public bool IsBackup => Mode == SetupMode.Backup;

    public bool IsRestore => Mode == SetupMode.Restore;

    public string Subtitle => IsSwitching && IsBackup
        ? "Für neue Zugangsdaten von Imagepixel oder einen anderen Speicher."
        : Mode switch
    {
        SetupMode.Backup => "In drei Schritten ist dein Backup eingerichtet.",
        SetupMode.Restore => "In zwei Schritten bist du wieder bei deinen Daten.",
        _ => "Was möchtest du tun?",
    };

    /// <summary>Cloud storage found on this computer, offered as a source with one click.</summary>
    public IReadOnlyList<CloudFolder> CloudFolders => _main.CloudFolders;

    public bool HasCloudFolders => CloudFolders.Count > 0;

    /// <summary>Whether a chosen folder belongs to a cloud service, which calls for an explanation.</summary>
    public bool HasCloudSource => Sources.Any(source => CloudFolderDetector.Containing(source, CloudFolders) is not null);

    /// <summary>The folders the customer wants to back up.</summary>
    public ObservableCollection<string> Sources { get; } = [];

    public bool HasSources => Sources.Count > 0;

    /// <summary>
    /// The storage can be chosen once it is clear what is backed up. Restoring starts
    /// with the storage, because that is where the backup is.
    /// </summary>
    public bool CanChooseDestination => IsRestore || (IsBackup && HasSources);

    public string DestinationHeading => IsRestore ? "1. Wo liegt dein Backup?" : "2. Wohin soll gesichert werden?";

    public string PasswordHeading => IsRestore ? "2. Dein Verschlüsselungs-Passwort" : "3. Dein Verschlüsselungs-Passwort";

    public string PasswordLabel => IsRestore
        ? "Verschlüsselungs-Passwort"
        : $"Verschlüsselungs-Passwort (mindestens {MinimumPasswordLength} Zeichen)";

    public string ConnectText => IsRestore ? "Backup öffnen"
        : IsSameStorage ? "Speichern"
        : IsSwitching ? "Zum neuen Speicher wechseln"
        : "Backup starten";

    /// <summary>Where the backup is stored. Imagepixel Storage is the recommended choice and preselected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsImagepixel), nameof(IsLocal), nameof(DestinationHint), nameof(HasDestinationHint),
        nameof(ShowImagepixelFields), nameof(ShowLocalFields), nameof(ShowAccessChoice), nameof(ShowPairingFields),
        nameof(ShowSetupCodeFields))]
    public partial StorageKind Kind { get; set; } = StorageKind.ImagepixelStorage;

    public bool IsImagepixel => Kind == StorageKind.ImagepixelStorage;

    public bool IsLocal => Kind == StorageKind.LocalFolder;

    // The fields of a storage only appear once the customer has reached that step.
    public bool ShowImagepixelFields => IsImagepixel && CanChooseDestination;

    public bool ShowLocalFields => IsLocal && CanChooseDestination;

    /// <summary>
    /// Whether this computer can be paired with the customer portal instead of entering a
    /// setup code: where the portal is set up, and only for backing up. The access data
    /// of a pairing reach the folder of this computer alone, which holds nothing to
    /// restore from yet.
    /// </summary>
    public bool CanPair => _pairing is not null && !IsRestore;

    /// <summary>The way to the access data the customer chose: pairing with the portal, or else the setup code.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesPairing), nameof(UsesSetupCode), nameof(ShowPairingFields), nameof(ShowSetupCodeFields))]
    public partial bool UsePairing { get; set; }

    public bool UsesPairing => CanPair && UsePairing;

    public bool UsesSetupCode => !UsesPairing;

    public bool ShowAccessChoice => ShowImagepixelFields && CanPair;

    public bool ShowPairingFields => ShowImagepixelFields && UsesPairing;

    public bool ShowSetupCodeFields => ShowImagepixelFields && UsesSetupCode;

    /// <summary>Whether the fields of Imagepixel Storage hold access data, from a setup code or from the pairing.</summary>
    [ObservableProperty]
    public partial bool HasAccess { get; set; }

    /// <summary>How far the pairing has got, or null while no code has been asked for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(ShowPairingIntro), nameof(ShowPairingCode), nameof(IsPairingPending), nameof(IsPairingDone),
        nameof(PairingStatusText), nameof(PairingProblem), nameof(HasPairingProblem), nameof(CanRequestPairingCode),
        nameof(PairingRequestText))]
    public partial PairingStage? PairingStage { get; set; }

    /// <summary>The code to enter in the portal, such as "K7QM-4TZP".</summary>
    [ObservableProperty]
    public partial string PairingCode { get; set; } = "";

    [ObservableProperty]
    public partial string PairingCodeHint { get; set; } = "";

    /// <summary>Why no code could be asked for, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairingProblem), nameof(HasPairingProblem))]
    public partial string? PairingStartProblem { get; set; }

    /// <summary>The portal could not be asked last time. It is asked again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairingStatusText))]
    public partial bool PairingUnreachable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRequestPairingCode), nameof(PairingRequestText))]
    public partial bool IsRequestingPairingCode { get; set; }

    public bool ShowPairingIntro => PairingStage is null or Core.PairingStage.Failed or Core.PairingStage.Expired;

    public bool ShowPairingCode => PairingStage is Core.PairingStage.Waiting;

    /// <summary>Whether the portal is being waited for: the code to be entered, or the access data to be made.</summary>
    public bool IsPairingPending => PairingStage is Core.PairingStage.Waiting or Core.PairingStage.InProgress;

    public bool IsPairingDone => PairingStage is Core.PairingStage.Done;

    public string PairingStatusText => PairingUnreachable && IsPairingPending
        ? Texts.PairingUnreachable
        : PairingStage switch
        {
            Core.PairingStage.Waiting => Texts.PairingWaiting,
            Core.PairingStage.InProgress => Texts.PairingInProgress,
            _ => "",
        };

    public string PairingDoneText => Texts.PairingDone(_paired?.DeviceName ?? "");

    public string? PairingProblem => PairingStage switch
    {
        Core.PairingStage.Failed => Texts.PairingFailed(_pairingMessage),
        Core.PairingStage.Expired => Texts.PairingExpired,
        _ => PairingStartProblem,
    };

    public bool HasPairingProblem => PairingProblem is not null;

    public bool CanRequestPairingCode =>
        !IsRequestingPairingCode && PairingStage is null or Core.PairingStage.Failed or Core.PairingStage.Expired;

    public string PairingRequestText => IsRequestingPairingCode ? Texts.PairingRequesting
        : PairingStage is null ? Texts.PairingRequestCode
        : Texts.PairingRequestNewCode;

    [RelayCommand]
    private void ChooseAccessWay(string way)
    {
        UsePairing = way == "Pairing";
        ErrorMessage = null;
    }

    /// <summary>Asks the portal for a code and waits for it to be entered there.</summary>
    [RelayCommand]
    private async Task RequestPairingCodeAsync()
    {
        if (_pairing is null || !CanRequestPairingCode)
        {
            return;
        }

        IsRequestingPairingCode = true;
        PairingStartProblem = null;
        ErrorMessage = null;
        try
        {
            ShowPairing(await _pairing.StartAsync());
            PairingWatch = WatchPairingAsync(++_pairingRun, checkAtOnce: false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or PortalException)
        {
            PairingStartProblem = Texts.PairingStartFailed(e);
        }
        catch (System.Security.SecurityException e)
        {
            // the key of the pairing could not be kept
            PairingStartProblem = Texts.Error(e);
        }
        finally
        {
            IsRequestingPairingCode = false;
        }
    }

    /// <summary>The asking of the portal that goes on while the pairing waits, for tests to wait for.</summary>
    public Task PairingWatch { get; private set; } = Task.CompletedTask;

    /// <summary>Asks the portal once how far the pairing has got. The page does it by itself while the pairing waits.</summary>
    public async Task CheckPairingAsync()
    {
        if (_pairing is null)
        {
            return;
        }

        try
        {
            var status = await _pairing.CheckAsync();
            PairingUnreachable = false;
            ShowPairing(status);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or PortalException)
        {
            // asked again next time; what was shown stays
            PairingUnreachable = true;
        }
        catch (InvalidOperationException)
        {
            // the pairing was given up meanwhile, on another page
            _pairingRun++;
        }
        catch (System.Security.SecurityException e)
        {
            // the access data could not be kept: said, and not asked again on its own
            _pairingRun++;
            PairingStartProblem = Texts.Error(e);
        }
    }

    // The pairing kept from earlier. A keyring that cannot be reached keeps it hidden
    // instead of keeping the setup from opening: the setup code still works.
    private PairingStatus? CurrentPairing()
    {
        try
        {
            return _pairing?.Current();
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }

    // Asks the portal again and again while the pairing waits, until another run takes over.
    private async Task WatchPairingAsync(int run, bool checkAtOnce)
    {
        var first = true;
        while (run == _pairingRun && IsPairingPending)
        {
            if (!(first && checkAtOnce))
            {
                await Task.Delay(_main.PairingInterval);
            }

            first = false;
            if (run != _pairingRun)
            {
                return;
            }

            await CheckPairingAsync();
        }
    }

    private void ShowPairing(PairingStatus status)
    {
        _pairingMessage = status.Message;
        PairingCode = status.Code;
        PairingCodeHint = Texts.PairingCodeHint(status.ValidUntil);
        PairingStartProblem = null;
        if (status.Access is { } access && !Equals(access, _paired))
        {
            _paired = access;
            OnPropertyChanged(nameof(PairingDoneText));
            UpdateAccess();
        }

        PairingStage = status.Stage;
        OnPropertyChanged(nameof(PairingProblem));
        OnPropertyChanged(nameof(HasPairingProblem));
        OnPropertyChanged(nameof(PairingStatusText));
    }

    partial void OnUsePairingChanged(bool value) => UpdateAccess();

    /// <summary>Shows the password while typing. It cannot be reset later, so typos matter.</summary>
    [ObservableProperty]
    public partial bool ShowPassword { get; set; }

    [ObservableProperty]
    public partial string SetupCodeText { get; set; } = "";

    /// <summary>Whether what was entered is a setup code of Imagepixel. The storage cannot be reached without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeProblem))]
    public partial bool CodeAccepted { get; set; }

    /// <summary>Something was entered, but it is not a setup code.</summary>
    public bool HasCodeProblem => !CodeAccepted && !string.IsNullOrWhiteSpace(SetupCodeText);

    [ObservableProperty]
    public partial string Bucket { get; set; } = "";

    [ObservableProperty]
    public partial string AccessKeyId { get; set; } = "";

    [ObservableProperty]
    public partial string SecretAccessKey { get; set; } = "";

    /// <summary>
    /// The folder inside the Imagepixel Storage that holds the backup, as the customer
    /// typed it. Empty for a backup that lies directly in the storage.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderProblem), nameof(HasFolderProblem))]
    public partial string Folder { get; set; } = "";

    /// <summary>What is wrong with the folder, or null. Said while typing, not only on the last click.</summary>
    public string? FolderProblem => Texts.FolderProblem(StorageFolder.Check(StorageFolder.Normalize(Folder)));

    public bool HasFolderProblem => FolderProblem is not null;

    public string FolderHint => IsRestore ? Texts.FolderHintRestore : Texts.FolderHintBackup;

    /// <summary>The backups found in the storage. Empty until the storage is known and has been looked into.</summary>
    public ObservableCollection<FoundBackup> FoundBackups { get; } = [];

    public bool HasFoundBackups => FoundBackups.Count > 0;

    public string FoundBackupsNote => IsRestore ? Texts.FoundBackupsRestore : Texts.FoundBackupsBackup;

    /// <summary>The look into the storage that is under way, for tests to wait for.</summary>
    public Task Search { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationHint), nameof(HasDestinationHint))]
    public partial string LocalPath { get; set; } = "";

    /// <summary>
    /// Explains what it means to store the backup in the folder of a cloud service.
    /// Null when the chosen folder is an ordinary one, and when restoring.
    /// </summary>
    public string? DestinationHint =>
        IsBackup && IsLocal && !string.IsNullOrWhiteSpace(LocalPath) && CloudOf(LocalPath) is { } cloud
            ? Texts.CloudDestination(cloud.Name)
            : null;

    public bool HasDestinationHint => DestinationHint is not null;

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string PasswordRepeat { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsBusy { get; set; }

    public bool CanEdit => !IsBusy;

    [ObservableProperty]
    public partial string BusyText { get; set; } = "";

    partial void OnModeChanged(SetupMode value)
    {
        OnPropertyChanged(nameof(DestinationHint));
        OnPropertyChanged(nameof(HasDestinationHint));

        // restoring takes the setup code, also where pairing was chosen for backing up
        UpdateAccess();
    }

    partial void OnSetupCodeTextChanged(string value)
    {
        UpdateAccess();

        // a code that works takes back what was said about the one before
        if (CodeAccepted && UsesSetupCode)
        {
            ErrorMessage = null;
        }
    }

    /// <summary>
    /// Fills the fields of Imagepixel Storage from the way the customer chose: the setup
    /// code, which carries the bucket and both keys, or the access data of the pairing,
    /// which carry the folder of this computer in the bucket as well.
    /// </summary>
    private void UpdateAccess()
    {
        SetupCode? code = SetupCode.TryDecode(SetupCodeText, out var decoded) ? decoded : null;
        CodeAccepted = code is not null;

        if (UsesPairing && _paired is { } access)
        {
            (Bucket, AccessKeyId, SecretAccessKey, _region, _apiUrl, _apiToken) =
                (access.Bucket, access.AccessKeyId, access.SecretAccessKey, access.Region, access.ApiUrl, access.ApiToken);
            (_root, _deviceName, _deviceId) = (access.Root, access.DeviceName, access.DeviceId);
        }
        else if (UsesSetupCode && code is not null)
        {
            (Bucket, AccessKeyId, SecretAccessKey, _region, _apiUrl, _apiToken) =
                (code.Bucket, code.AccessKeyId, code.SecretAccessKey, code.Region, code.ApiUrl, code.ApiToken);
            (_root, _deviceName, _deviceId) = ("", "", "");
        }
        else
        {
            (Bucket, AccessKeyId, SecretAccessKey, _region, _apiUrl, _apiToken) =
                ("", "", "", ImagepixelStorage.DefaultRegion, null, null);
            (_root, _deviceName, _deviceId) = ("", "", "");
        }

        var hadAccess = HasAccess;
        HasAccess = Bucket.Length > 0;
        if (HasAccess && !hadAccess)
        {
            ErrorMessage = null;
        }

        OnPropertyChanged(nameof(HasCodeProblem));
        NotifyStorageChange();
        Search = LookForBackupsAsync();
    }

    /// <summary>
    /// Looks into the storage for the backups it holds, so that the customer can pick
    /// one instead of remembering how its folder was spelled. A help only: if the
    /// storage cannot be read, nothing is offered and the folder can still be typed.
    /// </summary>
    private async Task LookForBackupsAsync()
    {
        var search = ++_search;
        FoundBackups.Clear();

        IReadOnlyList<string> found;
        try
        {
            // a paired computer starts with a folder of its own, which holds no backup yet
            if (!HasAccess || UsesPairing)
            {
                return;
            }

            found = await _main.FindBackupFolders(StorageFromFields(""), SecretAccessKey.Trim());
        }
        catch (Exception)
        {
            return;
        }

        // another code was entered meanwhile: its own look fills the list
        if (search != _search)
        {
            return;
        }

        foreach (var folder in found)
        {
            FoundBackups.Add(new FoundBackup(folder, folder.Length == 0 ? Texts.NoFolder : folder));
        }
    }

    [RelayCommand]
    private void ChooseFoundBackup(FoundBackup backup)
    {
        Folder = backup.Folder;
        ErrorMessage = null;
    }

    private StorageSettings StorageFromFields(string folder) => new()
    {
        Kind = Kind,
        Bucket = Bucket.Trim(),
        Region = _region,
        AccessKeyId = AccessKeyId.Trim(),
        LocalPath = LocalPath.Trim(),
        ApiUrl = IsImagepixel ? _apiUrl ?? "" : "",
        Root = IsImagepixel ? _root : "",
        DeviceName = IsImagepixel ? _deviceName : "",
        DeviceId = IsImagepixel ? _deviceId : "",
        Folder = folder,
    };

    [RelayCommand]
    private void ChooseMode(string mode)
    {
        Mode = Enum.Parse<SetupMode>(mode);
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        if (await _main.PickFolder("Ordner auswählen, der gesichert werden soll") is not { } path)
        {
            return;
        }

        if (!Sources.Contains(path, FolderRules.PathComparer))
        {
            Sources.Add(path);
        }

        ErrorMessage = null;
    }

    [RelayCommand]
    private void AddCloudSource(CloudFolder folder)
    {
        if (!Sources.Contains(folder.Path, FolderRules.PathComparer))
        {
            Sources.Add(folder.Path);
        }

        ErrorMessage = null;
    }

    [RelayCommand]
    private void RemoveSource(string path) => Sources.Remove(path);

    [RelayCommand]
    private void ShowAbout() => _main.ShowAbout();

    [RelayCommand]
    private void ChooseKind(string kind)
    {
        Kind = Enum.Parse<StorageKind>(kind);
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var title = IsRestore ? "Ordner auswählen, in dem dein Backup liegt" : "Ordner für das Backup auswählen";
        if (await _main.PickFolder(title) is { } path)
        {
            LocalPath = path;
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        ErrorMessage = Validate();
        if (HasError)
        {
            return;
        }

        // a paired computer backs up to the top of its own folder in the storage
        var folder = IsImagepixel && UsesSetupCode ? StorageFolder.Normalize(Folder) : "";
        var storage = StorageFromFields(folder);
        var secret = IsImagepixel ? SecretAccessKey.Trim() : null;
        var apiToken = IsImagepixel ? _apiToken : null;

        Task<RepositoryState> Connect(bool create) =>
            _manager.ConnectAsync(storage, Password, secret, createIfMissing: create, apiToken: apiToken);

        IsBusy = true;
        BusyText = IsRestore ? "Backup wird geöffnet …" : "Verbindung wird geprüft …";
        try
        {
            var state = await Connect(false);
            if (state == RepositoryState.NotFound)
            {
                // Restoring never creates anything: an empty place means the data is elsewhere.
                if (IsRestore)
                {
                    ErrorMessage = IsImagepixel && HasFoundBackups
                        ? Texts.NoBackupHereButElsewhere
                        : "An diesem Ort wurde kein Backup gefunden. Bitte prüfe die Angaben. Wenn du noch kein Backup hast, wähle oben „Daten sichern“.";
                    return;
                }

                // A new backup is about to be created. Its password cannot be recovered,
                // so it has to be typed twice.
                if (Password != PasswordRepeat)
                {
                    ErrorMessage = "An diesem Ort liegt noch kein Backup. Bitte wiederhole das Passwort, damit es neu angelegt werden kann.";
                    return;
                }

                BusyText = "Backup wird eingerichtet …";
                state = await Connect(true);
            }

            if (state == RepositoryState.WrongPassword)
            {
                ErrorMessage = IsRestore
                    ? "Das Verschlüsselungs-Passwort passt nicht zu diesem Backup."
                    : "An diesem Ort liegt bereits ein Backup, aber das Passwort passt nicht dazu.";
                return;
            }

            // the access data of the pairing are kept with the storage from now on
            if (IsImagepixel && UsesPairing)
            {
                _pairingRun++;
                _pairing?.Forget();
            }

            // another storage is connected now: what ran for the previous one ends here
            if (IsSwitching)
            {
                await _main.RestartDashboardAsync();
            }

            if (IsRestore)
            {
                // straight to the saved states, which is what the customer came for
                _main.ShowRestore();
                return;
            }

            // what the customer took out of the list in step 1 is no longer backed up
            if (IsSwitching)
            {
                foreach (var dropped in _manager.Config.Folders.Where(item => !Sources.Contains(item.Path, FolderRules.PathComparer)).ToList())
                {
                    _manager.RemoveFolder(dropped);
                }
            }

            foreach (var source in Sources)
            {
                _manager.AddFolder(source);
            }

            // the overview starts the first backup of the chosen folders right away
            _main.ShowDashboard(startDueBackups: true);
        }
        catch (Exception e)
        {
            ErrorMessage = Texts.Error(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private CloudFolder? CloudOf(string path)
    {
        try
        {
            return CloudFolderDetector.Containing(path, CloudFolders);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.IOException)
        {
            // what has been typed so far is not a path yet
            return null;
        }
    }

    private string? Validate()
    {
        if (!HasMode)
        {
            return "Bitte wähle zuerst aus, was du tun möchtest.";
        }

        if (!CanChooseDestination)
        {
            return "Bitte wähle zuerst aus, was gesichert werden soll.";
        }

        if (IsImagepixel)
        {
            if (UsesPairing)
            {
                if (!HasAccess)
                {
                    return Texts.PairingMissing;
                }
            }
            else if (!CodeAccepted)
            {
                return HasCodeProblem ? Texts.SetupCodeProblem : Texts.SetupCodeMissing;
            }
            else if (FolderProblem is { } problem)
            {
                return problem;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(LocalPath))
            {
                return IsRestore
                    ? "Bitte wähle den Ordner aus, in dem dein Backup liegt."
                    : "Bitte wähle einen Ordner für das Backup aus.";
            }

            // the backup must not lie inside a folder it backs up, nor around one
            var folders = Sources.Concat(_manager.Config.Folders.Select(f => f.Path));
            if (IsBackup && folders.FirstOrDefault(folder => FolderRules.Overlap(folder, LocalPath)) is { } overlapping)
            {
                return Texts.Overlap(overlapping, LocalPath.Trim());
            }
        }

        if (IsRestore)
        {
            // an existing backup may have any password, so only an empty one is rejected
            return Password.Length == 0 ? "Bitte gib dein Verschlüsselungs-Passwort ein." : null;
        }

        if (Password.Length < MinimumPasswordLength)
        {
            return $"Bitte wähle ein Verschlüsselungs-Passwort mit mindestens {MinimumPasswordLength} Zeichen.";
        }

        return null;
    }
}
