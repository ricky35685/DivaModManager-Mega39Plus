using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Sprites;
using MikuMikuLibrary.Textures;
using System.Text.RegularExpressions;
using Xunit;

namespace DivaModManager.Tests;

public sealed class SongCatalogServiceTests
{
    [Fact]
    public void ParsesLegacySongAndResolvesMega39PlusAssets()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Folder Name", "enabled = true\nname = \"Configured Name\"\ninclude = [\".\"]\n");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_0808.song_name=Primary title",
            "pv_0808.song_name_en=English=title",
            "pv_0808.song_name_reading=reading",
            "pv_0808.songinfo.music=Composer",
            "pv_0808.songinfo.lyrics=Writer",
            "pv_0808.song_file_name=rom/sound/song/pv_0808.ogg",
            "pv_0808.movie_file_name=rom/movie/pv_0808.usm",
            "pv_0808.difficulty.extreme.0.level=PV_LV_09_0",
            "pv_0808.difficulty.extreme.0.script_file_name=rom/script/pv_0808_extreme.dsc"
        }));
        mod.Write("rom/sound/song/pv_0808.ogg");
        mod.Write("rom/movie/pv_0808.usm");
        mod.Write("rom/script/pv_0808_extreme.dsc");
        mod.Write("rom/2d/spr_sel_pv0808.farc");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.Equal(808, song.PvId);
        Assert.Equal("0808", song.RawPvId);
        Assert.Equal("Configured Name", song.ModName);
        Assert.Equal("Primary title", song.SongName);
        Assert.Equal("English=title", song.SongNameEnglish);
        Assert.Equal("reading", song.SongNameReading);
        Assert.Equal("Composer, Writer", song.AuthorSummary);
        Assert.Equal(SongFormat.Legacy, song.Format);
        Assert.Equal("完整", song.AssetStatus);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
        Assert.False(song.IsSongPatch);
        Assert.True(song.AudioExists);
        Assert.True(song.VideoExists);
        Assert.True(song.CoverExists);
        var difficulty = Assert.Single(song.Difficulties);
        Assert.True(difficulty.ScriptExists);
        Assert.Equal("extreme", difficulty.NormalizedName);
        Assert.Equal(9m, difficulty.NumericLevel);
        Assert.Equal("9", difficulty.LevelDisplay);
        Assert.Equal(64, song.DatabaseHash.Length);
        Assert.Matches(new Regex("^[0-9A-F]{64}$"), song.DatabaseHash);
        Assert.NotEqual(default, song.DatabaseLastWriteTimeUtc);
        Assert.Contains(song.AudioPath, song.ReferencedAssetPaths);
        Assert.Contains("English=title", song.SearchText);
    }

    [Theory]
    [InlineData("mp4", "usm")]
    [InlineData("usm", "mp4")]
    public void ResolvesConvertedVideoContainerWithTheSameStem(
        string declaredExtension,
        string installedExtension)
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Converted Video");
        var declaredReference = $"rom/movie/pv_8629.{declaredExtension}";
        var installedReference = $"rom/movie/pv_8629.{installedExtension}";
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8629.song_name=Converted song",
            "pv_8629.song_file_name=rom/sound/song/pv_8629.ogg",
            $"pv_8629.movie_file_name={declaredReference}"
        }));
        mod.Write("rom/sound/song/pv_8629.ogg");
        mod.Write(installedReference);
        mod.Write("rom/2d/spr_sel_pv8629.farc");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        var installedPath = Path.GetFullPath(Path.Combine(
            mod.Root,
            installedReference.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(declaredReference, song.VideoReference);
        Assert.Equal(installedPath, song.VideoPath);
        Assert.True(song.VideoExists);
        Assert.Contains(installedPath, song.ReferencedAssetPaths);
        Assert.DoesNotContain("找不到视频文件", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectsExistingExplicitRomFileReferencesForDeletionAndSharing()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Related Assets");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_742.song_name=Related asset song",
            "pv_742.song_file_name=rom/sound/song/pv_742.ogg",
            "pv_742.movie_file_name=rom/movie/pv_742.usm",
            "pv_742.movie_list.0.name=rom/movie/pv_742_alt.mp4",
            "pv_742.difficulty.extreme.0.script_file_name=rom/script/pv_742_extreme.dsc",
            "pv_742.difficulty.extreme.0.movie_file_name=rom/movie/pv_742_extreme.usm",
            "pv_742.pv_expression.file_name=rom/pv_expression/exp_PV742.bin",
            "pv_742.effect_se_file_name=rom/sound/pv742_effect.farc",
            "pv_742.stage_param.0.wind_file=rom/light_param/wind_pv742.txt",
            "pv_742.future_asset=rom/custom/future_asset.bin",
            "pv_742.missing_asset=rom/custom/missing.bin",
            "pv_742.outside_asset=rom/../../outside.bin"
        }));
        var existingReferences = new[]
        {
            "rom/sound/song/pv_742.ogg",
            "rom/movie/pv_742.usm",
            "rom/movie/pv_742_alt.usm",
            "rom/script/pv_742_extreme.dsc",
            "rom/movie/pv_742_extreme.usm",
            "rom/pv_expression/exp_PV742.bin",
            "rom/sound/pv742_effect.farc",
            "rom/light_param/wind_pv742.txt",
            "rom/custom/future_asset.bin"
        };
        foreach (var reference in existingReferences)
            mod.Write(reference);

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        var expectedPaths = existingReferences
            .Select(reference => Path.GetFullPath(Path.Combine(
                mod.Root,
                reference.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();
        Assert.All(expectedPaths, path => Assert.Contains(path, song.ExplicitAssetPaths));
        Assert.All(expectedPaths, path => Assert.Contains(path, song.ReferencedAssetPaths));
        Assert.DoesNotContain(
            song.ExplicitAssetPaths,
            path => path.EndsWith("missing.bin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("越过内容目录", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsesNewClassicsMetadataAndTomlSemantically()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("NC Song");
        mod.Write("rom/mod_nc_pv_db.txt", String.Join("\n", new[]
        {
            "pv_4726.song_name=NC title",
            "pv_4726.song_file_name=rom/sound/song/pv_4726.ogg"
        }));
        mod.Write("rom/nc_db.toml", NewClassicsSong(4726, "MIXED", "rom/script/pv_4726_extreme.dsc"));
        mod.Write("rom/sound/song/pv_4726.ogg");
        mod.Write("rom/script/pv_4726_extreme.dsc");
        mod.Write("rom/2d/spr_sel_pv4726.farc");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.Equal(SongFormat.NewClassics, song.Format);
        Assert.EndsWith("nc_db.toml", song.NewClassicsDatabasePath, StringComparison.OrdinalIgnoreCase);
        var chart = Assert.Single(song.Difficulties);
        Assert.Equal(SongDifficultySource.NewClassicsDatabase, chart.Source);
        Assert.Equal("MIXED", chart.Style);
        Assert.True(chart.ScriptExists);
    }

    [Fact]
    public void ParsesNestedNewClassicsExtraDifficultyArrays()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("NC Extra");
        mod.Write(
            "rom/mod_nc_pv_db.txt",
            "pv_9800.song_name=Extra chart song\n" +
            "pv_9800.song_file_name=rom/sound/song/pv_9800.ogg\n");
        mod.Write(
            "rom/nc_db.toml",
            "[[songs]]\n" +
            "id = 9800\n\n" +
            "[[songs.extra.extreme]]\n" +
            "style = \"ARCADE\"\n" +
            "level = \"PV_LV_09_5\"\n" +
            "script_file_name = \"rom/script_nc/pv_9800_ex_extreme.dsc\"\n");
        mod.Write("rom/sound/song/pv_9800.ogg");
        mod.Write("rom/script_nc/pv_9800_ex_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));
        var chart = Assert.Single(song.Difficulties);

        Assert.Equal("ex_extreme", chart.Name);
        Assert.Equal("ARCADE", chart.Style);
        Assert.Equal("PV_LV_09_5", chart.Level);
        Assert.True(chart.ScriptExists);
        Assert.Equal(64, song.NewClassicsDatabaseHash.Length);
        Assert.NotEqual(default, song.NewClassicsDatabaseLastWriteTimeUtc);
    }

    [Fact]
    public void IgnoresEmptyNewClassicsArrayPlaceholders()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("NC placeholders");
        mod.Write(
            "rom/mod_pv_db.txt",
            "pv_9801.song_name=Placeholder song\n" +
            "pv_9801.song_file_name=rom/sound/song/pv_9801.ogg\n" +
            "pv_9801.difficulty.extreme.0.script_file_name=rom/script/pv_9801_extreme.dsc\n");
        mod.Write(
            "rom/nc_db.toml",
            "[[songs]]\n" +
            "id = 9801\n\n" +
            "[[songs.hard]]\n" +
            "level = \"PV_LV_07_0\"\n" +
            "script_file_name = \"rom/script_nc/pv_9801_hard.dsc\"\n\n" +
            "[[songs.hard]]\n");
        mod.Write("rom/sound/song/pv_9801.ogg");
        mod.Write("rom/script/pv_9801_extreme.dsc");
        mod.Write("rom/script_nc/pv_9801_hard.dsc");
        mod.Write("rom/2d/spr_sel_pv9801.farc");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.Equal(2, song.Difficulties.Count);
        var chart = Assert.Single(
            song.Difficulties,
            difficulty => difficulty.Source == SongDifficultySource.NewClassicsDatabase);
        Assert.Equal("hard", chart.NormalizedName);
        Assert.True(chart.ScriptExists);
        Assert.Single(song.Difficulties, difficulty => difficulty.NormalizedName == "hard");
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
    }

    [Fact]
    public void RequiresStyleOnlyDifficultyToInheritAMatchingChart()
    {
        using var mods = new TemporaryMods();
        var broken = mods.CreateMod("Unresolved style chart");
        broken.Write(
            "rom/mod_pv_db.txt",
            "pv_9802.song_name=Unresolved style\n" +
            "pv_9802.song_file_name=rom/sound/song/pv_9802.ogg\n" +
            "pv_9802.difficulty.extreme.0.script_file_name=rom/script/pv_9802_extreme.dsc\n");
        broken.Write(
            "rom/nc_db.toml",
            "[[songs]]\n" +
            "id = 9802\n" +
            "[[songs.easy]]\n" +
            "style = \"ARCADE\"\n" +
            "level = \"PV_LV_03_0\"\n");
        broken.Write("rom/sound/song/pv_9802.ogg");
        broken.Write("rom/script/pv_9802_extreme.dsc");
        broken.Write("rom/2d/spr_sel_pv9802.farc");

        var inherited = mods.CreateMod("Resolved style chart");
        inherited.Write(
            "rom/mod_pv_db.txt",
            "pv_9803.song_name=Resolved style\n" +
            "pv_9803.song_file_name=rom/sound/song/pv_9803.ogg\n" +
            "pv_9803.difficulty.hard.0.script_file_name=rom/script/pv_9803_hard.dsc\n");
        inherited.Write(
            "rom/nc_db.toml",
            "[[songs]]\n" +
            "id = 9803\n" +
            "[[songs.hard]]\n" +
            "style = \"ARCADE\"\n");
        inherited.Write("rom/sound/song/pv_9803.ogg");
        inherited.Write("rom/script/pv_9803_hard.dsc");
        inherited.Write("rom/2d/spr_sel_pv9803.farc");

        var wrongIndex = mods.CreateMod("Wrong chart index");
        wrongIndex.Write(
            "rom/mod_pv_db.txt",
            "pv_9804.song_name=Wrong index\n" +
            "pv_9804.song_file_name=rom/sound/song/pv_9804.ogg\n" +
            "pv_9804.difficulty.extreme.0.script_file_name=rom/script/pv_9804_extreme.dsc\n" +
            "pv_9804.difficulty.extreme.1.level=PV_LV_10_0\n");
        wrongIndex.Write("rom/sound/song/pv_9804.ogg");
        wrongIndex.Write("rom/script/pv_9804_extreme.dsc");
        wrongIndex.Write("rom/2d/spr_sel_pv9804.farc");

        var songs = SongCatalogService.ScanMods(mods.Root);
        var unresolved = songs.Single(song => song.PvId == 9802);
        var resolved = songs.Single(song => song.PvId == 9803);
        var mismatched = songs.Single(song => song.PvId == 9804);

        Assert.Equal(SongRunStatus.Warning, unresolved.RunStatus);
        Assert.Contains("可继承的谱面", unresolved.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SongRunStatus.Ready, resolved.RunStatus);
        Assert.Equal(SongRunStatus.Warning, mismatched.RunStatus);
        Assert.Contains("可继承的谱面", mismatched.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MergesNewClassicsChartsIntoLegacyMetadataByNumericId()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Mixed");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_06029.song_name=Hybrid title",
            "pv_06029.song_file_name=rom/sound/song/pv_06029.ogg",
            "pv_06029.difficulty.hard.0.script_file_name=rom/script/pv_06029_hard.dsc"
        }));
        mod.Write("rom/nc_db.toml", NewClassicsSong(6029, "CONSOLE", "rom/script_nc/pv_6029_extreme.dsc"));
        mod.Write("rom/sound/song/pv_06029.ogg");
        mod.Write("rom/script/pv_06029_hard.dsc");
        mod.Write("rom/script_nc/pv_6029_extreme.dsc");
        mod.Write("rom/2d/spr_sel_pv06029.farc");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.Equal(SongFormat.LegacyWithNewClassics, song.Format);
        Assert.Equal("06029", song.RawPvId);
        Assert.Equal(2, song.Difficulties.Count);
        Assert.Contains(song.Difficulties, difficulty => difficulty.Source == SongDifficultySource.LegacyDatabase);
        Assert.Contains(song.Difficulties, difficulty => difficulty.Source == SongDifficultySource.NewClassicsDatabase);
    }

    [Fact]
    public void ExposesOrphanNewClassicsSongAsAdditionalDifficulty()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Difficulty Pack");
        mod.Write("rom/nc_db.toml", NewClassicsSong(9887, "CONSOLE", "rom/script_nc/pv_9887_extreme.dsc"));
        mod.Write("rom/script_nc/pv_9887_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.Equal(SongFormat.AdditionalDifficulty, song.Format);
        Assert.Equal("PV 9887", song.SongName);
        Assert.Empty(song.DatabasePath);
        Assert.EndsWith("nc_db.toml", song.NewClassicsDatabasePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, song.DatabaseHash.Length);
        Assert.True(Assert.Single(song.Difficulties).ScriptExists);
        Assert.Equal("完整", song.AssetStatus);
        Assert.DoesNotContain("缺少音频引用", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.Contains("目标歌曲", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HonorsWrapperIncludesAndIgnoresInactiveDatabaseVariants()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Wrapper", "enabled = true\ninclude = [\"./AP/./\"]\n");
        mod.Write("AP/rom/mod_pv_db.txt", "pv_144.song_name=Loaded\npv_144.song_file_name=rom/sound/song/pv_144.ogg\n");
        mod.Write("rom/mod_pv_db.txt", "pv_9001.song_name=Root inactive\n");
        mod.Write("Archive/rom/mod_pv_db.txt", "pv_9002.song_name=Archive inactive\n");
        mod.Write("no3D/rom/mod_pv_db.txt", "pv_9003.song_name=no3D inactive\n");
        mod.Write("AP/rom/backup/mod_pv_db.txt", "pv_9004.song_name=Nested backup\n");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.Equal(144, song.PvId);
        Assert.Equal(Path.GetFullPath(Path.Combine(mod.Root, "AP")), song.ContentRoot);
    }

    [Fact]
    public void MalformedNewClassicsTomlDoesNotHideLegacySongsOrMutateFiles()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Broken NC");
        mod.Write("rom/mod_pv_db.txt", "pv_75.song_name=Still visible\n");
        const string invalidToml = "[[songs]\nid = 75\n";
        mod.Write("rom/nc_db.toml", invalidToml);

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.Equal(SongFormat.Legacy, song.Format);
        Assert.Contains("nc_db.toml 解析警告", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(invalidToml, File.ReadAllText(Path.Combine(mod.Root, "rom", "nc_db.toml")));
    }

    [Fact]
    public void MergesNumericPvAliasesAndCollectsEveryAliasAsset()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Mixed raw aliases");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_77.song_name=One numeric song",
            "pv_77.song_file_name=rom/sound/song/pv_77.ogg",
            "pv_077.difficulty.easy.0.script_file_name=rom/script/pv_077_easy.dsc",
            "pv_077.difficulty.extreme.0.script_file_name=rom/script/pv_077_extreme.dsc"
        }));
        mod.Write("rom/sound/song/pv_77.ogg");
        mod.Write("rom/script/pv_077_easy.dsc");
        mod.Write("rom/script/pv_077_extreme.dsc");
        var primaryArtwork = mod.Write("rom/2d/spr_sel_pv77.farc");
        var aliasArtwork = mod.Write("rom/2d/spr_sel_pv077.farc");
        var primaryAdditionalParameter = mod.Write("rom/add_param/pv_77.adp");
        var aliasAdditionalParameter = mod.Write("rom/add_param/pv_077.adp");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.Equal(77, song.PvId);
        Assert.Equal("77", song.RawPvId);
        Assert.Equal(new[] { "77", "077" }, song.RawPvIds);
        Assert.Equal(2, song.Difficulties.Count);
        Assert.Contains(song.Difficulties, difficulty => difficulty.Name == "easy");
        Assert.Contains(song.Difficulties, difficulty => difficulty.Name == "extreme");
        Assert.Equal(primaryArtwork, song.ArtworkPath);
        Assert.Equal(primaryAdditionalParameter, song.AdditionalParameterPath);
        Assert.Contains(primaryArtwork, song.ReferencedAssetPaths);
        Assert.Contains(aliasArtwork, song.ReferencedAssetPaths);
        Assert.Contains(primaryAdditionalParameter, song.ReferencedAssetPaths);
        Assert.Contains(aliasAdditionalParameter, song.ReferencedAssetPaths);
        Assert.Contains("多个原始别名", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("077", song.SearchText);
    }

    [Fact]
    public void RetainsSameNameAcrossDifferentPvIdsAndModsWithoutConflict()
    {
        using var mods = new TemporaryMods();
        var first = mods.CreateMod("First author");
        var second = mods.CreateMod("Second author");
        first.Write("rom/mod_pv_db.txt", "pv_8101.song_name=Same title\n");
        second.Write("rom/mod_pv_db.txt", "pv_8102.song_name=Same title\n");

        var songs = SongCatalogService.ScanMods(mods.Root);

        Assert.Equal(2, songs.Count);
        Assert.Equal(new[] { 8101, 8102 }, songs.Select(song => song.PvId));
        Assert.All(songs, song => Assert.Equal("Same title", song.SongName));
        Assert.All(songs, song => Assert.False(song.HasIdConflict));
    }

    [Fact]
    public void AdditionalDifficultyTargetsFullSongWithoutCreatingIdConflict()
    {
        using var mods = new TemporaryMods();
        var fullSong = mods.CreateMod("Full song");
        var difficultyPack = mods.CreateMod("Difficulty pack");
        fullSong.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8201.song_name=Target song",
            "pv_8201.song_file_name=rom/sound/song/pv_8201.ogg",
            "pv_8201.difficulty.extreme.0.script_file_name=rom/script/pv_8201_extreme.dsc"
        }));
        fullSong.Write("rom/sound/song/pv_8201.ogg");
        fullSong.Write("rom/script/pv_8201_extreme.dsc");
        fullSong.Write("rom/2d/spr_sel_pv8201.farc");
        difficultyPack.Write(
            "rom/nc_db.toml",
            NewClassicsSong(8201, "CONSOLE", "rom/script_nc/pv_8201_extreme.dsc"));
        difficultyPack.Write("rom/script_nc/pv_8201_extreme.dsc");

        var songs = SongCatalogService.ScanMods(mods.Root);

        Assert.Equal(2, songs.Count);
        Assert.Contains(songs, song => song.Format == SongFormat.Legacy);
        Assert.Contains(songs, song => song.Format == SongFormat.AdditionalDifficulty);
        Assert.All(songs, song => Assert.False(song.HasIdConflict));
        Assert.All(songs, song => Assert.DoesNotContain("多个模组中定义了完整歌曲", song.WarningsDisplay));
        Assert.All(songs, song => Assert.Equal(SongRunStatus.Ready, song.RunStatus));
    }

    [Fact]
    public void AdditionalDifficultyCannotUseDisabledTargetSong()
    {
        using var mods = new TemporaryMods();
        var disabledTarget = mods.CreateMod(
            "Disabled target",
            "enabled = false\nname = \"Disabled target\"\ninclude = [\".\"]\n");
        var difficultyPack = mods.CreateMod("Enabled difficulty");
        disabledTarget.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8202.song_name=Disabled target",
            "pv_8202.song_file_name=rom/sound/song/pv_8202.ogg",
            "pv_8202.difficulty.extreme.0.script_file_name=rom/script/pv_8202_extreme.dsc"
        }));
        disabledTarget.Write("rom/sound/song/pv_8202.ogg");
        disabledTarget.Write("rom/script/pv_8202_extreme.dsc");
        difficultyPack.Write(
            "rom/nc_db.toml",
            NewClassicsSong(8202, "CONSOLE", "rom/script_nc/pv_8202_extreme.dsc"));
        difficultyPack.Write("rom/script_nc/pv_8202_extreme.dsc");

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);
        var additional = songs.Single(song => song.Format == SongFormat.AdditionalDifficulty);

        Assert.Equal(SongRunStatus.Broken, additional.RunStatus);
        Assert.Contains("目标歌曲", additional.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditingScanReportsCompleteOnlyWhenEveryDatabaseWasReadAndParsed()
    {
        using var mods = new TemporaryMods();
        var valid = mods.CreateMod("Valid scan");
        valid.Write("rom/mod_pv_db.txt", "pv_8301.song_name=Valid\n");
        var validScan = SongCatalogService.ScanModForEditing(valid.Root);
        Assert.True(validScan.IsComplete);
        Assert.Single(validScan.Entries);

        var malformedNc = mods.CreateMod("Malformed NC");
        malformedNc.Write("rom/mod_pv_db.txt", "pv_8302.song_name=Still visible\n");
        malformedNc.Write("rom/nc_db.toml", "[[songs]\nid = 8302\n");
        var malformedNcScan = SongCatalogService.ScanModForEditing(malformedNc.Root);
        Assert.False(malformedNcScan.IsComplete);
        Assert.Single(malformedNcScan.Entries);

        var missingInclude = mods.CreateMod("Missing include", "enabled = true\ninclude = [\"missing\"]\n");
        var missingIncludeScan = SongCatalogService.ScanModForEditing(missingInclude.Root);
        Assert.False(missingIncludeScan.IsComplete);
        Assert.Empty(missingIncludeScan.Entries);
    }

    [Fact]
    public void EditingScanReportsIncompleteWhenFlatDatabaseCannotBeRead()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Locked database");
        var databasePath = mod.Write("rom/mod_pv_db.txt", "pv_8401.song_name=Locked\n");
        using var locked = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var scan = SongCatalogService.ScanModForEditing(mod.Root);

        Assert.False(scan.IsComplete);
        Assert.Empty(scan.Entries);
    }

    [Fact]
    public void RetainsCrossModDuplicateIdsAndMarksEveryConflict()
    {
        using var mods = new TemporaryMods();
        var first = mods.CreateMod("First");
        var second = mods.CreateMod("Second");
        first.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_100.song_name=First title",
            "pv_100.song_file_name=rom/sound/song/pv_100.ogg",
            "pv_100.difficulty.extreme.0.script_file_name=rom/script/pv_100_extreme.dsc"
        }));
        first.Write("rom/sound/song/pv_100.ogg");
        first.Write("rom/script/pv_100_extreme.dsc");
        first.Write("rom/2d/spr_sel_pv100.farc");
        second.Write("rom/mod_nc_pv_db.txt", String.Join("\n", new[]
        {
            "pv_0100.song_name=Second title",
            "pv_0100.song_file_name=rom/sound/song/pv_0100.ogg",
            "pv_0100.difficulty.extreme.0.script_file_name=rom/script/pv_0100_extreme.dsc"
        }));
        second.Write("rom/sound/song/pv_0100.ogg");
        second.Write("rom/script/pv_0100_extreme.dsc");
        second.Write("rom/2d/spr_sel_pv0100.farc");

        var songs = SongCatalogService.ScanMods(mods.Root);

        Assert.Equal(2, songs.Count);
        Assert.All(songs, song => Assert.True(song.HasIdConflict));
        Assert.All(songs, song => Assert.Equal(SongRunStatus.Broken, song.RunStatus));
        Assert.All(songs, song => Assert.Contains("多个模组中定义了完整歌曲", song.RunStatusReasonsDisplay));
        Assert.All(songs, song => Assert.Equal(2, song.IdConflictSources.Count));
        Assert.All(songs, song => Assert.All(song.IdConflictSources, source =>
        {
            Assert.True(File.Exists(source.SourcePath));
            Assert.True(Directory.Exists(source.ContentRoot));
            Assert.True(Directory.Exists(source.ModRoot));
        }));
        Assert.All(songs, song => Assert.Contains(song.DatabasePath, song.IdConflictSources.Select(source => source.SourcePath)));
        Assert.All(songs, song => Assert.Contains("PVID 100", song.WarningsDisplay));
        Assert.Contains(songs, song => song.RawPvId == "100");
        Assert.Contains(songs, song => song.RawPvId == "0100");
    }

    [Fact]
    public void SameSourceSameSongWithDistinctDifficultiesIsReadyAndKeepsSourcePaths()
    {
        using var mods = new TemporaryMods();
        var songs = CreateSameSourceDefinitions(
            mods, 8920,
            "hard", 0, "PV_LV_06_0", false,
            "extreme", 0, "PV_LV_08_0", false);

        Assert.Equal(2, songs.Select(song => song.ModRoot).Distinct().Count());
        Assert.All(songs, song => Assert.Equal(SongRunStatus.Ready, song.RunStatus));
        Assert.All(songs, song => Assert.False(song.HasIdConflict));
        Assert.All(songs, song => Assert.Equal(2, song.IdConflictSources.Count));
        Assert.All(songs, song => Assert.All(song.IdConflictSources, source =>
            Assert.True(File.Exists(source.SourcePath))));
    }

    [Fact]
    public void SameSourceSameDifficultyWithDifferentStarsIsWarningEvenAcrossIndexes()
    {
        using var mods = new TemporaryMods();
        var songs = CreateSameSourceDefinitions(
            mods, 8921,
            "extreme", 0, "PV_LV_08_0", false,
            "extreme", 1, "PV_LV_09_0", false);

        Assert.All(songs, song => Assert.Equal(SongRunStatus.Warning, song.RunStatus));
        Assert.All(songs, song => Assert.False(song.HasIdConflict));
        Assert.All(songs, song => Assert.Equal(2, song.IdConflictSources.Count));
        Assert.All(songs, song => Assert.Contains(
            "索引 0 与 1",
            song.RunStatusReasonsDisplay,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SameSourceSameDifficultyAndStarIsBrokenEvenAcrossIndexes()
    {
        using var mods = new TemporaryMods();
        var songs = CreateSameSourceDefinitions(
            mods, 8922,
            "extreme", 0, "PV_LV_09_0", false,
            "extreme", 1, "PV_LV_09_0", false);

        Assert.All(songs, song => Assert.Equal(SongRunStatus.Broken, song.RunStatus));
        Assert.All(songs, song => Assert.True(song.HasIdConflict));
        Assert.All(songs, song => Assert.Contains("重复星级 9", song.RunStatusReasonsDisplay));
    }

    [Fact]
    public void ExtraDifficultyDoesNotOverlapItsBaseDifficulty()
    {
        using var mods = new TemporaryMods();
        var songs = CreateSameSourceDefinitions(
            mods, 8923,
            "extreme", 0, "PV_LV_09_0", false,
            "extreme", 0, "PV_LV_09_0", true);

        Assert.All(songs, song => Assert.Equal(SongRunStatus.Ready, song.RunStatus));
        Assert.All(songs, song => Assert.False(song.HasIdConflict));
    }

    [Fact]
    public void UnknownStarOnOverlappingSameSourceDifficultyIsConservativelyBroken()
    {
        using var mods = new TemporaryMods();
        var songs = CreateSameSourceDefinitions(
            mods, 8924,
            "extreme", 0, null, false,
            "extreme", 1, "PV_LV_09_0", false);

        Assert.All(songs, song => Assert.Equal(SongRunStatus.Broken, song.RunStatus));
        Assert.All(songs, song => Assert.True(song.HasIdConflict));
        Assert.All(songs, song => Assert.Contains(
            "星级未知",
            song.RunStatusReasonsDisplay,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DifferentModNamesStillConflictWhenTheirDifficultiesDoNotOverlap()
    {
        using var mods = new TemporaryMods();
        var first = mods.CreateMod("Different source A");
        var second = mods.CreateMod("Different source B");
        WriteSongDefinition(first, 8925, "Shared title", "hard", 0, "PV_LV_06_0", false, "a");
        WriteSongDefinition(second, 8925, "Shared title", "extreme", 0, "PV_LV_09_0", false, "b");

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);

        Assert.All(songs, song => Assert.Equal(SongRunStatus.Broken, song.RunStatus));
        Assert.All(songs, song => Assert.True(song.HasIdConflict));
        Assert.All(songs, song => Assert.Contains(
            "多个模组中定义了完整歌曲",
            song.RunStatusReasonsDisplay,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SameModNameWithDifferentDeclaredAuthorsStillUsesNormalConflictRules()
    {
        using var mods = new TemporaryMods();
        var first = mods.CreateMod(
            "Author A fragment",
            "enabled = true\nname = \"Shared source\"\nauthor = \"Author A\"\ninclude = [\".\"]\n");
        var second = mods.CreateMod(
            "Author B fragment",
            "enabled = true\nname = \"Shared source\"\nauthor = \"Author B\"\ninclude = [\".\"]\n");
        WriteSongDefinition(first, 8926, "Shared title", "hard", 0, "PV_LV_06_0", false, "a");
        WriteSongDefinition(second, 8926, "Shared title", "extreme", 0, "PV_LV_09_0", false, "b");

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);

        Assert.All(songs, song => Assert.True(song.HasIdConflict));
        Assert.All(songs, song => Assert.Equal(SongRunStatus.Broken, song.RunStatus));
    }

    [Fact]
    public void MissingSameSourceChartDoesNotCreateADifficultyOverlapConflict()
    {
        using var mods = new TemporaryMods();
        const string config =
            "enabled = true\nname = \"Shared source\"\nauthor = \"Shared author\"\ninclude = [\".\"]\n";
        var complete = mods.CreateMod("Complete fragment", config);
        var missing = mods.CreateMod("Missing fragment", config);
        WriteSongDefinition(complete, 8927, "Shared title", "extreme", 0, "PV_LV_09_0", false, "a");
        WriteSongDefinition(missing, 8927, "Shared title", "extreme", 1, "PV_LV_09_0", false, "b");
        File.Delete(Path.Combine(missing.Root, "rom", "script", "pv_8927_extreme_b.dsc"));

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);
        var completeSong = songs.Single(song => song.ModRoot == complete.Root);
        var missingSong = songs.Single(song => song.ModRoot == missing.Root);

        Assert.Equal(SongRunStatus.Ready, completeSong.RunStatus);
        Assert.False(completeSong.HasIdConflict);
        Assert.Equal(SongRunStatus.Broken, missingSong.RunStatus);
        Assert.False(missingSong.HasIdConflict);
        Assert.Contains("谱面中有 1 个找不到", missingSong.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncompleteDatabaseFragmentsAreBrokenInsteadOfBeingMergedAsPatches()
    {
        using var mods = new TemporaryMods();
        var fullSong = mods.CreateMod("Full song");
        var metadataPatch = mods.CreateMod("Metadata patch");
        var audioPatch = mods.CreateMod("Audio patch");
        fullSong.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8601.song_name=Original title",
            "pv_8601.song_file_name=rom/sound/song/pv_8601.ogg",
            "pv_8601.difficulty.extreme.0.script_file_name=rom/script/pv_8601_extreme.dsc"
        }));
        fullSong.Write("rom/sound/song/pv_8601.ogg");
        fullSong.Write("rom/script/pv_8601_extreme.dsc");
        fullSong.Write("rom/2d/spr_sel_pv8601.farc");
        metadataPatch.Write("rom/mod_pv_db.txt", "pv_8601.song_name_en=Patched title\n");
        audioPatch.Write("rom/mod_pv_db.txt", "pv_8601.song_file_name=rom/sound/song/pv_8601_patch.ogg\n");
        audioPatch.Write("rom/sound/song/pv_8601_patch.ogg");

        var songs = SongCatalogService.ScanMods(mods.Root);

        Assert.Equal(3, songs.Count);
        var full = songs.Single(song => song.ModRoot == fullSong.Root);
        Assert.Equal("Full song", full.ModName);
        Assert.All(songs, song => Assert.False(song.HasIdConflict));
        Assert.Equal(SongRunStatus.Ready, full.RunStatus);
        Assert.Empty(full.PatchSources);
        Assert.All(songs.Where(song => song.ModRoot != fullSong.Root), song =>
        {
            Assert.False(song.IsSongPatch);
            Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        });
    }

    [Fact]
    public void ReportsChartlessCopiedSongDatabaseAsPatchOnItsProvider()
    {
        using var mods = new TemporaryMods();
        var provider = mods.CreateMod("Original song");
        var patch = mods.CreateMod("Translation patch");
        var database = String.Join("\n", new[]
        {
            "pv_8610.song_name=Shared song name",
            "pv_8610.song_file_name=rom/sound/song/pv_8610.ogg",
            "pv_8610.difficulty.extreme.0.script_file_name=rom/script/pv_8610_extreme.dsc"
        });
        provider.Write("rom/mod_pv_db.txt", database);
        provider.Write("rom/sound/song/pv_8610.ogg");
        provider.Write("rom/script/pv_8610_extreme.dsc");
        patch.Write("rom/mod_pv_db.txt", database);

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);
        var original = songs.Single(song => song.ModRoot == provider.Root);
        var translated = songs.Single(song => song.ModRoot == patch.Root);

        Assert.True(translated.IsSongPatch);
        Assert.Equal(SongRunStatus.Ready, translated.RunStatus);
        Assert.False(original.HasIdConflict);
        var patchSource = Assert.Single(original.PatchSources);
        Assert.Equal("Translation patch", patchSource.ModName);
        Assert.Equal(translated.DatabasePath, patchSource.SourcePath);
        Assert.Equal(translated.ContentRoot, patchSource.ContentRoot);
        Assert.True(File.Exists(patchSource.SourcePath));
        Assert.Contains(patchSource.SourcePath, original.WarningsDisplay);
    }

    [Fact]
    public void PatchCanInheritExactAlternateMediaFromItsPvIdProvider()
    {
        using var mods = new TemporaryMods();
        var provider = mods.CreateMod("Media provider");
        var patch = mods.CreateMod("Media metadata patch");
        provider.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8602.song_name=Provider",
            "pv_8602.song_file_name=rom/sound/song/pv_8602.ogg",
            "pv_8602.another_song.0.song_file_name=rom/sound/song/pv_8602_alt.ogg",
            "pv_8602.movie_list.0.name=rom/movie/pv_8602_alt.mp4",
            "pv_8602.difficulty.extreme.0.script_file_name=rom/script/pv_8602_extreme.dsc"
        }));
        provider.Write("rom/sound/song/pv_8602.ogg");
        provider.Write("rom/sound/song/pv_8602_alt.ogg");
        provider.Write("rom/movie/pv_8602_alt.mp4");
        provider.Write("rom/script/pv_8602_extreme.dsc");
        patch.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8602.song_name=Provider",
            "pv_8602.song_name_en=Patched title",
            "pv_8602.song_file_name=rom/sound/song/pv_8602.ogg",
            "pv_8602.another_song.0.song_file_name=rom/sound/song/pv_8602_alt.ogg",
            "pv_8602.movie_list.0.name=rom/movie/pv_8602_alt.mp4",
            "pv_8602.difficulty.extreme.0.script_file_name=rom/script/pv_8602_extreme.dsc"
        }));

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);
        var patchedEntry = songs.Single(song => song.ModRoot == patch.Root);

        Assert.True(patchedEntry.IsSongPatch);
        Assert.False(patchedEntry.HasIdConflict);
        Assert.Equal(SongRunStatus.Ready, patchedEntry.RunStatus);
    }

    [Fact]
    public void ClassifiesMissingRuntimeAssetsAndCoverOnlyDegradation()
    {
        using var mods = new TemporaryMods();
        var missingAudio = CreateHealthTestSong(mods, 8501, "Missing audio", writeAudio: false, writeChart: true, writeCover: true);
        var missingChart = CreateHealthTestSong(mods, 8502, "Missing chart", writeAudio: true, writeChart: false, writeCover: true);
        var missingVideo = CreateHealthTestSong(mods, 8503, "Missing video", writeAudio: true, writeChart: true, writeCover: true, declareVideo: true);
        var coverOnly = CreateHealthTestSong(mods, 8504, "Missing cover", writeAudio: true, writeChart: true, writeCover: false);

        var songs = SongCatalogService.ScanMods(mods.Root);

        Assert.Equal(SongRunStatus.Broken, songs.Single(song => song.ModRoot == missingAudio.Root).RunStatus);
        Assert.Contains("缺少主音频文件", songs.Single(song => song.ModRoot == missingAudio.Root).RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SongRunStatus.Broken, songs.Single(song => song.ModRoot == missingChart.Root).RunStatus);
        Assert.Contains("谱面", songs.Single(song => song.ModRoot == missingChart.Root).RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SongRunStatus.Broken, songs.Single(song => song.ModRoot == missingVideo.Root).RunStatus);
        Assert.Contains("缺少数据库声明的视频文件", songs.Single(song => song.ModRoot == missingVideo.Root).RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        var degraded = songs.Single(song => song.ModRoot == coverOnly.Root);
        Assert.Equal(SongRunStatus.Warning, degraded.RunStatus);
        Assert.Single(degraded.RunStatusReasons);
        Assert.Contains("缺少歌曲图片", degraded.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WarnsWhenArtworkArchiveExistsButRequiredSpritesAreMissing()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Incomplete artwork");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8505.song_name=Jacket only",
            "pv_8505.song_file_name=rom/sound/song/pv_8505.ogg",
            "pv_8505.difficulty.extreme.0.script_file_name=rom/script/pv_8505_extreme.dsc"
        }));
        mod.Write("rom/sound/song/pv_8505.ogg");
        mod.Write("rom/script/pv_8505_extreme.dsc");
        mod.WriteJacketOnlyArtwork("8505");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.Equal(SongRunStatus.Warning, song.RunStatus);
        Assert.False(song.ThumbnailExists);
        Assert.True(song.JacketExists);
        Assert.False(song.BackgroundExists);
        Assert.Contains("小图标", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("背景", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TreatsStandaloneFullDeclarationWithNoAssetsAsBrokenInsteadOfPatch()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Completely missing song");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8549.song_name=Missing everything",
            "pv_8549.song_file_name=rom/sound/song/pv_8549.ogg",
            "pv_8549.difficulty.extreme.0.script_file_name=rom/script/pv_8549_extreme.dsc"
        }));

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.False(song.IsSongPatch);
        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.Contains("缺少主音频文件", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("可用的谱面文件", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TreatsStandaloneHighPvPartialDeclarationAsBroken()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Incomplete custom song");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8551.song_name=Missing audio declaration",
            "pv_8551.difficulty.extreme.0.script_file_name=rom/script/pv_8551_extreme.dsc"
        }));
        mod.Write("rom/script/pv_8551_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.False(song.IsSongPatch);
        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.Contains("缺少主音频引用", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecognizesCopiedDatabaseAsPatchOnlyWhenProviderSuppliesExactCoreReferences()
    {
        using var mods = new TemporaryMods();
        var provider = mods.CreateMod("Provider");
        var copiedMetadata = mods.CreateMod("Copied metadata patch");
        var unrelatedBrokenSong = mods.CreateMod("Different broken song");
        var providerDatabase = String.Join("\n", new[]
        {
            "pv_8552.song_name=Original",
            "pv_8552.song_file_name=rom/sound/song/pv_8552.ogg",
            "pv_8552.difficulty.extreme.0.script_file_name=rom/script/pv_8552_extreme.dsc"
        });
        provider.Write("rom/mod_pv_db.txt", providerDatabase);
        provider.Write("rom/sound/song/pv_8552.ogg");
        provider.Write("rom/script/pv_8552_extreme.dsc");
        copiedMetadata.Write("rom/mod_pv_db.txt", providerDatabase.Replace("Original", "Renamed"));
        unrelatedBrokenSong.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8552.song_name=Unrelated",
            "pv_8552.song_file_name=rom/sound/song/different.ogg",
            "pv_8552.difficulty.extreme.0.script_file_name=rom/script/different.dsc"
        }));

        var songs = SongCatalogService.ScanMods(mods.Root);
        var patch = songs.Single(song => song.ModRoot == copiedMetadata.Root);
        var broken = songs.Single(song => song.ModRoot == unrelatedBrokenSong.Root);

        Assert.True(patch.IsSongPatch);
        Assert.False(patch.HasIdConflict);
        Assert.Equal(SongRunStatus.Ready, patch.RunStatus);
        Assert.False(broken.IsSongPatch);
        Assert.True(broken.HasIdConflict);
        Assert.Equal(SongRunStatus.Broken, broken.RunStatus);
        Assert.True(songs.Single(song => song.ModRoot == provider.Root).HasIdConflict);
    }

    [Fact]
    public void RecognizesMissingStandardLowPvAssetsAsMega39PlusBaseSongPatch()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Base song metadata patch");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_008.song_name=Patched stock title",
            "pv_008.song_file_name=rom/sound/song/pv_203.ogg",
            "pv_008.movie_file_name=rom/movie/pv_008.usm",
            "pv_008.difficulty.extreme.0.script_file_name=rom/script/pv_008_extreme.dsc"
        }));

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.True(song.IsSongPatch);
        Assert.True(song.IsMega39PlusOfficialPvId);
        Assert.False(song.HasIdConflict);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
    }

    [Fact]
    public void RejectsModChartThatTargetsAnOfficialMega39PlusPvId()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Illegal official chart");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_008.song_name=Official chart override",
            "pv_008.song_file_name=rom/sound/song/pv_008.ogg",
            "pv_008.difficulty.extreme.0.script_file_name=rom/script/pv_008_extreme.dsc"
        }));
        mod.Write("rom/sound/song/pv_008.ogg");
        var chartPath = mod.Write("rom/script/pv_008_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.True(song.IsMega39PlusOfficialPvId);
        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.Contains("属于 MEGA39+ 官曲", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(chartPath, song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsOnlyLegacyChartAlongsideDeclaredNewClassicsOfficialExtension()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Mixed official chart extension");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_008.song_name=Mixed official extension",
            "pv_008.song_file_name=rom/sound/song/pv_008.ogg",
            "pv_008.difficulty.extreme.0.script_file_name=rom/script/pv_008_extreme.dsc"
        }));
        mod.Write("rom/nc_db.toml", NewClassicsSong(
            8,
            "CONSOLE",
            "rom/script_nc/pv_008_extreme_console.dsc"));
        mod.Write("rom/sound/song/pv_008.ogg");
        var legacyChartPath = mod.Write("rom/script/pv_008_extreme.dsc");
        var newClassicsChartPath = mod.Write("rom/script_nc/pv_008_extreme_console.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(SongFormat.LegacyWithNewClassics, song.Format);
        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.Contains(legacyChartPath, song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(newClassicsChartPath, song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsOfficialChartWhenLegacyAndNewClassicsDeclareTheSamePath()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Shared Legacy and New Classics chart path");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_008.song_name=Shared official chart path",
            "pv_008.song_file_name=rom/sound/song/pv_008.ogg",
            "pv_008.difficulty.extreme.0.script_file_name=rom/script_nc/pv_008_extreme.dsc"
        }));
        mod.Write("rom/nc_db.toml", NewClassicsSong(
            8,
            "CONSOLE",
            "rom/script_nc/pv_008_extreme.dsc"));
        mod.Write("rom/sound/song/pv_008.ogg");
        var sharedChartPath = mod.Write("rom/script_nc/pv_008_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.Contains(sharedChartPath, song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnregisteredChartBesideDeclaredNewClassicsOfficialExtension()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("New Classics extension with stray chart");
        mod.Write("rom/nc_db.toml", NewClassicsSong(
            8,
            "CONSOLE",
            "rom/script_nc/pv_008_extreme.dsc"));
        var declaredChartPath = mod.Write("rom/script_nc/pv_008_extreme.dsc");
        var unregisteredChartPath = mod.Write("rom/script_nc/pv_008_hard.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.Contains(unregisteredChartPath, song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(declaredChartPath, song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(27)]
    [InlineData(700)]
    [InlineData(701)]
    [InlineData(744)]
    public void AllowsCustomSongsOnPvIdsNotMarkedMmPlusBySpreadsheet(int pvId)
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod($"Custom PV {pvId}");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            $"pv_{pvId}.song_name=Custom song {pvId}",
            $"pv_{pvId}.song_file_name=rom/sound/song/pv_{pvId}.ogg",
            $"pv_{pvId}.difficulty.extreme.0.script_file_name=rom/script/pv_{pvId}_extreme.dsc"
        }));
        mod.Write($"rom/sound/song/pv_{pvId}.ogg");
        mod.Write($"rom/script/pv_{pvId}_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.False(song.IsMega39PlusOfficialPvId);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
        Assert.False(song.IsSongPatch);
    }

    [Fact]
    public void AllowsDeclaredNewClassicsExtensionThatTargetsAnOfficialPvId()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Official song additional chart");
        mod.Write("rom/nc_db.toml", NewClassicsSong(
            8,
            "ARCADE",
            "rom/script/pv_008_extreme.dsc"));
        var chartPath = mod.Write("rom/script/pv_008_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(SongFormat.AdditionalDifficulty, song.Format);
        Assert.Equal("New Classics Extension", song.FormatDisplayName);
        Assert.True(song.IsMega39PlusOfficialPvId);
        Assert.True(song.IsSongPatch);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
        Assert.False(song.HasIdConflict);
        Assert.Empty(song.IdConflictSources);
        Assert.Equal(chartPath, Assert.Single(song.LocalChartOverlayPaths));
        Assert.True(Assert.Single(song.Difficulties).IsDeclaredByNewClassicsDatabase);
        Assert.DoesNotContain("属于 MEGA39+ 官曲", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("未找到 MEGA39+ mod_pv_db.txt", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowsNewClassicsMetadataExtensionToInheritOfficialMedia()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Official New Classics metadata extension");
        mod.Write("rom/mod_nc_pv_db.txt", String.Join("\n", new[]
        {
            "pv_008.song_name=Official New Classics extension",
            "pv_008.song_file_name=rom/sound/song/pv_008.ogg",
            "pv_008.movie_file_name=rom/movie/pv_008.usm"
        }));
        mod.Write("rom/nc_db.toml", NewClassicsSong(
            8,
            "MIXED",
            "rom/script_nc/pv_008_extreme.dsc"));
        mod.Write("rom/script_nc/pv_008_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(SongFormat.NewClassics, song.Format);
        Assert.True(song.IsMega39PlusOfficialPvId);
        Assert.True(song.IsSongPatch);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
        Assert.False(song.HasIdConflict);
        Assert.Empty(song.IdConflictSources);
        Assert.False(song.AudioExists);
        Assert.False(song.VideoExists);
        Assert.True(Assert.Single(song.Difficulties).IsDeclaredByNewClassicsDatabase);
    }

    [Fact]
    public void DoesNotLetOfficialNewClassicsExtensionInheritCustomMissingMedia()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Official New Classics extension with custom media");
        mod.Write("rom/mod_nc_pv_db.txt", String.Join("\n", new[]
        {
            "pv_008.song_name=Broken official New Classics extension",
            "pv_008.song_file_name=rom/sound/song/custom_missing.ogg"
        }));
        mod.Write("rom/nc_db.toml", NewClassicsSong(
            8,
            "CONSOLE",
            "rom/script_nc/pv_008_extreme.dsc"));
        mod.Write("rom/script_nc/pv_008_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.False(song.AudioExists);
        Assert.Contains("custom_missing.ogg", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowsMixedEntryToUseStockLegacyAssetsAndLocalNewClassicsChart()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Official mixed New Classics extension");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_008.song_name=Official mixed extension",
            "pv_008.song_file_name=rom/sound/song/pv_008.ogg",
            "pv_008.movie_file_name=rom/movie/pv_008.usm",
            "pv_008.difficulty.extreme.0.script_file_name=rom/script/pv_008_extreme.dsc"
        }));
        mod.Write("rom/nc_db.toml", NewClassicsSong(
            8,
            "CONSOLE",
            "rom/script_nc/pv_008_extreme_console.dsc"));
        mod.Write("rom/script_nc/pv_008_extreme_console.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(SongFormat.LegacyWithNewClassics, song.Format);
        Assert.True(song.IsMega39PlusOfficialPvId);
        Assert.True(song.IsSongPatch);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
        Assert.False(song.HasIdConflict);
        Assert.Empty(song.IdConflictSources);
        Assert.False(song.AudioExists);
        Assert.False(song.VideoExists);
        Assert.False(Assert.Single(
            song.Difficulties,
            difficulty => difficulty.Source == SongDifficultySource.LegacyDatabase).ScriptExists);
        Assert.True(Assert.Single(
            song.Difficulties,
            difficulty => difficulty.IsDeclaredByNewClassicsDatabase).ScriptExists);
    }

    [Theory]
    [InlineData(0x12020220)]
    [InlineData(0x25061313)]
    public void DetectsOfficialPvChartFileWithoutAnyDatabaseEntry(int signature)
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Orphan official chart");
        var chartPath = mod.Write("rom/script_nc/pv_008_extreme.dsc");
        File.WriteAllBytes(chartPath, BitConverter.GetBytes(signature));

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(8, song.PvId);
        Assert.True(song.IsMega39PlusOfficialPvId);
        Assert.True(song.IsOrphanResourceEntry);
        Assert.Equal(SongFormat.OrphanResources, song.Format);
        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.Equal(chartPath, Assert.Single(song.LocalChartOverlayPaths));
        Assert.Empty(song.Difficulties);
        var orphan = Assert.Single(song.OrphanResources);
        Assert.Equal(SongResourceKind.Chart, orphan.Kind);
        Assert.Equal(chartPath, orphan.Path);
        Assert.Equal("废案资源", song.FormatDisplayName);
        Assert.Contains("直接覆盖官曲", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("未被歌曲数据库声明", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsUndeclaredResourcesOutOfDeclaredSongRuntimeState()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Song with abandoned resources");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_9999.song_name=Released song",
            "pv_9999.song_file_name=rom/sound/song/pv_9999.ogg",
            "pv_9999.movie_file_name=rom/movie/pv_9999.usm",
            "pv_9999.difficulty.hard.0.level=PV_LV_07_0",
            "pv_9999.difficulty.hard.0.script_file_name=rom/script/pv_9999_hard.dsc"
        }));
        mod.Write(
            "rom/nc_db.toml",
            NewClassicsSong(9999, "CONSOLE", "rom/script_nc/pv_9999_extreme.dsc"));
        mod.Write("rom/sound/song/pv_9999.ogg");
        mod.Write("rom/movie/pv_9999.usm");
        mod.Write("rom/script/pv_9999_hard.dsc");
        mod.Write("rom/script_nc/pv_9999_extreme.dsc");

        var orphanPaths = new Dictionary<SongResourceKind, string>
        {
            [SongResourceKind.Chart] = mod.Write("rom/script/pv_9999_easy_unused.dsc"),
            [SongResourceKind.Audio] = mod.Write("rom/sound/song/pv_9999_unused.ogg"),
            [SongResourceKind.Video] = mod.Write("rom/movie/pv_9999_unused.mp4"),
            [SongResourceKind.Artwork] = mod.Write("rom/2d/spr_sel_pv9999_unused.farc", "unused"),
            [SongResourceKind.AdditionalParameter] = mod.Write("rom/add_param/pv_9999_unused.adp")
        };

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.False(song.IsOrphanResourceEntry);
        Assert.True(song.HasOrphanResources);
        Assert.Equal(SongFormat.LegacyWithNewClassics, song.Format);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
        Assert.Equal(new[] { "hard", "extreme" },
            song.Difficulties.Select(difficulty => difficulty.NormalizedName));
        Assert.Equal(orphanPaths.Count, song.OrphanResources.Count);
        Assert.All(orphanPaths, expected =>
        {
            var resource = Assert.Single(song.OrphanResources, resource => resource.Kind == expected.Key);
            Assert.Equal(expected.Value, resource.Path);
            Assert.Equal(9999, resource.PvId);
            Assert.DoesNotContain(expected.Value, song.ReferencedAssetPaths);
        });
        Assert.DoesNotContain(song.Difficulties, difficulty =>
            difficulty.ScriptPath.Equals(orphanPaths[SongResourceKind.Chart], StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("unused", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TreatsArtworkArchiveReferencedBySpriteDatabaseAsDeclared()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Sprite DB artwork");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_9998.song_name=Sprite DB song",
            "pv_9998.song_file_name=rom/sound/song/pv_9998.ogg",
            "pv_9998.difficulty.extreme.0.script_file_name=rom/script/pv_9998_extreme.dsc"
        }));
        mod.Write("rom/sound/song/pv_9998.ogg");
        mod.Write("rom/script/pv_9998_extreme.dsc");
        var artworkPath = mod.Write("rom/2d/spr_sel_pv9998_custom.farc", "custom artwork");

        using (var database = new SpriteDatabase())
        {
            database.SpriteSets.Add(new SpriteSetInfo
            {
                Id = 1,
                Name = "SPR_SEL_PV9998_CUSTOM",
                FileName = "spr_sel_pv9998_custom.bin"
            });
            database.Save(Path.Combine(mod.Root, "rom", "2d", "mod_spr_db.bin"));
        }

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.False(song.HasOrphanResources);
        Assert.DoesNotContain(
            song.OrphanResources,
            resource => resource.Path.Equals(artworkPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DoesNotTreatStandaloneCoverReplacementAsOrphanSong()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Cover replacement");
        mod.Write("rom/sound/song/pv_9997.ogg");

        Assert.Empty(SongCatalogService.ScanModsWithoutArtwork(mods.Root));
    }

    [Fact]
    public void CreatesSearchableReadOnlyEntryForResourcesWithoutAnyDatabaseSong()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Abandoned custom song");
        var chartPath = mod.Write("rom/script/pv_9876_extreme_unused.dsc");
        var audioPath = mod.Write("rom/sound/song/pv_9876_unused.ogg");
        var videoPath = mod.Write("rom/movie/pv_9876_unused.mp4");
        var artworkPath = mod.Write("rom/2d/spr_sel_pv9876_unused.farc", "unused");
        var parameterPath = mod.Write("rom/add_param/pv_9876_unused.adp");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(9876, song.PvId);
        Assert.True(song.IsOrphanResourceEntry);
        Assert.True(song.HasOrphanResources);
        Assert.Equal(SongFormat.OrphanResources, song.Format);
        Assert.Empty(song.DatabasePath);
        Assert.Empty(song.NewClassicsDatabasePath);
        Assert.Empty(song.Difficulties);
        Assert.Empty(song.ReferencedAssetPaths);
        Assert.False(song.HasIdConflict);
        Assert.Equal(5, song.OrphanResources.Count);
        Assert.Contains(song.OrphanResources, resource =>
            resource.Kind == SongResourceKind.Chart && resource.Path == chartPath);
        Assert.Contains(song.OrphanResources, resource =>
            resource.Kind == SongResourceKind.Audio && resource.Path == audioPath);
        Assert.Contains(song.OrphanResources, resource =>
            resource.Kind == SongResourceKind.Video && resource.Path == videoPath);
        Assert.Contains(song.OrphanResources, resource =>
            resource.Kind == SongResourceKind.Artwork && resource.Path == artworkPath);
        Assert.Contains(song.OrphanResources, resource =>
            resource.Kind == SongResourceKind.AdditionalParameter && resource.Path == parameterPath);
        Assert.All(song.OrphanResources, resource =>
        {
            Assert.Equal(9876, resource.PvId);
            Assert.False(String.IsNullOrWhiteSpace(resource.RelativePath));
            Assert.False(String.IsNullOrWhiteSpace(resource.DisplayName));
            Assert.Contains(Path.GetFileName(resource.Path), song.SearchText, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void OrphanOnlyEntryDoesNotCreatePvIdConflictWithDeclaredSong()
    {
        using var mods = new TemporaryMods();
        var released = mods.CreateMod("Released provider");
        released.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_9875.song_name=Released provider",
            "pv_9875.song_file_name=rom/sound/song/pv_9875.ogg",
            "pv_9875.difficulty.extreme.0.script_file_name=rom/script/pv_9875_extreme.dsc"
        }));
        released.Write("rom/sound/song/pv_9875.ogg");
        released.Write("rom/script/pv_9875_extreme.dsc");
        var abandoned = mods.CreateMod("Abandoned draft");
        var abandonedChart = abandoned.Write("rom/script/pv_9875_hard_unused.dsc");

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);
        var declaredSong = Assert.Single(songs, song => !song.IsOrphanResourceEntry);
        var orphanEntry = Assert.Single(songs, song => song.IsOrphanResourceEntry);

        Assert.False(declaredSong.HasIdConflict);
        Assert.Empty(declaredSong.IdConflictSources);
        Assert.False(declaredSong.HasSongPatches);
        Assert.Equal(SongRunStatus.Ready, declaredSong.RunStatus);
        Assert.False(orphanEntry.HasIdConflict);
        Assert.Empty(orphanEntry.IdConflictSources);
        Assert.Equal(abandonedChart, Assert.Single(orphanEntry.OrphanResources).Path);
    }

    [Theory]
    [InlineData(448)]
    [InlineData(493)]
    [InlineData(541)]
    public void DoesNotTreatLowIdCustomSongsAsMega39PlusBaseSongPatches(int pvId)
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Low ID custom song");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            $"pv_{pvId}.song_name=Custom PV {pvId}",
            $"pv_{pvId}.song_file_name=rom/sound/song/pv_{pvId}.ogg",
            $"pv_{pvId}.difficulty.extreme.0.script_file_name=rom/script/pv_{pvId}_extreme.dsc"
        }));

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.False(song.IsSongPatch);
        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.Contains("缺少主音频文件", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingAlternateAudioAndAnyDeclaredVideoAreBlocking()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Missing alternate media");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8603.song_name=Alternate media song",
            "pv_8603.song_file_name=rom/sound/song/pv_8603.ogg",
            "pv_8603.another_song.0.song_file_name=rom/sound/song/pv_8603_alt.ogg",
            "pv_8603.movie_file_name=rom/movie/pv_8603.mp4",
            "pv_8603.movie_list.0.name=rom/movie/pv_8603_alt.mp4",
            "pv_8603.difficulty.extreme.0.script_file_name=rom/script/pv_8603_extreme.dsc"
        }));
        mod.Write("rom/sound/song/pv_8603.ogg");
        mod.Write("rom/movie/pv_8603.mp4");
        mod.Write("rom/script/pv_8603_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.Contains("可选演唱版本因缺少音频文件", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("缺少数据库声明的视频文件", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pv_8603_alt.ogg", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pv_8603_alt.mp4", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnabledSiblingMaySupplyMediaButNotLegacyCharts()
    {
        using var mods = new TemporaryMods();
        var songMod = mods.CreateMod("Song metadata and local chart");
        var mediaProvider = mods.CreateMod("Shared media provider");
        songMod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8901.song_name=Shared media song",
            "pv_8901.song_file_name=rom/sound/song/pv_8901.ogg",
            "pv_8901.movie_file_name=rom/movie/pv_8901.usm",
            "pv_8901.difficulty.hard.0.script_file_name=rom/script/pv_8901_hard.dsc",
            "pv_8901.difficulty.extreme.0.script_file_name=rom/script/pv_8901_extreme.dsc"
        }));
        songMod.Write("rom/script/pv_8901_hard.dsc");
        var sharedAudio = mediaProvider.Write("rom/sound/song/pv_8901.ogg");
        var sharedVideo = mediaProvider.Write("rom/movie/pv_8901.usm");
        var unusedChart = mediaProvider.Write("rom/script/pv_8901_extreme.dsc");

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);
        var song = Assert.Single(songs, entry => !entry.IsOrphanResourceEntry);
        var orphan = Assert.Single(songs, entry => entry.IsOrphanResourceEntry);

        Assert.True(song.AudioExists);
        Assert.Equal(sharedAudio, song.AudioPath);
        Assert.True(song.VideoExists);
        Assert.Equal(sharedVideo, song.VideoPath);
        Assert.True(song.Difficulties.Single(difficulty => difficulty.NormalizedName == "hard").ScriptExists);
        var missingExtreme = song.Difficulties.Single(difficulty => difficulty.NormalizedName == "extreme");
        Assert.False(missingExtreme.ScriptExists);
        Assert.StartsWith(songMod.Root, missingExtreme.ScriptPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SongRunStatus.Warning, song.RunStatus);
        Assert.Contains(missingExtreme.ScriptPath, song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unusedChart, Assert.Single(orphan.OrphanResources).Path);
    }

    [Fact]
    public void NewClassicsChartMustExistInDeclaringContentRoot()
    {
        using var mods = new TemporaryMods();
        var songMod = mods.CreateMod("New Classics declaration");
        var sibling = mods.CreateMod("Unrelated matching chart");
        songMod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8902.song_name=Local New Classics declaration",
            "pv_8902.song_file_name=rom/sound/song/pv_8902.ogg",
            "pv_8902.difficulty.hard.0.script_file_name=rom/script/pv_8902_hard.dsc"
        }));
        songMod.Write(
            "rom/nc_db.toml",
            NewClassicsSong(8902, "CONSOLE", "rom/script_nc/pv_8902_extreme.dsc"));
        songMod.Write("rom/sound/song/pv_8902.ogg");
        songMod.Write("rom/script/pv_8902_hard.dsc");
        var unusedChart = sibling.Write("rom/script_nc/pv_8902_extreme.dsc");

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);
        var song = Assert.Single(songs, entry => !entry.IsOrphanResourceEntry);
        var orphan = Assert.Single(songs, entry => entry.IsOrphanResourceEntry);
        var newClassics = Assert.Single(
            song.Difficulties,
            difficulty => difficulty.Source == SongDifficultySource.NewClassicsDatabase);

        Assert.False(newClassics.ScriptExists);
        Assert.False(newClassics.UsesInheritedScript);
        Assert.StartsWith(songMod.Root, newClassics.ScriptPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SongRunStatus.Warning, song.RunStatus);
        Assert.Contains(newClassics.ScriptPath, song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unusedChart, Assert.Single(orphan.OrphanResources).Path);
    }

    [Fact]
    public void SignedEdenCoreInheritsStockBaseChartOnlyAfterLocalExtensionIsVerified()
    {
        using var mods = new TemporaryMods();
        var core = mods.CreateMod("Eden Core");
        WriteSignedEdenCore(core, 83, writeExtraExtremeChart: true);

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));
        var baseExtreme = song.Difficulties.Single(difficulty => difficulty.Index == 0);
        var extraExtreme = song.Difficulties.Single(difficulty => difficulty.Index == 1);

        Assert.True(baseExtreme.ScriptExists);
        Assert.True(baseExtreme.IsAvailableFromGame);
        Assert.True(baseExtreme.UsesInheritedScript);
        Assert.True(extraExtreme.ScriptExists);
        Assert.True(extraExtreme.IsOfficialLegacyExtension);
        Assert.False(extraExtreme.UsesInheritedScript);
        Assert.StartsWith(core.Root, extraExtreme.ScriptPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(song.HasInvalidOfficialChartOverride);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
    }

    [Fact]
    public void SignedEdenCoreCannotBorrowExtraExtremeChartFromSibling()
    {
        using var mods = new TemporaryMods();
        var core = mods.CreateMod("Eden Core");
        var sibling = mods.CreateMod("Matching chart in another mod");
        WriteSignedEdenCore(core, 83, writeExtraExtremeChart: false);
        sibling.Write("rom/script/pv_083_extreme_1.dsc");

        var song = SongCatalogService.ScanModsWithoutArtwork(mods.Root)
            .Single(entry => entry.ModRoot == core.Root);
        var baseExtreme = song.Difficulties.Single(difficulty => difficulty.Index == 0);
        var extraExtreme = song.Difficulties.Single(difficulty => difficulty.Index == 1);

        Assert.False(baseExtreme.ScriptExists);
        Assert.False(extraExtreme.ScriptExists);
        Assert.False(extraExtreme.IsOfficialLegacyExtension);
        Assert.False(extraExtreme.UsesInheritedScript);
        Assert.StartsWith(core.Root, extraExtreme.ScriptPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
    }

    [Theory]
    [InlineData("HasCompleteCoreAssets")]
    [InlineData("HasUsableSongMedia")]
    public void VirtualGameMediaUsesRecordedAvailabilityInsteadOfFileExists(string predicateName)
    {
        var entry = new SongEntry
        {
            ModEnabled = true,
            RunStatus = SongRunStatus.Ready,
            AudioExists = true,
            AudioPath = "game://rom/sound/song/pv_083.ogg",
            AlternateAudioReferences = new[] { "rom/sound/song/pv_083_alt.ogg" },
            AlternateAudioPaths = new[] { "game://rom/sound/song/pv_083_alt.ogg" },
            AlternateAudioAvailability = new[] { true },
            VideoReferences = new[] { "rom/movie/pv_083.usm" },
            VideoPaths = new[] { "game://rom/movie/pv_083.usm" },
            VideoAvailability = new[] { true },
            Difficulties = new[]
            {
                new SongDifficulty
                {
                    Name = "extreme",
                    NormalizedName = "extreme",
                    ScriptExists = true
                }
            }
        };
        var predicate = typeof(SongCatalogService).GetMethod(
            predicateName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(predicate);
        Assert.True(Assert.IsType<bool>(predicate.Invoke(null, new object[] { entry })));
    }

    [Fact]
    public void DisabledSongsRemainManageableButDoNotCreateActivePvIdConflicts()
    {
        using var mods = new TemporaryMods();
        var enabled = mods.CreateMod("Enabled song");
        var disabled = mods.CreateMod(
            "Disabled song",
            "enabled = false\nname = \"Disabled song\"\ninclude = [\".\"]\n");
        foreach (var item in new[] { enabled, disabled })
        {
            item.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
            {
                "pv_8604.song_name=Same active ID",
                "pv_8604.song_file_name=rom/sound/song/pv_8604.ogg",
                "pv_8604.difficulty.extreme.0.script_file_name=rom/script/pv_8604_extreme.dsc"
            }));
            item.Write("rom/sound/song/pv_8604.ogg");
            item.Write("rom/script/pv_8604_extreme.dsc");
        }

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);

        Assert.Equal(2, songs.Count);
        Assert.True(songs.Single(song => song.ModRoot == enabled.Root).ModEnabled);
        Assert.False(songs.Single(song => song.ModRoot == disabled.Root).ModEnabled);
        Assert.All(songs, song => Assert.False(song.HasIdConflict));
    }

    [Fact]
    public void DisabledSongPatchIsNotReportedOnTheActiveProvider()
    {
        using var mods = new TemporaryMods();
        var provider = mods.CreateMod("Active provider");
        var patch = mods.CreateMod(
            "Disabled patch",
            "enabled = false\nname = \"Disabled patch\"\ninclude = [\".\"]\n");
        var database = String.Join("\n", new[]
        {
            "pv_8611.song_name=Shared title",
            "pv_8611.song_file_name=rom/sound/song/pv_8611.ogg",
            "pv_8611.difficulty.extreme.0.script_file_name=rom/script/pv_8611_extreme.dsc"
        });
        provider.Write("rom/mod_pv_db.txt", database);
        provider.Write("rom/sound/song/pv_8611.ogg");
        provider.Write("rom/script/pv_8611_extreme.dsc");
        patch.Write("rom/mod_pv_db.txt", database);

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);
        var original = songs.Single(song => song.ModRoot == provider.Root);

        Assert.Empty(original.PatchSources);
        Assert.False(songs.Single(song => song.ModRoot == patch.Root).ModEnabled);
    }

    [Fact]
    public void LoaderPriorityExcludesUnlistedModsFromActivePvIdConflicts()
    {
        using var mods = new TemporaryMods();
        var active = mods.CreateMod("Active song");
        var excluded = mods.CreateMod("Excluded song");
        mods.WriteLoaderConfig($"enabled = true\nmods = \"mods\"\npriority = [\"./{Path.GetFileName(active.Root)}\"]\n");
        foreach (var item in new[] { active, excluded })
        {
            item.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
            {
                "pv_8612.song_name=Priority song",
                "pv_8612.song_file_name=rom/sound/song/pv_8612.ogg",
                "pv_8612.difficulty.extreme.0.script_file_name=rom/script/pv_8612_extreme.dsc"
            }));
            item.Write("rom/sound/song/pv_8612.ogg");
            item.Write("rom/script/pv_8612_extreme.dsc");
        }

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);

        Assert.True(songs.Single(song => song.ModRoot == active.Root).ModEnabled);
        Assert.False(songs.Single(song => song.ModRoot == excluded.Root).ModEnabled);
        Assert.All(songs, song => Assert.False(song.HasIdConflict));
    }

    [Fact]
    public void LoaderPriorityScansNestedRelativeModPath()
    {
        using var mods = new TemporaryMods();
        var nested = new TemporaryMod(Path.Combine(mods.Root, "Group", "Nested song"));
        nested.Write("config.toml", "enabled = true\nname = \"Nested song\"\ninclude = [\".\"]\n");
        nested.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8613.song_name=Nested priority song",
            "pv_8613.song_file_name=rom/sound/song/pv_8613.ogg",
            "pv_8613.difficulty.extreme.0.script_file_name=rom/script/pv_8613_extreme.dsc"
        }));
        nested.Write("rom/sound/song/pv_8613.ogg");
        nested.Write("rom/script/pv_8613_extreme.dsc");
        mods.WriteLoaderConfig("enabled = true\nmods = \"mods\"\npriority = [\"Group/Nested song\"]\n");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.True(song.ModEnabled);
        Assert.Equal(nested.Root, song.ModRoot);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
    }

    [Fact]
    public void ScansSiblingContentRootMountedByLoaderInclude()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod(
            "Shared include",
            "enabled = true\nname = \"Shared include\"\ninclude = [\"../../shared-content\"]\n");
        var sharedRoot = Path.GetFullPath(Path.Combine(mod.Root, "..", "..", "shared-content"));
        var databasePath = Path.Combine(sharedRoot, "rom", "mod_pv_db.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllText(databasePath, String.Join("\n", new[]
        {
            "pv_8614.song_name=Shared content song",
            "pv_8614.song_file_name=rom/sound/song/pv_8614.ogg",
            "pv_8614.difficulty.extreme.0.script_file_name=rom/script/pv_8614_extreme.dsc"
        }));
        var audioPath = Path.Combine(sharedRoot, "rom", "sound", "song", "pv_8614.ogg");
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        File.WriteAllText(audioPath, String.Empty);
        var chartPath = Path.Combine(sharedRoot, "rom", "script", "pv_8614_extreme.dsc");
        Directory.CreateDirectory(Path.GetDirectoryName(chartPath)!);
        File.WriteAllText(chartPath, String.Empty);

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.True(song.ModEnabled);
        Assert.Equal(sharedRoot, song.ContentRoot);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
    }

    [Fact]
    public void BrokenMediaProviderCannotMakeItsMetadataPatchReady()
    {
        using var mods = new TemporaryMods();
        var provider = mods.CreateMod("Broken provider");
        var patch = mods.CreateMod("Metadata patch");
        provider.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8605.song_name=Provider",
            "pv_8605.song_file_name=rom/sound/song/pv_8605.ogg",
            "pv_8605.movie_file_name=rom/movie/pv_8605_missing.mp4",
            "pv_8605.difficulty.extreme.0.script_file_name=rom/script/pv_8605_extreme.dsc"
        }));
        provider.Write("rom/sound/song/pv_8605.ogg");
        provider.Write("rom/script/pv_8605_extreme.dsc");
        patch.Write("rom/mod_pv_db.txt", "pv_8605.song_name_en=Patched title\n");

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);

        Assert.Equal(SongRunStatus.Broken, songs.Single(song => song.ModRoot == provider.Root).RunStatus);
        Assert.Equal(SongRunStatus.Broken, songs.Single(song => song.ModRoot == patch.Root).RunStatus);
    }

    [Fact]
    public void AdditionalDifficultyDoesNotUseADisabledTargetSong()
    {
        using var mods = new TemporaryMods();
        var target = mods.CreateMod(
            "Disabled target",
            "enabled = false\nname = \"Disabled target\"\ninclude = [\".\"]\n");
        var addition = mods.CreateMod("Enabled additional difficulty");
        target.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8606.song_name=Disabled target",
            "pv_8606.song_file_name=rom/sound/song/pv_8606.ogg",
            "pv_8606.difficulty.extreme.0.script_file_name=rom/script/pv_8606_extreme.dsc"
        }));
        target.Write("rom/sound/song/pv_8606.ogg");
        target.Write("rom/script/pv_8606_extreme.dsc");
        addition.Write("rom/nc_db.toml", NewClassicsSong(
            8606,
            "ARCADE",
            "rom/script_nc/pv_8606_extreme.dsc"));
        addition.Write("rom/script_nc/pv_8606_extreme.dsc");

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);
        var additional = songs.Single(song => song.Format == SongFormat.AdditionalDifficulty);

        Assert.Equal(SongRunStatus.Broken, additional.RunStatus);
        Assert.Contains("目标歌曲", additional.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingIndexedChartIsAWarningWhenAnotherChartIsUsable()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Indexed difficulties");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8607.song_name=Indexed charts",
            "pv_8607.song_file_name=rom/sound/song/pv_8607.ogg",
            "pv_8607.difficulty.extreme.0.script_file_name=rom/script/pv_8607_extreme.dsc",
            "pv_8607.difficulty.extreme.1.level=PV_LV_10_0"
        }));
        mod.Write("rom/sound/song/pv_8607.ogg");
        mod.Write("rom/script/pv_8607_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(SongRunStatus.Warning, song.RunStatus);
        Assert.Contains("未声明谱面路径", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DifficultyLengthZeroIgnoresInactiveTemplateSlot()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Inactive difficulty template");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8801.song_name=Inactive template",
            "pv_8801.song_file_name=rom/sound/song/pv_8801.ogg",
            "pv_8801.difficulty.easy.length=0",
            "pv_8801.difficulty.easy.0.level=PV_LV_03_0",
            "pv_8801.difficulty.easy.0.script_file_name=rom/script/pv_8801_easy.dsc",
            "pv_8801.difficulty.hard.length=1",
            "pv_8801.difficulty.hard.0.level=PV_LV_06_0",
            "pv_8801.difficulty.hard.0.script_file_name=rom/script/pv_8801_hard.dsc"
        }));
        mod.Write("rom/sound/song/pv_8801.ogg");
        mod.Write("rom/script/pv_8801_hard.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        var difficulty = Assert.Single(song.Difficulties);
        Assert.Equal("hard", difficulty.NormalizedName);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
        Assert.False(song.IsSongPatch);
        Assert.DoesNotContain("pv_8801_easy.dsc", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(song.ReferencedAssetPaths, path =>
            path.EndsWith("pv_8801_easy.dsc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DifficultyLengthOneIgnoresHigherIndexedTemplateSlot()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Single active difficulty slot");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8802.song_name=Single active slot",
            "pv_8802.song_file_name=rom/sound/song/pv_8802.ogg",
            "pv_8802.difficulty.extreme.length=1",
            "pv_8802.difficulty.extreme.0.level=PV_LV_08_0",
            "pv_8802.difficulty.extreme.0.script_file_name=rom/script/pv_8802_extreme.dsc",
            "pv_8802.difficulty.extreme.1.attribute.extra=1",
            "pv_8802.difficulty.extreme.1.level=PV_LV_09_0",
            "pv_8802.difficulty.extreme.1.script_file_name=rom/script/pv_8802_extreme_1.dsc"
        }));
        mod.Write("rom/sound/song/pv_8802.ogg");
        mod.Write("rom/script/pv_8802_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        var difficulty = Assert.Single(song.Difficulties);
        Assert.Equal(0, difficulty.Index);
        Assert.Equal("extreme", difficulty.NormalizedName);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
        Assert.DoesNotContain("pv_8802_extreme_1.dsc", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DifficultyLengthTwoWarnsWhenOnlyOneActiveSlotIsMissing()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Missing active difficulty slot");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8803.song_name=Missing active slot",
            "pv_8803.song_file_name=rom/sound/song/pv_8803.ogg",
            "pv_8803.difficulty.extreme.length=2",
            "pv_8803.difficulty.extreme.0.script_file_name=rom/script/pv_8803_extreme.dsc"
        }));
        mod.Write("rom/sound/song/pv_8803.ogg");
        mod.Write("rom/script/pv_8803_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(2, song.Difficulties.Count);
        Assert.Single(song.Difficulties, difficulty => difficulty.ScriptExists);
        Assert.Equal(SongRunStatus.Warning, song.RunStatus);
        Assert.Contains("数据库引用的", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("extreme[1]", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("谱面", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingDifficultyLengthPreservesLegacyIndexedSlots()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Legacy database without lengths");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8804.song_name=Legacy indexed slots",
            "pv_8804.song_file_name=rom/sound/song/pv_8804.ogg",
            "pv_8804.difficulty.extreme.0.script_file_name=rom/script/pv_8804_extreme.dsc",
            "pv_8804.difficulty.extreme.1.attribute.extra=1",
            "pv_8804.difficulty.extreme.1.script_file_name=rom/script/pv_8804_extreme_1.dsc"
        }));
        mod.Write("rom/sound/song/pv_8804.ogg");
        mod.Write("rom/script/pv_8804_extreme.dsc");
        mod.Write("rom/script/pv_8804_extreme_1.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal(new[] { "extreme", "ex_extreme" },
            song.Difficulties.Select(difficulty => difficulty.NormalizedName));
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
    }

    [Fact]
    public void InvalidDifficultyLengthFallsBackToLegacySlotsAndWarns()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Invalid difficulty length");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8805.song_name=Invalid length",
            "pv_8805.song_file_name=rom/sound/song/pv_8805.ogg",
            "pv_8805.difficulty.extreme.length=invalid",
            "pv_8805.difficulty.extreme.0.script_file_name=rom/script/pv_8805_extreme.dsc"
        }));
        mod.Write("rom/sound/song/pv_8805.ogg");
        mod.Write("rom/script/pv_8805_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Single(song.Difficulties);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
        Assert.Contains("difficulty.extreme.length", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingOptionalAlternateSongAudioIsWarningOnly()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Optional alternate vocal");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8806.song_name=Optional alternate vocal",
            "pv_8806.song_file_name=rom/sound/song/pv_8806.ogg",
            "pv_8806.another_song.length=2",
            "pv_8806.another_song.0.name=Main vocal",
            "pv_8806.another_song.1.name=Optional vocal",
            "pv_8806.another_song.1.song_file_name=rom/sound/song/pv_8806_optional.ogg",
            "pv_8806.difficulty.extreme.length=1",
            "pv_8806.difficulty.extreme.0.script_file_name=rom/script/pv_8806_extreme.dsc"
        }));
        mod.Write("rom/sound/song/pv_8806.ogg");
        mod.Write("rom/script/pv_8806_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Single(song.AlternateAudioReferences);
        Assert.Equal(SongRunStatus.Warning, song.RunStatus);
        Assert.Contains("可选演唱版本", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("可选音频", song.AssetStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("主音频", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingOptionalAlternateAudioDoesNotInvalidateProviderTargetOrPatch()
    {
        using var mods = new TemporaryMods();
        var provider = mods.CreateMod("Optional-vocal provider");
        var patch = mods.CreateMod("Optional-vocal patch");
        var addition = mods.CreateMod("Additional difficulty");
        var database = String.Join("\n", new[]
        {
            "pv_8810.song_name=Shared optional vocal song",
            "pv_8810.song_file_name=rom/sound/song/pv_8810.ogg",
            "pv_8810.another_song.0.song_file_name=rom/sound/song/pv_8810_optional.ogg",
            "pv_8810.difficulty.extreme.0.script_file_name=rom/script/pv_8810_extreme.dsc"
        });
        provider.Write("rom/mod_pv_db.txt", database);
        provider.Write("rom/sound/song/pv_8810.ogg");
        provider.Write("rom/script/pv_8810_extreme.dsc");
        patch.Write("rom/mod_pv_db.txt", database);
        addition.Write(
            "rom/nc_db.toml",
            NewClassicsSong(8810, "ARCADE", "rom/script_nc/pv_8810_hard.dsc"));
        addition.Write("rom/script_nc/pv_8810_hard.dsc");

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);
        var providerSong = songs.Single(song => song.ModRoot == provider.Root);
        var patchSong = songs.Single(song => song.ModRoot == patch.Root);
        var additionalDifficulty = songs.Single(song => song.ModRoot == addition.Root);

        Assert.Equal(SongRunStatus.Warning, providerSong.RunStatus);
        Assert.True(patchSong.IsSongPatch);
        Assert.Equal(SongRunStatus.Warning, patchSong.RunStatus);
        Assert.Contains("可选音频", patchSong.RunStatusReasonsDisplay);
        Assert.Equal(SongRunStatus.Ready, additionalDifficulty.RunStatus);
        Assert.False(additionalDifficulty.Uses3dPv);
    }

    [Fact]
    public void InactiveDifficultySlotDoesNotDowngradeFullSongProviderToPatch()
    {
        using var mods = new TemporaryMods();
        var edenStyle = mods.CreateMod("Eden-style provider");
        var completeProvider = mods.CreateMod("Complete provider");
        edenStyle.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8807.song_name=Shared full song",
            "pv_8807.song_file_name=rom/sound/song/pv_8807.ogg",
            "pv_8807.difficulty.easy.length=0",
            "pv_8807.difficulty.easy.0.script_file_name=rom/script/pv_8807_easy.dsc",
            "pv_8807.difficulty.extreme.length=1",
            "pv_8807.difficulty.extreme.0.script_file_name=rom/script/pv_8807_extreme.dsc"
        }));
        edenStyle.Write("rom/sound/song/pv_8807.ogg");
        edenStyle.Write("rom/script/pv_8807_extreme.dsc");
        completeProvider.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8807.song_name=Shared full song",
            "pv_8807.song_file_name=rom/sound/song/pv_8807.ogg",
            "pv_8807.difficulty.easy.length=1",
            "pv_8807.difficulty.easy.0.script_file_name=rom/script/pv_8807_easy.dsc",
            "pv_8807.difficulty.extreme.length=1",
            "pv_8807.difficulty.extreme.0.script_file_name=rom/script/pv_8807_extreme.dsc"
        }));
        completeProvider.Write("rom/sound/song/pv_8807.ogg");
        completeProvider.Write("rom/script/pv_8807_easy.dsc");
        completeProvider.Write("rom/script/pv_8807_extreme.dsc");

        var songs = SongCatalogService.ScanModsWithoutArtwork(mods.Root);

        Assert.Equal(2, songs.Count);
        Assert.All(songs, song => Assert.False(song.IsSongPatch));
        Assert.All(songs, song => Assert.True(song.HasIdConflict));
        Assert.All(songs, song => Assert.Equal(SongRunStatus.Broken, song.RunStatus));
        Assert.All(songs, song => Assert.Equal(2, song.IdConflictSources.Count));
        Assert.All(songs, song => Assert.Contains(
            "多个模组中定义了完整歌曲",
            song.RunStatusReasonsDisplay,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WarnsWhenEdenSongPackVersionDoesNotMatchResearchedV56Release()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod(
            "Old Eden pack",
            "enabled = true\n" +
            "name = \"Eden Project - Beginner Pack\"\n" +
            "version = \"1.0.3\"\n" +
            "author = \"Eden Project Team\"\n" +
            "include = [\".\"]\n");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8808.song_name=Old Eden song",
            "pv_8808.song_file_name=rom/sound/song/pv_8808.ogg",
            "pv_8808.difficulty.extreme.length=1",
            "pv_8808.difficulty.extreme.0.script_file_name=rom/script/pv_8808_extreme.dsc"
        }));
        mod.Write("rom/sound/song/pv_8808.ogg");
        mod.Write("rom/script/pv_8808_extreme.dsc");

        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(mods.Root));

        Assert.Equal("1.0.3", song.ModVersion);
        Assert.True(song.RequiresEdenProjectCore);
        Assert.Contains("v5.6", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.0.4", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SongRunStatus.Broken, song.RunStatus);
        Assert.Contains("Eden Core", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsWarningWhenOnlySomeDeclaredDifficultyChartsAreMissing()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Partially inherited difficulties");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8550.song_name=Multi-difficulty song",
            "pv_8550.song_file_name=rom/sound/song/pv_8550.ogg",
            "pv_8550.difficulty.hard.0.script_file_name=rom/script/pv_8550_hard.dsc",
            "pv_8550.difficulty.extreme.0.script_file_name=rom/script/pv_8550_extreme.dsc",
            "pv_8550.another_song.0.song_file_name=rom/sound/song/pv_8550_alt.ogg"
        }));
        mod.Write("rom/sound/song/pv_8550.ogg");
        mod.Write("rom/script/pv_8550_hard.dsc");
        mod.Write("rom/2d/spr_sel_pv8550.farc");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.Equal(2, song.Difficulties.Count);
        Assert.Single(song.Difficulties, difficulty => difficulty.ScriptExists);
        Assert.Contains("谱面文件", song.AssetStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("可选音频", song.AssetStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SongRunStatus.Warning, song.RunStatus);
        Assert.Contains("数据库引用的", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pv_8550_extreme.dsc", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizesDifficultyNamesAndMega39PlusLevelRatingsForFiltering()
    {
        using var mods = new TemporaryMods();
        var mod = mods.CreateMod("Difficulty filters");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", new[]
        {
            "pv_8701.song_name=Filter song",
            "pv_8701.song_file_name=rom/sound/song/pv_8701.ogg",
            "pv_8701.difficulty.easy.0.level=PV_LV_01_0",
            "pv_8701.difficulty.easy.0.script_file_name=rom/script/pv_8701_easy.dsc",
            "pv_8701.difficulty.extreme.0.level=PV_LV_09_5",
            "pv_8701.difficulty.extreme.0.script_file_name=rom/script/pv_8701_extreme.dsc",
            "pv_8701.difficulty.hard.1.attribute.extra=1",
            "pv_8701.difficulty.hard.1.level=PV_LV_10_0",
            "pv_8701.difficulty.hard.1.script_file_name=rom/script/pv_8701_ex_hard.dsc",
            "pv_8701.difficulty.encore.0.level=PV_LV_11_0",
            "pv_8701.difficulty.encore.0.script_file_name=rom/script/pv_8701_encore.dsc",
            "pv_8701.difficulty.normal.0.level=PV_LV_09_3",
            "pv_8701.difficulty.normal.0.script_file_name=rom/script/pv_8701_normal.dsc"
        }));
        mod.Write("rom/sound/song/pv_8701.ogg");
        mod.Write("rom/2d/spr_sel_pv8701.farc");
        foreach (var name in new[] { "easy", "extreme", "ex_hard", "encore", "normal" })
            mod.Write($"rom/script/pv_8701_{name}.dsc");

        var song = Assert.Single(SongCatalogService.ScanMods(mods.Root));

        Assert.Equal(new[] { "easy", "normal", "extreme", "encore", "ex_hard" },
            song.Difficulties.Select(difficulty => difficulty.NormalizedName));
        Assert.Equal(1m, song.Difficulties.Single(difficulty => difficulty.NormalizedName == "easy").NumericLevel);
        Assert.Equal(9.5m, song.Difficulties.Single(difficulty => difficulty.NormalizedName == "extreme").NumericLevel);
        var extraHard = song.Difficulties.Single(difficulty => difficulty.NormalizedName == "ex_hard");
        Assert.True(extraHard.IsExtra);
        Assert.Equal(10m, extraHard.NumericLevel);
        Assert.Equal("10", extraHard.LevelDisplay);
        Assert.All(
            song.Difficulties.Where(difficulty => difficulty.NormalizedName is "encore" or "normal"),
            difficulty =>
            {
                Assert.Null(difficulty.NumericLevel);
                Assert.Equal("?", difficulty.LevelDisplay);
            });
    }

    [Fact]
    public void ExplicitEmptyIncludeListAndMalformedConfigAreNotScanned()
    {
        using var mods = new TemporaryMods();
        var empty = mods.CreateMod("Empty", "enabled = true\ninclude = []\n");
        var emptyString = mods.CreateMod("Empty string", "enabled = true\ninclude = [\"\"]\n");
        var malformed = mods.CreateMod("Malformed", "enabled = true\ninclude = [\n");
        empty.Write("rom/mod_pv_db.txt", "pv_1.song_name=Ignored\n");
        emptyString.Write("rom/mod_pv_db.txt", "pv_2.song_name=Ignored\n");
        malformed.Write("rom/mod_pv_db.txt", "pv_3.song_name=Ignored\n");

        Assert.Empty(SongCatalogService.ScanMods(mods.Root));
    }

    [Fact]
    public async Task SyncAndAsyncScansHonorCancellation()
    {
        using var mods = new TemporaryMods();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SongCatalogService.ScanMods(mods.Root, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Task.FromResult(SongCatalogService.ScanModsAsync(mods.Root, cancellation.Token)).Unwrap());
    }

    private static TemporaryMod CreateHealthTestSong(
        TemporaryMods mods,
        int pvId,
        string name,
        bool writeAudio,
        bool writeChart,
        bool writeCover,
        bool declareVideo = false)
    {
        var mod = mods.CreateMod(name);
        var rawId = pvId.ToString();
        var fields = new List<string>
        {
            $"pv_{rawId}.song_name={name}",
            $"pv_{rawId}.song_file_name=rom/sound/song/pv_{rawId}.ogg",
            $"pv_{rawId}.difficulty.extreme.0.script_file_name=rom/script/pv_{rawId}_extreme.dsc"
        };
        if (declareVideo)
            fields.Add($"pv_{rawId}.movie_file_name=rom/movie/pv_{rawId}.usm");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", fields));
        if (writeAudio)
            mod.Write($"rom/sound/song/pv_{rawId}.ogg");
        if (writeChart)
            mod.Write($"rom/script/pv_{rawId}_extreme.dsc");
        if (writeCover)
            mod.Write($"rom/2d/spr_sel_pv{rawId}.farc");
        return mod;
    }

    private static IReadOnlyList<SongEntry> CreateSameSourceDefinitions(
        TemporaryMods mods,
        int pvId,
        string firstDifficulty,
        int firstIndex,
        string? firstLevel,
        bool firstExtra,
        string secondDifficulty,
        int secondIndex,
        string? secondLevel,
        bool secondExtra)
    {
        var config =
            "enabled = true\n" +
            "name = \"Shared logical source\"\n" +
            "author = \"Shared author\"\n" +
            "include = [\".\"]\n";
        var first = mods.CreateMod($"Source fragment A {pvId}", config);
        var second = mods.CreateMod($"Source fragment B {pvId}", config);
        var songName = $"Shared song {pvId}";
        WriteSongDefinition(
            first, pvId, songName, firstDifficulty, firstIndex, firstLevel, firstExtra, "a");
        WriteSongDefinition(
            second, pvId, songName, secondDifficulty, secondIndex, secondLevel, secondExtra, "b");
        return SongCatalogService.ScanModsWithoutArtwork(mods.Root);
    }

    private static void WriteSongDefinition(
        TemporaryMod mod,
        int pvId,
        string songName,
        string difficulty,
        int index,
        string? level,
        bool isExtra,
        string suffix)
    {
        var fields = new List<string>
        {
            $"pv_{pvId}.song_name={songName}",
            $"pv_{pvId}.song_file_name=rom/sound/song/pv_{pvId}.ogg"
        };
        if (isExtra)
            fields.Add($"pv_{pvId}.difficulty.{difficulty}.{index}.attribute.extra=1");
        if (!String.IsNullOrWhiteSpace(level))
            fields.Add($"pv_{pvId}.difficulty.{difficulty}.{index}.level={level}");
        var chartReference = $"rom/script/pv_{pvId}_{difficulty}_{suffix}.dsc";
        fields.Add($"pv_{pvId}.difficulty.{difficulty}.{index}.script_file_name={chartReference}");
        mod.Write("rom/mod_pv_db.txt", String.Join("\n", fields));
        mod.Write($"rom/sound/song/pv_{pvId}.ogg");
        mod.Write(chartReference);
    }

    private static void WriteSignedEdenCore(
        TemporaryMod mod,
        int pvId,
        bool writeExtraExtremeChart)
    {
        var rawPvId = pvId.ToString("D3");
        mod.Write(
            "config.toml",
            "enabled = true\n" +
            "name = \"Eden Project - Core\"\n" +
            "version = \"1.0.4\"\n" +
            "author = \"Eden Project Team\"\n" +
            "include = [\".\"]\n" +
            "dll = [\"DLCChecker.dll\", \"OldMan.dll\", \"SaveDataMigrator.dll\"]\n");
        mod.Write("settings.toml", "[settings]\n");
        mod.Write("DLCChecker.dll", "test signature");
        mod.Write("OldMan.dll", "test signature");
        mod.Write("SaveDataMigrator.dll", "test signature");
        mod.Write(
            "rom/mod_pv_db.txt",
            $"pv_{rawPvId}.song_name=Eden official Extra Extreme {pvId}\n" +
            $"pv_{rawPvId}.song_file_name=rom/sound/song/pv_{rawPvId}.ogg\n" +
            $"pv_{rawPvId}.difficulty.extreme.length=2\n" +
            $"pv_{rawPvId}.difficulty.extreme.0.level=PV_LV_08_0\n" +
            $"pv_{rawPvId}.difficulty.extreme.0.script_file_name=rom/script/pv_{rawPvId}_extreme.dsc\n" +
            $"pv_{rawPvId}.difficulty.extreme.1.attribute.extra=1\n" +
            $"pv_{rawPvId}.difficulty.extreme.1.level=PV_LV_09_5\n" +
            $"pv_{rawPvId}.difficulty.extreme.1.script_file_name=rom/script/pv_{rawPvId}_extreme_1.dsc\n");
        if (writeExtraExtremeChart)
            mod.Write($"rom/script/pv_{rawPvId}_extreme_1.dsc");
    }

    private static string NewClassicsSong(int id, string style, string script)
    {
        return $"[[songs]]\nid = {id}\n\n" +
            "[[songs.extreme]]\n" +
            $"style = \"{style}\"\n" +
            $"script_file_name = \"{script}\"\n" +
            "level = \"PV_LV_09_0\"\n" +
            "charter = \"Test Charter\"\n";
    }

    private sealed class TemporaryMods : IDisposable
    {
        private int modCounter;
        private readonly string containerRoot;

        public TemporaryMods()
        {
            containerRoot = Path.Combine(Path.GetTempPath(), "DivaModManager.SongCatalogTests", Guid.NewGuid().ToString("N"));
            Root = Path.Combine(containerRoot, "mods");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public TemporaryMod CreateMod(string name, string? config = null)
        {
            var folderName = $"{++modCounter:00}-{name}";
            var mod = new TemporaryMod(Path.Combine(Root, folderName));
            mod.Write("config.toml", config ?? $"enabled = true\nname = \"{name}\"\ninclude = [\".\"]\n");
            return mod;
        }

        public void WriteLoaderConfig(string contents)
        {
            File.WriteAllText(Path.Combine(containerRoot, "config.toml"), contents);
        }

        public void Dispose()
        {
            if (Directory.Exists(containerRoot))
                Directory.Delete(containerRoot, true);
        }
    }

    private sealed class TemporaryMod
    {
        public TemporaryMod(string root)
        {
            Root = root;
            Directory.CreateDirectory(root);
        }

        public string Root { get; }

        public string Write(string relativePath, string contents = "")
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var artworkMatch = Regex.Match(
                relativePath,
                @"^rom/2d/spr_sel_pv(\d+)\.farc$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (contents.Length == 0 && artworkMatch.Success)
            {
                WriteCompleteArtwork(artworkMatch.Groups[1].Value, path);
                return path;
            }
            File.WriteAllText(path, contents);
            return path;
        }

        public void WriteJacketOnlyArtwork(string rawPvId)
        {
            var path = Path.Combine(Root, "rom", "2d", $"spr_sel_pv{rawPvId}.farc");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var texture = new Texture(8, 8, TextureFormat.RGBA8);
            SaveArtworkArchive(
                path,
                $"spr_sel_pv{rawPvId}.bin",
                texture,
                new[]
                {
                    new Sprite
                    {
                        Name = $"SPR_SEL_PV{rawPvId}_SONG_JK{rawPvId}",
                        TextureIndex = 0,
                        X = 0,
                        Y = 0,
                        Width = 8,
                        Height = 8
                    }
                });
        }

        private void WriteCompleteArtwork(string rawPvId, string perPvArchivePath)
        {
            var perPvTexture = new Texture(16, 8, TextureFormat.RGBA8);
            SaveArtworkArchive(
                perPvArchivePath,
                $"spr_sel_pv{rawPvId}.bin",
                perPvTexture,
                new[]
                {
                    new Sprite
                    {
                        Name = $"SPR_SEL_PV{rawPvId}_SONG_JK{rawPvId}",
                        TextureIndex = 0,
                        X = 0,
                        Y = 0,
                        Width = 8,
                        Height = 8
                    },
                    new Sprite
                    {
                        Name = $"SPR_SEL_PV{rawPvId}_SONG_BG{rawPvId}",
                        TextureIndex = 0,
                        X = 0,
                        Y = 0,
                        Width = 16,
                        Height = 8
                    }
                });

            var thumbnailPath = Path.Combine(
                Root,
                "rom",
                "2d",
                $"spr_sel_pvtmb_{rawPvId}.farc");
            SaveArtworkArchive(
                thumbnailPath,
                $"spr_sel_pvtmb_{rawPvId}.bin",
                new Texture(8, 4, TextureFormat.RGBA8),
                new[]
                {
                    new Sprite
                    {
                        Name = $"SPR_SEL_PVTMB_{rawPvId}",
                        TextureIndex = 0,
                        X = 0,
                        Y = 0,
                        Width = 8,
                        Height = 4
                    }
                });
        }

        private static void SaveArtworkArchive(
            string archivePath,
            string internalName,
            Texture texture,
            IEnumerable<Sprite> sprites)
        {
            var spriteSet = new SpriteSet();
            spriteSet.TextureSet.Textures.Add(texture);
            foreach (var sprite in sprites)
                spriteSet.Sprites.Add(sprite);

            using var spriteBytes = new MemoryStream();
            spriteSet.Save(spriteBytes, true);
            spriteBytes.Position = 0;
            using var archive = new FarcArchive();
            archive.Add(internalName, spriteBytes, true, ConflictPolicy.Replace);
            archive.Save(archivePath);
        }
    }
}
