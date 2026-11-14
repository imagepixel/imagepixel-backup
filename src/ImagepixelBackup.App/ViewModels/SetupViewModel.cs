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
    private string _region = ImagepixelStorage.DefaultRegion;

    public SetupViewModel(BackupManager manager, MainViewModel main)
    {
        _manager = manager;
        _main = main;
        Sources.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSources));
            OnPropertyChanged(nameof(HasCloudSource));
            OnPropertyChanged(nameof(CanChooseDestination));
            OnPropertyChanged(nameof(ShowImagepixelFields));
            OnPropertyChanged(nameof(ShowLocalFields));
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasMode), nameof(IsBackup), nameof(IsRestore), nameof(Subtitle),
        nameof(CanChooseDestination), nameof(ShowImagepixelFields), nameof(ShowLocalFields),
        nameof(DestinationHeading), nameof(PasswordHeading), nameof(PasswordLabel), nameof(ConnectText))]
    public partial SetupMode Mode { get; set; }

    public bool HasMode => Mode != SetupMode.None;

    public bool IsBackup => Mode == SetupMode.Backup;

    public bool IsRestore => Mode == SetupMode.Restore;

    public string Subtitle => Mode switch
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

    public string ConnectText => IsRestore ? "Backup öffnen" : "Backup starten";

    /// <summary>Where the backup is stored. Imagepixel Storage is the recommended choice and preselected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsImagepixel), nameof(IsLocal), nameof(DestinationHint), nameof(HasDestinationHint),
        nameof(ShowImagepixelFields), nameof(ShowLocalFields))]
    public partial StorageKind Kind { get; set; } = StorageKind.ImagepixelStorage;

    public bool IsImagepixel => Kind == StorageKind.ImagepixelStorage;

    public bool IsLocal => Kind == StorageKind.LocalFolder;

    // The fields of a storage only appear once the customer has reached that step.
    public bool ShowImagepixelFields => IsImagepixel && CanChooseDestination;

    public bool ShowLocalFields => IsLocal && CanChooseDestination;

    /// <summary>Shows the password while typing. It cannot be reset later, so typos matter.</summary>
    [ObservableProperty]
    public partial bool ShowPassword { get; set; }

    [ObservableProperty]
    public partial string SetupCodeText { get; set; } = "";

    [ObservableProperty]
    public partial string Bucket { get; set; } = "";

    [ObservableProperty]
    public partial string AccessKeyId { get; set; } = "";

    [ObservableProperty]
    public partial string SecretAccessKey { get; set; } = "";

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
    }

    // A pasted setup code fills in the fields the customer would otherwise type.
    partial void OnSetupCodeTextChanged(string value)
    {
        if (SetupCode.TryDecode(value, out var code))
        {
            Bucket = code!.Bucket;
            AccessKeyId = code.AccessKeyId;
            SecretAccessKey = code.SecretAccessKey;
            _region = code.Region;
            ErrorMessage = null;
        }
    }

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

        if (!Sources.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            Sources.Add(path);
        }

        ErrorMessage = null;
    }

    [RelayCommand]
    private void AddCloudSource(CloudFolder folder)
    {
        if (!Sources.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
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

        var storage = new StorageSettings
        {
            Kind = Kind,
            Bucket = Bucket.Trim(),
            Region = _region,
            AccessKeyId = AccessKeyId.Trim(),
            LocalPath = LocalPath.Trim(),
        };
        var secret = IsImagepixel ? SecretAccessKey.Trim() : null;

        IsBusy = true;
        BusyText = IsRestore ? "Backup wird geöffnet …" : "Verbindung wird geprüft …";
        try
        {
            var state = await _manager.ConnectAsync(storage, Password, secret, createIfMissing: false);
            if (state == RepositoryState.NotFound)
            {
                // Restoring never creates anything: an empty place means the data is elsewhere.
                if (IsRestore)
                {
                    ErrorMessage = "An diesem Ort wurde kein Backup gefunden. Bitte prüfe die Angaben. Wenn du noch kein Backup hast, wähle oben „Daten sichern“.";
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
                state = await _manager.ConnectAsync(storage, Password, secret, createIfMissing: true);
            }

            if (state == RepositoryState.WrongPassword)
            {
                ErrorMessage = IsRestore
                    ? "Das Verschlüsselungs-Passwort passt nicht zu diesem Backup."
                    : "An diesem Ort liegt bereits ein Backup, aber das Passwort passt nicht dazu.";
                return;
            }

            if (IsRestore)
            {
                // straight to the saved states, which is what the customer came for
                _main.ShowRestore();
                return;
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
            if (string.IsNullOrWhiteSpace(Bucket) || string.IsNullOrWhiteSpace(AccessKeyId) || string.IsNullOrWhiteSpace(SecretAccessKey))
            {
                return "Bitte trage Bucket-Name, Zugangsschlüssel und geheimen Schlüssel ein.";
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
