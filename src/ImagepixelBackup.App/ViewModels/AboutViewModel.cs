using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>Version, licence and the components the application is built on.</summary>
public partial class AboutViewModel : ViewModelBase
{
    public const string SourceUrl = "https://github.com/imagepixel/imagepixel-backup";
    public const string GuideUrl = "https://github.com/imagepixel/imagepixel-backup/wiki";
    public const string TermsUrl = "https://github.com/imagepixel/imagepixel-backup/blob/main/NUTZUNGSHINWEISE.md";
    public const string ResticUrl = "https://restic.net";
    public const string WinFspUrl = "https://github.com/winfsp/winfsp";
    public const string RcloneUrl = "https://rclone.org";

    private readonly MainViewModel _main;
    private readonly ViewModelBase _previous;

    public AboutViewModel(MainViewModel main, ViewModelBase previous)
    {
        _main = main;
        _previous = previous;
        StartAtLogin = main.LoginItem is { IsEnabled: true };
    }

    public string Version { get; } =
        "Version " + (typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    /// <summary>WinFsp is only part of the application where it shows the backup as a drive.</summary>
    public bool ShowWinFsp => OperatingSystem.IsWindows();

    /// <summary>rclone comes with the versions that show drives: Windows and Linux.</summary>
    public bool ShowRclone => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>A setup code or storage can only be changed once one is set up.</summary>
    public bool CanChangeStorage => _main.HasStorage;

    public string ChangeStorageHint => Texts.ChangeStorageHint(_main.Pairing is not null);

    [RelayCommand]
    private void ChangeStorage() => _main.ShowStorageChange();

    /// <summary>
    /// On macOS the application itself registers to start at login. On Windows the
    /// setup program offers this, so there is nothing to choose here.
    /// </summary>
    public bool ShowStartAtLogin => _main.LoginItem is not null;

    [ObservableProperty]
    public partial bool StartAtLogin { get; set; }

    /// <summary>What keeps the application from starting at login, if anything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartAtLoginProblem))]
    public partial string? StartAtLoginProblem { get; set; }

    public bool HasStartAtLoginProblem => StartAtLoginProblem is not null;

    partial void OnStartAtLoginChanged(bool value)
    {
        if (_main.LoginItem is not { } loginItem || value == loginItem.IsEnabled)
        {
            return;
        }

        if (value)
        {
            StartAtLoginProblem = _main.EnableStartAtLogin();
            if (StartAtLoginProblem is not null)
            {
                // once the box has taken the click, it is cleared again
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StartAtLogin = false);
            }
        }
        else
        {
            loginItem.Disable();
            StartAtLoginProblem = null;
        }
    }

    [RelayCommand]
    private void Back()
    {
        // the overview tells whether the application starts at login, which may have changed here
        (_previous as DashboardViewModel)?.RefreshStartAtLogin();
        _main.CurrentPage = _previous;
    }

    [RelayCommand]
    private void OpenLink(string url) => _main.OpenInFileManager(url);
}

/// <summary>Converters used by the views.</summary>
public static class Converters
{
    /// <summary>Dims a section that cannot be used yet.</summary>
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, double> EnabledOpacity =
        new(enabled => enabled ? 1.0 : 0.5);
}
