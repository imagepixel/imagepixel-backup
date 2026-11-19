using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImagepixelBackup.Core;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>
/// The page where the customer chooses the destination in the storage for the backups
/// of one folder. When it changes, they say what becomes of what was saved before: it
/// stays, or it is deleted after some days. Nothing changes until they save. The same
/// page comes before the first backup of a folder that is being added: the folder is
/// only added, and backed up, once the customer has saved its destination.
/// </summary>
public partial class DestinationViewModel : ViewModelBase
{
    private static readonly int[] Days = [7, 30, 90];

    private readonly MainViewModel _main;
    private readonly BackupManager _manager;
    private readonly FolderItemViewModel? _folder;
    private readonly string _path;
    private readonly string _name;
    private readonly string _current;

    /// <summary>The page for a folder that is backed up already.</summary>
    public DestinationViewModel(MainViewModel main, BackupManager manager, FolderItemViewModel folder)
        : this(main, manager, folder, folder.Path, folder.Name, manager.DestinationOf(folder.Folder))
    {
    }

    /// <summary>The page for a folder that is about to be added, before its first backup.</summary>
    public DestinationViewModel(MainViewModel main, BackupManager manager, string newFolder, string name)
        : this(main, manager, null, newFolder, name, manager.Config.Storage?.Folder ?? "")
    {
    }

    private DestinationViewModel(
        MainViewModel main, BackupManager manager, FolderItemViewModel? folder, string path, string name, string current)
    {
        _main = main;
        _manager = manager;
        _folder = folder;
        _path = path;
        _name = name;
        _current = current;
        Destination = _current;
        FoundBackups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFoundBackups));
        Search = LookForDestinationsAsync();
    }

    /// <summary>Whether the folder is about to be added: then nothing has been saved yet, and nothing is asked about old backups.</summary>
    public bool IsNew => _folder is null;

    public string Title => $"Ziel für „{_name}“";

    public string Path => _path;

    public string Note => IsNew ? Texts.NewFolderNote : Texts.DestinationNote;

    public string CurrentText => (IsNew ? "Vorschlag: " : "Bisher: ") + Texts.DestinationLabel(_manager.Config.Storage, _current);

    public string SaveText => IsNew ? "Speichern und sichern" : "Speichern";

    /// <summary>Whether to ask what becomes of the backups at the destination the folder leaves.</summary>
    public bool AsksAboutOldBackups => !IsNew && IsChange;

    /// <summary>The destination as the customer typed or picked it. Empty for directly in the storage.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(HasProblem), nameof(IsChange), nameof(AsksAboutOldBackups))]
    public partial string Destination { get; set; }

    /// <summary>What is wrong with the destination, or null. Said while typing.</summary>
    public string? Problem => Texts.FolderProblem(StorageFolder.Check(StorageFolder.Normalize(Destination)));

    public bool HasProblem => Problem is not null;

    /// <summary>Whether the destination differs from the one in use. Only then is the question about the old backups asked.</summary>
    public bool IsChange => StorageFolder.Normalize(Destination) != _current;

    public string OldBackupsQuestion =>
        Texts.OldBackupsQuestion(_name, Texts.DestinationPhrase(_manager.Config.Storage, _current));

    /// <summary>The destinations the storage holds already, offered for one click.</summary>
    public ObservableCollection<FoundBackup> FoundBackups { get; } = [];

    public bool HasFoundBackups => FoundBackups.Count > 0;

    /// <summary>The look into the storage that is under way, for tests to wait for.</summary>
    public Task Search { get; }

    // The question has no answer until the customer gives one: deleting is never chosen for them.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswered))]
    public partial bool KeepOld { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswered))]
    public partial bool DeleteOld { get; set; }

    public bool HasAnswered => KeepOld || DeleteOld;

    public IReadOnlyList<string> DayOptions { get; } = [.. Days.Select(days => $"{days} Tagen")];

    [ObservableProperty]
    public partial int DayIndex { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsBusy { get; set; }

    public bool CanEdit => !IsBusy;

    // a message about what was missing goes once the customer has seen to it
    partial void OnDestinationChanged(string value) => ErrorMessage = null;

    partial void OnKeepOldChanged(bool value) => ErrorMessage = null;

    partial void OnDeleteOldChanged(bool value) => ErrorMessage = null;

    private async Task LookForDestinationsAsync()
    {
        IReadOnlyList<string> found;
        try
        {
            found = await _main.FindBackupFolders(null, null);
        }
        catch (Exception)
        {
            // a help only: the destination can always be typed
            return;
        }

        foreach (var destination in found)
        {
            FoundBackups.Add(new FoundBackup(destination, Texts.DestinationLabel(_manager.Config.Storage, destination)));
        }
    }

    [RelayCommand]
    private void ChooseFoundBackup(FoundBackup backup)
    {
        Destination = backup.Folder;
        ErrorMessage = null;
    }

    /// <summary>Takes over the destination. The next backup of the folder goes there.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_folder is null)
        {
            await AddAsync();
            return;
        }

        if (!IsChange)
        {
            _main.ShowDashboard();
            return;
        }

        if (Problem is { } problem)
        {
            ErrorMessage = problem;
            return;
        }

        if (!HasAnswered)
        {
            ErrorMessage = Texts.OldBackupsUnanswered;
            return;
        }

        if (_folder.IsRunning)
        {
            ErrorMessage = Texts.DestinationWhileRunning;
            return;
        }

        var destination = StorageFolder.Normalize(Destination);
        var days = DeleteOld ? Days[Math.Clamp(DayIndex, 0, Days.Length - 1)] : (int?)null;
        var place = Texts.DestinationPhrase(_manager.Config.Storage, destination);

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await Task.Run(() => _manager.ChangeDestinationAsync(_folder.Folder, destination, days));
        }
        catch (DestinationPasswordException)
        {
            ErrorMessage = Texts.DestinationOtherPassword(place);
            return;
        }
        catch (Exception e)
        {
            ErrorMessage = Texts.Error(e);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var dashboard = _main.Dashboard;
        dashboard.RefreshDestinations();
        dashboard.StatusLine = Texts.DestinationChanged(_folder.Name, place, days);
        _main.ShowDashboard();

        // the folder is saved at its new destination right away, unless the customer starts its backups themselves
        if (_folder.Folder.Schedule != ScheduleKind.Manual && _folder.CanStart)
        {
            _ = _folder.BackupNowAsync();
        }
    }

    // A folder that is about to be added: it is added with its destination and backed up
    // right away, unless the customer starts its backups themselves.
    private async Task AddAsync()
    {
        if (Problem is { } problem)
        {
            ErrorMessage = problem;
            return;
        }

        var destination = StorageFolder.Normalize(Destination);
        var place = Texts.DestinationPhrase(_manager.Config.Storage, destination);

        IsBusy = true;
        ErrorMessage = null;
        BackupFolder folder;
        try
        {
            folder = await Task.Run(() => _manager.AddFolderAsync(_path, destination));
        }
        catch (DestinationPasswordException)
        {
            ErrorMessage = Texts.DestinationOtherPassword(place);
            return;
        }
        catch (Exception e)
        {
            ErrorMessage = Texts.Error(e);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var dashboard = _main.Dashboard;
        var item = dashboard.ShowAddedFolder(folder);
        dashboard.StatusLine = Texts.FolderAdded(item.Name, place);
        _main.ShowDashboard();

        if (folder.Schedule != ScheduleKind.Manual)
        {
            _ = item.BackupNowAsync();
        }
    }

    /// <summary>Goes back. A folder that was about to be added is not added.</summary>
    [RelayCommand]
    private void Cancel() => _main.ShowDashboard();
}
