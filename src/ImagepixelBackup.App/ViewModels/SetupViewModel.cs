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

/// <summary>
/// First start. The customer first chooses what to back up, then where to, and
/// finally sets the password of the backup.
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

    /// <summary>Cloud storage found on this computer, offered as a source with one click.</summary>
    public IReadOnlyList<CloudFolder> CloudFolders => _main.CloudFolders;

    public bool HasCloudFolders => CloudFolders.Count > 0;

    /// <summary>Whether a chosen folder belongs to a cloud service, which calls for an explanation.</summary>
    public bool HasCloudSource => Sources.Any(source => CloudFolderDetector.Containing(source, CloudFolders) is not null);

    /// <summary>The folders the customer wants to back up.</summary>
    public ObservableCollection<string> Sources { get; } = [];

    public bool HasSources => Sources.Count > 0;

    /// <summary>
    /// Set when the customer only wants to reach an existing backup, for example to
    /// restore it on a new computer. No folder has to be chosen then.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(CanChooseDestination), nameof(NeedsSource), nameof(ConnectText),
        nameof(ShowImagepixelFields), nameof(ShowLocalFields))]
    public partial bool ExistingBackupOnly { get; set; }

    public bool NeedsSource => !ExistingBackupOnly;

    /// <summary>The destination can be chosen once it is clear what is backed up.</summary>
    public bool CanChooseDestination => HasSources || ExistingBackupOnly;

    /// <summary>Where the backup goes. Imagepixel Storage is the recommended choice and preselected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsImagepixel), nameof(IsLocal), nameof(DestinationHint), nameof(HasDestinationHint),
        nameof(ShowImagepixelFields), nameof(ShowLocalFields))]
    public partial StorageKind Kind { get; set; } = StorageKind.ImagepixelStorage;

    public bool IsImagepixel => Kind == StorageKind.ImagepixelStorage;

    public bool IsLocal => Kind == StorageKind.LocalFolder;

    // The fields of a destination only appear once the customer has reached that step.
    public bool ShowImagepixelFields => IsImagepixel && CanChooseDestination;

    public bool ShowLocalFields => IsLocal && CanChooseDestination;

    /// <summary>Shows the password while typing. It cannot be reset later, so typos matter.</summary>
    [ObservableProperty]
    public partial bool ShowPassword { get; set; }

    public string ConnectText => ExistingBackupOnly ? "Verbinden" : "Backup starten";

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
    /// Null when the chosen folder is an ordinary one.
    /// </summary>
    public string? DestinationHint =>
        IsLocal && !string.IsNullOrWhiteSpace(LocalPath) && CloudOf(LocalPath) is { } cloud
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

        ExistingBackupOnly = false;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void AddCloudSource(CloudFolder folder)
    {
        if (!Sources.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
        {
            Sources.Add(folder.Path);
        }

        ExistingBackupOnly = false;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void RemoveSource(string path) => Sources.Remove(path);

    [RelayCommand]
    private void UseExistingBackup()
    {
        Sources.Clear();
        ExistingBackupOnly = true;
        ErrorMessage = null;
    }

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
        if (await _main.PickFolder("Ordner für das Backup auswählen") is { } path)
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
        BusyText = "Verbindung wird geprüft …";
        try
        {
            var state = await _manager.ConnectAsync(storage, Password, secret, createIfMissing: false);
            if (state == RepositoryState.NotFound)
            {
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
                ErrorMessage = "An diesem Ort liegt bereits ein Backup, aber das Passwort passt nicht dazu.";
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
                return "Bitte wähle einen Ordner für das Backup aus.";
            }

            // the backup must not lie inside a folder it backs up, nor around one
            var folders = Sources.Concat(_manager.Config.Folders.Select(f => f.Path));
            if (folders.FirstOrDefault(folder => FolderRules.Overlap(folder, LocalPath)) is { } overlapping)
            {
                return Texts.Overlap(overlapping, LocalPath.Trim());
            }
        }

        if (Password.Length < MinimumPasswordLength)
        {
            return $"Bitte wähle ein Verschlüsselungs-Passwort mit mindestens {MinimumPasswordLength} Zeichen.";
        }

        return null;
    }
}
