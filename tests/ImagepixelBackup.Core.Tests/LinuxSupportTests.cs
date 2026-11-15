using System.Runtime.Versioning;

namespace ImagepixelBackup.Core.Tests;

public sealed class LinuxSupportTests : IDisposable
{
    private const string Program = "/opt/imagepixel-backup/ImagepixelBackup";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-linux-" + Guid.NewGuid().ToString("N"));

    private LinuxLoginItem LoginItem() => new(Path.Combine(_root, "autostart"));

    [Fact]
    public void Login_item_is_a_desktop_entry_in_the_autostart_folder()
    {
        var item = LoginItem();
        Assert.False(item.IsEnabled);

        item.Enable(Program, "--minimized");

        Assert.True(item.IsEnabled);
        Assert.Equal("de.imagepixel.backup.desktop", Path.GetFileName(item.FilePath));
        var lines = File.ReadAllText(item.FilePath).Split('\n');
        Assert.Equal("[Desktop Entry]", lines[0]);
        Assert.Contains("Type=Application", lines);
        Assert.Contains($"Exec=\"{Program}\" \"--minimized\"", lines);
        Assert.Contains("Terminal=false", lines);

        item.Disable();
        item.Disable();
        Assert.False(item.IsEnabled);
    }

    [Fact]
    public void Login_item_quotes_what_has_a_meaning_in_a_desktop_entry()
    {
        var item = LoginItem();

        item.Enable("/home/anna/Meine Programme/50% $neu/Imagepixel\"Backup", "--minimized");

        Assert.Contains(
            "Exec=\"/home/anna/Meine Programme/50%% \\\\$neu/Imagepixel\\\\\"Backup\" \"--minimized\"\n",
            File.ReadAllText(item.FilePath));
    }

    [Fact]
    public void Login_item_follows_the_application_when_it_was_moved()
    {
        var item = LoginItem();
        item.Refresh(Program, "--minimized");
        Assert.False(item.IsEnabled);

        item.Enable("/home/anna/imagepixel-backup/ImagepixelBackup", "--minimized");
        item.Refresh(Program, "--minimized");
        Assert.Contains($"Exec=\"{Program}\"", File.ReadAllText(item.FilePath));

        // a program in the temp folder is gone at the next login
        Assert.False(item.IsPermanentLocation("/tmp/ipb/ImagepixelBackup"));
        item.Refresh("/tmp/ipb/ImagepixelBackup", "--minimized");
        Assert.Contains($"Exec=\"{Program}\"", File.ReadAllText(item.FilePath));
    }

    [SkippableFact]
    [SupportedOSPlatform("linux")]
    public void Secrets_are_kept_in_the_keyring_of_the_desktop()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "this keyring only exists on Linux");
        Skip.If(
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")),
            "there is no desktop session with a keyring");

        // a service of its own, so that the test never touches entries of the application
        var service = "de.imagepixel.backup.test-" + Guid.NewGuid().ToString("N");
        var store = new LinuxSecretStore(service);
        const string secret = "geheim äöü \"mit\" Leerzeichen & 'Zeichen' \\ $HOME\nzweite Zeile";
        try
        {
            Assert.Null(store.Get(SecretNames.RepositoryPassword));

            store.Set(SecretNames.RepositoryPassword, secret);
            Assert.Equal(secret, store.Get(SecretNames.RepositoryPassword));

            // what another start of the application finds
            Assert.Equal(secret, new LinuxSecretStore(service).Get(SecretNames.RepositoryPassword));
            Assert.Null(new LinuxSecretStore(service).Get(SecretNames.SecretAccessKey));

            store.Set(SecretNames.RepositoryPassword, "geändert");
            Assert.Equal("geändert", new LinuxSecretStore(service).Get(SecretNames.RepositoryPassword));
        }
        finally
        {
            store.Set(SecretNames.RepositoryPassword, null);
        }

        Assert.Null(new LinuxSecretStore(service).Get(SecretNames.RepositoryPassword));
        store.Set(SecretNames.RepositoryPassword, null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
