using System.ComponentModel;
using System.Diagnostics;
using ImagepixelBackup.Core.Restic;

namespace ImagepixelBackup.Core;

/// <summary>
/// Shows the storage read-only as it is: the files of the backup the way they lie there,
/// encrypted, with names that are checksums. The customer can see for themselves that
/// nothing in it can be read without their password. rclone does it, as released by its
/// makers. It gets the access data through its environment, never on its command line,
/// and keeps no settings of its own for it.
/// </summary>
public static class EncryptedView
{
    // the name rclone knows the storage by, in the names of its environment variables
    private const string Remote = "imagepixel";

    /// <summary>
    /// Finds the rclone program: next to the application, the one fetched by
    /// get-rclone.ps1 or get-rclone.sh when running from the source tree, or on Linux
    /// one that is installed.
    /// </summary>
    public static string? FindRclone()
    {
        if (Environment.GetEnvironmentVariable("IMAGEPIXEL_BACKUP_RCLONE") is { Length: > 0 } custom && File.Exists(custom))
        {
            return custom;
        }

        var name = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
        var fetched = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone-linux-amd64";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(directory.FullName, name), Path.Combine(directory.FullName, "bin", fetched) })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>The folder the view is shown at on Linux: one of the user's own that is emptied when they log out.</summary>
    public static string LinuxMountpoint() => MountSession.RuntimeFolder("imagepixel-backup-verschluesselt");

    /// <summary>What rclone is started with to show a destination of Imagepixel Storage.</summary>
    /// <param name="rclone">The rclone program.</param>
    /// <param name="storage">The storage, with its bucket, region and access key.</param>
    /// <param name="secretAccessKey">The secret key that belongs to the access key.</param>
    /// <param name="destination">The destination in the storage, empty for its top.</param>
    /// <param name="mountpoint">Windows: a free drive letter such as "S:"; Linux: an empty folder.</param>
    /// <param name="volumeName">The name the drive shows on Windows.</param>
    public static ProcessStartInfo CreateStartInfo(
        string rclone,
        StorageSettings storage,
        string secretAccessKey,
        string destination,
        string mountpoint,
        string volumeName)
    {
        var inside = storage.PathOf(destination);
        var path = storage.Bucket + (inside.Length > 0 ? "/" + inside : "");
        List<string> arguments = ["mount", $"{Remote}:{path}", mountpoint];

        // the figures Imagepixel keeps at the top of the bucket are not part of the backup
        if (inside.Length == 0)
        {
            arguments.AddRange(["--exclude", "/imagepixel/**"]);
        }

        var startInfo = Create(rclone, arguments, mountpoint, volumeName);
        var prefix = $"RCLONE_CONFIG_{Remote.ToUpperInvariant()}_";
        startInfo.Environment[prefix + "TYPE"] = "s3";
        startInfo.Environment[prefix + "PROVIDER"] = "Wasabi";
        startInfo.Environment[prefix + "ENDPOINT"] = ImagepixelStorage.Endpoint(storage.Region);
        startInfo.Environment[prefix + "REGION"] = storage.Region;
        startInfo.Environment[prefix + "ACCESS_KEY_ID"] = storage.AccessKeyId;
        startInfo.Environment[prefix + "SECRET_ACCESS_KEY"] = secretAccessKey;
        startInfo.Environment[prefix + "NO_CHECK_BUCKET"] = "true";
        return startInfo;
    }

    /// <summary>The same for a folder on this computer, to try the view without a storage.</summary>
    internal static ProcessStartInfo CreateStartInfoForFolder(string rclone, string folder, string mountpoint, string volumeName) =>
        Create(rclone, ["mount", folder, mountpoint], mountpoint, volumeName);

    private static ProcessStartInfo Create(string rclone, List<string> arguments, string mountpoint, string volumeName)
    {
        // read-only, and every read goes to the storage: nothing of it is kept on this computer
        arguments.AddRange(["--read-only", "--vfs-cache-mode", "off", "--dir-cache-time", "30s"]);

        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            arguments.AddRange(["--volname", volumeName]);
            startInfo = new ProcessStartInfo(rclone);
        }
        else
        {
            // a shell ends rclone once standard input closes, as it does for restic
            Directory.CreateDirectory(mountpoint);
            startInfo = new ProcessStartInfo("/bin/sh");
            arguments.InsertRange(0, ["-c", MountSession.StopOnInputClose, "sh", rclone]);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        // a settings file that does not exist: rclone neither reads nor writes one of the user's
        startInfo.Environment["RCLONE_CONFIG"] = Path.Combine(Path.GetTempPath(), "imagepixel-backup-rclone-none.conf");
        return startInfo;
    }

    /// <summary>
    /// Ends views that an application left behind when it did not end properly: rclone
    /// processes started from the rclone of this application. Those of other programs,
    /// Imagepixel Drive among them, run from another file and are left alone.
    /// </summary>
    public static void StopLeftovers(string? rclone = null)
    {
        if ((rclone ?? FindRclone()) is not { } program)
        {
            return;
        }

        var full = Path.GetFullPath(program);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(program)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, full, FolderRules.PathComparison))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // gone meanwhile, or one this user may not look into
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
