namespace ImagepixelBackup.Core;

/// <summary>What a look through a folder found for one ready-made group of exceptions.</summary>
/// <param name="Preset">The group.</param>
/// <param name="Bytes">How much the files it would leave out take up.</param>
/// <param name="Files">How many files that are.</param>
public sealed record ExclusionFinding(ExclusionPreset Preset, long Bytes, long Files);

/// <summary>The outcome of looking through a folder before its first backup.</summary>
/// <param name="TotalBytes">The size of everything in the folder.</param>
/// <param name="TotalFiles">The number of files in the folder.</param>
/// <param name="Findings">The groups that would leave something out, the largest first.</param>
public sealed record ExclusionScan(long TotalBytes, long TotalFiles, IReadOnlyList<ExclusionFinding> Findings)
{
    /// <summary>
    /// The groups worth suggesting unasked: things that can be made again or that
    /// nobody misses. Videos and setup programs are the user's own files and are only
    /// shown, not ticked.
    /// </summary>
    public static readonly IReadOnlyList<ExclusionPreset> SuggestedPresets = [FolderExclusions.BuildOutput, FolderExclusions.Temporary];

    // Below this size leaving something out is not worth a question.
    private const long WorthAsking = 10 * 1024 * 1024;

    /// <summary>Whether the folder holds enough of what can be made again to suggest leaving it out.</summary>
    public bool IsWorthSuggesting =>
        Findings.Where(finding => SuggestedPresets.Contains(finding.Preset)).Sum(finding => finding.Bytes) >= WorthAsking;
}

/// <summary>
/// Looks through a folder for the things the ready-made groups of exceptions would
/// leave out, and adds up how much space they take. Nothing is read but names and
/// sizes. The sizes are those on the disk; in the backup, which packs and stores
/// every piece only once, the saving is usually somewhat smaller.
/// </summary>
public static class ExclusionScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,

        // links to other places are not followed, as restic does not follow them either
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static ExclusionScan Scan(string folder, CancellationToken cancellationToken = default)
    {
        var bytes = new Dictionary<ExclusionPreset, long>();
        var files = new Dictionary<ExclusionPreset, long>();
        long totalBytes = 0;
        long totalFiles = 0;

        var pending = new Stack<(string Path, ExclusionPreset? Owner)>();
        pending.Push((folder, null));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, owner) = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", Options);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            try
            {
                foreach (var entry in entries)
                {
                    // everything below a folder that is left out belongs to the group that leaves it out
                    var group = owner ?? PresetFor(entry.Name);
                    if (entry is DirectoryInfo)
                    {
                        pending.Push((entry.FullName, group));
                        continue;
                    }

                    var size = ((FileInfo)entry).Length;
                    totalBytes += size;
                    totalFiles++;
                    if (group is not null)
                    {
                        bytes[group] = bytes.GetValueOrDefault(group) + size;
                        files[group] = files.GetValueOrDefault(group) + 1;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a folder that vanished or may not be read while it was listed
            }
        }

        var findings = bytes
            .Select(pair => new ExclusionFinding(pair.Key, pair.Value, files[pair.Key]))
            .OrderByDescending(finding => finding.Bytes)
            .ToList();
        return new ExclusionScan(totalBytes, totalFiles, findings);
    }

    /// <summary>The first group that leaves out a file or folder of this name, if any does.</summary>
    public static ExclusionPreset? PresetFor(string name) =>
        FolderExclusions.AllPresets.FirstOrDefault(preset => preset.Patterns.Any(pattern => Matches(name, pattern)));

    // The patterns of the groups are plain names, "*.ending" or "beginning*".
    private static bool Matches(string name, string pattern)
    {
        if (pattern.StartsWith('*'))
        {
            return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }

        return pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
