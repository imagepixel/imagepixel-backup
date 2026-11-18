using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ImagepixelBackup.Core.Restic;

/// <summary>Runs restic for one repository and translates its output.</summary>
public sealed class ResticClient(string resticPath, RepositoryAccess access)
{
    private const int ExitIncompleteBackup = 3;

    public string ResticPath { get; } = resticPath;

    public RepositoryAccess Access { get; } = access;

    /// <summary>
    /// Finds the restic program: next to the application, or the one built by
    /// build-restic.ps1 when running from the source tree.
    /// </summary>
    public static string? FindRestic()
    {
        var name = OperatingSystem.IsWindows() ? "restic.exe" : "restic";

        if (Environment.GetEnvironmentVariable("IMAGEPIXEL_BACKUP_RESTIC") is { Length: > 0 } custom && File.Exists(custom))
        {
            return custom;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(directory.FullName, name), Path.Combine(directory.FullName, "bin", name) })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Checks whether there is a repository at the location and whether the password opens it.</summary>
    public async Task<RepositoryState> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["cat", "config", "--no-lock", "--json"], null, null, cancellationToken);
        return result.ExitCode switch
        {
            0 => RepositoryState.Ready,
            ResticException.ExitRepositoryNotFound => RepositoryState.NotFound,
            ResticException.ExitWrongPassword => RepositoryState.WrongPassword,
            _ => throw result.ToException(),
        };
    }

    /// <summary>Creates a new, empty repository.</summary>
    public async Task InitAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["init", "--json"], null, null, cancellationToken);
        result.ThrowIfFailed();
    }

    /// <summary>Lists all snapshots, oldest first.</summary>
    public async Task<IReadOnlyList<Snapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        var result = await RunAsync(["snapshots", "--json", "--no-lock"], null, line => output.AppendLine(line), cancellationToken);
        result.ThrowIfFailed();

        var snapshots = JsonSerializer.Deserialize<List<Snapshot>>(output.ToString()) ?? [];
        return [.. snapshots.OrderBy(s => s.Time)];
    }

    /// <summary>
    /// Backs up a folder. The folder is stored under its own name, without the path
    /// that leads to it, so that a snapshot starts with the folder itself.
    /// </summary>
    /// <param name="folder">The folder to back up.</param>
    /// <param name="progress">Receives the progress of the backup.</param>
    /// <param name="cancellationToken">Cancels the backup.</param>
    /// <param name="onFileSaved">
    /// Called with the full path of every file restic has finished with, whether it
    /// was read or found unchanged. Asking for this makes restic report every file.
    /// </param>
    /// <param name="exclusions">Arguments that tell restic what to leave out, if anything.</param>
    public async Task<BackupSummary> BackupAsync(
        string folder,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<string>? onFileSaved = null,
        IReadOnlyList<string>? exclusions = null)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"The folder {full} does not exist.");
        }

        // restic keeps the path as it is given. Running it from the parent folder with
        // only the folder name stores "Documents/..." instead of "C/Users/.../Documents/...".
        var parent = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        var (workingDirectory, target) = string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)
            ? ((string?)null, full)
            : (parent, name);

        BackupSummary? summary = null;
        var warnings = new List<string>();

        void OnLine(string line)
        {
            if (!TryParse(line, out var message))
            {
                return;
            }

            // output and error stream are read concurrently
            using (message)
            lock (warnings)
            {
                switch (MessageType(message))
                {
                    case "status":
                        progress?.Report(message.Deserialize<BackupProgress>()!);
                        break;
                    case "summary":
                        summary = message.Deserialize<BackupSummary>();
                        break;
                    case "error":
                        warnings.Add(DescribeError(message));
                        break;
                    case "verbose_status" when onFileSaved is not null:
                        var root = message.RootElement;
                        if (root.TryGetProperty("action", out var action)
                            && action.GetString() is "new" or "modified" or "unchanged"
                            && root.TryGetProperty("item", out var item)
                            && item.GetString() is { Length: > 0 } path
                            && !path.EndsWith('/'))
                        {
                            onFileSaved(ItemToPath(path, workingDirectory));
                        }

                        break;
                }
            }
        }

        string[] arguments =
        [
            "backup",
            "--json",
            .. onFileSaved is null ? Array.Empty<string>() : ["--verbose=2"],
            .. exclusions ?? [],
            target,
        ];
        var result = await RunAsync(arguments, workingDirectory, OnLine, cancellationToken, OnLine);
        if (result.ExitCode is not (0 or ExitIncompleteBackup) || summary is null)
        {
            throw result.ToException();
        }

        summary.Warnings.AddRange(warnings);
        return summary;
    }

    /// <summary>Restores a complete snapshot into a folder.</summary>
    public async Task RestoreAsync(
        string snapshotId,
        string targetFolder,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        void OnLine(string line)
        {
            if (TryParse(line, out var message))
            {
                using (message)
                {
                    var type = MessageType(message);
                    if (type is "status" or "summary")
                    {
                        var report = message.Deserialize<RestoreProgress>()!;
                        if (type == "summary")
                        {
                            // the summary has the final counts but no percentage
                            report.Fraction = 1;
                        }

                        progress?.Report(report);
                    }
                }
            }
        }

        var result = await RunAsync(["restore", snapshotId, "--target", targetFolder, "--json"], null, OnLine, cancellationToken);
        result.ThrowIfFailed();
    }

    /// <summary>
    /// Mounts the repository as a read-only volume. On Windows the mountpoint is an
    /// unused drive letter such as "R:", on Linux an empty folder.
    /// </summary>
    public Task<MountSession> MountAsync(string mountpoint, string volumeName, CancellationToken cancellationToken = default) =>
        MountSession.StartAsync(this, mountpoint, volumeName, cancellationToken);

    /// <param name="arguments">The arguments of the program.</param>
    /// <param name="workingDirectory">The folder to start the program in, if it matters.</param>
    /// <param name="program">A program to start in place of restic, with the access data of restic.</param>
    internal ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments, string? workingDirectory, string? program = null)
    {
        var startInfo = new ProcessStartInfo(program ?? ResticPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Access.ApplyTo(startInfo.Environment);
        startInfo.Environment["RESTIC_PROGRESS_FPS"] = "4";
        return startInfo;
    }

    private async Task<ResticResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<string>? onOutputLine,
        CancellationToken cancellationToken,
        Action<string>? onErrorLine = null)
    {
        using var process = new Process { StartInfo = CreateStartInfo(arguments, workingDirectory) };
        process.Start();
        process.StandardInput.Close();

        var errors = new List<string>();
        var readOutput = ReadLinesAsync(process.StandardOutput, onOutputLine);
        var readError = ReadLinesAsync(process.StandardError, line =>
        {
            lock (errors)
            {
                errors.Add(line);
            }

            onErrorLine?.Invoke(line);
        });

        await using (cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // the process has already exited
            }
        }))
        {
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(readOutput, readError);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ResticResult(process.ExitCode, errors);
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > 0)
            {
                onLine?.Invoke(line);
            }
        }
    }

    /// <summary>
    /// Turns the path restic reports for a file, such as "/Dokumente/Brief.txt", back
    /// into the path on this computer. restic was either started in a folder with a
    /// relative target, or with an absolute target, which it reports as "/C/Users/...".
    /// </summary>
    internal static string ItemToPath(string item, string? workingDirectory)
    {
        var relative = item.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (workingDirectory is not null)
        {
            return Path.Combine(workingDirectory, relative);
        }

        if (OperatingSystem.IsWindows() && relative.Length > 0 && char.IsAsciiLetter(relative[0])
            && (relative.Length == 1 || relative[1] == Path.DirectorySeparatorChar))
        {
            return relative.Length == 1 ? $"{relative}:{Path.DirectorySeparatorChar}" : $"{relative[0]}:{relative[1..]}";
        }

        return Path.DirectorySeparatorChar + relative;
    }

    internal static bool TryParse(string line, out JsonDocument message)
    {
        message = null!;
        if (!line.StartsWith('{'))
        {
            return false;
        }

        try
        {
            message = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? MessageType(JsonDocument message) =>
        message.RootElement.TryGetProperty("message_type", out var type) ? type.GetString() : null;

    private static string DescribeError(JsonDocument message)
    {
        var root = message.RootElement;
        var item = root.TryGetProperty("item", out var i) ? i.GetString() : null;
        var text = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : null;
        return string.IsNullOrEmpty(item) ? text ?? "unknown error" : $"{item}: {text}";
    }

    internal sealed record ResticResult(int ExitCode, IReadOnlyList<string> ErrorLines)
    {
        public void ThrowIfFailed()
        {
            if (ExitCode != 0)
            {
                throw ToException();
            }
        }

        public ResticException ToException() => new(ErrorMessage(ErrorLines, ExitCode), ExitCode);
    }

    /// <summary>Extracts a readable message from what restic wrote to its error stream.</summary>
    internal static string ErrorMessage(IReadOnlyList<string> errorLines, int exitCode)
    {
        var messages = new List<string>();
        foreach (var line in errorLines)
        {
            if (TryParse(line, out var message))
            {
                using (message)
                {
                    if (message.RootElement.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        messages.Add(text.GetString()!);
                    }
                    else if (MessageType(message) == "error")
                    {
                        messages.Add(DescribeError(message));
                    }
                }
            }
            else
            {
                messages.Add(line);
            }
        }

        var combined = string.Join(Environment.NewLine, messages).Trim();
        if (combined.StartsWith("Fatal: ", StringComparison.Ordinal))
        {
            combined = combined["Fatal: ".Length..];
        }

        return combined.Length > 0 ? combined : $"restic exited with code {exitCode}.";
    }
}
