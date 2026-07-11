using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Sprites;
using MikuMikuLibrary.Textures;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace DivaModManager.Tests;

public sealed class SongArtworkServiceTests
{
    [Fact]
    public async Task ThumbnailPreviewIsVerticallyCorrectedAndReplacementPreservesNeighborSprite()
    {
        using var workspace = new ArtworkWorkspace();
        workspace.CreateSharedThumbnailArchive();
        var firstSong = workspace.CreateSong(42);
        var neighborSong = workspace.CreateSong(43);
        var editService = new SongEditService(workspace.BackupRoot);
        var service = new SongArtworkService(workspace.ModsRoot, editService);

        var initial = await service.LoadPreviewAsync(firstSong, SongArtworkKind.Thumbnail);

        Assert.True(initial.IsAvailable, initial.Message);
        Assert.True(initial.CanReplace);
        Assert.Equal(4, initial.Width);
        Assert.Equal(4, initial.Height);
        using (var bitmap = LoadPng(initial.PngBytes))
        {
            AssertColor(bitmap.GetPixel(1, 0), Color.Red);
            AssertColor(bitmap.GetPixel(1, 3), Color.Blue);
        }

        var replacementPath = workspace.CreateReplacementImage(Color.Lime);
        var result = await service.ReplaceAsync(
            firstSong,
            SongArtworkKind.Thumbnail,
            replacementPath);

        Assert.True(result.Success, result.Message);
        Assert.True(Directory.Exists(result.BackupPath));
        Assert.True(File.Exists(Path.Combine(result.BackupPath!, "manifest.json")));

        var replaced = await service.LoadPreviewAsync(firstSong, SongArtworkKind.Thumbnail);
        using (var bitmap = LoadPng(replaced.PngBytes))
        {
            AssertColor(bitmap.GetPixel(1, 0), Color.Lime, 4);
            AssertColor(bitmap.GetPixel(1, 3), Color.Lime, 4);
        }

        var neighbor = await service.LoadPreviewAsync(neighborSong, SongArtworkKind.Thumbnail);
        Assert.True(neighbor.IsAvailable, neighbor.Message);
        using (var bitmap = LoadPng(neighbor.PngBytes))
            AssertColor(bitmap.GetPixel(1, 1), Color.Magenta);
    }

    [Fact]
    public async Task YCbCrArtworkCanBePreviewedAndReplaced()
    {
        using var workspace = new ArtworkWorkspace();
        workspace.CreateYCbCrJacketArchive(44, Color.Red);
        var song = workspace.CreateSong(44);
        var service = new SongArtworkService(
            workspace.ModsRoot,
            new SongEditService(workspace.BackupRoot));

        var initial = await service.LoadPreviewAsync(song, SongArtworkKind.Jacket);

        Assert.True(initial.IsAvailable, initial.Message);
        using (var bitmap = LoadPng(initial.PngBytes))
            AssertColor(bitmap.GetPixel(3, 3), Color.Red, 35);

        var replacementPath = workspace.CreateReplacementImage(Color.Lime);
        var result = await service.ReplaceAsync(song, SongArtworkKind.Jacket, replacementPath);
        var replaced = await service.LoadPreviewAsync(song, SongArtworkKind.Jacket);

        Assert.True(result.Success, result.Message);
        Assert.True(replaced.IsAvailable, replaced.Message);
        using (var bitmap = LoadPng(replaced.PngBytes))
            AssertColor(bitmap.GetPixel(3, 3), Color.Lime, 45);
    }

    [Fact]
    public async Task DatabaseEntryDoesNotFallbackToAnotherInternalSpriteSet()
    {
        using var workspace = new ArtworkWorkspace();
        workspace.CreateMismatchedDatabaseArchive(42);
        var song = workspace.CreateSong(42);
        var service = new SongArtworkService(
            workspace.ModsRoot,
            new SongEditService(workspace.BackupRoot));

        var preview = await service.LoadPreviewAsync(song, SongArtworkKind.Thumbnail);
        var result = await service.ReplaceAsync(
            song,
            SongArtworkKind.Thumbnail,
            workspace.CreateReplacementImage(Color.Lime));

        Assert.False(preview.IsAvailable);
        Assert.False(preview.CanReplace);
        Assert.False(service.ProbeAvailability(song, SongArtworkKind.Thumbnail));
        Assert.False(result.Success);
    }

    [Fact]
    public async Task MissingDatabaseDoesNotTreatAnUnrelatedSingleSpriteAsTheSong()
    {
        using var workspace = new ArtworkWorkspace();
        workspace.CreateUnrelatedSingleThumbnailArchive();
        var song = workspace.CreateSong(42);
        var service = new SongArtworkService(workspace.ModsRoot);

        var preview = await service.LoadPreviewAsync(song, SongArtworkKind.Thumbnail);

        Assert.False(preview.IsAvailable);
        Assert.False(service.ProbeAvailability(song, SongArtworkKind.Thumbnail));
    }

    [Fact]
    public async Task CompressedReplacementPreservesEveryDecodedNeighborPixel()
    {
        using var workspace = new ArtworkWorkspace();
        workspace.CreateCompressedSharedThumbnailArchive();
        var targetSong = workspace.CreateSong(42);
        var neighborSong = workspace.CreateSong(43);
        var service = new SongArtworkService(
            workspace.ModsRoot,
            new SongEditService(workspace.BackupRoot));
        var before = await service.LoadPreviewAsync(neighborSong, SongArtworkKind.Thumbnail);

        var result = await service.ReplaceAsync(
            targetSong,
            SongArtworkKind.Thumbnail,
            workspace.CreateReplacementImage(Color.Lime));
        var target = await service.LoadPreviewAsync(targetSong, SongArtworkKind.Thumbnail);
        var after = await service.LoadPreviewAsync(neighborSong, SongArtworkKind.Thumbnail);

        Assert.True(result.Success, result.Message);
        Assert.True(before.IsAvailable, before.Message);
        Assert.True(after.IsAvailable, after.Message);
        AssertPngPixelsEqual(before.PngBytes, after.PngBytes);
        using var targetBitmap = LoadPng(target.PngBytes);
        AssertBitmapColor(targetBitmap, Color.Lime, 55);
        var textureIndices = workspace.ReadCompressedThumbnailTextureIndices();
        Assert.Equal(textureIndices.Target, textureIndices.Alias);
        Assert.NotEqual(textureIndices.Target, textureIndices.Neighbor);
        Assert.Equal(2, textureIndices.TextureCount);

        var secondResult = await service.ReplaceAsync(
            targetSong,
            SongArtworkKind.Thumbnail,
            workspace.CreateReplacementImage(Color.Magenta));
        var secondTarget = await service.LoadPreviewAsync(targetSong, SongArtworkKind.Thumbnail);
        var secondNeighbor = await service.LoadPreviewAsync(neighborSong, SongArtworkKind.Thumbnail);

        Assert.True(secondResult.Success, secondResult.Message);
        AssertPngPixelsEqual(before.PngBytes, secondNeighbor.PngBytes);
        using var secondTargetBitmap = LoadPng(secondTarget.PngBytes);
        AssertBitmapColor(secondTargetBitmap, Color.Magenta, 55);
        Assert.All(
            workspace.ReadCompressedTargetMipColors(1),
            color => AssertColor(color, Color.Magenta, 70));
        Assert.Equal(2, workspace.ReadCompressedThumbnailTextureIndices().TextureCount);
    }

    [Fact]
    public async Task SharedYCbCrReplacementPreservesEveryDecodedNeighborPixel()
    {
        using var workspace = new ArtworkWorkspace();
        workspace.CreateSharedYCbCrThumbnailArchive();
        var targetSong = workspace.CreateSong(42);
        var neighborSong = workspace.CreateSong(43);
        var service = new SongArtworkService(
            workspace.ModsRoot,
            new SongEditService(workspace.BackupRoot));
        var before = await service.LoadPreviewAsync(neighborSong, SongArtworkKind.Thumbnail);

        var result = await service.ReplaceAsync(
            targetSong,
            SongArtworkKind.Thumbnail,
            workspace.CreateReplacementImage(Color.Lime));
        var target = await service.LoadPreviewAsync(targetSong, SongArtworkKind.Thumbnail);
        var after = await service.LoadPreviewAsync(neighborSong, SongArtworkKind.Thumbnail);

        Assert.True(result.Success, result.Message);
        Assert.True(before.IsAvailable, before.Message);
        Assert.True(after.IsAvailable, after.Message);
        AssertPngPixelsEqual(before.PngBytes, after.PngBytes);
        using var targetBitmap = LoadPng(target.PngBytes);
        AssertBitmapColor(targetBitmap, Color.Lime, 75);

        var secondResult = await service.ReplaceAsync(
            targetSong,
            SongArtworkKind.Thumbnail,
            workspace.CreateReplacementImage(Color.Magenta));
        var secondTarget = await service.LoadPreviewAsync(targetSong, SongArtworkKind.Thumbnail);
        var secondNeighbor = await service.LoadPreviewAsync(neighborSong, SongArtworkKind.Thumbnail);

        Assert.True(secondResult.Success, secondResult.Message);
        AssertPngPixelsEqual(before.PngBytes, secondNeighbor.PngBytes);
        using var secondTargetBitmap = LoadPng(secondTarget.PngBytes);
        AssertBitmapColor(secondTargetBitmap, Color.Magenta, 85);
    }

    [Fact]
    public void AggregateProbeDoesNotTreatAnExistingFarcAsMissingArtworkKinds()
    {
        using var workspace = new ArtworkWorkspace();
        workspace.CreateJacketOnlyArchive(45);
        var service = new SongArtworkService(workspace.ModsRoot);

        var availability = service.ProbeAvailability(workspace.CreateSong(45));

        Assert.False(availability.Thumbnail);
        Assert.True(availability.Jacket);
        Assert.False(availability.Background);
        Assert.True(availability.IsAvailable(SongArtworkKind.Jacket));
        Assert.Equal(1, service.ArchiveSnapshotBuildCount);
    }

    [Fact]
    public void GlobalDatabaseIndexCachesPositiveNegativeAndRawAliasLookups()
    {
        using var workspace = new ArtworkWorkspace();
        workspace.CreateIndexedJacketProvider(
            "Global alias provider",
            42,
            "0042",
            Color.Blue,
            includeNonExactFallback: true);
        var service = new SongArtworkService(workspace.ModsRoot);
        var aliasedSong = workspace.CreateSong(42);
        aliasedSong.RawPvId = "0042";
        aliasedSong.RawPvIds = new[] { "0042", "42" };

        Assert.True(service.ProbeAvailability(aliasedSong, SongArtworkKind.Jacket));
        for (var pvId = 5000; pvId < 5100; pvId++)
            Assert.False(service.ProbeAvailability(workspace.CreateSong(pvId), SongArtworkKind.Jacket));
        Assert.True(service.ProbeAvailability(aliasedSong, SongArtworkKind.Jacket));
        Assert.Equal(1, service.GlobalDatabaseIndexBuildCount);
    }

    [Fact]
    public async Task LocalDatabaseRemainsPreferredOverGlobalIndex()
    {
        using var workspace = new ArtworkWorkspace();
        workspace.CreateIndexedJacketProvider(
            "Shared thumbnails",
            46,
            "046",
            Color.Red,
            includeNonExactFallback: false,
            local: true);
        workspace.CreateIndexedJacketProvider(
            "Earlier global provider",
            46,
            "046",
            Color.Blue,
            includeNonExactFallback: false);
        var song = workspace.CreateSong(46);
        song.RawPvId = "046";
        song.RawPvIds = new[] { "046", "46" };
        var service = new SongArtworkService(workspace.ModsRoot);

        var preview = await service.LoadPreviewAsync(song, SongArtworkKind.Jacket);

        Assert.True(preview.IsAvailable, preview.Message);
        Assert.True(preview.CanReplace);
        Assert.Equal(0, service.GlobalDatabaseIndexBuildCount);
        using var bitmap = LoadPng(preview.PngBytes);
        AssertColor(bitmap.GetPixel(3, 3), Color.Red);
    }

    [Fact]
    public void ProbeReleasesArchiveSoItCanBeMovedAndDeletedImmediately()
    {
        using var workspace = new ArtworkWorkspace();
        workspace.CreateJacketOnlyArchive(47);
        var archivePath = Path.Combine(
            workspace.ModRoot,
            "rom",
            "2d",
            "spr_sel_pv47.farc");
        var movedPath = archivePath + ".moved";
        var service = new SongArtworkService(workspace.ModsRoot);

        Assert.True(service.ProbeAvailability(
            workspace.CreateSong(47),
            SongArtworkKind.Jacket));
        File.Move(archivePath, movedPath);
        File.Delete(movedPath);

        Assert.False(File.Exists(movedPath));
    }

    [Fact]
    public void DisabledGlobalProviderDoesNotSupplyArtwork()
    {
        using var workspace = new ArtworkWorkspace();
        workspace.CreateIndexedJacketProvider(
            "Disabled provider",
            48,
            "048",
            Color.Blue,
            includeNonExactFallback: false,
            enabled: false);
        var song = workspace.CreateSong(48);
        song.RawPvId = "048";
        song.RawPvIds = new[] { "048", "48" };
        var service = new SongArtworkService(workspace.ModsRoot);

        Assert.False(service.ProbeAvailability(song, SongArtworkKind.Jacket));
        Assert.Equal(1, service.GlobalDatabaseIndexBuildCount);
    }

    private static Bitmap LoadPng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private static void AssertColor(Color actual, Color expected, int tolerance = 0)
    {
        Assert.InRange(actual.R, Math.Max(0, expected.R - tolerance), Math.Min(255, expected.R + tolerance));
        Assert.InRange(actual.G, Math.Max(0, expected.G - tolerance), Math.Min(255, expected.G + tolerance));
        Assert.InRange(actual.B, Math.Max(0, expected.B - tolerance), Math.Min(255, expected.B + tolerance));
        Assert.InRange(actual.A, Math.Max(0, expected.A - tolerance), Math.Min(255, expected.A + tolerance));
    }

    private static void AssertPngPixelsEqual(byte[] expectedBytes, byte[] actualBytes)
    {
        using var expected = LoadPng(expectedBytes);
        using var actual = LoadPng(actualBytes);
        Assert.Equal(expected.Size, actual.Size);
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
        }
    }

    private static void AssertBitmapColor(Bitmap bitmap, Color expected, int tolerance)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
                AssertColor(bitmap.GetPixel(x, y), expected, tolerance);
        }
    }

    private sealed class ArtworkWorkspace : IDisposable
    {
        public ArtworkWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "DmmArtworkTests", Guid.NewGuid().ToString("N"));
            ModsRoot = Path.Combine(Root, "mods");
            ModRoot = Path.Combine(ModsRoot, "Shared thumbnails");
            BackupRoot = Path.Combine(Root, "backups");
            Directory.CreateDirectory(Path.Combine(ModRoot, "rom", "2d"));
        }

        public string Root { get; }
        public string ModsRoot { get; }
        public string ModRoot { get; }
        public string BackupRoot { get; }

        public SongEntry CreateSong(int pvId)
        {
            return new SongEntry
            {
                ModName = "Shared thumbnails",
                ModRoot = ModRoot,
                ContentRoot = ModRoot,
                PvId = pvId,
                RawPvId = pvId.ToString(),
                RawPvIds = new[] { pvId.ToString() },
                SongName = "PV " + pvId
            };
        }

        public void CreateSharedThumbnailArchive()
        {
            const int width = 8;
            const int height = 4;
            var texture = new Texture(width, height, TextureFormat.RGBA8);
            var data = texture[0, 0].Data;
            for (var logicalY = 0; logicalY < height; logicalY++)
            {
                var storedY = height - 1 - logicalY;
                for (var x = 0; x < width; x++)
                {
                    var color = x < 4
                        ? logicalY == 0 ? Color.Red : Color.Blue
                        : Color.Magenta;
                    var offset = (storedY * width + x) * 4;
                    data[offset] = color.R;
                    data[offset + 1] = color.G;
                    data[offset + 2] = color.B;
                    data[offset + 3] = color.A;
                }
            }

            var spriteSet = new SpriteSet();
            spriteSet.TextureSet.Textures.Add(texture);
            spriteSet.Sprites.Add(new Sprite
            {
                Name = "42",
                TextureIndex = 0,
                X = 0,
                Y = 0,
                Width = 4,
                Height = 4
            });
            spriteSet.Sprites.Add(new Sprite
            {
                Name = "43",
                TextureIndex = 0,
                X = 4,
                Y = 0,
                Width = 4,
                Height = 4
            });

            using var spriteBytes = new MemoryStream();
            spriteSet.Save(spriteBytes, true);
            spriteBytes.Position = 0;
            using var archive = new FarcArchive();
            archive.Add("spr_sel_pvtmb_test.bin", spriteBytes, true, ConflictPolicy.Replace);
            archive.Save(Path.Combine(ModRoot, "rom", "2d", "spr_sel_pvtmb_test.farc"));
        }

        public void CreateMismatchedDatabaseArchive(int pvId)
        {
            var texture = CreateRgbaTexture(4, 4, (_, _) => Color.Magenta);
            var sprites = new[]
            {
                new Sprite
                {
                    Name = "SPR_SEL_PVTMB_999",
                    TextureIndex = 0,
                    X = 0,
                    Y = 0,
                    Width = 4,
                    Height = 4
                }
            };
            SaveThumbnailArchive(
                "spr_sel_pvtmb_expected.farc",
                "another_sprite_set.bin",
                texture,
                sprites);

            using var database = new SpriteDatabase();
            var set = new SpriteSetInfo
            {
                Id = 1,
                Name = "SPR_SEL_PVTMB_EXPECTED",
                FileName = "spr_sel_pvtmb_expected.bin"
            };
            set.Sprites.Add(new SpriteInfo
            {
                Id = 1,
                Name = $"SPR_SEL_PVTMB_{pvId}",
                Index = 0
            });
            database.SpriteSets.Add(set);
            database.Save(Path.Combine(ModRoot, "rom", "2d", "mod_spr_db.bin"));
        }

        public void CreateUnrelatedSingleThumbnailArchive()
        {
            var texture = CreateRgbaTexture(4, 4, (_, _) => Color.Magenta);
            SaveThumbnailArchive(
                "spr_sel_pvtmb_unknown.farc",
                "spr_sel_pvtmb_unknown.bin",
                texture,
                new[]
                {
                    new Sprite
                    {
                        Name = "UNRELATED_SPRITE",
                        TextureIndex = 0,
                        X = 0,
                        Y = 0,
                        Width = 4,
                        Height = 4
                    }
                });
        }

        public void CreateCompressedSharedThumbnailArchive()
        {
            const int width = 12;
            const int height = 8;
            const int targetWidth = 6;
            Color GetColor(int x, int y)
            {
                if (x < targetWidth)
                    return Color.FromArgb(255, 210, 25 + y * 3, 35);
                return Color.FromArgb(
                    255,
                    35 + (x * 29 + y * 17) % 190,
                    25 + (x * 13 + y * 31) % 200,
                    30 + (x * 41 + y * 11) % 180);
            }
            var rgba = CreateStoredRgba(width, height, GetColor);
            var encoder = new BcEncoder(CompressionFormat.Bc3);
            encoder.OutputOptions.GenerateMipMaps = false;
            var encoded = encoder.EncodeToRawBytes(
                rgba,
                width,
                height,
                BCnEncoder.Encoder.PixelFormat.Rgba32)[0];
            var mipRgba = CreateStoredRgba(
                width / 2,
                height / 2,
                (x, y) => GetColor(x * 2, y * 2));
            var encodedMip = encoder.EncodeToRawBytes(
                mipRgba,
                width / 2,
                height / 2,
                BCnEncoder.Encoder.PixelFormat.Rgba32)[0];
            var texture = new Texture(width, height, TextureFormat.DXT5, 1, 2);
            Buffer.BlockCopy(encoded, 0, texture[0, 0].Data, 0, encoded.Length);
            Buffer.BlockCopy(encodedMip, 0, texture[0, 1].Data, 0, encodedMip.Length);
            SaveThumbnailArchive(
                "spr_sel_pvtmb_bc.farc",
                "spr_sel_pvtmb_bc.bin",
                texture,
                new[]
                {
                    new Sprite
                    {
                        Name = "SPR_SEL_PVTMB_42",
                        TextureIndex = 0,
                        X = 0,
                        Y = 0,
                        Width = targetWidth,
                        Height = height
                    },
                    new Sprite
                    {
                        Name = "SPR_SEL_PVTMB_43",
                        TextureIndex = 0,
                        X = targetWidth,
                        Y = 0,
                        Width = width - targetWidth,
                        Height = height
                    },
                    new Sprite
                    {
                        Name = "SPR_SEL_PVTMB_ALIAS_42",
                        TextureIndex = 0,
                        X = 0,
                        Y = 0,
                        Width = targetWidth,
                        Height = height
                    }
                });
        }

        public (uint Target, uint Alias, uint Neighbor, int TextureCount)
            ReadCompressedThumbnailTextureIndices()
        {
            var path = Path.Combine(ModRoot, "rom", "2d", "spr_sel_pvtmb_bc.farc");
            using var archive = BinaryFile.Load<FarcArchive>(path);
            using var stream = archive.Open("spr_sel_pvtmb_bc.bin", EntryStreamMode.MemoryStream);
            using var spriteSet = BinaryFile.Load<SpriteSet>(stream, true);
            return (
                spriteSet.Sprites.Single(sprite => sprite.Name == "SPR_SEL_PVTMB_42").TextureIndex,
                spriteSet.Sprites.Single(sprite => sprite.Name == "SPR_SEL_PVTMB_ALIAS_42").TextureIndex,
                spriteSet.Sprites.Single(sprite => sprite.Name == "SPR_SEL_PVTMB_43").TextureIndex,
                spriteSet.TextureSet.Textures.Count);
        }

        public IReadOnlyList<Color> ReadCompressedTargetMipColors(int level)
        {
            var path = Path.Combine(ModRoot, "rom", "2d", "spr_sel_pvtmb_bc.farc");
            using var archive = BinaryFile.Load<FarcArchive>(path);
            using var stream = archive.Open("spr_sel_pvtmb_bc.bin", EntryStreamMode.MemoryStream);
            using var spriteSet = BinaryFile.Load<SpriteSet>(stream, true);
            var sprite = spriteSet.Sprites.Single(sprite => sprite.Name == "SPR_SEL_PVTMB_42");
            var texture = spriteSet.TextureSet.Textures[(int)sprite.TextureIndex];
            var mip = texture[0, level];
            var decoded = new BcDecoder().DecodeRaw(
                mip.Data,
                mip.Width,
                mip.Height,
                CompressionFormat.Bc3);
            var targetWidth = (int)Math.Ceiling(sprite.Width * mip.Width / texture.Width);
            var colors = new List<Color>();
            for (var y = 0; y < mip.Height; y++)
            {
                for (var x = 0; x < targetWidth; x++)
                {
                    var color = decoded[y * mip.Width + x];
                    colors.Add(Color.FromArgb(color.a, color.r, color.g, color.b));
                }
            }
            return colors;
        }

        public void CreateSharedYCbCrThumbnailArchive()
        {
            const int width = 24;
            const int height = 8;
            const int targetWidth = 10;
            var texture = CreateYCbCrTexture(width, height, (x, y) =>
            {
                if (x < targetWidth)
                    return Color.FromArgb(255, 220, 30, 45);
                return Color.FromArgb(
                    255,
                    25 + (x * 17 + y * 23) % 205,
                    20 + (x * 37 + y * 7) % 210,
                    30 + (x * 11 + y * 43) % 195);
            });
            SaveThumbnailArchive(
                "spr_sel_pvtmb_ycbcr.farc",
                "spr_sel_pvtmb_ycbcr.bin",
                texture,
                new[]
                {
                    new Sprite
                    {
                        Name = "SPR_SEL_PVTMB_42",
                        TextureIndex = 0,
                        X = 0,
                        Y = 0,
                        Width = targetWidth,
                        Height = height
                    },
                    new Sprite
                    {
                        Name = "SPR_SEL_PVTMB_43",
                        TextureIndex = 0,
                        X = targetWidth,
                        Y = 0,
                        Width = width - targetWidth,
                        Height = height
                    }
                });
        }

        public void CreateJacketOnlyArchive(int pvId)
        {
            var texture = CreateRgbaTexture(8, 8, (_, _) => Color.Cyan);
            var spriteSet = new SpriteSet();
            spriteSet.TextureSet.Textures.Add(texture);
            spriteSet.Sprites.Add(new Sprite
            {
                Name = $"SPR_SEL_PV{pvId}_SONG_JK{pvId}",
                TextureIndex = 0,
                X = 0,
                Y = 0,
                Width = 8,
                Height = 8
            });

            using var spriteBytes = new MemoryStream();
            spriteSet.Save(spriteBytes, true);
            spriteBytes.Position = 0;
            using var archive = new FarcArchive();
            archive.Add($"spr_sel_pv{pvId}.bin", spriteBytes, true, ConflictPolicy.Replace);
            archive.Save(Path.Combine(ModRoot, "rom", "2d", $"spr_sel_pv{pvId}.farc"));
        }

        public void CreateIndexedJacketProvider(
            string providerName,
            int pvId,
            string rawPvId,
            Color color,
            bool includeNonExactFallback,
            bool local = false,
            bool enabled = true)
        {
            var providerRoot = local ? ModRoot : Path.Combine(ModsRoot, providerName);
            var twoD = Path.Combine(providerRoot, "rom", "2d");
            Directory.CreateDirectory(twoD);
            if (!local)
            {
                File.WriteAllText(
                    Path.Combine(providerRoot, "config.toml"),
                    $"enabled = {enabled.ToString().ToLowerInvariant()}\n");
            }
            var setFileName = $"spr_sel_pv{rawPvId}.bin";
            var spriteName = $"SPR_SEL_PV{rawPvId}_SONG_JK{rawPvId}";
            var texture = CreateRgbaTexture(8, 8, (_, _) => color);
            var spriteSet = new SpriteSet();
            spriteSet.TextureSet.Textures.Add(texture);
            spriteSet.Sprites.Add(new Sprite
            {
                Name = spriteName,
                TextureIndex = 0,
                X = 0,
                Y = 0,
                Width = 8,
                Height = 8
            });
            using (var spriteBytes = new MemoryStream())
            {
                spriteSet.Save(spriteBytes, true);
                spriteBytes.Position = 0;
                using var archive = new FarcArchive();
                archive.Add(setFileName, spriteBytes, true, ConflictPolicy.Replace);
                archive.Save(Path.Combine(twoD, Path.GetFileNameWithoutExtension(setFileName) + ".farc"));
            }

            using var database = new SpriteDatabase();
            if (includeNonExactFallback)
            {
                var fallbackSet = new SpriteSetInfo
                {
                    Id = 1,
                    Name = "NON_EXACT_FALLBACK",
                    FileName = "missing_fallback.bin"
                };
                fallbackSet.Sprites.Add(new SpriteInfo
                {
                    Id = 1,
                    Name = $"CUSTOM_SONG_JK{pvId}",
                    Index = 0
                });
                database.SpriteSets.Add(fallbackSet);
            }

            var set = new SpriteSetInfo
            {
                Id = 2,
                Name = "INDEXED_JACKET",
                FileName = setFileName
            };
            set.Sprites.Add(new SpriteInfo
            {
                Id = 2,
                Name = spriteName,
                Index = 0
            });
            database.SpriteSets.Add(set);
            database.Save(Path.Combine(twoD, "mod_spr_db.bin"));
        }

        public void CreateYCbCrJacketArchive(int pvId, Color color)
        {
            const int width = 8;
            const int height = 8;
            var red = color.R / 255f;
            var green = color.G / 255f;
            var blue = color.B / 255f;
            var valueY = red * 0.212593317f + green * 0.715214610f + blue * 0.0721921176f;
            var valueCb = (red * -0.114568502f + green * -0.385435730f + blue * 0.5000042320f + 0.503929f) / 1.003922f;
            var valueCr = (red * 0.500004232f + green * -0.454162151f + blue * -0.0458420813f + 0.503929f) / 1.003922f;
            var luminancePixels = CreateRgPixels(width, height, valueY, color.A / 255f);
            var chromaPixels = CreateRgPixels(width / 2, height / 2, valueCb, valueCr);
            var encoder = new BcEncoder(CompressionFormat.Bc5);
            encoder.OutputOptions.GenerateMipMaps = false;
            var luminanceData = encoder.EncodeToRawBytes(
                luminancePixels,
                width,
                height,
                BCnEncoder.Encoder.PixelFormat.Rgba32)[0];
            var chromaData = encoder.EncodeToRawBytes(
                chromaPixels,
                width / 2,
                height / 2,
                BCnEncoder.Encoder.PixelFormat.Rgba32)[0];

            var texture = new Texture(width, height, TextureFormat.ATI2, 1, 2);
            Buffer.BlockCopy(luminanceData, 0, texture[0, 0].Data, 0, luminanceData.Length);
            Buffer.BlockCopy(chromaData, 0, texture[0, 1].Data, 0, chromaData.Length);
            var spriteSet = new SpriteSet();
            spriteSet.TextureSet.Textures.Add(texture);
            spriteSet.Sprites.Add(new Sprite
            {
                Name = $"SPR_SEL_PV{pvId}_SONG_JK{pvId}",
                TextureIndex = 0,
                X = 0,
                Y = 0,
                Width = width,
                Height = height
            });

            using var spriteBytes = new MemoryStream();
            spriteSet.Save(spriteBytes, true);
            spriteBytes.Position = 0;
            using var archive = new FarcArchive();
            archive.Add($"spr_sel_pv{pvId}.bin", spriteBytes, true, ConflictPolicy.Replace);
            archive.Save(Path.Combine(ModRoot, "rom", "2d", $"spr_sel_pv{pvId}.farc"));
        }

        private void SaveThumbnailArchive(
            string archiveName,
            string internalName,
            Texture texture,
            IReadOnlyList<Sprite> sprites)
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
            archive.Save(Path.Combine(ModRoot, "rom", "2d", archiveName));
        }

        private static Texture CreateRgbaTexture(
            int width,
            int height,
            Func<int, int, Color> getLogicalColor)
        {
            var texture = new Texture(width, height, TextureFormat.RGBA8);
            var rgba = CreateStoredRgba(width, height, getLogicalColor);
            Buffer.BlockCopy(rgba, 0, texture[0, 0].Data, 0, rgba.Length);
            return texture;
        }

        private static byte[] CreateStoredRgba(
            int width,
            int height,
            Func<int, int, Color> getLogicalColor)
        {
            var rgba = new byte[width * height * 4];
            for (var storedY = 0; storedY < height; storedY++)
            {
                var logicalY = height - 1 - storedY;
                for (var x = 0; x < width; x++)
                {
                    var color = getLogicalColor(x, logicalY);
                    var offset = (storedY * width + x) * 4;
                    rgba[offset] = color.R;
                    rgba[offset + 1] = color.G;
                    rgba[offset + 2] = color.B;
                    rgba[offset + 3] = color.A;
                }
            }
            return rgba;
        }

        private static Texture CreateYCbCrTexture(
            int width,
            int height,
            Func<int, int, Color> getLogicalColor)
        {
            var rgba = CreateStoredRgba(width, height, getLogicalColor);
            var chromaWidth = Math.Max(1, width / 2);
            var chromaHeight = Math.Max(1, height / 2);
            var luminancePixels = new byte[width * height * 4];
            var fullChroma = new float[width * height * 2];
            for (var index = 0; index < width * height; index++)
            {
                var source = index * 4;
                var red = rgba[source] / 255f;
                var green = rgba[source + 1] / 255f;
                var blue = rgba[source + 2] / 255f;
                var valueY = red * 0.212593317f + green * 0.715214610f + blue * 0.0721921176f;
                var valueCb = (red * -0.114568502f + green * -0.385435730f + blue * 0.5000042320f + 0.503929f) / 1.003922f;
                var valueCr = (red * 0.500004232f + green * -0.454162151f + blue * -0.0458420813f + 0.503929f) / 1.003922f;
                luminancePixels[source] = ToByte(valueY);
                luminancePixels[source + 1] = rgba[source + 3];
                luminancePixels[source + 3] = 255;
                fullChroma[index * 2] = Math.Clamp(valueCb, 0f, 1f);
                fullChroma[index * 2 + 1] = Math.Clamp(valueCr, 0f, 1f);
            }

            var chromaPixels = new byte[chromaWidth * chromaHeight * 4];
            for (var y = 0; y < chromaHeight; y++)
            {
                for (var x = 0; x < chromaWidth; x++)
                {
                    var cb = 0f;
                    var cr = 0f;
                    var count = 0;
                    var startX = x * width / chromaWidth;
                    var endX = Math.Max(startX + 1, (x + 1) * width / chromaWidth);
                    var startY = y * height / chromaHeight;
                    var endY = Math.Max(startY + 1, (y + 1) * height / chromaHeight);
                    for (var sourceY = startY; sourceY < endY && sourceY < height; sourceY++)
                    {
                        for (var sourceX = startX; sourceX < endX && sourceX < width; sourceX++)
                        {
                            var source = (sourceY * width + sourceX) * 2;
                            cb += fullChroma[source];
                            cr += fullChroma[source + 1];
                            count++;
                        }
                    }
                    var target = (y * chromaWidth + x) * 4;
                    chromaPixels[target] = ToByte(cb / count);
                    chromaPixels[target + 1] = ToByte(cr / count);
                    chromaPixels[target + 3] = 255;
                }
            }

            var encoder = new BcEncoder(CompressionFormat.Bc5);
            encoder.OutputOptions.GenerateMipMaps = false;
            var luminance = encoder.EncodeToRawBytes(
                luminancePixels,
                width,
                height,
                BCnEncoder.Encoder.PixelFormat.Rgba32)[0];
            var chroma = encoder.EncodeToRawBytes(
                chromaPixels,
                chromaWidth,
                chromaHeight,
                BCnEncoder.Encoder.PixelFormat.Rgba32)[0];
            var texture = new Texture(width, height, TextureFormat.ATI2, 1, 2);
            Buffer.BlockCopy(luminance, 0, texture[0, 0].Data, 0, luminance.Length);
            Buffer.BlockCopy(chroma, 0, texture[0, 1].Data, 0, chroma.Length);
            return texture;
        }

        private static byte ToByte(float value)
        {
            return (byte)Math.Clamp((int)Math.Round(value * 255f), 0, 255);
        }

        private static byte[] CreateRgPixels(int width, int height, float red, float green)
        {
            var pixels = new byte[width * height * 4];
            for (var index = 0; index < width * height; index++)
            {
                pixels[index * 4] = (byte)Math.Clamp((int)Math.Round(red * 255), 0, 255);
                pixels[index * 4 + 1] = (byte)Math.Clamp((int)Math.Round(green * 255), 0, 255);
                pixels[index * 4 + 2] = 0;
                pixels[index * 4 + 3] = 255;
            }
            return pixels;
        }

        public string CreateReplacementImage(Color color)
        {
            var path = Path.Combine(Root, "replacement.png");
            using var bitmap = new Bitmap(12, 8, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(color);
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
            catch
            {
            }
        }
    }
}
