using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.Core.Tests;

/// <summary>The view of the storage as it is, with the encrypted files of the backup.</summary>
public sealed class EncryptedViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-test-" + Guid.NewGuid().ToString("N"));

    private static readonly StorageSettings Storage = new()
    {
        Kind = StorageKind.ImagepixelStorage,
        Bucket = "kunde-4711",
        AccessKeyId = "ACCESSKEY",
        Region = "eu-central-2",
    };

    private static bool CanMount => OperatingSystem.IsWindows()
        ? Directory.Exists(@"C:\Program Files (x86)\WinFsp")
        : OperatingSystem.IsLinux() && File.Exists("/dev/fuse");

    [Fact]
    public void Rclone_gets_the_access_data_through_its_environment_and_shows_a_destination_read_only()
    {
        var startInfo = EncryptedView.CreateStartInfo("rclone", Storage, "ganz-geheim", "Notebook", "S:", "Imagepixel Backup (verschlüsselt)");
        var arguments = startInfo.ArgumentList.ToList();

        Assert.Contains("imagepixel:kunde-4711/Notebook", arguments);
        Assert.Contains("--read-only", arguments);
        Assert.DoesNotContain("--exclude", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("ganz-geheim") || argument.Contains("ACCESSKEY"));

        Assert.Equal("s3", startInfo.Environment["RCLONE_CONFIG_IMAGEPIXEL_TYPE"]);
        Assert.Equal("Wasabi", startInfo.Environment["RCLONE_CONFIG_IMAGEPIXEL_PROVIDER"]);
        Assert.Equal("s3.eu-central-2.wasabisys.com", startInfo.Environment["RCLONE_CONFIG_IMAGEPIXEL_ENDPOINT"]);
        Assert.Equal("ACCESSKEY", startInfo.Environment["RCLONE_CONFIG_IMAGEPIXEL_ACCESS_KEY_ID"]);
        Assert.Equal("ganz-geheim", startInfo.Environment["RCLONE_CONFIG_IMAGEPIXEL_SECRET_ACCESS_KEY"]);

        // no settings file of the user is read or written
        Assert.False(File.Exists(startInfo.Environment["RCLONE_CONFIG"]));
    }

    [Fact]
    public void At_the_top_of_the_bucket_the_files_of_Imagepixel_are_left_out()
    {
        var arguments = EncryptedView.CreateStartInfo("rclone", Storage, "geheim", "", "S:", "Backup").ArgumentList.ToList();

        Assert.Contains("imagepixel:kunde-4711", arguments);
        var exclude = arguments.IndexOf("--exclude");
        Assert.True(exclude >= 0);
        Assert.Equal("/imagepixel/**", arguments[exclude + 1]);
    }

    [SkippableFact]
    public void The_rclone_of_the_application_is_found()
    {
        var rclone = EncryptedView.FindRclone();
        Skip.If(rclone is null, "rclone has not been fetched");

        Assert.True(File.Exists(rclone));
    }

    /// <summary>
    /// rclone and the drive of the computer for real, with a folder in place of the
    /// storage: the files appear, nothing can be written, and the drive goes again.
    /// </summary>
    [SkippableFact]
    public async Task A_folder_is_shown_read_only_and_the_drive_goes_again()
    {
        var rclone = EncryptedView.FindRclone();
        Skip.If(rclone is null, "rclone has not been fetched");
        Skip.IfNot(CanMount, "the computer cannot show drives (WinFsp or FUSE missing)");

        var folder = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(folder, "data", "3f"));
        File.WriteAllBytes(Path.Combine(folder, "data", "3f", "3f9a0c"), [0x8c, 0x01, 0xfe, 0x42]);
        File.WriteAllText(Path.Combine(folder, "config"), "verschlüsselt");

        var mountpoint = OperatingSystem.IsWindows() ? MountSession.FreeDriveLetters().Last() : Path.Combine(_root, "ansicht");
        var startInfo = EncryptedView.CreateStartInfoForFolder(rclone!, folder, mountpoint, "Imagepixel Backup (verschlüsselt)");
        var session = await MountSession.StartViewAsync(startInfo, mountpoint, CancellationToken.None);
        try
        {
            var root = session.RootPath;
            Assert.Equal([0x8c, 0x01, 0xfe, 0x42], File.ReadAllBytes(Path.Combine(root, "data", "3f", "3f9a0c")));
            Assert.Equal("verschlüsselt", File.ReadAllText(Path.Combine(root, "config")));

            // read-only: nothing can be added, changed or deleted through the view. Windows
            // reports no error for an attempt to delete; what counts is that nothing goes.
            Assert.ThrowsAny<Exception>(() => File.WriteAllText(Path.Combine(root, "neu.txt"), "x"));
            Assert.ThrowsAny<Exception>(() => File.WriteAllText(Path.Combine(root, "config"), "geändert"));
            TryDelete(Path.Combine(root, "config"));
            Assert.Equal("verschlüsselt", File.ReadAllText(Path.Combine(folder, "config")));
            Assert.True(File.Exists(Path.Combine(root, "config")));
            Assert.False(File.Exists(Path.Combine(folder, "neu.txt")));
        }
        finally
        {
            await session.StopAsync();
        }

        Assert.False(session.IsRunning);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(Directory.Exists(mountpoint + Path.DirectorySeparatorChar));
        }
        else
        {
            Assert.False(MountSession.IsMounted(mountpoint));
        }
    }

    /// <summary>
    /// The encrypted view of a real storage. Runs only where a bucket for tests is named in
    /// the environment (IPB_TEST_BUCKET, IPB_TEST_ACCESS_KEY_ID, IPB_TEST_SECRET_ACCESS_KEY,
    /// IPB_TEST_REGION) and the computer can show drives. It looks into the backup the
    /// other tests keep in the folder "Test Büro Notebook" and changes nothing.
    /// </summary>
    [SkippableFact]
    public async Task The_files_of_a_real_storage_are_shown_as_they_lie_there()
    {
        var restic = ResticClient.FindRestic();
        Skip.If(restic is null || EncryptedView.FindRclone() is null, "restic or rclone is missing");
        Skip.IfNot(CanMount, "the computer cannot show drives (WinFsp or FUSE missing)");

        var bucket = Environment.GetEnvironmentVariable("IPB_TEST_BUCKET");
        var keyId = Environment.GetEnvironmentVariable("IPB_TEST_ACCESS_KEY_ID");
        var secret = Environment.GetEnvironmentVariable("IPB_TEST_SECRET_ACCESS_KEY");
        Skip.If(
            string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(secret),
            "no storage for tests is named in the environment");

        var storage = new StorageSettings
        {
            Kind = StorageKind.ImagepixelStorage,
            Bucket = bucket!,
            AccessKeyId = keyId!,
            Region = Environment.GetEnvironmentVariable("IPB_TEST_REGION") is { Length: > 0 } region ? region : ImagepixelStorage.DefaultRegion,
            Folder = "Test Büro Notebook",
        };
        var manager = new BackupManager(new ConfigStore(Path.Combine(_root, "settings")), new MemorySecretStore(), restic!);
        Assert.Equal(RepositoryState.Ready, await manager.ConnectAsync(storage, "nur-ein-test-passwort", secret, createIfMissing: true));

        var letter = OperatingSystem.IsWindows() ? MountSession.FreeDriveLetters().Last() : null;
        var session = await manager.MountEncryptedAsync("Imagepixel Backup (verschlüsselt)", driveLetter: letter);
        try
        {
            // the parts of a restic backup, and its settings file is not readable text
            var root = session.RootPath;
            foreach (var part in new[] { "data", "index", "keys", "snapshots" })
            {
                Assert.True(Directory.Exists(Path.Combine(root, part)), part);
            }

            var config = File.ReadAllBytes(Path.Combine(root, "config"));
            Assert.NotEmpty(config);
            Assert.NotEqual((byte)'{', config[0]);
            Assert.DoesNotContain("chunker_polynomial", System.Text.Encoding.UTF8.GetString(config));
        }
        finally
        {
            await session.StopAsync();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // refused, as it should be
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
