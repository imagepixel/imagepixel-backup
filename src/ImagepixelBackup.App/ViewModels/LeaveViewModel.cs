using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>
/// Shown instead of silently doing what was asked when the customer closes the window
/// or quits the application at a moment where that needs a word: while a backup runs,
/// and the first time the window is closed at all. Closing the window never stops a
/// backup, because the application keeps running in the background; quitting does.
/// </summary>
public partial class LeaveViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly ViewModelBase _previous;

    public LeaveViewModel(MainViewModel main, ViewModelBase previous, bool quitting, bool backupRunning)
    {
        _main = main;
        _previous = previous;
        IsQuitting = quitting;
        IsBackupRunning = backupRunning;
    }

    /// <summary>Whether the customer asked to quit the application, not only to close its window.</summary>
    public bool IsQuitting { get; }

    public bool IsBackupRunning { get; }

    /// <summary>Closing the window is offered unless the customer asked to quit.</summary>
    public bool CanCloseWindow => !IsQuitting;

    public string Title => (IsQuitting, IsBackupRunning) switch
    {
        (true, _) => "Es läuft gerade eine Sicherung",
        (false, true) => "Die Sicherung läuft im Hintergrund weiter",
        _ => "Imagepixel Backup läuft im Hintergrund weiter",
    };

    public string Text => (IsQuitting, IsBackupRunning) switch
    {
        (true, _) =>
            "Wenn du das Programm jetzt beendest, wird die laufende Sicherung abgebrochen. Sie wird beim nächsten Start "
            + "von selbst fortgesetzt; was schon im Backup liegt, wird nicht noch einmal übertragen.",
        (false, true) =>
            $"Du kannst das Fenster schließen. Das Programm bleibt {Texts.TrayPlace} geöffnet und sichert zu Ende. "
            + "Du erkennst es dort am bunten Handabdruck; ein Klick darauf holt das Fenster zurück.",
        _ =>
            $"Wenn du das Fenster schließt, bleibt das Programm {Texts.TrayPlace} geöffnet und sichert deine Ordner nach Zeitplan. "
            + "Du erkennst es dort am bunten Handabdruck; ein Klick darauf holt das Fenster zurück. "
            + "Nur wenn du es beendest, finden keine Sicherungen mehr statt.",
    };

    public string StayText => IsQuitting ? "Weiterlaufen lassen" : "Zurück";

    public string QuitText => IsBackupRunning ? "Programm beenden und Sicherung abbrechen" : "Programm beenden";

    /// <summary>Hides the window; the application and its backups go on.</summary>
    [RelayCommand]
    private void CloseWindow()
    {
        _main.CurrentPage = _previous;
        _main.HideWindow();
    }

    /// <summary>Back to where the customer was, as if nothing had been asked.</summary>
    [RelayCommand]
    private void Stay() => _main.CurrentPage = _previous;

    [RelayCommand]
    private async Task QuitAsync()
    {
        _main.CurrentPage = _previous;
        await _main.Quit();
    }
}
