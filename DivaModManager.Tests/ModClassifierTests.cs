using System.Buffers.Binary;
using Xunit;

namespace DivaModManager.Tests;

public sealed class ModClassifierTests
{
    [Fact]
    public void DetectsNewClassicsMixedCustomSong()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.Write("rom/nc_db.toml", NewClassicsDatabase("MIXED", 4726));
        mod.WriteBinary("rom/script/pv_4726_extreme.dsc", 0x14050921);
        mod.Write("rom/sound/song/pv_4726.ogg");
        mod.Write("rom/movie/pv_4726.usm");

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal("Custom Song", result.PrimaryCategory);
        Assert.Contains("New Classics (Mixed)", result.FormatVariant);
        Assert.Equal(ModClassificationConfidence.High, result.Confidence);
    }

    [Fact]
    public void DetectsNewClassicsAdditionalDifficultiesWithoutMedia()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.Write("rom/nc_db.toml", NewClassicsDatabase("CONSOLE", 9887));
        mod.WriteBinary("rom/script_nc/pv_9887_extreme.dsc", 0x12020220);

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal("Additional Difficulties", result.PrimaryCategory);
        Assert.Contains("New Classics (Console)", result.FormatVariant);
        Assert.DoesNotContain("Custom Song", result.DetectedCategories);
    }

    [Fact]
    public void FolderNameCannotTurnLegacyContentIntoNewClassics()
    {
        using var mod = new TemporaryMod("New Classics Script Pack");
        mod.WriteConfig();
        mod.Write("rom/mod_pv_db.txt", "pv_8550.name=Example");
        mod.WriteBinary("rom/script/pv_8550_hard.dsc", 0x12020220);
        mod.Write("rom/movie/pv_8550.usm");

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal("Custom Song", result.PrimaryCategory);
        Assert.Equal("Legacy", result.FormatVariant);
        Assert.DoesNotContain("New Classics", result.FormatVariant);
    }

    [Fact]
    public void HonorsConfiguredIncludeRootsAndWrapperDirectories()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig("AP");
        mod.Write("AP/rom/mod_pv_db.txt", "pv_144.name=Example");
        mod.Write("AP/rom/nc_db.toml", NewClassicsDatabase("CONSOLE", 144));
        mod.WriteBinary("AP/rom/script_nc/pv_144.dsc", 0x12020220);
        mod.Write("AP/rom/sound/song/pv_144.ogg");
        mod.Write("AP/rom/movie/pv_144.usm");
        mod.Write("backup/rom/nc_db.toml", NewClassicsDatabase("MIXED", 9999));

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal("Custom Song", result.PrimaryCategory);
        Assert.Contains("Legacy", result.FormatVariant);
        Assert.Contains("New Classics (Console)", result.FormatVariant);
        Assert.DoesNotContain("Mixed", result.FormatVariant);
    }

    [Fact]
    public void NormalizesDotSegmentsInIncludeRoots()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig("./content/./");
        mod.Write("content/rom/mod_gm_module_tbl.farc");

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal("Module", result.PrimaryCategory);
    }

    [Fact]
    public void ExplicitEmptyIncludeListDoesNotScanUnloadedContent()
    {
        using var mod = new TemporaryMod();
        mod.Write("config.toml", "enabled = true\ninclude = []\n");
        mod.Write("rom/mod_gm_module_tbl.farc");

        var result = ModClassifier.Classify(mod.Root);

        Assert.True(result.IsUnknown);
    }

    [Fact]
    public void DoesNotMatchDatabaseInsideRomBackupDirectory()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.Write("rom/backup/nc_db.toml", NewClassicsDatabase("CONSOLE", 9999));

        var result = ModClassifier.Classify(mod.Root);

        Assert.DoesNotContain("New Classics", result.FormatVariant);
        Assert.NotEqual("Additional Difficulties", result.PrimaryCategory);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("Missing.dll")]
    public void InvalidDllEntriesAreNotPlugins(string configuredDll)
    {
        using var mod = new TemporaryMod();
        mod.Write("config.toml", $"enabled = true\ninclude = [\".\"]\ndll = [\"{configuredDll}\"]\n");

        var result = ModClassifier.Classify(mod.Root);

        Assert.DoesNotContain("Plugin", result.DetectedCategories);
    }

    [Fact]
    public void ConfiguredExistingDllIsAPluginButAnUnlistedDependencyIsNot()
    {
        using var mod = new TemporaryMod();
        mod.Write("config.toml", "enabled = true\ninclude = [\".\"]\ndll = [\"Main.dll\"]\n");
        mod.Write("Main.dll");
        mod.Write("Dependency.dll");

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal("Plugin", result.PrimaryCategory);
        Assert.Contains("configured plugin DLL: Main.dll", result.Evidence);
    }

    [Fact]
    public void DetectsCustomSongAndModuleAsSeparateLabels()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.Write("rom/mod_gm_module_tbl.farc");
        mod.Write("rom/mod_chritm_prop.farc");
        mod.Write("rom/mod_pv_db.txt", "pv_800.name=Example");
        mod.WriteBinary("rom/script/pv_800_extreme.dsc", 0x14050921);
        mod.Write("rom/sound/song/pv_800.ogg");

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal("Custom Song", result.PrimaryCategory);
        Assert.Contains("Module", result.DetectedCategories);
    }

    [Theory]
    [InlineData("rom/2d/spr_gam_cmn.farc", "User Interface")]
    [InlineData("rom/sound/bgm/result_ft_clear.ogg", "Sound Replacement")]
    [InlineData("rom/sound/song/pv_999.ogg", "Cover")]
    public void DetectsOtherStrongStructuralCategories(string relativePath, string category)
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.Write(relativePath);

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal(category, result.PrimaryCategory);
    }

    [Fact]
    public void StagePatchWithChartsIsNotAutomaticallyAdditionalDifficulties()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.WriteBinary("rom/script/pv_038_extreme.dsc", 0x14050921);
        mod.Write("rom/light_param/light_pv038s01.txt");

        var result = ModClassifier.Classify(mod.Root);

        Assert.DoesNotContain("Additional Difficulties", result.DetectedCategories);
        Assert.Equal("Other/Misc", result.PrimaryCategory);
    }

    [Fact]
    public void ChartPackDoesNotDependOnAnArbitraryAuxiliaryFileLimit()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.WriteBinary("rom/script/pv_144_extreme.dsc", 0x12020220);
        for (var index = 0; index < 20; index++)
            mod.Write($"rom/2d/spr_sel_pv144_{index:00}.farc");

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal("Additional Difficulties", result.PrimaryCategory);
    }

    [Fact]
    public void RegionalAssetWrappersAloneAreNotTranslations()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.Write("rom_steam_en/rom/2d/aet_db.bin");
        mod.Write("rom_steam_fr/rom/2d/aet_db.bin");

        var result = ModClassifier.Classify(mod.Root);

        Assert.DoesNotContain("Translation", result.DetectedCategories);
        Assert.Equal("Other/Misc", result.PrimaryCategory);
    }

    [Fact]
    public void EmptyTemplateIsUnknown()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.Write("rom/.gitkeep");

        var result = ModClassifier.Classify(mod.Root);

        Assert.True(result.IsUnknown);
        Assert.Equal(ModClassificationConfidence.None, result.Confidence);
    }

    [Fact]
    public void NestedPlaceholderIsUnknown()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.Write("rom/empty/.gitkeep");

        Assert.True(ModClassifier.Classify(mod.Root).IsUnknown);
    }

    [Fact]
    public void NewClassicsChartSignatureCanIdentifyFormatWithoutDatabase()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.WriteBinary("rom/script/pv_9000_extreme.dsc", 0x25061313);

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal("Additional Difficulties", result.PrimaryCategory);
        Assert.Equal("New Classics", result.FormatVariant);
    }

    [Fact]
    public void MatchingIsCaseInsensitiveAndMalformedTomlIsReadOnly()
    {
        using var mod = new TemporaryMod();
        const string invalidConfig = "enabled = true\ndll = [\n";
        mod.Write("config.toml", invalidConfig);
        mod.Write("ROM/NC_DB.TOML", NewClassicsDatabase("CONSOLE", 9001));
        mod.WriteBinary("ROM/SCRIPT_NC/PV_9001_EXTREME.DSC", 0x12020220);

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal("Additional Difficulties", result.PrimaryCategory);
        Assert.Contains("New Classics (Console)", result.FormatVariant);
        Assert.Contains(result.Evidence, item => item.StartsWith("config.toml parse warning:", StringComparison.Ordinal));
        Assert.Equal(invalidConfig, File.ReadAllText(Path.Combine(mod.Root, "config.toml")));
    }

    [Fact]
    public void NonObjectMetaJsonAndInvalidNewClassicsSongsDoNotPolluteResults()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        mod.Write("meta.json", "[]");
        mod.Write("rom/nc_db.toml", "style = \"MIXED\"\n[[songs]]\nid = true\n");

        var result = ModClassifier.Classify(mod.Root);

        Assert.Equal("Additional Difficulties", result.PrimaryCategory);
        Assert.Equal("New Classics", result.FormatVariant);
        Assert.DoesNotContain("Mixed", result.FormatVariant);
        Assert.Contains("nc_db.toml has no valid positive song IDs", result.Evidence);
    }

    [Fact]
    public void HonorsCancellationBeforeScanning()
    {
        using var mod = new TemporaryMod();
        mod.WriteConfig();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ModClassifier.Classify(mod.Root, cancellationSource.Token));
    }

    private static string NewClassicsDatabase(string style, int id)
    {
        return $"[[songs]]\nid = {id}\n\n" +
            $"[[songs.extreme]]\nstyle = \"{style}\"\n" +
            $"script_file_name = \"rom/script_nc/pv_{id}_extreme.dsc\"\n" +
            "level = \"PV_LV_08_0\"\n";
    }

    private sealed class TemporaryMod : IDisposable
    {
        public TemporaryMod(string? name = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "DivaModManager.Tests", name ?? Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void WriteConfig(params string[] includeRoots)
        {
            if (includeRoots.Length == 0)
                includeRoots = new[] { "." };
            var values = String.Join(", ", includeRoots.Select(value => $"\"{value}\""));
            Write("config.toml", $"enabled = true\ninclude = [{values}]\n");
        }

        public void Write(string relativePath, string contents = "")
        {
            var path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void WriteBinary(string relativePath, uint signature)
        {
            var bytes = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, signature);
            var path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }

        private string Resolve(string relativePath)
        {
            return Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
