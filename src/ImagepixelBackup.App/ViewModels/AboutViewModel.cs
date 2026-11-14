using CommunityToolkit.Mvvm.Input;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>Version, licence and the components the application is built on.</summary>
public partial class AboutViewModel(MainViewModel main, ViewModelBase previous) : ViewModelBase
{
    public const string SourceUrl = "https://github.com/imagepixel/imagepixel-backup";
    public const string ResticUrl = "https://restic.net";
    public const string WinFspUrl = "https://github.com/winfsp/winfsp";

    public string Version { get; } =
        "Version " + (typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    [RelayCommand]
    private void Back() => main.CurrentPage = previous;

    [RelayCommand]
    private void OpenLink(string url) => main.OpenInFileManager(url);
}

/// <summary>Converters used by the views.</summary>
public static class Converters
{
    /// <summary>Dims a section that cannot be used yet.</summary>
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, double> EnabledOpacity =
        new(enabled => enabled ? 1.0 : 0.5);
}
