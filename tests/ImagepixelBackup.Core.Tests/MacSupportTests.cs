using System.Runtime.Versioning;

namespace ImagepixelBackup.Core.Tests;

public sealed class MacSupportTests : IDisposable
{
    private const string Program = "/Applications/Imagepixel Backup.app/Contents/MacOS/ImagepixelBackup";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-mac-" + Guid.NewGuid().ToString("N"));

    private MacLoginItem LoginItem() => new(Path.Combine(_root, "LaunchAgents"));

    [Fact]
    public void Login_item_is_written_and_removed()
    {
        var item = LoginItem();
        Assert.False(item.IsEnabled);

        item.Enable(Program, "--minimized");

        Assert.True(item.IsEnabled);
        var plist = File.ReadAllText(item.FilePath);
        Assert.Contains("<string>de.imagepixel.backup</string>", plist);
        Assert.Contains($"<string>{Program}</string>", plist);
        Assert.Contains("<string>--minimized</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.DoesNotContain("\r", plist);

        item.Disable();
        item.Disable();
        Assert.False(item.IsEnabled);
    }

    [Fact]
    public void Login_item_escapes_characters_that_have_a_meaning_in_the_file()
    {
        var item = LoginItem();

        item.Enable("/Users/anna/Apps & <Tools>/ImagepixelBackup", "--minimized");

        Assert.Contains("<string>/Users/anna/Apps &amp; &lt;Tools&gt;/ImagepixelBackup</string>", File.ReadAllText(item.FilePath));
    }

    [Fact]
    public void Login_item_follows_the_application_when_it_was_moved()
    {
        var item = LoginItem();

        // nothing happens while starting at login is turned off
        item.Refresh(Program, "--minimized");
        Assert.False(item.IsEnabled);

        item.Enable("/Users/anna/Downloads/Imagepixel Backup.app/Contents/MacOS/ImagepixelBackup", "--minimized");
        item.Refresh(Program, "--minimized");
        Assert.Contains($"<string>{Program}</string>", File.ReadAllText(item.FilePath));

        // a place that is gone at the next login never replaces a good one
        item.Refresh("/Volumes/Imagepixel Backup/Imagepixel Backup.app/Contents/MacOS/ImagepixelBackup", "--minimized");
        Assert.Contains($"<string>{Program}</string>", File.ReadAllText(item.FilePath));
    }

    [Theory]
    [InlineData(Program, true)]
    [InlineData("/Users/anna/Applications/Imagepixel Backup.app/Contents/MacOS/ImagepixelBackup", true)]
    [InlineData("/Volumes/Imagepixel Backup/Imagepixel Backup.app/Contents/MacOS/ImagepixelBackup", false)]
    [InlineData("/private/var/folders/ab/c123/T/AppTranslocation/0A1B/d/Imagepixel Backup.app/Contents/MacOS/ImagepixelBackup", false)]
    public void Starting_at_login_needs_a_place_that_is_still_there_at_the_next_login(string program, bool expected)
    {
        Assert.Equal(expected, LoginItem().IsPermanentLocation(program));
    }

    [Theory]
    [InlineData("")]
    [InlineData("geheim")]
    [InlineData("geheim äöü \"mit\" Leerzeichen & 'Zeichen' \\ $HOME")]
    [InlineData("-w fängt wie eine Option an")]
    [InlineData("密码 🔑")]
    public void Secrets_are_written_as_plain_letters_and_digits(string secret)
    {
        var text = SecretText.Encode(secret);

        Assert.Matches("^[A-Za-z0-9]+$", text);
        Assert.Equal(secret, SecretText.Decode(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("geheim")]
    [InlineData("ipb1XYZ")]
    [InlineData("ipb1ABC")]
    public void Text_that_is_no_stored_secret_is_not_taken_for_one(string text)
    {
        Assert.Null(SecretText.Decode(text));
    }

    [SkippableFact]
    [SupportedOSPlatform("macos")]
    public void Secrets_are_kept_in_the_keychain()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "the keychain only exists on macOS");

        // a service of its own, so that the test never touches entries of the application
        var service = "de.imagepixel.backup.test-" + Guid.NewGuid().ToString("N");
        var store = new MacKeychainSecretStore(service);
        const string secret = "geheim äöü \"mit\" Leerzeichen & 'Zeichen' \\ $HOME";
        try
        {
            Assert.Null(store.Get(SecretNames.RepositoryPassword));

            store.Set(SecretNames.RepositoryPassword, secret);
            Assert.Equal(secret, store.Get(SecretNames.RepositoryPassword));

            // what another start of the application finds
            Assert.Equal(secret, new MacKeychainSecretStore(service).Get(SecretNames.RepositoryPassword));
            Assert.Null(new MacKeychainSecretStore(service).Get(SecretNames.SecretAccessKey));

            store.Set(SecretNames.RepositoryPassword, "geändert");
            Assert.Equal("geändert", new MacKeychainSecretStore(service).Get(SecretNames.RepositoryPassword));
        }
        finally
        {
            store.Set(SecretNames.RepositoryPassword, null);
        }

        Assert.Null(new MacKeychainSecretStore(service).Get(SecretNames.RepositoryPassword));
        store.Set(SecretNames.RepositoryPassword, null);
    }

    [SkippableFact]
    [SupportedOSPlatform("macos")]
    public void Names_that_would_need_quoting_are_refused()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "the keychain only exists on macOS");

        Assert.Throws<ArgumentException>(() => new MacKeychainSecretStore("two words"));
        Assert.Throws<ArgumentException>(() => new MacKeychainSecretStore().Get("name; rm"));
        Assert.Throws<ArgumentException>(() => new MacKeychainSecretStore().Set("", "x"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
