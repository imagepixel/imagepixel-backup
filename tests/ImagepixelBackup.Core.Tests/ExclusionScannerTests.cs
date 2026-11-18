namespace ImagepixelBackup.Core.Tests;

/// <summary>The look through a folder before its first backup.</summary>
public sealed class ExclusionScannerTests : IDisposable
{
    private const int Mb = 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-scan-" + Guid.NewGuid().ToString("N"));

    public ExclusionScannerTests()
    {
        Write(new byte[100], "Brief.txt");
        Write(new byte[200], "Projekt", "Programm.cs");
        Write(new byte[6 * Mb], "Projekt", "node_modules", "paket", "index.js");
        Write(new byte[5 * Mb], "Projekt", "Web", "NODE_MODULES", "anderes", "lib.js");

        // a video inside a folder that is left out anyway belongs to that folder
        Write(new byte[Mb], "Projekt", "node_modules", "paket", "demo.mp4");
        Write(new byte[2 * Mb], "Urlaub.MP4");
        Write(new byte[300], "Thumbs.db");
        Write(new byte[400], ".git", "HEAD");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_look_adds_up_what_each_group_would_leave_out()
    {
        var scan = ExclusionScanner.Scan(_root);

        Assert.Equal(8, scan.TotalFiles);
        Assert.Equal(14L * Mb + 1000, scan.TotalBytes);

        var build = Assert.Single(scan.Findings, finding => finding.Preset == FolderExclusions.BuildOutput);
        Assert.Equal(12L * Mb, build.Bytes);
        Assert.Equal(3, build.Files);

        var videos = Assert.Single(scan.Findings, finding => finding.Preset == FolderExclusions.Videos);
        Assert.Equal(2L * Mb, videos.Bytes);
        Assert.Equal(1, videos.Files);

        var temporary = Assert.Single(scan.Findings, finding => finding.Preset == FolderExclusions.Temporary);
        Assert.Equal(300, temporary.Bytes);

        // the largest comes first, and nothing is found for setup programs
        Assert.Same(build, scan.Findings[0]);
        Assert.DoesNotContain(scan.Findings, finding => finding.Preset == FolderExclusions.Installers);
        Assert.True(scan.IsWorthSuggesting);
    }

    [Fact]
    public void A_folder_without_typical_things_is_not_worth_a_question()
    {
        Directory.Delete(Path.Combine(_root, "Projekt"), recursive: true);

        var scan = ExclusionScanner.Scan(_root);

        // a video and a small leftover are the user's own business
        Assert.False(scan.IsWorthSuggesting);
        Assert.Equal(4, scan.TotalFiles);
    }

    [Fact]
    public void The_history_of_a_project_is_never_suggested()
    {
        Assert.Null(ExclusionScanner.PresetFor(".git"));
        Assert.Null(ExclusionScanner.PresetFor("Programm.cs"));
        Assert.Same(FolderExclusions.BuildOutput, ExclusionScanner.PresetFor("Node_Modules"));
        Assert.Same(FolderExclusions.Temporary, ExclusionScanner.PresetFor("~$Brief.docx"));
        Assert.Same(FolderExclusions.Installers, ExclusionScanner.PresetFor("Setup.EXE"));
    }

    [Fact]
    public void The_look_can_be_stopped()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ExclusionScanner.Scan(_root, cancellation.Token));
    }

    private void Write(byte[] content, params string[] path)
    {
        var file = Path.Combine([_root, .. path]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, content);
    }
}
