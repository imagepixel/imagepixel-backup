using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ImagepixelBackup.App.ViewModels;
using ImagepixelBackup.App.Views;
using ImagepixelBackup.Core;

namespace ImagepixelBackup.App.Tests;

/// <summary>
/// Pairing a computer with the customer portal, the way a customer does it: ask for a
/// code, enter it in the portal, wait for the access data, choose a password. The portal
/// is played by the test; restic by a small script that says yes and writes down where
/// it was sent.
/// </summary>
public sealed class PairingWalkthroughTests : IDisposable
{
    private const string Password = "sehr-geheim-123";

    private const string DeviceId = "3f6c2a1e-8d4b-4c7a-9e21-5b0f7d9c4a10";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-pairing-" + Guid.NewGuid().ToString("N"));

    private string DataFolder => Path.Combine(_root, "Einstellungen");

    private string SourceFolder => Path.Combine(_root, "Dokumente");

    private string ResticLog => Path.Combine(_root, "restic.log");

    public PairingWalkthroughTests()
    {
        Directory.CreateDirectory(SourceFolder);
        File.WriteAllText(Path.Combine(SourceFolder, "Brief.txt"), "Sehr geehrte Damen und Herren");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a process of the test may still hold a file; the temp folder is cleaned up eventually
        }
    }

    [AvaloniaFact]
    public async Task Customer_pairs_the_computer_with_the_portal_and_starts_the_backup()
    {
        var portal = new FakePortal();
        var secrets = new MemorySecretStore();
        var (main, manager) = Start(portal, secrets);
        var window = new MainWindow { DataContext = main };
        window.Show();

        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        setup.ChooseModeCommand.Execute("Backup");
        setup.Sources.Add(SourceFolder);

        // where the portal is set up, pairing is offered next to the setup code, and chosen
        Assert.True(setup.ShowAccessChoice);
        Assert.True(setup.UsesPairing);
        Assert.True(setup.ShowPairingFields);
        Assert.False(setup.ShowSetupCodeFields);
        Assert.True(setup.ShowPairingIntro);
        Assert.Equal(Texts.PairingRequestCode, setup.PairingRequestText);
        Capture(window, "20-kopplung-start");

        setup.Password = Password;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(Texts.PairingMissing, setup.ErrorMessage);

        // the portal turns the request down: its sentence is shown, and asking again is possible
        portal.Answer = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"message":"Zu viele Codes in kurzer Zeit. Bitte warte eine Stunde."}"""),
        };
        await setup.RequestPairingCodeCommand.ExecuteAsync(null);
        Assert.Equal("Zu viele Codes in kurzer Zeit. Bitte warte eine Stunde.", setup.PairingProblem);
        Assert.True(setup.CanRequestPairingCode);
        portal.Answer = null;

        // --- the code, to be entered in the portal ------------------------------------
        await setup.RequestPairingCodeCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);
        Assert.Equal("K7QM-4TZP", setup.PairingCode);
        Assert.True(setup.ShowPairingCode);
        Assert.True(setup.IsPairingPending);
        Assert.False(setup.HasPairingProblem);
        Assert.Equal(Texts.PairingWaiting, setup.PairingStatusText);
        Assert.Contains("Zugang für ein Gerät anlegen", setup.PairingCodeHint);
        Assert.Contains("Uhr", setup.PairingCodeHint);
        Assert.False(setup.CanRequestPairingCode);
        Assert.Equal("windows", portal.System);
        Capture(window, "21-kopplung-code");

        // the key and the secret wait with the other secrets, not in the settings
        Assert.NotNull(secrets.Get(SecretNames.DevicePairing));
        Assert.DoesNotContain("K7QM", File.ReadAllText(Path.Combine(DataFolder, "config.json")));

        // --- the code was entered, Imagepixel makes the access data -------------------
        portal.Stage = "in-arbeit";
        await setup.CheckPairingAsync();
        Assert.False(setup.ShowPairingCode);
        Assert.True(setup.IsPairingPending);
        Assert.Equal(Texts.PairingInProgress, setup.PairingStatusText);
        Capture(window, "22-kopplung-in-arbeit");

        // the portal cannot be reached for a moment: it is said, and asked again
        portal.Answer = _ => throw new HttpRequestException("connection refused");
        await setup.CheckPairingAsync();
        Assert.Equal(Texts.PairingUnreachable, setup.PairingStatusText);
        Assert.True(setup.IsPairingPending);
        portal.Answer = null;

        // --- the access data arrive, encrypted for this computer ----------------------
        portal.Stage = "fertig";
        portal.Access = PairingCipher.Encrypt(Access(), portal.PublicKey!, "K7QM4TZP");
        await setup.CheckPairingAsync();
        Assert.True(setup.IsPairingDone);
        Assert.False(setup.IsPairingPending);
        Assert.True(setup.HasAccess);
        Assert.Equal("kunde-beispiel", setup.Bucket);
        Assert.Equal(Texts.PairingDone("Notebook Anna"), setup.PairingDoneText);
        Assert.Equal(1, portal.Confirmed);
        Assert.Null(setup.ErrorMessage);
        Capture(window, "23-kopplung-fertig");

        // the setup code is still one click away, and takes over only while it is chosen
        setup.ChooseAccessWayCommand.Execute("SetupCode");
        Assert.True(setup.ShowSetupCodeFields);
        Assert.False(setup.HasAccess);
        setup.ChooseAccessWayCommand.Execute("Pairing");
        Assert.True(setup.HasAccess);

        // restoring takes the setup code: the folder of a new pairing holds nothing yet
        setup.ChooseModeCommand.Execute("Restore");
        Assert.False(setup.ShowAccessChoice);
        Assert.True(setup.ShowSetupCodeFields);
        Assert.False(setup.HasAccess);
        setup.ChooseModeCommand.Execute("Backup");
        Assert.True(setup.HasAccess);
        Assert.Equal([SourceFolder], setup.Sources);

        // --- the password, and off it goes --------------------------------------------
        setup.Password = Password;
        setup.PasswordRepeat = Password;
        window.Height = 1300;
        Capture(window, "23b-kopplung-komplett");
        window.Height = 700;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);

        // every backup of the computer goes into its own folder of the bucket
        var storage = manager.Config.Storage!;
        Assert.Equal("geraete/" + DeviceId, storage.Root);
        Assert.Equal("Notebook Anna", storage.DeviceName);
        Assert.Equal($"s3:https://s3.eu-central-2.wasabisys.com/kunde-beispiel/geraete/{DeviceId}", storage.RepositoryUrl);
        Assert.Contains(storage.RepositoryUrl, File.ReadAllLines(ResticLog));
        Assert.Equal("EXAMPLEACCESSKEY0001", storage.AccessKeyId);
        Assert.Equal("beispiel-geheim", secrets.Get(SecretNames.SecretAccessKey));
        Assert.Equal("https://status.example.test/v1", storage.ApiUrl);
        Assert.Equal("beispiel-token", secrets.Get(SecretNames.ApiToken));

        // what the pairing kept is with the storage now
        Assert.Null(secrets.Get(SecretNames.DevicePairing));
        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.Equal("Imagepixel Storage · kunde-beispiel · Notebook Anna", dashboard.StorageName);

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task A_pairing_that_cannot_go_on_offers_a_new_code()
    {
        var portal = new FakePortal();
        var (main, _) = Start(portal, new MemorySecretStore());
        var window = new MainWindow { DataContext = main };
        window.Show();

        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        setup.ChooseModeCommand.Execute("Backup");
        setup.Sources.Add(SourceFolder);
        await setup.RequestPairingCodeCommand.ExecuteAsync(null);

        // the portal could not make the access data: its sentence, and a new code
        portal.Stage = "fehler";
        portal.Result = "Für deine Firma ist noch kein Speicher eingerichtet. Bitte wende dich an Imagepixel.";
        await setup.CheckPairingAsync();
        Assert.Equal(Texts.PairingFailed(portal.Result), setup.PairingProblem);
        Assert.False(setup.IsPairingPending);
        Assert.True(setup.CanRequestPairingCode);
        Assert.Equal(Texts.PairingRequestNewCode, setup.PairingRequestText);
        Capture(window, "24-kopplung-fehler");

        // nobody entered the code in time
        portal.Stage = "wartet";
        await setup.RequestPairingCodeCommand.ExecuteAsync(null);
        Assert.False(setup.HasPairingProblem);
        portal.Stage = "abgelaufen";
        await setup.CheckPairingAsync();
        Assert.Equal(Texts.PairingExpired, setup.PairingProblem);
        Assert.True(setup.CanRequestPairingCode);
        Capture(window, "25-kopplung-abgelaufen");

        // access data that this computer cannot open
        portal.Stage = "wartet";
        await setup.RequestPairingCodeCommand.ExecuteAsync(null);
        portal.Stage = "fertig";
        portal.Access = PairingCipher.Encrypt(Access(), PairingCipher.CreateDeviceKey().PublicKey, "K7QM4TZP");
        await setup.CheckPairingAsync();
        Assert.Equal(Texts.PairingFailed(null), setup.PairingProblem);
        Assert.False(setup.HasAccess);

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task The_page_asks_the_portal_by_itself_and_goes_on_after_a_restart()
    {
        var portal = new FakePortal();
        var secrets = new MemorySecretStore();
        var (main, _) = Start(portal, secrets, TimeSpan.FromMilliseconds(20));

        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        setup.ChooseModeCommand.Execute("Backup");
        setup.Sources.Add(SourceFolder);
        await setup.RequestPairingCodeCommand.ExecuteAsync(null);
        var asked = portal.Fetched;

        portal.Stage = "in-arbeit";
        await Until(() => setup.PairingStage == PairingStage.InProgress);
        Assert.True(portal.Fetched > asked);

        // the application ends and starts again: the same pairing goes on
        await main.ShutdownAsync();
        (main, _) = Start(portal, secrets, TimeSpan.FromMilliseconds(20));
        setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        Assert.Equal("K7QM-4TZP", setup.PairingCode);
        await Until(() => setup.PairingStage == PairingStage.InProgress);

        portal.Stage = "fertig";
        portal.Access = PairingCipher.Encrypt(Access(), portal.PublicKey!, "K7QM-4TZP");
        await Until(() => setup.IsPairingDone);
        await setup.PairingWatch;

        // once the access data are there, the portal is no longer asked
        var after = portal.Fetched;
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(after, portal.Fetched);
        Assert.Equal(1, portal.Confirmed);

        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task A_computer_with_a_setup_code_can_be_paired_later()
    {
        var portal = new FakePortal();
        var secrets = new MemorySecretStore();
        var (main, manager) = Start(portal, secrets);
        await manager.ConnectAsync(
            new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde-beispiel", AccessKeyId = "ALLES", Folder = "Notebook" },
            Password, "alles-geheim", createIfMissing: false);
        manager.AddFolder(SourceFolder);

        var window = new MainWindow { DataContext = main };
        window.Show();
        main.ShowAbout();
        var about = Assert.IsType<AboutViewModel>(main.CurrentPage);
        Assert.Contains("mit dem Kundenportal koppeln", about.ChangeStorageHint);

        main.ShowStorageChange();
        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        Assert.Contains("koppelst du diesen Rechner mit dem Kundenportal", setup.SwitchNote);
        Assert.True(setup.UsesPairing);
        Assert.False(setup.HasStorageChangeNote);

        await setup.RequestPairingCodeCommand.ExecuteAsync(null);
        portal.Stage = "fertig";
        portal.Access = PairingCipher.Encrypt(Access(), portal.PublicKey!, "K7QM-4TZP");
        await setup.CheckPairingAsync();

        // the same bucket, but the folder of the computer: the backups start there anew, and it is said
        Assert.True(setup.IsOtherStorage);
        Assert.Equal(Texts.OtherStorageNote, setup.StorageChangeNote);
        Capture(window, "26-kopplung-statt-einrichtungscode");

        setup.Password = Password;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);
        Assert.Equal("geraete/" + DeviceId, manager.Config.Storage!.Root);
        Assert.Equal("", manager.Config.Storage.Folder);
        Assert.Equal([SourceFolder], manager.Config.Folders.Select(folder => folder.Path));
        Assert.Null(Assert.Single(manager.Config.Folders).Destination);

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public void Without_the_portal_only_the_setup_code_is_offered()
    {
        var manager = new BackupManager(new ConfigStore(DataFolder), new MemorySecretStore(), FakeRestic());
        manager.AcceptTerms(Texts.TermsVersion);
        var main = new MainViewModel(manager, []);
        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        setup.ChooseModeCommand.Execute("Backup");
        setup.Sources.Add(SourceFolder);

        Assert.False(setup.CanPair);
        Assert.False(setup.ShowAccessChoice);
        Assert.True(setup.ShowSetupCodeFields);
        Assert.False(setup.ShowPairingFields);
        Assert.DoesNotContain("Kundenportal", setup.SwitchNote);
    }

    private (MainViewModel Main, BackupManager Manager) Start(FakePortal portal, ISecretStore secrets, TimeSpan? interval = null)
    {
        var manager = new BackupManager(new ConfigStore(DataFolder), secrets, FakeRestic());
        manager.AcceptTerms(Texts.TermsVersion);
        var pairing = new DevicePairing(
            new PortalClient(new HttpClient(portal), new PortalSettings("https://portal.example.test", "oeffentlich")), secrets, "windows");

        // without an interval the test asks the portal itself, step by step
        var main = new MainViewModel(manager, [], pairing) { PairingInterval = interval ?? Timeout.InfiniteTimeSpan };
        main.LoadStorageStatus = () => Task.FromResult<StorageStatus?>(null);
        main.FindBackupFolders = (_, _) => Task.FromResult<IReadOnlyList<string>>([]);
        main.PickFolder = _ => Task.FromResult<string?>(SourceFolder);
        return (main, manager);
    }

    /// <summary>
    /// Stands in for restic: says that the repository is there, and fails at anything
    /// else. It writes down which repository it was sent to.
    /// </summary>
    private string FakeRestic()
    {
        Directory.CreateDirectory(_root);
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(_root, "restic.cmd");
            File.WriteAllText(path, $"""
                @echo off
                echo %RESTIC_REPOSITORY%>>"{ResticLog}"
                if "%1"=="cat" exit /b 0
                exit /b 1
                """);
            return path;
        }

        var script = Path.Combine(_root, "restic");
        File.WriteAllText(script, $"""
            #!/bin/sh
            echo "$RESTIC_REPOSITORY" >> "{ResticLog}"
            [ "$1" = "cat" ] && exit 0
            exit 1

            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static byte[] Access() => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
    {
        ["v"] = 1,
        ["g"] = DeviceId,
        ["n"] = "Notebook Anna",
        ["b"] = "kunde-beispiel",
        ["r"] = "eu-central-2",
        ["k"] = "EXAMPLEACCESSKEY0001",
        ["s"] = "beispiel-geheim",
        ["p"] = $"geraete/{DeviceId}/",
        ["a"] = "https://status.example.test/v1",
        ["t"] = "beispiel-token",
    });

    private static async Task Until(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            Assert.True(waited < 5000, "The page did not get there in time.");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    // tall enough for the step of the storage, which lies below the fold
    private static void Capture(Window window, string name)
    {
        var height = window.Height;
        window.Height = 1250;
        ScreenCapture.Save(window, "screenshots", name);
        window.Height = height;
    }

    /// <summary>The three functions of the portal for pairing, as the test sets them.</summary>
    private sealed class FakePortal : HttpMessageHandler
    {
        private string? _secretHash;

        public string Stage { get; set; } = "wartet";

        public string? Access { get; set; }

        public string? Result { get; set; }

        public byte[]? PublicKey { get; private set; }

        public string? System { get; private set; }

        public int Fetched { get; private set; }

        public int Confirmed { get; private set; }

        /// <summary>Takes the place of the answers below when set.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage>? Answer { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Answer is { } answer)
            {
                return answer(request);
            }

            var body = JsonSerializer.Deserialize<Dictionary<string, string>>(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var function = request.RequestUri!.Segments[^1];
            if (function == "kopplung_beginnen")
            {
                PublicKey = Convert.FromBase64String(body["p_schluessel"]);
                _secretHash = body["p_geheimnis_hash"];
                System = body["p_system"];
                return Json(new { code = "K7QM-4TZP", gueltig_bis = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O") });
            }

            if (PairingCipher.SecretHash(Convert.FromBase64String(body["p_geheimnis"])) != _secretHash)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"Der Code passt nicht."}""") };
            }

            if (function == "kopplung_bestaetigen")
            {
                Confirmed++;
                return Json(new { stand = "bestaetigt" });
            }

            Fetched++;
            return Json(new
            {
                stand = Stage,
                zugang = Stage == "fertig" && Access is not null ? JsonNode.Parse(Access) : null,
                ergebnis = Stage == "fehler" ? Result : null,
            });
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
