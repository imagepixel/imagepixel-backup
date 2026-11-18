using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.Core.Tests;

/// <summary>Which drive letter the backup is opened under on Windows.</summary>
public sealed class DriveLetterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-drive-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_first_free_letter_is_suggested()
    {
        Assert.Equal("R:", MountSession.SuggestDriveLetter(["R:", "S:", "T:"], null));
        Assert.Equal("S:", MountSession.SuggestDriveLetter(["S:", "T:"], null));
    }

    [Fact]
    public void The_letter_the_user_chose_is_suggested_while_it_is_free()
    {
        Assert.Equal("X:", MountSession.SuggestDriveLetter(["R:", "S:", "X:"], "X:"));
        Assert.Equal("X:", MountSession.SuggestDriveLetter(["R:", "S:", "X:"], "x:"));

        // something else uses it: another one for now
        Assert.Equal("R:", MountSession.SuggestDriveLetter(["R:", "S:"], "X:"));
    }

    [Fact]
    public void Nothing_is_suggested_when_no_letter_is_free()
    {
        Assert.Null(MountSession.SuggestDriveLetter([], null));
        Assert.Null(MountSession.SuggestDriveLetter([], "X:"));
    }

    [SkippableFact]
    public void Free_letters_leave_out_the_drives_of_the_computer()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "drive letters are a thing of Windows");

        var free = MountSession.FreeDriveLetters();
        var used = DriveInfo.GetDrives().Select(drive => drive.Name[..2].ToUpperInvariant()).ToList();

        Assert.NotEmpty(used);
        Assert.DoesNotContain(free, used.Contains);
        Assert.All(free, letter => Assert.Matches("^[D-Z]:$", letter));
    }

    [SkippableFact]
    public async Task A_letter_that_is_in_use_is_refused_instead_of_replaced()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "drive letters are a thing of Windows");

        var manager = new BackupManager(new ConfigStore(_root), new MemorySecretStore(), "restic");
        var system = Path.GetPathRoot(Environment.SystemDirectory)![..2];

        var error = await Assert.ThrowsAsync<DriveLetterInUseException>(
            () => manager.MountAsync("Test", driveLetter: system));
        Assert.Equal(system, error.Letter);
    }
}
