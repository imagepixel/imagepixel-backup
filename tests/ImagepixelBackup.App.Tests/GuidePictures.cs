using System.Net;
using Avalonia.Headless.XUnit;
using ImagepixelBackup.App.ViewModels;
using ImagepixelBackup.App.Views;
using ImagepixelBackup.Core;
using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.App.Tests;

/// <summary>
/// Renders the pictures for the customer guide. They show a made-up customer, so that
/// no real folder, account or key appears in them. The pictures are saved to
/// artifacts/guide.
/// </summary>
public sealed class GuidePictures : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string Bucket = "musterfirma-gmbh";

    private static readonly CloudFolder[] CloudStorage =
    [
        new(CloudService.OneDrive, "OneDrive", @"C:\Users\Anna\OneDrive"),
        new(CloudService.GoogleDrive, "Google Drive", @"G:\Meine Ablage"),
        new(CloudService.ICloudDrive, "iCloud Drive", @"C:\Users\Anna\iCloudDrive"),
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-guide-" + Guid.NewGuid().ToString("N"));

    /// <summary>Keeps the pictures from depending on, or talking to, anything outside.</summary>
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private BackupManager Manager(ISecretStore? secrets = null, string restic = "restic") => new(
        new ConfigStore(Path.Combine(_root, "settings")),
        secrets ?? new MemorySecretStore(),
        restic,
        null,
        new StorageStatusService(new HttpClient(new NoNetwork())));

    [AvaloniaFact]
    public void Setting_up_a_backup_and_restoring()
    {
        var main = new MainViewModel(Manager(), CloudStorage);
        var window = new MainWindow { DataContext = main };
        window.Show();

        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        window.Height = 330;
        ScreenCapture.Save(window, "guide", "start");

        setup.ChooseModeCommand.Execute("Backup");
        setup.Sources.Add(@"C:\Users\Anna\Dokumente");
        setup.Sources.Add(@"C:\Users\Anna\OneDrive");
        setup.SetupCodeText = new SetupCode(Bucket, "ABCDEFGHIJKLMNOPQRST", "ein-geheimer-schluessel", "eu-central-2").Encode();
        setup.Password = "ein langes Passwort";
        setup.PasswordRepeat = "ein langes Passwort";
        window.Height = 1700;
        ScreenCapture.Save(window, "guide", "sichern");

        setup.ChooseModeCommand.Execute("Restore");
        setup.PasswordRepeat = "";
        window.Height = 1110;
        ScreenCapture.Save(window, "guide", "wiederherstellen-anmelden");

        window.Close();
    }

    [AvaloniaFact]
    public async Task Overview_with_storage_figures()
    {
        var now = DateTimeOffset.Now;
        new ConfigStore(Path.Combine(_root, "settings")).Save(new AppConfig
        {
            Storage = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = Bucket, AccessKeyId = "ABCDEFGHIJKLMNOPQRST" },
            Folders =
            {
                new BackupFolder { Path = @"C:\Users\Anna\Dokumente", LastAttempt = now.AddMinutes(-25), LastSuccess = now.AddMinutes(-25) },
                new BackupFolder
                {
                    Path = @"C:\Users\Anna\OneDrive",
                    Schedule = ScheduleKind.Daily,
                    DailyAt = new TimeOnly(18, 30),
                    LastAttempt = now,
                    LastSuccess = now.AddHours(-3),
                },
            },
        });

        var secrets = new MemorySecretStore();
        secrets.Set(SecretNames.RepositoryPassword, "password");
        var main = new MainViewModel(Manager(secrets), CloudStorage);
        var window = new MainWindow { DataContext = main, Height = 900 };
        window.Show();

        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        StorageStatus? figures = new(100 * Gb, 62 * Gb, now.AddHours(-2), StorageStatusSource.Api);
        main.LoadStorageStatus = () => Task.FromResult<StorageStatus?>(figures);
        await dashboard.RefreshStorageStatusAsync();
        dashboard.StatusLine = "„Dokumente“ gesichert: 14 neue oder geänderte Dateien, 38,2 MB übertragen.";
        ScreenCapture.Save(window, "guide", "uebersicht");

        figures = figures with { UsedBytes = 86 * Gb };
        await dashboard.RefreshStorageStatusAsync();
        window.Height = 300;
        ScreenCapture.Save(window, "guide", "speicher-warnung");

        await main.ShutdownAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Choosing_a_state_to_restore()
    {
        var restic = ResticClient.FindRestic();
        Assert.SkipWhen(restic is null, "restic has not been built");

        // a small backup with two states, so that the list has something to show
        var source = Path.Combine(_root, "Dokumente");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "Angebot.pdf"), new byte[1_800_000]);
        File.WriteAllText(Path.Combine(source, "Brief.docx"), "Erste Fassung");

        var secrets = new MemorySecretStore();
        var manager = Manager(secrets, restic!);
        var storage = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Path.Combine(_root, "repo") };
        await manager.ConnectAsync(storage, "password", null, createIfMissing: true);
        var folder = manager.AddFolder(source);
        await manager.BackupAsync(folder);
        File.WriteAllText(Path.Combine(source, "Notizen.txt"), "Neu");
        await manager.BackupAsync(folder);

        var main = new MainViewModel(manager, []);
        var window = new MainWindow { DataContext = main, Height = 560 };
        window.Show();
        main.ShowRestore();

        var restore = Assert.IsType<RestoreViewModel>(main.CurrentPage);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (restore.IsLoading)
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(50);
        }

        Assert.Equal(2, restore.Snapshots.Count);
        restore.SelectedSnapshot = restore.Snapshots[0];
        restore.TargetFolder = @"D:\Wiederhergestellt";
        ScreenCapture.Save(window, "guide", "wiederherstellen");

        await main.ShutdownAsync();
        window.Close();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
