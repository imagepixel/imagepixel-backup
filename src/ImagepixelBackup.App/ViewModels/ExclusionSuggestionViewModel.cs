using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImagepixelBackup.Core;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>One group of things the look through the folder found, to be left out or not.</summary>
public partial class SuggestionOption(ExclusionFinding finding, bool isOn) : ObservableObject
{
    public ExclusionFinding Finding { get; } = finding;

    public string Title { get; } = Texts.PresetTitle(finding.Preset.Id);

    /// <summary>How much the group takes up in this folder.</summary>
    public string Size { get; } = $"etwa {Texts.Bytes(finding.Bytes)} in {Texts.Files(finding.Files)}";

    public string Detail { get; } = "Das sind: " + string.Join(", ", finding.Preset.Patterns);

    [ObservableProperty]
    public partial bool IsOn { get; set; } = isOn;
}

/// <summary>
/// Shown before the first backup of a folder that holds typical things worth leaving
/// out, such as the folders development tools build again. It says how much space
/// they take and waits for an answer: the backup starts once the customer has taken
/// over the suggestion or turned it down.
/// </summary>
public partial class ExclusionSuggestionViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly FolderItemViewModel _folder;
    private readonly ExclusionScan _scan;

    public ExclusionSuggestionViewModel(MainViewModel main, FolderItemViewModel folder, ExclusionScan scan)
    {
        _main = main;
        _folder = folder;
        _scan = scan;

        // what can be made again is ticked; the customer's own files are only shown
        Options = [.. scan.Findings.Select(finding => new SuggestionOption(finding, ExclusionScan.SuggestedPresets.Contains(finding.Preset)))];
        foreach (var option in Options)
        {
            option.PropertyChanged += OnOptionChanged;
        }
    }

    public string Title => $"Vorschlag für „{_folder.Name}“";

    public string Path => _folder.Path;

    public IReadOnlyList<SuggestionOption> Options { get; }

    private long SavedBytes => Options.Where(option => option.IsOn).Sum(option => option.Finding.Bytes);

    /// <summary>What the ticked groups add up to, compared with the whole folder.</summary>
    public string Summary => SavedBytes > 0
        ? $"Mit den angehakten Ausnahmen sparst du etwa {Texts.Bytes(SavedBytes)} von {Texts.Bytes(_scan.TotalBytes)}."
        : $"Ohne Ausnahmen wird der ganze Ordner gesichert: {Texts.Bytes(_scan.TotalBytes)}.";

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Summary));

    /// <summary>Backs the folder up without what is ticked.</summary>
    [RelayCommand]
    private async Task AcceptAsync()
    {
        string[] presets = [.. Options.Where(option => option.IsOn).Select(option => option.Finding.Preset.Id)];
        _main.ShowDashboard();
        await _folder.DecideExclusionsAsync(presets);
    }

    /// <summary>Backs the whole folder up.</summary>
    [RelayCommand]
    private async Task DeclineAsync()
    {
        _main.ShowDashboard();
        await _folder.DecideExclusionsAsync([]);
    }
}
