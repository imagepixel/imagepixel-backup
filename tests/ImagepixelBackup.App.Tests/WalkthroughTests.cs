using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ImagepixelBackup.App.ViewModels;
using ImagepixelBackup.App.Views;
using ImagepixelBackup.Core;
using ImagepixelBackup.Core.Restic;

[assembly: AvaloniaTestApplication(typeof(ImagepixelBackup.App.Tests.TestAppBuilder))]

namespace ImagepixelBackup.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<global::ImagepixelBackup.App.App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Walks through the application the way a customer would, without showing a window:
/// choose what to back up and where to, restore it and open the backup as a drive.
/// The tests run the real restic program against folders in the temp directory and
/// save a picture of every screen to artifacts/screenshots.
/// </summary>
public sealed class WalkthroughTests : IDisposable
{
    private const string Password = "sehr-geheim-123";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-ui-" + Guid.NewGuid().ToString("N"));

    private string DataFolder => Path.Combine(_root, "Einstellungen");

    private string SourceFolder => Path.Combine(_root, "Dokumente");

    private string StorageFolder => Path.Combine(_root, "USB-Festplatte", "Backup");

    private string RestoreFolder => Path.Combine(_root, "Wiederhergestellt");

    /// <summary>Stands in for the cloud storage a customer has set up on the computer.</summary>
    private CloudFolder[] CloudStorage =>
    [
        new(CloudService.OneDrive, "OneDrive", Path.Combine(_root, "OneDrive")),
        new(CloudService.GoogleDrive, "Google Drive", Path.Combine(_root, "Google", "Meine Ablage")),
        new(CloudService.ICloudDrive, "iCloud Drive", Path.Combine(_root, "iCloudDrive")),
    ];

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
        var main = new MainViewModel(manager, CloudStorage);
        var window = new MainWindow { DataContext = main };

        // answers of the folder dialog, and the folders the application wants to show
        string? chosenFolder = null;
        var opened = new List<string>();
        main.PickFolder = _ => Task.FromResult(chosenFolder);
        main.OpenInFileManager = opened.Add;
        window.Show();

        // --- step 1: what to back up ---------------------------------------------
        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        Assert.False(setup.CanChooseDestination);
        Assert.True(setup.IsImagepixel);

        // the fields of the destination only appear once a source has been chosen
        Assert.False(setup.ShowImagepixelFields);
        Capture(window, "01-einrichtung-start");

        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("zuerst aus, was gesichert werden soll", setup.ErrorMessage);

        // cloud storage found on the computer is one click away and comes with an explanation
        Assert.Equal(3, setup.CloudFolders.Count);
        Assert.False(setup.HasCloudSource);
        setup.AddCloudSourceCommand.Execute(CloudStorage[0]);
        setup.AddCloudSourceCommand.Execute(CloudStorage[0]);
        Assert.Equal([CloudStorage[0].Path], setup.Sources);
        Assert.True(setup.HasCloudSource);
        Assert.True(setup.CanChooseDestination);
        Capture(window, "01b-einrichtung-cloud-speicher");
        setup.RemoveSourceCommand.Execute(CloudStorage[0].Path);
        Assert.False(setup.CanChooseDestination);

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
        Capture(window, "03-einrichtung-imagepixel-storage");

        // the whole page: what comes from Imagepixel, and the password the customer chooses
        setup.ShowPassword = true;
        setup.Password = "Mein-eigenes-Passwort";
        window.Height = 1420;
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

        // a folder of a cloud service is allowed as a destination, with an explanation
        setup.LocalPath = Path.Combine(CloudStorage[0].Path, "Backup");
        Assert.True(setup.HasDestinationHint);
        Assert.Contains("gehört zu OneDrive", setup.DestinationHint);
        Capture(window, "04-einrichtung-ziel-im-cloud-ordner");

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

        // what a running backup looks like
        folder.IsRunning = true;
        folder.ProgressPercent = 42;
        folder.ProgressText = "1,2 GB von 2,9 GB · 1.204 von 3.870 Dateien";
        Capture(window, "07-uebersicht-sicherung-laeuft");
        folder.IsRunning = false;

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
        if (OperatingSystem.IsWindows() && Directory.Exists(@"C:\Program Files (x86)\WinFsp"))
        {
            await dashboard.MountCommand.ExecuteAsync(null);
            Assert.True(dashboard.IsMounted, dashboard.StatusLine);
            var drive = Assert.Single(opened);
            Capture(window, "09-uebersicht-laufwerk-geoeffnet");

            var snapshot = Directory.GetDirectories(drive).Last();
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
    public async Task Existing_backup_can_be_opened_without_choosing_a_folder()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        await new ResticClient(restic!, new RepositoryAccess(StorageFolder, Password)).InitAsync();

        var manager = new BackupManager(new ConfigStore(DataFolder), new MemorySecretStore(), restic!);
        var main = new MainViewModel(manager, CloudStorage);
        var window = new MainWindow { DataContext = main };
        var opened = new List<string>();
        main.OpenInFileManager = opened.Add;
        window.Show();

        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        setup.UseExistingBackupCommand.Execute(null);
        Assert.True(setup.CanChooseDestination);
        Assert.Equal("Verbinden", setup.ConnectText);

        setup.ChooseKindCommand.Execute("LocalFolder");
        setup.LocalPath = StorageFolder;
        setup.Password = "ein-anderes-passwort";
        await setup.ConnectCommand.ExecuteAsync(null);

        Assert.Contains("das Passwort passt nicht", setup.ErrorMessage);
        Assert.False(manager.IsConnected);
        Assert.IsType<SetupViewModel>(main.CurrentPage);

        // the right password connects without asking for it twice
        setup.Password = Password;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);

        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.True(dashboard.HasNoFolders);
        Assert.Equal(3, dashboard.AvailableCloudFolders.Count);
        Capture(window, "10-uebersicht-ohne-ordner");

        // a cloud folder is added with one click, backed up right away and explained
        await dashboard.AddCloudFolderCommand.ExecuteAsync(CloudStorage[2]);
        var cloudFolder = Assert.Single(dashboard.Folders);
        Assert.Equal(CloudStorage[2].Path, cloudFolder.Path);
        Assert.True(cloudFolder.IsCloudFolder);
        Assert.False(cloudFolder.HasProblem, cloudFolder.StatusText);
        Assert.NotNull(cloudFolder.Folder.LastSuccess);
        Assert.Equal(["OneDrive", "Google Drive"], dashboard.AvailableCloudFolders.Select(c => c.Name));
        Capture(window, "10b-uebersicht-cloud-ordner");

        cloudFolder.RemoveCommand.Execute(null);
        Assert.Equal(3, dashboard.AvailableCloudFolders.Count);

        // the info page names the components and returns to where it was opened
        dashboard.ShowAboutCommand.Execute(null);
        var about = Assert.IsType<AboutViewModel>(main.CurrentPage);
        Assert.StartsWith("Version 0.", about.Version);
        Capture(window, "11-info");
        about.OpenLinkCommand.Execute(AboutViewModel.WinFspUrl);
        Assert.Equal([AboutViewModel.WinFspUrl], opened);
        about.BackCommand.Execute(null);
        Assert.Same(dashboard, main.CurrentPage);

        await main.ShutdownAsync();
        window.Close();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(50);
        }
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            return;
        }

        var folder = Path.Combine(RepositoryRoot(), "artifacts", "screenshots");
        Directory.CreateDirectory(folder);
        frame.Save(Path.Combine(folder, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ImagepixelBackup.slnx")))
            {
                return directory.FullName;
            }
        }

        return Path.GetTempPath();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
