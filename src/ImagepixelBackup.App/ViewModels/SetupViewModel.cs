using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    // How long the look into the storage waits while the customer types access data by hand.
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(600);

    private string _region = ImagepixelStorage.DefaultRegion;
    private string? _apiUrl;
    private string? _apiToken;
    private string _root = "";
    private string _deviceName = "";
    private int _search;

    public SetupViewModel(BackupManager manager, MainViewModel main, bool switching = false)
    {
        _manager = manager;
        _main = main;
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
            OnPropertyChanged(nameof(ShowSetupCodeFields));
            OnPropertyChanged(nameof(ShowOwnAccountFields));
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

    public string SwitchNote => _manager.Config.Storage is { } storage ? Texts.SwitchNote(Texts.StorageName(storage)) : "";

    [RelayCommand]
    private void Back() => _main.ShowDashboard();

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasMode), nameof(IsBackup), nameof(IsRestore), nameof(Subtitle),
        nameof(CanChooseDestination), nameof(ShowImagepixelFields), nameof(ShowLocalFields),
        nameof(DestinationHeading), nameof(PasswordHeading), nameof(PasswordLabel), nameof(ConnectText),
        nameof(FolderHint), nameof(FoundBackupsNote), nameof(ShowSetupCodeFields), nameof(ShowOwnAccountFields))]
    public partial SetupMode Mode { get; set; }

    public bool HasMode => Mode != SetupMode.None;

    public bool IsBackup => Mode == SetupMode.Backup;

    public bool IsRestore => Mode == SetupMode.Restore;

    public string Subtitle => IsSwitching && IsBackup
        ? "Für neue Zugangsdaten oder einen anderen Speicher."
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

    /// <summary>Where the backup is stored. The online storage at Wasabi is the recommended choice and preselected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsImagepixel), nameof(IsLocal), nameof(DestinationHint), nameof(HasDestinationHint),
        nameof(ShowImagepixelFields), nameof(ShowLocalFields), nameof(ShowSetupCodeFields), nameof(ShowOwnAccountFields))]
    public partial StorageKind Kind { get; set; } = StorageKind.ImagepixelStorage;

    public bool IsImagepixel => Kind == StorageKind.ImagepixelStorage;

    public bool IsLocal => Kind == StorageKind.LocalFolder;

    // The fields of a storage only appear once the customer has reached that step.
    public bool ShowImagepixelFields => IsImagepixel && CanChooseDestination;

    public bool ShowLocalFields => IsLocal && CanChooseDestination;

    /// <summary>
    /// The way to the access data of the online storage: false for the setup code that the
    /// administrator hands out, true for the access data of a Wasabi account of one's own,
    /// typed in by hand.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesSetupCode), nameof(ShowSetupCodeFields), nameof(ShowOwnAccountFields), nameof(HasCodeProblem))]
    public partial bool UseOwnAccount { get; set; }

    public bool UsesSetupCode => !UseOwnAccount;

    public bool ShowSetupCodeFields => ShowImagepixelFields && !UseOwnAccount;

    public bool ShowOwnAccountFields => ShowImagepixelFields && UseOwnAccount;

    [RelayCommand]
    private void ChooseAccessWay(string way)
    {
        UseOwnAccount = way == "OwnAccount";
        ErrorMessage = null;
    }

    partial void OnUseOwnAccountChanged(bool value) => UpdateAccess();

    /// <summary>The bucket of the own Wasabi account, as typed.</summary>
    [ObservableProperty]
    public partial string OwnBucket { get; set; } = "";

    /// <summary>The region of that bucket, as typed.</summary>
    [ObservableProperty]
    public partial string OwnRegion { get; set; } = ImagepixelStorage.DefaultRegion;

    [ObservableProperty]
    public partial string OwnAccessKeyId { get; set; } = "";

    [ObservableProperty]
    public partial string OwnSecretAccessKey { get; set; } = "";

    partial void OnOwnBucketChanged(string value) => UpdateAccess();

    partial void OnOwnRegionChanged(string value) => UpdateAccess();

    partial void OnOwnAccessKeyIdChanged(string value) => UpdateAccess();

    partial void OnOwnSecretAccessKeyChanged(string value) => UpdateAccess();

    /// <summary>Whether the fields of the online storage hold access data, from a setup code or typed in by hand.</summary>
    [ObservableProperty]
    public partial bool HasAccess { get; set; }

    /// <summary>The name of this computer as the setup code gives it, or empty.</summary>
    public string DeviceName => _deviceName;

    public bool HasDeviceName => _deviceName.Length > 0;

    /// <summary>Shows the password while typing. It cannot be reset later, so typos matter.</summary>
    [ObservableProperty]
    public partial bool ShowPassword { get; set; }

    [ObservableProperty]
    public partial string SetupCodeText { get; set; } = "";

    /// <summary>Whether what was entered is a setup code. Without one, the access data have to be typed in by hand.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeProblem))]
    public partial bool CodeAccepted { get; set; }

    /// <summary>Something was entered, but it is not a setup code.</summary>
    public bool HasCodeProblem => UsesSetupCode && !CodeAccepted && !string.IsNullOrWhiteSpace(SetupCodeText);

    /// <summary>What is said under a setup code that works, with the name of the computer if the code names one.</summary>
    public string CodeAcceptedText => HasDeviceName ? Texts.SetupCodeForDevice(DeviceName) : Texts.SetupCodeAccepted;

    [ObservableProperty]
    public partial string Bucket { get; set; } = "";

    [ObservableProperty]
    public partial string AccessKeyId { get; set; } = "";

    [ObservableProperty]
    public partial string SecretAccessKey { get; set; } = "";

    /// <summary>
    /// The folder inside the online storage that holds the backup, as the customer typed
    /// it. Empty for a backup that lies directly in the storage, or directly in the folder
    /// of this computer when the setup code names one.
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
        NotifyStorageChange();
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
    /// Fills the fields of the online storage from the way the customer chose: the setup
    /// code, which carries the bucket and both keys and maybe the folder of this computer,
    /// or the access data of an own Wasabi account, typed in by hand.
    /// </summary>
    private void UpdateAccess()
    {
        SetupCode? code = SetupCode.TryDecode(SetupCodeText, out var decoded) ? decoded : null;
        CodeAccepted = code is not null;

        if (UseOwnAccount)
        {
            (Bucket, AccessKeyId, SecretAccessKey, _region, _apiUrl, _apiToken) =
                (OwnBucket.Trim(), OwnAccessKeyId.Trim(), OwnSecretAccessKey.Trim(), OwnRegionNormalized, null, null);
            (_root, _deviceName) = ("", "");
        }
        else if (code is not null)
        {
            (Bucket, AccessKeyId, SecretAccessKey, _region, _apiUrl, _apiToken) =
                (code.Bucket, code.AccessKeyId, code.SecretAccessKey, code.Region, code.ApiUrl, code.ApiToken);
            (_root, _deviceName) = (code.Root, code.DeviceName);
        }
        else
        {
            (Bucket, AccessKeyId, SecretAccessKey, _region, _apiUrl, _apiToken) =
                ("", "", "", ImagepixelStorage.DefaultRegion, null, null);
            (_root, _deviceName) = ("", "");
        }

        HasAccess = Bucket.Length > 0 && AccessKeyId.Length > 0 && SecretAccessKey.Length > 0
            && (UsesSetupCode || OwnAccountProblem is null);
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(HasDeviceName));
        OnPropertyChanged(nameof(CodeAcceptedText));
        OnPropertyChanged(nameof(HasCodeProblem));
        NotifyStorageChange();
        Search = LookForBackupsAsync();
    }

    // the region as it goes into the address of the storage: no spaces, in small letters
    private string OwnRegionNormalized =>
        string.IsNullOrWhiteSpace(OwnRegion) ? ImagepixelStorage.DefaultRegion : OwnRegion.Trim().ToLowerInvariant();

    /// <summary>What is wrong with the access data of the own account so far, or null.</summary>
    private string? OwnAccountProblem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(OwnBucket))
            {
                return Texts.OwnBucketMissing;
            }

            if (!ImagepixelStorage.IsBucketName(OwnBucket.Trim()))
            {
                return Texts.OwnBucketProblem;
            }

            if (!ImagepixelStorage.IsRegionName(OwnRegionNormalized))
            {
                return Texts.OwnRegionProblem;
            }

            return string.IsNullOrWhiteSpace(OwnAccessKeyId) || string.IsNullOrWhiteSpace(OwnSecretAccessKey)
                ? Texts.OwnKeysMissing
                : null;
        }
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
            if (!HasAccess)
            {
                return;
            }

            // access data typed by hand are looked at once the customer pauses
            if (UseOwnAccount)
            {
                await Task.Delay(TypingPause);
                if (search != _search)
                {
                    return;
                }
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

        var folder = IsImagepixel ? StorageFolder.Normalize(Folder) : "";
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
            if (UseOwnAccount)
            {
                if (OwnAccountProblem is { } own)
                {
                    return own;
                }
            }
            else if (!CodeAccepted)
            {
                return HasCodeProblem ? Texts.SetupCodeProblem : Texts.SetupCodeMissing;
            }

            if (FolderProblem is { } problem)
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
