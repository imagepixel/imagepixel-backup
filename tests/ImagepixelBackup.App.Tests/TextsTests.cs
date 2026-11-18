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

    [Theory]
    [InlineData(0, "0 Bytes/s")]
    [InlineData(512, "512 Bytes/s")]
    [InlineData(4.2 * 1024 * 1024, "4,2 MB/s")]
    [InlineData(180.0 * 1024 * 1024, "180 MB/s")]
    [InlineData(-5, "0 Bytes/s")]
    public void A_speed_is_an_amount_per_second(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, Texts.Speed(bytesPerSecond));
    }

    [Fact]
    public void A_long_path_keeps_its_beginning_and_the_name_of_the_file()
    {
        var separator = Path.DirectorySeparatorChar;
        var path = string.Join(separator, "Code", "projekt", "node_modules", "ein-sehr-langes-paket", "dist", "esm", "intern", "Datei.js");

        Assert.Equal(path, Texts.ShortenPath(path, 200));

        var shortened = Texts.ShortenPath(path, 40);
        Assert.True(shortened.Length <= 40, shortened);
        Assert.StartsWith("Code" + separator + "…" + separator, shortened);
        Assert.EndsWith(separator + "Datei.js", shortened);

        // a name that is too long on its own is cut at the front
        Assert.Equal("…" + new string('x', 19), Texts.ShortenPath(new string('x', 60), 20));
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
