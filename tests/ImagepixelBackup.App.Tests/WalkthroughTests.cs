using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ImagepixelBackup.App.ViewModels;
using ImagepixelBackup.App.Views;
using ImagepixelBackup.Core;
using ImagepixelBackup.Core.Restic;

[assembly: AvaloniaTestApplication(typeof(ImagepixelBackup.App.Tests.TestAppBuilder))]

namespace ImagepixelBackup.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        NativeLibraries.BindEarly();
        return Configure();
    }

    private static AppBuilder Configure() => AppBuilder.Configure<global::ImagepixelBackup.App.App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Walks through the application the way a customer would, without showing a window:
/// once to set up a backup, and once to get data back on a new computer. The tests
/// run the real restic program against folders in the temp directory and save a
/// picture of every screen to artifacts/screenshots.
/// </summary>
public sealed class WalkthroughTests : IDisposable
{
    private const string Password = "sehr-geheim-123";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-ui-" + Guid.NewGuid().ToString("N"));

    private string DataFolder => Path.Combine(_root, "Einstellungen");

    private string SourceFolder => Path.Combine(_root, "Dokumente");

    private string StorageFolder => Path.Combine(_root, "USB-Festplatte", "Backup");

    private string RestoreFolder => Path.Combine(_root, "Wiederhergestellt");

    private static bool CanMount => OperatingSystem.IsWindows()
        ? Directory.Exists(@"C:\Program Files (x86)\WinFsp")
        : OperatingSystem.IsLinux() && File.Exists("/dev/fuse");

    private const string ProgramOnDiskImage = "/Volumes/Imagepixel Backup/Imagepixel Backup.app/Contents/MacOS/ImagepixelBackup";

    private const string ProgramInApplications = "/Applications/Imagepixel Backup.app/Contents/MacOS/ImagepixelBackup";

    /// <summary>
    /// Starting at login as macOS has it, with its file in the temp folder: the tests
    /// must not change what starts at login on the computer they run on.
    /// </summary>
    private MacLoginItem LoginItem => new(Path.Combine(_root, "LaunchAgents"));

    /// <summary>Stands in for the cloud storage a customer has set up on the computer.</summary>
    private CloudFolder[] CloudStorage =>
    [
        new(CloudService.OneDrive, "OneDrive", Path.Combine(_root, "OneDrive")),
        new(CloudService.GoogleDrive, "Google Drive", Path.Combine(_root, "Google", "Meine Ablage")),
        new(CloudService.ICloudDrive, "iCloud Drive", Path.Combine(_root, "iCloudDrive")),
    ];

    /// <summary>What the application finds of it: nothing on Linux, where no cloud storage is offered.</summary>
    private CloudFolder[] FoundCloudStorage => CloudFolderDetector.IsSupported ? CloudStorage : [];

    private static bool OffersCloudStorage => CloudFolderDetector.IsSupported;

    public WalkthroughTests()
    {
        foreach (var cloud in CloudStorage)
        {
            Directory.CreateDirectory(cloud.Path);
            File.WriteAllText(Path.Combine(cloud.Path, "Urlaub.txt"), "aus der Cloud");
        }

        Directory.CreateDirectory(Path.Combine(SourceFolder, "Verträge 2026"));
        File.WriteAllText(Path.Combine(SourceFolder, "Brief.txt"), "Sehr geehrte Damen und Herren äöü");
        File.WriteAllText(Path.Combine(SourceFolder, "Verträge 2026", "Mietvertrag Büro.txt"), "Vertrag");

        var random = new byte[2 * 1024 * 1024];
        new Random(7).NextBytes(random);
        File.WriteAllBytes(Path.Combine(SourceFolder, "Fotos.zip"), random);
    }

    [AvaloniaFact]
    public async Task Customer_sets_up_a_backup_and_gets_the_data_back()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        var manager = new BackupManager(new ConfigStore(DataFolder), new MemorySecretStore(), restic!);
        var main = new MainViewModel(manager, FoundCloudStorage);
        var window = new MainWindow { DataContext = main };

        // answers of the folder dialog, and the folders the application wants to show
        string? chosenFolder = null;
        var opened = new List<string>();
        main.PickFolder = _ => Task.FromResult(chosenFolder);
        main.OpenInFileManager = opened.Add;
        if (main.LoginItem is not null)
        {
            main.LoginItem = LoginItem;
        }

        // what a look into the storage finds, and what it was asked with
        var lookedInto = new List<(string? Bucket, string? Secret)>();
        main.FindBackupFolders = (storage, secret) =>
        {
            lookedInto.Add((storage?.Bucket, secret));
            return Task.FromResult<IReadOnlyList<string>>(["", "Notebook"]);
        };

        window.Show();

        // --- once, before anything else: the notes on use and liability ---------------
        var terms = Assert.IsType<TermsViewModel>(main.CurrentPage);
        Assert.False(main.IsScheduleRunning);
        Assert.False(terms.ContinueCommand.CanExecute(null));
        Capture(window, "00a-nutzungshinweise");

        terms.OpenTermsCommand.Execute(null);
        Assert.Equal([AboutViewModel.TermsUrl], opened);
        opened.Clear();

        terms.Confirmed = true;
        Assert.True(terms.ContinueCommand.CanExecute(null));
        terms.ContinueCommand.Execute(null);
        Assert.Equal(Texts.TermsVersion, manager.Config.TermsAccepted);
        Assert.NotNull(manager.Config.TermsAcceptedAt);
        Assert.Equal(Texts.TermsVersion, new ConfigStore(DataFolder).Load().TermsAccepted);

        // --- then: back up or restore -------------------------------------------------
        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        Assert.False(setup.HasMode);
        Assert.False(setup.CanChooseDestination);
        Capture(window, "00-start-auswahl");

        setup.ChooseModeCommand.Execute("Backup");
        Assert.True(setup.IsBackup);
        Assert.Equal("2. Wohin soll gesichert werden?", setup.DestinationHeading);

        // --- step 1: what to back up ---------------------------------------------
        Assert.False(setup.CanChooseDestination);
        Assert.True(setup.IsImagepixel);

        // the fields of the destination only appear once a source has been chosen
        Assert.False(setup.ShowImagepixelFields);
        Capture(window, "01-einrichtung-start");

        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("zuerst aus, was gesichert werden soll", setup.ErrorMessage);

        // cloud storage found on the computer is one click away and comes with an
        // explanation; Linux offers none
        Assert.Equal(OffersCloudStorage, setup.HasCloudFolders);
        Assert.False(setup.HasCloudSource);
        if (OffersCloudStorage)
        {
            Assert.Equal(3, setup.CloudFolders.Count);
            setup.AddCloudSourceCommand.Execute(CloudStorage[0]);
            setup.AddCloudSourceCommand.Execute(CloudStorage[0]);
            Assert.Equal([CloudStorage[0].Path], setup.Sources);
            Assert.True(setup.HasCloudSource);
            Assert.True(setup.CanChooseDestination);
            Capture(window, "01b-einrichtung-cloud-speicher");
            setup.RemoveSourceCommand.Execute(CloudStorage[0].Path);
            Assert.False(setup.CanChooseDestination);
        }

        chosenFolder = SourceFolder;
        await setup.AddSourceCommand.ExecuteAsync(null);
        await setup.AddSourceCommand.ExecuteAsync(null);
        Assert.Equal([SourceFolder], setup.Sources);
        Assert.True(setup.CanChooseDestination);
        Capture(window, "02-einrichtung-quelle-gewaehlt");

        // --- step 2: where to ------------------------------------------------------
        // Imagepixel Storage is the recommended choice and already selected
        Assert.True(setup.IsImagepixel);
        setup.SetupCodeText = new SetupCode("kunde-4711", "ABCDEFGHIJKLMNOPQRST", "geheim", "eu-central-2").Encode();
        Assert.Equal("kunde-4711", setup.Bucket);
        Assert.Equal("ABCDEFGHIJKLMNOPQRST", setup.AccessKeyId);
        Assert.True(setup.ShowImagepixelFields);

        // with the code the storage is known: the backups it holds already are offered
        await setup.Search;
        Assert.Equal([("kunde-4711", "geheim")], lookedInto);
        Assert.Equal(["Ohne Ordner (direkt im Speicher)", "Notebook"], setup.FoundBackups.Select(backup => backup.Label));

        // the folder inside the storage is optional, and what cannot be one is said while typing
        Assert.Equal("", setup.Folder);
        Assert.False(setup.HasFolderProblem);
        setup.Folder = "Büro: Notebook";
        Assert.Contains("nur Buchstaben, Ziffern", setup.FolderProblem);
        setup.Folder = "imagepixel";
        Assert.Contains("verwendet der Speicher selbst", setup.FolderProblem);
        setup.Password = Password;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(setup.FolderProblem, setup.ErrorMessage);
        Assert.False(manager.IsConnected);
        Capture(window, "03a-einrichtung-ordner-im-speicher-unzulaessig");
        setup.Password = "";

        setup.ChooseFoundBackupCommand.Execute(setup.FoundBackups[1]);
        Assert.Equal("Notebook", setup.Folder);
        Assert.False(setup.HasFolderProblem);
        Assert.Null(setup.ErrorMessage);
        Capture(window, "03-einrichtung-imagepixel-storage");

        // the whole page: what comes from Imagepixel, and the password the customer chooses
        setup.ShowPassword = true;
        setup.Password = "Mein-eigenes-Passwort";
        window.Height = 1560;
        Capture(window, "03b-einrichtung-komplett");
        window.Height = 700;
        setup.ShowPassword = false;
        setup.Password = "";

        setup.ChooseKindCommand.Execute("LocalFolder");
        Assert.True(setup.IsLocal);
        Assert.False(setup.ShowImagepixelFields);
        Assert.True(setup.ShowLocalFields);
        setup.Password = Password;
        setup.PasswordRepeat = Password;

        // the backup must not be stored inside the folder it backs up
        setup.LocalPath = Path.Combine(SourceFolder, "Backup");
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("liegen ineinander", setup.ErrorMessage);
        Assert.False(manager.IsConnected);

        // a folder of a cloud service is allowed as a destination, with an explanation;
        // on Linux it is a folder like any other
        setup.LocalPath = Path.Combine(CloudStorage[0].Path, "Backup");
        Assert.Equal(OffersCloudStorage, setup.HasDestinationHint);
        if (OffersCloudStorage)
        {
            Assert.Contains("gehört zu OneDrive", setup.DestinationHint);
            Capture(window, "04-einrichtung-ziel-im-cloud-ordner");
        }

        chosenFolder = StorageFolder;
        await setup.BrowseCommand.ExecuteAsync(null);
        Assert.Equal(StorageFolder, setup.LocalPath);
        Assert.False(setup.HasDestinationHint);
        setup.PasswordRepeat = "";

        // --- step 3: password ------------------------------------------------------
        setup.Password = "kurz";
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("mindestens 8 Zeichen", setup.ErrorMessage);

        // a new backup needs the password twice
        setup.Password = Password;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("wiederhole das Passwort", setup.ErrorMessage);
        Assert.False(manager.IsConnected);
        Capture(window, "05-einrichtung-usb-passwort-wiederholen");

        setup.PasswordRepeat = Password;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);
        Assert.True(manager.IsConnected);

        // --- overview: the chosen folder is backed up right away -------------------
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        var folder = Assert.Single(dashboard.Folders);
        Assert.Equal(SourceFolder, folder.Path);
        await WaitUntilAsync(() => folder.Folder.LastSuccess is not null && !folder.IsRunning);
        Assert.False(folder.HasProblem, folder.StatusText);
        Assert.StartsWith("Zuletzt gesichert: heute", folder.StatusText);
        Assert.Contains("gesichert: 3 neue oder geänderte Dateien", dashboard.StatusLine);
        Capture(window, "06-uebersicht-ordner-gesichert");

        // --- macOS and Linux: the schedule needs the application to start at login -----
        Assert.Equal(!OperatingSystem.IsWindows(), dashboard.ShowStartAtLoginNote);
        var program = ProgramOnDiskImage;
        main.LoginItem = LoginItem;
        main.ExecutablePath = () => program;
        dashboard.RefreshStartAtLogin();
        Assert.True(dashboard.ShowStartAtLoginNote);
        Capture(window, "06a-uebersicht-autostart-hinweis");

        // opened from its disk image, the application would be gone at the next login
        dashboard.StartAtLoginCommand.Execute(null);
        Assert.Contains("in den Ordner „Programme“", dashboard.StatusLine);
        Assert.True(dashboard.ShowStartAtLoginNote);

        program = ProgramInApplications;
        dashboard.StartAtLoginCommand.Execute(null);
        Assert.Contains("startet jetzt bei jeder Anmeldung", dashboard.StatusLine);
        Assert.False(dashboard.ShowStartAtLoginNote);
        Assert.Contains(ProgramInApplications, File.ReadAllText(main.LoginItem.FilePath));

        // --- storage figures ---------------------------------------------------------
        // a local folder has no booked size, so nothing is shown
        Assert.False(dashboard.HasStorageStatus);

        // where the storage keeps a status file, the overview shows how much of the quota is used
        const long gb = 1024L * 1024 * 1024;
        StorageStatus? figures = new(100 * gb, 62 * gb, DateTimeOffset.Now.AddHours(-3), StorageStatusSource.Api);
        main.LoadStorageStatus = () => Task.FromResult<StorageStatus?>(figures);
        await dashboard.RefreshStorageStatusAsync();
        Assert.True(dashboard.HasStorageStatus);
        Assert.Equal("62 GB von 100 GB Kontingent belegt (62 %)", dashboard.StorageText);
        Assert.False(dashboard.HasStorageWarning);
        Capture(window, "06b-uebersicht-speicher");

        // from 80 % on the customer is warned; backups keep running, the quota is no limit
        figures = figures with { UsedBytes = 86 * gb };
        await dashboard.RefreshStorageStatusAsync();
        Assert.Contains("Kontingent ist zu 86 % belegt", dashboard.StorageWarning);
        Assert.Contains("laufen weiter", dashboard.StorageWarning);
        Capture(window, "06c-uebersicht-speicher-warnung");

        figures = figures with { UsedBytes = 104 * gb };
        await dashboard.RefreshStorageStatusAsync();
        Assert.Contains("ausgeschöpft (104 %)", dashboard.StorageWarning);
        Assert.Equal(100, dashboard.StoragePercent);

        // if the figures cannot be fetched, the last ones stay on the screen
        main.LoadStorageStatus = () => throw new HttpRequestException("offline");
        await dashboard.RefreshStorageStatusAsync();
        Assert.True(dashboard.HasStorageStatus);

        main.LoadStorageStatus = () => Task.FromResult<StorageStatus?>(null);
        await dashboard.RefreshStorageStatusAsync();
        Assert.False(dashboard.HasStorageStatus);

        // adding the same folder again does not create a second entry
        chosenFolder = SourceFolder;
        await dashboard.AddFolderCommand.ExecuteAsync(null);
        Assert.Single(dashboard.Folders);

        // the folder that holds the backup cannot be added as a source
        chosenFolder = Path.GetDirectoryName(StorageFolder);
        await dashboard.AddFolderCommand.ExecuteAsync(null);
        Assert.Single(dashboard.Folders);
        Assert.Contains("liegen ineinander", dashboard.StatusLine);

        // the schedule is saved as soon as it is changed
        folder.ScheduleIndex = (int)ScheduleKind.Daily;
        folder.DailyTime = new TimeSpan(18, 30, 0);
        var saved = Assert.Single(new ConfigStore(DataFolder).Load().Folders);
        Assert.Equal(ScheduleKind.Daily, saved.Schedule);
        Assert.Equal(new TimeOnly(18, 30), saved.DailyAt);
        Assert.Contains("nächste Sicherung", folder.StatusText);

        // what a running backup looks like: amounts, and after a moment the speed
        Assert.False(folder.CanShowDetails);
        folder.IsRunning = true;
        var began = new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.FromHours(1));
        const long mb = 1024 * 1024;
        folder.ShowProgress(new BackupProgress { TotalFiles = 3870 }, began);
        Assert.Equal("Dateien werden gezählt …", folder.ProgressText);

        folder.ShowProgress(Progress(1200 * mb, 1200, "Dokumente/Brief.txt"), began.AddSeconds(1));
        Assert.Equal("1,2 GB von 2,9 GB · 1200 von 3870 Dateien", folder.ProgressText);
        folder.ShowProgress(Progress(1218 * mb, 1204, "Dokumente/Fotos.zip"), began.AddSeconds(3));
        Assert.Equal("1,2 GB von 2,9 GB · 1204 von 3870 Dateien · 9 MB/s", folder.ProgressText);
        Assert.Equal(42, folder.ProgressPercent, 3);
        Capture(window, "07-uebersicht-sicherung-laeuft");

        // the details are offered while the backup runs and stay closed until asked for
        Assert.True(folder.CanShowDetails);
        Assert.False(folder.ShowDetails);
        Assert.Equal("Im Detail anzeigen", folder.DetailsButtonText);

        // the backups that ran before left their first and their last line
        Assert.Contains("gestartet", folder.DetailLines[0]);
        Assert.Contains("Fertig: ", folder.DetailLines[^1]);

        folder.ToggleDetailsCommand.Execute(null);
        Assert.Equal("Details ausblenden", folder.DetailsButtonText);
        for (var step = 0; step < 30; step++)
        {
            var file = $"Dokumente/Verträge 2026/Anlage {step + 1}.pdf";
            folder.ShowProgress(Progress((1220 + 9 * step) * mb, 1205 + step, file), began.AddSeconds(4 + step));
        }

        // only the last lines are kept, the newest at the bottom, each with its time
        Assert.Equal(FolderItemViewModel.MaxDetailLines, folder.DetailLines.Count);
        Assert.EndsWith(Path.Combine("Dokumente", "Verträge 2026", "Anlage 30.pdf"), folder.DetailLines[^1]);
        Assert.Matches(@"^\d\d:\d\d:\d\d  ", folder.DetailLines[^1]);
        Assert.Contains("9 MB/s", folder.ProgressText);

        // the same file is not listed twice in a row, and lines do not come faster than they can be read
        var lines = folder.DetailLines.ToList();
        folder.ShowProgress(Progress(1500 * mb, 1240, "Dokumente/Verträge 2026/Anlage 30.pdf"), began.AddSeconds(40));
        Assert.Equal(lines, folder.DetailLines);
        folder.ShowProgress(Progress(1509 * mb, 1241, "Dokumente/Brief.txt"), began.AddSeconds(41));
        folder.ShowProgress(Progress(1509 * mb, 1241, "Dokumente/Zu schnell.txt"), began.AddSeconds(41.05));
        Assert.EndsWith("Brief.txt", folder.DetailLines[^1]);
        Capture(window, "07a-uebersicht-details");

        // after the backup the details stay until they are closed
        folder.IsRunning = false;
        Assert.True(folder.CanShowDetails);
        folder.ToggleDetailsCommand.Execute(null);
        Assert.False(folder.CanShowDetails);

        // --- restore -----------------------------------------------------------------
        dashboard.RestoreCommand.Execute(null);
        var restore = Assert.IsType<RestoreViewModel>(main.CurrentPage);
        await WaitUntilAsync(() => !restore.IsLoading);
        Assert.False(restore.HasProblem, restore.Message);
        Assert.Equal(2, restore.Snapshots.Count);
        Assert.Equal("Dokumente", restore.Snapshots[0].FolderName);
        Assert.False(restore.CanRestore);

        restore.SelectedSnapshot = restore.Snapshots[0];
        chosenFolder = RestoreFolder;
        await restore.BrowseCommand.ExecuteAsync(null);
        Assert.True(restore.CanRestore);

        await restore.RestoreCommand.ExecuteAsync(null);
        Assert.True(restore.IsDone, restore.Message);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(SourceFolder, "Fotos.zip")),
            File.ReadAllBytes(Path.Combine(RestoreFolder, "Dokumente", "Fotos.zip")));
        Assert.Equal(
            "Vertrag",
            File.ReadAllText(Path.Combine(RestoreFolder, "Dokumente", "Verträge 2026", "Mietvertrag Büro.txt")));
        Capture(window, "08-wiederherstellen-fertig");

        restore.BackCommand.Execute(null);
        Assert.Same(dashboard, main.CurrentPage);

        // --- drive -------------------------------------------------------------------
        string? chosenLetter = null;
        if (dashboard.OffersDriveLetter)
        {
            // Windows: the letter the drive will get is suggested, and only free letters are offered
            // another program may have taken or freed a letter since the overview was opened
            dashboard.RefreshDriveLetters();
            var free = MountSession.FreeDriveLetters();
            Assert.True(free.Count > 1, "the test needs two free drive letters");
            Assert.Equal(free[0], dashboard.SelectedDriveLetter);
            Assert.Equal(free.Order().ToList(), dashboard.DriveLetters.ToList());
            Assert.DoesNotContain(Path.GetPathRoot(Environment.SystemDirectory)![..2], dashboard.DriveLetters);
            Assert.True(dashboard.CanMount);

            // a suggestion is not remembered, a choice is
            Assert.Null(manager.Config.DriveLetter);
            Capture(window, "09a-uebersicht-laufwerk-buchstabe");
            chosenLetter = free[^1];
            dashboard.SelectedDriveLetter = chosenLetter;
            Assert.Equal(chosenLetter, new ConfigStore(DataFolder).Load().DriveLetter);

            // something else took the letter: another one is suggested for now, theirs comes back later
            dashboard.FindFreeDriveLetters = () => [.. free.Where(letter => letter != chosenLetter)];
            dashboard.RefreshDriveLetters();
            Assert.Equal(free[0], dashboard.SelectedDriveLetter);
            Assert.DoesNotContain(chosenLetter, dashboard.DriveLetters);
            Assert.Equal(chosenLetter, manager.Config.DriveLetter);

            // no letter free at all: the drive cannot be opened
            dashboard.FindFreeDriveLetters = () => [];
            dashboard.RefreshDriveLetters();
            Assert.True(dashboard.HasNoDriveLetter);
            Assert.False(dashboard.CanMount);

            dashboard.FindFreeDriveLetters = MountSession.FreeDriveLetters;
            dashboard.RefreshDriveLetters();
            Assert.Equal(chosenLetter, dashboard.SelectedDriveLetter);
            Assert.False(dashboard.HasNoDriveLetter);
            Assert.True(dashboard.CanMount);
        }

        if (CanMount)
        {
            await dashboard.MountCommand.ExecuteAsync(null);
            Assert.True(dashboard.IsMounted, dashboard.StatusLine);
            var drive = Assert.Single(opened);
            if (chosenLetter is not null)
            {
                // the drive got the letter that was shown
                Assert.StartsWith(chosenLetter, drive);
                Assert.Contains(chosenLetter, dashboard.MountText);
            }

            Capture(window, "09-uebersicht-laufwerk-geoeffnet");

            var snapshot = SnapshotFolders(drive).Last();
            var file = Path.Combine(snapshot, "Dokumente", "Brief.txt");
            Assert.Equal("Sehr geehrte Damen und Herren äöü", File.ReadAllText(file));
            Assert.ThrowsAny<Exception>(() => File.Delete(file));
            Assert.True(File.Exists(file));

            await dashboard.UnmountCommand.ExecuteAsync(null);
            Assert.False(dashboard.IsMounted);
            Assert.False(Directory.Exists(drive));
        }

        // --- a second start finds everything again ------------------------------------
        await main.ShutdownAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(StorageFolder, "locks")));
        Assert.Single(new ConfigStore(DataFolder).Load().Folders);
        Assert.Equal(StorageKind.LocalFolder, new ConfigStore(DataFolder).Load().Storage!.Kind);
        Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(DataFolder, "config.json")));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Customer_gets_the_data_back_on_a_new_computer()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        // the backup that was made on the old computer
        var old = new ResticClient(restic!, new RepositoryAccess(StorageFolder, Password));
        await old.InitAsync();
        await old.BackupAsync(SourceFolder);

        // the new computer: nothing has been set up
        var manager = new BackupManager(new ConfigStore(DataFolder), new MemorySecretStore(), restic!);
        var main = new MainViewModel(manager, FoundCloudStorage);
        var window = new MainWindow { DataContext = main };
        string? chosenFolder = null;
        var opened = new List<string>();
        main.PickFolder = _ => Task.FromResult(chosenFolder);
        main.OpenInFileManager = opened.Add;
        if (main.LoginItem is not null)
        {
            main.LoginItem = LoginItem;
        }

        window.Show();

        // --- a new computer asks to confirm the notes on use and liability, too --------
        var terms = Assert.IsType<TermsViewModel>(main.CurrentPage);
        terms.Confirmed = true;
        terms.ContinueCommand.Execute(null);

        // --- restoring is a choice of its own on the first screen ---------------------
        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        setup.ChooseModeCommand.Execute("Restore");
        Assert.True(setup.IsRestore);
        Assert.True(setup.CanChooseDestination);
        Assert.Equal("1. Wo liegt dein Backup?", setup.DestinationHeading);
        Assert.Equal("2. Dein Verschlüsselungs-Passwort", setup.PasswordHeading);
        Assert.Equal("Backup öffnen", setup.ConnectText);
        window.Height = 1100;
        Capture(window, "20-wiederherstellen-start");
        window.Height = 700;

        setup.ChooseKindCommand.Execute("LocalFolder");
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("in dem dein Backup liegt", setup.ErrorMessage);

        // a place without a backup is reported and not turned into a new backup
        var empty = Path.Combine(_root, "Leer");
        setup.LocalPath = empty;
        setup.Password = Password;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("kein Backup gefunden", setup.ErrorMessage);
        Assert.False(Directory.Exists(empty));
        Assert.False(manager.IsConnected);

        chosenFolder = StorageFolder;
        await setup.BrowseCommand.ExecuteAsync(null);
        Assert.Equal(StorageFolder, setup.LocalPath);
        Assert.False(setup.HasDestinationHint);

        setup.Password = "ein-anderes-passwort";
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("passt nicht zu diesem Backup", setup.ErrorMessage);
        Assert.False(manager.IsConnected);
        Assert.IsType<SetupViewModel>(main.CurrentPage);
        window.Height = 900;
        Capture(window, "21-wiederherstellen-falsches-passwort");
        window.Height = 700;

        // the right password is asked for once and leads straight to the saved states
        setup.Password = Password;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);
        Assert.True(manager.IsConnected);

        var restore = Assert.IsType<RestoreViewModel>(main.CurrentPage);
        await WaitUntilAsync(() => !restore.IsLoading);
        Assert.False(restore.HasProblem, restore.Message);
        var state = Assert.Single(restore.Snapshots);
        Assert.Equal("Dokumente", state.FolderName);
        Capture(window, "22-wiederherstellen-staende");

        restore.SelectedSnapshot = state;
        chosenFolder = RestoreFolder;
        await restore.BrowseCommand.ExecuteAsync(null);
        await restore.RestoreCommand.ExecuteAsync(null);
        Assert.True(restore.IsDone, restore.Message);
        Assert.Equal(
            "Sehr geehrte Damen und Herren äöü",
            File.ReadAllText(Path.Combine(RestoreFolder, "Dokumente", "Brief.txt")));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(SourceFolder, "Fotos.zip")),
            File.ReadAllBytes(Path.Combine(RestoreFolder, "Dokumente", "Fotos.zip")));

        // single files can be picked from a drive, opened right from this page
        if (CanMount)
        {
            await restore.OpenAsDriveCommand.ExecuteAsync(null);
            Assert.False(restore.HasProblem, restore.Message);
            Assert.Contains("als Laufwerk", restore.Message);
            var drive = Assert.Single(opened);
            Assert.Single(SnapshotFolders(drive));
            Capture(window, "23-wiederherstellen-laufwerk");
        }

        // --- from here the customer can go on to back up the new computer --------------
        restore.BackCommand.Execute(null);
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.True(dashboard.HasNoFolders);
        Assert.Equal(CanMount, dashboard.IsMounted);
        Assert.Equal(OffersCloudStorage ? 3 : 0, dashboard.AvailableCloudFolders.Count);
        Assert.Equal(OffersCloudStorage, dashboard.HasAvailableCloudFolders);
        Capture(window, "10-uebersicht-ohne-ordner");

        if (OffersCloudStorage)
        {
            // a cloud folder is added with one click and its destination, backed up and explained
            await dashboard.AddCloudFolderCommand.ExecuteAsync(CloudStorage[2]);
            var cloudPage = Assert.IsType<DestinationViewModel>(main.CurrentPage);
            Assert.Equal("Ziel für „iCloud Drive“", cloudPage.Title);
            await cloudPage.SaveCommand.ExecuteAsync(null);
            var cloudFolder = Assert.Single(dashboard.Folders);
            await WaitUntilAsync(() => cloudFolder.Folder.LastSuccess is not null || cloudFolder.HasProblem);
            Assert.Equal(CloudStorage[2].Path, cloudFolder.Path);
            Assert.Equal("iCloud Drive", cloudFolder.Name);
            Assert.True(cloudFolder.IsCloudFolder);
            Assert.False(cloudFolder.HasProblem, cloudFolder.StatusText);
            Assert.NotNull(cloudFolder.Folder.LastSuccess);
            Assert.Equal(["OneDrive", "Google Drive"], dashboard.AvailableCloudFolders.Select(c => c.Name));
            Capture(window, "10b-uebersicht-cloud-ordner");

            cloudFolder.RemoveCommand.Execute(null);
            Assert.Equal(3, dashboard.AvailableCloudFolders.Count);
        }

        // the info page names the components and returns to where it was opened
        dashboard.ShowAboutCommand.Execute(null);
        var about = Assert.IsType<AboutViewModel>(main.CurrentPage);
        Assert.StartsWith("Version 0.", about.Version);

        // no system gets a version that calls itself a beta
        Assert.DoesNotContain("Beta", about.Version);
        Assert.Equal("Imagepixel Backup", window.Title);
        Capture(window, "11-info");
        about.OpenLinkCommand.Execute(AboutViewModel.WinFspUrl);
        Assert.Equal(AboutViewModel.WinFspUrl, opened.Last());

        // the notes the customer confirmed at the first start can be read again here
        about.OpenLinkCommand.Execute(AboutViewModel.TermsUrl);
        Assert.Equal(AboutViewModel.TermsUrl, opened.Last());
        about.BackCommand.Execute(null);
        Assert.Same(dashboard, main.CurrentPage);

        // --- macOS: the info page turns starting at login on and off -------------------
        var program = ProgramOnDiskImage;
        main.LoginItem = LoginItem;
        main.ExecutablePath = () => program;
        dashboard.ShowAboutCommand.Execute(null);
        about = Assert.IsType<AboutViewModel>(main.CurrentPage);
        Assert.True(about.ShowStartAtLogin);
        Assert.False(about.StartAtLogin);

        // not from the disk image: the box is cleared again and says why
        about.StartAtLogin = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(about.StartAtLogin);
        Assert.Contains("in den Ordner „Programme“", about.StartAtLoginProblem);
        Assert.False(main.LoginItem.IsEnabled);
        Capture(window, "12-info-autostart-nicht-moeglich");

        program = ProgramInApplications;
        about.StartAtLogin = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(about.StartAtLogin);
        Assert.Null(about.StartAtLoginProblem);
        Assert.True(main.LoginItem.IsEnabled);
        Capture(window, "13-info-autostart");
        about.BackCommand.Execute(null);
        Assert.False(dashboard.ShowStartAtLoginNote);

        dashboard.ShowAboutCommand.Execute(null);
        about = Assert.IsType<AboutViewModel>(main.CurrentPage);
        Assert.True(about.StartAtLogin);
        about.StartAtLogin = false;
        Assert.False(main.LoginItem.IsEnabled);
        about.BackCommand.Execute(null);
        Assert.True(dashboard.ShowStartAtLoginNote);

        // leaving the application unmounts the drive and releases the lock
        await main.ShutdownAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(StorageFolder, "locks")));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Paused_backup_waits_for_the_customer_also_after_a_restart()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        // a backup that is set up, with one folder that is only backed up when asked
        var secrets = new MemorySecretStore();
        var manager = new BackupManager(new ConfigStore(DataFolder), secrets, restic!);
        manager.AcceptTerms(Texts.TermsVersion);
        await manager.ConnectAsync(
            new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = StorageFolder }, Password, null, createIfMissing: true);
        manager.AddFolder(SourceFolder).Schedule = ScheduleKind.Hourly;
        manager.Save();

        var main = new MainViewModel(manager, []);
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        var folder = Assert.Single(dashboard.Folders);
        Assert.True(folder.CanStart);
        Assert.False(folder.CanResume);

        // pausing right after the start: the run ends and the folder waits
        var run = folder.BackupNowCommand.ExecuteAsync(null);
        Assert.True(folder.IsRunning);
        folder.ShowProgress(new BackupProgress { TotalBytes = 2 * 1024 * 1024, BytesDone = 1024 * 1024, TotalFiles = 3, FilesDone = 1 }, DateTimeOffset.Now);
        folder.PauseCommand.Execute(null);
        await run;

        Assert.False(folder.IsRunning);
        Assert.True(folder.IsPaused);
        Assert.True(folder.CanResume);
        Assert.False(folder.CanStart);
        Assert.StartsWith("Pausiert seit heute, ", folder.StatusText);
        Assert.Contains("1 MB von 2 MB waren geschafft", folder.StatusText);
        Assert.Contains("pausiert", dashboard.StatusLine);
        Assert.Null(folder.Folder.LastSuccess);

        // the schedule leaves the folder alone
        Assert.False(BackupSchedule.IsDue(folder.Folder, DateTimeOffset.Now.AddDays(3)));
        await dashboard.RunDueBackupsAsync();
        Assert.Null(folder.Folder.LastSuccess);
        await main.ShutdownAsync();

        // after a restart, as after an update, the settings are as they were and the folder still waits
        var restarted = new MainViewModel(new BackupManager(new ConfigStore(DataFolder), secrets, restic!), []);
        var again = Assert.Single(Assert.IsType<DashboardViewModel>(restarted.CurrentPage).Folders);
        Assert.Equal(SourceFolder, again.Path);
        Assert.Equal((int)ScheduleKind.Hourly, again.ScheduleIndex);
        Assert.True(again.IsPaused);
        Assert.True(again.CanResume);

        var window = new MainWindow { DataContext = restarted };
        window.Show();
        Capture(window, "07b-uebersicht-pausiert");

        // going on backs the folder up at once and ends the pause
        await again.ResumeCommand.ExecuteAsync(null);
        Assert.False(again.IsPaused);
        Assert.NotNull(again.Folder.LastSuccess);
        Assert.StartsWith("Zuletzt gesichert: heute", again.StatusText);
        Assert.Null(new ConfigStore(DataFolder).Load().Folders[0].PausedAt);

        // cancelling, unlike pausing, only ends the run
        var second = again.BackupNowCommand.ExecuteAsync(null);
        again.CancelCommand.Execute(null);
        await second;
        Assert.False(again.IsPaused);
        Assert.Contains("abgebrochen", ((DashboardViewModel)restarted.CurrentPage).StatusLine);

        window.Close();
        await restarted.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task Changing_the_storage_forgets_nothing_until_another_one_is_connected()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        var secrets = new MemorySecretStore();
        var manager = new BackupManager(new ConfigStore(DataFolder), secrets, restic!);
        manager.AcceptTerms(Texts.TermsVersion);
        await manager.ConnectAsync(
            new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = StorageFolder }, Password, null, createIfMissing: true);
        manager.AddFolder(SourceFolder).Schedule = ScheduleKind.Manual;
        manager.Save();

        var main = new MainViewModel(manager, []);
        var window = new MainWindow { DataContext = main };
        window.Show();
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);

        // "Einrichtungscode oder Speicher ändern" only opens the setup
        dashboard.ChangeStorageCommand.Execute(null);
        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        Assert.True(setup.IsSwitching);
        Assert.Equal(Texts.ChangeStorage, setup.Title);
        Assert.Contains("Du bist mit", setup.SwitchNote);
        Assert.Contains("Sicherungen laufen weiter", setup.SwitchNote);
        Assert.Equal([SourceFolder], setup.Sources);
        Assert.True(setup.ShowKeptFoldersNote);

        Assert.Empty(setup.FoundBackups);
        Capture(window, "14-speicher-wechseln");

        // nothing was forgotten and nothing was stopped
        Assert.True(manager.IsConnected);
        Assert.Equal(StorageFolder, new ConfigStore(DataFolder).Load().Storage!.LocalPath);
        Assert.Equal(Password, secrets.Get(SecretNames.RepositoryPassword));
        Assert.True(main.IsScheduleRunning);

        // and there is a way back
        setup.BackCommand.Execute(null);
        Assert.Same(dashboard, main.CurrentPage);

        // an attempt that does not work out changes nothing either
        dashboard.ChangeStorageCommand.Execute(null);
        setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        setup.ChooseModeCommand.Execute("Backup");
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(Texts.SetupCodeMissing, setup.ErrorMessage);

        setup.SetupCodeText = "IPB1-kaputt";
        Assert.True(setup.HasCodeProblem);
        Assert.False(setup.CodeAccepted);
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(Texts.SetupCodeProblem, setup.ErrorMessage);
        Assert.Equal(StorageFolder, manager.Config.Storage!.LocalPath);
        Assert.Equal(Password, secrets.Get(SecretNames.RepositoryPassword));

        // moving for real: another place and another password, said before it is done
        var other = Path.Combine(_root, "Zweite Festplatte", "Backup");
        setup.ChooseKindCommand.Execute("LocalFolder");
        setup.LocalPath = other;
        Assert.True(setup.IsOtherStorage);
        Assert.Equal(Texts.OtherStorageNote, setup.StorageChangeNote);
        Assert.Equal("Zum neuen Speicher wechseln", setup.ConnectText);
        setup.Password = "ein-anderes-passwort";
        setup.PasswordRepeat = "ein-anderes-passwort";
        window.Height = 1500;
        Capture(window, "14a-anderer-speicher");
        window.Height = 700;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);

        var next = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.NotSame(dashboard, next);
        Assert.Equal(other, new ConfigStore(DataFolder).Load().Storage!.LocalPath);
        Assert.Equal("ein-anderes-passwort", secrets.Get(SecretNames.RepositoryPassword));
        Assert.Equal(SourceFolder, Assert.Single(next.Folders).Path);

        // the backup in the previous storage is still there
        Assert.True(File.Exists(Path.Combine(StorageFolder, "config")));

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task Each_folder_has_a_destination_of_its_own()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        // two folders on a USB disk, both saved directly into the folder of the backup
        var photosFolder = Path.Combine(_root, "Fotos");
        Directory.CreateDirectory(photosFolder);
        File.WriteAllText(Path.Combine(photosFolder, "Urlaub.jpg"), "ein Bild");
        var manager = new BackupManager(new ConfigStore(DataFolder), new MemorySecretStore(), restic!);
        manager.AcceptTerms(Texts.TermsVersion);
        await manager.ConnectAsync(
            new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = StorageFolder }, Password, null, createIfMissing: true);
        var documents = manager.AddFolder(SourceFolder);
        var photos = manager.AddFolder(photosFolder);
        documents.Schedule = photos.Schedule = ScheduleKind.Manual;
        documents.ExclusionsDecided = photos.ExclusionsDecided = true;
        manager.Save();
        await manager.BackupAsync(documents);
        await manager.BackupAsync(photos);

        var main = new MainViewModel(manager, []);
        main.LoadStorageStatus = () => Task.FromResult<StorageStatus?>(null);
        var window = new MainWindow { DataContext = main };
        window.Show();
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        var item = dashboard.Folders.Single(folder => folder.Path == SourceFolder);
        Assert.Equal("Ziel: direkt im Backup-Ordner", item.DestinationText);
        Assert.False(item.HasCleanup);
        Assert.False(dashboard.HasMountDestinations);

        // the page of the destination shows what the storage holds
        item.ChangeDestinationCommand.Execute(null);
        var page = Assert.IsType<DestinationViewModel>(main.CurrentPage);
        await page.Search;
        Assert.Equal(["Ohne Ordner (direkt im Backup-Ordner)"], page.FoundBackups.Select(backup => backup.Label));
        Assert.Equal("Bisher: Ohne Ordner (direkt im Backup-Ordner)", page.CurrentText);
        Assert.False(page.IsChange);
        Capture(window, "18-ziel-eines-ordners");

        // a name that cannot be a destination is said while typing
        page.Destination = "Büro?";
        Assert.True(page.HasProblem);
        await page.SaveCommand.ExecuteAsync(null);
        Assert.Contains("nur Buchstaben", page.ErrorMessage);

        // another destination asks what becomes of the old backups, and answers nothing for the customer
        page.Destination = "Dokumente Notebook";
        Assert.True(page.IsChange);
        Assert.False(page.HasAnswered);
        Assert.Contains("„Dokumente“ direkt im Backup-Ordner", page.OldBackupsQuestion);
        await page.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Texts.OldBackupsUnanswered, page.ErrorMessage);
        Assert.Null(documents.Destination);

        page.DeleteOld = true;
        Assert.Equal("30 Tagen", page.DayOptions[page.DayIndex]);
        window.Height = 1000;
        Capture(window, "18a-ziel-aendern-frage");
        window.Height = 700;
        await page.SaveCommand.ExecuteAsync(null);
        Assert.Null(page.ErrorMessage);
        Assert.Same(dashboard, main.CurrentPage);

        // the folder goes to its new destination, a backup of its own; the other folder stays where it was
        Assert.Equal("Dokumente Notebook", documents.Destination);
        Assert.Equal("Ziel: Ordner „Dokumente Notebook“", item.DestinationText);
        Assert.True(File.Exists(Path.Combine(StorageFolder, "Dokumente Notebook", "config")));
        Assert.Null(documents.LastSuccess);
        Assert.Equal("", manager.DestinationOf(photos));
        Assert.Contains("ab jetzt im Ordner „Dokumente Notebook“ gesichert", dashboard.StatusLine);

        // the old backups wait for their day, and can be kept after all
        var cleanup = Assert.Single(manager.Config.Cleanups);
        Assert.Equal("", cleanup.Destination);
        Assert.InRange(cleanup.DueAt, DateTimeOffset.Now.AddDays(29.9), DateTimeOffset.Now.AddDays(30.1));
        Assert.True(item.HasCleanup);
        Assert.Contains("direkt im Backup-Ordner werden ab dem", item.CleanupText);
        Assert.True(dashboard.HasMountDestinations);
        Capture(window, "18b-uebersicht-ziel-geaendert");

        item.KeepOldBackupsCommand.Execute(null);
        Assert.Empty(manager.Config.Cleanups);
        Assert.False(item.HasCleanup);
        Assert.Contains("bleiben erhalten", dashboard.StatusLine);

        // the next backup lands at the new destination
        await item.BackupNowCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !item.IsRunning);
        Assert.NotNull(documents.LastSuccess);
        Assert.Null(documents.LastError);

        // the restore page lists the states of both places and says where each lies
        dashboard.RestoreCommand.Execute(null);
        var restore = Assert.IsType<RestoreViewModel>(main.CurrentPage);
        await WaitUntilAsync(() => !restore.IsLoading);
        Assert.Equal(3, restore.Snapshots.Count);
        Assert.Equal("Dokumente Notebook", restore.Snapshots[0].Destination);
        Assert.All(restore.Snapshots.Skip(1), snapshot => Assert.Equal("Ohne Ordner (direkt im Backup-Ordner)", snapshot.Destination));
        Capture(window, "18c-wiederherstellen-zwei-ziele");

        // a state is restored from where it lies
        restore.SelectedSnapshot = restore.Snapshots[0];
        restore.TargetFolder = RestoreFolder;
        await restore.RestoreCommand.ExecuteAsync(null);
        Assert.True(restore.IsDone, restore.Message);
        Assert.True(File.Exists(Path.Combine(RestoreFolder, "Dokumente", "Brief.txt")));
        restore.BackCommand.Execute(null);

        // moving on once more, with the old backups deleted when their day has come
        item.ChangeDestinationCommand.Execute(null);
        page = Assert.IsType<DestinationViewModel>(main.CurrentPage);
        page.Destination = "Archiv";
        page.DeleteOld = true;
        await page.SaveCommand.ExecuteAsync(null);
        var pending = Assert.Single(manager.Config.Cleanups);
        Assert.Equal("Dokumente Notebook", pending.Destination);
        pending.DueAt = DateTimeOffset.Now.AddMinutes(-1);

        await dashboard.RunDueCleanupsAsync();
        Assert.Empty(manager.Config.Cleanups);
        Assert.Contains("„Dokumente“ im Ordner „Dokumente Notebook“ wurde gelöscht. Dort liegt jetzt nichts mehr.", dashboard.StatusLine);
        Assert.False(Directory.Exists(Path.Combine(StorageFolder, "Dokumente Notebook")));
        Assert.False(item.HasCleanup);

        // the backups at the first place are untouched
        Assert.Equal(2, (await new ResticClient(restic!, new RepositoryAccess(StorageFolder, Password)).GetSnapshotsAsync()).Count);

        window.Close();
        await main.ShutdownAsync();
    }

    /// <summary>
    /// The same with a real storage behind it: another destination for a folder, a backup
    /// there, and the old backups deleted. Runs only where a bucket for tests is named in
    /// the environment (IPB_TEST_BUCKET, IPB_TEST_ACCESS_KEY_ID,
    /// IPB_TEST_SECRET_ACCESS_KEY, IPB_TEST_REGION). It keeps a small backup in the folder
    /// "Test Büro Notebook" and removes the destination it makes for itself.
    /// </summary>
    [AvaloniaFact]
    public async Task Customer_changes_the_destination_of_a_folder_in_a_real_storage()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        var bucket = Environment.GetEnvironmentVariable("IPB_TEST_BUCKET");
        var keyId = Environment.GetEnvironmentVariable("IPB_TEST_ACCESS_KEY_ID");
        var secret = Environment.GetEnvironmentVariable("IPB_TEST_SECRET_ACCESS_KEY");
        Assert.SkipWhen(
            string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(secret),
            "no storage for tests is named in the environment");

        const string password = "nur-ein-test-passwort";
        const string notebook = "Test Büro Notebook";
        var fresh = "Test Ziel " + Guid.NewGuid().ToString("N")[..8];
        var storage = new StorageSettings
        {
            Kind = StorageKind.ImagepixelStorage,
            Bucket = bucket!,
            AccessKeyId = keyId!,
            Region = Environment.GetEnvironmentVariable("IPB_TEST_REGION") is { Length: > 0 } region ? region : ImagepixelStorage.DefaultRegion,
            Folder = notebook,
        };

        var manager = new BackupManager(new ConfigStore(DataFolder), new MemorySecretStore(), restic!);
        manager.AcceptTerms(Texts.TermsVersion);
        Assert.Equal(RepositoryState.Ready, await manager.ConnectAsync(storage, password, secret, createIfMissing: true));
        var folder = manager.AddFolder(SourceFolder);
        folder.Schedule = ScheduleKind.Manual;
        folder.ExclusionsDecided = true;
        manager.Save();
        await manager.BackupAsync(folder);

        var main = new MainViewModel(manager, []);
        main.LoadStorageStatus = () => Task.FromResult<StorageStatus?>(null);
        var window = new MainWindow { DataContext = main };
        window.Show();
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        var item = Assert.Single(dashboard.Folders);
        Assert.Equal($"Ziel: Ordner „{notebook}“", item.DestinationText);

        // the storage is looked into for real
        item.ChangeDestinationCommand.Execute(null);
        var page = Assert.IsType<DestinationViewModel>(main.CurrentPage);
        await page.Search;
        Assert.Contains(page.FoundBackups, backup => backup.Folder == notebook);

        page.Destination = fresh;
        page.DeleteOld = true;
        await page.SaveCommand.ExecuteAsync(null);
        Assert.Null(page.ErrorMessage);
        Assert.Equal(fresh, folder.Destination);

        await item.BackupNowCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !item.IsRunning);
        Assert.Null(folder.LastError);
        Assert.NotNull(folder.LastSuccess);
        var all = await manager.GetSnapshotsAsync();
        Assert.Single(all, snapshot => snapshot.Destination == fresh);
        Assert.Contains(all, snapshot => snapshot.Destination == notebook && snapshot.Paths.Any(path => FolderRules.IsSameFolder(path, SourceFolder)));

        // the old backup of this folder is deleted when its day has come
        Assert.Single(manager.Config.Cleanups).DueAt = DateTimeOffset.Now.AddMinutes(-1);
        await dashboard.RunDueCleanupsAsync();
        Assert.Empty(manager.Config.Cleanups);
        Assert.Contains("wurde gelöscht", dashboard.StatusLine);
        all = await manager.GetSnapshotsAsync();
        Assert.DoesNotContain(all, snapshot => snapshot.Destination == notebook && snapshot.Paths.Any(path => FolderRules.IsSameFolder(path, SourceFolder)));

        // going back removes the destination of this run, so that the test leaves nothing behind
        await manager.ChangeDestinationAsync(folder, notebook, deleteOldAfterDays: 0);
        await dashboard.RunDueCleanupsAsync();
        Assert.Contains("Dort liegt jetzt nichts mehr", dashboard.StatusLine);
        var left = new List<string>();
        await foreach (var (key, _) in S3Client.For(new HttpClient(), storage, secret!).ListObjectsAsync(fresh + "/"))
        {
            left.Add(key);
        }

        Assert.Empty(left);

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task The_encrypted_files_of_a_backup_on_a_disk_are_shown_in_the_file_manager()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        var manager = new BackupManager(new ConfigStore(DataFolder), new MemorySecretStore(), restic!);
        manager.AcceptTerms(Texts.TermsVersion);
        await manager.ConnectAsync(
            new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = StorageFolder }, Password, null, createIfMissing: true);
        var folder = manager.AddFolder(SourceFolder);
        folder.Schedule = ScheduleKind.Manual;
        manager.Save();

        var main = new MainViewModel(manager, []);
        main.LoadStorageStatus = () => Task.FromResult<StorageStatus?>(null);
        var opened = new List<string>();
        main.OpenInFileManager = opened.Add;
        var window = new MainWindow { DataContext = main };
        window.Show();
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);

        // offered where the computer shows drives; a folder on a disk needs no rclone
        Assert.Equal(BackupManager.IsMountSupported, dashboard.CanOpenEncrypted);
        Assert.SkipUnless(dashboard.CanOpenEncrypted, "this computer cannot show drives");
        Capture(window, "19-verschluesselt-oeffnen");

        await dashboard.OpenEncryptedCommand.ExecuteAsync(null);
        Assert.Equal([StorageFolder], opened);
        Assert.Contains("verschlüsselt, mit Prüfsummen als Namen", dashboard.StatusLine);
        Assert.False(dashboard.IsMounted);

        // a destination of its own lies in a folder of its own, and the choice of the drive leads there
        await manager.ChangeDestinationAsync(folder, "Notebook", deleteOldAfterDays: null);
        dashboard.RefreshDestinations();
        Assert.True(dashboard.HasMountDestinations);
        dashboard.SelectedMountDestination = dashboard.MountDestinations.Single(option => option.Folder == "Notebook");
        await dashboard.OpenEncryptedCommand.ExecuteAsync(null);
        Assert.Equal(Path.Combine(StorageFolder, "Notebook"), opened[^1]);

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task Renewing_the_access_data_of_the_same_storage_keeps_everything()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        var photosFolder = Path.Combine(_root, "Fotos");
        Directory.CreateDirectory(photosFolder);
        File.WriteAllText(Path.Combine(photosFolder, "Urlaub.jpg"), "ein Bild");
        var secrets = new MemorySecretStore();
        var manager = new BackupManager(new ConfigStore(DataFolder), secrets, restic!);
        manager.AcceptTerms(Texts.TermsVersion);
        await manager.ConnectAsync(
            new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = StorageFolder }, Password, null, createIfMissing: true);
        var documents = manager.AddFolder(SourceFolder);
        var photos = manager.AddFolder(photosFolder);
        documents.Schedule = photos.Schedule = ScheduleKind.Manual;
        documents.ExclusionsDecided = photos.ExclusionsDecided = true;
        await manager.BackupAsync(documents);
        await manager.ChangeDestinationAsync(documents, "Notebook", deleteOldAfterDays: 30);
        await manager.BackupAsync(documents);
        var lastSuccess = documents.LastSuccess;

        var main = new MainViewModel(manager, []);
        main.LoadStorageStatus = () => Task.FromResult<StorageStatus?>(null);
        var window = new MainWindow { DataContext = main };
        window.Show();
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);

        // the way there leads through the info page now, with a word on what it is for
        dashboard.ShowAboutCommand.Execute(null);
        var about = Assert.IsType<AboutViewModel>(main.CurrentPage);
        Assert.True(about.CanChangeStorage);
        window.Height = 1300;
        Capture(window, "11a-info-einrichtungscode-aendern");
        window.Height = 700;
        about.ChangeStorageCommand.Execute(null);

        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        Assert.Equal(Texts.ChangeStorage, setup.Title);
        Assert.True(setup.IsBackup);
        Assert.True(setup.ShowKeptFoldersNote);
        Assert.False(setup.HasStorageChangeNote);

        // the same place: only the details of the storage are renewed
        setup.ChooseKindCommand.Execute("LocalFolder");
        setup.LocalPath = StorageFolder;
        Assert.True(setup.IsSameStorage);
        Assert.Equal(Texts.SameStorageNote, setup.StorageChangeNote);
        Assert.Equal("Speichern", setup.ConnectText);

        // a folder taken out of the list is no longer backed up
        setup.RemoveSourceCommand.Execute(photosFolder);
        setup.Password = Password;
        window.Height = 1500;
        Capture(window, "14b-gleicher-speicher");
        window.Height = 700;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);

        // everything else stays as it was: destination, last backup, the deletion that waits
        var next = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        var kept = Assert.Single(manager.Config.Folders);
        Assert.Same(documents, kept);
        Assert.Equal("Notebook", kept.Destination);
        Assert.Equal(lastSuccess, kept.LastSuccess);
        Assert.Single(manager.Config.Cleanups);
        Assert.Equal("Ziel: Ordner „Notebook“", Assert.Single(next.Folders).DestinationText);

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task Access_data_the_storage_turns_down_lead_to_a_new_setup_code()
    {
        // a computer whose last backup failed because the storage turned down its key
        new ConfigStore(DataFolder).Save(new AppConfig
        {
            Storage = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde-4711", AccessKeyId = "ALTERSCHLUESSEL" },
            TermsAccepted = Texts.TermsVersion,
            Folders =
            {
                new BackupFolder
                {
                    Path = SourceFolder,
                    Schedule = ScheduleKind.Manual,
                    LastAttempt = DateTimeOffset.Now,
                    LastError = "Fatal: unable to open config file: Stat: The AWS Access Key Id you provided does not exist in our records. InvalidAccessKeyId",
                },
            },
        });
        var secrets = new MemorySecretStore();
        secrets.Set(SecretNames.RepositoryPassword, Password);
        secrets.Set(SecretNames.SecretAccessKey, "alt");
        var main = new MainViewModel(new BackupManager(new ConfigStore(DataFolder), secrets, "restic"), []);
        main.LoadStorageStatus = () => Task.FromResult<StorageStatus?>(null);
        main.FindBackupFolders = (_, _) => Task.FromResult<IReadOnlyList<string>>([]);
        var window = new MainWindow { DataContext = main };
        window.Show();

        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.True(dashboard.HasAccessProblem);
        Capture(window, "14c-zugangsdaten-abgelehnt");

        dashboard.ChangeStorageCommand.Execute(null);
        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);

        // a new code of the same bucket renews the key only
        setup.SetupCodeText = new SetupCode("kunde-4711", "NEUERSCHLUESSEL", "neu", "eu-central-2").Encode();
        Assert.True(setup.IsSameStorage);
        Assert.Equal("Speichern", setup.ConnectText);

        // the code of another bucket moves, and says so before anything happens
        setup.SetupCodeText = new SetupCode("kunde-0815", "NEUERSCHLUESSEL", "neu", "eu-central-2").Encode();
        Assert.True(setup.IsOtherStorage);
        Assert.Equal(Texts.OtherStorageNote, setup.StorageChangeNote);
        Assert.Equal("Zum neuen Speicher wechseln", setup.ConnectText);

        // a folder that backs up without trouble raises no alarm
        Assert.False(Texts.IsAccessProblem("Fatal: unable to save snapshot: some file is locked"));
        Assert.False(Texts.IsAccessProblem(null));

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task A_new_folder_gets_its_destination_before_the_first_backup()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        var photosFolder = Path.Combine(_root, "Fotos");
        Directory.CreateDirectory(photosFolder);
        File.WriteAllText(Path.Combine(photosFolder, "Urlaub.jpg"), "ein Bild");
        var manager = new BackupManager(new ConfigStore(DataFolder), new MemorySecretStore(), restic!);
        manager.AcceptTerms(Texts.TermsVersion);
        await manager.ConnectAsync(
            new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = StorageFolder, Folder = "Notebook" },
            Password, null, createIfMissing: true);

        var main = new MainViewModel(manager, []);
        main.LoadStorageStatus = () => Task.FromResult<StorageStatus?>(null);
        var window = new MainWindow { DataContext = main };
        string? chosen = photosFolder;
        main.PickFolder = _ => Task.FromResult(chosen);
        window.Show();
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);

        // choosing a folder leads to its destination first; nothing is added or backed up yet
        await dashboard.AddFolderCommand.ExecuteAsync(null);
        var page = Assert.IsType<DestinationViewModel>(main.CurrentPage);
        Assert.True(page.IsNew);
        Assert.Equal("Ziel für „Fotos“", page.Title);
        Assert.Equal("Notebook", page.Destination);
        Assert.Equal("Vorschlag: Notebook", page.CurrentText);
        Assert.Equal("Speichern und sichern", page.SaveText);
        Assert.False(page.AsksAboutOldBackups);
        Assert.Empty(manager.Config.Folders);
        await page.Search;
        Capture(window, "20-neuer-ordner-ziel");

        // going back adds nothing
        page.CancelCommand.Execute(null);
        Assert.Same(dashboard, main.CurrentPage);
        Assert.Empty(manager.Config.Folders);
        Assert.True(dashboard.HasNoFolders);

        // another destination, and no question about old backups, since there are none
        await dashboard.AddFolderCommand.ExecuteAsync(null);
        page = Assert.IsType<DestinationViewModel>(main.CurrentPage);
        page.Destination = "Fotos";
        Assert.False(page.AsksAboutOldBackups);
        await page.SaveCommand.ExecuteAsync(null);
        Assert.Null(page.ErrorMessage);
        Assert.Same(dashboard, main.CurrentPage);

        // only now the folder is there, with its destination, and its first backup lands there
        var item = Assert.Single(dashboard.Folders);
        Assert.Equal("Fotos", item.Folder.Destination);
        Assert.Equal("Ziel: Ordner „Fotos“", item.DestinationText);
        Assert.Contains("„Fotos“ wird ab jetzt im Ordner „Fotos“ gesichert", dashboard.StatusLine);
        await WaitUntilAsync(() => item.Folder.LastSuccess is not null || item.HasProblem);
        Assert.False(item.HasProblem, item.StatusText);
        Assert.Single(await new ResticClient(restic!, new RepositoryAccess(Path.Combine(StorageFolder, "Fotos"), Password)).GetSnapshotsAsync());
        Assert.Empty(manager.Config.Cleanups);

        // choosing it once more backs it up again instead of asking for a destination
        await dashboard.AddFolderCommand.ExecuteAsync(null);
        Assert.Same(dashboard, main.CurrentPage);
        Assert.Single(dashboard.Folders);

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task Closing_the_window_or_quitting_during_a_backup_asks_first()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        var secrets = new MemorySecretStore();
        var manager = new BackupManager(new ConfigStore(DataFolder), secrets, restic!);
        manager.AcceptTerms(Texts.TermsVersion);
        await manager.ConnectAsync(
            new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = StorageFolder }, Password, null, createIfMissing: true);
        manager.AddFolder(SourceFolder);

        var main = new MainViewModel(manager, []);
        var hidden = 0;
        var quit = 0;
        main.HideWindow = () => hidden++;
        main.Quit = () =>
        {
            quit++;
            return Task.CompletedTask;
        };
        var window = new MainWindow { DataContext = main };
        window.Show();
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        var folder = Assert.Single(dashboard.Folders);

        // the very first time the window is closed, a page says that the application stays
        Assert.False(main.MayCloseWindowSilently());
        var leave = Assert.IsType<LeaveViewModel>(main.CurrentPage);
        Assert.False(leave.IsBackupRunning);
        Assert.Equal("Imagepixel Backup läuft im Hintergrund weiter", leave.Title);
        Assert.True(leave.CanCloseWindow);
        Capture(window, "15-fenster-schliessen-erstes-mal");
        leave.CloseWindowCommand.Execute(null);
        Assert.Equal(1, hidden);
        Assert.Same(dashboard, main.CurrentPage);
        Assert.True(new ConfigStore(DataFolder).Load().BackgroundExplained);

        // from then on the window closes without a word while nothing runs, and so does quitting
        Assert.True(main.MayCloseWindowSilently());
        Assert.True(main.MayQuitSilently());
        Assert.Same(dashboard, main.CurrentPage);

        // while a backup runs, closing explains that the backup goes on
        folder.IsRunning = true;
        Assert.True(main.IsBackupRunning);
        Assert.False(main.MayCloseWindowSilently());
        leave = Assert.IsType<LeaveViewModel>(main.CurrentPage);
        Assert.True(leave.IsBackupRunning);
        Assert.Equal("Die Sicherung läuft im Hintergrund weiter", leave.Title);
        Assert.Contains("sichert zu Ende", leave.Text);
        Capture(window, "15a-fenster-schliessen-sicherung-laeuft");
        leave.StayCommand.Execute(null);
        Assert.Same(dashboard, main.CurrentPage);
        Assert.Equal(1, hidden);

        // quitting would stop the backup, so it asks and only quits on a clear answer
        Assert.False(main.MayQuitSilently());
        leave = Assert.IsType<LeaveViewModel>(main.CurrentPage);
        Assert.True(leave.IsQuitting);
        Assert.False(leave.CanCloseWindow);
        Assert.Contains("abgebrochen", leave.Text);
        Assert.Contains("beim nächsten Start", leave.Text);
        Capture(window, "15b-beenden-sicherung-laeuft");
        leave.StayCommand.Execute(null);
        Assert.Equal(0, quit);
        Assert.Same(dashboard, main.CurrentPage);

        Assert.False(main.MayQuitSilently());
        await Assert.IsType<LeaveViewModel>(main.CurrentPage).QuitCommand.ExecuteAsync(null);
        Assert.Equal(1, quit);
        folder.IsRunning = false;

        // a backup that the end of the application stops counts as not attempted and is due again at once
        Assert.Null(folder.Folder.LastAttempt);
        var run = folder.BackupNowCommand.ExecuteAsync(null);
        folder.CancelBackup();
        await run;
        Assert.Null(folder.Folder.LastAttempt);
        Assert.Null(new ConfigStore(DataFolder).Load().Folders[0].LastAttempt);
        Assert.True(BackupSchedule.IsDue(folder.Folder, DateTimeOffset.Now));
        Assert.False(folder.IsPaused);

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task Exceptions_leave_out_what_the_customer_chose_and_nothing_else()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        // a folder as a developer has it: things worth keeping next to things that can be made again
        Directory.CreateDirectory(Path.Combine(SourceFolder, "node_modules", "paket"));
        File.WriteAllText(Path.Combine(SourceFolder, "node_modules", "paket", "index.js"), "neu erzeugbar");
        Directory.CreateDirectory(Path.Combine(SourceFolder, "Projekt", "NODE_MODULES"));
        File.WriteAllText(Path.Combine(SourceFolder, "Projekt", "NODE_MODULES", "x.js"), "neu erzeugbar");
        File.WriteAllText(Path.Combine(SourceFolder, "Projekt", "Programm.cs"), "Quelltext");
        File.WriteAllText(Path.Combine(SourceFolder, "Protokoll.LOG"), "Protokoll");
        File.WriteAllText(Path.Combine(SourceFolder, "Film.mp4"), "Video");
        Directory.CreateDirectory(Path.Combine(SourceFolder, ".git"));
        File.WriteAllText(Path.Combine(SourceFolder, ".git", "HEAD"), "ref: refs/heads/main");

        var manager = new BackupManager(new ConfigStore(DataFolder), new MemorySecretStore(), restic!);
        manager.AcceptTerms(Texts.TermsVersion);
        await manager.ConnectAsync(
            new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = StorageFolder }, Password, null, createIfMissing: true);
        manager.AddFolder(SourceFolder).Schedule = ScheduleKind.Manual;
        manager.Save();

        var main = new MainViewModel(manager, []);
        var window = new MainWindow { DataContext = main };
        window.Show();
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        var folder = Assert.Single(dashboard.Folders);
        Assert.False(folder.HasExclusions);
        Assert.Equal("", folder.ExclusionsText);

        // nothing is ticked for the customer, and leaving the page changes nothing
        folder.EditExclusionsCommand.Execute(null);
        var page = Assert.IsType<ExclusionsViewModel>(main.CurrentPage);
        Assert.Equal(4, page.Presets.Count);
        Assert.All(page.Presets, preset => Assert.False(preset.IsOn));
        Assert.False(page.LimitSize);
        Assert.Empty(page.Rules);
        page.Presets[0].IsOn = true;
        page.CancelCommand.Execute(null);
        Assert.Same(dashboard, main.CurrentPage);
        Assert.False(folder.HasExclusions);

        // suggestions in plain words
        folder.EditExclusionsCommand.Execute(null);
        page = Assert.IsType<ExclusionsViewModel>(main.CurrentPage);
        var buildOutput = page.Presets.Single(preset => preset.Preset == FolderExclusions.BuildOutput);
        Assert.Equal("Keine Ordner, die Entwicklungswerkzeuge neu erzeugen", buildOutput.Title);
        Assert.Contains("node_modules", buildOutput.Detail);
        buildOutput.IsOn = true;
        page.Presets.Single(preset => preset.Preset == FolderExclusions.Videos).IsOn = true;

        // a rule of the customer's own, however they type the ending
        page.NewKindIndex = (int)ExclusionKind.Extension;
        page.NewValue = "*.log";
        page.AddRuleCommand.Execute(null);
        Assert.Equal("Dateien mit der Endung .log", Assert.Single(page.Rules).Text);
        Assert.Equal("", page.NewValue);

        page.NewValue = ".LOG";
        page.AddRuleCommand.Execute(null);
        Assert.Equal("Diese Regel gibt es schon.", page.RuleMessage);
        page.NewValue = @"Ordner\Datei";
        page.AddRuleCommand.Execute(null);
        Assert.StartsWith("Bitte gib eine Endung oder einen Namen ein", page.RuleMessage);
        Assert.Single(page.Rules);

        // a rule for names with a dot would take the history of the project along: said at once
        Assert.False(page.LeavesOutGit);
        page.NewKindIndex = (int)ExclusionKind.NamePrefix;
        page.NewValue = ".";
        page.AddRuleCommand.Execute(null);
        Assert.Null(page.RuleMessage);
        Assert.True(page.LeavesOutGit);
        page.LimitSize = true;
        page.SizeIndex = 1;
        window.Height = 1180;
        Capture(window, "16-ausnahmen");
        window.Height = 700;
        page.RemoveRuleCommand.Execute(page.Rules[1]);
        Assert.False(page.LeavesOutGit);

        page.SaveCommand.Execute(null);
        Assert.Same(dashboard, main.CurrentPage);
        Assert.True(folder.HasExclusions);
        Assert.Equal("Ausnahmen: 4 Regeln", folder.ExclusionsText);
        Capture(window, "16a-uebersicht-mit-ausnahmen");

        var saved = new ConfigStore(DataFolder).Load().Folders[0].Exclusions;
        Assert.Equal([FolderExclusions.BuildOutput.Id, FolderExclusions.Videos.Id], saved.Presets);
        Assert.Equal(1024, saved.LargerThanMb);

        // the backup follows the rules: what was chosen is missing, everything else is there
        await folder.BackupNowCommand.ExecuteAsync(null);
        Assert.NotNull(folder.Folder.LastSuccess);
        var snapshot = (await manager.GetSnapshotsAsync())[^1];
        await manager.RestoreAsync(snapshot.Id, RestoreFolder);
        var restored = Path.Combine(RestoreFolder, "Dokumente");

        Assert.True(File.Exists(Path.Combine(restored, "Brief.txt")));
        Assert.True(File.Exists(Path.Combine(restored, "Fotos.zip")));
        Assert.True(File.Exists(Path.Combine(restored, "Projekt", "Programm.cs")));
        Assert.True(File.Exists(Path.Combine(restored, ".git", "HEAD")));

        Assert.False(Directory.Exists(Path.Combine(restored, "node_modules")));
        Assert.False(Directory.Exists(Path.Combine(restored, "Projekt", "NODE_MODULES")));
        Assert.False(File.Exists(Path.Combine(restored, "Protokoll.LOG")));
        Assert.False(File.Exists(Path.Combine(restored, "Film.mp4")));

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task First_backup_waits_for_the_answer_to_the_suggested_exceptions()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        // two folders of a developer, each with more than enough that can be made again
        var second = Path.Combine(_root, "Zweites Projekt");
        foreach (var folderPath in new[] { SourceFolder, second })
        {
            Directory.CreateDirectory(Path.Combine(folderPath, "node_modules", "paket"));
            File.WriteAllBytes(Path.Combine(folderPath, "node_modules", "paket", "index.js"), new byte[11 * 1024 * 1024]);
            File.WriteAllText(Path.Combine(folderPath, "Thumbs.db"), "Vorschaubilder");
            File.WriteAllText(Path.Combine(folderPath, "Film.mp4"), "Video");
            File.WriteAllText(Path.Combine(folderPath, "Programm.cs"), "Quelltext");
        }

        var manager = new BackupManager(new ConfigStore(DataFolder), new MemorySecretStore(), restic!);
        manager.AcceptTerms(Texts.TermsVersion);
        await manager.ConnectAsync(
            new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = StorageFolder }, Password, null, createIfMissing: true);
        manager.AddFolder(SourceFolder).Schedule = ScheduleKind.Manual;
        manager.AddFolder(second).Schedule = ScheduleKind.Manual;
        manager.Save();

        var main = new MainViewModel(manager, []);
        var window = new MainWindow { DataContext = main };
        window.Show();
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        var folder = dashboard.Folders[0];
        var other = dashboard.Folders[1];

        // starting the first backup looks through the folder and asks instead of backing up
        await folder.BackupNowCommand.ExecuteAsync(null);
        var page = Assert.IsType<ExclusionSuggestionViewModel>(main.CurrentPage);
        Assert.Null(folder.Folder.LastAttempt);
        Assert.False(folder.IsRunning);
        Assert.True(folder.HasSuggestion);
        Assert.Contains("Vor der ersten Sicherung", folder.SuggestionText);

        // what can be made again is ticked, with the space it takes; the customer's own files are only shown
        var build = page.Options.Single(option => option.Finding.Preset == FolderExclusions.BuildOutput);
        var temporary = page.Options.Single(option => option.Finding.Preset == FolderExclusions.Temporary);
        var videos = page.Options.Single(option => option.Finding.Preset == FolderExclusions.Videos);
        Assert.True(build.IsOn);
        Assert.True(temporary.IsOn);
        Assert.False(videos.IsOn);
        Assert.Equal("etwa 11 MB in 1 Datei", build.Size);
        Assert.StartsWith("Mit den angehakten Ausnahmen sparst du etwa 11 MB von ", page.Summary);
        window.Height = 900;
        Capture(window, "17-vorschlag-ausnahmen");
        window.Height = 700;

        // the customer changes the suggestion and takes it over: the backup runs with what is ticked
        temporary.IsOn = false;
        await page.AcceptCommand.ExecuteAsync(null);
        Assert.Same(dashboard, main.CurrentPage);
        Assert.NotNull(folder.Folder.LastSuccess);
        Assert.False(folder.HasSuggestion);
        Assert.Equal("Ausnahmen: 1 Regel", folder.ExclusionsText);
        var saved = new ConfigStore(DataFolder).Load().Folders[0];
        Assert.True(saved.ExclusionsDecided);
        Assert.Equal([FolderExclusions.BuildOutput.Id], saved.Exclusions.Presets);

        var snapshot = (await manager.GetSnapshotsAsync())[^1];
        await manager.RestoreAsync(snapshot.Id, RestoreFolder);
        var restored = Path.Combine(RestoreFolder, "Dokumente");
        Assert.True(File.Exists(Path.Combine(restored, "Programm.cs")));
        Assert.True(File.Exists(Path.Combine(restored, "Thumbs.db")));
        Assert.True(File.Exists(Path.Combine(restored, "Film.mp4")));
        Assert.False(Directory.Exists(Path.Combine(restored, "node_modules")));

        // the exceptions can be changed at any time afterwards
        folder.EditExclusionsCommand.Execute(null);
        var exceptions = Assert.IsType<ExclusionsViewModel>(main.CurrentPage);
        Assert.True(exceptions.Presets.Single(preset => preset.Preset == FolderExclusions.BuildOutput).IsOn);
        exceptions.CancelCommand.Execute(null);

        // a suggestion that comes up while the customer is elsewhere waits in the overview
        main.ShowAbout();
        var about = main.CurrentPage;
        await other.BackupNowCommand.ExecuteAsync(null);
        Assert.Same(about, main.CurrentPage);
        Assert.True(other.HasSuggestion);
        Assert.Null(other.Folder.LastAttempt);
        main.ShowDashboard();
        Capture(window, "17a-uebersicht-vorschlag-wartet");
        other.ShowSuggestionCommand.Execute(null);
        page = Assert.IsType<ExclusionSuggestionViewModel>(main.CurrentPage);

        // turning it down backs the whole folder up and does not ask again
        await page.DeclineCommand.ExecuteAsync(null);
        Assert.Same(dashboard, main.CurrentPage);
        Assert.NotNull(other.Folder.LastSuccess);
        Assert.False(other.HasExclusions);
        Assert.False(other.HasSuggestion);
        Assert.True(new ConfigStore(DataFolder).Load().Folders[1].ExclusionsDecided);
        await other.BackupNowCommand.ExecuteAsync(null);
        Assert.Same(dashboard, main.CurrentPage);

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task Backup_that_is_set_up_keeps_its_schedule_until_the_notes_are_confirmed()
    {
        // a computer that was set up by a version without the notes, as after an update
        new ConfigStore(DataFolder).Save(new AppConfig
        {
            Storage = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = StorageFolder },
        });
        var secrets = new MemorySecretStore();
        secrets.Set(SecretNames.RepositoryPassword, Password);

        var main = new MainViewModel(new BackupManager(new ConfigStore(DataFolder), secrets, "restic"), []);

        // the notes come first, but the backups do not wait for the customer to open the window
        var terms = Assert.IsType<TermsViewModel>(main.CurrentPage);
        Assert.True(main.IsScheduleRunning);
        Assert.False(terms.ContinueCommand.CanExecute(null));

        terms.Confirmed = true;
        terms.ContinueCommand.Execute(null);
        Assert.IsType<DashboardViewModel>(main.CurrentPage);
        await main.ShutdownAsync();

        // the next start does not ask again
        var next = new MainViewModel(new BackupManager(new ConfigStore(DataFolder), secrets, "restic"), []);
        Assert.IsType<DashboardViewModel>(next.CurrentPage);
        await next.ShutdownAsync();
    }

    private static BackupProgress Progress(long bytesDone, long filesDone, string currentFile) => new()
    {
        Fraction = 0.42,
        TotalBytes = 2970L * 1024 * 1024,
        TotalFiles = 3870,
        BytesDone = bytesDone,
        FilesDone = filesDone,
        CurrentFiles = [currentFile],
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(50);
        }
    }

    /// <summary>The folders of the saved states on the drive. Linux adds "latest", a link to the newest.</summary>
    private static string[] SnapshotFolders(string drive) =>
        [.. Directory.GetDirectories(drive).Where(d => Path.GetFileName(d) != "latest").Order()];

    private static void Capture(Window window, string name) => ScreenCapture.Save(window, "screenshots", name);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
