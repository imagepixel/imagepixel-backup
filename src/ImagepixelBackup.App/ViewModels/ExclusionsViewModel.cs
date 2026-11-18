using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImagepixelBackup.Core;

namespace ImagepixelBackup.App.ViewModels;

/// <summary>One ready-made suggestion on the page of exceptions, to be ticked or not.</summary>
public partial class PresetOption(ExclusionPreset preset) : ObservableObject
{
    public ExclusionPreset Preset { get; } = preset;

    public string Title { get; } = Texts.PresetTitle(preset.Id);

    /// <summary>What the suggestion leaves out, so that nobody has to guess.</summary>
    public string Detail { get; } = "Das sind: " + string.Join(", ", preset.Patterns);

    [ObservableProperty]
    public partial bool IsOn { get; set; }
}

/// <summary>One rule the customer wrote, as a line of the list.</summary>
public sealed record RuleItem(ExclusionKind Kind, string Value)
{
    public string Text => Texts.Rule(Kind, Value);
}

/// <summary>
/// The page where the customer says what the backup of one folder leaves out:
/// suggestions in plain words, rules of their own, and a limit for the size of files.
/// Nothing is left out until they save, and nothing is ticked for them.
/// </summary>
public partial class ExclusionsViewModel : ViewModelBase
{
    private static readonly int[] SizesInMb = [500, 1024, 2048, 5120, 10240];

    private readonly MainViewModel _main;
    private readonly BackupManager _manager;
    private readonly FolderItemViewModel _folder;

    public ExclusionsViewModel(MainViewModel main, BackupManager manager, FolderItemViewModel folder)
    {
        _main = main;
        _manager = manager;
        _folder = folder;

        var current = folder.Folder.Exclusions;
        Presets = [.. FolderExclusions.AllPresets.Select(preset => new PresetOption(preset) { IsOn = current.Presets.Contains(preset.Id) })];
        foreach (var rule in current.Rules)
        {
            if (FolderExclusions.Normalize(rule.Kind, rule.Value) is { } value)
            {
                Rules.Add(new RuleItem(rule.Kind, value));
            }
        }

        Rules.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LeavesOutGit));
        LimitSize = current.LargerThanMb is > 0;
        SizeIndex = Array.IndexOf(SizesInMb, current.LargerThanMb ?? 2048) is >= 0 and var index ? index : 2;
    }

    public string Title => $"Ausnahmen für „{_folder.Name}“";

    public string Path => _folder.Path;

    public IReadOnlyList<PresetOption> Presets { get; }

    public ObservableCollection<RuleItem> Rules { get; } = [];

    public IReadOnlyList<string> KindOptions { get; } =
        ["Dateien mit der Endung", "Dateien und Ordner mit dem Namen", "Namen, die beginnen mit"];

    [ObservableProperty]
    public partial int NewKindIndex { get; set; }

    [ObservableProperty]
    public partial string NewValue { get; set; } = "";

    /// <summary>Why what was typed could not be added as a rule.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuleMessage))]
    public partial string? RuleMessage { get; set; }

    public bool HasRuleMessage => RuleMessage is not null;

    /// <summary>
    /// Whether a rule leaves out the folder ".git", which holds the whole history of
    /// a project. Worth a word, because a rule for names with a dot does it unnoticed.
    /// </summary>
    public bool LeavesOutGit => Rules.Any(rule => rule.Kind switch
    {
        ExclusionKind.Name => string.Equals(rule.Value, ".git", StringComparison.OrdinalIgnoreCase),
        ExclusionKind.NamePrefix => ".git".StartsWith(rule.Value, StringComparison.OrdinalIgnoreCase),
        _ => false,
    });

    public IReadOnlyList<string> SizeOptions { get; } = [.. SizesInMb.Select(mb => Texts.Bytes(mb * 1024L * 1024))];

    [ObservableProperty]
    public partial bool LimitSize { get; set; }

    [ObservableProperty]
    public partial int SizeIndex { get; set; }

    [RelayCommand]
    private void AddRule()
    {
        var kind = (ExclusionKind)Math.Clamp(NewKindIndex, 0, 2);
        if (FolderExclusions.Normalize(kind, NewValue) is not { } value)
        {
            RuleMessage = "Bitte gib eine Endung oder einen Namen ein, ohne Pfad und ohne Platzhalter.";
            return;
        }

        if (Rules.Any(rule => rule.Kind == kind && string.Equals(rule.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            RuleMessage = "Diese Regel gibt es schon.";
            return;
        }

        Rules.Add(new RuleItem(kind, value));
        NewValue = "";
        RuleMessage = null;
    }

    [RelayCommand]
    private void RemoveRule(RuleItem rule) => Rules.Remove(rule);

    /// <summary>Takes over what the page shows. The next backup of the folder follows it.</summary>
    [RelayCommand]
    private void Save()
    {
        var exclusions = _folder.Folder.Exclusions;
        exclusions.Presets = [.. Presets.Where(option => option.IsOn).Select(option => option.Preset.Id)];
        exclusions.Rules = [.. Rules.Select(rule => new ExclusionRule { Kind = rule.Kind, Value = rule.Value })];
        exclusions.LargerThanMb = LimitSize ? SizesInMb[Math.Clamp(SizeIndex, 0, SizesInMb.Length - 1)] : null;

        // the customer has dealt with the exceptions themselves: no suggestion is needed any more
        _folder.Folder.ExclusionsDecided = true;
        _manager.Save();
        _folder.DropSuggestion();
        _folder.RefreshExclusions();
        _main.ShowDashboard();
    }

    [RelayCommand]
    private void Cancel() => _main.ShowDashboard();
}
