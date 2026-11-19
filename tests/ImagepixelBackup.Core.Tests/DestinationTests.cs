using System.Net;
using System.Text;
using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.Core.Tests;

/// <summary>Each folder that is backed up has its own destination in the storage, and old backups are cleaned up.</summary>
public sealed class DestinationTests : IDisposable
{
    private const string Password = "sehr-geheim-123";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-test-" + Guid.NewGuid().ToString("N"));
    private readonly string? _restic = ResticClient.FindRestic();

    public DestinationTests()
    {
        Directory.CreateDirectory(Documents);
        File.WriteAllText(Path.Combine(Documents, "Brief.txt"), "Sehr geehrte Damen und Herren äöü");
        Directory.CreateDirectory(Photos);
        File.WriteAllText(Path.Combine(Photos, "Urlaub.jpg"), "ein Bild");
    }

    private string Documents => Path.Combine(_root, "Dokumente");

    private string Photos => Path.Combine(_root, "Fotos");

    private string Disk => Path.Combine(_root, "USB-Festplatte", "Backup");

    /// <summary>A computer that backs up two folders to a folder on a disk, both directly into it.</summary>
    private async Task<(BackupManager Manager, BackupFolder Documents, BackupFolder Photos)> SetUpAsync()
    {
        var manager = new BackupManager(new ConfigStore(Path.Combine(_root, "settings")), new MemorySecretStore(), _restic!);
        var storage = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Disk };
        Assert.Equal(RepositoryState.Ready, await manager.ConnectAsync(storage, Password, null, createIfMissing: true));
        var documents = manager.AddFolder(Documents);
        var photos = manager.AddFolder(Photos);
        documents.Schedule = photos.Schedule = ScheduleKind.Manual;
        manager.Save();
        await manager.BackupAsync(documents);
        await manager.BackupAsync(photos);
        return (manager, documents, photos);
    }

    private static async Task<IReadOnlyList<Snapshot>> SnapshotsAt(string restic, string repository) =>
        await new ResticClient(restic, new RepositoryAccess(repository, Password)).GetSnapshotsAsync();

    [SkippableFact]
    public async Task Each_folder_backs_up_to_its_own_destination()
    {
        Skip.If(_restic is null, "restic has not been built");
        var (manager, documents, photos) = await SetUpAsync();
        Assert.Equal("", manager.DestinationOf(documents));
        Assert.Equal(["" ], manager.KnownDestinations);

        await manager.ChangeDestinationAsync(documents, " Notebook/ ", deleteOldAfterDays: null);

        // the new destination is a backup of its own, created with the same password
        Assert.Equal("Notebook", documents.Destination);
        Assert.True(File.Exists(Path.Combine(Disk, "Notebook", "config")));
        Assert.Null(documents.LastSuccess);
        Assert.Empty(manager.Config.Cleanups);
        Assert.Equal("", manager.DestinationOf(photos));
        Assert.Equal(["", "Notebook"], manager.KnownDestinations);
        Assert.Equal("Notebook", new ConfigStore(Path.Combine(_root, "settings")).Load().Folders[0].Destination);

        // the next backup of the folder lands there, the other folder stays where it was
        File.WriteAllText(Path.Combine(Documents, "Brief.txt"), "die neue Fassung");
        await manager.BackupAsync(documents);
        await manager.BackupAsync(photos);
        Assert.Single(await SnapshotsAt(_restic!, Path.Combine(Disk, "Notebook")));
        Assert.Equal(3, (await SnapshotsAt(_restic!, Disk)).Count);

        // the list of saved states covers both places and says where each lies
        var all = await manager.GetSnapshotsAsync();
        Assert.Equal(4, all.Count);
        var latestOfDocuments = all.Last(snapshot => snapshot.Paths.Any(path => FolderRules.IsSameFolder(path, Documents)));
        Assert.Equal("Notebook", latestOfDocuments.Destination);
        Assert.Equal(3, all.Count(snapshot => snapshot.Destination == ""));

        // a state is restored from where it lies
        var target = Path.Combine(_root, "Wiederhergestellt");
        await manager.RestoreAsync(latestOfDocuments.Id, target, destination: latestOfDocuments.Destination);
        Assert.Equal("die neue Fassung", File.ReadAllText(Path.Combine(target, "Dokumente", "Brief.txt")));
    }

    [SkippableFact]
    public async Task A_destination_whose_backup_has_another_password_is_refused()
    {
        Skip.If(_restic is null, "restic has not been built");
        var (manager, documents, _) = await SetUpAsync();
        await new ResticClient(_restic!, new RepositoryAccess(Path.Combine(Disk, "Fremd"), "ein-anderes-passwort")).InitAsync();

        var error = await Assert.ThrowsAsync<DestinationPasswordException>(
            () => manager.ChangeDestinationAsync(documents, "Fremd", deleteOldAfterDays: 7));

        Assert.Equal("Fremd", error.Destination);
        Assert.Null(documents.Destination);
        Assert.NotNull(documents.LastSuccess);
        Assert.Empty(manager.Config.Cleanups);

        // and a name that cannot be a destination never reaches the storage
        await Assert.ThrowsAsync<ArgumentException>(() => manager.ChangeDestinationAsync(documents, "data", null));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.ChangeDestinationAsync(documents, "Büro?", null));
    }

    [SkippableFact]
    public async Task Old_backups_are_deleted_for_their_folder_only_and_not_before_their_day()
    {
        Skip.If(_restic is null, "restic has not been built");
        var (manager, documents, _) = await SetUpAsync();

        await manager.ChangeDestinationAsync(documents, "Neu", deleteOldAfterDays: 30);

        var cleanup = Assert.Single(manager.Config.Cleanups);
        Assert.Equal("", cleanup.Destination);
        Assert.Equal(Documents, cleanup.FolderPath);
        Assert.InRange(cleanup.DueAt, DateTimeOffset.Now.AddDays(29.9), DateTimeOffset.Now.AddDays(30.1));
        Assert.Same(cleanup, Assert.Single(manager.CleanupsOf(documents)));

        // until then the old backup stays and can be restored
        Assert.Empty(await manager.RunDueCleanupsAsync(DateTimeOffset.Now.AddDays(29)));
        Assert.Equal(2, (await SnapshotsAt(_restic!, Disk)).Count);
        Assert.Contains(await manager.GetSnapshotsAsync(), snapshot => snapshot.Destination == "");

        // once the day has come, only the backup of this folder goes; the other one stays
        var result = Assert.Single(await manager.RunDueCleanupsAsync(DateTimeOffset.Now.AddDays(31)));
        Assert.Null(result.Error);
        Assert.Equal(1, result.SnapshotsDeleted);
        Assert.False(result.DestinationRemoved); // the destination of the storage stays, new folders go there
        Assert.Empty(manager.Config.Cleanups);
        var left = Assert.Single(await SnapshotsAt(_restic!, Disk));
        Assert.True(FolderRules.IsSameFolder(left.Paths.Single(), Photos));
        Assert.Empty(new ConfigStore(Path.Combine(_root, "settings")).Load().Cleanups);
    }

    [SkippableFact]
    public async Task Old_backups_can_be_kept_after_all()
    {
        Skip.If(_restic is null, "restic has not been built");
        var (manager, documents, _) = await SetUpAsync();

        // going back to the destination whose backups were to be deleted keeps them
        await manager.ChangeDestinationAsync(documents, "Neu", deleteOldAfterDays: 7);
        await manager.ChangeDestinationAsync(documents, "", deleteOldAfterDays: null);
        Assert.Empty(manager.Config.Cleanups);
        Assert.Equal("", documents.Destination);

        // every destination the folder left has a deletion of its own
        await manager.ChangeDestinationAsync(documents, "Neu", deleteOldAfterDays: 7);
        await manager.ChangeDestinationAsync(documents, "Noch neuer", deleteOldAfterDays: 90);
        Assert.Equal(["", "Neu"], manager.Config.Cleanups.Select(cleanup => cleanup.Destination).Order());

        // and the customer can keep everything with one click
        manager.KeepOldBackups(documents);
        Assert.Empty(manager.Config.Cleanups);

        // a deletion whose folder goes there again by the time it is due deletes nothing
        await manager.ChangeDestinationAsync(documents, "Wieder", deleteOldAfterDays: 0);
        documents.Destination = "Noch neuer";
        var result = Assert.Single(await manager.RunDueCleanupsAsync(DateTimeOffset.Now.AddMinutes(1)));
        Assert.Equal(0, result.SnapshotsDeleted);
        Assert.Empty(manager.Config.Cleanups);
        Assert.True(File.Exists(Path.Combine(Disk, "Noch neuer", "config")));
    }

    [SkippableFact]
    public async Task A_destination_left_empty_is_removed_and_nothing_next_to_it()
    {
        Skip.If(_restic is null, "restic has not been built");
        var (manager, documents, photos) = await SetUpAsync();

        await manager.ChangeDestinationAsync(documents, "Alt", null);
        await manager.ChangeDestinationAsync(photos, "Alt/PC", null);
        await manager.BackupAsync(documents);
        await manager.BackupAsync(photos);
        File.WriteAllText(Path.Combine(Disk, "Alt", "Notiz.txt"), "gehört nicht zum Backup");

        await manager.ChangeDestinationAsync(documents, "Neu", deleteOldAfterDays: 0);
        var result = Assert.Single(await manager.RunDueCleanupsAsync(DateTimeOffset.Now.AddMinutes(1)));

        Assert.Null(result.Error);
        Assert.Equal(1, result.SnapshotsDeleted);
        Assert.True(result.DestinationRemoved);

        // what restic kept there is gone, what lies next to it is not
        foreach (var part in new[] { "config", "data", "index", "keys", "snapshots", "locks" })
        {
            Assert.False(Path.Exists(Path.Combine(Disk, "Alt", part)), part);
        }

        Assert.True(File.Exists(Path.Combine(Disk, "Alt", "Notiz.txt")));
        Assert.Single(await SnapshotsAt(_restic!, Path.Combine(Disk, "Alt", "PC")));
        Assert.Equal(2, (await SnapshotsAt(_restic!, Disk)).Count);
        Assert.DoesNotContain("Alt", manager.KnownDestinations);
    }

    /// <summary>Answers requests the way the test says and remembers what was asked.</summary>
    private sealed class FakeStorage : HttpMessageHandler
    {
        public List<string> Keys { get; } = [];

        public List<string> Deleted { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Delete)
            {
                var key = Uri.UnescapeDataString(uri.AbsolutePath["/kunde-4711/".Length..]);
                Deleted.Add(key);
                Keys.Remove(key);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            var prefix = System.Web.HttpUtility.ParseQueryString(uri.Query)["prefix"] ?? "";
            var contents = string.Concat(Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(key => $"<Contents><Key>{key}</Key><Size>1</Size></Contents>"));
            var xml = $"""<ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><IsTruncated>false</IsTruncated>{contents}</ListBucketResult>""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") });
        }
    }

    [Theory]
    [InlineData("Alt")]
    [InlineData("")]
    public async Task Removing_a_destination_in_the_bucket_deletes_its_backup_and_nothing_else(string destination)
    {
        var prefix = destination.Length > 0 ? destination + "/" : "";
        var storage = new FakeStorage();
        string[] backup = [prefix + "config", prefix + "keys/k1", prefix + "data/00/d1", prefix + "index/i1", prefix + "snapshots/s1", prefix + "locks/l1"];
        string[] others = ["imagepixel/storage-status.json", "Alt/PC/config", "Alt/PC/data/00/d2", "Alt/Notiz.txt", "Notebook/config", "configs/x", "database/y"];
        storage.Keys.AddRange([.. backup, .. others]);

        var secrets = new MemorySecretStore();
        secrets.Set(SecretNames.SecretAccessKey, "geheim");
        var manager = new BackupManager(new ConfigStore(Path.Combine(_root, "settings")), secrets, "restic", http: new HttpClient(storage));
        manager.Config.Storage = new StorageSettings { Kind = StorageKind.ImagepixelStorage, Bucket = "kunde-4711", AccessKeyId = "KEY" };

        await manager.DeleteRepositoryAsync(destination);

        Assert.Equal(backup.Order(), storage.Deleted.Order());
        Assert.Equal(others.Order(), storage.Keys.Order());
    }

    [SkippableTheory]
    [InlineData(@"C:\Users\anna\Code", @"C:\Users\anna\Code", true)]
    [InlineData("/C/Users/anna/Code", @"C:\Users\anna\Code", true)]
    [InlineData(@"C:\Users\anna\Code\", @"c:\users\anna\code", true)]
    [InlineData(@"D:\", @"D:\", true)]
    [InlineData(@"C:\Users\anna\Code alt", @"C:\Users\anna\Code", false)]
    public void Paths_restic_recorded_are_matched_with_the_folder(string recorded, string folder, bool expected)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the paths are Windows paths");

        Assert.Equal(expected, FolderRules.IsSameFolder(recorded, folder));
    }

    [SkippableFact]
    public void A_folder_reached_through_a_link_is_matched_with_its_real_path()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows needs special rights for links");

        var real = Path.Combine(_root, "wirklich", "Dokumente");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_root, "verknuepft");
        Directory.CreateSymbolicLink(link, Path.Combine(_root, "wirklich"));

        // restic records the real path, the application knows the folder by the way it was chosen
        Assert.True(FolderRules.IsSameFolder(real, Path.Combine(link, "Dokumente")));
        Assert.True(FolderRules.IsSameFolder(Path.Combine(link, "Dokumente"), real));
        Assert.False(FolderRules.IsSameFolder(Path.Combine(_root, "wirklich"), Path.Combine(link, "Dokumente")));

        // the temp folder may lie behind a link itself, as it does on macOS
        Assert.Equal(FolderRules.RealPath(real), FolderRules.RealPath(Path.Combine(link, "Dokumente")));
        Assert.DoesNotContain("verknuepft", FolderRules.RealPath(Path.Combine(link, "Dokumente")));
    }

    [Fact]
    public void A_destination_on_a_disk_is_a_folder_inside_the_folder_of_the_backup()
    {
        var storage = new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Disk };

        Assert.Equal(Disk, storage.RepositoryUrl);
        Assert.Equal(Path.Combine(Disk, "Büro", "PC"), storage.WithFolder("Büro/PC").RepositoryUrl);
    }

    [Fact]
    public async Task The_backups_in_the_folder_on_a_disk_are_found()
    {
        foreach (var folder in new[] { "", "Notebook", "data", "Leer" })
        {
            Directory.CreateDirectory(Path.Combine(Disk, folder));
            if (folder != "Leer")
            {
                File.WriteAllText(Path.Combine(Disk, folder, "config"), "x");
            }
        }

        var found = await new BackupFinder(new HttpClient()).FindAsync(
            new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Disk }, null);

        // the folder "data" belongs to the backup at the top, "Leer" holds none
        Assert.Equal(["", "Notebook"], found);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
