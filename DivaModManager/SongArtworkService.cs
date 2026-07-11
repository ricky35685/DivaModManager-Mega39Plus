using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Sprites;
using MikuMikuLibrary.Textures;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Tomlyn;
using Tomlyn.Model;
using BcnPixelFormat = BCnEncoder.Encoder.PixelFormat;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace DivaModManager
{
    public enum SongArtworkKind
    {
        Thumbnail,
        Jacket,
        Background
    }

    public sealed class SongArtworkPreview
    {
        internal SongArtworkPreview(
            SongArtworkKind kind,
            byte[] pngBytes,
            int width,
            int height,
            string sourceDescription,
            string archivePath,
            bool canReplace,
            string message)
        {
            Kind = kind;
            PngBytes = pngBytes ?? Array.Empty<byte>();
            Width = width;
            Height = height;
            SourceDescription = sourceDescription ?? String.Empty;
            ArchivePath = archivePath ?? String.Empty;
            CanReplace = canReplace;
            Message = message ?? String.Empty;
        }

        public SongArtworkKind Kind { get; }
        public byte[] PngBytes { get; }
        public int Width { get; }
        public int Height { get; }
        public string SourceDescription { get; }
        public string ArchivePath { get; }
        public bool CanReplace { get; }
        public string Message { get; }
        public bool IsAvailable => PngBytes.Length > 0;

        internal static SongArtworkPreview Missing(SongArtworkKind kind, string message)
        {
            return new SongArtworkPreview(kind, null, 0, 0, String.Empty, String.Empty, false, message);
        }
    }

    public sealed class SongArtworkAvailability
    {
        internal SongArtworkAvailability(bool thumbnail, bool jacket, bool background)
        {
            Thumbnail = thumbnail;
            Jacket = jacket;
            Background = background;
        }

        public bool Thumbnail { get; }
        public bool Jacket { get; }
        public bool Background { get; }

        public bool IsAvailable(SongArtworkKind kind)
        {
            return kind switch
            {
                SongArtworkKind.Thumbnail => Thumbnail,
                SongArtworkKind.Jacket => Jacket,
                SongArtworkKind.Background => Background,
                _ => false
            };
        }
    }

    /// <summary>
    /// Reads and edits MEGA39+ sprite atlases. Game atlases are stored upside down;
    /// all crop coordinates are applied after the atlas is vertically corrected.
    /// </summary>
    public sealed class SongArtworkService
    {
        private readonly object cacheLock = new object();
        private readonly Dictionary<string, DatabaseCacheEntry> databaseCache =
            new Dictionary<string, DatabaseCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ArchiveCacheEntry> archiveCache =
            new Dictionary<string, ArchiveCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ArchiveCacheEntry> archiveHeaderCache =
            new Dictionary<string, ArchiveCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> thumbnailArchivePathCache =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> providerContentRootCache =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly SongEditService editService;
        private readonly string modsFolder;
        private readonly Lazy<GlobalSpriteDatabaseIndex> globalDatabaseIndex;
        private readonly Lazy<GlobalLooseArchiveIndex> globalLooseArchiveIndex;
        private string[] globalDatabasePaths;
        private int globalDatabaseIndexBuildCount;
        private int archiveSnapshotBuildCount;

        public SongArtworkService(string modsFolder, SongEditService editService = null)
        {
            this.modsFolder = NormalizePath(modsFolder);
            this.editService = editService ?? new SongEditService();
            globalDatabaseIndex = new Lazy<GlobalSpriteDatabaseIndex>(
                BuildGlobalDatabaseIndex,
                LazyThreadSafetyMode.ExecutionAndPublication);
            globalLooseArchiveIndex = new Lazy<GlobalLooseArchiveIndex>(
                BuildGlobalLooseArchiveIndex,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal int GlobalDatabaseIndexBuildCount =>
            Volatile.Read(ref globalDatabaseIndexBuildCount);
        internal int ArchiveSnapshotBuildCount =>
            Volatile.Read(ref archiveSnapshotBuildCount);

        public Task<SongArtworkPreview> LoadPreviewAsync(
            SongEntry song,
            SongArtworkKind kind,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => LoadPreview(song, kind, cancellationToken), cancellationToken);
        }

        public bool ProbeAvailability(
            SongEntry song,
            SongArtworkKind kind,
            CancellationToken cancellationToken = default)
        {
            if (song == null)
                return false;

            try
            {
                var location = ResolveLocation(song, kind, false, cancellationToken);
                if (location == null)
                    return false;

                if (location.SpriteIndex >= 0 || !String.IsNullOrWhiteSpace(location.SpriteName))
                    return true;

                var archive = ReadArchiveSnapshot(location.ArchivePath);
                if (!archive.TryGetSpriteSet(location.InternalFileName, out var spriteSet))
                    return false;
                var sprite = SelectSprite(spriteSet, location, song.PvId, kind);
                ValidateSprite(spriteSet, sprite);
                return true;
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                return false;
            }
        }

        public SongArtworkAvailability ProbeAvailability(
            SongEntry song,
            CancellationToken cancellationToken = default)
        {
            if (song == null)
                return new SongArtworkAvailability(false, false, false);

            return new SongArtworkAvailability(
                ProbeAvailability(song, SongArtworkKind.Thumbnail, cancellationToken),
                ProbeAvailability(song, SongArtworkKind.Jacket, cancellationToken),
                ProbeAvailability(song, SongArtworkKind.Background, cancellationToken));
        }

        public IReadOnlyDictionary<SongEntry, SongArtworkAvailability> ProbeAvailability(
            IEnumerable<SongEntry> songs,
            CancellationToken cancellationToken = default)
        {
            var entries = (songs ?? Enumerable.Empty<SongEntry>())
                .Where(song => song != null)
                .Distinct()
                .ToArray();
            var archivePaths = CollectPotentialArchivePaths(entries, cancellationToken);
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount))
            };
            Parallel.ForEach(
                archivePaths,
                parallelOptions,
                path => ReadArchiveHeaderSnapshot(path));

            var availability = new Dictionary<SongEntry, SongArtworkAvailability>();
            foreach (var song in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                availability.Add(song, ProbeAvailability(song, cancellationToken));
            }
            return availability;
        }

        public async Task<SongEditResult> ReplaceAsync(
            SongEntry song,
            SongArtworkKind kind,
            string imagePath,
            CancellationToken cancellationToken = default)
        {
            if (song == null)
                return SongEditResult.Failed("没有选中歌曲。");
            if (String.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return SongEditResult.Failed("请选择存在的 PNG、JPEG 或 BMP 图片。");

            PreparedReplacement replacement;
            try
            {
                replacement = await Task.Run(
                    () => PrepareReplacement(song, kind, imagePath, cancellationToken),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return SongEditResult.Failed($"无法生成 {GetKindDisplayName(kind)}：{UserErrorMessage.From(exception)}");
            }

            return await editService.ReplaceArtworkArchiveAsync(
                song,
                replacement.Location.ArchivePath,
                replacement.Location.OwnerContentRoot,
                replacement.OriginalHash,
                replacement.ArchiveBytes,
                GetKindDisplayName(kind),
                cancellationToken);
        }

        private IReadOnlyList<string> CollectPotentialArchivePaths(
            IReadOnlyList<SongEntry> songs,
            CancellationToken cancellationToken)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var thumbnailDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var contentGroup in songs.GroupBy(song => song.ContentRoot, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var twoDDirectory = Path.Combine(contentGroup.Key ?? String.Empty, "rom", "2d");
                AddThumbnailArchives(twoDDirectory, paths, thumbnailDirectories);
                foreach (var song in contentGroup)
                    AddPerPvArchives(twoDDirectory, song, paths);

                foreach (var databasePath in EnumerateSpriteDatabases(contentGroup.Key))
                {
                    SpriteDatabaseSnapshot database;
                    try
                    {
                        database = ReadDatabase(databasePath);
                    }
                    catch
                    {
                        continue;
                    }
                    foreach (var song in contentGroup)
                    {
                        foreach (var kind in ArtworkKinds)
                        {
                            foreach (var match in FindDatabaseMatches(database, song, kind))
                                AddSetArchive(twoDDirectory, match.SetFileName, paths);
                        }
                    }
                }
            }

            foreach (var song in songs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var looseArchive in GetGlobalLooseArchiveCandidates(song))
                    paths.Add(looseArchive.Path);
                foreach (var kind in ArtworkKinds)
                {
                    foreach (var database in GetGlobalDatabaseCandidates(song, kind))
                    {
                        var twoDDirectory = Path.GetDirectoryName(database.Path);
                        foreach (var match in FindDatabaseMatches(database.Snapshot, song, kind))
                            AddSetArchive(twoDDirectory, match.SetFileName, paths);
                        if (kind == SongArtworkKind.Thumbnail)
                            AddThumbnailArchives(twoDDirectory, paths, thumbnailDirectories);
                        else
                            AddPerPvArchives(twoDDirectory, song, paths);
                    }
                }
            }
            return paths.ToArray();
        }

        private void AddThumbnailArchives(
            string twoDDirectory,
            ISet<string> paths,
            ISet<string> visitedDirectories)
        {
            if (String.IsNullOrWhiteSpace(twoDDirectory) || !visitedDirectories.Add(twoDDirectory))
                return;
            foreach (var path in GetThumbnailArchivePaths(twoDDirectory))
                paths.Add(path);
        }

        private static void AddPerPvArchives(
            string twoDDirectory,
            SongEntry song,
            ISet<string> paths)
        {
            if (String.IsNullOrWhiteSpace(twoDDirectory))
                return;
            foreach (var rawId in GetRawIds(song))
            {
                var path = Path.Combine(twoDDirectory, $"spr_sel_pv{rawId}.farc");
                if (File.Exists(path))
                    paths.Add(path);
            }
        }

        private static void AddSetArchive(
            string twoDDirectory,
            string setFileName,
            ISet<string> paths)
        {
            if (String.IsNullOrWhiteSpace(twoDDirectory))
                return;
            var normalizedSetName = Path.GetFileName(setFileName ?? String.Empty);
            if (String.IsNullOrWhiteSpace(normalizedSetName))
                return;
            var path = Path.Combine(
                twoDDirectory,
                Path.GetFileNameWithoutExtension(normalizedSetName) + ".farc");
            if (File.Exists(path))
                paths.Add(path);
        }

        private static readonly SongArtworkKind[] ArtworkKinds =
        {
            SongArtworkKind.Thumbnail,
            SongArtworkKind.Jacket,
            SongArtworkKind.Background
        };

        public static string GetKindDisplayName(SongArtworkKind kind)
        {
            return kind switch
            {
                SongArtworkKind.Thumbnail => "歌曲小图标",
                SongArtworkKind.Jacket => "歌曲封面",
                SongArtworkKind.Background => "歌曲背景",
                _ => "歌曲图片"
            };
        }

        private SongArtworkPreview LoadPreview(
            SongEntry song,
            SongArtworkKind kind,
            CancellationToken cancellationToken)
        {
            if (song == null)
                return SongArtworkPreview.Missing(kind, "没有选中歌曲。");

            cancellationToken.ThrowIfCancellationRequested();
            var location = ResolveLocation(song, kind, false, cancellationToken);
            if (location == null)
            {
                return SongArtworkPreview.Missing(
                    kind,
                    $"未在歌曲模组或其他已安装模组中找到 {GetKindDisplayName(kind)}。");
            }

            try
            {
                using var archiveSource = OpenArchiveSource(location.ArchivePath);
                using var archive = BinaryFile.Load<FarcArchive>(archiveSource, true);
                using var entryStream = archive.Open(location.InternalFileName, EntryStreamMode.MemoryStream);
                using var spriteSet = BinaryFile.Load<SpriteSet>(entryStream, true);
                var sprite = SelectSprite(spriteSet, location, song.PvId, kind);
                ValidateSprite(spriteSet, sprite);

                var texture = spriteSet.TextureSet.Textures[(int)sprite.TextureIndex];
                using var atlas = DecodeTexture(texture);
                atlas.RotateFlip(RotateFlipType.Rotate180FlipX);
                var rectangle = GetSpriteRectangle(sprite, atlas.Width, atlas.Height);
                using var cropped = atlas.Clone(rectangle, DrawingPixelFormat.Format32bppArgb);
                using var output = new MemoryStream();
                cropped.Save(output, ImageFormat.Png);

                var message = location.CanReplace
                    ? $"来源：{location.SourceDescription}"
                    : $"来源：{location.SourceDescription}。该图片由其他模组提供，只能预览。";
                return new SongArtworkPreview(
                    kind,
                    output.ToArray(),
                    cropped.Width,
                    cropped.Height,
                    location.SourceDescription,
                    location.ArchivePath,
                    location.CanReplace,
                    message);
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                return SongArtworkPreview.Missing(
                    kind,
                    $"{GetKindDisplayName(kind)}存在，但无法解析：{UserErrorMessage.From(exception)}");
            }
        }

        private PreparedReplacement PrepareReplacement(
            SongEntry song,
            SongArtworkKind kind,
            string imagePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = ResolveLocation(song, kind, true, cancellationToken);
            if (location == null)
            {
                throw new FileNotFoundException(
                    $"歌曲所属模组没有提供可编辑的 {GetKindDisplayName(kind)}。" +
                    "管理器不会改写其他模组或 Thumbnail Manager 的生成缓存。");
            }

            var originalHash = ComputeSha256(location.ArchivePath);
            using var archiveSource = OpenArchiveSource(location.ArchivePath);
            using var archive = BinaryFile.Load<FarcArchive>(archiveSource, true);
            using var entryStream = archive.Open(location.InternalFileName, EntryStreamMode.MemoryStream);
            using var spriteSet = BinaryFile.Load<SpriteSet>(entryStream, true);
            var sprite = SelectSprite(spriteSet, location, song.PvId, kind);
            ValidateSprite(spriteSet, sprite);

            var textureIndex = (int)sprite.TextureIndex;
            var originalTexture = spriteSet.TextureSet.Textures[textureIndex];
            using var atlas = DecodeTexture(originalTexture);
            atlas.RotateFlip(RotateFlipType.Rotate180FlipX);
            var rectangle = GetSpriteRectangle(sprite, atlas.Width, atlas.Height);
            var protectedRectangles = GetProtectedSpriteRectangles(
                spriteSet,
                sprite,
                textureIndex,
                rectangle,
                atlas.Width,
                atlas.Height);
            var storedRectangle = new Rectangle(
                rectangle.X,
                atlas.Height - rectangle.Bottom,
                rectangle.Width,
                rectangle.Height);
            var forkTexture = RequiresTextureFork(
                originalTexture,
                storedRectangle,
                protectedRectangles);

            using (var source = LoadInputBitmap(imagePath))
                DrawImageCover(atlas, source, rectangle);
            var compressionPadding = originalTexture.IsYCbCr
                ? 10
                : TextureFormatUtilities.IsBlockCompressed(originalTexture.Format) ? 4 : 0;
            ExtendSpriteEdges(atlas, rectangle, compressionPadding);

            atlas.RotateFlip(RotateFlipType.Rotate180FlipX);
            cancellationToken.ThrowIfCancellationRequested();
            var replacementTexture = EncodeTexture(
                atlas,
                originalTexture,
                storedRectangle,
                forkTexture ? Array.Empty<Rectangle>() : protectedRectangles,
                cancellationToken);
            replacementTexture.Name = originalTexture.Name;
            if (forkTexture)
            {
                replacementTexture.Id = GetUnusedTextureId(spriteSet);
                var replacementTextureIndex = (uint)spriteSet.TextureSet.Textures.Count;
                foreach (var candidate in spriteSet.Sprites.Where(candidate =>
                    candidate.TextureIndex == textureIndex))
                {
                    try
                    {
                        if (GetSpriteRectangle(candidate, atlas.Width, atlas.Height) == rectangle)
                            candidate.TextureIndex = replacementTextureIndex;
                    }
                    catch (InvalidDataException)
                    {
                    }
                }
                spriteSet.TextureSet.Textures.Add(replacementTexture);
            }
            else
            {
                replacementTexture.Id = originalTexture.Id;
                spriteSet.TextureSet.Textures[textureIndex] = replacementTexture;
            }

            using var spriteSetBytes = new MemoryStream();
            spriteSet.Save(spriteSetBytes, true);
            spriteSetBytes.Position = 0;
            archive.Add(location.InternalFileName, spriteSetBytes, true, ConflictPolicy.Replace);

            using var archiveBytes = new MemoryStream();
            archive.Save(archiveBytes, true);
            return new PreparedReplacement(location, originalHash, archiveBytes.ToArray());
        }

        private ArtworkLocation ResolveLocation(
            SongEntry song,
            SongArtworkKind kind,
            bool localOnly,
            CancellationToken cancellationToken)
        {
            var localDatabases = EnumerateSpriteDatabases(song.ContentRoot).ToArray();
            var localDatabaseMatched = false;
            foreach (var databasePath in localDatabases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = ResolveFromDatabase(
                    song,
                    kind,
                    databasePath,
                    true,
                    cancellationToken,
                    out var databaseMatched);
                localDatabaseMatched |= databaseMatched;
                if (location != null)
                    return location;
            }

            var fallback = localDatabaseMatched
                ? null
                : ResolveWithoutDatabase(song, kind, song.ContentRoot, true, cancellationToken);
            if (fallback != null || localOnly || String.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder))
                return fallback;

            foreach (var indexedDatabase in GetGlobalDatabaseCandidates(song, kind))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (localDatabases.Any(local => PathsEqual(local, indexedDatabase.Path)))
                    continue;

                var location = ResolveFromDatabase(
                    song,
                    kind,
                    indexedDatabase.Path,
                    false,
                    cancellationToken,
                    indexedDatabase.Snapshot,
                    out _);
                if (location != null)
                    return location;
            }

            if (kind != SongArtworkKind.Thumbnail)
            {
                foreach (var looseArchive in GetGlobalLooseArchiveCandidates(song))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsPathUnder(song.ContentRoot, looseArchive.Path))
                        continue;
                    var location = ResolveWithoutDatabase(
                        song,
                        kind,
                        looseArchive.ContentRoot,
                        false,
                        cancellationToken);
                    if (location != null)
                        return location;
                }
            }
            return null;
        }

        private ArtworkLocation ResolveFromDatabase(
            SongEntry song,
            SongArtworkKind kind,
            string databasePath,
            bool isLocal,
            CancellationToken cancellationToken,
            out bool databaseMatched)
        {
            databaseMatched = false;
            SpriteDatabaseSnapshot database;
            try
            {
                database = ReadDatabase(databasePath);
            }
            catch
            {
                return null;
            }

            return ResolveFromDatabase(
                song,
                kind,
                databasePath,
                isLocal,
                cancellationToken,
                database,
                out databaseMatched);
        }

        private ArtworkLocation ResolveFromDatabase(
            SongEntry song,
            SongArtworkKind kind,
            string databasePath,
            bool isLocal,
            CancellationToken cancellationToken,
            SpriteDatabaseSnapshot database,
            out bool databaseMatched)
        {
            databaseMatched = false;

            var matches = FindDatabaseMatches(database, song, kind).ToArray();
            databaseMatched = matches.Length > 0;
            foreach (var match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var twoDDirectory = Path.GetDirectoryName(databasePath);
                var archive = FindArchive(
                    twoDDirectory,
                    match.SetFileName,
                    song,
                    kind,
                    cancellationToken,
                    out var internalFileName);
                if (String.IsNullOrWhiteSpace(archive))
                    continue;

                var contentRoot = GetContentRootFromDatabase(databasePath);
                var canReplace = isLocal &&
                    IsPathUnder(song.ModRoot, contentRoot) &&
                    !IsThumbnailManagerPath(archive);
                return new ArtworkLocation(
                    kind,
                    archive,
                    internalFileName,
                    match.Index,
                    match.Name,
                    contentRoot,
                    GetProviderName(archive),
                    canReplace);
            }
            return null;
        }

        private ArtworkLocation ResolveWithoutDatabase(
            SongEntry song,
            SongArtworkKind kind,
            string contentRoot,
            bool canReplace,
            CancellationToken cancellationToken)
        {
            var twoDDirectory = Path.Combine(contentRoot, "rom", "2d");
            if (!Directory.Exists(twoDDirectory))
                return null;

            var candidates = new List<string>();
            if (kind == SongArtworkKind.Thumbnail)
            {
                candidates.AddRange(GetThumbnailArchivePaths(twoDDirectory));
            }
            else
            {
                foreach (var rawId in GetRawIds(song))
                    candidates.Add(Path.Combine(twoDDirectory, $"spr_sel_pv{rawId}.farc"));
            }

            foreach (var archivePath in candidates
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var archive = ReadArchiveSnapshot(archivePath);
                    foreach (var internalName in archive.FileNames.Where(IsSpriteSetFile))
                    {
                        if (!archive.TryGetSpriteSet(internalName, out var spriteSet))
                            continue;
                        var temporary = new ArtworkLocation(
                            kind,
                            archivePath,
                            internalName,
                            -1,
                            String.Empty,
                            contentRoot,
                            GetProviderName(archivePath),
                            canReplace && !IsThumbnailManagerPath(archivePath));
                        var sprite = SelectSprite(spriteSet, temporary, song.PvId, kind, false);
                        if (sprite != null)
                            return temporary;
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private SpriteDatabaseSnapshot ReadDatabase(string path)
        {
            var file = new FileInfo(path);
            lock (cacheLock)
            {
                if (databaseCache.TryGetValue(path, out var cached) &&
                    cached.Length == file.Length &&
                    cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
                {
                    return cached.Snapshot;
                }
            }

            using var database = BinaryFile.Load<SpriteDatabase>(path);
            var entries = database.SpriteSets
                .SelectMany(set => set.Sprites.Select(sprite => new SpriteDatabaseEntry(
                    sprite.Name,
                    set.FileName,
                    sprite.Index)))
                .ToArray();
            var snapshot = new SpriteDatabaseSnapshot(entries);
            lock (cacheLock)
            {
                databaseCache[path] = new DatabaseCacheEntry(
                    file.Length,
                    file.LastWriteTimeUtc,
                    snapshot);
            }
            return snapshot;
        }

        private ArchiveStructureSnapshot ReadArchiveSnapshot(string path)
        {
            var file = new FileInfo(path);
            lock (cacheLock)
            {
                if (archiveCache.TryGetValue(path, out var cached) &&
                    cached.Length == file.Length &&
                    cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
                {
                    return cached.Snapshot;
                }
            }

            ArchiveStructureSnapshot snapshot;
            Interlocked.Increment(ref archiveSnapshotBuildCount);
            try
            {
                using var archiveSource = OpenArchiveSource(path);
                using var archive = BinaryFile.Load<FarcArchive>(archiveSource, true);
                var fileNames = archive.FileNames.ToArray();
                var spriteSets = new Dictionary<string, SpriteSetStructureSnapshot>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var internalName in fileNames.Where(IsSpriteSetFile))
                {
                    try
                    {
                        using var entryStream = archive.Open(internalName, EntryStreamMode.MemoryStream);
                        using var spriteSet = BinaryFile.Load<SpriteSet>(entryStream, true);
                        var sprites = spriteSet.Sprites
                            .Select(sprite => new SpriteStructureSnapshot(
                                sprite.Name,
                                sprite.TextureIndex,
                                sprite.X,
                                sprite.Y,
                                sprite.Width,
                                sprite.Height))
                            .ToArray();
                        var textures = spriteSet.TextureSet.Textures
                            .Select(texture => new TextureStructureSnapshot(
                                texture.Width,
                                texture.Height))
                            .ToArray();
                        spriteSets[internalName] = new SpriteSetStructureSnapshot(sprites, textures);
                    }
                    catch
                    {
                    }
                }
                snapshot = new ArchiveStructureSnapshot(fileNames, spriteSets);
            }
            catch
            {
                snapshot = ArchiveStructureSnapshot.Empty;
            }

            lock (cacheLock)
            {
                archiveCache[path] = new ArchiveCacheEntry(
                    file.Length,
                    file.LastWriteTimeUtc,
                    snapshot);
            }
            return snapshot;
        }

        private ArchiveStructureSnapshot ReadArchiveHeaderSnapshot(string path)
        {
            var file = new FileInfo(path);
            lock (cacheLock)
            {
                if (archiveHeaderCache.TryGetValue(path, out var cached) &&
                    cached.Length == file.Length &&
                    cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
                {
                    return cached.Snapshot;
                }
            }

            ArchiveStructureSnapshot snapshot;
            try
            {
                using var archiveSource = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var archive = BinaryFile.Load<FarcArchive>(archiveSource, true);
                snapshot = new ArchiveStructureSnapshot(
                    archive.FileNames.ToArray(),
                    new Dictionary<string, SpriteSetStructureSnapshot>(StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                snapshot = ArchiveStructureSnapshot.Empty;
            }

            lock (cacheLock)
            {
                archiveHeaderCache[path] = new ArchiveCacheEntry(
                    file.Length,
                    file.LastWriteTimeUtc,
                    snapshot);
            }
            return snapshot;
        }

        private IReadOnlyList<string> GetThumbnailArchivePaths(string twoDDirectory)
        {
            lock (cacheLock)
            {
                if (thumbnailArchivePathCache.TryGetValue(twoDDirectory, out var cached))
                    return cached;
            }

            string[] paths;
            try
            {
                paths = Directory
                    .EnumerateFiles(
                        twoDDirectory,
                        "spr_sel_pvtmb*.farc",
                        SearchOption.TopDirectoryOnly)
                    .ToArray();
            }
            catch
            {
                paths = Array.Empty<string>();
            }

            lock (cacheLock)
            {
                if (!thumbnailArchivePathCache.TryGetValue(twoDDirectory, out var cached))
                {
                    thumbnailArchivePathCache.Add(twoDDirectory, paths);
                    return paths;
                }
                return cached;
            }
        }

        private IReadOnlyList<string> GetGlobalDatabasePaths()
        {
            lock (cacheLock)
            {
                if (globalDatabasePaths != null)
                    return globalDatabasePaths;
            }

            string[] paths;
            try
            {
                paths = Directory
                    .EnumerateFiles(modsFolder, "mod_spr_db.bin", SearchOption.AllDirectories)
                    .Where(path => path.IndexOf(
                        Path.DirectorySeparatorChar + "Backups" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) < 0)
                    .Where(IsProviderEnabled)
                    .OrderBy(path => IsThumbnailManagerPath(path) ? 1 : 0)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                paths = Array.Empty<string>();
            }

            lock (cacheLock)
            {
                globalDatabasePaths ??= paths;
                return globalDatabasePaths;
            }
        }

        private bool IsProviderEnabled(string resourcePath)
        {
            if (String.IsNullOrWhiteSpace(modsFolder) || !IsPathUnder(modsFolder, resourcePath))
                return true;

            try
            {
                var relative = Path.GetRelativePath(modsFolder, resourcePath);
                var providerName = relative
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .FirstOrDefault();
                if (String.IsNullOrWhiteSpace(providerName))
                    return true;
                return GetEnabledProviderContentRoots(Path.Combine(modsFolder, providerName))
                    .Any(root => IsPathUnder(root, resourcePath));
            }
            catch
            {
                return false;
            }
        }

        private IReadOnlyList<string> GetEnabledProviderContentRoots(string providerRoot)
        {
            lock (cacheLock)
            {
                if (providerContentRootCache.TryGetValue(providerRoot, out var cached))
                    return cached;
            }

            string[] roots;
            try
            {
                var configPath = Path.Combine(providerRoot, "config.toml");
                if (!File.Exists(configPath))
                {
                    roots = new[] { NormalizePath(providerRoot) };
                }
                else if (!Toml.TryToModel(
                    File.ReadAllText(configPath),
                    out TomlTable config,
                    out _) ||
                    (config.TryGetValue("enabled", out var enabledValue) &&
                        enabledValue is bool enabled && !enabled))
                {
                    roots = Array.Empty<string>();
                }
                else if (!config.TryGetValue("include", out var includeValue))
                {
                    roots = new[] { NormalizePath(providerRoot) };
                }
                else if (includeValue is string || includeValue is not System.Collections.IEnumerable includes)
                {
                    roots = Array.Empty<string>();
                }
                else
                {
                    roots = includes
                        .Cast<object>()
                        .OfType<string>()
                        .Select(include => NormalizePath(Path.Combine(
                            providerRoot,
                            include.Replace('/', Path.DirectorySeparatorChar)
                                .Replace('\\', Path.DirectorySeparatorChar))))
                        .Where(root => IsPathUnder(providerRoot, root))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
            catch
            {
                roots = Array.Empty<string>();
            }

            lock (cacheLock)
            {
                providerContentRootCache[providerRoot] = roots;
            }
            return roots;
        }

        private GlobalSpriteDatabaseIndex BuildGlobalDatabaseIndex()
        {
            Interlocked.Increment(ref globalDatabaseIndexBuildCount);
            var lookup = new Dictionary<ArtworkLookupKey, List<IndexedSpriteDatabase>>();
            var order = 0;
            foreach (var path in GetGlobalDatabasePaths())
            {
                SpriteDatabaseSnapshot snapshot;
                try
                {
                    snapshot = ReadDatabase(path);
                }
                catch
                {
                    order++;
                    continue;
                }

                var indexedDatabase = new IndexedSpriteDatabase(path, snapshot, order++);
                foreach (var key in snapshot.Keys)
                {
                    if (!lookup.TryGetValue(key, out var databases))
                    {
                        databases = new List<IndexedSpriteDatabase>();
                        lookup.Add(key, databases);
                    }
                    databases.Add(indexedDatabase);
                }
            }

            return new GlobalSpriteDatabaseIndex(lookup.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<IndexedSpriteDatabase>)pair.Value.ToArray()));
        }

        private GlobalLooseArchiveIndex BuildGlobalLooseArchiveIndex()
        {
            var lookup = new Dictionary<int, List<LooseArchiveLocation>>();
            IEnumerable<string> providerRoots;
            try
            {
                providerRoots = Directory
                    .EnumerateDirectories(modsFolder, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                providerRoots = Array.Empty<string>();
            }

            foreach (var providerRoot in providerRoots)
            {
                foreach (var contentRoot in GetEnabledProviderContentRoots(providerRoot))
                {
                    var twoDDirectory = Path.Combine(contentRoot, "rom", "2d");
                    if (!Directory.Exists(twoDDirectory))
                        continue;
                    IEnumerable<string> archivePaths;
                    try
                    {
                        archivePaths = Directory
                            .EnumerateFiles(
                                twoDDirectory,
                                "spr_sel_pv*.farc",
                                SearchOption.TopDirectoryOnly)
                            .ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var archivePath in archivePaths)
                    {
                        var stem = Path.GetFileNameWithoutExtension(archivePath);
                        const string prefix = "spr_sel_pv";
                        if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                            !Int32.TryParse(
                                stem.Substring(prefix.Length),
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var pvId))
                        {
                            continue;
                        }
                        if (!lookup.TryGetValue(pvId, out var matches))
                        {
                            matches = new List<LooseArchiveLocation>();
                            lookup.Add(pvId, matches);
                        }
                        matches.Add(new LooseArchiveLocation(archivePath, contentRoot));
                    }
                }
            }
            return new GlobalLooseArchiveIndex(lookup.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<LooseArchiveLocation>)pair.Value.ToArray()));
        }

        private IReadOnlyList<LooseArchiveLocation> GetGlobalLooseArchiveCandidates(SongEntry song)
        {
            var expectedNames = new HashSet<string>(
                GetRawIds(song).Select(rawId => $"spr_sel_pv{rawId}.farc"),
                StringComparer.OrdinalIgnoreCase);
            return globalLooseArchiveIndex.Value
                .Get(song.PvId)
                .Where(location => expectedNames.Contains(Path.GetFileName(location.Path)))
                .ToArray();
        }

        private IReadOnlyList<IndexedSpriteDatabase> GetGlobalDatabaseCandidates(
            SongEntry song,
            SongArtworkKind kind)
        {
            var index = globalDatabaseIndex.Value;
            var numericIds = GetNumericLookupIds(song);
            if (numericIds.Count == 1)
                return index.Get(kind, numericIds[0]);

            var matches = new Dictionary<string, IndexedSpriteDatabase>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in numericIds)
            {
                foreach (var database in index.Get(kind, id))
                    matches.TryAdd(database.Path, database);
            }
            return matches.Values
                .OrderBy(database => database.Order)
                .ToArray();
        }

        private static IEnumerable<SpriteDatabaseEntry> FindDatabaseMatches(
            SpriteDatabaseSnapshot database,
            SongEntry song,
            SongArtworkKind kind)
        {
            var ids = GetRawIds(song);
            var expectedNames = new List<string>();
            foreach (var id in ids)
            {
                switch (kind)
                {
                    case SongArtworkKind.Thumbnail:
                        expectedNames.Add("SPR_SEL_PVTMB_" + id);
                        break;
                    case SongArtworkKind.Jacket:
                        expectedNames.Add($"SPR_SEL_PV{id}_SONG_JK{id}");
                        break;
                    case SongArtworkKind.Background:
                        expectedNames.Add($"SPR_SEL_PV{id}_SONG_BG{id}");
                        break;
                }
            }

            var indexedCandidates = GetNumericLookupIds(song)
                .SelectMany(id => database.Get(kind, id))
                .Distinct()
                .ToArray();
            var exact = indexedCandidates
                .Where(entry => expectedNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (exact.Length > 0)
                return exact;

            var marker = kind switch
            {
                SongArtworkKind.Thumbnail => "SPR_SEL_PVTMB_",
                SongArtworkKind.Jacket => "_SONG_JK",
                SongArtworkKind.Background => "_SONG_BG",
                _ => String.Empty
            };
            return database.Get(kind, song.PvId).Where(entry =>
                entry.Name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0 &&
                TryGetTrailingId(entry.Name, out var id) &&
                id == song.PvId);
        }

        private static IReadOnlyList<int> GetNumericLookupIds(SongEntry song)
        {
            var ids = new List<int> { song.PvId };
            foreach (var rawId in GetRawIds(song))
            {
                if (Int32.TryParse(
                    rawId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var numericId) &&
                    !ids.Contains(numericId))
                {
                    ids.Add(numericId);
                }
            }
            return ids;
        }

        private static bool TryCreateArtworkLookupKey(
            SpriteDatabaseEntry entry,
            out ArtworkLookupKey key)
        {
            key = default;
            if (!TryGetTrailingId(entry.Name, out var pvId))
                return false;

            SongArtworkKind kind;
            if (entry.Name.IndexOf("SPR_SEL_PVTMB_", StringComparison.OrdinalIgnoreCase) >= 0)
                kind = SongArtworkKind.Thumbnail;
            else if (entry.Name.IndexOf("_SONG_JK", StringComparison.OrdinalIgnoreCase) >= 0)
                kind = SongArtworkKind.Jacket;
            else if (entry.Name.IndexOf("_SONG_BG", StringComparison.OrdinalIgnoreCase) >= 0)
                kind = SongArtworkKind.Background;
            else
                return false;

            key = new ArtworkLookupKey(kind, pvId);
            return true;
        }

        private string FindArchive(
            string twoDDirectory,
            string setFileName,
            SongEntry song,
            SongArtworkKind kind,
            CancellationToken cancellationToken,
            out string internalFileName)
        {
            internalFileName = null;
            if (String.IsNullOrWhiteSpace(twoDDirectory) || !Directory.Exists(twoDDirectory))
                return null;

            var normalizedSetName = Path.GetFileName(setFileName ?? String.Empty);
            if (String.IsNullOrWhiteSpace(Path.GetExtension(normalizedSetName)))
                normalizedSetName += ".bin";
            var setStem = Path.GetFileNameWithoutExtension(normalizedSetName);
            var candidates = new List<string>
            {
                Path.Combine(twoDDirectory, setStem + ".farc")
            };
            if (kind == SongArtworkKind.Thumbnail)
            {
                candidates.AddRange(GetThumbnailArchivePaths(twoDDirectory));
            }
            else
            {
                foreach (var rawId in GetRawIds(song))
                    candidates.Add(Path.Combine(twoDDirectory, $"spr_sel_pv{rawId}.farc"));
            }

            foreach (var candidate in candidates
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var archive = ReadArchiveHeaderSnapshot(candidate);
                    var entry = archive.FileNames.FirstOrDefault(name =>
                        Path.GetFileName(name).Equals(normalizedSetName, StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                        continue;

                    internalFileName = entry;
                    return candidate;
                }
                catch
                {
                }
            }
            return null;
        }

        private static Sprite SelectSprite(
            SpriteSet spriteSet,
            ArtworkLocation location,
            int pvId,
            SongArtworkKind kind,
            bool throwWhenMissing = true)
        {
            var expectedName = location.SpriteName;
            var byName = spriteSet.Sprites.FirstOrDefault(sprite =>
                !String.IsNullOrWhiteSpace(sprite.Name) &&
                !String.IsNullOrWhiteSpace(expectedName) &&
                sprite.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
                return byName;

            var hasDatabaseIdentity = location.SpriteIndex >= 0 ||
                !String.IsNullOrWhiteSpace(expectedName);
            if (hasDatabaseIdentity)
            {
                if (location.SpriteIndex >= 0 && location.SpriteIndex < spriteSet.Sprites.Count)
                {
                    var indexed = spriteSet.Sprites[location.SpriteIndex];
                    if (IsCompatibleSpriteName(indexed.Name, expectedName, pvId, kind))
                        return indexed;
                }

                if (!throwWhenMissing)
                    return null;
                throw new InvalidDataException(
                    $"数据库指定的 SpriteSet 中没有找到 PV {pvId} 的 {GetKindDisplayName(kind)}。");
            }

            var marker = kind switch
            {
                SongArtworkKind.Jacket => "SONG_JK",
                SongArtworkKind.Background => "SONG_BG",
                _ => String.Empty
            };
            if (!String.IsNullOrWhiteSpace(marker))
            {
                byName = spriteSet.Sprites.FirstOrDefault(sprite =>
                    !String.IsNullOrWhiteSpace(sprite.Name) &&
                    sprite.Name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    TryGetTrailingId(sprite.Name, out var id) &&
                    id == pvId);
                if (byName != null)
                    return byName;
            }
            else
            {
                byName = spriteSet.Sprites.FirstOrDefault(sprite =>
                    TryGetTrailingId(sprite.Name, out var id) && id == pvId);
                if (byName != null)
                    return byName;
            }

            if (!throwWhenMissing)
                return null;
            throw new InvalidDataException($"SpriteSet 中没有找到 PV {pvId} 的 {GetKindDisplayName(kind)}。");
        }

        private static SpriteStructureSnapshot SelectSprite(
            SpriteSetStructureSnapshot spriteSet,
            ArtworkLocation location,
            int pvId,
            SongArtworkKind kind,
            bool throwWhenMissing = true)
        {
            var expectedName = location.SpriteName;
            var byName = spriteSet.Sprites.FirstOrDefault(sprite =>
                !String.IsNullOrWhiteSpace(sprite.Name) &&
                !String.IsNullOrWhiteSpace(expectedName) &&
                sprite.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
                return byName;

            var hasDatabaseIdentity = location.SpriteIndex >= 0 ||
                !String.IsNullOrWhiteSpace(expectedName);
            if (hasDatabaseIdentity)
            {
                if (location.SpriteIndex >= 0 && location.SpriteIndex < spriteSet.Sprites.Count)
                {
                    var indexed = spriteSet.Sprites[location.SpriteIndex];
                    if (IsCompatibleSpriteName(indexed.Name, expectedName, pvId, kind))
                        return indexed;
                }

                if (!throwWhenMissing)
                    return null;
                throw new InvalidDataException(
                    $"数据库指定的 SpriteSet 中没有找到 PV {pvId} 的 {GetKindDisplayName(kind)}。");
            }

            var marker = kind switch
            {
                SongArtworkKind.Jacket => "SONG_JK",
                SongArtworkKind.Background => "SONG_BG",
                _ => String.Empty
            };
            if (!String.IsNullOrWhiteSpace(marker))
            {
                byName = spriteSet.Sprites.FirstOrDefault(sprite =>
                    !String.IsNullOrWhiteSpace(sprite.Name) &&
                    sprite.Name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    TryGetTrailingId(sprite.Name, out var id) &&
                    id == pvId);
                if (byName != null)
                    return byName;
            }
            else
            {
                byName = spriteSet.Sprites.FirstOrDefault(sprite =>
                    TryGetTrailingId(sprite.Name, out var id) && id == pvId);
                if (byName != null)
                    return byName;
            }

            if (!throwWhenMissing)
                return null;
            throw new InvalidDataException($"SpriteSet 中没有找到 PV {pvId} 的 {GetKindDisplayName(kind)}。");
        }

        private static bool IsCompatibleSpriteName(
            string actualName,
            string expectedName,
            int pvId,
            SongArtworkKind kind)
        {
            if (String.IsNullOrWhiteSpace(actualName))
                return true;
            if (!String.IsNullOrWhiteSpace(expectedName) &&
                actualName.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!TryGetTrailingId(actualName, out var id) || id != pvId)
                return false;
            return kind switch
            {
                SongArtworkKind.Thumbnail => true,
                SongArtworkKind.Jacket => actualName.IndexOf(
                    "SONG_JK", StringComparison.OrdinalIgnoreCase) >= 0,
                SongArtworkKind.Background => actualName.IndexOf(
                    "SONG_BG", StringComparison.OrdinalIgnoreCase) >= 0,
                _ => false
            };
        }

        private static void ValidateSprite(SpriteSet spriteSet, Sprite sprite)
        {
            if (sprite == null)
                throw new InvalidDataException("目标 Sprite 不存在。");
            if (sprite.TextureIndex >= spriteSet.TextureSet.Textures.Count)
                throw new InvalidDataException("目标 Sprite 指向了不存在的纹理。");
            if (sprite.Width <= 0 || sprite.Height <= 0)
                throw new InvalidDataException("目标 Sprite 的尺寸无效。");
        }

        private static void ValidateSprite(
            SpriteSetStructureSnapshot spriteSet,
            SpriteStructureSnapshot sprite)
        {
            if (sprite == null)
                throw new InvalidDataException("目标 Sprite 不存在。");
            if (sprite.TextureIndex >= spriteSet.Textures.Count)
                throw new InvalidDataException("目标 Sprite 指向了不存在的纹理。");
            if (sprite.Width <= 0 || sprite.Height <= 0)
                throw new InvalidDataException("目标 Sprite 的尺寸无效。");
            var texture = spriteSet.Textures[(int)sprite.TextureIndex];
            GetSpriteRectangle(sprite, texture.Width, texture.Height);
        }

        private static Rectangle GetSpriteRectangle(Sprite sprite, int atlasWidth, int atlasHeight)
        {
            var left = (int)Math.Floor(sprite.X);
            var top = (int)Math.Floor(sprite.Y);
            var right = (int)Math.Ceiling(sprite.X + sprite.Width);
            var bottom = (int)Math.Ceiling(sprite.Y + sprite.Height);
            if (left < 0 || top < 0 || right > atlasWidth || bottom > atlasHeight || right <= left || bottom <= top)
                throw new InvalidDataException("目标 Sprite 超出了纹理边界。");
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private static Rectangle GetSpriteRectangle(
            SpriteStructureSnapshot sprite,
            int atlasWidth,
            int atlasHeight)
        {
            var left = (int)Math.Floor(sprite.X);
            var top = (int)Math.Floor(sprite.Y);
            var right = (int)Math.Ceiling(sprite.X + sprite.Width);
            var bottom = (int)Math.Ceiling(sprite.Y + sprite.Height);
            if (left < 0 || top < 0 || right > atlasWidth || bottom > atlasHeight || right <= left || bottom <= top)
                throw new InvalidDataException("目标 Sprite 超出了纹理边界。");
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private static IReadOnlyList<Rectangle> GetProtectedSpriteRectangles(
            SpriteSet spriteSet,
            Sprite selectedSprite,
            int textureIndex,
            Rectangle selectedRectangle,
            int atlasWidth,
            int atlasHeight)
        {
            var protectedRectangles = new List<Rectangle>();
            foreach (var sprite in spriteSet.Sprites)
            {
                if (ReferenceEquals(sprite, selectedSprite) ||
                    sprite.TextureIndex != textureIndex ||
                    sprite.Width <= 0 ||
                    sprite.Height <= 0)
                {
                    continue;
                }

                Rectangle logicalRectangle;
                try
                {
                    logicalRectangle = GetSpriteRectangle(sprite, atlasWidth, atlasHeight);
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                if (logicalRectangle == selectedRectangle)
                    continue;

                protectedRectangles.Add(new Rectangle(
                    logicalRectangle.X,
                    atlasHeight - logicalRectangle.Bottom,
                    logicalRectangle.Width,
                    logicalRectangle.Height));
            }
            return protectedRectangles;
        }

        private static uint GetUnusedTextureId(SpriteSet spriteSet)
        {
            var usedIds = new HashSet<uint>(spriteSet.TextureSet.Textures.Select(texture => texture.Id));
            for (uint candidate = 0; candidate < UInt32.MaxValue; candidate++)
            {
                if (!usedIds.Contains(candidate))
                    return candidate;
            }
            throw new InvalidDataException("SpriteSet 中没有可用的纹理 ID。");
        }

        private static bool RequiresTextureFork(
            Texture texture,
            Rectangle replacementRectangle,
            IReadOnlyList<Rectangle> protectedRectangles)
        {
            if (protectedRectangles.Count == 0)
                return false;
            if (!TextureFormatUtilities.IsBlockCompressed(texture.Format))
                return protectedRectangles.Any(replacementRectangle.IntersectsWith);

            if (texture.IsYCbCr)
            {
                if (HasSharedCompressedBlock(
                    texture[0, 0],
                    replacementRectangle,
                    protectedRectangles))
                {
                    return true;
                }

                var chroma = texture[0, 1];
                var blocksWide = (chroma.Width + 3) / 4;
                var blocksHigh = (chroma.Height + 3) / 4;
                for (var blockY = 0; blockY < blocksHigh; blockY++)
                {
                    var y = blockY * 4;
                    var height = Math.Min(4, chroma.Height - y);
                    for (var blockX = 0; blockX < blocksWide; blockX++)
                    {
                        var x = blockX * 4;
                        var width = Math.Min(4, chroma.Width - x);
                        var horizontalInfluence = GetSampleInfluence(
                            x,
                            width,
                            chroma.Width,
                            texture.Width);
                        var verticalInfluence = GetSampleInfluence(
                            y,
                            height,
                            chroma.Height,
                            texture.Height);
                        var influence = new Rectangle(
                            horizontalInfluence.Start,
                            verticalInfluence.Start,
                            horizontalInfluence.Length,
                            verticalInfluence.Length);
                        if (influence.IntersectsWith(replacementRectangle) &&
                            protectedRectangles.Any(influence.IntersectsWith))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            for (var level = 0; level < texture.MipMapCount; level++)
            {
                var subTexture = texture[0, level];
                var target = ScaleRectangleToCoverage(
                    replacementRectangle,
                    texture.Width,
                    texture.Height,
                    subTexture.Width,
                    subTexture.Height);
                var neighbors = protectedRectangles
                    .Select(rectangle => ScaleRectangleToCoverage(
                        rectangle,
                        texture.Width,
                        texture.Height,
                        subTexture.Width,
                        subTexture.Height))
                    .ToArray();
                if (HasSharedCompressedBlock(subTexture, target, neighbors))
                    return true;
            }
            return false;
        }

        private static bool HasSharedCompressedBlock(
            SubTexture texture,
            Rectangle replacementRectangle,
            IReadOnlyList<Rectangle> protectedRectangles)
        {
            var blocksWide = (texture.Width + 3) / 4;
            var blocksHigh = (texture.Height + 3) / 4;
            for (var blockY = 0; blockY < blocksHigh; blockY++)
            {
                var y = blockY * 4;
                var height = Math.Min(4, texture.Height - y);
                for (var blockX = 0; blockX < blocksWide; blockX++)
                {
                    var x = blockX * 4;
                    var width = Math.Min(4, texture.Width - x);
                    var block = new Rectangle(x, y, width, height);
                    if (block.IntersectsWith(replacementRectangle) &&
                        protectedRectangles.Any(block.IntersectsWith))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static Bitmap DecodeTexture(Texture texture)
        {
            if (texture.ArraySize != 1)
                throw new NotSupportedException("暂不支持数组纹理。");

            var subTexture = texture[0, 0];
            var rgba = texture.IsYCbCr
                ? DecodeYCbCr(texture)
                : DecodeRgba(subTexture);
            var bitmap = new Bitmap(subTexture.Width, subTexture.Height, DrawingPixelFormat.Format32bppArgb);
            var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, DrawingPixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bitmap.Width * 4];
                for (var y = 0; y < bitmap.Height; y++)
                {
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        var source = (y * bitmap.Width + x) * 4;
                        var target = x * 4;
                        row[target] = rgba[source + 2];
                        row[target + 1] = rgba[source + 1];
                        row[target + 2] = rgba[source];
                        row[target + 3] = rgba[source + 3];
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }

        private static byte[] DecodeRgba(SubTexture texture)
        {
            var pixelCount = checked(texture.Width * texture.Height);
            if (TextureFormatUtilities.IsBlockCompressed(texture.Format))
            {
                if (texture.Format == TextureFormat.BC6H)
                    throw new NotSupportedException("BC6H HDR Sprite 不能作为歌曲图片编辑。");
                var decoder = new BcDecoder();
                var colors = decoder.DecodeRaw(
                    texture.Data,
                    texture.Width,
                    texture.Height,
                    ToCompressionFormat(texture.Format));
                var rgba = new byte[pixelCount * 4];
                for (var index = 0; index < colors.Length; index++)
                {
                    var target = index * 4;
                    rgba[target] = colors[index].r;
                    rgba[target + 1] = colors[index].g;
                    rgba[target + 2] = texture.Format == TextureFormat.ATI2 ? (byte)255 : colors[index].b;
                    rgba[target + 3] = colors[index].a;
                }
                return rgba;
            }

            var output = new byte[pixelCount * 4];
            for (var index = 0; index < pixelCount; index++)
            {
                var target = index * 4;
                switch (texture.Format)
                {
                    case TextureFormat.A8:
                        output[target] = output[target + 1] = output[target + 2] = 255;
                        output[target + 3] = texture.Data[index];
                        break;
                    case TextureFormat.L8:
                        output[target] = output[target + 1] = output[target + 2] = texture.Data[index];
                        output[target + 3] = 255;
                        break;
                    case TextureFormat.RGB8:
                        CopyRgb(texture.Data, index * 3, output, target, false);
                        break;
                    case TextureFormat.RGBA8:
                        Buffer.BlockCopy(texture.Data, index * 4, output, target, 4);
                        break;
                    case TextureFormat.RGB5:
                        Decode16(texture.Data, index * 2, output, target, TextureFormat.RGB5);
                        break;
                    case TextureFormat.RGB5A1:
                        Decode16(texture.Data, index * 2, output, target, TextureFormat.RGB5A1);
                        break;
                    case TextureFormat.RGBA4:
                        Decode16(texture.Data, index * 2, output, target, TextureFormat.RGBA4);
                        break;
                    case TextureFormat.L8A8:
                        output[target] = output[target + 1] = output[target + 2] = texture.Data[index * 2];
                        output[target + 3] = texture.Data[index * 2 + 1];
                        break;
                    default:
                        throw new NotSupportedException($"不支持纹理格式 {texture.Format}。");
                }
            }
            return output;
        }

        private static byte[] DecodeYCbCr(Texture texture)
        {
            var luminance = texture[0, 0];
            var chroma = texture[0, 1];
            var decoder = new BcDecoder();
            var luminanceColors = decoder.DecodeRaw(
                luminance.Data,
                luminance.Width,
                luminance.Height,
                CompressionFormat.Bc5);
            var chromaColors = decoder.DecodeRaw(
                chroma.Data,
                chroma.Width,
                chroma.Height,
                CompressionFormat.Bc5);
            var output = new byte[luminance.Width * luminance.Height * 4];
            for (var y = 0; y < luminance.Height; y++)
            {
                for (var x = 0; x < luminance.Width; x++)
                {
                    var luminanceColor = luminanceColors[y * luminance.Width + x];
                    SampleChroma(chromaColors, chroma.Width, chroma.Height, x, y,
                        luminance.Width, luminance.Height, out var encodedCb, out var encodedCr);

                    var valueY = luminanceColor.r / 255f;
                    var valueCb = encodedCb * 1.003922f - 0.503929f;
                    var valueCr = encodedCr * 1.003922f - 0.503929f;
                    var target = (y * luminance.Width + x) * 4;
                    output[target] = ClampToByte(valueY + 1.5748f * valueCr);
                    output[target + 1] = ClampToByte(valueY - 0.1873f * valueCb - 0.4681f * valueCr);
                    output[target + 2] = ClampToByte(valueY + 1.8556f * valueCb);
                    output[target + 3] = luminanceColor.g;
                }
            }
            return output;
        }

        private static Texture EncodeTexture(
            Bitmap bitmap,
            Texture original,
            Rectangle replacementRectangle,
            IReadOnlyList<Rectangle> protectedRectangles,
            CancellationToken cancellationToken)
        {
            if (original.ArraySize != 1)
                throw new NotSupportedException("不能重编码此纹理布局。");
            if (original.IsYCbCr)
                return EncodeYCbCrTexture(
                    bitmap,
                    original,
                    replacementRectangle,
                    protectedRectangles,
                    cancellationToken);
            if (original.Format == TextureFormat.BC6H)
                throw new NotSupportedException("不能重编码 BC6H HDR Sprite。");

            var subTextures = new SubTexture[1, original.MipMapCount];
            var baseLevelChanged = false;
            for (var level = 0; level < original.MipMapCount; level++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var originalSubTexture = original[0, level];
                var width = originalSubTexture.Width;
                var height = originalSubTexture.Height;
                using var mip = ResizeBitmap(bitmap, width, height);
                var encoded = EncodeBitmap(mip, original.Format);
                var subTexture = CloneSubTexture(originalSubTexture);
                if (encoded.Length != subTexture.Data.Length)
                {
                    throw new InvalidDataException(
                        $"纹理编码大小不匹配：预期 {subTexture.Data.Length}，实际 {encoded.Length}。");
                }

                var mipRectangle = ScaleRectangleToCoverage(
                    replacementRectangle,
                    bitmap.Width,
                    bitmap.Height,
                    width,
                    height);
                var mipProtectedRectangles = protectedRectangles
                    .Select(rectangle => ScaleRectangleToCoverage(
                        rectangle,
                        bitmap.Width,
                        bitmap.Height,
                        width,
                        height))
                    .ToArray();
                var changedUnits = MergeEncodedRegion(
                    subTexture,
                    encoded,
                    mipRectangle,
                    mipProtectedRectangles);
                if (level == 0)
                    baseLevelChanged = changedUnits > 0;
                subTextures[0, level] = subTexture;
            }

            if (!baseLevelChanged)
            {
                throw new InvalidDataException(
                    "目标 Sprite 与邻图共享全部可用的纹理块，无法在不改动邻图的前提下替换。");
            }
            return new Texture(subTextures);
        }

        private static Texture EncodeYCbCrTexture(
            Bitmap bitmap,
            Texture original,
            Rectangle replacementRectangle,
            IReadOnlyList<Rectangle> protectedRectangles,
            CancellationToken cancellationToken)
        {
            if (original.MipMapCount != 2)
                throw new NotSupportedException("不能重编码此 YCbCr 纹理布局。");

            cancellationToken.ThrowIfCancellationRequested();
            var bgra = GetBgraBytes(bitmap);
            var fullWidth = bitmap.Width;
            var fullHeight = bitmap.Height;
            var chromaWidth = Math.Max(1, fullWidth >> 1);
            var chromaHeight = Math.Max(1, fullHeight >> 1);
            var luminancePixels = new byte[fullWidth * fullHeight * 4];
            var fullChroma = new float[fullWidth * fullHeight * 2];

            for (var index = 0; index < fullWidth * fullHeight; index++)
            {
                var source = index * 4;
                var blue = bgra[source] / 255f;
                var green = bgra[source + 1] / 255f;
                var red = bgra[source + 2] / 255f;
                var alpha = bgra[source + 3];
                var valueY = red * 0.212593317f + green * 0.715214610f + blue * 0.0721921176f;
                var valueCb = (red * -0.114568502f + green * -0.385435730f + blue * 0.5000042320f + 0.503929f) / 1.003922f;
                var valueCr = (red * 0.500004232f + green * -0.454162151f + blue * -0.0458420813f + 0.503929f) / 1.003922f;

                luminancePixels[source] = ClampToByte(valueY);
                luminancePixels[source + 1] = alpha;
                luminancePixels[source + 2] = 0;
                luminancePixels[source + 3] = 255;
                fullChroma[index * 2] = Math.Clamp(valueCb, 0f, 1f);
                fullChroma[index * 2 + 1] = Math.Clamp(valueCr, 0f, 1f);
            }

            var chromaPixels = new byte[chromaWidth * chromaHeight * 4];
            for (var y = 0; y < chromaHeight; y++)
            {
                for (var x = 0; x < chromaWidth; x++)
                {
                    var startX = x * fullWidth / chromaWidth;
                    var endX = Math.Max(startX + 1, (x + 1) * fullWidth / chromaWidth);
                    var startY = y * fullHeight / chromaHeight;
                    var endY = Math.Max(startY + 1, (y + 1) * fullHeight / chromaHeight);
                    var cb = 0f;
                    var cr = 0f;
                    var count = 0;
                    for (var sourceY = startY; sourceY < endY && sourceY < fullHeight; sourceY++)
                    {
                        for (var sourceX = startX; sourceX < endX && sourceX < fullWidth; sourceX++)
                        {
                            var source = (sourceY * fullWidth + sourceX) * 2;
                            cb += fullChroma[source];
                            cr += fullChroma[source + 1];
                            count++;
                        }
                    }

                    var target = (y * chromaWidth + x) * 4;
                    chromaPixels[target] = ClampToByte(cb / Math.Max(1, count));
                    chromaPixels[target + 1] = ClampToByte(cr / Math.Max(1, count));
                    chromaPixels[target + 2] = 0;
                    chromaPixels[target + 3] = 255;
                }
            }

            var encoder = new BcEncoder(CompressionFormat.Bc5);
            encoder.OutputOptions.GenerateMipMaps = false;
            encoder.OutputOptions.Quality = CompressionQuality.Balanced;
            var luminanceData = encoder.EncodeToRawBytes(
                luminancePixels,
                fullWidth,
                fullHeight,
                BcnPixelFormat.Rgba32)[0];
            var chromaData = encoder.EncodeToRawBytes(
                chromaPixels,
                chromaWidth,
                chromaHeight,
                BcnPixelFormat.Rgba32)[0];

            var luminance = CloneSubTexture(original[0, 0]);
            var chroma = CloneSubTexture(original[0, 1]);
            if (luminance.Width != fullWidth || luminance.Height != fullHeight ||
                chroma.Width != chromaWidth || chroma.Height != chromaHeight ||
                luminanceData.Length != luminance.Data.Length ||
                chromaData.Length != chroma.Data.Length)
            {
                throw new InvalidDataException("YCbCr 纹理编码大小不匹配。");
            }

            var changedLuminanceBlocks = MergeCompressedBlocks(
                luminance,
                luminanceData,
                (x, y, width, height) =>
                {
                    var block = new Rectangle(x, y, width, height);
                    return block.IntersectsWith(replacementRectangle) &&
                        !protectedRectangles.Any(block.IntersectsWith);
                });
            var changedChromaBlocks = MergeCompressedBlocks(
                chroma,
                chromaData,
                (x, y, width, height) =>
                {
                    var horizontalInfluence = GetSampleInfluence(
                        x,
                        width,
                        chromaWidth,
                        fullWidth);
                    var verticalInfluence = GetSampleInfluence(
                        y,
                        height,
                        chromaHeight,
                        fullHeight);
                    var influence = new Rectangle(
                        horizontalInfluence.Start,
                        verticalInfluence.Start,
                        horizontalInfluence.Length,
                        verticalInfluence.Length);
                    return influence.IntersectsWith(replacementRectangle) &&
                        !protectedRectangles.Any(influence.IntersectsWith);
                });

            if (changedLuminanceBlocks == 0 || changedChromaBlocks == 0)
            {
                throw new InvalidDataException(
                    "目标 Sprite 与邻图共享全部可用的 YCbCr 压缩块，无法在不改动邻图的前提下替换。");
            }

            var subTextures = new SubTexture[1, 2];
            subTextures[0, 0] = luminance;
            subTextures[0, 1] = chroma;
            return new Texture(subTextures);
        }

        private static SubTexture CloneSubTexture(SubTexture source)
        {
            var clone = new SubTexture(source.Width, source.Height, source.Format);
            if (clone.Data.Length != source.Data.Length)
                throw new InvalidDataException("原始纹理数据大小无效。");
            Buffer.BlockCopy(source.Data, 0, clone.Data, 0, source.Data.Length);
            return clone;
        }

        private static Rectangle ScaleRectangleToCoverage(
            Rectangle rectangle,
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            var left = (int)((long)rectangle.Left * targetWidth / sourceWidth);
            var top = (int)((long)rectangle.Top * targetHeight / sourceHeight);
            var right = DivideRoundUp((long)rectangle.Right * targetWidth, sourceWidth);
            var bottom = DivideRoundUp((long)rectangle.Bottom * targetHeight, sourceHeight);
            left = Math.Clamp(left, 0, targetWidth);
            top = Math.Clamp(top, 0, targetHeight);
            right = Math.Clamp(right, left, targetWidth);
            bottom = Math.Clamp(bottom, top, targetHeight);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private static int DivideRoundUp(long value, int divisor)
        {
            return (int)((value + divisor - 1) / divisor);
        }

        private static int MergeEncodedRegion(
            SubTexture target,
            byte[] encoded,
            Rectangle replacementRectangle,
            IReadOnlyList<Rectangle> protectedRectangles)
        {
            if (replacementRectangle.Width <= 0 || replacementRectangle.Height <= 0)
                return 0;
            if (TextureFormatUtilities.IsBlockCompressed(target.Format))
            {
                return MergeCompressedBlocks(
                    target,
                    encoded,
                    (x, y, width, height) =>
                    {
                        var block = new Rectangle(x, y, width, height);
                        return block.IntersectsWith(replacementRectangle) &&
                            !protectedRectangles.Any(block.IntersectsWith);
                    });
            }

            var bytesPerPixel = TextureFormatUtilities.CalculateDataSize(1, 1, target.Format);
            var changed = 0;
            for (var y = replacementRectangle.Top; y < replacementRectangle.Bottom; y++)
            {
                for (var x = replacementRectangle.Left; x < replacementRectangle.Right; x++)
                {
                    if (protectedRectangles.Any(rectangle => rectangle.Contains(x, y)))
                        continue;
                    var offset = (y * target.Width + x) * bytesPerPixel;
                    Buffer.BlockCopy(encoded, offset, target.Data, offset, bytesPerPixel);
                    changed++;
                }
            }
            return changed;
        }

        private static int MergeCompressedBlocks(
            SubTexture target,
            byte[] encoded,
            Func<int, int, int, int, bool> shouldReplace)
        {
            if (!TextureFormatUtilities.IsBlockCompressed(target.Format))
                throw new ArgumentException("目标纹理不是块压缩格式。", nameof(target));
            if (encoded.Length != target.Data.Length)
                throw new InvalidDataException("压缩纹理编码大小不匹配。");

            var encoder = new BcEncoder(ToCompressionFormat(target.Format));
            var blockSize = encoder.GetBlockSize();
            var blocksWide = (target.Width + 3) / 4;
            var blocksHigh = (target.Height + 3) / 4;
            var changed = 0;
            for (var blockY = 0; blockY < blocksHigh; blockY++)
            {
                var y = blockY * 4;
                var height = Math.Min(4, target.Height - y);
                for (var blockX = 0; blockX < blocksWide; blockX++)
                {
                    var x = blockX * 4;
                    var width = Math.Min(4, target.Width - x);
                    if (!shouldReplace(x, y, width, height))
                        continue;

                    var offset = (blockY * blocksWide + blockX) * blockSize;
                    Buffer.BlockCopy(encoded, offset, target.Data, offset, blockSize);
                    changed++;
                }
            }
            return changed;
        }

        private static (int Start, int Length) GetSampleInfluence(
            int sampleStart,
            int sampleLength,
            int sampleCount,
            int targetCount)
        {
            var sampleEnd = sampleStart + sampleLength;
            var firstAffected = targetCount;
            var lastAffected = -1;
            for (var target = 0; target < targetCount; target++)
            {
                var source = (target + 0.5f) * sampleCount / targetCount - 0.5f;
                var firstSample = Math.Clamp((int)Math.Floor(source), 0, sampleCount - 1);
                var secondSample = Math.Min(sampleCount - 1, firstSample + 1);
                if ((firstSample >= sampleStart && firstSample < sampleEnd) ||
                    (secondSample >= sampleStart && secondSample < sampleEnd))
                {
                    firstAffected = Math.Min(firstAffected, target);
                    lastAffected = target;
                }
            }

            return lastAffected < firstAffected
                ? (0, 0)
                : (firstAffected, lastAffected - firstAffected + 1);
        }

        private static byte[] EncodeBitmap(Bitmap bitmap, TextureFormat format)
        {
            var bgra = GetBgraBytes(bitmap);
            if (TextureFormatUtilities.IsBlockCompressed(format))
            {
                var encoder = new BcEncoder(ToCompressionFormat(format));
                encoder.OutputOptions.GenerateMipMaps = false;
                encoder.OutputOptions.Quality = CompressionQuality.Balanced;
                return encoder.EncodeToRawBytes(
                    bgra,
                    bitmap.Width,
                    bitmap.Height,
                    BcnPixelFormat.Bgra32)[0];
            }

            var pixelCount = bitmap.Width * bitmap.Height;
            var bytesPerPixel = TextureFormatUtilities.CalculateDataSize(1, 1, format);
            var output = new byte[TextureFormatUtilities.CalculateDataSize(bitmap.Width, bitmap.Height, format)];
            for (var index = 0; index < pixelCount; index++)
            {
                var source = index * 4;
                var b = bgra[source];
                var g = bgra[source + 1];
                var r = bgra[source + 2];
                var a = bgra[source + 3];
                var target = index * bytesPerPixel;
                switch (format)
                {
                    case TextureFormat.A8:
                        output[target] = a;
                        break;
                    case TextureFormat.L8:
                        output[target] = ToLuminance(r, g, b);
                        break;
                    case TextureFormat.RGB8:
                        output[target] = r;
                        output[target + 1] = g;
                        output[target + 2] = b;
                        break;
                    case TextureFormat.RGBA8:
                        output[target] = r;
                        output[target + 1] = g;
                        output[target + 2] = b;
                        output[target + 3] = a;
                        break;
                    case TextureFormat.RGB5:
                        WriteUInt16(output, target, (ushort)((r >> 3) | ((g >> 2) << 5) | ((b >> 3) << 11)));
                        break;
                    case TextureFormat.RGB5A1:
                        WriteUInt16(output, target, (ushort)((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10) | ((a >= 128 ? 1 : 0) << 15)));
                        break;
                    case TextureFormat.RGBA4:
                        WriteUInt16(output, target, (ushort)((r >> 4) | ((g >> 4) << 4) | ((b >> 4) << 8) | ((a >> 4) << 12)));
                        break;
                    case TextureFormat.L8A8:
                        output[target] = ToLuminance(r, g, b);
                        output[target + 1] = a;
                        break;
                    default:
                        throw new NotSupportedException($"不支持纹理格式 {format}。");
                }
            }
            return output;
        }

        private static Bitmap LoadInputBitmap(string path)
        {
            using var image = Image.FromFile(path);
            var bitmap = new Bitmap(image.Width, image.Height, DrawingPixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImage(image, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            return bitmap;
        }

        private static void DrawImageCover(Bitmap destination, Bitmap source, Rectangle target)
        {
            var targetAspect = target.Width / (float)target.Height;
            var sourceAspect = source.Width / (float)source.Height;
            RectangleF sourceRectangle;
            if (sourceAspect > targetAspect)
            {
                var width = source.Height * targetAspect;
                sourceRectangle = new RectangleF((source.Width - width) / 2f, 0, width, source.Height);
            }
            else
            {
                var height = source.Width / targetAspect;
                sourceRectangle = new RectangleF(0, (source.Height - height) / 2f, source.Width, height);
            }

            using var graphics = Graphics.FromImage(destination);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            using var imageAttributes = new ImageAttributes();
            imageAttributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(
                source,
                target,
                sourceRectangle.X,
                sourceRectangle.Y,
                sourceRectangle.Width,
                sourceRectangle.Height,
                GraphicsUnit.Pixel,
                imageAttributes);
        }

        private static void ExtendSpriteEdges(Bitmap bitmap, Rectangle target, int padding)
        {
            if (padding <= 0)
                return;

            var expanded = Rectangle.Intersect(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                Rectangle.FromLTRB(
                    target.Left - padding,
                    target.Top - padding,
                    target.Right + padding,
                    target.Bottom + padding));
            var pixels = GetBgraBytes(bitmap);
            var original = (byte[])pixels.Clone();
            for (var y = expanded.Top; y < expanded.Bottom; y++)
            {
                for (var x = expanded.Left; x < expanded.Right; x++)
                {
                    if (target.Contains(x, y))
                        continue;
                    var sourceX = Math.Clamp(x, target.Left, target.Right - 1);
                    var sourceY = Math.Clamp(y, target.Top, target.Bottom - 1);
                    Buffer.BlockCopy(
                        original,
                        (sourceY * bitmap.Width + sourceX) * 4,
                        pixels,
                        (y * bitmap.Width + x) * 4,
                        4);
                }
            }
            SetBgraBytes(bitmap, pixels);
        }

        private static Bitmap ResizeBitmap(Bitmap source, int width, int height)
        {
            if (source.Width == width && source.Height == height)
                return source.Clone(new Rectangle(0, 0, width, height), DrawingPixelFormat.Format32bppArgb);

            var output = new Bitmap(width, height, DrawingPixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(output);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var imageAttributes = new ImageAttributes();
            imageAttributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, width, height),
                0,
                0,
                source.Width,
                source.Height,
                GraphicsUnit.Pixel,
                imageAttributes);
            return output;
        }

        private static byte[] GetBgraBytes(Bitmap bitmap)
        {
            var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
            try
            {
                var output = new byte[bitmap.Width * bitmap.Height * 4];
                var rowLength = bitmap.Width * 4;
                for (var y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), output, y * rowLength, rowLength);
                return output;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static void SetBgraBytes(Bitmap bitmap, byte[] pixels)
        {
            if (pixels.Length != bitmap.Width * bitmap.Height * 4)
                throw new ArgumentException("像素数据大小与位图不匹配。", nameof(pixels));

            var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, DrawingPixelFormat.Format32bppArgb);
            try
            {
                var rowLength = bitmap.Width * 4;
                for (var y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(pixels, y * rowLength, IntPtr.Add(data.Scan0, y * data.Stride), rowLength);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static CompressionFormat ToCompressionFormat(TextureFormat format)
        {
            return format switch
            {
                TextureFormat.DXT1 => CompressionFormat.Bc1,
                TextureFormat.DXT1a => CompressionFormat.Bc1WithAlpha,
                TextureFormat.DXT3 => CompressionFormat.Bc2,
                TextureFormat.DXT5 => CompressionFormat.Bc3,
                TextureFormat.ATI1 => CompressionFormat.Bc4,
                TextureFormat.ATI2 => CompressionFormat.Bc5,
                TextureFormat.BC7 => CompressionFormat.Bc7,
                _ => throw new NotSupportedException($"不支持压缩纹理格式 {format}。")
            };
        }

        private static void SampleChroma(
            BCnEncoder.Shared.ColorRgba32[] colors,
            int width,
            int height,
            int targetX,
            int targetY,
            int targetWidth,
            int targetHeight,
            out float cb,
            out float cr)
        {
            var sourceX = (targetX + 0.5f) * width / targetWidth - 0.5f;
            var sourceY = (targetY + 0.5f) * height / targetHeight - 0.5f;
            var left = Math.Clamp((int)Math.Floor(sourceX), 0, width - 1);
            var top = Math.Clamp((int)Math.Floor(sourceY), 0, height - 1);
            var right = Math.Min(width - 1, left + 1);
            var bottom = Math.Min(height - 1, top + 1);
            var amountX = Math.Clamp(sourceX - (float)Math.Floor(sourceX), 0f, 1f);
            var amountY = Math.Clamp(sourceY - (float)Math.Floor(sourceY), 0f, 1f);

            var topLeft = colors[top * width + left];
            var topRight = colors[top * width + right];
            var bottomLeft = colors[bottom * width + left];
            var bottomRight = colors[bottom * width + right];
            var topCb = Lerp(topLeft.r / 255f, topRight.r / 255f, amountX);
            var bottomCb = Lerp(bottomLeft.r / 255f, bottomRight.r / 255f, amountX);
            var topCr = Lerp(topLeft.g / 255f, topRight.g / 255f, amountX);
            var bottomCr = Lerp(bottomLeft.g / 255f, bottomRight.g / 255f, amountX);
            cb = Lerp(topCb, bottomCb, amountY);
            cr = Lerp(topCr, bottomCr, amountY);
        }

        private static float Lerp(float first, float second, float amount)
        {
            return first + (second - first) * amount;
        }

        private static byte ClampToByte(float value)
        {
            return (byte)Math.Clamp((int)Math.Round(value * 255f), 0, 255);
        }

        private static void CopyRgb(byte[] source, int sourceOffset, byte[] target, int targetOffset, bool hasAlpha)
        {
            target[targetOffset] = source[sourceOffset];
            target[targetOffset + 1] = source[sourceOffset + 1];
            target[targetOffset + 2] = source[sourceOffset + 2];
            target[targetOffset + 3] = hasAlpha ? source[sourceOffset + 3] : (byte)255;
        }

        private static void Decode16(
            byte[] source,
            int sourceOffset,
            byte[] target,
            int targetOffset,
            TextureFormat format)
        {
            var value = (ushort)(source[sourceOffset] | source[sourceOffset + 1] << 8);
            if (format == TextureFormat.RGB5)
            {
                target[targetOffset] = Expand5(value & 0x1F);
                target[targetOffset + 1] = Expand6((value >> 5) & 0x3F);
                target[targetOffset + 2] = Expand5((value >> 11) & 0x1F);
                target[targetOffset + 3] = 255;
            }
            else if (format == TextureFormat.RGB5A1)
            {
                target[targetOffset] = Expand5(value & 0x1F);
                target[targetOffset + 1] = Expand5((value >> 5) & 0x1F);
                target[targetOffset + 2] = Expand5((value >> 10) & 0x1F);
                target[targetOffset + 3] = (value & 0x8000) == 0 ? (byte)0 : (byte)255;
            }
            else
            {
                target[targetOffset] = Expand4(value & 0xF);
                target[targetOffset + 1] = Expand4((value >> 4) & 0xF);
                target[targetOffset + 2] = Expand4((value >> 8) & 0xF);
                target[targetOffset + 3] = Expand4((value >> 12) & 0xF);
            }
        }

        private static byte Expand4(int value) => (byte)(value | value << 4);
        private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));
        private static byte Expand6(int value) => (byte)((value << 2) | (value >> 4));

        private static void WriteUInt16(byte[] target, int offset, ushort value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
        }

        private static byte ToLuminance(byte r, byte g, byte b)
        {
            return (byte)Math.Clamp((r * 299 + g * 587 + b * 114 + 500) / 1000, 0, 255);
        }

        private static IReadOnlyList<string> GetRawIds(SongEntry song)
        {
            var ids = new List<string>();
            AddUnique(ids, song.RawPvId);
            foreach (var rawId in song.RawPvIds)
                AddUnique(ids, rawId);
            AddUnique(ids, song.PvId.ToString(CultureInfo.InvariantCulture));
            AddUnique(ids, song.PvId.ToString("D3", CultureInfo.InvariantCulture));
            return ids;
        }

        private static IEnumerable<string> EnumerateSpriteDatabases(string contentRoot)
        {
            var direct = Path.Combine(contentRoot ?? String.Empty, "rom", "2d", "mod_spr_db.bin");
            if (File.Exists(direct))
                yield return direct;
        }

        private static string GetContentRootFromDatabase(string databasePath)
        {
            var twoD = Directory.GetParent(databasePath);
            var rom = twoD?.Parent;
            return rom?.Parent?.FullName ?? String.Empty;
        }

        private string GetProviderName(string path)
        {
            if (!String.IsNullOrWhiteSpace(modsFolder) && IsPathUnder(modsFolder, path))
            {
                var relative = Path.GetRelativePath(modsFolder, path);
                var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (!String.IsNullOrWhiteSpace(first))
                    return first;
            }
            return Path.GetFileName(Path.GetDirectoryName(path)) ?? "未知模组";
        }

        private static bool IsSpriteSetFile(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".spr", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetTrailingId(string value, out int id)
        {
            id = 0;
            if (String.IsNullOrWhiteSpace(value))
                return false;
            var index = value.Length - 1;
            while (index >= 0 && Char.IsDigit(value[index]))
                index--;
            return index < value.Length - 1 && Int32.TryParse(
                value.Substring(index + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out id);
        }

        private static bool IsThumbnailManagerPath(string path)
        {
            return !String.IsNullOrWhiteSpace(path) &&
                path.IndexOf("MegaMixThumbnailManager", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsPathUnder(string root, string path)
        {
            if (String.IsNullOrWhiteSpace(root) || String.IsNullOrWhiteSpace(path))
                return false;
            var normalizedRoot = NormalizePath(root) + Path.DirectorySeparatorChar;
            var normalizedPath = NormalizePath(path);
            return normalizedPath.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathsEqual(string first, string second)
        {
            return NormalizePath(first).Equals(NormalizePath(second), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                return String.Empty;
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream));
        }

        private static MemoryStream OpenArchiveSource(string path)
        {
            return new MemoryStream(File.ReadAllBytes(path), false);
        }

        private static void AddUnique(ICollection<string> values, string value)
        {
            if (!String.IsNullOrWhiteSpace(value) && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                values.Add(value);
        }

        private sealed class ArtworkLocation
        {
            public ArtworkLocation(
                SongArtworkKind kind,
                string archivePath,
                string internalFileName,
                int spriteIndex,
                string spriteName,
                string ownerContentRoot,
                string sourceDescription,
                bool canReplace)
            {
                Kind = kind;
                ArchivePath = archivePath;
                InternalFileName = internalFileName;
                SpriteIndex = spriteIndex;
                SpriteName = spriteName;
                OwnerContentRoot = ownerContentRoot;
                SourceDescription = sourceDescription;
                CanReplace = canReplace;
            }

            public SongArtworkKind Kind { get; }
            public string ArchivePath { get; }
            public string InternalFileName { get; }
            public int SpriteIndex { get; }
            public string SpriteName { get; }
            public string OwnerContentRoot { get; }
            public string SourceDescription { get; }
            public bool CanReplace { get; }
        }

        private sealed class PreparedReplacement
        {
            public PreparedReplacement(ArtworkLocation location, string originalHash, byte[] archiveBytes)
            {
                Location = location;
                OriginalHash = originalHash;
                ArchiveBytes = archiveBytes;
            }

            public ArtworkLocation Location { get; }
            public string OriginalHash { get; }
            public byte[] ArchiveBytes { get; }
        }

        private sealed class SpriteDatabaseEntry
        {
            public SpriteDatabaseEntry(string name, string setFileName, int index)
            {
                Name = name ?? String.Empty;
                SetFileName = setFileName ?? String.Empty;
                Index = index;
            }

            public string Name { get; }
            public string SetFileName { get; }
            public int Index { get; }
        }

        private sealed class SpriteDatabaseSnapshot
        {
            private readonly IReadOnlyDictionary<ArtworkLookupKey, IReadOnlyList<SpriteDatabaseEntry>> lookup;

            public SpriteDatabaseSnapshot(IReadOnlyList<SpriteDatabaseEntry> entries)
            {
                Entries = entries;
                var mutableLookup = new Dictionary<ArtworkLookupKey, List<SpriteDatabaseEntry>>();
                foreach (var entry in entries)
                {
                    if (!TryCreateArtworkLookupKey(entry, out var key))
                        continue;
                    if (!mutableLookup.TryGetValue(key, out var matches))
                    {
                        matches = new List<SpriteDatabaseEntry>();
                        mutableLookup.Add(key, matches);
                    }
                    matches.Add(entry);
                }
                lookup = mutableLookup.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<SpriteDatabaseEntry>)pair.Value.ToArray());
                Keys = lookup.Keys.ToArray();
            }

            public IReadOnlyList<SpriteDatabaseEntry> Entries { get; }
            public IReadOnlyList<ArtworkLookupKey> Keys { get; }

            public IReadOnlyList<SpriteDatabaseEntry> Get(SongArtworkKind kind, int pvId)
            {
                return lookup.TryGetValue(new ArtworkLookupKey(kind, pvId), out var matches)
                    ? matches
                    : Array.Empty<SpriteDatabaseEntry>();
            }
        }

        private readonly struct ArtworkLookupKey : IEquatable<ArtworkLookupKey>
        {
            public ArtworkLookupKey(SongArtworkKind kind, int pvId)
            {
                Kind = kind;
                PvId = pvId;
            }

            public SongArtworkKind Kind { get; }
            public int PvId { get; }

            public bool Equals(ArtworkLookupKey other)
            {
                return Kind == other.Kind && PvId == other.PvId;
            }

            public override bool Equals(object obj)
            {
                return obj is ArtworkLookupKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine((int)Kind, PvId);
            }
        }

        private sealed class IndexedSpriteDatabase
        {
            public IndexedSpriteDatabase(string path, SpriteDatabaseSnapshot snapshot, int order)
            {
                Path = path;
                Snapshot = snapshot;
                Order = order;
            }

            public string Path { get; }
            public SpriteDatabaseSnapshot Snapshot { get; }
            public int Order { get; }
        }

        private sealed class GlobalSpriteDatabaseIndex
        {
            private readonly IReadOnlyDictionary<ArtworkLookupKey, IReadOnlyList<IndexedSpriteDatabase>> lookup;

            public GlobalSpriteDatabaseIndex(
                IReadOnlyDictionary<ArtworkLookupKey, IReadOnlyList<IndexedSpriteDatabase>> lookup)
            {
                this.lookup = lookup;
            }

            public IReadOnlyList<IndexedSpriteDatabase> Get(SongArtworkKind kind, int pvId)
            {
                return lookup.TryGetValue(new ArtworkLookupKey(kind, pvId), out var databases)
                    ? databases
                    : Array.Empty<IndexedSpriteDatabase>();
            }
        }

        private sealed class LooseArchiveLocation
        {
            public LooseArchiveLocation(string path, string contentRoot)
            {
                Path = path;
                ContentRoot = contentRoot;
            }

            public string Path { get; }
            public string ContentRoot { get; }
        }

        private sealed class GlobalLooseArchiveIndex
        {
            private readonly IReadOnlyDictionary<int, IReadOnlyList<LooseArchiveLocation>> lookup;

            public GlobalLooseArchiveIndex(
                IReadOnlyDictionary<int, IReadOnlyList<LooseArchiveLocation>> lookup)
            {
                this.lookup = lookup;
            }

            public IReadOnlyList<LooseArchiveLocation> Get(int pvId)
            {
                return lookup.TryGetValue(pvId, out var matches)
                    ? matches
                    : Array.Empty<LooseArchiveLocation>();
            }
        }

        private sealed class ArchiveStructureSnapshot
        {
            public static readonly ArchiveStructureSnapshot Empty = new ArchiveStructureSnapshot(
                Array.Empty<string>(),
                new Dictionary<string, SpriteSetStructureSnapshot>(StringComparer.OrdinalIgnoreCase));

            private readonly IReadOnlyDictionary<string, SpriteSetStructureSnapshot> spriteSets;

            public ArchiveStructureSnapshot(
                IReadOnlyList<string> fileNames,
                IReadOnlyDictionary<string, SpriteSetStructureSnapshot> spriteSets)
            {
                FileNames = fileNames;
                this.spriteSets = spriteSets;
            }

            public IReadOnlyList<string> FileNames { get; }

            public bool TryGetSpriteSet(string internalName, out SpriteSetStructureSnapshot spriteSet)
            {
                return spriteSets.TryGetValue(internalName, out spriteSet);
            }
        }

        private sealed class SpriteSetStructureSnapshot
        {
            public SpriteSetStructureSnapshot(
                IReadOnlyList<SpriteStructureSnapshot> sprites,
                IReadOnlyList<TextureStructureSnapshot> textures)
            {
                Sprites = sprites;
                Textures = textures;
            }

            public IReadOnlyList<SpriteStructureSnapshot> Sprites { get; }
            public IReadOnlyList<TextureStructureSnapshot> Textures { get; }
        }

        private sealed class SpriteStructureSnapshot
        {
            public SpriteStructureSnapshot(
                string name,
                uint textureIndex,
                float x,
                float y,
                float width,
                float height)
            {
                Name = name ?? String.Empty;
                TextureIndex = textureIndex;
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }

            public string Name { get; }
            public uint TextureIndex { get; }
            public float X { get; }
            public float Y { get; }
            public float Width { get; }
            public float Height { get; }
        }

        private sealed class TextureStructureSnapshot
        {
            public TextureStructureSnapshot(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public int Width { get; }
            public int Height { get; }
        }

        private sealed class ArchiveCacheEntry
        {
            public ArchiveCacheEntry(
                long length,
                DateTime lastWriteTimeUtc,
                ArchiveStructureSnapshot snapshot)
            {
                Length = length;
                LastWriteTimeUtc = lastWriteTimeUtc;
                Snapshot = snapshot;
            }

            public long Length { get; }
            public DateTime LastWriteTimeUtc { get; }
            public ArchiveStructureSnapshot Snapshot { get; }
        }

        private sealed class DatabaseCacheEntry
        {
            public DatabaseCacheEntry(long length, DateTime lastWriteTimeUtc, SpriteDatabaseSnapshot snapshot)
            {
                Length = length;
                LastWriteTimeUtc = lastWriteTimeUtc;
                Snapshot = snapshot;
            }

            public long Length { get; }
            public DateTime LastWriteTimeUtc { get; }
            public SpriteDatabaseSnapshot Snapshot { get; }
        }
    }
}
