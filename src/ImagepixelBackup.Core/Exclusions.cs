namespace ImagepixelBackup.Core;

/// <summary>What a rule the user wrote themselves leaves out.</summary>
public enum ExclusionKind
{
    /// <summary>Files with an ending such as ".log".</summary>
    Extension,

    /// <summary>Everything with exactly this name, such as the folder "node_modules".</summary>
    Name,

    /// <summary>Everything whose name begins with this, such as ".".</summary>
    NamePrefix,
}

/// <summary>One rule the user wrote themselves.</summary>
public sealed class ExclusionRule
{
    public ExclusionKind Kind { get; set; }

    public string Value { get; set; } = "";
}

/// <summary>A ready-made group of things to leave out, offered to the user in plain words.</summary>
/// <param name="Id">The name the preset is stored under.</param>
/// <param name="Patterns">What restic is told to leave out.</param>
public sealed record ExclusionPreset(string Id, IReadOnlyList<string> Patterns);

/// <summary>
/// What the backup of a folder leaves out. Nothing is left out unless the user asks for
/// it. The rules only shape backups that are made from then on: what earlier backups
/// saved stays in their states.
/// </summary>
public sealed class FolderExclusions
{
    /// <summary>Files and folders that programs create by the way and nobody misses.</summary>
    public static readonly ExclusionPreset Temporary = new(
        "temporary",
        ["*.tmp", "*.temp", "~$*", "Thumbs.db", ".DS_Store", "desktop.ini", "$RECYCLE.BIN", "System Volume Information"]);

    /// <summary>Folders that development tools build again from the project itself.</summary>
    public static readonly ExclusionPreset BuildOutput = new(
        "build-output",
        ["node_modules", "bin", "obj", "dist", ".next", ".nuxt", "__pycache__", ".venv"]);

    public static readonly ExclusionPreset Videos = new(
        "videos",
        ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.wmv", "*.m4v"]);

    /// <summary>Setup programs and disk images, which can usually be downloaded again.</summary>
    public static readonly ExclusionPreset Installers = new(
        "installers",
        ["*.iso", "*.msi", "*.exe"]);

    /// <summary>The presets in the order they are offered.</summary>
    public static readonly IReadOnlyList<ExclusionPreset> AllPresets = [Temporary, BuildOutput, Videos, Installers];

    /// <summary>The ids of the presets the user switched on.</summary>
    public List<string> Presets { get; set; } = [];

    /// <summary>The rules the user wrote themselves.</summary>
    public List<ExclusionRule> Rules { get; set; } = [];

    /// <summary>Files larger than this many megabytes are left out, or null for no limit.</summary>
    public int? LargerThanMb { get; set; }

    /// <summary>How many things are switched on: presets, own rules and the size limit.</summary>
    public int Count() =>
        Presets.Count(id => AllPresets.Any(preset => preset.Id == id))
        + Rules.Count(rule => PatternOf(rule) is not null)
        + (LargerThanMb is > 0 ? 1 : 0);

    /// <summary>
    /// Brings what the user typed for a rule into the form it is stored in: no stars,
    /// no surrounding spaces, an ending without its dot. Returns null if nothing usable
    /// is left, for example because the text names a path instead of a name.
    /// </summary>
    public static string? Normalize(ExclusionKind kind, string? value)
    {
        var text = (value ?? "").Trim().Trim('*').Trim();
        if (kind == ExclusionKind.Extension)
        {
            text = text.TrimStart('.');
        }

        // a rule names one file or folder name, never a path, and no pattern of its own
        return text.Length == 0 || text.IndexOfAny(['/', '\\', '*', '?', '[', ']', '"', '\n', '\r']) >= 0
            ? null
            : text;
    }

    /// <summary>The pattern restic gets for a rule, or null if the rule is not usable.</summary>
    public static string? PatternOf(ExclusionRule rule) => Normalize(rule.Kind, rule.Value) is not { } value
        ? null
        : rule.Kind switch
        {
            ExclusionKind.Extension => "*." + value,
            ExclusionKind.NamePrefix => value + "*",
            _ => value,
        };

    /// <summary>
    /// The arguments that make restic leave out what the user asked for. Names are
    /// matched in every folder below the one that is backed up, without regard to
    /// upper and lower case. restic cannot tell a folder from a file of the same name.
    /// </summary>
    public IReadOnlyList<string> ToResticArguments()
    {
        var patterns = AllPresets
            .Where(preset => Presets.Contains(preset.Id))
            .SelectMany(preset => preset.Patterns)
            .Concat(Rules.Select(PatternOf).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var arguments = patterns.Select(pattern => "--iexclude=" + pattern).ToList();
        if (LargerThanMb is > 0)
        {
            arguments.Add($"--exclude-larger-than={LargerThanMb}M");
        }

        return arguments;
    }
}
