using System.Diagnostics;
using System.Text;
using System.IO.Compression;
using Xunit;

namespace DivaModManager.Tests;

public sealed class SongEditServiceTests
{
    [Fact]
    public async Task UpdateMetadataPreservesUtf8BomCrlfAndUnrelatedContentAndCreatesBackup()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("Metadata Song");
        var originalText = String.Join("\r\n", new[]
        {
            "# preserve=this comment",
            "pv_0008.song_name=Old title",
            "pv_0008.song_name_en=Old=English=title",
            "pv_0008.song_name_reading=old-reading",
            "pv_0008.unknown_field=alpha=beta=gamma",
            "pv_0008.song_file_name=rom/sound/song/pv_0008.ogg",
            "pv_0008.difficulty.extreme.0.script_file_name=rom/script/pv_0008_extreme.dsc",
            "pv_00080.song_name=Prefix neighbor stays",
            "# final comment"
        }) + "\r\n";
        var databasePath = mod.WriteUtf8Bom("rom/mod_pv_db.txt", originalText);
        mod.Write("rom/sound/song/pv_0008.ogg");
        mod.Write("rom/script/pv_0008_extreme.dsc");
        mod.Write("rom/2d/spr_sel_pv0008.farc");
        var originalBytes = File.ReadAllBytes(databasePath);
        var song = SongCatalogService.ScanMods(workspace.ModsRoot).Single(entry => entry.PvId == 8);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.UpdateMetadataAsync(song, new SongMetadataUpdate
        {
            SongName = "新しい=歌",
            SongNameEnglish = "Updated=English",
            SongNameReading = "あたらしいうた"
        });

        Assert.True(result.Success, result.Message);
        Assert.False(String.IsNullOrWhiteSpace(result.BackupPath));
        Assert.True(Directory.Exists(result.BackupPath));
        Assert.True(File.Exists(Path.Combine(result.BackupPath!, "manifest.json")));

        var updatedBytes = File.ReadAllBytes(databasePath);
        var utf8Preamble = Encoding.UTF8.GetPreamble();
        Assert.True(updatedBytes.AsSpan(0, utf8Preamble.Length).SequenceEqual(utf8Preamble));
        var updatedText = new UTF8Encoding(false, true).GetString(
            updatedBytes,
            utf8Preamble.Length,
            updatedBytes.Length - utf8Preamble.Length);
        Assert.Equal(updatedText.Count(character => character == '\n'), updatedText.Count(character => character == '\r'));
        Assert.DoesNotContain("\n", updatedText.Replace("\r\n", String.Empty));
        Assert.Contains("pv_0008.song_name=新しい=歌\r\n", updatedText);
        Assert.Contains("pv_0008.song_name_en=Updated=English\r\n", updatedText);
        Assert.Contains("pv_0008.song_name_reading=あたらしいうた\r\n", updatedText);
        Assert.Contains("# preserve=this comment\r\n", updatedText);
        Assert.Contains("pv_0008.unknown_field=alpha=beta=gamma\r\n", updatedText);
        Assert.Contains("pv_00080.song_name=Prefix neighbor stays\r\n", updatedText);

        var backupDatabase = Path.Combine(result.BackupPath!, "content", "rom", "mod_pv_db.txt");
        Assert.Equal(originalBytes, File.ReadAllBytes(backupDatabase));

        var rescanned = SongCatalogService.ScanMods(workspace.ModsRoot).Single(entry => entry.PvId == 8);
        Assert.Equal("新しい=歌", rescanned.SongName);
        Assert.Equal("Updated=English", rescanned.SongNameEnglish);
        Assert.Equal("あたらしいうた", rescanned.SongNameReading);
    }

    [Fact]
    public async Task UpdateMetadataRejectsDatabaseChangedAfterScanWithoutOverwritingIt()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("Concurrent Edit");
        var databasePath = mod.Write(
            "rom/mod_pv_db.txt",
            "pv_41.song_name=Scanned title\n" +
            "pv_41.song_file_name=rom/sound/song/pv_41.ogg\n" +
            "pv_41.difficulty.extreme.length=1\n" +
            "pv_41.difficulty.extreme.0.script_file_name=rom/script/pv_41_extreme.dsc\n");
        mod.Write("rom/sound/song/pv_41.ogg");
        mod.Write("rom/script/pv_41_extreme.dsc");
        mod.Write("rom/2d/spr_sel_pv41.farc");
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        File.AppendAllText(databasePath, "# external edit=must survive\n", new UTF8Encoding(false));
        var externallyModifiedBytes = File.ReadAllBytes(databasePath);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.UpdateMetadataAsync(song, new SongMetadataUpdate
        {
            SongName = "Must not be written",
            SongNameEnglish = "",
            SongNameReading = ""
        });

        Assert.False(result.Success);
        Assert.Contains("修改", result.Message);
        Assert.Contains("刷新", result.Message);
        Assert.Equal(externallyModifiedBytes, File.ReadAllBytes(databasePath));
        Assert.False(Directory.Exists(workspace.BackupRoot));
    }

    [Fact]
    public async Task UpdateMetadataRejectsDatabaseFileSymbolicLink()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("Linked database");
        var externalDatabase = workspace.WriteRootBytes(
            "outside/linked-mod_pv_db.txt",
            Encoding.UTF8.GetBytes(
                "pv_40.song_name=Linked title\n" +
                "pv_40.song_file_name=rom/sound/song/pv_40.ogg\n" +
                "pv_40.difficulty.extreme.0.script_file_name=rom/script/pv_40_extreme.dsc\n"));
        mod.Write("rom/sound/song/pv_40.ogg");
        mod.Write("rom/script/pv_40_extreme.dsc");
        var databaseLink = Path.Combine(mod.Root, "rom", "mod_pv_db.txt");
        if (!TryCreateFileSymbolicLink(databaseLink, externalDatabase))
            return;
        var originalBytes = File.ReadAllBytes(externalDatabase);
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.UpdateMetadataAsync(song, new SongMetadataUpdate
        {
            SongName = "Must not follow link",
            SongNameEnglish = String.Empty,
            SongNameReading = String.Empty
        });

        Assert.False(result.Success);
        Assert.Contains("符号链接", result.Message);
        Assert.Equal(originalBytes, File.ReadAllBytes(externalDatabase));
        Assert.False(Directory.Exists(workspace.BackupRoot));
    }

    [Fact]
    public async Task UpdateMetadataUpdatesEveryRawAliasAndInsertsMissingFieldsOnPrimaryAlias()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("Aliased metadata");
        var databasePath = mod.Write(
            "rom/mod_pv_db.txt",
            "pv_0008.song_name=Old padded title\n" +
            "pv_0008.song_file_name=rom/sound/song/pv_0008.ogg\n" +
            "pv_8.song_name=Old plain title\n" +
            "pv_8.difficulty.extreme.0.script_file_name=rom/script/pv_8_extreme.dsc\n");
        mod.Write("rom/sound/song/pv_0008.ogg");
        mod.Write("rom/script/pv_8_extreme.dsc");
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        Assert.Equal("0008", song.RawPvId);
        Assert.Equal(new[] { "0008", "8" }, song.RawPvIds);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.UpdateMetadataAsync(song, new SongMetadataUpdate
        {
            SongName = "Unified title",
            SongNameEnglish = "Unified English",
            SongNameReading = "unified-reading"
        });

        Assert.True(result.Success, result.Message);
        var updated = File.ReadAllText(databasePath);
        Assert.Contains("pv_0008.song_name=Unified title", updated);
        Assert.Contains("pv_8.song_name=Unified title", updated);
        Assert.Equal(2, updated.Split('\n').Count(line => line.EndsWith("song_name=Unified title", StringComparison.Ordinal)));
        Assert.Contains("pv_0008.song_name_en=Unified English", updated);
        Assert.Contains("pv_0008.song_name_reading=unified-reading", updated);
        Assert.DoesNotContain("pv_8.song_name_en", updated);
        Assert.DoesNotContain("pv_8.song_name_reading", updated);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(9001, false)]
    public async Task UpdateMetadataRejectsSongPatchWithoutChangingDatabaseOrCreatingBackup(
        int pvId,
        bool isOfficialPvId)
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod(isOfficialPvId ? "Official song patch" : "Custom song patch");
        var databasePath = mod.Write(
            "rom/mod_pv_db.txt",
            $"pv_{pvId}.song_name=Protected patch\n" +
            $"pv_{pvId}.song_file_name=rom/sound/song/pv_{pvId}.ogg\n" +
            $"pv_{pvId}.difficulty.extreme.0.script_file_name=rom/script/pv_{pvId}_extreme.dsc\n");
        mod.Write($"rom/sound/song/pv_{pvId}.ogg", "audio");
        mod.Write($"rom/script/pv_{pvId}_extreme.dsc", "chart");
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        song.IsSongPatch = true;
        Assert.Equal(isOfficialPvId, song.IsMega39PlusOfficialPvId);
        var originalDatabase = File.ReadAllBytes(databasePath);
        var originalMod = Snapshot(mod.Root);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.UpdateMetadataAsync(song, new SongMetadataUpdate
        {
            SongName = "Must not be written",
            SongNameEnglish = "Must not be written",
            SongNameReading = "must-not-be-written"
        });

        Assert.False(result.Success);
        Assert.Contains("歌曲补丁", result.Message);
        Assert.Contains("不能修改歌名", result.Message);
        Assert.Null(result.BackupPath);
        Assert.Equal(originalDatabase, File.ReadAllBytes(databasePath));
        AssertSnapshotUnchanged(mod.Root, originalMod);
        Assert.False(Directory.Exists(workspace.BackupRoot));
    }

    [Fact]
    public async Task DeleteSongRemovesExactLegacyAndNewClassicsIdAndOnlyExclusiveAssets()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("Mixed Delete");
        var legacyText = String.Join("\n", new[]
        {
            "# legacy header stays",
            "pv_012.song_name=Delete target",
            "pv_012.song_file_name=rom/sound/song/pv_012.ogg",
            "pv_012.movie_file_name=rom/movie/shared.usm",
            "pv_012.difficulty.extreme.0.script_file_name=rom/script/pv_012_extreme.dsc",
            "pv_0123.song_name=Keep neighbor",
            "pv_0123.song_file_name=rom/sound/song/pv_0123.ogg",
            "pv_0123.movie_file_name=rom/movie/shared.usm",
            "pv_0123.difficulty.extreme.0.script_file_name=rom/script/pv_0123_extreme.dsc",
            "# legacy footer stays"
        }) + "\n";
        var newClassicsText = String.Join("\n", new[]
        {
            "# nc header stays",
            "[[songs]]",
            "id = 12",
            "marker = \"delete exact block\"",
            "[[songs.extreme]]",
            "style = \"MIXED\"",
            "script_file_name = \"rom/script_nc/pv_12_extreme.dsc\"",
            "level = \"PV_LV_09_0\"",
            "[[songs]]",
            "id = 123",
            "marker = \"keep exact block\"",
            "[[songs.extreme]]",
            "style = \"MIXED\"",
            "script_file_name = \"rom/script_nc/pv_123_extreme.dsc\"",
            "level = \"PV_LV_08_0\"",
            "# nc footer stays"
        }) + "\n";
        var legacyPath = mod.Write("rom/mod_pv_db.txt", legacyText);
        var newClassicsPath = mod.Write("rom/nc_db.toml", newClassicsText);
        var targetAudio = mod.Write("rom/sound/song/pv_012.ogg", "target audio");
        var neighborAudio = mod.Write("rom/sound/song/pv_0123.ogg", "neighbor audio");
        var sharedVideo = mod.Write("rom/movie/shared.usm", "shared video");
        var targetLegacyChart = mod.Write("rom/script/pv_012_extreme.dsc", "target legacy chart");
        var neighborLegacyChart = mod.Write("rom/script/pv_0123_extreme.dsc", "neighbor legacy chart");
        var targetNcChart = mod.Write("rom/script_nc/pv_12_extreme.dsc", "target nc chart");
        var neighborNcChart = mod.Write("rom/script_nc/pv_123_extreme.dsc", "neighbor nc chart");
        var targetArtwork = mod.Write("rom/2d/spr_sel_pv012.farc", "target artwork");
        var neighborArtwork = mod.Write("rom/2d/spr_sel_pv0123.farc", "neighbor artwork");
        var songs = SongCatalogService.ScanMods(workspace.ModsRoot);
        var target = songs.Single(entry => entry.PvId == 12);
        Assert.Equal(SongFormat.LegacyWithNewClassics, target.Format);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.DeleteSongAsync(target);

        Assert.True(result.Success, result.Message);
        Assert.False(String.IsNullOrWhiteSpace(result.BackupPath));
        Assert.True(File.Exists(Path.Combine(result.BackupPath!, "manifest.json")));

        var remainingSongs = SongCatalogService.ScanMods(workspace.ModsRoot);
        var remaining = Assert.Single(remainingSongs);
        Assert.Equal(123, remaining.PvId);
        Assert.Equal("Keep neighbor", remaining.SongName);

        var updatedLegacy = File.ReadAllText(legacyPath);
        var updatedNewClassics = File.ReadAllText(newClassicsPath);
        Assert.DoesNotContain("pv_012.", updatedLegacy);
        Assert.Contains("pv_0123.song_name=Keep neighbor", updatedLegacy);
        Assert.DoesNotContain("delete exact block", updatedNewClassics);
        Assert.Contains("keep exact block", updatedNewClassics);
        Assert.Contains("# legacy header stays", updatedLegacy);
        Assert.Contains("# nc header stays", updatedNewClassics);

        Assert.False(File.Exists(targetAudio));
        Assert.False(File.Exists(targetLegacyChart));
        Assert.False(File.Exists(targetNcChart));
        Assert.False(File.Exists(targetArtwork));
        Assert.True(File.Exists(sharedVideo));
        Assert.True(File.Exists(neighborAudio));
        Assert.True(File.Exists(neighborLegacyChart));
        Assert.True(File.Exists(neighborNcChart));
        Assert.True(File.Exists(neighborArtwork));

        Assert.True(File.Exists(BackupContentPath(result, "rom/mod_pv_db.txt")));
        Assert.True(File.Exists(BackupContentPath(result, "rom/nc_db.toml")));
        Assert.Equal("target audio", File.ReadAllText(BackupContentPath(result, "rom/sound/song/pv_012.ogg")));
        Assert.Equal("target artwork", File.ReadAllText(BackupContentPath(result, "rom/2d/spr_sel_pv012.farc")));
        Assert.False(File.Exists(BackupContentPath(result, "rom/movie/shared.usm")));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(9001, false)]
    public async Task DeleteSongRejectsSongPatchWithoutChangingDatabaseOrCreatingBackup(
        int pvId,
        bool isOfficialPvId)
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod(isOfficialPvId ? "Official song patch" : "Custom song patch");
        var databasePath = mod.Write(
            "rom/mod_pv_db.txt",
            $"pv_{pvId}.song_name=Protected patch\n" +
            $"pv_{pvId}.song_file_name=rom/sound/song/pv_{pvId}.ogg\n" +
            $"pv_{pvId}.difficulty.extreme.0.script_file_name=rom/script/pv_{pvId}_extreme.dsc\n");
        mod.Write($"rom/sound/song/pv_{pvId}.ogg", "audio");
        mod.Write($"rom/script/pv_{pvId}_extreme.dsc", "chart");
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        song.IsSongPatch = true;
        Assert.Equal(isOfficialPvId, song.IsMega39PlusOfficialPvId);
        var originalDatabase = File.ReadAllBytes(databasePath);
        var originalMod = Snapshot(mod.Root);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.DeleteSongAsync(song);

        Assert.False(result.Success);
        Assert.Contains("歌曲补丁", result.Message);
        Assert.Contains("不能作为独立歌曲删除", result.Message);
        Assert.Null(result.BackupPath);
        Assert.Equal(originalDatabase, File.ReadAllBytes(databasePath));
        AssertSnapshotUnchanged(mod.Root, originalMod);
        Assert.False(Directory.Exists(workspace.BackupRoot));
    }

    [Fact]
    public async Task DeleteSongRemovesExplicitAssetsButPreservesGenericSharedReferences()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("Explicit Assets");
        mod.Write(
            "rom/mod_pv_db.txt",
            "pv_12.song_name=Delete target\n" +
            "pv_12.song_file_name=rom/sound/song/pv_12.ogg\n" +
            "pv_12.movie_list.0.name=rom/movie/pv_12_alt.usm\n" +
            "pv_12.pv_expression.file_name=rom/pv_expression/exp_PV12.bin\n" +
            "pv_12.difficulty.extreme.length=1\n" +
            "pv_12.difficulty.extreme.0.script_file_name=rom/script/pv_12_extreme.dsc\n" +
            "pv_12.effect_se_file_name=rom/sound/shared_effect.farc\n" +
            "pv_13.song_name=Keep neighbor\n" +
            "pv_13.song_file_name=rom/sound/song/pv_13.ogg\n" +
            "pv_13.effect_se_file_name=rom/sound/shared_effect.farc\n");
        var targetAudio = mod.Write("rom/sound/song/pv_12.ogg", "target audio");
        var neighborAudio = mod.Write("rom/sound/song/pv_13.ogg", "neighbor audio");
        var targetMovie = mod.Write("rom/movie/pv_12_alt.usm", "target movie");
        var targetExpression = mod.Write("rom/pv_expression/exp_PV12.bin", "target expression");
        var targetChart = mod.Write("rom/script/pv_12_extreme.dsc", "target chart");
        var sharedEffect = mod.Write("rom/sound/shared_effect.farc", "shared effect");
        var target = SongCatalogService.ScanMods(workspace.ModsRoot)
            .Single(song => song.PvId == 12);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.DeleteSongAsync(target);

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(targetAudio));
        Assert.False(File.Exists(targetMovie));
        Assert.False(File.Exists(targetExpression));
        Assert.False(File.Exists(targetChart));
        Assert.True(File.Exists(sharedEffect));
        Assert.True(File.Exists(neighborAudio));
        Assert.Equal("target movie", File.ReadAllText(BackupContentPath(result, "rom/movie/pv_12_alt.usm")));
        Assert.Equal("target expression", File.ReadAllText(BackupContentPath(result, "rom/pv_expression/exp_PV12.bin")));
        Assert.Equal("target chart", File.ReadAllText(BackupContentPath(result, "rom/script/pv_12_extreme.dsc")));
        Assert.False(File.Exists(BackupContentPath(result, "rom/sound/shared_effect.farc")));
    }

    [Fact]
    public async Task DeleteSongRemovesEveryRawAliasForNumericPvId()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("Aliased delete");
        var databasePath = mod.Write(
            "rom/mod_pv_db.txt",
            "pv_0022.song_name=Aliased song\n" +
            "pv_0022.song_file_name=rom/sound/song/pv_0022.ogg\n" +
            "pv_22.difficulty.extreme.0.script_file_name=rom/script/pv_22_extreme.dsc\n" +
            "pv_220.song_name=Neighbor stays\n");
        var audioPath = mod.Write("rom/sound/song/pv_0022.ogg");
        var chartPath = mod.Write("rom/script/pv_22_extreme.dsc");
        var paddedArtwork = mod.Write("rom/2d/spr_sel_pv0022.farc");
        var plainArtwork = mod.Write("rom/2d/spr_sel_pv22.farc");
        var song = SongCatalogService.ScanMods(workspace.ModsRoot).Single(entry => entry.PvId == 22);
        Assert.Equal(2, song.RawPvIds.Count);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.DeleteSongAsync(song);

        Assert.True(result.Success, result.Message);
        var updated = File.ReadAllText(databasePath);
        Assert.DoesNotContain("pv_0022.", updated);
        Assert.DoesNotContain("pv_22.", updated);
        Assert.Contains("pv_220.song_name=Neighbor stays", updated);
        Assert.False(File.Exists(audioPath));
        Assert.False(File.Exists(chartPath));
        Assert.False(File.Exists(paddedArtwork));
        Assert.False(File.Exists(plainArtwork));
    }

    [Fact]
    public async Task DeleteKeepsAllAssetsWhenEditingScanIsIncomplete()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("Incomplete reverse index");
        mod.Write(
            "config.toml",
            "enabled = true\nname = \"Incomplete reverse index\"\ninclude = [\".\", \"../outside\"]\n");
        var databasePath = mod.Write(
            "rom/mod_pv_db.txt",
            "pv_23.song_name=Keep resources\n" +
            "pv_23.song_file_name=rom/sound/song/pv_23.ogg\n" +
            "pv_23.difficulty.extreme.0.script_file_name=rom/script/pv_23_extreme.dsc\n");
        var audioPath = mod.Write("rom/sound/song/pv_23.ogg", "audio");
        var chartPath = mod.Write("rom/script/pv_23_extreme.dsc", "chart");
        var artworkPath = mod.Write("rom/2d/spr_sel_pv23.farc", "artwork");
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.DeleteSongAsync(song);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("pv_23.", File.ReadAllText(databasePath));
        Assert.True(File.Exists(audioPath));
        Assert.True(File.Exists(chartPath));
        Assert.True(File.Exists(artworkPath));
        Assert.Contains("0 个独占资源", result.Message);
    }

    [Fact]
    public async Task DeleteAdditionalDifficultyIsRejectedAsSongPatch()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("NC-only Delete");
        var ncPath = mod.Write(
            "rom/nc_db.toml",
            "[[songs]]\n" +
            "id = 9887\n\n" +
            "[[songs.extreme]]\n" +
            "style = \"CONSOLE\"\n" +
            "script_file_name = \"rom/script_nc/pv9887/pv_9887_extreme.dsc\"\n");
        var scriptPath = mod.Write("rom/script_nc/pv9887/pv_9887_extreme.dsc", "chart");
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        Assert.True(song.IsSongPatch);
        var originalDatabase = File.ReadAllBytes(ncPath);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.DeleteSongAsync(song);

        Assert.False(result.Success);
        Assert.Contains("歌曲补丁", result.Message);
        Assert.Equal(originalDatabase, File.ReadAllBytes(ncPath));
        Assert.True(File.Exists(scriptPath));
        Assert.False(Directory.Exists(workspace.BackupRoot));
    }

    [Fact]
    public async Task DeleteRejectsNewClassicsDatabaseChangedAfterScan()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("NC stale");
        var ncPath = mod.Write(
            "rom/nc_db.toml",
            "[[songs]]\nid = 7001\n[[songs.extreme]]\nstyle = \"CONSOLE\"\n" +
            "script_file_name = \"rom/script_nc/pv_7001_extreme.dsc\"\n");
        mod.Write(
            "rom/mod_pv_db.txt",
            "pv_7001.song_name=NC stale\n" +
            "pv_7001.song_file_name=rom/sound/song/pv_7001.ogg\n" +
            "pv_7001.difficulty.extreme.length=1\n" +
            "pv_7001.difficulty.extreme.0.script_file_name=rom/script/pv_7001_extreme.dsc\n");
        mod.Write("rom/sound/song/pv_7001.ogg", "audio");
        mod.Write("rom/script/pv_7001_extreme.dsc", "legacy chart");
        var scriptPath = mod.Write("rom/script_nc/pv_7001_extreme.dsc", "chart");
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        Assert.False(song.IsSongPatch);
        File.AppendAllText(ncPath, "# external change\n");
        var changedBytes = File.ReadAllBytes(ncPath);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.DeleteSongAsync(song);

        Assert.False(result.Success);
        Assert.Contains("其他程序修改", result.Message);
        Assert.Equal(changedBytes, File.ReadAllBytes(ncPath));
        Assert.True(File.Exists(scriptPath));
    }

    [Fact]
    public async Task BackupCreationFailureDoesNotRewriteSongDatabase()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("Backup failure");
        var databasePath = mod.Write(
            "rom/mod_pv_db.txt",
            "pv_702.song_name=Keep me\n" +
            "pv_702.song_file_name=rom/sound/song/pv_702.ogg\n");
        mod.Write("rom/sound/song/pv_702.ogg", "audio");
        var originalBytes = File.ReadAllBytes(databasePath);
        var knownTimestamp = new DateTime(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(databasePath, knownTimestamp);
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        var backupRootFile = workspace.WriteRootBytes("not-a-backup-directory", new byte[] { 1, 2, 3 });
        var service = new SongEditService(backupRootFile);

        var result = await service.DeleteSongAsync(song);

        Assert.False(result.Success);
        Assert.Equal(originalBytes, File.ReadAllBytes(databasePath));
        Assert.Equal(knownTimestamp, File.GetLastWriteTimeUtc(databasePath));
    }

    [Fact]
    public async Task ReplaceArtworkRejectsFarcForDifferentPvWithoutChangingExistingFile()
    {
        using var workspace = new TemporaryWorkspace();
        var mod = workspace.CreateMod("Artwork Song");
        mod.Write(
            "rom/mod_pv_db.txt",
            "pv_042.song_name=Artwork target\n" +
            "pv_042.song_file_name=rom/sound/song/pv_042.ogg\n");
        mod.Write("rom/sound/song/pv_042.ogg");
        var artworkPath = mod.WriteBytes("rom/2d/spr_sel_pv042.farc", Encoding.ASCII.GetBytes("existing artwork bytes"));
        var originalArtwork = File.ReadAllBytes(artworkPath);
        var wrongFarc = workspace.WriteRootBytes(
            "prepared/spr_sel_pv043.farc",
            Encoding.ASCII.GetBytes("FArC0000-wrong-spr_sel_pv043.bin-resource"));
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ReplaceArtworkAsync(song, wrongFarc);

        Assert.False(result.Success);
        Assert.Contains("另一首歌曲", result.Message);
        Assert.Equal(originalArtwork, File.ReadAllBytes(artworkPath));
        Assert.False(Directory.Exists(workspace.BackupRoot));
    }

    [Fact]
    public async Task ImportCompleteMega39PlusFolderCreatesIndependentModAndLeavesSourceUntouched()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("Complete MM+ Song");
        package.Write(
            "config.toml",
            "enabled = true\nname = \"Imported MM+ Song\"\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_700.song_name=Imported title\n" +
            "pv_700.song_file_name=rom/sound/song/pv_700.ogg\n" +
            "pv_700.difficulty.extreme.0.script_file_name=rom/script/pv_700_extreme.dsc\n");
        package.Write("rom/sound/song/pv_700.ogg", "source audio");
        package.Write("rom/script/pv_700_extreme.dsc", "source chart");
        package.Write("rom/2d/spr_sel_pv700.farc", "source artwork");
        package.Write("preview.png", "source preview");
        var sourceSnapshot = Snapshot(package.Root);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.True(result.Success, result.Message);
        Assert.False(String.IsNullOrWhiteSpace(result.InstalledModPath));
        Assert.True(Directory.Exists(result.InstalledModPath));
        Assert.StartsWith(
            Path.GetFullPath(workspace.ModsRoot) + Path.DirectorySeparatorChar,
            Path.GetFullPath(result.InstalledModPath!),
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Path.GetFullPath(package.Root), Path.GetFullPath(result.InstalledModPath!));

        AssertSnapshotUnchanged(package.Root, sourceSnapshot);
        AssertSnapshotUnchanged(result.InstalledModPath!, sourceSnapshot);
        var imported = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        Assert.Equal(700, imported.PvId);
        Assert.Equal("Imported title", imported.SongName);
        Assert.Equal("Imported MM+ Song", imported.ModName);
    }

    [Fact]
    public async Task ImportIgnoresDifficultySlotsOutsideDeclaredLength()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("inactive-difficulty-template");
        package.Write("config.toml", "enabled = true\nname = \"Inactive template import\"\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_8808.song_name=Inactive difficulty template\n" +
            "pv_8808.song_file_name=rom/sound/song/pv_8808.ogg\n" +
            "pv_8808.difficulty.easy.length=0\n" +
            "pv_8808.difficulty.easy.0.level=PV_LV_03_0\n" +
            "pv_8808.difficulty.easy.0.script_file_name=rom/script/pv_8808_easy_missing.dsc\n" +
            "pv_8808.difficulty.hard.length=1\n" +
            "pv_8808.difficulty.hard.0.level=PV_LV_06_0\n" +
            "pv_8808.difficulty.hard.0.script_file_name=rom/script/pv_8808_hard.dsc\n");
        package.Write("rom/sound/song/pv_8808.ogg");
        package.Write("rom/script/pv_8808_hard.dsc");
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.True(result.Success, result.Message);
        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(workspace.ModsRoot));
        var difficulty = Assert.Single(song.Difficulties);
        Assert.Equal("hard", difficulty.NormalizedName);
        Assert.True(difficulty.ScriptExists);
        Assert.DoesNotContain("pv_8808_easy_missing.dsc", song.WarningsDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportRejectsMissingDifficultySlotInsideDeclaredLength()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("missing-active-difficulty-slot");
        package.Write("config.toml", "enabled = true\nname = \"Missing active slot\"\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_8809.song_name=Missing active difficulty slot\n" +
            "pv_8809.song_file_name=rom/sound/song/pv_8809.ogg\n" +
            "pv_8809.difficulty.extreme.length=2\n" +
            "pv_8809.difficulty.extreme.0.level=PV_LV_08_0\n" +
            "pv_8809.difficulty.extreme.0.script_file_name=rom/script/pv_8809_extreme.dsc\n" +
            "pv_8809.difficulty.extreme.1.attribute.extra=1\n" +
            "pv_8809.difficulty.extreme.1.script_file_name=rom/script/pv_8809_extreme_1_missing.dsc\n");
        package.Write("rom/sound/song/pv_8809.ogg");
        package.Write("rom/script/pv_8809_extreme.dsc");
        var sourceSnapshot = Snapshot(package.Root);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.False(result.Success);
        Assert.Contains("8809", result.Message);
        Assert.Contains("谱面", result.Message);
        Assert.Empty(Directory.GetDirectories(workspace.ModsRoot));
        AssertSnapshotUnchanged(package.Root, sourceSnapshot);
    }

    [Fact]
    public async Task ImportZipWithFilesAtRootUsesConfiguredModName()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("zip-source");
        package.Write(
            "config.toml",
            "enabled = true\nname = \"Configured Zip Song\"\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_701.song_name=ZIP title\n" +
            "pv_701.song_file_name=rom/sound/song/pv_701.ogg\n" +
            "pv_701.difficulty.extreme.0.script_file_name=rom/script/pv_701_extreme.dsc\n");
        package.Write("rom/sound/song/pv_701.ogg", "audio");
        package.Write("rom/script/pv_701_extreme.dsc", "chart");
        var zipPath = Path.Combine(workspace.Root, "unhelpful-archive-name.zip");
        ZipFile.CreateFromDirectory(package.Root, zipPath, CompressionLevel.NoCompression, false);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(zipPath, workspace.ModsRoot);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Configured Zip Song", Path.GetFileName(result.InstalledModPath));
        Assert.True(File.Exists(Path.Combine(result.InstalledModPath!, "rom", "sound", "song", "pv_701.ogg")));
    }

    [Fact]
    public async Task ImportSevenZipArchiveWithWrapperDirectory()
    {
        const string archiveBase64 =
            "N3q8ryccAATG0tCJiAEAAAAAAAAjAAAAAAAAAHtV+wbgAN4Ak10AMpuIRupebfdJhKOXQ4e+gbS/JU89bk/FYNCQONzhe3xeiDSgqizyc+JvnndTAYx0b2qYePTK8e7J1g9PijWG41Hy80ivg0JZwKvyY9/UYmVHmPajV6rtIuNB2j1yWtpCebt/GTeDlUnf+bG3jjmx4nkcPek5ENXRt7SGMesLL9nUcv12HIRRYoDlab5a93kWK7jXAAAAgTMHrg/VMECb1mwpH0gdmrW3YqP+n1ZHZN5Uo7z0uIQX8oLMNdWCjIBLY2RnaKPDDZ3gAjXTsVVA10CPzyxVjGHoKfY2dJWIOGTuJdAMDFZ7Sks97HyKQQfILJ/nVPbnxTFp8Iv9psF9zRVz19yFs7YChNhkBysqOlteKhCWD4eXf3Aa+2fPbZ50J4MTqvXN3uOcG2eCUrlmozkRagce2EYd7rR10U4202XpHHOYKsZvOhpfbi6cUWSjU5wIc1Kcemur85PrudiZPcEJz7aLuTrgXFyT05broVYIs6Sxe/6FjNmHqjOjNLVEABcGgJsBCYDtAAcLAQABIwMBAQVdABAAAAyCogoBxMwOSgAA";
        using var workspace = new TemporaryWorkspace();
        var archivePath = workspace.WriteRootBytes(
            "sevenzip-song.7z",
            Convert.FromBase64String(archiveBase64));
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(archivePath, workspace.ModsRoot);

        Assert.True(result.Success, result.Message);
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        Assert.Equal(718, song.PvId);
        Assert.Equal("SevenZip song", song.SongName);
        Assert.True(song.AudioExists);
        Assert.True(Assert.Single(song.Difficulties).ScriptExists);
    }

    [Fact]
    public async Task ImportKeepsMultipleDifficultiesAsOneSongWithSharedMedia()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("shared-difficulty-media");
        package.Write(
            "config.toml",
            "enabled = true\nname = \"Shared difficulty media\"\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_711.song_name=One song, three charts\n" +
            "pv_711.song_file_name=rom/sound/song/pv_711.ogg\n" +
            "pv_711.another_song.0.song_file_name=rom/sound/song/pv_711_alt.ogg\n" +
            "pv_711.movie_file_name=rom/movie/pv_711.mp4\n" +
            "pv_711.movie_list.0.name=rom/movie/pv_711_alt.mp4\n" +
            "pv_711.difficulty.easy.0.script_file_name=rom/script/pv_711_easy.dsc\n" +
            "pv_711.difficulty.easy.0.movie_file_name=rom/movie/pv_711.mp4\n" +
            "pv_711.difficulty.hard.0.script_file_name=rom/script/pv_711_hard.dsc\n" +
            "pv_711.difficulty.extreme.0.script_file_name=rom/script/pv_711_extreme.dsc\n");
        package.Write("rom/sound/song/pv_711.ogg", "shared main audio");
        package.Write("rom/sound/song/pv_711_alt.ogg", "alternate vocal audio");
        package.Write("rom/movie/pv_711.usm", "shared video");
        package.Write("rom/movie/pv_711_alt.usm", "alternate video");
        package.Write("rom/script/pv_711_easy.dsc", "easy chart");
        package.Write("rom/script/pv_711_hard.dsc", "hard chart");
        package.Write("rom/script/pv_711_extreme.dsc", "extreme chart");
        package.Write("rom/2d/spr_sel_pv711.farc", "one shared cover");
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.True(result.Success, result.Message);
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        Assert.Equal(3, song.Difficulties.Count);
        Assert.All(song.Difficulties, difficulty => Assert.True(difficulty.ScriptExists));
        Assert.Single(song.AlternateAudioReferences);
        Assert.Single(song.AlternateAudioPaths);
        Assert.EndsWith("pv_711.usm", song.VideoPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(song.ArtworkPath));
        Assert.False(song.CoverExists);
        Assert.Equal(SongRunStatus.Warning, song.RunStatus);
        Assert.Contains("缺少歌曲图片", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            song.ExplicitAssetPaths,
            path => path.EndsWith("pv_711_alt.usm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportRetainsDifferentPvIdsWithTheSameSongNameAndSharedVideo()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("same-title-different-ids");
        package.Write("config.toml", "enabled = true\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_711.song_name=Duplicate title\n" +
            "pv_711.song_file_name=rom/sound/song/pv_711.ogg\n" +
            "pv_711.movie_file_name=rom/movie/shared.usm\n" +
            "pv_711.difficulty.extreme.0.script_file_name=rom/script/pv_711_extreme.dsc\n" +
            "pv_712.song_name=Duplicate title\n" +
            "pv_712.song_file_name=rom/sound/song/pv_712.ogg\n" +
            "pv_712.movie_file_name=rom/movie/shared.usm\n" +
            "pv_712.difficulty.extreme.0.script_file_name=rom/script/pv_712_extreme.dsc\n");
        package.Write("rom/sound/song/pv_711.ogg");
        package.Write("rom/sound/song/pv_712.ogg");
        package.Write("rom/movie/shared.usm");
        package.Write("rom/script/pv_711_extreme.dsc");
        package.Write("rom/script/pv_712_extreme.dsc");
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.True(result.Success, result.Message);
        var songs = SongCatalogService.ScanMods(workspace.ModsRoot);
        Assert.Equal(2, songs.Count);
        Assert.Equal(new[] { 711, 712 }, songs.Select(song => song.PvId).OrderBy(id => id));
        Assert.All(songs, song => Assert.Equal("Duplicate title", song.SongName));
        Assert.Single(songs.Select(song => song.VideoPath).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportHonorsWrapperIncludeDirectory()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("wrapper-package");
        package.Write(
            "config.toml",
            "enabled = true\nname = \"Wrapper import\"\ninclude = [\"./payload/./\"]\n");
        package.Write(
            "payload/rom/mod_pv_db.txt",
            "pv_713.song_name=Wrapped song\n" +
            "pv_713.song_file_name=rom/sound/song/pv_713.ogg\n" +
            "pv_713.difficulty.extreme.0.script_file_name=rom/script/pv_713_extreme.dsc\n");
        package.Write("payload/rom/sound/song/pv_713.ogg");
        package.Write("payload/rom/script/pv_713_extreme.dsc");
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.True(result.Success, result.Message);
        var song = Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot));
        Assert.Equal(713, song.PvId);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(result.InstalledModPath!, "payload")),
            song.ContentRoot);
    }

    [Fact]
    public async Task ImportRejectsInstalledPvIdConflictWithoutCopyingPackage()
    {
        using var workspace = new TemporaryWorkspace();
        var installed = workspace.CreateMod("Already installed");
        installed.Write(
            "rom/mod_pv_db.txt",
            "pv_714.song_name=Installed\n" +
            "pv_714.song_file_name=rom/sound/song/pv_714.ogg\n" +
            "pv_714.difficulty.extreme.0.script_file_name=rom/script/pv_714_extreme.dsc\n");
        installed.Write("rom/sound/song/pv_714.ogg");
        installed.Write("rom/script/pv_714_extreme.dsc");

        var package = workspace.CreateSourcePackage("conflicting-package");
        package.Write("config.toml", "enabled = true\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_714.song_name=Conflicting import\n" +
            "pv_714.song_file_name=rom/sound/song/imported_714.ogg\n" +
            "pv_714.difficulty.extreme.0.script_file_name=rom/script/imported_714.dsc\n");
        package.Write("rom/sound/song/imported_714.ogg");
        package.Write("rom/script/imported_714.dsc");
        var installedDirectories = Directory.GetDirectories(workspace.ModsRoot);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.False(result.Success);
        Assert.Contains("PV ID", result.Message);
        Assert.Contains("714", result.Message);
        Assert.Equal(installedDirectories, Directory.GetDirectories(workspace.ModsRoot));
        Assert.Equal("Installed", Assert.Single(SongCatalogService.ScanMods(workspace.ModsRoot)).SongName);
    }

    [Fact]
    public async Task ImportAllowsBaseSongWhenOnlyAdditionalDifficultyUsesItsPvId()
    {
        using var workspace = new TemporaryWorkspace();
        var additional = workspace.CreateMod("Additional difficulty");
        additional.Write(
            "rom/nc_db.toml",
            "[[songs]]\n" +
            "id = 721\n" +
            "[[songs.extreme]]\n" +
            "script_file_name = \"rom/script_nc/pv_721_extreme.dsc\"\n");
        additional.Write("rom/script_nc/pv_721_extreme.dsc");

        var package = workspace.CreateSourcePackage("base-song-package");
        package.Write("config.toml", "enabled = true\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_721.song_name=Base song\n" +
            "pv_721.song_file_name=rom/sound/song/pv_721.ogg\n" +
            "pv_721.difficulty.hard.0.script_file_name=rom/script/pv_721_hard.dsc\n");
        package.Write("rom/sound/song/pv_721.ogg");
        package.Write("rom/script/pv_721_hard.dsc");
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.True(result.Success, result.Message);
        var songs = SongCatalogService.ScanMods(workspace.ModsRoot);
        Assert.Equal(2, songs.Count(song => song.PvId == 721));
        Assert.Contains(songs, song => song.PvId == 721 && song.Format == SongFormat.AdditionalDifficulty);
        Assert.Contains(songs, song => song.PvId == 721 && song.SongName == "Base song");
    }

    [Fact]
    public async Task ImportAllowsPackageWhenOptionalAlternateVocalIsMissing()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("optional-alternate-vocal-package");
        package.Write("config.toml", "enabled = true\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_715.song_name=Complete first song\n" +
            "pv_715.song_file_name=rom/sound/song/pv_715.ogg\n" +
            "pv_715.difficulty.extreme.0.script_file_name=rom/script/pv_715_extreme.dsc\n" +
            "pv_716.song_name=Broken second song\n" +
            "pv_716.song_file_name=rom/sound/song/pv_716.ogg\n" +
            "pv_716.another_song.0.song_file_name=rom/sound/song/pv_716_missing_vocal.ogg\n" +
            "pv_716.difficulty.extreme.0.script_file_name=rom/script/pv_716_extreme.dsc\n");
        package.Write("rom/sound/song/pv_715.ogg");
        package.Write("rom/script/pv_715_extreme.dsc");
        package.Write("rom/sound/song/pv_716.ogg");
        package.Write("rom/script/pv_716_extreme.dsc");
        var sourceSnapshot = Snapshot(package.Root);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.True(result.Success, result.Message);
        var songs = SongCatalogService.ScanModsWithoutArtwork(workspace.ModsRoot);
        Assert.Equal(2, songs.Count);
        var song = songs.Single(entry => entry.PvId == 716);
        Assert.Equal(SongRunStatus.Warning, song.RunStatus);
        Assert.Contains("pv_716_missing_vocal.ogg", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("可选演唱版本", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("音频", song.RunStatusReasonsDisplay, StringComparison.OrdinalIgnoreCase);
        AssertSnapshotUnchanged(package.Root, sourceSnapshot);
    }

    [Fact]
    public async Task ImportRejectsMissingMovieListVideo()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("missing-movie-list-video");
        package.Write("config.toml", "enabled = true\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_717.song_name=Missing secondary video\n" +
            "pv_717.song_file_name=rom/sound/song/pv_717.ogg\n" +
            "pv_717.movie_list.0.name=rom/movie/pv_717_missing.mp4\n" +
            "pv_717.difficulty.extreme.0.script_file_name=rom/script/pv_717_extreme.dsc\n");
        package.Write("rom/sound/song/pv_717.ogg");
        package.Write("rom/script/pv_717_extreme.dsc");
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.False(result.Success);
        Assert.Contains("pv_717_missing.mp4", result.Message);
        Assert.Contains("视频", result.Message);
        Assert.Empty(Directory.GetDirectories(workspace.ModsRoot));
    }

    [Fact]
    public async Task ImportRejectsNewClassicsAdditionalDifficultyWithoutChart()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("empty-additional-difficulty");
        package.Write("config.toml", "enabled = true\ninclude = [\".\"]\n");
        package.Write("rom/nc_db.toml", "[[songs]]\nid = 7200\n");
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.False(result.Success);
        Assert.Contains("追加难度谱面", result.Message);
        Assert.Empty(Directory.GetDirectories(workspace.ModsRoot));
    }

    [Fact]
    public async Task ImportAllowsDeclaredNewClassicsOfficialExtension()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("official-new-classics-extension");
        package.Write("config.toml", "enabled = true\ninclude = [\".\"]\n");
        package.Write(
            "rom/nc_db.toml",
            "[[songs]]\n" +
            "id = 8\n" +
            "[[songs.extreme]]\n" +
            "style = \"CONSOLE\"\n" +
            "script_file_name = \"rom/script_nc/pv_008_extreme.dsc\"\n");
        package.Write("rom/script_nc/pv_008_extreme.dsc");
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.True(result.Success, result.Message);
        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(workspace.ModsRoot));
        Assert.Equal(8, song.PvId);
        Assert.Equal(SongFormat.AdditionalDifficulty, song.Format);
        Assert.True(Assert.Single(song.Difficulties).IsDeclaredByNewClassicsDatabase);
        Assert.Equal(SongRunStatus.Ready, song.RunStatus);
    }

    [Theory]
    [InlineData(83)]
    [InlineData(93)]
    [InlineData(95)]
    [InlineData(276)]
    [InlineData(434)]
    [InlineData(623)]
    public async Task ImportAllowsSignedEdenCoreExtraExtremeForKnownOfficialPv(int pvId)
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage($"eden-core-extra-extreme-{pvId}");
        WriteSignedEdenCorePackage(package, pvId);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.True(result.Success, result.Message);
        var song = Assert.Single(SongCatalogService.ScanModsWithoutArtwork(workspace.ModsRoot));
        Assert.Equal(pvId, song.PvId);
        Assert.True(song.IsEdenProjectCore);
        Assert.False(song.HasInvalidOfficialChartOverride);
        Assert.Equal("Eden Extra Extreme Extension", song.FormatDisplayName);
        var extraExtreme = Assert.Single(song.Difficulties, difficulty => difficulty.Index == 1);
        Assert.True(extraExtreme.IsExtra);
        Assert.True(extraExtreme.IsOfficialLegacyExtension);
        Assert.True(extraExtreme.ScriptExists);
    }

    [Fact]
    public async Task ImportRejectsOrdinaryLegacyChartForOfficialPvEvenWhenPvIsOnEdenWhitelist()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("ordinary-official-legacy-chart");
        package.Write("config.toml", "enabled = true\nname = \"Ordinary Legacy Mod\"\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_083.song_name=Ordinary official override\n" +
            "pv_083.song_file_name=rom/sound/song/pv_083.ogg\n" +
            "pv_083.difficulty.extreme.length=1\n" +
            "pv_083.difficulty.extreme.0.level=PV_LV_09_0\n" +
            "pv_083.difficulty.extreme.0.script_file_name=rom/script/pv_083_extreme.dsc\n");
        package.Write("rom/sound/song/pv_083.ogg");
        package.WriteBytes("rom/script/pv_083_extreme.dsc", BitConverter.GetBytes(0x14050921));
        var sourceSnapshot = Snapshot(package.Root);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.False(result.Success);
        Assert.Contains("83", result.Message);
        Assert.Contains("Legacy", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(workspace.ModsRoot));
        AssertSnapshotUnchanged(package.Root, sourceSnapshot);
    }

    [Fact]
    public async Task ImportRejectsSignedEdenCoreExtraExtremeOutsideKnownOfficialPvSet()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("eden-core-unrecognized-extra-extreme");
        WriteSignedEdenCorePackage(package, 84);
        var sourceSnapshot = Snapshot(package.Root);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.False(result.Success);
        Assert.Contains("84", result.Message);
        Assert.Empty(Directory.GetDirectories(workspace.ModsRoot));
        AssertSnapshotUnchanged(package.Root, sourceSnapshot);
    }

    [Fact]
    public async Task ImportRejectsUnregisteredChartBesideDeclaredOfficialExtension()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("official-extension-with-stray-chart");
        package.Write("config.toml", "enabled = true\ninclude = [\".\"]\n");
        package.Write(
            "rom/nc_db.toml",
            "[[songs]]\n" +
            "id = 8\n" +
            "[[songs.extreme]]\n" +
            "style = \"CONSOLE\"\n" +
            "script_file_name = \"rom/script_nc/pv_008_extreme.dsc\"\n");
        package.Write("rom/script_nc/pv_008_extreme.dsc");
        package.Write("rom/script_nc/pv_008_hard.dsc");
        var sourceSnapshot = Snapshot(package.Root);
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.False(result.Success);
        Assert.Contains("nc_db.toml", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pv_008_hard.dsc", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(workspace.ModsRoot));
        AssertSnapshotUnchanged(package.Root, sourceSnapshot);
    }

    [Fact]
    public async Task ImportRejectsConfigWithAnyIncludeOutsidePackage()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("unsafe-include-package");
        package.Write(
            "config.toml",
            "enabled = true\ninclude = [\".\", \"../outside-content\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_720.song_name=Unsafe include\n" +
            "pv_720.song_file_name=rom/sound/song/pv_720.ogg\n" +
            "pv_720.difficulty.extreme.0.script_file_name=rom/script/pv_720_extreme.dsc\n");
        package.Write("rom/sound/song/pv_720.ogg");
        package.Write("rom/script/pv_720_extreme.dsc");
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

        Assert.False(result.Success);
        Assert.Contains("include", result.Message);
        Assert.Contains("越过歌曲包目录", result.Message);
        Assert.Empty(Directory.GetDirectories(workspace.ModsRoot));
    }

    [Fact]
    public async Task ImportArchiveRejectsPathTraversalWithoutWritingOutsideWorkspace()
    {
        using var workspace = new TemporaryWorkspace();
        var zipPath = Path.Combine(workspace.Root, "traversal.zip");
        using (var stream = File.Create(zipPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open()))
            writer.Write("must not escape");
        var service = new SongEditService(workspace.BackupRoot);

        var result = await service.ImportSongPackageAsync(zipPath, workspace.ModsRoot);

        Assert.False(result.Success);
        Assert.Contains("越界路径", result.Message);
        Assert.False(File.Exists(Path.Combine(workspace.BackupRoot, "escaped.txt")));
        Assert.Empty(Directory.GetDirectories(workspace.ModsRoot));
    }

    [Fact]
    public async Task ImportFolderRejectsNestedDirectoryJunction()
    {
        using var workspace = new TemporaryWorkspace();
        var package = workspace.CreateSourcePackage("junction-package");
        package.Write("config.toml", "enabled = true\ninclude = [\".\"]\n");
        package.Write(
            "rom/mod_pv_db.txt",
            "pv_719.song_name=Junction package\n" +
            "pv_719.song_file_name=rom/sound/song/pv_719.ogg\n" +
            "pv_719.difficulty.extreme.0.script_file_name=rom/script/pv_719_extreme.dsc\n");
        package.Write("rom/sound/song/pv_719.ogg");
        package.Write("rom/script/pv_719_extreme.dsc");
        var junctionTargetFile = workspace.WriteRootBytes(
            "junction-target/outside.txt",
            Encoding.UTF8.GetBytes("must not be copied"));
        var junctionTarget = Path.GetDirectoryName(junctionTargetFile)!;
        var junctionPath = Path.Combine(package.Root, "linked-content");
        Assert.True(CreateDirectoryJunction(junctionPath, junctionTarget));

        try
        {
            var service = new SongEditService(workspace.BackupRoot);

            var result = await service.ImportSongPackageAsync(package.Root, workspace.ModsRoot);

            Assert.False(result.Success);
            Assert.Contains("联接点", result.Message);
            Assert.Empty(Directory.GetDirectories(workspace.ModsRoot));
            Assert.Equal("must not be copied", File.ReadAllText(junctionTargetFile));
        }
        finally
        {
            if (Directory.Exists(junctionPath))
                Directory.Delete(junctionPath);
        }
    }

    private static bool CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process == null || !process.WaitForExit(10000))
        {
            try
            {
                process?.Kill(true);
            }
            catch
            {
            }
            return false;
        }
        return process.ExitCode == 0 && Directory.Exists(junctionPath);
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return File.Exists(linkPath);
        }
        catch (Exception exception) when (exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is PlatformNotSupportedException ||
            exception is NotSupportedException)
        {
            return false;
        }
    }

    private static string BackupContentPath(SongEditResult result, string relativePath)
    {
        return Path.Combine(
            result.BackupPath!,
            "content",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void WriteSignedEdenCorePackage(TemporaryDirectory package, int pvId)
    {
        var rawPvId = pvId.ToString("D3");
        package.Write(
            "config.toml",
            "enabled = true\n" +
            "name = \"Eden Project - Core\"\n" +
            "version = \"1.0.4\"\n" +
            "author = \"Eden Project Team\"\n" +
            "include = [\".\"]\n" +
            "dll = [\"DLCChecker.dll\", \"OldMan.dll\", \"SaveDataMigrator.dll\"]\n");
        package.Write("settings.toml", "[settings]\n");
        package.Write("DLCChecker.dll", "test signature");
        package.Write("OldMan.dll", "test signature");
        package.Write("SaveDataMigrator.dll", "test signature");
        package.Write(
            "rom/mod_pv_db.txt",
            $"pv_{rawPvId}.song_name=Eden official Extra Extreme {pvId}\n" +
            $"pv_{rawPvId}.song_file_name=rom/sound/song/pv_{rawPvId}.ogg\n" +
            $"pv_{rawPvId}.difficulty.extreme.length=2\n" +
            $"pv_{rawPvId}.difficulty.extreme.0.level=PV_LV_08_0\n" +
            $"pv_{rawPvId}.difficulty.extreme.0.script_file_name=rom/script/pv_{rawPvId}_extreme.dsc\n" +
            $"pv_{rawPvId}.difficulty.extreme.1.attribute.extra=1\n" +
            $"pv_{rawPvId}.difficulty.extreme.1.level=PV_LV_09_5\n" +
            $"pv_{rawPvId}.difficulty.extreme.1.script_file_name=rom/script/pv_{rawPvId}_extreme_1.dsc\n");
        package.WriteBytes(
            $"rom/script/pv_{rawPvId}_extreme_1.dsc",
            BitConverter.GetBytes(0x14050921));
    }

    private static IReadOnlyDictionary<string, byte[]> Snapshot(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertSnapshotUnchanged(string root, IReadOnlyDictionary<string, byte[]> expected)
    {
        var actual = Snapshot(root);
        Assert.Equal(expected.Keys.OrderBy(key => key), actual.Keys.OrderBy(key => key));
        foreach (var pair in expected)
            Assert.Equal(pair.Value, actual[pair.Key]);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private int modCounter;

        public TemporaryWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "DivaModManager.SongEditTests", Guid.NewGuid().ToString("N"));
            ModsRoot = Path.Combine(Root, "mods");
            BackupRoot = Path.Combine(Root, "backups");
            Directory.CreateDirectory(ModsRoot);
            Directory.CreateDirectory(Path.Combine(Root, "sources"));
        }

        public string Root { get; }
        public string ModsRoot { get; }
        public string BackupRoot { get; }

        public TemporaryDirectory CreateMod(string name)
        {
            var mod = new TemporaryDirectory(Path.Combine(ModsRoot, $"{++modCounter:00}-{name}"));
            mod.Write("config.toml", $"enabled = true\nname = \"{name}\"\ninclude = [\".\"]\n");
            return mod;
        }

        public TemporaryDirectory CreateSourcePackage(string name)
        {
            return new TemporaryDirectory(Path.Combine(Root, "sources", name));
        }

        public string WriteRootBytes(string relativePath, byte[] bytes)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
    }

    private sealed class TemporaryDirectory
    {
        public TemporaryDirectory(string root)
        {
            Root = root;
            Directory.CreateDirectory(root);
        }

        public string Root { get; }

        public string Write(string relativePath, string contents = "")
        {
            var path = Resolve(relativePath);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            return path;
        }

        public string WriteUtf8Bom(string relativePath, string contents)
        {
            var path = Resolve(relativePath);
            File.WriteAllText(path, contents, new UTF8Encoding(true));
            return path;
        }

        public string WriteBytes(string relativePath, byte[] bytes)
        {
            var path = Resolve(relativePath);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private string Resolve(string relativePath)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }
    }
}
