using System.Diagnostics;

namespace ImagepixelBackup.Core.Restic;

/// <summary>
/// A repository mounted as a read-only volume. The volume exists as long as the
/// session is running. If this process ends for any reason, the standard input of the
/// mount is closed, which unmounts the volume and releases the lock of restic.
/// </summary>
public sealed class MountSession : IAsyncDisposable
{
    /// <summary>The names of the folders of the snapshots, in the notation of restic.</summary>
    private const string TimeTemplate = "2006-01-02_15-04-05";

    // Linux: the one folder at the top of the volume. restic does not show the
    // snapshots when they are asked to lie at the top themselves.
    private const string SnapshotsFolder = "snapshots";

    // Linux: restic does not watch its standard input there, so a shell does. It runs
    // restic and ends it once the input is closed, which makes restic unmount the
    // volume. The exit code of restic becomes the one of the shell. The input is kept
    // as number 3 first, because a shell gives what it runs in the background an
    // empty input of its own.
    private static readonly string StopOnInputClose = """
        exec 3<&0
        "$@" 3<&- &
        mount=$!
        ( cat <&3 > /dev/null; kill -TERM "$mount" 2> /dev/null ) > /dev/null 2>&1 &
        exec 3<&-
        wait "$mount"
        """.ReplaceLineEndings("\n");

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    private readonly Process _process;
    private readonly List<string> _errors = [];

    private MountSession(Process process, string mountpoint)
    {
        _process = process;
        Mountpoint = mountpoint;
    }

    /// <summary>The drive letter or folder the repository is mounted at.</summary>
    public string Mountpoint { get; }

    /// <summary>
    /// The path to open in a file manager: the folder that holds one folder per
    /// snapshot. On Linux restic adds "latest" there, a link to the newest of them.
    /// </summary>
    public string RootPath => IsDriveLetter(Mountpoint)
        ? Mountpoint + Path.DirectorySeparatorChar
        : Path.Combine(Mountpoint, SnapshotsFolder);

    public bool IsRunning => !_process.HasExited;

    /// <summary>Raised when the volume disappears, whether it was stopped or failed.</summary>
    public event EventHandler? Exited;

    /// <summary>Returns the drive letters that are not in use, in the order they should be tried.</summary>
    public static IReadOnlyList<string> FreeDriveLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return [.. "RSTUVWXYZQPONMLKJIHGFED".Where(c => !used.Contains(c)).Select(c => $"{c}:")];
    }

    /// <summary>
    /// The drive letter to suggest: the one the user chose before if it is free, else
    /// the first of the free ones, or null if none is free.
    /// </summary>
    public static string? SuggestDriveLetter(IReadOnlyList<string> free, string? preferred) =>
        preferred is not null && free.Contains(preferred, StringComparer.OrdinalIgnoreCase)
            ? free.First(letter => string.Equals(letter, preferred, StringComparison.OrdinalIgnoreCase))
            : free.FirstOrDefault();

    /// <summary>
    /// The folder a repository is mounted at on Linux: one of the user's own that is
    /// emptied when they log out. Its path has no spaces.
    /// </summary>
    public static string LinuxMountpoint() => RuntimeFolder("imagepixel-backup");

    private static string RuntimeFolder(string name)
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return runtime is { Length: > 0 } && Directory.Exists(runtime)
            ? Path.Combine(runtime, name)
            : Path.Combine(Path.GetTempPath(), name + "-" + Environment.UserName);
    }

    /// <summary>
    /// restic asks for the program "fusermount". Systems that only have its successor,
    /// as CachyOS does, call it "fusermount3". Returns a folder that holds a link to it
    /// under the old name, to put in front of the search path of restic, or null if
    /// the system needs no such help or has neither.
    /// </summary>
    internal static string? FusermountFolder()
    {
        if (FindProgram("fusermount") is not null || FindProgram("fusermount3") is not { } successor)
        {
            return null;
        }

        var folder = RuntimeFolder("imagepixel-backup-tools");
        var link = Path.Combine(folder, "fusermount");
        try
        {
            Directory.CreateDirectory(folder);
            if (!File.Exists(link))
            {
                File.CreateSymbolicLink(link, successor);
            }

            return folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindProgram(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);

    /// <summary>Whether something is mounted at a folder. Only Linux can tell.</summary>
    public static bool IsMounted(string folder)
    {
        try
        {
            // each line: device, mountpoint, type, ... with spaces written as \040
            return File.ReadLines("/proc/mounts").Any(line =>
                line.Split(' ') is { Length: > 1 } fields && fields[1].Replace("\\040", " ") == folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a volume that was left behind at a folder, for example by a restic that
    /// was killed. Linux only; does nothing if the volume cannot be removed.
    /// </summary>
    public static void ReleaseStale(string folder)
    {
        foreach (var program in new[] { "fusermount3", "fusermount" })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(program)
                {
                    ArgumentList = { "-u", "-z", folder },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                });
                process?.WaitForExit(10_000);
                if (!IsMounted(folder))
                {
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // this program is not installed, try the other one
            }
        }
    }

    internal static async Task<MountSession> StartAsync(
        ResticClient client,
        string mountpoint,
        string volumeName,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo;
        Func<bool> isReady;
        if (OperatingSystem.IsWindows())
        {
            startInfo = client.CreateStartInfo(
                ["mount", mountpoint, "--volume-name", volumeName, "--stop-on-stdin-close"], null);
            var root = mountpoint + Path.DirectorySeparatorChar;
            isReady = () => Directory.Exists(root);
        }
        else
        {
            // the snapshots are named as they are on Windows, and the user who mounts the
            // volume can read every file, whoever owned it when it was backed up
            Directory.CreateDirectory(mountpoint);
            startInfo = client.CreateStartInfo(
                [
                    "-c", StopOnInputClose, "sh", client.ResticPath, "mount", mountpoint,
                    "--path-template", SnapshotsFolder + "/%T", "--time-template", TimeTemplate,
                    "--no-default-permissions",
                ],
                null,
                "/bin/sh");
            if (FusermountFolder() is { } tools)
            {
                startInfo.Environment["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            }

            isReady = () => IsMounted(mountpoint);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var session = new MountSession(process, mountpoint);
        process.Exited += (_, _) => session.Exited?.Invoke(session, EventArgs.Empty);
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (session._errors)
                {
                    session._errors.Add(e.Data);
                }
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            var deadline = Stopwatch.StartNew();
            while (!isReady())
            {
                if (process.HasExited)
                {
                    process.WaitForExit();
                    throw new ResticException(session.ErrorText(), process.ExitCode);
                }

                if (deadline.Elapsed > StartTimeout)
                {
                    throw new TimeoutException($"The volume did not appear at {mountpoint} in time.");
                }

                await Task.Delay(150, cancellationToken);
            }
        }
        catch
        {
            await session.StopAsync();
            throw;
        }

        return session;
    }

    /// <summary>Unmounts the volume and waits until restic has released its lock.</summary>
    public async Task StopAsync()
    {
        try
        {
            // closing standard input asks for the volume to be unmounted
            _process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // closed before, or the process is gone
        }

        try
        {
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(StopTimeout);
                await _process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // the process has already exited
        }

        if (!IsDriveLetter(Mountpoint))
        {
            try
            {
                // the empty folder the volume was mounted at
                Directory.Delete(Mountpoint);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // still in use, or gone already
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _process.Dispose();
    }

    private string ErrorText()
    {
        lock (_errors)
        {
            return ResticClient.ErrorMessage(_errors, _process.ExitCode);
        }
    }

    private static bool IsDriveLetter(string mountpoint) =>
        mountpoint.Length == 2 && mountpoint[1] == ':' && char.IsAsciiLetter(mountpoint[0]);
}
