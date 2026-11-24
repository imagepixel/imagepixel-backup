using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ImagepixelBackup.App.ViewModels;
using ImagepixelBackup.App.Views;
using ImagepixelBackup.Core;

namespace ImagepixelBackup.App.Tests;

/// <summary>
/// The two ways to the access data of the online storage at Wasabi: the setup code the
/// administrator hands out, which may limit the computer to a folder of its own, and the
/// access data of an own Wasabi account, typed in by hand. restic is played by a small
/// script that says yes and writes down where it was sent.
/// </summary>
public sealed class StorageAccessWalkthroughTests : IDisposable
{
    private const string Password = "sehr-geheim-123";

    private const string DeviceFolder = "geraete/imagepixel-backup-geraet-3f6c2a1e";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-access-" + Guid.NewGuid().ToString("N"));

    private string DataFolder => Path.Combine(_root, "Einstellungen");

    private string SourceFolder => Path.Combine(_root, "Dokumente");

    private string ResticLog => Path.Combine(_root, "restic.log");

    public StorageAccessWalkthroughTests()
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
    public async Task Customer_connects_an_own_Wasabi_account_by_hand()
    {
        var secrets = new MemorySecretStore();
        var (main, manager, lookedInto) = Start(secrets);
        var window = new MainWindow { DataContext = main };
        window.Show();

        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        setup.ChooseModeCommand.Execute("Backup");
        setup.Sources.Add(SourceFolder);

        // the setup code is the way chosen at first; the own account is one click away
        Assert.True(setup.ShowSetupCodeFields);
        Assert.False(setup.ShowOwnAccountFields);
        setup.ChooseAccessWayCommand.Execute("OwnAccount");
        Assert.True(setup.UseOwnAccount);
        Assert.False(setup.ShowSetupCodeFields);
        Assert.True(setup.ShowOwnAccountFields);
        Assert.Equal(ImagepixelStorage.DefaultRegion, setup.OwnRegion);
        Assert.False(setup.HasAccess);
        Capture(window, "30-eigenes-konto-leer");

        // what is missing or cannot be is said before anything is tried
        setup.Password = Password;
        setup.PasswordRepeat = Password;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(Texts.OwnBucketMissing, setup.ErrorMessage);

        setup.OwnBucket = "Meine Sicherung";
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(Texts.OwnBucketProblem, setup.ErrorMessage);

        setup.OwnBucket = " sicherung-buero ";
        setup.OwnRegion = "Europa";
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(Texts.OwnRegionProblem, setup.ErrorMessage);

        setup.OwnRegion = " EU-West-1 ";
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(Texts.OwnKeysMissing, setup.ErrorMessage);
        Assert.False(manager.IsConnected);

        setup.OwnAccessKeyId = " EIGENERSCHLUESSEL01 ";
        Assert.False(setup.HasAccess);
        setup.OwnSecretAccessKey = "eigenes-geheimnis";
        Assert.True(setup.HasAccess);
        Assert.Equal(("sicherung-buero", "EIGENERSCHLUESSEL01"), (setup.Bucket, setup.AccessKeyId));

        // the storage is looked into once the typing pauses, with the access data as typed
        await setup.Search;
        var (looked, secret) = Assert.Single(lookedInto);
        Assert.Equal(("sicherung-buero", "eu-west-1", "EIGENERSCHLUESSEL01", ""), (looked!.Bucket, looked.Region, looked.AccessKeyId, looked.Root));
        Assert.Equal("eigenes-geheimnis", secret);
        Capture(window, "31-eigenes-konto-ausgefuellt");

        // the setup code takes over only while it is chosen, and the typed data stay
        setup.ChooseAccessWayCommand.Execute("SetupCode");
        Assert.False(setup.HasAccess);
        Assert.Equal("", setup.Bucket);
        setup.ChooseAccessWayCommand.Execute("OwnAccount");
        Assert.True(setup.HasAccess);

        // restoring offers both ways as well
        setup.ChooseModeCommand.Execute("Restore");
        Assert.True(setup.ShowOwnAccountFields);
        setup.ChooseModeCommand.Execute("Backup");

        setup.Folder = "Notebook";
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);

        var storage = manager.Config.Storage!;
        Assert.Equal("s3:https://s3.eu-west-1.wasabisys.com/sicherung-buero/Notebook", storage.RepositoryUrl);
        Assert.Contains(storage.RepositoryUrl, File.ReadAllLines(ResticLog));
        Assert.Equal(("EIGENERSCHLUESSEL01", "", ""), (storage.AccessKeyId, storage.Root, storage.ApiUrl));
        Assert.Equal("eigenes-geheimnis", secrets.Get(SecretNames.SecretAccessKey));
        Assert.DoesNotContain("eigenes-geheimnis", File.ReadAllText(Path.Combine(DataFolder, "config.json")));

        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.Equal("Wasabi · sicherung-buero", dashboard.StorageName);

        window.Close();
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task A_setup_code_for_one_computer_keeps_every_backup_in_its_folder()
    {
        var secrets = new MemorySecretStore();
        var (main, manager, lookedInto) = Start(secrets);
        var window = new MainWindow { DataContext = main };
        window.Show();

        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        setup.ChooseModeCommand.Execute("Backup");
        setup.Sources.Add(SourceFolder);

        // the code as the control centre makes it for one device: limited to its folder, with its name
        setup.SetupCodeText = new SetupCode(
            "kunde-beispiel", "GERAETESCHLUESSEL01", "geraete-geheimnis", "eu-central-2",
            Root: DeviceFolder + "/", DeviceName: "Notebook Anna").Encode();
        Assert.True(setup.CodeAccepted);
        Assert.Equal(Texts.SetupCodeForDevice("Notebook Anna"), setup.CodeAcceptedText);
        Assert.True(setup.HasAccess);
        Capture(window, "32-einrichtungscode-geraet");

        // only the folder of the computer is looked into
        await setup.Search;
        Assert.Equal(DeviceFolder, Assert.Single(lookedInto).Storage!.Root);

        setup.Password = Password;
        setup.PasswordRepeat = Password;
        await setup.ConnectCommand.ExecuteAsync(null);
        Assert.Null(setup.ErrorMessage);

        var storage = manager.Config.Storage!;
        Assert.Equal((DeviceFolder, "Notebook Anna"), (storage.Root, storage.DeviceName));
        Assert.Equal($"s3:https://s3.eu-central-2.wasabisys.com/kunde-beispiel/{DeviceFolder}", storage.RepositoryUrl);
        Assert.Contains(storage.RepositoryUrl, File.ReadAllLines(ResticLog));
        Assert.Equal("geraete-geheimnis", secrets.Get(SecretNames.SecretAccessKey));

        var dashboard = Assert.IsType<DashboardViewModel>(main.CurrentPage);
        Assert.Equal("Wasabi · kunde-beispiel · Notebook Anna", dashboard.StorageName);

        window.Close();
        await main.ShutdownAsync();
    }

    [Theory]
    [InlineData("Fatal: unable to open config file: Stat: The AWS Access Key Id you provided does not exist in our records. (InvalidAccessKeyId)")]
    [InlineData("Fatal: unable to open repository: Access Denied.")]
    public void A_locked_access_is_said_plainly(string error)
    {
        Assert.True(Texts.IsAccessProblem(error));
        Assert.Contains("gesperrt", Texts.Error(new Exception(error)));
        Assert.Contains("gesperrt", Texts.AccessProblem);
    }

    private (MainViewModel Main, BackupManager Manager, List<(StorageSettings? Storage, string? Secret)> LookedInto) Start(ISecretStore secrets)
    {
        var manager = new BackupManager(new ConfigStore(DataFolder), secrets, FakeRestic());
        manager.AcceptTerms(Texts.TermsVersion);
        var lookedInto = new List<(StorageSettings? Storage, string? Secret)>();
        var main = new MainViewModel(manager, []);
        main.LoadStorageStatus = () => Task.FromResult<StorageStatus?>(null);
        main.FindBackupFolders = (storage, secret) =>
        {
            lookedInto.Add((storage, secret));
            return Task.FromResult<IReadOnlyList<string>>([]);
        };
        main.PickFolder = _ => Task.FromResult<string?>(SourceFolder);
        return (main, manager, lookedInto);
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

    // tall enough for the step of the storage, which lies below the fold
    private static void Capture(Window window, string name)
    {
        var height = window.Height;
        window.Height = 1250;
        ScreenCapture.Save(window, "screenshots", name);
        window.Height = height;
    }
}
