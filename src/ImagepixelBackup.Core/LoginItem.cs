using System.Security;

namespace ImagepixelBackup.Core;

/// <summary>
/// Starts the application when the user logs in, so that scheduled backups run without
/// the user opening it. macOS and Linux have this; on Windows the setup program takes
/// care of it instead.
/// </summary>
public interface ILoginItem
{
    /// <summary>The file that tells the system to start the application.</summary>
    string FilePath { get; }

    bool IsEnabled { get; }

    /// <summary>Whether a program lies where it will still be found at the next login.</summary>
    bool IsPermanentLocation(string executablePath);

    /// <summary>Makes the system start the given program, without its window, at every login.</summary>
    void Enable(string executablePath, string hiddenArgument);

    /// <summary>
    /// Points the system to the program again if the application was moved since
    /// starting at login was turned on. Does nothing while it is turned off.
    /// </summary>
    void Refresh(string executablePath, string hiddenArgument);

    void Disable();
}

public static class LoginItem
{
    /// <summary>Returns how this system starts programs at login, or null if the application leaves that to its setup program.</summary>
    public static ILoginItem? Create() =>
        OperatingSystem.IsMacOS() ? new MacLoginItem()
        : OperatingSystem.IsLinux() ? new LinuxLoginItem()
        : null;
}

/// <summary>A login item that is a single file in a folder the system looks into at login.</summary>
public abstract class FileLoginItem(string directory, string fileName) : ILoginItem
{
    public string FilePath => Path.Combine(directory, fileName);

    public bool IsEnabled => File.Exists(FilePath);

    public abstract bool IsPermanentLocation(string executablePath);

    /// <summary>What the file has to say to start the program.</summary>
    protected abstract string Content(string executablePath, string hiddenArgument);

    public void Enable(string executablePath, string hiddenArgument)
    {
        var content = Content(executablePath, hiddenArgument).ReplaceLineEndings("\n");
        if (!IsEnabled || File.ReadAllText(FilePath) != content)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, content);
        }
    }

    public void Refresh(string executablePath, string hiddenArgument)
    {
        if (IsEnabled && IsPermanentLocation(executablePath))
        {
            Enable(executablePath, hiddenArgument);
        }
    }

    public void Disable()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}

/// <summary>macOS: a launch agent in the user's Library folder.</summary>
public sealed class MacLoginItem(string? launchAgentsDirectory = null) : FileLoginItem(
    launchAgentsDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"),
    Label + ".plist")
{
    private const string Label = "de.imagepixel.backup";

    /// <summary>
    /// Not while the application is opened from its disk image, or from the temporary
    /// copy macOS runs an application from that was not moved after its download.
    /// </summary>
    public override bool IsPermanentLocation(string executablePath) =>
        !executablePath.StartsWith("/Volumes/", StringComparison.Ordinal)
        && !executablePath.Contains("/AppTranslocation/", StringComparison.Ordinal);

    protected override string Content(string executablePath, string hiddenArgument) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{Label}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(executablePath)}</string>
                <string>{SecurityElement.Escape(hiddenArgument)}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>ProcessType</key>
            <string>Interactive</string>
        </dict>
        </plist>

        """;
}

/// <summary>Linux: a desktop entry in the autostart folder of the user, which the common desktops honour.</summary>
public sealed class LinuxLoginItem(string? autostartDirectory = null) : FileLoginItem(
    autostartDirectory ?? Path.Combine(ConfigHome, "autostart"),
    "de.imagepixel.backup.desktop")
{
    private static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    /// <summary>Not from the temp folder, where a program that was only unpacked to try it would lie.</summary>
    public override bool IsPermanentLocation(string executablePath) =>
        !executablePath.StartsWith("/tmp/", StringComparison.Ordinal);

    protected override string Content(string executablePath, string hiddenArgument) => $"""
        [Desktop Entry]
        Type=Application
        Name=Imagepixel Backup
        Comment=Sichert deine Ordner im Hintergrund
        Exec={Quote(executablePath)} {Quote(hiddenArgument)}
        Icon=imagepixel-backup
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;

    // The rules of desktop entries: an argument in double quotes, in which the
    // characters with a meaning of their own are escaped, and every backslash doubled
    // once more because the value of a key is a string.
    private static string Quote(string argument)
    {
        var quoted = new System.Text.StringBuilder("\"");
        foreach (var c in argument)
        {
            quoted.Append(c switch
            {
                '"' or '`' or '$' => "\\\\" + c,
                '\\' => "\\\\\\\\",
                '%' => "%%",
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }
}
