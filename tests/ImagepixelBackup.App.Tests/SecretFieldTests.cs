using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImagepixelBackup.App.ViewModels;
using ImagepixelBackup.App.Views;
using ImagepixelBackup.Core;

namespace ImagepixelBackup.App.Tests;

/// <summary>
/// Secrets that are hidden on screen must stay hidden from other programs too. Those
/// read a window through the accessibility interface, which Avalonia serves from the
/// automation peers of the controls. A masked text box hands out its dots there, not
/// its text; these tests notice if that ever changes or a field loses its mask.
/// </summary>
public sealed class SecretFieldTests : IDisposable
{
    private const string Secret = "sehr-geheim-123";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-secret-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Masked_text_is_handed_out_as_dots()
    {
        var box = new TextBox { PasswordChar = '●', Text = Secret };
        new Window { Content = box }.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new string('●', Secret.Length), ValueOf(box));

        // what is shown on screen may be read
        box.RevealPassword = true;
        Assert.Equal(Secret, ValueOf(box));
    }

    [AvaloniaFact]
    public async Task Setup_does_not_hand_out_the_secret_key_or_the_password()
    {
        var manager = new BackupManager(new ConfigStore(_root), new MemorySecretStore(), "restic");
        manager.AcceptTerms(Texts.TermsVersion);
        var main = new MainViewModel(manager, []);
        var window = new MainWindow { DataContext = main };
        window.Show();

        // the setup code carries the secret key, so it is a secret as well
        var code = new SetupCode("kunde-4711", "ABCDEFGHIJKLMNOPQRST", Secret, "eu-central-2").Encode();
        var setup = Assert.IsType<SetupViewModel>(main.CurrentPage);
        setup.ChooseModeCommand.Execute("Backup");
        setup.Sources.Add(_root);
        setup.SetupCodeText = code;
        setup.Password = Secret;
        setup.PasswordRepeat = Secret;
        Dispatcher.UIThread.RunJobs();
        Assert.True(setup.CodeAccepted);

        var boxes = window.GetVisualDescendants().OfType<TextBox>().ToList();

        // the code, the password and its repetition are the fields that hold something secret
        var holders = boxes.Where(box => box.Text == Secret || box.Text == code).ToList();
        Assert.Equal(3, holders.Count);
        Assert.All(holders, box => Assert.NotEqual(default, box.PasswordChar));
        Assert.DoesNotContain(boxes, box => ValueOf(box) is { } value && (value.Contains(Secret) || value.Contains(code)));

        // the keys themselves are not shown anywhere
        Assert.DoesNotContain(boxes, box => box.Text == "ABCDEFGHIJKLMNOPQRST");

        window.Close();
        await main.ShutdownAsync();
    }

    private static string? ValueOf(TextBox box) =>
        ControlAutomationPeer.CreatePeerForElement(box).GetProvider<IValueProvider>()?.Value;
}
