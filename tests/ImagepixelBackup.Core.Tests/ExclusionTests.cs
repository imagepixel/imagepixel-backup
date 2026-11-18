namespace ImagepixelBackup.Core.Tests;

/// <summary>What the backup of a folder leaves out, and how restic is told.</summary>
public sealed class ExclusionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipb-excl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Nothing_is_left_out_unless_the_user_asks_for_it()
    {
        var folder = new BackupFolder { Path = "x" };

        Assert.Empty(folder.Exclusions.ToResticArguments());
        Assert.Equal(0, folder.Exclusions.Count());
    }

    [Fact]
    public void Presets_become_the_patterns_they_stand_for()
    {
        var exclusions = new FolderExclusions { Presets = [FolderExclusions.BuildOutput.Id, FolderExclusions.Videos.Id, "gibt-es-nicht"] };

        var arguments = exclusions.ToResticArguments();

        Assert.Contains("--iexclude=node_modules", arguments);
        Assert.Contains("--iexclude=obj", arguments);
        Assert.Contains("--iexclude=*.mp4", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("tmp"));
        Assert.All(arguments, argument => Assert.StartsWith("--iexclude=", argument));

        // a preset the application does not know is not counted
        Assert.Equal(2, exclusions.Count());
    }

    [Fact]
    public void No_preset_leaves_out_the_history_of_a_project()
    {
        Assert.DoesNotContain(
            FolderExclusions.AllPresets.SelectMany(preset => preset.Patterns),
            pattern => pattern is ".git" or ".*" or "*");
    }

    [Theory]
    [InlineData(ExclusionKind.Extension, "log", "*.log")]
    [InlineData(ExclusionKind.Extension, " *.LOG ", "*.LOG")]
    [InlineData(ExclusionKind.Extension, ".tar.gz", "*.tar.gz")]
    [InlineData(ExclusionKind.Name, "node_modules", "node_modules")]
    [InlineData(ExclusionKind.Name, " Alte Fotos ", "Alte Fotos")]
    [InlineData(ExclusionKind.NamePrefix, ".", ".*")]
    [InlineData(ExclusionKind.NamePrefix, "~$", "~$*")]
    public void A_rule_of_the_user_becomes_one_pattern(ExclusionKind kind, string value, string expected)
    {
        var exclusions = new FolderExclusions { Rules = [new ExclusionRule { Kind = kind, Value = value }] };

        Assert.Equal(["--iexclude=" + expected], exclusions.ToResticArguments());
        Assert.Equal(1, exclusions.Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("ordner/datei")]
    [InlineData(@"ordner\datei")]
    [InlineData("a*b")]
    [InlineData("datei?")]
    [InlineData("--exclude-file=x\ny")]
    public void What_is_not_a_plain_name_is_not_a_rule(string value)
    {
        Assert.Null(FolderExclusions.Normalize(ExclusionKind.Name, value));

        var exclusions = new FolderExclusions { Rules = [new ExclusionRule { Kind = ExclusionKind.Name, Value = value }] };
        Assert.Empty(exclusions.ToResticArguments());
        Assert.Equal(0, exclusions.Count());
    }

    [Fact]
    public void A_size_limit_is_passed_in_megabytes_and_the_same_pattern_only_once()
    {
        var exclusions = new FolderExclusions
        {
            Presets = [FolderExclusions.BuildOutput.Id],
            Rules =
            [
                new ExclusionRule { Kind = ExclusionKind.Name, Value = "NODE_MODULES" },
                new ExclusionRule { Kind = ExclusionKind.Extension, Value = "log" },
            ],
            LargerThanMb = 2048,
        };

        var arguments = exclusions.ToResticArguments();

        Assert.Single(arguments, argument => argument.Equals("--iexclude=node_modules", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("--exclude-larger-than=2048M", arguments[^1]);
        Assert.Equal(4, exclusions.Count());
    }

    [Fact]
    public void Exclusions_are_kept_with_the_settings_and_older_settings_have_none()
    {
        var store = new ConfigStore(_root);
        var config = new AppConfig();
        config.Folders.Add(new BackupFolder
        {
            Path = "C:\\Daten",
            Exclusions = new FolderExclusions
            {
                Presets = [FolderExclusions.Temporary.Id],
                Rules = [new ExclusionRule { Kind = ExclusionKind.Extension, Value = "log" }],
                LargerThanMb = 500,
            },
        });
        store.Save(config);

        var loaded = store.Load().Folders[0].Exclusions;
        Assert.Equal([FolderExclusions.Temporary.Id], loaded.Presets);
        Assert.Equal(ExclusionKind.Extension, Assert.Single(loaded.Rules).Kind);
        Assert.Equal(500, loaded.LargerThanMb);

        // settings written before there were exclusions
        File.WriteAllText(Path.Combine(_root, "config.json"), """{"Folders":[{"Path":"C:\\Daten","Schedule":"Hourly"}]}""");
        Assert.Empty(store.Load().Folders[0].Exclusions.ToResticArguments());
    }
}
