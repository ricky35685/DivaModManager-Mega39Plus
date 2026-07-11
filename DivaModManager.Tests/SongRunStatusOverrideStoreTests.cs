using System.Text.Json;
using Xunit;

namespace DivaModManager.Tests;

public sealed class SongRunStatusOverrideStoreTests
{
    [Fact]
    public void PersistsOverrideAndKeepsAutomaticDiagnosisIntact()
    {
        using var temp = new TemporaryDirectory();
        var filePath = Path.Combine(temp.Path, "overrides.json");
        var original = CreateEntry(temp.Path, 701, SongRunStatus.Broken);
        original.RunStatusReasons = new[] { "自动谱面诊断" };

        var writer = new SongRunStatusOverrideStore(filePath);
        writer.Set(original, SongRunStatus.Ready);

        var restored = CreateEntry(temp.Path, 701, SongRunStatus.Broken);
        restored.RunStatusReasons = new[] { "自动谱面诊断" };
        new SongRunStatusOverrideStore(filePath).Apply(restored);

        Assert.Equal(SongRunStatus.Ready, restored.RunStatus);
        Assert.Equal(SongRunStatus.Ready, restored.ManualRunStatusOverride);
        Assert.Equal(SongRunStatus.Broken, restored.AutomaticRunStatus);
        Assert.Equal("自动谱面诊断", restored.AutomaticRunStatusReasonsDisplay);
        Assert.DoesNotContain(Directory.EnumerateFiles(temp.Path), path => path.EndsWith(".tmp"));
    }

    [Fact]
    public void IdentityIsCaseInsensitiveAndIsolatesEachDatabaseContentRootAndPvId()
    {
        using var temp = new TemporaryDirectory();
        var filePath = Path.Combine(temp.Path, "overrides.json");
        var store = new SongRunStatusOverrideStore(filePath);
        store.Set(CreateEntry(temp.Path, 100, SongRunStatus.Broken), SongRunStatus.Warning);

        var differentCase = CreateEntry(temp.Path.ToUpperInvariant(), 100, SongRunStatus.Ready);
        store.Apply(differentCase);
        Assert.Equal(SongRunStatus.Warning, differentCase.RunStatus);

        var differentPv = CreateEntry(temp.Path, 101, SongRunStatus.Ready);
        store.Apply(differentPv);
        Assert.False(differentPv.HasManualRunStatusOverride);

        var differentContentRoot = CreateEntry(temp.Path, 100, SongRunStatus.Ready);
        differentContentRoot.ContentRoot = Path.Combine(temp.Path, "Mod", "rom-alt");
        store.Apply(differentContentRoot);
        Assert.False(differentContentRoot.HasManualRunStatusOverride);

        var differentDatabase = CreateEntry(temp.Path, 100, SongRunStatus.Ready);
        differentDatabase.DatabasePath = Path.Combine(temp.Path, "Mod", "rom", "other_pv_db.txt");
        store.Apply(differentDatabase);
        Assert.False(differentDatabase.HasManualRunStatusOverride);
    }

    [Fact]
    public void ClearRemovesPersistedOverrideAndRestoresAutomaticStatus()
    {
        using var temp = new TemporaryDirectory();
        var filePath = Path.Combine(temp.Path, "overrides.json");
        var entry = CreateEntry(temp.Path, 200, SongRunStatus.Broken);
        var store = new SongRunStatusOverrideStore(filePath);
        store.Set(entry, SongRunStatus.Ready);

        store.Clear(entry);

        Assert.False(entry.HasManualRunStatusOverride);
        Assert.Equal(SongRunStatus.Broken, entry.RunStatus);
        var restored = CreateEntry(temp.Path, 200, SongRunStatus.Warning);
        new SongRunStatusOverrideStore(filePath).Apply(restored);
        Assert.False(restored.HasManualRunStatusOverride);
        Assert.Equal(SongRunStatus.Warning, restored.RunStatus);
    }

    [Fact]
    public void DamagedJsonFallsBackToAutomaticAndCanBeReplacedByAValidSave()
    {
        using var temp = new TemporaryDirectory();
        var filePath = Path.Combine(temp.Path, "overrides.json");
        File.WriteAllText(filePath, "{ definitely-not-json");
        var entry = CreateEntry(temp.Path, 300, SongRunStatus.Broken);

        var store = new SongRunStatusOverrideStore(filePath);
        store.Apply(entry);
        Assert.Equal(SongRunStatus.Broken, entry.RunStatus);
        Assert.False(entry.HasManualRunStatusOverride);

        store.Set(entry, SongRunStatus.Warning);

        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        Assert.Equal(1, document.RootElement.GetProperty("Version").GetInt32());
        var restored = CreateEntry(temp.Path, 300, SongRunStatus.Ready);
        new SongRunStatusOverrideStore(filePath).Apply(restored);
        Assert.Equal(SongRunStatus.Warning, restored.RunStatus);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void IncompleteOrUnsupportedDocumentsCannotSilentlyMarkSongsReady(
        int version,
        bool includeStatus)
    {
        using var temp = new TemporaryDirectory();
        var filePath = Path.Combine(temp.Path, "overrides.json");
        var entry = CreateEntry(temp.Path, 701, SongRunStatus.Broken);
        var stored = new Dictionary<string, object>
        {
            ["ModRoot"] = entry.ModRoot,
            ["ContentRoot"] = entry.ContentRoot,
            ["DatabasePath"] = entry.DatabasePath,
            ["PvId"] = entry.PvId
        };
        if (includeStatus)
            stored["Status"] = "Ready";
        File.WriteAllText(filePath, JsonSerializer.Serialize(new
        {
            Version = version,
            Overrides = new[] { stored }
        }));

        new SongRunStatusOverrideStore(filePath).Apply(entry);

        Assert.Equal(SongRunStatus.Broken, entry.RunStatus);
        Assert.False(entry.HasManualRunStatusOverride);
    }

    [Fact]
    public void SongWithoutDeclaredVideoIsRecognizedAsThreeDimensionalPv()
    {
        var threeDimensional = new SongEntry();
        var movieBased = new SongEntry
        {
            VideoReference = "rom/movie/pv_001.mp4",
            VideoReferences = new[] { "rom/movie/pv_001.mp4" }
        };
        var patch = new SongEntry { IsSongPatch = true };
        var additionalDifficulty = new SongEntry { Format = SongFormat.AdditionalDifficulty };

        Assert.True(threeDimensional.Uses3dPv);
        Assert.False(movieBased.Uses3dPv);
        Assert.False(patch.Uses3dPv);
        Assert.False(additionalDifficulty.Uses3dPv);
    }

    private static SongEntry CreateEntry(string root, int pvId, SongRunStatus automaticStatus)
    {
        var modRoot = Path.Combine(root, "Mod");
        var contentRoot = Path.Combine(modRoot, "rom");
        return new SongEntry
        {
            ModRoot = modRoot,
            ContentRoot = contentRoot,
            DatabasePath = Path.Combine(contentRoot, "mod_pv_db.txt"),
            PvId = pvId,
            RunStatus = automaticStatus
        };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DivaModManagerOverrideTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
