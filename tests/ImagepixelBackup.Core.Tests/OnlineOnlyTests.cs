using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.Core.Tests;

public sealed class OnlineOnlyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-online-" + Guid.NewGuid().ToString("N"));

    /// <summary>A cloud folder whose state the test controls.</summary>
    private sealed class FakeCloud : ICloudFileSystem
    {
        public Dictionary<string, CloudFileState> States { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> InUse { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> FreedUp { get; } = [];

        /// <summary>Set to act as if the backup reads, and so downloads, every online-only file.</summary>
        public bool DownloadAfterScan { get; set; }

        public IEnumerable<string> FindOnlineOnly(string folder)
        {
            var onlineOnly = States.Where(s => s.Value == CloudFileState.OnlineOnly).Select(s => s.Key).ToList();
            if (DownloadAfterScan)
            {
                onlineOnly.ForEach(Download);
            }

            return onlineOnly;
        }

        public CloudFileState GetState(string path) => States.GetValueOrDefault(path, CloudFileState.NotFound);

        public bool FreeUp(string path)
        {
            if (InUse.Contains(path))
            {
                return false;
            }

            States[path] = CloudFileState.OnlineOnly;
            FreedUp.Add(path);
            return true;
        }

        /// <summary>What the cloud service does when a program reads an online-only file.</summary>
        public void Download(string path) => States[path] = CloudFileState.Local;
    }

    [Fact]
    public void No_guard_is_needed_without_online_only_files()
    {
        var cloud = new FakeCloud { States = { ["a"] = CloudFileState.Local, ["b"] = CloudFileState.Pinned } };

        Assert.Null(OnlineOnlyGuard.TryCreate("folder", cloud));
    }

    [Fact]
    public void A_downloaded_file_is_freed_up_as_soon_as_it_is_saved()
    {
        var cloud = new FakeCloud { States = { ["a"] = CloudFileState.OnlineOnly, ["b"] = CloudFileState.OnlineOnly } };
        var guard = OnlineOnlyGuard.TryCreate("folder", cloud)!;
        Assert.Equal(2, guard.Count);

        // restic may report the path in a different case than the folder listing
        cloud.Download("a");
        guard.FileSaved("A");

        Assert.Equal(["A"], cloud.FreedUp);
        Assert.Equal(1, guard.Freed);
        Assert.Equal(CloudFileState.OnlineOnly, cloud.States["a"]);
    }

    [Fact]
    public void Files_that_were_on_the_computer_before_are_never_freed_up()
    {
        var cloud = new FakeCloud
        {
            States =
            {
                ["online"] = CloudFileState.OnlineOnly,
                ["local"] = CloudFileState.Local,
                ["pinned"] = CloudFileState.Pinned,
            },
        };
        var guard = OnlineOnlyGuard.TryCreate("folder", cloud)!;

        guard.FileSaved("local");
        guard.FileSaved("pinned");
        guard.Finish();

        Assert.Empty(cloud.FreedUp);
        Assert.Equal(CloudFileState.Local, cloud.States["local"]);
        Assert.Equal(CloudFileState.Pinned, cloud.States["pinned"]);
    }

    [Fact]
    public void A_file_the_user_pinned_during_the_backup_is_left_alone()
    {
        var cloud = new FakeCloud { States = { ["a"] = CloudFileState.OnlineOnly } };
        var guard = OnlineOnlyGuard.TryCreate("folder", cloud)!;

        cloud.States["a"] = CloudFileState.Pinned;
        guard.FileSaved("a");
        guard.Finish();

        Assert.Empty(cloud.FreedUp);
        Assert.Equal(CloudFileState.Pinned, cloud.States["a"]);
    }

    [Fact]
    public void An_unchanged_file_that_was_not_downloaded_needs_nothing()
    {
        var cloud = new FakeCloud { States = { ["a"] = CloudFileState.OnlineOnly } };
        var guard = OnlineOnlyGuard.TryCreate("folder", cloud)!;

        guard.FileSaved("a");
        guard.Finish();

        Assert.Empty(cloud.FreedUp);
        Assert.Equal(0, guard.Freed);
    }

    [Fact]
    public void A_file_that_is_still_in_use_is_freed_up_at_the_end()
    {
        var cloud = new FakeCloud { States = { ["a"] = CloudFileState.OnlineOnly } };
        var guard = OnlineOnlyGuard.TryCreate("folder", cloud)!;

        cloud.Download("a");
        cloud.InUse.Add("a");
        guard.FileSaved("a");
        Assert.Empty(cloud.FreedUp);

        cloud.InUse.Clear();
        guard.Finish();
        guard.Finish();

        Assert.Equal(["a"], cloud.FreedUp);
        Assert.Equal(1, guard.Freed);
    }

    [Fact]
    public void A_cancelled_backup_still_frees_up_what_it_downloaded()
    {
        var cloud = new FakeCloud { States = { ["done"] = CloudFileState.OnlineOnly, ["half"] = CloudFileState.OnlineOnly, ["never"] = CloudFileState.OnlineOnly } };
        var guard = OnlineOnlyGuard.TryCreate("folder", cloud)!;

        cloud.Download("done");
        guard.FileSaved("done");
        cloud.Download("half");

        // the backup stops here, "half" was downloaded but never reported as saved
        guard.Finish();

        Assert.Equal(["done", "half"], cloud.FreedUp);
        Assert.Equal(CloudFileState.OnlineOnly, cloud.States["never"]);
    }

    [SkippableTheory]
    [InlineData("/Dokumente/Brief.txt", @"C:\Users\anna", @"C:\Users\anna\Dokumente\Brief.txt")]
    [InlineData("/Meine Ablage/a/b.txt", @"G:\", @"G:\Meine Ablage\a\b.txt")]
    [InlineData("/C/Users/anna/Dokumente/Brief.txt", null, @"C:\Users\anna\Dokumente\Brief.txt")]
    [InlineData("/G/Brief.txt", null, @"G:\Brief.txt")]
    public void Paths_reported_by_restic_are_mapped_back_to_the_computer(string item, string? workingDirectory, string expected)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the expected paths are Windows paths");

        Assert.Equal(expected, ResticClient.ItemToPath(item, workingDirectory));
    }

    [SkippableTheory]
    [InlineData("/Dokumente/Brief.txt", "/Users/anna", "/Users/anna/Dokumente/Brief.txt")]
    [InlineData("/Meine Ablage/a/b.txt", "/Volumes/Daten", "/Volumes/Daten/Meine Ablage/a/b.txt")]
    [InlineData("/Users/anna/Dokumente/Brief.txt", null, "/Users/anna/Dokumente/Brief.txt")]
    public void Paths_reported_by_restic_are_mapped_back_on_macOS_too(string item, string? workingDirectory, string expected)
    {
        Skip.If(OperatingSystem.IsWindows(), "the expected paths are macOS paths");

        Assert.Equal(expected, ResticClient.ItemToPath(item, workingDirectory));
    }

    [SkippableFact]
    public async Task Backup_reports_every_file_and_frees_up_the_ones_that_were_online_only()
    {
        var restic = ResticClient.FindRestic();
        Skip.If(restic is null, "restic has not been built");

        var source = Path.Combine(_root, "OneDrive");
        Directory.CreateDirectory(Path.Combine(source, "Bilder"));
        var online = Path.Combine(source, "Bilder", "Urlaub.jpg");
        var local = Path.Combine(source, "Notiz.txt");
        File.WriteAllText(online, "nur online");
        File.WriteAllText(local, "immer hier");

        // the fake treats one file as online-only; restic reading it "downloads" it
        var cloud = new FakeCloud { States = { [online] = CloudFileState.OnlineOnly, [local] = CloudFileState.Pinned } };
        var store = new ConfigStore(Path.Combine(_root, "settings"));
        var manager = new BackupManager(store, new MemorySecretStore(), restic!, cloud);
        await manager.ConnectAsync(
            new StorageSettings { Kind = StorageKind.LocalFolder, LocalPath = Path.Combine(_root, "repo") },
            "test-password", null, createIfMissing: true);
        var folder = manager.AddFolder(source);

        cloud.DownloadAfterScan = true;
        var first = await manager.BackupAsync(folder);

        Assert.Equal(2, first.TotalFiles);
        Assert.Equal(1, first.FilesFreedUp);
        Assert.Equal([online], cloud.FreedUp);
        Assert.Equal(CloudFileState.OnlineOnly, cloud.States[online]);
        Assert.Equal(CloudFileState.Pinned, cloud.States[local]);

        // the next backup finds the file unchanged, does not read it and frees nothing
        cloud.DownloadAfterScan = false;
        var second = await manager.BackupAsync(folder);
        Assert.Equal(2, second.FilesUnmodified);
        Assert.Equal(0, second.FilesFreedUp);
        Assert.Single(cloud.FreedUp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
