using System.Net;
using System.Text;
using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.Core.Tests;

/// <summary>The folder inside an Imagepixel Storage that a backup is kept in.</summary>
public sealed class StorageFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-test-" + Guid.NewGuid().ToString("N"));

    private static readonly StorageSettings Storage = new()
    {
        Kind = StorageKind.ImagepixelStorage,
        Bucket = "kunde-4711",
        AccessKeyId = "ACCESSKEY",
    };

    /// <summary>Answers requests the way the test says and remembers what was asked.</summary>
    private sealed class FakeNetwork(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!.AbsolutePath + request.RequestUri.Query);
            return Task.FromResult(answer(request));
        }
    }

    // what the storage answers when asked for the folders at its top
    private const string Folders = """
        <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
          <IsTruncated>false</IsTruncated>
          <Contents><Key>config</Key><Size>155</Size></Contents>
          <CommonPrefixes><Prefix>Büro/</Prefix></CommonPrefixes>
          <CommonPrefixes><Prefix>Fotos/</Prefix></CommonPrefixes>
          <CommonPrefixes><Prefix>Notebook/</Prefix></CommonPrefixes>
          <CommonPrefixes><Prefix>data/</Prefix></CommonPrefixes>
          <CommonPrefixes><Prefix>imagepixel/</Prefix></CommonPrefixes>
          <CommonPrefixes><Prefix>keys/</Prefix></CommonPrefixes>
        </ListBucketResult>
        """;

    /// <summary>A storage with a backup at its top, one each in "Büro" and "Notebook", and a folder that holds none.</summary>
    private static FakeNetwork StorageWithBackups() => new(request =>
    {
        if (request.RequestUri!.Query.Contains("list-type"))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Folders, Encoding.UTF8, "application/xml") };
        }

        return request.RequestUri.AbsolutePath is "/kunde-4711/config" or "/kunde-4711/Notebook/config" or "/kunde-4711/B%C3%BCro/config"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("config") }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    [Theory]
    [InlineData("Notebook", "Notebook")]
    [InlineData("  Notebook  ", "Notebook")]
    [InlineData("/Büro/ Notebook Anna /", "Büro/Notebook Anna")]
    [InlineData(@"Büro\Notebook", "Büro/Notebook")]
    [InlineData("Büro//Notebook", "Büro/Notebook")]
    [InlineData("Bu\u0308ro", "B\u00fcro")] // a Mac writes the dots of an umlaut as a sign of their own
    [InlineData(" / ", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void What_was_typed_is_brought_into_one_form(string? typed, string expected)
    {
        Assert.Equal(expected, StorageFolder.Normalize(typed));
    }

    [Theory]
    [InlineData("", StorageFolderProblem.None)]
    [InlineData("Notebook", StorageFolderProblem.None)]
    [InlineData("Büro/Notebook-2_alt v1.5", StorageFolderProblem.None)]
    [InlineData("Kunde/data", StorageFolderProblem.None)] // only the top of the storage is taken
    [InlineData("Notebook?", StorageFolderProblem.Characters)]
    [InlineData("a%20b", StorageFolderProblem.Characters)]
    [InlineData("a#b", StorageFolderProblem.Characters)]
    [InlineData("C:", StorageFolderProblem.Characters)]
    [InlineData("a*", StorageFolderProblem.Characters)]
    [InlineData("..", StorageFolderProblem.Characters)]
    [InlineData("Büro/../Notebook", StorageFolderProblem.Characters)]
    [InlineData("imagepixel", StorageFolderProblem.Reserved)]
    [InlineData("Data", StorageFolderProblem.Reserved)]
    [InlineData("config/Notebook", StorageFolderProblem.Reserved)]
    [InlineData("snapshots", StorageFolderProblem.Reserved)]
    public void Names_the_storage_cannot_keep_apart_are_turned_down(string folder, StorageFolderProblem expected)
    {
        Assert.Equal(expected, StorageFolder.Check(folder));
    }

    [Fact]
    public void A_name_can_be_too_long()
    {
        Assert.Equal(StorageFolderProblem.None, StorageFolder.Check(new string('a', StorageFolder.MaxLength)));
        Assert.Equal(StorageFolderProblem.TooLong, StorageFolder.Check(new string('a', StorageFolder.MaxLength + 1)));
    }

    [Fact]
    public void The_folder_is_part_of_where_the_backup_lies()
    {
        var without = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde-4711", AccessKeyId = "KEY", ApiUrl = "https://portal.example.test" };
        var with = without.WithFolder("Büro/Notebook");

        Assert.Equal("s3:https://s3.eu-central-2.wasabisys.com/kunde-4711", without.RepositoryUrl);
        Assert.Equal("s3:https://s3.eu-central-2.wasabisys.com/kunde-4711/Büro/Notebook", with.RepositoryUrl);

        // the same storage with the same access otherwise
        Assert.Equal((without.Kind, without.Bucket, without.Region, without.AccessKeyId, without.ApiUrl),
            (with.Kind, with.Bucket, with.Region, with.AccessKeyId, with.ApiUrl));
        Assert.Equal("", without.Folder);

        // restic gets the folder through its environment like the rest
        var environment = new Dictionary<string, string?>();
        RepositoryAccess.From(with, "pw", "secret").ApplyTo(environment);
        Assert.Equal("s3:https://s3.eu-central-2.wasabisys.com/kunde-4711/Büro/Notebook", environment["RESTIC_REPOSITORY"]);
    }

    [Fact]
    public void Settings_from_before_folders_existed_keep_pointing_at_the_same_backup()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "config.json"), """
            {
              "Storage": { "Kind": "ImagepixelStorage", "Bucket": "kunde-4711", "Region": "eu-central-2", "AccessKeyId": "KEY", "LocalPath": "", "ApiUrl": "" },
              "Folders": []
            }
            """);

        var storage = new ConfigStore(_root).Load().Storage!;

        Assert.Equal("", storage.Folder);
        Assert.Equal("s3:https://s3.eu-central-2.wasabisys.com/kunde-4711", storage.RepositoryUrl);

        // and a folder survives saving and loading
        var store = new ConfigStore(_root);
        store.Save(new AppConfig { Storage = storage.WithFolder("Notebook") });
        Assert.Equal("Notebook", store.Load().Storage!.Folder);
    }

    [Fact]
    public async Task The_backups_of_a_storage_are_found()
    {
        var network = StorageWithBackups();

        var found = await new BackupFinder(new HttpClient(network)).FindAsync(Storage, "secret");

        // the one at the top has no folder; "Fotos" holds something else
        Assert.Equal(["", "Büro", "Notebook"], found);

        // the folders are asked for in one request, and only those that could be a backup are looked into
        Assert.Contains("/kunde-4711?delimiter=%2F&list-type=2", network.Asked);
        Assert.Contains("/kunde-4711/Fotos/config", network.Asked);
        Assert.DoesNotContain(network.Asked, path => path.Contains("data/") || path.Contains("imagepixel/") || path.Contains("keys/"));
    }

    [Fact]
    public async Task Nothing_is_found_where_the_storage_cannot_be_read()
    {
        var denied = new FakeNetwork(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        Assert.Empty(await new BackupFinder(new HttpClient(denied)).FindAsync(Storage, "secret"));

        var offline = new FakeNetwork(_ => throw new HttpRequestException("no route to host"));
        Assert.Empty(await new BackupFinder(new HttpClient(offline)).FindAsync(Storage, "secret"));

        // a folder on a disk is not looked into this way, and without the key nothing is asked
        var network = StorageWithBackups();
        var finder = new BackupFinder(new HttpClient(network));
        Assert.Empty(await finder.FindAsync(new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = @"E:\Backup" }, null));
        Assert.Empty(await finder.FindAsync(Storage, null));
        Assert.Empty(network.Asked);
    }

    [Fact]
    public async Task The_storage_that_is_connected_is_looked_into_with_the_key_the_application_holds()
    {
        var network = StorageWithBackups();
        var secrets = new MemorySecretStore();
        var manager = new BackupManager(
            new ConfigStore(_root), secrets, "restic", null, null, new BackupFinder(new HttpClient(network)));

        // nothing is connected yet
        Assert.Empty(await manager.FindBackupFoldersAsync());

        manager.Config.Storage = Storage.WithFolder("Notebook");
        secrets.Set(SecretNames.SecretAccessKey, "secret");
        Assert.Equal(["", "Büro", "Notebook"], await manager.FindBackupFoldersAsync());

        // moving to another folder is for Imagepixel Storage only
        manager.Config.Storage = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Path.Combine(_root, "repo") };
        Assert.Empty(await manager.FindBackupFoldersAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConnectToFolderAsync("Notebook", "password", createIfMissing: false));
    }

    [SkippableFact]
    public async Task At_another_place_the_folders_start_anew()
    {
        var restic = ResticClient.FindRestic();
        Skip.If(restic is null, "restic has not been built");

        var documents = Path.Combine(_root, "Dokumente");
        Directory.CreateDirectory(documents);
        File.WriteAllText(Path.Combine(documents, "Brief.txt"), "Sehr geehrte Damen und Herren");

        var first = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Path.Combine(_root, "Erste Festplatte") };
        var second = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Path.Combine(_root, "Zweite Festplatte") };
        var manager = new BackupManager(new ConfigStore(Path.Combine(_root, "settings")), new MemorySecretStore(), restic!);
        Assert.Equal(RepositoryState.Ready, await manager.ConnectAsync(first, "password", null, createIfMissing: true));

        var folder = manager.AddFolder(documents);
        folder.Schedule = ScheduleKind.Daily;
        folder.Exclusions.Presets.Add("temporary");
        folder.ExclusionsDecided = true;
        await manager.BackupAsync(folder);
        Assert.NotNull(folder.LastSuccess);

        // opening the same place again changes nothing
        Assert.Equal(RepositoryState.Ready, await manager.ConnectAsync(first, "password", null, createIfMissing: false));
        Assert.Same(folder, Assert.Single(manager.Config.Folders));

        // a place that could not be opened changes nothing either
        Assert.Equal(RepositoryState.NotFound, await manager.ConnectAsync(second, "password", null, createIfMissing: false));
        Assert.Same(folder, Assert.Single(manager.Config.Folders));

        // at another place nothing has been saved yet: the folder is due at once, set up as before
        Assert.Equal(RepositoryState.Ready, await manager.ConnectAsync(second, "password", null, createIfMissing: true));
        var moved = Assert.Single(manager.Config.Folders);
        Assert.NotSame(folder, moved);
        Assert.Equal(documents, moved.Path);
        Assert.Null(moved.LastAttempt);
        Assert.Null(moved.LastSuccess);
        Assert.Equal(ScheduleKind.Daily, moved.Schedule);
        Assert.Equal(["temporary"], moved.Exclusions.Presets);
        Assert.True(moved.ExclusionsDecided);
        Assert.True(BackupSchedule.IsDue(moved, DateTimeOffset.Now));

        // and it is saved that way
        var saved = Assert.Single(new ConfigStore(Path.Combine(_root, "settings")).Load().Folders);
        Assert.Null(saved.LastSuccess);

        // the backup at the first place is untouched
        Assert.True(File.Exists(Path.Combine(first.LocalPath, "config")));
        Assert.Single(await new ResticClient(restic!, RepositoryAccess.From(first, "password", null)).GetSnapshotsAsync());
    }

    /// <summary>
    /// Folders against a real storage, with real names: an umlaut, a space, a folder
    /// inside a folder. Runs only where a bucket for tests is named in the environment:
    /// IPB_TEST_BUCKET, IPB_TEST_ACCESS_KEY_ID, IPB_TEST_SECRET_ACCESS_KEY and, unless it
    /// is the usual one, IPB_TEST_REGION. It leaves two small backups in that bucket,
    /// in the folders named here, and uses them again the next time.
    /// </summary>
    [SkippableFact]
    public async Task Folders_work_in_a_real_storage()
    {
        var restic = ResticClient.FindRestic();
        Skip.If(restic is null, "restic has not been built");

        var bucket = Environment.GetEnvironmentVariable("IPB_TEST_BUCKET");
        var keyId = Environment.GetEnvironmentVariable("IPB_TEST_ACCESS_KEY_ID");
        var secret = Environment.GetEnvironmentVariable("IPB_TEST_SECRET_ACCESS_KEY");
        Skip.If(
            string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(secret),
            "no storage for tests is named in the environment");

        const string password = "nur-ein-test-passwort";
        const string notebook = "Test Büro Notebook";
        const string second = "Test Büro/Zweiter PC";
        var storage = new StorageSettings
        {
            Kind = StorageKind.ImagepixelStorage,
            Bucket = bucket!,
            AccessKeyId = keyId!,
            Region = Environment.GetEnvironmentVariable("IPB_TEST_REGION") is { Length: > 0 } region ? region : ImagepixelStorage.DefaultRegion,
        };

        var documents = Path.Combine(_root, "Dokumente");
        Directory.CreateDirectory(documents);
        File.WriteAllText(Path.Combine(documents, "Brief.txt"), "geschrieben " + Guid.NewGuid());

        var manager = new BackupManager(new ConfigStore(Path.Combine(_root, "settings")), new MemorySecretStore(), restic!);

        // a backup in a folder of the storage
        Assert.Equal(RepositoryState.Ready, await manager.ConnectAsync(storage.WithFolder(notebook), password, secret, createIfMissing: true));
        var folder = manager.AddFolder(documents);
        var saved = (await manager.BackupAsync(folder)).SnapshotId;
        Assert.Contains(await manager.GetSnapshotsAsync(), snapshot => snapshot.Id == saved);

        // it is found, by the name of its folder; a folder that only holds other folders is not one
        var found = await manager.FindBackupFoldersAsync();
        Assert.Contains(notebook, found);
        Assert.DoesNotContain("Test Büro", found);

        // another folder is another backup: it does not hold what was just saved, and the folder is due there
        Assert.Equal(RepositoryState.Ready, await manager.ConnectToFolderAsync(second, password, createIfMissing: true));
        Assert.Equal(second, manager.Config.Storage!.Folder);
        Assert.DoesNotContain(await manager.GetSnapshotsAsync(), snapshot => snapshot.Id == saved);
        Assert.Null(Assert.Single(manager.Config.Folders).LastSuccess);

        // a password that does not open a folder leaves the application where it is
        Assert.Equal(RepositoryState.WrongPassword, await manager.ConnectToFolderAsync(notebook, "falsches-passwort", createIfMissing: false));
        Assert.Equal(second, manager.Config.Storage.Folder);

        // and back: the first backup is as it was left
        Assert.Equal(RepositoryState.Ready, await manager.ConnectToFolderAsync(notebook, password, createIfMissing: false));
        Assert.Contains(await manager.GetSnapshotsAsync(), snapshot => snapshot.Id == saved);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
