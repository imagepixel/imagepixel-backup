using System.Diagnostics;

namespace ImagepixelBackup.Core.Restic;

/// <summary>
/// A repository mounted as a read-only volume. The volume exists as long as the
/// session is running. If this process ends for any reason, restic notices that its
/// standard input was closed, unmounts the volume and releases its lock.
/// </summary>
public sealed class MountSession : IAsyncDisposable
{
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

    /// <summary>The path to open in a file manager.</summary>
    public string RootPath => IsDriveLetter(Mountpoint) ? Mountpoint + Path.DirectorySeparatorChar : Mountpoint;

    public bool IsRunning => !_process.HasExited;

    /// <summary>Raised when the volume disappears, whether it was stopped or failed.</summary>
    public event EventHandler? Exited;

    /// <summary>Returns the drive letters that are not in use, in the order they should be tried.</summary>
    public static IReadOnlyList<string> FreeDriveLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return [.. "RSTUVWXYZQPONMLKJIHGFED".Where(c => !used.Contains(c)).Select(c => $"{c}:")];
    }

    internal static async Task<MountSession> StartAsync(
        ResticClient client,
        string mountpoint,
        string volumeName,
        CancellationToken cancellationToken)
    {
        var startInfo = client.CreateStartInfo(
            ["mount", mountpoint, "--volume-name", volumeName, "--stop-on-stdin-close"], null);

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
            while (!Directory.Exists(session.RootPath))
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
        if (_process.HasExited)
        {
            return;
        }

        try
        {
            // closing standard input asks restic to unmount and exit
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(StopTimeout);
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // the process has already exited
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
