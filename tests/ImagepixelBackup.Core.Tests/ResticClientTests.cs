using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.Core.Tests;

/// <summary>
/// Runs the real restic program against a repository in a temporary folder.
/// The tests are skipped when restic has not been built yet (see build-restic.ps1).
/// </summary>
public sealed class ResticClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-test-" + Guid.NewGuid().ToString("N"));
    private readonly string? _restic = ResticClient.FindRestic();

    public ResticClientTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Dokumente", "Verträge 2026"));
        File.WriteAllText(Path.Combine(_root, "Dokumente", "Brief.txt"), "Version 1 äöü");
        File.WriteAllText(Path.Combine(_root, "Dokumente", "Verträge 2026", "Mietvertrag Büro.txt"), "Vertrag");

        var random = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(random);
        File.WriteAllBytes(Path.Combine(_root, "Dokumente", "gross.bin"), random);
    }

    private string SourceFolder => Path.Combine(_root, "Dokumente");

    private ResticClient Client(string password = "test-password") =>
        new(_restic!, new RepositoryAccess(Path.Combine(_root, "repo"), password));

    [SkippableFact]
    public async Task Probe_reports_missing_repository_and_wrong_password()
    {
        Skip.If(_restic is null, "restic has not been built");

        Assert.Equal(RepositoryState.NotFound, await Client().ProbeAsync());

        await Client().InitAsync();

        Assert.Equal(RepositoryState.Ready, await Client().ProbeAsync());
        Assert.Equal(RepositoryState.WrongPassword, await Client("other").ProbeAsync());
    }

    [SkippableFact]
    public async Task Backup_stores_the_folder_under_its_own_name_and_restore_brings_it_back()
    {
        Skip.If(_restic is null, "restic has not been built");
        var client = Client();
        await client.InitAsync();

        var reported = new List<BackupProgress>();
        var first = await client.BackupAsync(SourceFolder, new SynchronousProgress<BackupProgress>(reported.Add));

        Assert.Equal(3, first.TotalFiles);
        Assert.Equal(3, first.FilesNew);
        Assert.Empty(first.Warnings);
        Assert.NotEmpty(first.SnapshotId);

        File.WriteAllText(Path.Combine(SourceFolder, "Brief.txt"), "Version 2");
        var second = await client.BackupAsync(SourceFolder);
        Assert.Equal(1, second.FilesChanged);
        Assert.Equal(2, second.FilesUnmodified);

        var snapshots = await client.GetSnapshotsAsync();
        Assert.Equal(2, snapshots.Count);
        Assert.True(snapshots[0].Time <= snapshots[1].Time);
        Assert.Equal(SourceFolder, snapshots[1].Paths.Single());
        Assert.StartsWith(snapshots[0].ShortId, first.SnapshotId);

        var target = Path.Combine(_root, "restored");
        var restoreProgress = new List<RestoreProgress>();
        await client.RestoreAsync(snapshots[0].Id, target, new SynchronousProgress<RestoreProgress>(restoreProgress.Add));

        // the snapshot starts with the folder itself, not with the path that led to it
        Assert.Equal("Version 1 äöü", File.ReadAllText(Path.Combine(target, "Dokumente", "Brief.txt")));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(SourceFolder, "gross.bin")),
            File.ReadAllBytes(Path.Combine(target, "Dokumente", "gross.bin")));
        Assert.Equal(1.0, restoreProgress.Last().Fraction);
    }

    [SkippableFact]
    public async Task Errors_carry_the_message_of_restic()
    {
        Skip.If(_restic is null, "restic has not been built");

        var error = await Assert.ThrowsAsync<ResticException>(() => Client().GetSnapshotsAsync());

        Assert.Equal(ResticException.ExitRepositoryNotFound, error.ExitCode);
        Assert.Contains("repository does not exist", error.Message);
        Assert.DoesNotContain("message_type", error.Message);
    }

    [SkippableFact]
    public async Task Mount_shows_the_snapshots_read_only_and_releases_the_lock_when_stopped()
    {
        Skip.If(_restic is null, "restic has not been built");
        Skip.IfNot(OperatingSystem.IsWindows(), "mounting is only implemented for Windows");
        Skip.IfNot(Directory.Exists(@"C:\Program Files (x86)\WinFsp"), "WinFsp is not installed");

        var client = Client();
        await client.InitAsync();
        await client.BackupAsync(SourceFolder);

        var letter = MountSession.FreeDriveLetters().Last();
        var session = await client.MountAsync(letter, "Imagepixel Test");
        try
        {
            Assert.True(session.IsRunning);

            var snapshot = Directory.GetDirectories(session.RootPath).Single();
            var file = Path.Combine(snapshot, "Dokumente", "Brief.txt");
            Assert.Equal("Version 1 äöü", File.ReadAllText(file));

            Assert.ThrowsAny<Exception>(() => File.WriteAllText(file, "changed"));
            Assert.ThrowsAny<Exception>(() => File.Delete(file));
            Assert.Equal("Version 1 äöü", File.ReadAllText(file));
        }
        finally
        {
            await session.StopAsync();
        }

        Assert.False(session.IsRunning);
        Assert.False(Directory.Exists(session.RootPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "repo", "locks")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Reports progress on the calling thread, so that tests see every value.</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (handler)
            {
                handler(value);
            }
        }
    }
}
