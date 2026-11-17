namespace ImagepixelBackup.App.Tests;

/// <summary>The German texts that depend on a number or on the system.</summary>
public sealed class TextsTests
{
    [Theory]
    [InlineData(0, "0 Dateien")]
    [InlineData(1, "1 Datei")]
    [InlineData(2, "2 Dateien")]
    public void A_single_file_is_not_called_files(long count, string expected)
    {
        Assert.Equal(expected, Texts.Files(count));
    }

    [Theory]
    [InlineData(0, "0 neue oder geänderte Dateien")]
    [InlineData(1, "1 neue oder geänderte Datei")]
    [InlineData(14, "14 neue oder geänderte Dateien")]
    public void A_backup_with_one_changed_file_says_so(long count, string expected)
    {
        Assert.Equal(expected, Texts.ChangedFiles(count));
    }

    [Fact]
    public void The_example_for_restoring_is_a_path_of_this_system()
    {
        var example = Texts.RestorePathExample;

        Assert.StartsWith("z. B. ", example);
        Assert.EndsWith("Wiederhergestellt", example);
        // a Windows path on a Mac was what the first test on a Mac showed
        Assert.Equal(OperatingSystem.IsWindows(), example.Contains('\\'));
    }
}
