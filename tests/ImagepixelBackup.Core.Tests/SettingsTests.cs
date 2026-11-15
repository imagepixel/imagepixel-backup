namespace ImagepixelBackup.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Setup_code_round_trips()
    {
        var code = new SetupCode("kunde-4711", "ACCESSKEYID123456789", "secret/with+special=chars", "eu-central-2");

        Assert.True(SetupCode.TryDecode("  " + code.Encode() + "\r\n", out var decoded));
        Assert.Equal(code, decoded);
        Assert.StartsWith("IPB1-", code.Encode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kunde-4711")]
    [InlineData("IPB1-not-base64!")]
    [InlineData("IPB1-e30")] // an empty JSON object
    public void Invalid_setup_codes_are_rejected(string? text)
    {
        Assert.False(SetupCode.TryDecode(text, out _));
    }

    [Fact]
    public void Repository_url_uses_the_endpoint_of_the_region()
    {
        var standard = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "b" };
        var amsterdam = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "b", Region = "eu-central-1" };
        var virginia = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "b", Region = "us-east-1" };
        var folder = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = @"E:\Backup" };

        Assert.Equal("s3:https://s3.eu-central-2.wasabisys.com/b", standard.RepositoryUrl);
        Assert.Equal("s3:https://s3.eu-central-1.wasabisys.com/b", amsterdam.RepositoryUrl);
        Assert.Equal("s3:https://s3.wasabisys.com/b", virginia.RepositoryUrl);
        Assert.Equal(@"E:\Backup", folder.RepositoryUrl);
    }

    [SkippableTheory]
    [InlineData(@"C:\Daten", @"C:\Daten", true)]
    [InlineData(@"C:\Daten", @"C:\Daten\Backup", true)]
    [InlineData(@"C:\Daten\Backup\", @"c:\daten", true)]
    [InlineData(@"C:\Daten", @"C:\Daten alt", false)]
    [InlineData(@"C:\Daten", @"D:\Daten", false)]
    [InlineData(@"C:\Daten", "", false)]
    public void Folders_overlap_when_one_is_or_contains_the_other(string first, string second, bool expected)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the paths are Windows paths");

        Assert.Equal(expected, FolderRules.Overlap(first, second));
        Assert.Equal(expected, FolderRules.Overlap(second, first));
    }

    [SkippableTheory]
    [InlineData("/Users/anna/Daten", "/Users/anna/Daten", true)]
    [InlineData("/Users/anna/Daten", "/Users/anna/Daten/Backup", true)]
    [InlineData("/Volumes/Backup/", "/Volumes/Backup/Daten", true)]
    [InlineData("/Users/anna/Daten", "/Users/anna/Daten alt", false)]
    [InlineData("/Users/anna/Daten", "/Volumes/Backup", false)]
    [InlineData("/Users/anna/Daten", "", false)]
    public void Folders_overlap_on_macOS_and_Linux_too(string first, string second, bool expected)
    {
        Skip.If(OperatingSystem.IsWindows(), "the paths are not Windows paths");

        Assert.Equal(expected, FolderRules.Overlap(first, second));
        Assert.Equal(expected, FolderRules.Overlap(second, first));
    }

    [Fact]
    public void Only_Linux_tells_folders_apart_by_upper_and_lower_case()
    {
        var root = Path.GetTempPath();

        Assert.Equal(
            !OperatingSystem.IsLinux(),
            FolderRules.Overlap(Path.Combine(root, "Daten"), Path.Combine(root, "daten", "Backup")));
    }

    [Fact]
    public void A_folder_that_holds_the_backup_cannot_be_backed_up()
    {
        var root = Path.Combine(Path.GetTempPath(), "ipb-test-" + Guid.NewGuid().ToString("N"));
        var manager = new BackupManager(new ConfigStore(Path.Combine(root, "settings")), new MemorySecretStore(), "restic");
        manager.Config.Storage = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Path.Combine(root, "Dokumente", "Backup") };

        var error = Assert.Throws<FolderOverlapException>(() => manager.AddFolder(Path.Combine(root, "Dokumente")));

        Assert.Equal(Path.Combine(root, "Dokumente"), error.Folder);
        Assert.Empty(manager.Config.Folders);
        Assert.Throws<FolderOverlapException>(() => manager.AddFolder(Path.Combine(root, "Dokumente", "Backup", "data")));

        // a folder next to the backup is fine, and so is any folder with online storage
        manager.Config.Folders.Clear();
        manager.Config.Storage = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde" };
        Assert.Equal(Path.Combine(root, "Dokumente"), manager.AddFolder(Path.Combine(root, "Dokumente")).Path);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task A_backup_cannot_be_moved_into_a_folder_that_is_backed_up()
    {
        var root = Path.Combine(Path.GetTempPath(), "ipb-test-" + Guid.NewGuid().ToString("N"));
        var manager = new BackupManager(new ConfigStore(Path.Combine(root, "settings")), new MemorySecretStore(), "restic");
        manager.AddFolder(Path.Combine(root, "Dokumente"));

        var inside = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Path.Combine(root, "Dokumente", "Backup") };
        await Assert.ThrowsAsync<FolderOverlapException>(() => manager.ConnectAsync(inside, "password", null, createIfMissing: true));

        Assert.Null(manager.Config.Storage);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Access_data_is_passed_through_the_environment()
    {
        var storage = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde", AccessKeyId = "KEY" };
        var environment = new Dictionary<string, string?> { ["RESTIC_PASSWORD_FILE"] = "stale" };

        RepositoryAccess.From(storage, "pw", "secret").ApplyTo(environment);

        Assert.Equal("s3:https://s3.eu-central-2.wasabisys.com/kunde", environment["RESTIC_REPOSITORY"]);
        Assert.Equal("pw", environment["RESTIC_PASSWORD"]);
        Assert.Equal("KEY", environment["AWS_ACCESS_KEY_ID"]);
        Assert.Equal("secret", environment["AWS_SECRET_ACCESS_KEY"]);
        Assert.False(environment.ContainsKey("RESTIC_PASSWORD_FILE"));
    }

    [Fact]
    public void Config_round_trips_and_keeps_secrets_out_of_the_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipb-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(directory);
            Assert.Null(store.Load().Storage);

            store.Save(new AppConfig
            {
                Storage = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "b", AccessKeyId = "KEY" },
                Folders = { new BackupFolder { Path = @"C:\Daten", Schedule = ScheduleKind.Daily, DailyAt = new TimeOnly(18, 30) } },
            });

            var loaded = store.Load();
            Assert.Equal(StorageKind.ImagepixelStorage, loaded.Storage!.Kind);
            Assert.Equal(new TimeOnly(18, 30), loaded.Folders.Single().DailyAt);

            if (OperatingSystem.IsWindows())
            {
                var secrets = new DpapiSecretStore(directory);
                secrets.Set(SecretNames.RepositoryPassword, "geheim-äöü");
                Assert.Equal("geheim-äöü", new DpapiSecretStore(directory).Get(SecretNames.RepositoryPassword));

                foreach (var file in Directory.GetFiles(directory))
                {
                    Assert.DoesNotContain("geheim", File.ReadAllText(file));
                }

                secrets.Set(SecretNames.RepositoryPassword, null);
                Assert.Null(secrets.Get(SecretNames.RepositoryPassword));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
