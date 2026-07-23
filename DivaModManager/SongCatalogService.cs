using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Sprites;
using Tomlyn;
using Tomlyn.Model;

namespace DivaModManager
{
    public static class SongCatalogService
    {
        private const string LegacyDatabaseName = "mod_pv_db.txt";
        private const string NewClassicsMetadataDatabaseName = "mod_nc_pv_db.txt";
        private const string NewClassicsDatabaseName = "nc_db.toml";
        private const string EdenProjectAuthor = "Eden Project Team";
        private const string EdenProjectCoreName = "Eden Project - Core";
        private const string CurrentEdenProjectSongPackVersion = "1.0.4";
        private const int MaxDeclaredDatabaseArrayLength = 64;

        private static readonly HashSet<int> EdenProjectOfficialExtraExtremePvIds = new HashSet<int>
        {
            83, 93, 95, 276, 434, 623
        };

        // Diva Mod Archive PV spreadsheet rows whose Source / Reservation Date is exactly
        // "MM+". Snapshot checked 2026-07-11. Keep this exact: low IDs such as 19 and 27
        // are custom-song reservations rather than MEGA39+ stock songs.
        // https://divamodarchive.com/pv_spreadsheet
        private static readonly HashSet<int> Mega39PlusStockPvIds = new HashSet<int>
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            20, 21, 22, 23, 24, 25, 28, 29, 30, 31, 32, 37, 38, 39, 40, 41, 42,
            43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59,
            60, 61, 62, 63, 64, 65, 66, 67, 68, 79, 81, 82, 83, 84, 85, 86, 87,
            88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 101, 102, 103, 104, 201, 202,
            203, 204, 205, 206, 208, 209, 210, 211, 212, 213, 214, 215, 216, 218,
            219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 231, 232, 233, 234,
            235, 236, 238, 239, 240, 241, 242, 243, 244, 246, 247, 248, 249, 250,
            251, 253, 254, 255, 257, 259, 260, 261, 262, 263, 265, 266, 267, 268,
            269, 270, 271, 272, 273, 274, 275, 276, 277, 278, 279, 280, 281, 401,
            402, 403, 404, 405, 407, 408, 409, 410, 411, 412, 413, 414, 415, 416,
            417, 418, 419, 420, 421, 422, 423, 424, 425, 426, 427, 428, 429, 430,
            431, 432, 433, 434, 435, 436, 437, 438, 439, 440, 441, 442, 443, 600,
            601, 602, 603, 604, 605, 607, 608, 609, 610, 611, 612, 613, 614, 615,
            616, 617, 618, 619, 620, 621, 622, 623, 624, 625, 626, 627, 628, 629,
            630, 631, 637, 638, 639, 640, 641, 642, 710, 722, 723, 724,
            725, 726, 727, 728, 729, 730, 731, 732, 733, 734, 736, 737, 738, 739,
            740, 832
        };

        private static readonly Regex DifficultyFieldPattern = new Regex(
            @"^difficulty\.([^.]+)\.(\d+)\.(.+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex DifficultyLengthFieldPattern = new Regex(
            @"^difficulty\.([^.]+)\.length$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex DifficultyLevelPattern = new Regex(
            @"^PV_LV_(\d{1,3})_([05])$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex ChartFilePvIdPattern = new Regex(
            @"^pv_?0*(\d+)(?:_|$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex ArtworkFilePvIdPattern = new Regex(
            @"^spr_sel_pv_?0*(\d+)(?:_|$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly string[] NewClassicsDifficultyNames =
        {
            "easy", "normal", "hard", "extreme", "encore",
            "ex_easy", "ex_normal", "ex_hard", "ex_extreme", "ex_encore"
        };

        public static IReadOnlyList<SongEntry> ScanMods(
            string modsFolder,
            CancellationToken cancellationToken = default)
        {
            return ScanModsCore(modsFolder, true, cancellationToken);
        }

        internal static IReadOnlyList<SongEntry> ScanModsWithoutArtwork(
            string modsFolder,
            CancellationToken cancellationToken = default)
        {
            return ScanModsCore(modsFolder, false, cancellationToken);
        }

        private static IReadOnlyList<SongEntry> ScanModsCore(
            string modsFolder,
            bool evaluateArtwork,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder))
                return Array.Empty<SongEntry>();

            var entries = new List<SongEntry>();
            var loaderActivation = ReadLoaderActivation(modsFolder);
            IEnumerable<string> modDirectories;
            try
            {
                modDirectories = Directory
                    .EnumerateDirectories(modsFolder, "*", SearchOption.TopDirectoryOnly)
                    .Concat(loaderActivation.GetPriorityModRoots())
                    .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                return Array.Empty<SongEntry>();
            }

            var scanContexts = modDirectories
                .Select(modRoot => new ModScanContext
                {
                    ModRoot = modRoot,
                    Config = ReadModConfig(modRoot),
                    IsActiveByLoader = loaderActivation.IsActive(modRoot)
                })
                .ToArray();
            var assetResolver = new VirtualAssetResolver(
                modsFolder,
                scanContexts
                    .Where(context =>
                        context.IsActiveByLoader &&
                        context.Config.IsEnabled &&
                        context.Config.CanScan)
                    .SelectMany(context => context.Config.ContentRoots));

            foreach (var context in scanContexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanMod(
                    context.ModRoot,
                    entries,
                    cancellationToken,
                    context.IsActiveByLoader,
                    context.Config,
                    assetResolver);
            }

            ReconcileCrossModOrphanResources(entries);
            MarkCrossModIdConflicts(entries);
            ValidateKnownModDependencies(entries, scanContexts);
            if (evaluateArtwork)
                EvaluateArtworkAvailability(entries, modsFolder, cancellationToken);
            return entries
                .OrderBy(entry => entry.PvId)
                .ThenBy(entry => entry.RawPvId, StringComparer.Ordinal)
                .ThenBy(entry => entry.ModName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static Task<IReadOnlyList<SongEntry>> ScanModsAsync(
            string modsFolder,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => ScanMods(modsFolder, cancellationToken), cancellationToken);
        }

        internal static SongModScanResult ScanModForEditing(
            string modRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
                return new SongModScanResult(Array.Empty<SongEntry>(), false);

            var entries = new List<SongEntry>();
            var fullModRoot = Path.GetFullPath(modRoot);
            var parent = Path.GetDirectoryName(fullModRoot);
            var config = ReadModConfig(fullModRoot);
            VirtualAssetResolver resolver = null;
            if (!String.IsNullOrWhiteSpace(parent))
            {
                try
                {
                    resolver = new VirtualAssetResolver(
                        parent,
                        Directory.EnumerateDirectories(parent)
                            .Select(ReadModConfig)
                            .Where(candidate => candidate.IsEnabled && candidate.CanScan)
                            .SelectMany(candidate => candidate.ContentRoots));
                }
                catch (Exception exception) when (IsFileSystemException(exception))
                {
                    resolver = null;
                }
            }
            var isComplete = ScanMod(
                fullModRoot,
                entries,
                cancellationToken,
                true,
                config,
                resolver);
            MarkCrossModIdConflicts(entries);
            return new SongModScanResult(
                entries
                    .OrderBy(entry => entry.PvId)
                    .ThenBy(entry => entry.RawPvId, StringComparer.Ordinal)
                    .ThenBy(entry => entry.ModName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                isComplete);
        }

        private static bool ScanMod(
            string modRoot,
            ICollection<SongEntry> result,
            CancellationToken cancellationToken,
            bool isActiveByLoader = true,
            ModConfig preloadedConfig = null,
            VirtualAssetResolver assetResolver = null)
        {
            var config = preloadedConfig ?? ReadModConfig(modRoot);
            if (!config.CanScan)
                return false;
            if (!isActiveByLoader)
            {
                AddUnique(
                    config.Warnings,
                    "此模组未在 DivaModLoader 当前的优先级配置中启用。");
            }

            var isComplete = config.IsComplete;

            foreach (var contentRoot in config.ContentRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(contentRoot))
                {
                    isComplete = false;
                    continue;
                }

                var romRoot = Path.Combine(contentRoot, "rom");
                if (!Directory.Exists(romRoot))
                    continue;

                var topLevelFiles = GetTopLevelFiles(romRoot, out var topLevelReadComplete);
                isComplete &= topLevelReadComplete;
                var legacyDatabases = FindFiles(topLevelFiles, LegacyDatabaseName);
                var newClassicsMetadataDatabases = FindFiles(topLevelFiles, NewClassicsMetadataDatabaseName);
                var newClassicsDatabasePath = FindFiles(topLevelFiles, NewClassicsDatabaseName).FirstOrDefault();
                var newClassics = ReadNewClassicsDatabase(
                    newClassicsDatabasePath,
                    contentRoot,
                    cancellationToken);
                isComplete &= newClassics.IsComplete;

                var contentEntries = new List<SongEntry>();
                foreach (var databasePath in legacyDatabases)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    isComplete &= AddFlatDatabaseEntries(
                        databasePath,
                        false,
                        config,
                        modRoot,
                        contentRoot,
                        newClassics,
                        config.Warnings,
                        contentEntries,
                        cancellationToken,
                        assetResolver);
                }

                foreach (var databasePath in newClassicsMetadataDatabases)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    isComplete &= AddFlatDatabaseEntries(
                        databasePath,
                        true,
                        config,
                        modRoot,
                        contentRoot,
                        newClassics,
                        config.Warnings,
                        contentEntries,
                        cancellationToken,
                        assetResolver);
                }

                var metadataIds = new HashSet<int>(contentEntries.Select(entry => entry.PvId));
                foreach (var newClassicsSong in newClassics.Songs
                    .Where(pair => !metadataIds.Contains(pair.Key))
                    .OrderBy(pair => pair.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    contentEntries.Add(BuildAdditionalDifficultyEntry(
                        config,
                        modRoot,
                        contentRoot,
                        newClassics,
                        newClassicsSong.Value,
                        config.Warnings,
                        assetResolver));
                }

                isComplete &= AddOfficialChartOverlayEntries(
                    contentRoot,
                    contentEntries,
                    cancellationToken);

                var hasSongDatabase = legacyDatabases.Count > 0 ||
                    newClassicsMetadataDatabases.Count > 0 ||
                    !String.IsNullOrWhiteSpace(newClassicsDatabasePath);
                isComplete &= AddOrphanResourceEntries(
                    config,
                    modRoot,
                    contentRoot,
                    hasSongDatabase,
                    config.Warnings,
                    contentEntries,
                    cancellationToken);

                foreach (var entry in contentEntries)
                {
                    entry.ModEnabled = config.IsEnabled && isActiveByLoader;
                    entry.SearchText = BuildSearchText(entry);
                    result.Add(entry);
                }
            }

            return isComplete;
        }

        private static bool AddOfficialChartOverlayEntries(
            string contentRoot,
            ICollection<SongEntry> contentEntries,
            CancellationToken cancellationToken)
        {
            var overlayPaths = new Dictionary<int, List<string>>();
            var isComplete = true;
            foreach (var relativeDirectory in new[] { "script", "script_nc" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scriptRoot = Path.Combine(contentRoot, "rom", relativeDirectory);
                if (!Directory.Exists(scriptRoot))
                    continue;

                try
                {
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    };
                    foreach (var chartPath in Directory.EnumerateFiles(scriptRoot, "*.dsc", options))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var match = ChartFilePvIdPattern.Match(Path.GetFileNameWithoutExtension(chartPath));
                        if (!match.Success ||
                            !Int32.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pvId) ||
                            !Mega39PlusStockPvIds.Contains(pvId))
                        {
                            continue;
                        }

                        if (!overlayPaths.TryGetValue(pvId, out var paths))
                        {
                            paths = new List<string>();
                            overlayPaths[pvId] = paths;
                        }
                        AddUnique(paths, Path.GetFullPath(chartPath));
                    }
                }
                catch (Exception exception) when (IsFileSystemException(exception))
                {
                    isComplete = false;
                }
            }

            foreach (var pair in overlayPaths.OrderBy(pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var paths = pair.Value
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var existingEntries = contentEntries
                    .Where(entry => entry.PvId == pair.Key)
                    .ToArray();
                if (existingEntries.Length > 0)
                {
                    foreach (var existingEntry in existingEntries)
                    {
                        existingEntry.LocalChartOverlayPaths = existingEntry.LocalChartOverlayPaths
                            .Concat(paths)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }
                }
            }

            return isComplete;
        }

        private static bool AddOrphanResourceEntries(
            ModConfig config,
            string modRoot,
            string contentRoot,
            bool hasSongDatabase,
            IReadOnlyCollection<string> configWarnings,
            ICollection<SongEntry> contentEntries,
            CancellationToken cancellationToken)
        {
            var candidates = EnumerateSongResourceCandidates(
                contentRoot,
                cancellationToken,
                out var isComplete);
            if (candidates.Count == 0 ||
                (!hasSongDatabase && !candidates.Any(candidate => candidate.Kind == SongResourceKind.Chart)))
            {
                return isComplete;
            }

            var declaredPaths = BuildDeclaredResourcePathSet(contentRoot, contentEntries);
            var orphanCandidates = candidates
                .Where(candidate => !declaredPaths.Contains(candidate.Path))
                .OrderBy(candidate => candidate.PvId ?? Int32.MaxValue)
                .ThenBy(candidate => candidate.Kind)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (orphanCandidates.Length == 0)
                return isComplete;

            foreach (var group in orphanCandidates.GroupBy(candidate => candidate.PvId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resources = group
                    .Select(candidate => CreateOrphanResource(contentRoot, candidate))
                    .ToArray();
                var matchingEntries = group.Key.HasValue
                    ? contentEntries.Where(entry =>
                        !entry.IsOrphanResourceEntry &&
                        entry.PvId == group.Key.Value).ToArray()
                    : Array.Empty<SongEntry>();
                if (matchingEntries.Length > 0)
                {
                    foreach (var entry in matchingEntries)
                        AttachOrphanResources(entry, resources);
                    continue;
                }

                var rawPvId = group.Key?.ToString(CultureInfo.InvariantCulture) ?? String.Empty;
                var warning = resources.Length == 1
                    ? "发现 1 个未被歌曲数据库声明的废案资源；它不参与运行判断或歌曲删除。"
                    : $"发现 {resources.Length} 个未被歌曲数据库声明的废案资源；它们不参与运行判断或歌曲删除。";
                var warnings = configWarnings.ToList();
                AddUnique(warnings, warning);
                var orphanEntry = new SongEntry
                {
                    ModName = config.ModName,
                    ModAuthor = config.ModAuthor,
                    ModVersion = config.ModVersion,
                    ModRoot = modRoot,
                    ContentRoot = contentRoot,
                    IsEdenProjectCore = config.IsEdenProjectCore,
                    HasValidEdenCoreSignature = config.HasValidEdenCoreSignature,
                    RequiresEdenProjectCore = config.RequiresEdenProjectCore,
                    PvId = group.Key ?? 0,
                    RawPvId = rawPvId,
                    RawPvIds = String.IsNullOrWhiteSpace(rawPvId)
                        ? Array.Empty<string>()
                        : new[] { rawPvId },
                    SongName = group.Key.HasValue
                        ? $"PV {group.Key.Value} 废案资源"
                        : "未归属的废案资源",
                    Format = SongFormat.OrphanResources,
                    IsOrphanResourceEntry = true,
                    IsMega39PlusOfficialPvId = group.Key.HasValue &&
                        Mega39PlusStockPvIds.Contains(group.Key.Value),
                    OrphanResources = resources,
                    ReferencedAssetPaths = Array.Empty<string>(),
                    AssetStatus = $"废案资源：{resources.Length} 个（不参与运行判断）",
                    Warnings = warnings.ToArray()
                };
                if (orphanEntry.IsMega39PlusOfficialPvId)
                {
                    var unsafeCharts = resources
                        .Where(resource => resource.Kind == SongResourceKind.Chart)
                        .Select(resource => resource.Path)
                        .ToArray();
                    if (unsafeCharts.Length > 0)
                    {
                        orphanEntry.LocalChartOverlayPaths = unsafeCharts;
                        AddBlockingRunStatusReason(
                            orphanEntry,
                            $"PVID {orphanEntry.PvId} 属于 MEGA39+ 官曲，但这些未登记谱面会通过虚拟文件系统直接覆盖官曲：{String.Join("; ", unsafeCharts)}。只有 nc_db.toml 明确声明的 New Classics 扩展或已核实的 Eden 扩展可以使用官曲 PVID。");
                    }
                }
                orphanEntry.SearchText = BuildSearchText(orphanEntry);
                contentEntries.Add(orphanEntry);
            }

            return isComplete;
        }

        private static void AttachOrphanResources(
            SongEntry entry,
            IReadOnlyCollection<SongOrphanResource> resources)
        {
            entry.OrphanResources = entry.OrphanResources
                .Concat(resources)
                .GroupBy(resource => resource.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(resource => resource.Kind)
                .ThenBy(resource => resource.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            UpdateOrphanResourceWarning(entry);
            entry.SearchText = BuildSearchText(entry);
        }

        private static void ReconcileCrossModOrphanResources(ICollection<SongEntry> entries)
        {
            var declaredPaths = entries
                .Where(entry => !entry.IsOrphanResourceEntry && entry.ModEnabled)
                .SelectMany(entry => entry.ReferencedAssetPaths)
                .Where(path => !String.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var emptyOrphanEntries = new List<SongEntry>();
            foreach (var entry in entries.Where(entry => entry.HasOrphanResources).ToArray())
            {
                entry.OrphanResources = entry.OrphanResources
                    .Where(resource => !declaredPaths.Contains(resource.Path))
                    .ToArray();
                if (entry.IsOrphanResourceEntry && !entry.HasOrphanResources)
                {
                    emptyOrphanEntries.Add(entry);
                    continue;
                }

                if (entry.IsOrphanResourceEntry)
                    entry.AssetStatus = $"废案资源：{entry.OrphanResources.Count} 个（不参与运行判断）";
                UpdateOrphanResourceWarning(entry);
                entry.SearchText = BuildSearchText(entry);
            }

            foreach (var entry in emptyOrphanEntries)
                entries.Remove(entry);
        }

        private static void UpdateOrphanResourceWarning(SongEntry entry)
        {
            var warnings = entry.Warnings
                .Where(warning =>
                    warning.IndexOf(
                        "未被歌曲数据库声明的废案资源",
                        StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();
            if (entry.OrphanResources.Count == 1)
            {
                AddUnique(
                    warnings,
                    "发现 1 个未被歌曲数据库声明的废案资源；它不参与运行判断或歌曲删除。");
            }
            else if (entry.OrphanResources.Count > 1)
            {
                AddUnique(
                    warnings,
                    $"发现 {entry.OrphanResources.Count} 个未被歌曲数据库声明的废案资源；它们不参与运行判断或歌曲删除。");
            }
            entry.Warnings = warnings.ToArray();
        }

        private static SongOrphanResource CreateOrphanResource(
            string contentRoot,
            SongResourceCandidate candidate)
        {
            var relativePath = Path.GetRelativePath(contentRoot, candidate.Path)
                .Replace(Path.DirectorySeparatorChar, '/');
            return new SongOrphanResource
            {
                PvId = candidate.PvId ?? 0,
                Kind = candidate.Kind,
                Path = candidate.Path,
                RelativePath = relativePath,
                DisplayName = $"{GetResourceKindDisplayName(candidate.Kind)} · {Path.GetFileName(candidate.Path)}"
            };
        }

        private static string GetResourceKindDisplayName(SongResourceKind kind)
        {
            return kind switch
            {
                SongResourceKind.Chart => "谱面",
                SongResourceKind.Audio => "音频",
                SongResourceKind.Video => "视频",
                SongResourceKind.Artwork => "歌曲图片",
                SongResourceKind.AdditionalParameter => "附加参数",
                _ => "资源"
            };
        }

        private static HashSet<string> BuildDeclaredResourcePathSet(
            string contentRoot,
            IEnumerable<SongEntry> entries)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddSpriteDatabaseDeclaredArtworkPaths(paths, contentRoot);
            foreach (var entry in entries.Where(entry => !entry.IsOrphanResourceEntry))
            {
                AddDeclaredResourcePath(paths, contentRoot, entry.AudioPath);
                foreach (var path in entry.AlternateAudioPaths)
                    AddDeclaredResourcePath(paths, contentRoot, path);
                foreach (var path in entry.VideoPaths)
                    AddDeclaredResourcePath(paths, contentRoot, path);
                foreach (var path in entry.ArtworkPaths)
                    AddDeclaredResourcePath(paths, contentRoot, path);
                foreach (var path in entry.AdditionalParameterPaths)
                    AddDeclaredResourcePath(paths, contentRoot, path);
                foreach (var path in entry.ExplicitAssetPaths)
                    AddDeclaredResourcePath(paths, contentRoot, path);
                foreach (var difficulty in entry.Difficulties.Where(difficulty =>
                    difficulty.Source != SongDifficultySource.DetectedChart))
                {
                    AddDeclaredResourcePath(paths, contentRoot, difficulty.ScriptPath);
                }
            }
            return paths;
        }

        private static void AddSpriteDatabaseDeclaredArtworkPaths(
            ISet<string> paths,
            string contentRoot)
        {
            var romRoot = Path.Combine(contentRoot, "rom");
            if (!Directory.Exists(romRoot))
                return;

            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                var twoDDirectory = Path.Combine(romRoot, "2d");
                foreach (var databasePath in Directory.EnumerateFiles(
                    romRoot,
                    "mod_spr_db.bin",
                    options))
                {
                    try
                    {
                        using var database = BinaryFile.Load<SpriteDatabase>(databasePath);
                        foreach (var set in database.SpriteSets)
                        {
                            var setFileName = Path.GetFileName(set.FileName ?? String.Empty);
                            if (String.IsNullOrWhiteSpace(setFileName))
                                continue;
                            AddDeclaredResourcePath(
                                paths,
                                contentRoot,
                                Path.Combine(
                                    twoDDirectory,
                                    Path.GetFileNameWithoutExtension(setFileName) + ".farc"));
                        }
                    }
                    catch (Exception exception) when (
                        IsFileSystemException(exception) ||
                        exception is InvalidDataException ||
                        exception is ArgumentException)
                    {
                        // Artwork probing reports malformed Sprite DB files separately.
                    }
                }
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                // An unreadable optional Sprite DB does not stop song database parsing.
            }
        }

        private static void AddDeclaredResourcePath(
            ISet<string> paths,
            string contentRoot,
            string path)
        {
            if (String.IsNullOrWhiteSpace(path) ||
                path.StartsWith("game://", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (IsUnderRoot(contentRoot, fullPath))
                    paths.Add(fullPath);
            }
            catch (Exception exception) when (
                IsFileSystemException(exception) ||
                exception is ArgumentException)
            {
                // Invalid database paths are already reported by the normal parser.
            }
        }

        private static IReadOnlyList<SongResourceCandidate> EnumerateSongResourceCandidates(
            string contentRoot,
            CancellationToken cancellationToken,
            out bool isComplete)
        {
            var candidates = new Dictionary<string, SongResourceCandidate>(StringComparer.OrdinalIgnoreCase);
            isComplete = true;
            isComplete &= AddResourceCandidates(
                Path.Combine(contentRoot, "rom", "script"),
                SongResourceKind.Chart,
                new[] { ".dsc" },
                null,
                candidates,
                cancellationToken);
            isComplete &= AddResourceCandidates(
                Path.Combine(contentRoot, "rom", "script_nc"),
                SongResourceKind.Chart,
                new[] { ".dsc" },
                null,
                candidates,
                cancellationToken);
            isComplete &= AddResourceCandidates(
                Path.Combine(contentRoot, "rom", "sound", "song"),
                SongResourceKind.Audio,
                new[] { ".ogg" },
                null,
                candidates,
                cancellationToken);
            isComplete &= AddResourceCandidates(
                Path.Combine(contentRoot, "rom", "movie"),
                SongResourceKind.Video,
                new[] { ".mp4", ".usm" },
                null,
                candidates,
                cancellationToken);
            isComplete &= AddResourceCandidates(
                Path.Combine(contentRoot, "rom", "2d"),
                SongResourceKind.Artwork,
                new[] { ".farc" },
                "spr_sel_pv",
                candidates,
                cancellationToken);
            isComplete &= AddResourceCandidates(
                Path.Combine(contentRoot, "rom", "add_param"),
                SongResourceKind.AdditionalParameter,
                new[] { ".adp" },
                null,
                candidates,
                cancellationToken);
            return candidates.Values.ToArray();
        }

        private static bool AddResourceCandidates(
            string directory,
            SongResourceKind kind,
            IReadOnlyCollection<string> extensions,
            string requiredFileNamePrefix,
            IDictionary<string, SongResourceCandidate> candidates,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(directory))
                return true;

            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var path in Directory.EnumerateFiles(directory, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
                    if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ||
                        (!String.IsNullOrWhiteSpace(requiredFileNamePrefix) &&
                            !fileNameWithoutExtension.StartsWith(
                                requiredFileNamePrefix,
                                StringComparison.OrdinalIgnoreCase)) ||
                        (kind == SongResourceKind.Artwork &&
                            fileNameWithoutExtension.StartsWith(
                                "spr_sel_pvtmb",
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var fullPath = Path.GetFullPath(path);
                    candidates[fullPath] = new SongResourceCandidate
                    {
                        Kind = kind,
                        Path = fullPath,
                        PvId = TryInferResourcePvId(kind, fullPath, out var pvId)
                            ? pvId
                            : null
                    };
                }
                return true;
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                return false;
            }
        }

        private static bool TryInferResourcePvId(
            SongResourceKind kind,
            string path,
            out int pvId)
        {
            pvId = 0;
            var fileName = Path.GetFileNameWithoutExtension(path);
            var pattern = kind == SongResourceKind.Artwork
                ? ArtworkFilePvIdPattern
                : ChartFilePvIdPattern;
            var match = pattern.Match(fileName ?? String.Empty);
            return match.Success && Int32.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out pvId);
        }

        private static bool AddFlatDatabaseEntries(
            string databasePath,
            bool isNewClassicsMetadata,
            ModConfig config,
            string modRoot,
            string contentRoot,
            NewClassicsDatabase newClassics,
            IReadOnlyCollection<string> configWarnings,
            ICollection<SongEntry> entries,
            CancellationToken cancellationToken,
            VirtualAssetResolver assetResolver)
        {
            var database = ReadFlatDatabase(databasePath, cancellationToken);
            foreach (var song in database.Songs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                newClassics.Songs.TryGetValue(song.PvId, out var newClassicsSong);

                var warnings = new List<string>();
                AddRangeUnique(warnings, configWarnings);
                AddRangeUnique(warnings, database.Warnings);
                AddRangeUnique(warnings, newClassics.Warnings);
                if (song.RawPvIds.Count > 1)
                {
                    AddUnique(
                        warnings,
                        $"PVID {song.PvId} 使用了多个原始别名（{String.Join(", ", song.RawPvIds)}）；已将这些字段合并为同一首歌曲。");
                }

                var difficulties = ReadLegacyDifficulties(song, contentRoot, warnings);
                if (newClassicsSong != null)
                    difficulties.AddRange(CloneDifficulties(newClassicsSong.Difficulties));

                MarkEdenProjectOfficialExtensions(
                    song.PvId,
                    difficulties,
                    config,
                    contentRoot);

                var format = isNewClassicsMetadata
                    ? SongFormat.NewClassics
                    : newClassicsSong == null
                        ? SongFormat.Legacy
                        : SongFormat.LegacyWithNewClassics;

                var entry = new SongEntry
                {
                    ModName = config.ModName,
                    ModAuthor = config.ModAuthor,
                    ModVersion = config.ModVersion,
                    ModRoot = modRoot,
                    ContentRoot = contentRoot,
                    IsEdenProjectCore = config.IsEdenProjectCore,
                    HasValidEdenCoreSignature = config.HasValidEdenCoreSignature,
                    RequiresEdenProjectCore = config.RequiresEdenProjectCore,
                    DatabasePath = databasePath,
                    NewClassicsDatabasePath = newClassics.Path,
                    DatabaseHash = database.Hash,
                    DatabaseLastWriteTimeUtc = database.LastWriteTimeUtc,
                    NewClassicsDatabaseHash = newClassics.Hash,
                    NewClassicsDatabaseLastWriteTimeUtc = newClassics.LastWriteTimeUtc,
                    PvId = song.PvId,
                    RawPvId = song.RawPvId,
                    RawPvIds = song.RawPvIds,
                    SongName = GetFirstValue(song.Fields, "song_name", "name"),
                    SongNameEnglish = GetFirstValue(song.Fields, "song_name_en"),
                    SongNameReading = GetFirstValue(song.Fields, "song_name_reading"),
                    AuthorSummary = BuildAuthorSummary(song.Fields),
                    Format = format,
                    Difficulties = OrderDifficulties(difficulties),
                    AudioReference = GetFirstValue(song.Fields, "song_file_name"),
                    AlternateAudioReferences = song.Fields
                        .Where(field => field.Key.StartsWith("another_song.", StringComparison.Ordinal) &&
                            field.Key.EndsWith(".song_file_name", StringComparison.Ordinal) &&
                            Mega39PvDatabaseSyntax.IsIndexedFieldActive(
                                song.Fields,
                                field.Key,
                                out _))
                        .OrderBy(field => field.Key, StringComparer.Ordinal)
                        .Select(field => field.Value)
                        .Where(value => !String.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    VideoReference = GetFirstValue(song.Fields, "movie_file_name")
                };

                CompleteEntry(entry, warnings, song.Fields, assetResolver);
                entries.Add(entry);
            }
            return database.IsComplete;
        }

        private static SongEntry BuildAdditionalDifficultyEntry(
            ModConfig config,
            string modRoot,
            string contentRoot,
            NewClassicsDatabase database,
            NewClassicsSong song,
            IReadOnlyCollection<string> configWarnings,
            VirtualAssetResolver assetResolver)
        {
            var warnings = new List<string>();
            AddRangeUnique(warnings, configWarnings);
            AddRangeUnique(warnings, database.Warnings);
            var isOfficialNewClassicsExtension = Mega39PlusStockPvIds.Contains(song.PvId);
            if (!isOfficialNewClassicsExtension)
                AddUnique(warnings, "此 PVID 未找到 MEGA39+ mod_pv_db.txt 或 mod_nc_pv_db.txt 元数据。");

            var entry = new SongEntry
            {
                ModName = config.ModName,
                ModAuthor = config.ModAuthor,
                ModVersion = config.ModVersion,
                ModRoot = modRoot,
                ContentRoot = contentRoot,
                IsEdenProjectCore = config.IsEdenProjectCore,
                HasValidEdenCoreSignature = config.HasValidEdenCoreSignature,
                RequiresEdenProjectCore = config.RequiresEdenProjectCore,
                DatabasePath = String.Empty,
                NewClassicsDatabasePath = database.Path,
                DatabaseHash = database.Hash,
                DatabaseLastWriteTimeUtc = database.LastWriteTimeUtc,
                NewClassicsDatabaseHash = database.Hash,
                NewClassicsDatabaseLastWriteTimeUtc = database.LastWriteTimeUtc,
                PvId = song.PvId,
                RawPvId = song.PvId.ToString(CultureInfo.InvariantCulture),
                RawPvIds = new[] { song.PvId.ToString(CultureInfo.InvariantCulture) },
                SongName = isOfficialNewClassicsExtension
                    ? $"官曲 PV {song.PvId} New Classics 扩展"
                    : $"PV {song.PvId}",
                Format = SongFormat.AdditionalDifficulty,
                Difficulties = OrderDifficulties(CloneDifficulties(song.Difficulties)),
                IsSongPatch = true
            };

            CompleteEntry(entry, warnings, null, assetResolver);
            return entry;
        }

        private static void CompleteEntry(
            SongEntry entry,
            List<string> warnings,
            IReadOnlyDictionary<string, string> databaseFields = null,
            VirtualAssetResolver assetResolver = null)
        {
            entry.IsMega39PlusOfficialPvId = Mega39PlusStockPvIds.Contains(entry.PvId);
            var audio = ResolveVirtualAsset(
                entry.ContentRoot,
                entry.AudioReference,
                warnings,
                "音频",
                false,
                assetResolver);
            entry.AudioPath = audio.Path;
            entry.AudioExists = audio.Exists;
            var resolvedAlternateAudio = entry.AlternateAudioReferences
                .Select(reference => ResolveVirtualAsset(
                    entry.ContentRoot,
                    reference,
                    warnings,
                    "可选音频",
                    false,
                    assetResolver))
                .ToArray();
            entry.AlternateAudioPaths = resolvedAlternateAudio.Select(asset => asset.Path).ToArray();
            entry.AlternateAudioAvailability = resolvedAlternateAudio.Select(asset => asset.Exists).ToArray();
            var declaredVideoReferences = GetDeclaredVideoReferences(entry.VideoReference, databaseFields);
            var resolvedVideos = declaredVideoReferences
                .Select(reference => ResolveVirtualAsset(
                    entry.ContentRoot,
                    reference,
                    warnings,
                    "视频",
                    true,
                    assetResolver))
                .ToArray();
            entry.VideoReferences = declaredVideoReferences;
            entry.VideoPaths = resolvedVideos.Select(asset => asset.Path).ToArray();
            entry.VideoAvailability = resolvedVideos.Select(asset => asset.Exists).ToArray();
            var primaryVideoIndex = declaredVideoReferences
                .Select((reference, index) => new { reference, index })
                .Where(item => item.reference.Equals(entry.VideoReference, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            entry.VideoPath = primaryVideoIndex >= 0
                ? resolvedVideos[primaryVideoIndex].Path
                : String.Empty;
            entry.VideoExists = primaryVideoIndex >= 0 && resolvedVideos[primaryVideoIndex].Exists;

            var hasVerifiedEdenOfficialExtension =
                entry.IsEdenProjectCore &&
                entry.HasValidEdenCoreSignature &&
                entry.IsMega39PlusOfficialPvId &&
                entry.Difficulties.Any(difficulty => difficulty.IsOfficialLegacyExtension);
            if (hasVerifiedEdenOfficialExtension)
            {
                if (!entry.AudioExists && IsStockPvReference(entry.AudioReference, entry.PvId, "sound/song"))
                    entry.AudioExists = true;
                for (var index = 0; index < resolvedAlternateAudio.Length; index++)
                {
                    if (!resolvedAlternateAudio[index].Exists &&
                        IsStockReferencedMedia(entry.AlternateAudioReferences[index], "sound/song"))
                    {
                        resolvedAlternateAudio[index].Exists = true;
                    }
                }
                for (var index = 0; index < resolvedVideos.Length; index++)
                {
                    if (!resolvedVideos[index].Exists &&
                        IsStockReferencedMedia(declaredVideoReferences[index], "movie"))
                    {
                        resolvedVideos[index].Exists = true;
                    }
                }
                foreach (var difficulty in entry.Difficulties.Where(difficulty =>
                    !difficulty.ScriptExists &&
                    difficulty.Index == 0 &&
                    IsStockPvReference(difficulty.ScriptReference, entry.PvId, "script")))
                {
                    difficulty.ScriptExists = true;
                    difficulty.IsAvailableFromGame = true;
                    difficulty.UsesInheritedScript = true;
                }
                entry.AlternateAudioAvailability = resolvedAlternateAudio.Select(asset => asset.Exists).ToArray();
                entry.VideoAvailability = resolvedVideos.Select(asset => asset.Exists).ToArray();
                if (primaryVideoIndex >= 0)
                    entry.VideoExists = resolvedVideos[primaryVideoIndex].Exists;
            }
            var rawPvIds = GetAssetRawPvIds(entry);
            entry.ArtworkPaths = rawPvIds
                .Select(rawPvId => Path.Combine(
                    entry.ContentRoot,
                    "rom",
                    "2d",
                    $"spr_sel_pv{rawPvId}.farc"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            entry.ArtworkPath = entry.ArtworkPaths.FirstOrDefault(File.Exists) ??
                entry.ArtworkPaths.FirstOrDefault() ??
                String.Empty;
            entry.CoverExists = entry.ArtworkPaths.Any(File.Exists);
            entry.AdditionalParameterPaths = rawPvIds
                .Select(rawPvId => Path.Combine(
                    entry.ContentRoot,
                    "rom",
                    "add_param",
                    $"pv_{rawPvId}.adp"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            entry.AdditionalParameterPath = entry.AdditionalParameterPaths.FirstOrDefault(File.Exists) ?? String.Empty;
            entry.ExplicitAssetPaths = ResolveExistingExplicitAssets(
                entry.ContentRoot,
                databaseFields,
                warnings);

            var missingAssets = new List<string>();
            var blockingIssues = new List<string>();
            var degradedIssues = new List<string>();
            if (entry.Format != SongFormat.AdditionalDifficulty && !entry.IsSongPatch)
            {
                if (String.IsNullOrWhiteSpace(entry.AudioReference))
                {
                    AddUnique(warnings, "缺少音频引用。");
                    missingAssets.Add("音频引用");
                    AddUnique(blockingIssues, "缺少主音频引用。");
                }
                else if (!String.IsNullOrWhiteSpace(entry.AudioReference) && !entry.AudioExists)
                {
                    AddUnique(warnings, $"找不到音频文件：{entry.AudioReference}");
                    missingAssets.Add("音频");
                    AddUnique(blockingIssues, $"缺少主音频文件：{entry.AudioReference}");
                }

                var missingAlternateAudio = entry.AlternateAudioReferences
                    .Select((reference, index) => new
                    {
                        Reference = reference,
                        Asset = resolvedAlternateAudio[index]
                    })
                    .Where(item => !item.Asset.Exists)
                    .ToArray();
                if (missingAlternateAudio.Length > 0)
                {
                    AddUnique(warnings, $"找不到 {missingAlternateAudio.Length} 个可选演唱版本的音频文件。");
                    missingAssets.Add($"{missingAlternateAudio.Length} 个可选音频");
                    foreach (var missing in missingAlternateAudio)
                        AddUnique(degradedIssues, $"可选演唱版本因缺少音频文件而不可用：{missing.Reference}");
                }

                var missingVideoReferences = declaredVideoReferences
                    .Select((reference, index) => new
                    {
                        Reference = reference,
                        Asset = resolvedVideos[index]
                    })
                    .Where(item => !item.Asset.Exists)
                    .ToArray();
                if (missingVideoReferences.Length > 0)
                {
                    foreach (var missing in missingVideoReferences)
                    {
                        AddUnique(warnings, $"找不到视频文件：{missing.Reference}");
                        AddUnique(blockingIssues, $"缺少数据库声明的视频文件：{missing.Reference}");
                    }
                    missingAssets.Add(missingVideoReferences.Length == 1
                        ? "视频"
                        : $"{missingVideoReferences.Length} 个视频");
                }

            }

            var declaredScripts = entry.Difficulties
                .Where(difficulty =>
                    !String.IsNullOrWhiteSpace(difficulty.ScriptReference) ||
                    difficulty.HasDeclaredLegacyLength)
                .ToArray();
            var missingScriptDifficulties = declaredScripts
                .Where(difficulty =>
                    !difficulty.ScriptExists &&
                    !HasLocalChartForDifficulty(entry, difficulty))
                .ToArray();
            var missingScripts = missingScriptDifficulties.Length;
            var requiresSelfContainedCharts =
                entry.Format == SongFormat.AdditionalDifficulty ||
                !entry.IsSongPatch ||
                DeclaresKnownEdenProjectOfficialExtension(entry);
            if (requiresSelfContainedCharts && missingScripts > 0)
            {
                var missingPaths = missingScriptDifficulties
                    .Select(GetMissingChartPathDisplay)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var missingMessage =
                    $"数据库引用的 {declaredScripts.Length} 个谱面中有 {missingScripts} 个找不到。路径：{String.Join("; ", missingPaths)}";
                AddUnique(warnings, missingMessage);
                missingAssets.Add($"{missingScripts} 个谱面文件");
                if (entry.Difficulties.Any(difficulty => difficulty.ScriptExists))
                    AddUnique(degradedIssues, missingMessage);
                else
                    AddUnique(blockingIssues, missingMessage);
            }

            if (requiresSelfContainedCharts && entry.Difficulties.Count == 0)
            {
                AddUnique(warnings, "此歌曲条目未声明任何难度谱面。");
                missingAssets.Add("谱面引用");
                AddUnique(blockingIssues, "未声明任何难度谱面。");
            }
            else if (requiresSelfContainedCharts && !entry.Difficulties.Any(difficulty => difficulty.ScriptExists))
            {
                AddUnique(warnings, "此歌曲条目没有可用的谱面文件。");
                missingAssets.Add("可用谱面");
                AddUnique(blockingIssues, "没有找到任何可用的谱面文件。");
            }

            var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(entry.AudioPath))
                AddPath(referencedPaths, entry.AudioPath);
            foreach (var alternateAudioPath in entry.AlternateAudioPaths)
            {
                if (File.Exists(alternateAudioPath))
                    AddPath(referencedPaths, alternateAudioPath);
            }
            foreach (var videoPath in entry.VideoPaths)
            {
                if (File.Exists(videoPath))
                    AddPath(referencedPaths, videoPath);
            }
            foreach (var artworkPath in entry.ArtworkPaths)
                AddPath(referencedPaths, artworkPath);
            foreach (var additionalParameterPath in entry.AdditionalParameterPaths)
                AddPath(referencedPaths, additionalParameterPath);
            foreach (var explicitAssetPath in entry.ExplicitAssetPaths)
                AddPath(referencedPaths, explicitAssetPath);
            foreach (var difficulty in entry.Difficulties)
            {
                if (File.Exists(difficulty.ScriptPath))
                    AddPath(referencedPaths, difficulty.ScriptPath);
            }

            entry.ReferencedAssetPaths = referencedPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            entry.AssetStatus = missingAssets.Count == 0
                ? "完整"
                : $"缺失：{String.Join("、", missingAssets.Distinct(StringComparer.OrdinalIgnoreCase))}";
            SetRunStatus(entry, blockingIssues, degradedIssues);
            entry.Warnings = warnings.ToArray();
            entry.SearchText = BuildSearchText(entry);
        }

        private static string GetMissingChartPathDisplay(SongDifficulty difficulty)
        {
            if (!String.IsNullOrWhiteSpace(difficulty.ScriptPath))
                return difficulty.ScriptPath;
            if (!String.IsNullOrWhiteSpace(difficulty.ScriptReference))
                return difficulty.ScriptReference;

            var name = String.IsNullOrWhiteSpace(difficulty.NormalizedName)
                ? "未知难度"
                : difficulty.NormalizedName;
            return $"{name}[{difficulty.Index}]（未声明谱面路径）";
        }

        private static bool DefinesFullSong(SongEntry entry)
        {
            return entry.Format != SongFormat.AdditionalDifficulty &&
                !String.IsNullOrWhiteSpace(entry.AudioReference) &&
                entry.Difficulties.Count > 0;
        }

        private static void EvaluateArtworkAvailability(
            IEnumerable<SongEntry> entries,
            string modsFolder,
            CancellationToken cancellationToken)
        {
            var artworkService = new SongArtworkService(modsFolder);
            var candidates = entries.Where(entry =>
                !entry.IsSongPatch &&
                !entry.IsOrphanResourceEntry).ToArray();
            var availabilityByEntry = artworkService.ProbeAvailability(candidates, cancellationToken);
            foreach (var entry in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var availability = availabilityByEntry[entry];
                entry.ThumbnailExists = availability.Thumbnail;
                entry.JacketExists = availability.Jacket;
                entry.BackgroundExists = availability.Background;
                entry.CoverExists = availability.Jacket;
                if (entry.ArtworkComplete)
                    continue;

                var missingKinds = new List<string>();
                if (!availability.Thumbnail)
                    missingKinds.Add("小图标");
                if (!availability.Jacket)
                    missingKinds.Add("封面");
                if (!availability.Background)
                    missingKinds.Add("背景");

                var missingDisplay = String.Join("、", missingKinds);
                var reason = $"缺少歌曲图片：{missingDisplay}。";
                var reasons = entry.RunStatusReasons.ToList();
                AddUnique(reasons, reason);
                entry.RunStatusReasons = reasons.ToArray();
                if (entry.RunStatus == SongRunStatus.Ready)
                    entry.RunStatus = SongRunStatus.Warning;

                var warnings = entry.Warnings.ToList();
                AddUnique(warnings, reason);
                entry.Warnings = warnings.ToArray();
                entry.AssetStatus = entry.AssetStatus.Equals("完整", StringComparison.OrdinalIgnoreCase)
                    ? $"缺失：歌曲图片（{missingDisplay}）"
                    : $"{entry.AssetStatus}、歌曲图片（{missingDisplay}）";
            }
        }

        private static IReadOnlyList<string> GetDeclaredVideoReferences(
            string primaryReference,
            IReadOnlyDictionary<string, string> databaseFields)
        {
            var references = new List<string>();
            AddUnique(references, primaryReference);
            if (databaseFields != null)
            {
                foreach (var field in databaseFields.Where(field =>
                    IsVideoReferenceField(field.Key) &&
                    Mega39PvDatabaseSyntax.IsIndexedFieldActive(
                        databaseFields,
                        field.Key,
                        out _)))
                    AddUnique(references, field.Value);
            }
            return references;
        }

        private static void SetRunStatus(
            SongEntry entry,
            IReadOnlyCollection<string> blockingIssues,
            IReadOnlyCollection<string> degradedIssues)
        {
            var reasons = new List<string>();
            AddRangeUnique(reasons, blockingIssues);
            AddRangeUnique(reasons, degradedIssues);
            entry.RunStatusReasons = reasons.ToArray();
            entry.RunStatus = blockingIssues.Count > 0
                ? SongRunStatus.Broken
                : degradedIssues.Count > 0
                    ? SongRunStatus.Warning
                    : SongRunStatus.Ready;
        }

        private static IReadOnlyList<string> GetAssetRawPvIds(SongEntry entry)
        {
            var rawPvIds = new List<string>();
            AddUnique(rawPvIds, entry.RawPvId);
            AddRangeUnique(rawPvIds, entry.RawPvIds);
            AddUnique(rawPvIds, entry.PvId.ToString(CultureInfo.InvariantCulture));
            return rawPvIds;
        }

        private static IReadOnlyList<string> ResolveExistingExplicitAssets(
            string contentRoot,
            IReadOnlyDictionary<string, string> fields,
            List<string> warnings)
        {
            if (fields == null || fields.Count == 0)
                return Array.Empty<string>();

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in fields)
            {
                if (!IsRomFileReference(field.Value))
                    continue;

                var path = IsVideoReferenceField(field.Key)
                    ? ResolveVideoReference(contentRoot, field.Value, warnings)
                    : ResolveAssetReference(contentRoot, field.Value, warnings, "数据库资源");
                if (!Mega39PvDatabaseSyntax.IsIndexedFieldActive(fields, field.Key, out _))
                    continue;
                if (!String.IsNullOrWhiteSpace(path) && File.Exists(path))
                    paths.Add(path);
            }
            return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static bool IsRomFileReference(string reference)
        {
            if (String.IsNullOrWhiteSpace(reference))
                return false;
            var normalized = reference.Trim().Replace('\\', '/');
            return normalized.StartsWith("rom/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVideoReferenceField(string key)
        {
            if (String.IsNullOrWhiteSpace(key))
                return false;
            return key.Equals("movie_file_name", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("movie_list.", StringComparison.OrdinalIgnoreCase) &&
                    key.EndsWith(".name", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("difficulty.", StringComparison.OrdinalIgnoreCase) &&
                    key.EndsWith(".movie_file_name", StringComparison.OrdinalIgnoreCase);
        }

        private static List<SongDifficulty> ReadLegacyDifficulties(
            FlatSong song,
            string contentRoot,
            List<string> warnings)
        {
            var records = new Dictionary<string, SongDifficulty>(StringComparer.OrdinalIgnoreCase);
            var declaredLengths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var invalidLengthNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in song.Fields)
            {
                var lengthMatch = DifficultyLengthFieldPattern.Match(field.Key);
                if (!lengthMatch.Success)
                    continue;

                var name = lengthMatch.Groups[1].Value;
                if (Int32.TryParse(
                        field.Value?.Trim(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var length) &&
                    length >= 0 &&
                    length <= MaxDeclaredDatabaseArrayLength)
                {
                    declaredLengths[name] = length;
                }
                else
                {
                    invalidLengthNames.Add(name);
                    AddUnique(
                        warnings,
                        $"difficulty.{name}.length 的值“{field.Value}”无效；为兼容此模组，仍解析了带索引的难度槽位。");
                }
            }

            foreach (var field in song.Fields)
            {
                var match = DifficultyFieldPattern.Match(field.Key);
                if (!match.Success || !Int32.TryParse(match.Groups[2].Value, out var index))
                    continue;

                var name = match.Groups[1].Value;
                if (declaredLengths.TryGetValue(name, out var declaredLength) && index >= declaredLength)
                    continue;
                var key = $"{name}\0{index}";
                if (!records.TryGetValue(key, out var difficulty))
                {
                    difficulty = new SongDifficulty
                    {
                        Name = name,
                        Index = index,
                        Source = SongDifficultySource.LegacyDatabase,
                        HasDeclaredLegacyLength = declaredLengths.ContainsKey(name),
                        DeclaredLegacyLength = declaredLength
                    };
                    records.Add(key, difficulty);
                }

                switch (match.Groups[3].Value.ToLowerInvariant())
                {
                    case "level":
                        difficulty.Level = field.Value;
                        break;
                    case "script_file_name":
                        difficulty.ScriptReference = field.Value;
                        break;
                    case "attribute.extra":
                        difficulty.IsExtra = field.Value.Trim().Equals("1", StringComparison.Ordinal) ||
                            field.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }

            foreach (var pair in declaredLengths)
            {
                for (var index = 0; index < pair.Value; index++)
                {
                    var key = $"{pair.Key}\0{index}";
                    if (records.ContainsKey(key))
                        continue;

                    records.Add(key, new SongDifficulty
                    {
                        Name = pair.Key,
                        Index = index,
                        Source = SongDifficultySource.LegacyDatabase,
                        HasDeclaredLegacyLength = true,
                        DeclaredLegacyLength = pair.Value
                    });
                    AddUnique(
                        warnings,
                        $"difficulty.{pair.Key}.length 声明了槽位 {index}，但该槽位没有任何数据库字段。");
                }
            }

            foreach (var difficulty in records.Values)
            {
                CompleteDifficultyMetadata(difficulty);
                var localPath = ResolveAssetReference(
                    contentRoot,
                    difficulty.ScriptReference,
                    warnings,
                    "谱面");
                difficulty.ScriptPath = localPath;
                difficulty.ScriptExists = !String.IsNullOrEmpty(localPath) && File.Exists(localPath);
            }
            return records.Values.ToList();
        }

        private static NewClassicsDatabase ReadNewClassicsDatabase(
            string path,
            string contentRoot,
            CancellationToken cancellationToken)
        {
            var result = new NewClassicsDatabase { Path = path ?? String.Empty };
            if (String.IsNullOrEmpty(path))
                return result;

            try
            {
                var snapshot = ReadFileSnapshot(path);
                result.Hash = snapshot.Hash;
                result.LastWriteTimeUtc = snapshot.LastWriteTimeUtc;
                if (!Toml.TryToModel(snapshot.Text, out TomlTable model, out var diagnostics))
                {
                    result.IsComplete = false;
                    var detail = "TOML 格式无效或语法有误。";
                    AddUnique(result.Warnings, $"nc_db.toml 解析警告：{detail}");
                    return result;
                }

                if (!model.TryGetValue("songs", out var songsValue) || songsValue is not IEnumerable songs)
                {
                    result.IsComplete = false;
                    AddUnique(result.Warnings, "nc_db.toml 不包含有效的 songs 数组。");
                    return result;
                }

                foreach (var value in songs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (value is not TomlTable songTable ||
                        !songTable.TryGetValue("id", out var idValue) ||
                        !TryGetPositiveInt32(idValue, out var pvId))
                    {
                        result.IsComplete = false;
                        AddUnique(result.Warnings, "nc_db.toml 中有歌曲缺少有效的 PVID。");
                        continue;
                    }

                    if (!result.Songs.TryGetValue(pvId, out var song))
                    {
                        song = new NewClassicsSong { PvId = pvId };
                        result.Songs.Add(pvId, song);
                    }

                    ReadNewClassicsDifficulties(
                        songTable,
                        song,
                        contentRoot,
                        result.Warnings);
                }
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                result.IsComplete = false;
                AddUnique(result.Warnings, $"读取 nc_db.toml 时发生警告：{UserErrorMessage.From(exception)}");
            }
            return result;
        }

        private static void ReadNewClassicsDifficulties(
            TomlTable songTable,
            NewClassicsSong song,
            string contentRoot,
            List<string> warnings)
        {
            foreach (var difficultyName in NewClassicsDifficultyNames)
            {
                ReadNewClassicsDifficultyArray(
                    songTable,
                    difficultyName,
                    difficultyName,
                    song,
                    contentRoot,
                    warnings);
            }

            // New Classics represents extra charts as nested arrays, for example
            // [[songs.extra.extreme]], rather than as a direct ex_extreme key.
            if (songTable.TryGetValue("extra", out var extraValue) && extraValue is TomlTable extraTable)
            {
                foreach (var difficultyName in NewClassicsDifficultyNames.Where(name => !name.StartsWith("ex_", StringComparison.Ordinal)))
                {
                    ReadNewClassicsDifficultyArray(
                        extraTable,
                        difficultyName,
                        "ex_" + difficultyName,
                        song,
                        contentRoot,
                        warnings);
                }
            }
        }

        private static void ReadNewClassicsDifficultyArray(
            TomlTable table,
            string tableKey,
            string displayName,
            NewClassicsSong song,
            string contentRoot,
            List<string> warnings)
        {
            if (!table.TryGetValue(tableKey, out var chartsValue) ||
                chartsValue is string ||
                chartsValue is not IEnumerable charts)
                return;

            var index = song.Difficulties.Count(difficulty =>
                difficulty.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase));
            foreach (var chartValue in charts)
            {
                if (chartValue is not TomlTable chart)
                    continue;

                var level = GetTomlString(chart, "level");
                var style = GetTomlString(chart, "style");
                var charter = GetTomlString(chart, "charter");
                var scriptReference = GetTomlString(chart, "script_file_name");
                if (String.IsNullOrWhiteSpace(level) &&
                    String.IsNullOrWhiteSpace(style) &&
                    String.IsNullOrWhiteSpace(charter) &&
                    String.IsNullOrWhiteSpace(scriptReference))
                {
                    continue;
                }

                var difficulty = new SongDifficulty
                {
                    Name = displayName,
                    Index = index++,
                    Level = level,
                    Style = style,
                    Charter = charter,
                    ScriptReference = scriptReference,
                    Source = SongDifficultySource.NewClassicsDatabase,
                    IsDeclaredByNewClassicsDatabase = true
                };
                CompleteDifficultyMetadata(difficulty);
                difficulty.ScriptPath = ResolveAssetReference(
                    contentRoot,
                    difficulty.ScriptReference,
                    warnings,
                    "chart");
                difficulty.ScriptExists = !String.IsNullOrEmpty(difficulty.ScriptPath) && File.Exists(difficulty.ScriptPath);
                song.Difficulties.Add(difficulty);
            }
        }

        private static FlatDatabase ReadFlatDatabase(string path, CancellationToken cancellationToken)
        {
            var result = new FlatDatabase();
            try
            {
                var snapshot = ReadFileSnapshot(path);
                result.Hash = snapshot.Hash;
                result.LastWriteTimeUtc = snapshot.LastWriteTimeUtc;
                var songs = new Dictionary<int, FlatSong>();
                using var reader = new StringReader(snapshot.Text);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Mega39PvDatabaseSyntax.TryParse(line, out var field))
                        continue;

                    if (!songs.TryGetValue(field.PvId, out var song))
                    {
                        song = new FlatSong { PvId = field.PvId };
                        songs.Add(field.PvId, song);
                    }
                    song.AddField(field.RawPvId, field.Key, field.Value);
                }

                foreach (var song in songs.Values)
                    song.FinalizeAliases();
                result.Songs.AddRange(songs.Values.OrderBy(song => song.PvId));
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                result.IsComplete = false;
                AddUnique(result.Warnings, $"读取 PV 数据库时发生警告：{UserErrorMessage.From(exception)}");
            }
            return result;
        }

        private static FileSnapshot ReadFileSnapshot(string path)
        {
            var bytes = File.ReadAllBytes(path);
            string text;
            using (var memory = new MemoryStream(bytes, false))
            using (var reader = new StreamReader(memory, Encoding.UTF8, true))
                text = reader.ReadToEnd();

            return new FileSnapshot
            {
                Text = text,
                Hash = Convert.ToHexString(SHA256.HashData(bytes)),
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(path)
            };
        }

        private static ModConfig ReadModConfig(string modRoot)
        {
            var result = new ModConfig { ModName = Path.GetFileName(modRoot) };
            var path = Path.Combine(modRoot, "config.toml");
            if (!File.Exists(path))
            {
                result.IsComplete = false;
                result.IsEnabled = false;
                result.ContentRoots.Add(Path.GetFullPath(modRoot));
                AddUnique(result.Warnings, "缺少 config.toml；DivaModLoader 将跳过此模组。管理器仅为管理用途扫描了模组根目录。");
                return result;
            }

            try
            {
                if (!Toml.TryToModel(File.ReadAllText(path), out TomlTable config, out var diagnostics))
                {
                    var detail = "TOML 格式无效或语法有误。";
                    AddUnique(result.Warnings, $"config.toml 解析警告：{detail}");
                    result.CanScan = false;
                    result.IsComplete = false;
                    return result;
                }

                if (config.TryGetValue("name", out var nameValue) &&
                    nameValue is string configuredName &&
                    !String.IsNullOrWhiteSpace(configuredName))
                    result.ModName = configuredName.Trim();

                if (config.TryGetValue("author", out var authorValue) &&
                    authorValue is string configuredAuthor &&
                    !String.IsNullOrWhiteSpace(configuredAuthor))
                    result.ModAuthor = configuredAuthor.Trim();

                if (config.TryGetValue("version", out var versionValue) &&
                    versionValue is string configuredVersion &&
                    !String.IsNullOrWhiteSpace(configuredVersion))
                    result.ModVersion = configuredVersion.Trim();

                result.IsEdenProjectCore =
                    result.ModName.Equals(EdenProjectCoreName, StringComparison.OrdinalIgnoreCase) &&
                    result.ModAuthor.Equals(EdenProjectAuthor, StringComparison.OrdinalIgnoreCase);
                result.RequiresEdenProjectCore =
                    !result.IsEdenProjectCore &&
                    result.ModAuthor.Equals(EdenProjectAuthor, StringComparison.OrdinalIgnoreCase) &&
                    result.ModName.StartsWith("Eden Project - ", StringComparison.OrdinalIgnoreCase);
                if (result.RequiresEdenProjectCore &&
                    !String.IsNullOrWhiteSpace(result.ModVersion) &&
                    !result.ModVersion.Equals(CurrentEdenProjectSongPackVersion, StringComparison.OrdinalIgnoreCase))
                {
                    AddUnique(
                        result.Warnings,
                        $"此 Eden Project 曲包版本为 {result.ModVersion}；已核实的 v5.6 发布版曲包版本为 {CurrentEdenProjectSongPackVersion}。混装或较旧的 Eden 版本可能缺少已知资源。");
                }

                if (config.TryGetValue("enabled", out var enabledValue) &&
                    enabledValue is bool enabled)
                    result.IsEnabled = enabled;

                if (!config.TryGetValue("include", out var includeValue))
                {
                    result.IsEnabled = false;
                    result.ContentRoots.Add(Path.GetFullPath(modRoot));
                    AddUnique(result.Warnings, "config.toml 没有 include 列表；DivaModLoader 不会挂载其中的文件。管理器仅为管理用途扫描了模组根目录。");
                    return result;
                }

                if (includeValue is string || includeValue is not IEnumerable includes)
                {
                    result.CanScan = false;
                    result.IsComplete = false;
                    return result;
                }

                foreach (var include in includes)
                {
                    if (include is not string includePath)
                    {
                        result.IsComplete = false;
                        continue;
                    }

                    if (String.IsNullOrWhiteSpace(includePath))
                    {
                        result.IsComplete = false;
                        AddUnique(result.Warnings, "DivaModLoader 会忽略空的 include 路径。");
                        continue;
                    }

                    var contentRoot = ResolveIncludeRoot(modRoot, includePath);
                    if (contentRoot == null)
                    {
                        AddUnique(result.Warnings, $"已忽略无效的 include 路径：{includePath}");
                        result.IsComplete = false;
                        continue;
                    }

                    if (!result.ContentRoots.Contains(contentRoot, StringComparer.OrdinalIgnoreCase))
                        result.ContentRoots.Add(contentRoot);
                }

                if (result.ContentRoots.Count == 0)
                {
                    result.CanScan = false;
                    result.IsComplete = false;
                }

                if (result.IsEdenProjectCore)
                {
                    result.HasValidEdenCoreSignature = HasEdenProjectCoreSignature(modRoot, config);
                    if (!result.HasValidEdenCoreSignature)
                    {
                        AddUnique(
                            result.Warnings,
                            "此模组自称 Eden Core，但缺少必需的 Core 数据库或 DLL 特征文件。");
                    }
                }
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                AddUnique(result.Warnings, $"读取 config.toml 时发生警告：{UserErrorMessage.From(exception)}");
                result.CanScan = false;
                result.IsComplete = false;
            }
            return result;
        }

        private static LoaderActivation ReadLoaderActivation(string modsFolder)
        {
            var activation = new LoaderActivation();
            try
            {
                var fullModsFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsFolder));
                var parent = Path.GetDirectoryName(fullModsFolder);
                if (String.IsNullOrWhiteSpace(parent))
                    return activation;

                var configPath = Path.Combine(parent, "config.toml");
                if (!File.Exists(configPath) ||
                    !Toml.TryToModel(File.ReadAllText(configPath), out TomlTable config, out _))
                {
                    return activation;
                }

                if (config.TryGetValue("enabled", out var enabledValue) &&
                    enabledValue is bool enabled &&
                    !enabled)
                {
                    activation.LoaderEnabled = false;
                }

                if (config.TryGetValue("priority", out var priorityValue) &&
                    priorityValue is not string &&
                    priorityValue is IEnumerable priorityItems)
                {
                    foreach (var item in priorityItems)
                    {
                        if (item is string name && !String.IsNullOrWhiteSpace(name))
                        {
                            var normalized = NormalizePriorityName(name);
                            if (String.IsNullOrWhiteSpace(normalized))
                                continue;
                            try
                            {
                                var priorityPath = Path.TrimEndingDirectorySeparator(
                                    Path.GetFullPath(Path.Combine(fullModsFolder, normalized)));
                                activation.PriorityPaths.Add(priorityPath);
                            }
                            catch (Exception exception) when (
                                IsFileSystemException(exception) ||
                                exception is ArgumentException)
                            {
                                // Match DivaModLoader by ignoring invalid priority entries.
                            }
                        }
                    }
                    activation.RestrictsMods = activation.PriorityPaths.Count > 0;
                }
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                return new LoaderActivation();
            }

            return activation;
        }

        private static string NormalizePriorityName(string name)
        {
            var normalized = (name ?? String.Empty)
                .Trim()
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
            while (normalized.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                normalized = normalized.Substring(2);
            return normalized;
        }

        private static bool HasEdenProjectCoreSignature(string modRoot, TomlTable config)
        {
            if (String.IsNullOrWhiteSpace(modRoot) ||
                !File.Exists(Path.Combine(modRoot, "rom", LegacyDatabaseName)) ||
                !File.Exists(Path.Combine(modRoot, "settings.toml")))
            {
                return false;
            }

            if (!config.TryGetValue("dll", out var dllValue) ||
                dllValue is string ||
                dllValue is not IEnumerable dlls)
            {
                return false;
            }

            var declaredDlls = dlls
                .OfType<string>()
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var required in new[] { "DLCChecker.dll", "OldMan.dll", "SaveDataMigrator.dll" })
            {
                if (!declaredDlls.Contains(required) ||
                    !File.Exists(Path.Combine(modRoot, required)))
                {
                    return false;
                }
            }
            return true;
        }

        private static void ValidateKnownModDependencies(
            IEnumerable<SongEntry> entries,
            IEnumerable<ModScanContext> scanContexts)
        {
            var activeCore = scanContexts.Any(context =>
                context.IsActiveByLoader &&
                context.Config.IsEnabled &&
                context.Config.IsEdenProjectCore &&
                context.Config.HasValidEdenCoreSignature);
            if (activeCore)
                return;

            foreach (var entry in entries.Where(entry =>
                entry.ModEnabled &&
                !entry.IsOrphanResourceEntry &&
                entry.RequiresEdenProjectCore))
            {
                AddBlockingRunStatusReason(
                    entry,
                    "此 Eden Project 曲包需要已启用且完整的 Eden Core。当前安装缺少 Eden Core 或其必需的 Core 文件。");
            }
        }

        private static void MarkEdenProjectOfficialExtensions(
            int pvId,
            IEnumerable<SongDifficulty> difficulties,
            ModConfig config,
            string contentRoot)
        {
            if (!config.IsEdenProjectCore ||
                !config.HasValidEdenCoreSignature ||
                !EdenProjectOfficialExtraExtremePvIds.Contains(pvId))
            {
                return;
            }

            foreach (var difficulty in difficulties.Where(difficulty =>
                difficulty.Source == SongDifficultySource.LegacyDatabase &&
                difficulty.Name.Equals("extreme", StringComparison.OrdinalIgnoreCase) &&
                difficulty.Index == 1 &&
                difficulty.IsExtra &&
                difficulty.ScriptExists &&
                difficulty.HasDeclaredLegacyLength &&
                difficulty.DeclaredLegacyLength >= 2))
            {
                var expectedName = $"pv_{pvId:D3}_extreme_1.dsc";
                if (Path.GetFileName(difficulty.ScriptPath).Equals(
                    expectedName,
                    StringComparison.OrdinalIgnoreCase) &&
                    IsUnderRoot(contentRoot, difficulty.ScriptPath))
                {
                    difficulty.IsOfficialLegacyExtension = true;
                }
            }
        }

        internal static bool IsKnownEdenProjectOfficialExtension(
            SongEntry song,
            SongDifficulty difficulty)
        {
            return song != null &&
                difficulty != null &&
                song.IsEdenProjectCore &&
                song.HasValidEdenCoreSignature &&
                EdenProjectOfficialExtraExtremePvIds.Contains(song.PvId) &&
                difficulty.IsOfficialLegacyExtension;
        }

        private static bool DeclaresKnownEdenProjectOfficialExtension(SongEntry entry)
        {
            if (entry == null ||
                !entry.IsEdenProjectCore ||
                !entry.HasValidEdenCoreSignature ||
                !EdenProjectOfficialExtraExtremePvIds.Contains(entry.PvId))
            {
                return false;
            }

            var expectedName = $"pv_{entry.PvId:D3}_extreme_1.dsc";
            return entry.Difficulties.Any(difficulty =>
                difficulty.Source == SongDifficultySource.LegacyDatabase &&
                difficulty.Name.Equals("extreme", StringComparison.OrdinalIgnoreCase) &&
                difficulty.Index == 1 &&
                difficulty.IsExtra &&
                difficulty.HasDeclaredLegacyLength &&
                difficulty.DeclaredLegacyLength >= 2 &&
                Path.GetFileName(difficulty.ScriptReference).Equals(
                    expectedName,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsGameStockMediaReference(string reference, string assetDirectory)
        {
            return IsStockReferencedMedia(reference, assetDirectory);
        }

        private static string ResolveIncludeRoot(string modRoot, string includePath)
        {
            try
            {
                var normalized = (includePath ?? String.Empty)
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                var fullPath = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(Path.Combine(modRoot, normalized)));
                return fullPath;
            }
            catch (Exception exception) when (IsFileSystemException(exception) || exception is ArgumentException)
            {
                return null;
            }
        }

        private static string ResolveAssetReference(
            string contentRoot,
            string reference,
            List<string> warnings,
            string assetKind)
        {
            if (String.IsNullOrWhiteSpace(reference))
                return String.Empty;

            try
            {
                var normalized = reference
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(contentRoot, normalized));
                if (IsUnderRoot(contentRoot, fullPath))
                    return fullPath;

                AddUnique(warnings, $"已忽略越过内容目录的{assetKind}路径：{reference}");
            }
            catch (Exception exception) when (IsFileSystemException(exception) || exception is ArgumentException)
            {
                AddUnique(warnings, $"无效的{assetKind}路径：{reference}");
            }
            return String.Empty;
        }

        private static string ResolveVideoReference(
            string contentRoot,
            string reference,
            List<string> warnings)
        {
            var declaredPath = ResolveAssetReference(contentRoot, reference, warnings, "视频");
            if (String.IsNullOrEmpty(declaredPath) || File.Exists(declaredPath))
                return declaredPath;

            foreach (var extension in new[] { ".usm", ".mp4" })
            {
                var candidatePath = Path.ChangeExtension(declaredPath, extension);
                if (candidatePath.Equals(declaredPath, StringComparison.OrdinalIgnoreCase) ||
                    !IsUnderRoot(contentRoot, candidatePath))
                    continue;

                if (File.Exists(candidatePath))
                    return candidatePath;
            }

            return declaredPath;
        }

        private static ResolvedVirtualAsset ResolveVirtualAsset(
            string contentRoot,
            string reference,
            List<string> warnings,
            string assetKind,
            bool allowVideoExtensionFallback,
            VirtualAssetResolver assetResolver)
        {
            var localPath = allowVideoExtensionFallback
                ? ResolveVideoReference(contentRoot, reference, warnings)
                : ResolveAssetReference(contentRoot, reference, warnings, assetKind);
            if (!String.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                return new ResolvedVirtualAsset
                {
                    Path = localPath,
                    Exists = true
                };
            }

            if (assetResolver?.TryResolve(
                    reference,
                    allowVideoExtensionFallback,
                    out var resolvedPath,
                    out var fromGame) == true)
            {
                return new ResolvedVirtualAsset
                {
                    Path = resolvedPath,
                    Exists = true,
                    FromGame = fromGame
                };
            }

            return new ResolvedVirtualAsset { Path = localPath };
        }

        private static bool IsUnderRoot(string root, string path)
        {
            var relativePath = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return relativePath == "." ||
                (!Path.IsPathRooted(relativePath) &&
                    !relativePath.Equals("..", StringComparison.Ordinal) &&
                    !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }

        private static bool ContainsReparsePoint(string root, string target)
        {
            var fullRoot = Path.GetFullPath(root);
            var fullTarget = Path.GetFullPath(target);
            if (!IsUnderRoot(fullRoot, fullTarget))
                return true;

            var current = new DirectoryInfo(fullRoot);
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;

            var relative = Path.GetRelativePath(fullRoot, fullTarget);
            if (relative == ".")
                return false;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (String.IsNullOrWhiteSpace(segment))
                    continue;
                current = new DirectoryInfo(Path.Combine(current.FullName, segment));
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            return false;
        }

        private static IReadOnlyList<string> GetTopLevelFiles(string directory, out bool isComplete)
        {
            try
            {
                isComplete = true;
                return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                isComplete = false;
                return Array.Empty<string>();
            }
        }

        private static IReadOnlyList<string> FindFiles(IEnumerable<string> files, string fileName)
        {
            return files
                .Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string GetFirstValue(
            IReadOnlyDictionary<string, string> fields,
            params string[] keys)
        {
            foreach (var key in keys)
            {
                if (fields.TryGetValue(key, out var value) && !String.IsNullOrWhiteSpace(value))
                    return value;
            }
            return String.Empty;
        }

        private static string BuildAuthorSummary(IReadOnlyDictionary<string, string> fields)
        {
            var keys = new[]
            {
                "songinfo.music", "songinfo.lyrics", "songinfo.arranger", "songinfo.pv_editor",
                "songinfo_en.music", "songinfo_en.lyrics", "songinfo_en.arranger", "songinfo_en.pv_editor"
            };
            var values = keys
                .Select(key => GetFirstValue(fields, key))
                .Where(value => !String.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return String.Join(", ", values);
        }

        private static string GetTomlString(TomlTable table, string key)
        {
            return table.TryGetValue(key, out var value) && value is string text
                ? text
                : String.Empty;
        }

        private static bool TryGetPositiveInt32(object value, out int result)
        {
            if (value is long number && number > 0 && number <= Int32.MaxValue)
            {
                result = (int)number;
                return true;
            }
            result = 0;
            return false;
        }

        private static IReadOnlyList<SongDifficulty> OrderDifficulties(IEnumerable<SongDifficulty> difficulties)
        {
            return difficulties
                .OrderBy(difficulty => DifficultyOrder(difficulty.NormalizedName))
                .ThenBy(difficulty => difficulty.Index)
                .ThenBy(difficulty => difficulty.Source)
                .ToArray();
        }

        private static void CompleteDifficultyMetadata(SongDifficulty difficulty)
        {
            var normalizedName = (difficulty.Name ?? String.Empty)
                .Trim()
                .Replace('-', '_')
                .ToLowerInvariant();
            if (normalizedName.StartsWith("extra_", StringComparison.Ordinal))
                normalizedName = "ex_" + normalizedName.Substring("extra_".Length);
            if (normalizedName.StartsWith("ex_", StringComparison.Ordinal))
                difficulty.IsExtra = true;
            else if (difficulty.IsExtra && !String.IsNullOrWhiteSpace(normalizedName))
                normalizedName = "ex_" + normalizedName;

            difficulty.NormalizedName = normalizedName;
            difficulty.NumericLevel = TryParseDifficultyLevel(difficulty.Level, out var level)
                ? level
                : (decimal?)null;
        }

        private static bool TryParseDifficultyLevel(string rawLevel, out decimal level)
        {
            var match = DifficultyLevelPattern.Match((rawLevel ?? String.Empty).Trim());
            if (match.Success &&
                Int32.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) &&
                Int32.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var half))
            {
                level = whole + half / 10m;
                if (level >= 1m && level <= 10m)
                    return true;
            }

            level = 0m;
            return false;
        }

        private static int DifficultyOrder(string name)
        {
            for (var index = 0; index < NewClassicsDifficultyNames.Length; index++)
            {
                if (NewClassicsDifficultyNames[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return index;
            }
            return Int32.MaxValue;
        }

        private static IEnumerable<SongDifficulty> CloneDifficulties(IEnumerable<SongDifficulty> difficulties)
        {
            return difficulties.Select(difficulty => new SongDifficulty
            {
                Name = difficulty.Name,
                NormalizedName = difficulty.NormalizedName,
                IsExtra = difficulty.IsExtra,
                Index = difficulty.Index,
                Level = difficulty.Level,
                NumericLevel = difficulty.NumericLevel,
                Style = difficulty.Style,
                Charter = difficulty.Charter,
                ScriptReference = difficulty.ScriptReference,
                ScriptPath = difficulty.ScriptPath,
                ScriptExists = difficulty.ScriptExists,
                IsAvailableFromGame = difficulty.IsAvailableFromGame,
                UsesInheritedScript = difficulty.UsesInheritedScript,
                Source = difficulty.Source,
                IsDeclaredByNewClassicsDatabase = difficulty.IsDeclaredByNewClassicsDatabase,
                HasDeclaredLegacyLength = difficulty.HasDeclaredLegacyLength,
                DeclaredLegacyLength = difficulty.DeclaredLegacyLength,
                IsOfficialLegacyExtension = difficulty.IsOfficialLegacyExtension
            });
        }

        private static string BuildSearchText(SongEntry entry)
        {
            var values = new List<string>
            {
                entry.PvId.ToString(CultureInfo.InvariantCulture),
                entry.RawPvId,
                entry.ModName,
                entry.SongName,
                entry.SongNameEnglish,
                entry.SongNameReading,
                entry.AuthorSummary,
                entry.FormatDisplayName,
                entry.DifficultiesDisplay
            };
            values.AddRange(entry.RawPvIds);
            values.AddRange(entry.Difficulties.Select(difficulty => difficulty.Charter));
            values.AddRange(entry.OrphanResources.Select(resource => resource.DisplayName));
            values.AddRange(entry.OrphanResources.Select(resource => resource.RelativePath));
            values.AddRange(entry.OrphanResources.Select(resource => resource.Path));
            return String.Join(" ", values.Where(value => !String.IsNullOrWhiteSpace(value)));
        }

        private static void MarkCrossModIdConflicts(ICollection<SongEntry> entries)
        {
            foreach (var group in entries.GroupBy(entry => entry.PvId))
            {
                var runtimeEntries = group
                    .Where(entry => !entry.IsOrphanResourceEntry)
                    .ToArray();
                if (runtimeEntries.Length == 0)
                    continue;

                var nonAdditionalEntries = runtimeEntries
                    .Where(entry => entry.Format != SongFormat.AdditionalDifficulty)
                    .ToArray();
                ValidateMega39PlusOfficialChartMods(runtimeEntries);
                var completeProviders = nonAdditionalEntries
                    .Where(entry => entry.ModEnabled && HasCompleteCoreAssets(entry))
                    .ToArray();

                foreach (var candidate in nonAdditionalEntries.Where(entry =>
                    DefinesFullSong(entry) &&
                    !HasCompleteCoreAssets(entry) &&
                    HasMissingDeclaredCoreAssets(entry)))
                {
                    var providers = FindCompatiblePatchProviders(candidate, completeProviders);
                    var canUseStockAssets = CanUseMega39PlusStockAssets(candidate);
                    if ((providers.Length > 0 || canUseStockAssets) &&
                        CanInheritMissingCoreAssets(candidate, providers, canUseStockAssets))
                    {
                        SetPatchRunStatus(candidate, providers, canUseStockAssets);
                    }
                }

                ValidateAdditionalDifficultyTargets(runtimeEntries, nonAdditionalEntries);
                ValidateImplicitChartReferences(runtimeEntries, nonAdditionalEntries);
                ValidateMega39PlusOfficialChartMods(runtimeEntries);
                AttachPatchSources(group.Key, nonAdditionalEntries);

                var fullSongDefinitions = nonAdditionalEntries
                    .Where(entry => entry.ModEnabled && DefinesFullSong(entry) && !entry.IsSongPatch)
                    .ToArray();
                if (fullSongDefinitions.Length < 2)
                    continue;

                var conflictSources = fullSongDefinitions
                    .Select(CreateSourcePath)
                    .ToArray();
                if (AreSameSourceDefinitionsForSameSong(fullSongDefinitions))
                {
                    MarkSameSourceDifficultyOverlaps(group.Key, fullSongDefinitions, conflictSources);
                    continue;
                }

                var modNames = fullSongDefinitions
                    .Select(entry => entry.ModName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
                var conflictPaths = conflictSources
                    .Select(source => source.SourcePath)
                    .Where(path => !String.IsNullOrWhiteSpace(path));
                var warning = $"PVID {group.Key} 在多个模组中定义了完整歌曲：{String.Join("、", modNames)}。路径：{String.Join("; ", conflictPaths)}";
                foreach (var entry in fullSongDefinitions)
                {
                    entry.HasIdConflict = true;
                    entry.IdConflictSources = conflictSources;
                    var warnings = entry.Warnings.ToList();
                    AddUnique(warnings, warning);
                    entry.Warnings = warnings.ToArray();
                    var runStatusReasons = entry.RunStatusReasons.ToList();
                    AddUnique(runStatusReasons, warning);
                    entry.RunStatusReasons = runStatusReasons.ToArray();
                    entry.RunStatus = SongRunStatus.Broken;
                }
            }
        }

        private static bool AreSameSourceDefinitionsForSameSong(
            IReadOnlyCollection<SongEntry> entries)
        {
            var modNames = entries
                .Select(entry => entry.ModName?.Trim())
                .Where(name => !String.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modNames.Length != 1 || entries.Any(entry => String.IsNullOrWhiteSpace(entry.ModName)))
                return false;

            var declaredAuthors = entries
                .Select(entry => entry.ModAuthor?.Trim())
                .Where(author => !String.IsNullOrWhiteSpace(author))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (declaredAuthors.Length > 1)
                return false;

            HashSet<string> commonNames = null;
            foreach (var entry in entries)
            {
                var names = GetComparableSongNames(entry)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (names.Count == 0)
                    return false;

                if (commonNames == null)
                    commonNames = names;
                else
                    commonNames.IntersectWith(names);
            }

            return commonNames?.Count > 0;
        }

        private static void MarkSameSourceDifficultyOverlaps(
            int pvId,
            IReadOnlyList<SongEntry> entries,
            IReadOnlyList<SongSourcePath> sources)
        {
            // Separate content roots in one loader mod can intentionally contribute
            // independent difficulty slots. Keep every source path available to the UI
            // even when those slots are compatible and are not an ID conflict.
            foreach (var entry in entries)
                entry.IdConflictSources = sources;

            for (var leftIndex = 0; leftIndex < entries.Count - 1; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < entries.Count; rightIndex++)
                {
                    var left = entries[leftIndex];
                    var right = entries[rightIndex];
                    var overlap = CompareSameSourceDifficultySlots(left, right, out var detail);
                    if (overlap == SameSourceDifficultyOverlap.None)
                        continue;

                    var pairPaths = new[] { CreateSourcePath(left), CreateSourcePath(right) }
                        .Select(source => source.SourcePath)
                        .Where(path => !String.IsNullOrWhiteSpace(path));
                    var message = overlap == SameSourceDifficultyOverlap.DifferentKnownStars
                        ? $"PVID {pvId} 的同来源同名歌曲重复使用了难度，但已知星级没有重复（{detail}）。该组合可能依赖数据库覆盖顺序，判定为勉强运行。路径：{String.Join("; ", pairPaths)}"
                        : $"PVID {pvId} 的同来源同名歌曲存在重复的难度和星级（{detail}）。路径：{String.Join("; ", pairPaths)}";

                    if (overlap == SameSourceDifficultyOverlap.DifferentKnownStars)
                    {
                        AddWarningRunStatusReason(left, message);
                        AddWarningRunStatusReason(right, message);
                    }
                    else
                    {
                        MarkIdConflict(left, message);
                        MarkIdConflict(right, message);
                    }
                }
            }
        }

        private static SameSourceDifficultyOverlap CompareSameSourceDifficultySlots(
            SongEntry left,
            SongEntry right,
            out string detail)
        {
            var leftSlots = left.Difficulties
                .Where(difficulty => difficulty.ScriptExists)
                .GroupBy(GetConflictDifficultyName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            var rightSlots = right.Difficulties
                .Where(difficulty => difficulty.ScriptExists)
                .GroupBy(GetConflictDifficultyName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            var sharedSlots = leftSlots.Keys
                .Intersect(rightSlots.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sharedSlots.Length == 0)
            {
                detail = String.Empty;
                return SameSourceDifficultyOverlap.None;
            }

            var differentStarDetails = new List<string>();
            var conflictingDetails = new List<string>();
            foreach (var slot in sharedSlots)
            {
                var leftDifficulties = leftSlots[slot];
                var rightDifficulties = rightSlots[slot];
                var slotDisplay = GetConflictDifficultySlotDisplay(
                    slot,
                    leftDifficulties,
                    rightDifficulties);
                if (leftDifficulties.Any(difficulty => !difficulty.NumericLevel.HasValue) ||
                    rightDifficulties.Any(difficulty => !difficulty.NumericLevel.HasValue))
                {
                    conflictingDetails.Add($"{slotDisplay}：星级未知，无法确认二者互不重复");
                    continue;
                }

                var leftLevels = leftDifficulties
                    .Select(difficulty => difficulty.NumericLevel.Value)
                    .Distinct()
                    .OrderBy(level => level)
                    .ToArray();
                var rightLevels = rightDifficulties
                    .Select(difficulty => difficulty.NumericLevel.Value)
                    .Distinct()
                    .OrderBy(level => level)
                    .ToArray();
                var overlappingLevels = leftLevels.Intersect(rightLevels).ToArray();
                if (overlappingLevels.Length > 0)
                {
                    conflictingDetails.Add(
                        $"{slotDisplay}：重复星级 {String.Join("、", overlappingLevels.Select(FormatDifficultyLevel))}");
                }
                else
                {
                    differentStarDetails.Add(
                        $"{slotDisplay}：{String.Join("、", leftLevels.Select(FormatDifficultyLevel))} 星与 {String.Join("、", rightLevels.Select(FormatDifficultyLevel))} 星");
                }
            }

            if (conflictingDetails.Count > 0)
            {
                detail = String.Join("; ", conflictingDetails);
                return SameSourceDifficultyOverlap.ConflictingOrUnknownStars;
            }

            detail = String.Join("; ", differentStarDetails);
            return SameSourceDifficultyOverlap.DifferentKnownStars;
        }

        private static string GetConflictDifficultySlotDisplay(
            string normalizedName,
            IEnumerable<SongDifficulty> left,
            IEnumerable<SongDifficulty> right)
        {
            var leftIndexes = left
                .Select(difficulty => difficulty.Index)
                .Distinct()
                .OrderBy(index => index)
                .Select(index => index.ToString(CultureInfo.InvariantCulture));
            var rightIndexes = right
                .Select(difficulty => difficulty.Index)
                .Distinct()
                .OrderBy(index => index)
                .Select(index => index.ToString(CultureInfo.InvariantCulture));
            return $"{normalizedName}（索引 {String.Join("、", leftIndexes)} 与 {String.Join("、", rightIndexes)}）";
        }

        private static string GetConflictDifficultyName(SongDifficulty difficulty)
        {
            var normalizedName = (difficulty.NormalizedName ?? difficulty.Name ?? String.Empty)
                .Trim()
                .Replace('-', '_')
                .ToLowerInvariant();
            if (normalizedName.StartsWith("extra_", StringComparison.Ordinal))
                normalizedName = "ex_" + normalizedName.Substring("extra_".Length);
            if (difficulty.IsExtra && !normalizedName.StartsWith("ex_", StringComparison.Ordinal))
                normalizedName = "ex_" + normalizedName;
            return String.IsNullOrWhiteSpace(normalizedName) ? "未知难度" : normalizedName;
        }

        private static string FormatDifficultyLevel(decimal level)
        {
            return level.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static void MarkIdConflict(SongEntry entry, string message)
        {
            entry.HasIdConflict = true;
            var warnings = entry.Warnings.ToList();
            AddUnique(warnings, message);
            entry.Warnings = warnings.ToArray();
            var runStatusReasons = entry.RunStatusReasons.ToList();
            AddUnique(runStatusReasons, message);
            entry.RunStatusReasons = runStatusReasons.ToArray();
            entry.RunStatus = SongRunStatus.Broken;
        }

        private enum SameSourceDifficultyOverlap
        {
            None,
            DifferentKnownStars,
            ConflictingOrUnknownStars
        }

        private static SongEntry[] FindCompatiblePatchProviders(
            SongEntry candidate,
            IReadOnlyCollection<SongEntry> providers)
        {
            var otherProviders = providers
                .Where(provider => !ReferenceEquals(provider, candidate))
                .ToArray();
            if (otherProviders.Length == 1 && !HasComparableSongName(candidate))
                return otherProviders;

            return otherProviders
                .Where(provider => IsPatchCompatibleWithProvider(candidate, provider))
                .ToArray();
        }

        private static bool IsPatchCompatibleWithProvider(SongEntry patch, SongEntry provider)
        {
            if (GetComparableSongNames(patch).Any(patchName =>
                GetComparableSongNames(provider).Contains(patchName, StringComparer.OrdinalIgnoreCase)))
            {
                return true;
            }

            var providerAudioReferences = new[] { provider.AudioReference }
                .Concat(provider.AlternateAudioReferences)
                .Select(NormalizeVirtualReference)
                .Where(reference => !String.IsNullOrWhiteSpace(reference))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (new[] { patch.AudioReference }
                .Concat(patch.AlternateAudioReferences)
                .Select(NormalizeVirtualReference)
                .Any(reference =>
                    !String.IsNullOrWhiteSpace(reference) &&
                    providerAudioReferences.Contains(reference)))
            {
                return true;
            }

            var providerChartReferences = provider.Difficulties
                .Select(difficulty => NormalizeVirtualReference(difficulty.ScriptReference))
                .Where(reference => !String.IsNullOrWhiteSpace(reference))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (patch.Difficulties
                .Select(difficulty => NormalizeVirtualReference(difficulty.ScriptReference))
                .Any(reference =>
                    !String.IsNullOrWhiteSpace(reference) &&
                    providerChartReferences.Contains(reference)))
            {
                return true;
            }

            var providerVideoReferences = provider.VideoReferences
                .Select(NormalizeVirtualReference)
                .Where(reference => !String.IsNullOrWhiteSpace(reference))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return patch.VideoReferences
                .Select(NormalizeVirtualReference)
                .Any(reference =>
                    !String.IsNullOrWhiteSpace(reference) &&
                    providerVideoReferences.Contains(reference));
        }

        private static bool HasComparableSongName(SongEntry entry)
        {
            return GetComparableSongNames(entry).Any();
        }

        private static IEnumerable<string> GetComparableSongNames(SongEntry entry)
        {
            return new[] { entry.SongName, entry.SongNameEnglish, entry.SongNameReading }
                .Where(name => !String.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static void ValidateMega39PlusOfficialChartMods(IEnumerable<SongEntry> entries)
        {
            foreach (var entry in entries.Where(entry => entry.IsMega39PlusOfficialPvId))
            {
                var declaredNewClassicsChartPaths = entry.Difficulties
                    .Where(difficulty =>
                        difficulty.IsDeclaredByNewClassicsDatabase &&
                        difficulty.ScriptExists &&
                        !String.IsNullOrWhiteSpace(difficulty.ScriptPath) &&
                        IsUnderRoot(entry.ContentRoot, difficulty.ScriptPath))
                    .Select(difficulty => difficulty.ScriptPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var declaredOfficialLegacyExtensionPaths = entry.Difficulties
                    .Where(difficulty =>
                        difficulty.IsOfficialLegacyExtension &&
                        difficulty.ScriptExists &&
                        !String.IsNullOrWhiteSpace(difficulty.ScriptPath))
                    .Select(difficulty => difficulty.ScriptPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var chartPaths = entry.Difficulties
                    .Where(difficulty =>
                        difficulty.ScriptExists &&
                        !difficulty.IsDeclaredByNewClassicsDatabase &&
                        !difficulty.IsOfficialLegacyExtension &&
                        !difficulty.IsAvailableFromGame)
                    .Select(difficulty => difficulty.ScriptPath)
                    .Concat(entry.LocalChartOverlayPaths.Where(path =>
                        !declaredNewClassicsChartPaths.Contains(path) &&
                        !declaredOfficialLegacyExtensionPaths.Contains(path)))
                    .Where(path => !String.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (chartPaths.Length == 0)
                    continue;

                var hasOnlyEdenOfficialExtensions =
                    entry.IsEdenProjectCore &&
                    entry.Difficulties.Any(difficulty => difficulty.IsOfficialLegacyExtension) &&
                    chartPaths.All(path => entry.Difficulties.Any(difficulty =>
                        difficulty.IsOfficialLegacyExtension &&
                        difficulty.ScriptPath.Equals(path, StringComparison.OrdinalIgnoreCase)));
                if (hasOnlyEdenOfficialExtensions)
                    continue;

                entry.HasInvalidOfficialChartOverride = true;
                var installedCharts = String.Join("; ", chartPaths);
                AddBlockingRunStatusReason(
                    entry,
                    $"PVID {entry.PvId} 属于 MEGA39+ 官曲，但此模组安装了指向该 PVID 的 Legacy 或未注册谱面：{installedCharts}。只有无谱面的歌曲补丁，以及在 nc_db.toml 中声明的 New Classics 扩展可以使用官曲 PVID。");
            }
        }

        private static void AttachPatchSources(
            int pvId,
            IReadOnlyCollection<SongEntry> nonAdditionalEntries)
        {
            if (Mega39PlusStockPvIds.Contains(pvId))
                return;

            var patches = nonAdditionalEntries
                .Where(entry => entry.ModEnabled && entry.IsSongPatch)
                .ToArray();
            if (patches.Length == 0)
                return;

            var providers = nonAdditionalEntries
                .Where(entry =>
                    entry.ModEnabled &&
                    !entry.IsSongPatch &&
                    entry.Difficulties.Any(difficulty => difficulty.ScriptExists))
                .ToArray();
            foreach (var provider in providers)
            {
                var compatiblePatches = patches
                    .Where(patch =>
                        (providers.Length == 1 && !HasComparableSongName(patch)) ||
                        IsPatchCompatibleWithProvider(patch, provider))
                    .ToArray();
                if (compatiblePatches.Length == 0)
                    continue;

                var patchSources = compatiblePatches
                    .Select(CreateSourcePath)
                    .ToArray();
                provider.PatchSources = patchSources;
                var patchNames = patchSources
                    .Select(source => source.ModName)
                    .Where(name => !String.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
                var patchPaths = patchSources
                    .Select(source => source.SourcePath)
                    .Where(path => !String.IsNullOrWhiteSpace(path));
                var warning = $"PVID {pvId} 存在已启用的歌曲补丁：{String.Join("、", patchNames)}。路径：{String.Join("; ", patchPaths)}";
                var warnings = provider.Warnings.ToList();
                AddUnique(warnings, warning);
                provider.Warnings = warnings.ToArray();
                provider.SearchText = String.Join(" ", new[]
                {
                    provider.SearchText,
                    String.Join(" ", patchNames),
                    String.Join(" ", patchPaths)
                });
            }
        }

        private static SongSourcePath CreateSourcePath(SongEntry entry)
        {
            var sourcePath = new[]
                {
                    entry.DatabasePath,
                    entry.NewClassicsDatabasePath,
                    entry.ContentRoot,
                    entry.ModRoot
                }
                .FirstOrDefault(path => !String.IsNullOrWhiteSpace(path)) ?? String.Empty;
            return new SongSourcePath
            {
                ModName = entry.ModName,
                SongName = entry.SongName,
                ModRoot = entry.ModRoot,
                ContentRoot = entry.ContentRoot,
                SourcePath = sourcePath
            };
        }

        private static bool HasCompleteCoreAssets(SongEntry entry)
        {
            return entry.ModEnabled &&
                entry.RunStatus != SongRunStatus.Broken &&
                entry.AudioExists &&
                AllReferencedAssetsExist(entry.VideoReferences, entry.VideoAvailability) &&
                entry.Difficulties.Any(difficulty => difficulty.ScriptExists) &&
                entry.Difficulties.All(difficulty =>
                    difficulty.ScriptExists ||
                    (String.IsNullOrWhiteSpace(difficulty.ScriptReference) &&
                        HasLocalChartForDifficulty(entry, difficulty)));
        }

        private static bool HasMissingDeclaredCoreAssets(SongEntry entry)
        {
            return !entry.AudioExists ||
                entry.Difficulties.Any(difficulty =>
                    !String.IsNullOrWhiteSpace(difficulty.ScriptReference) &&
                    !difficulty.ScriptExists);
        }

        private static bool HasUsableSongMedia(SongEntry entry)
        {
            return entry.ModEnabled &&
                entry.RunStatus != SongRunStatus.Broken &&
                entry.AudioExists &&
                AllReferencedAssetsExist(entry.VideoReferences, entry.VideoAvailability);
        }

        private static bool AllReferencedAssetsExist(
            IReadOnlyCollection<string> references,
            IReadOnlyCollection<bool> availability)
        {
            return references.Count == availability.Count && availability.All(exists => exists);
        }

        private static bool HasLocalChartForDifficulty(SongEntry entry, SongDifficulty difficulty)
        {
            return entry.Difficulties.Any(candidate =>
                !ReferenceEquals(candidate, difficulty) &&
                candidate.ScriptExists &&
                candidate.NormalizedName.Equals(
                    difficulty.NormalizedName,
                    StringComparison.OrdinalIgnoreCase) &&
                (candidate.Index == difficulty.Index || candidate.Source != difficulty.Source));
        }

        private static void ValidateAdditionalDifficultyTargets(
            IEnumerable<SongEntry> group,
            IReadOnlyCollection<SongEntry> nonAdditionalEntries)
        {
            var hasStockTarget = Mega39PlusStockPvIds.Contains(group.First().PvId);
            var hasInstalledTarget = nonAdditionalEntries.Any(entry =>
                entry.ModEnabled && HasUsableSongMedia(entry));
            if (hasStockTarget || hasInstalledTarget)
                return;

            foreach (var entry in group.Where(entry => entry.Format == SongFormat.AdditionalDifficulty))
            {
                AddBlockingRunStatusReason(
                    entry,
                    $"Additional Difficulty PVID {entry.PvId} 对应的目标歌曲尚未安装。");
            }
        }

        private static void ValidateImplicitChartReferences(
            IEnumerable<SongEntry> group,
            IReadOnlyCollection<SongEntry> nonAdditionalEntries)
        {
            foreach (var entry in group)
            {
                var unresolved = entry.Difficulties
                    .Where(difficulty => String.IsNullOrWhiteSpace(difficulty.ScriptReference))
                    .Where(difficulty => !HasLocalChartForDifficulty(entry, difficulty))
                    .Where(difficulty =>
                        !entry.IsSongPatch ||
                        !IsChartSupplied(
                            difficulty,
                            entry.PvId,
                            nonAdditionalEntries.Where(provider =>
                                provider.ModEnabled && !ReferenceEquals(provider, entry)),
                            Mega39PlusStockPvIds.Contains(entry.PvId)))
                    .ToArray();
                if (unresolved.Length == 0)
                    continue;

                var names = unresolved
                    .Select(difficulty => difficulty.DisplayName)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                var reason =
                    $"有 {unresolved.Length} 个难度槽位既未声明谱面路径，也没有可继承的谱面：{String.Join("、", names)}。";
                if (entry.Difficulties.Any(difficulty => difficulty.ScriptExists))
                    AddWarningRunStatusReason(entry, reason);
                else
                    AddBlockingRunStatusReason(entry, reason);
            }
        }

        private static void AddWarningRunStatusReason(SongEntry entry, string reason)
        {
            var reasons = entry.RunStatusReasons.ToList();
            AddUnique(reasons, reason);
            entry.RunStatusReasons = reasons.ToArray();

            var warnings = entry.Warnings.ToList();
            AddUnique(warnings, reason);
            entry.Warnings = warnings.ToArray();
            if (entry.RunStatus == SongRunStatus.Ready)
                entry.RunStatus = SongRunStatus.Warning;
        }

        private static void AddBlockingRunStatusReason(SongEntry entry, string reason)
        {
            var reasons = entry.RunStatusReasons.ToList();
            AddUnique(reasons, reason);
            entry.RunStatusReasons = reasons.ToArray();
            entry.RunStatus = SongRunStatus.Broken;
        }

        private static bool CanInheritMissingCoreAssets(
            SongEntry entry,
            IReadOnlyCollection<SongEntry> providers,
            bool canUseStockAssets)
        {
            if (String.IsNullOrWhiteSpace(entry.AudioReference) ||
                !entry.Difficulties.Any(difficulty =>
                    !String.IsNullOrWhiteSpace(difficulty.ScriptReference)))
            {
                return false;
            }

            if (!entry.AudioExists &&
                !IsAudioSupplied(entry.AudioReference, entry.PvId, providers, canUseStockAssets))
            {
                return false;
            }

            return entry.Difficulties
                .Where(difficulty => !String.IsNullOrWhiteSpace(difficulty.ScriptReference))
                .All(difficulty =>
                difficulty.ScriptExists ||
                IsChartSupplied(difficulty, entry.PvId, providers, canUseStockAssets));
        }

        private static bool CanUseMega39PlusStockAssets(SongEntry entry)
        {
            if (!Mega39PlusStockPvIds.Contains(entry.PvId))
                return false;

            // A signed Eden Core may inherit only the stock base slot after its
            // local, exact Extra Extreme chart has been verified.
            if (entry.IsEdenProjectCore &&
                !entry.Difficulties.Any(difficulty => difficulty.IsOfficialLegacyExtension))
            {
                return false;
            }

            if (!entry.AudioExists &&
                !String.IsNullOrWhiteSpace(entry.AudioReference) &&
                !IsStockPvReference(entry.AudioReference, entry.PvId, "sound/song"))
            {
                return false;
            }

            if (!entry.IsEdenProjectCore)
            {
                return entry.Difficulties.All(difficulty =>
                    difficulty.ScriptExists ||
                    String.IsNullOrWhiteSpace(difficulty.ScriptReference) ||
                    IsStockPvReference(difficulty.ScriptReference, entry.PvId, "script"));
            }

            return entry.Difficulties.All(difficulty =>
                difficulty.ScriptExists ||
                (difficulty.Index == 0 &&
                    IsStockPvReference(difficulty.ScriptReference, entry.PvId, "script")));
        }

        private static void SetPatchRunStatus(
            SongEntry entry,
            IReadOnlyCollection<SongEntry> providers,
            bool canUseStockAssets)
        {
            entry.IsSongPatch = true;
            var blockingIssues = new List<string>();
            var degradedIssues = new List<string>();
            if (!String.IsNullOrWhiteSpace(entry.AudioReference) &&
                !entry.AudioExists &&
                !IsAudioSupplied(entry.AudioReference, entry.PvId, providers, canUseStockAssets))
            {
                AddUnique(blockingIssues, $"歌曲补丁缺少音频文件：{entry.AudioReference}");
            }

            for (var index = 0; index < entry.AlternateAudioReferences.Count; index++)
            {
                var reference = entry.AlternateAudioReferences[index];
                var isAvailable = index < entry.AlternateAudioAvailability.Count &&
                    entry.AlternateAudioAvailability[index];
                if (!isAvailable &&
                    !IsAudioSupplied(reference, entry.PvId, providers, canUseStockAssets))
                {
                    AddUnique(degradedIssues, $"歌曲补丁缺少可选音频文件：{reference}");
                }
            }

            foreach (var difficulty in entry.Difficulties.Where(difficulty => !difficulty.ScriptExists))
            {
                if (!IsChartSupplied(difficulty, entry.PvId, providers, canUseStockAssets))
                {
                    var reference = String.IsNullOrWhiteSpace(difficulty.ScriptReference)
                        ? difficulty.DisplayName
                        : difficulty.ScriptReference;
                    AddUnique(blockingIssues, $"歌曲补丁缺少谱面：{reference}");
                }
            }

            for (var index = 0; index < entry.VideoReferences.Count; index++)
            {
                var reference = entry.VideoReferences[index];
                var isAvailable = index < entry.VideoAvailability.Count &&
                    entry.VideoAvailability[index];
                if (!isAvailable &&
                    !IsVideoSupplied(reference, entry.PvId, providers, canUseStockAssets))
                {
                    AddUnique(blockingIssues, $"歌曲补丁缺少视频文件：{reference}");
                }
            }

            SetRunStatus(entry, blockingIssues, degradedIssues);
        }

        private static bool IsAudioSupplied(
            string reference,
            int pvId,
            IEnumerable<SongEntry> providers,
            bool canUseStockAssets)
        {
            if (String.IsNullOrWhiteSpace(reference))
                return providers.Any() || canUseStockAssets;
            if (canUseStockAssets && IsStockPvReference(reference, pvId, "sound/song"))
                return true;

            var normalized = NormalizeVirtualReference(reference);
            return providers.Any(provider =>
                (provider.AudioExists &&
                    NormalizeVirtualReference(provider.AudioReference)
                        .Equals(normalized, StringComparison.OrdinalIgnoreCase)) ||
                provider.AlternateAudioReferences.Select((candidate, index) => new
                    {
                        Reference = candidate,
                        IsAvailable = index < provider.AlternateAudioAvailability.Count &&
                            provider.AlternateAudioAvailability[index]
                    })
                    .Any(candidate =>
                        candidate.IsAvailable &&
                        NormalizeVirtualReference(candidate.Reference)
                            .Equals(normalized, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool IsVideoSupplied(
            string reference,
            int pvId,
            IEnumerable<SongEntry> providers,
            bool canUseStockAssets)
        {
            if (canUseStockAssets && IsStockPvReference(reference, pvId, "movie"))
                return true;

            var normalized = NormalizeVirtualReference(reference);
            return providers.Any(provider => provider.VideoReferences
                .Select((candidate, index) => new
                {
                    Reference = candidate,
                    IsAvailable = index < provider.VideoAvailability.Count &&
                        provider.VideoAvailability[index]
                })
                .Any(candidate =>
                    candidate.IsAvailable &&
                    NormalizeVirtualReference(candidate.Reference)
                        .Equals(normalized, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool IsChartSupplied(
            SongDifficulty difficulty,
            int pvId,
            IEnumerable<SongEntry> providers,
            bool canUseStockAssets)
        {
            if (canUseStockAssets &&
                (String.IsNullOrWhiteSpace(difficulty.ScriptReference) ||
                    IsStockPvReference(difficulty.ScriptReference, pvId, "script")))
            {
                return true;
            }

            var normalized = NormalizeVirtualReference(difficulty.ScriptReference);
            return providers.Any(provider => provider.Difficulties.Any(candidate =>
                candidate.ScriptExists &&
                ((!String.IsNullOrWhiteSpace(normalized) &&
                    NormalizeVirtualReference(candidate.ScriptReference)
                        .Equals(normalized, StringComparison.OrdinalIgnoreCase)) ||
                 (String.IsNullOrWhiteSpace(normalized) &&
                    candidate.NormalizedName.Equals(
                        difficulty.NormalizedName,
                        StringComparison.OrdinalIgnoreCase) &&
                    candidate.Index == difficulty.Index))));
        }

        private static bool IsStockPvReference(string reference, int pvId, string assetDirectory)
        {
            var normalized = NormalizeVirtualReference(reference);
            var prefix = "rom/" + assetDirectory.Trim('/') + "/";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var fileName = Path.GetFileNameWithoutExtension(normalized);
            var match = Regex.Match(fileName, @"^pv_?0*(\d+)(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success &&
                Int32.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var referencedPvId) &&
                Mega39PlusStockPvIds.Contains(pvId) &&
                Mega39PlusStockPvIds.Contains(referencedPvId);
        }

        private static bool IsStockReferencedMedia(string reference, string assetDirectory)
        {
            var normalized = NormalizeVirtualReference(reference);
            var prefix = "rom/" + assetDirectory.Trim('/') + "/";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var fileName = Path.GetFileNameWithoutExtension(normalized);
            var match = Regex.Match(
                fileName,
                @"^pv_?0*(\d+)(?:_|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success &&
                Int32.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var referencedPvId) &&
                Mega39PlusStockPvIds.Contains(referencedPvId);
        }

        private static string NormalizeVirtualReference(string reference)
        {
            return (reference ?? String.Empty)
                .Trim()
                .Replace('\\', '/')
                .TrimStart('/');
        }

        private static void AddPath(ISet<string> paths, string path)
        {
            if (!String.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        private static void AddRangeUnique(ICollection<string> target, IEnumerable<string> values)
        {
            foreach (var value in values)
                AddUnique(target, value);
        }

        private static void AddUnique(ICollection<string> values, string value)
        {
            if (!String.IsNullOrWhiteSpace(value) && !values.Contains(value))
                values.Add(value);
        }

        private static bool IsFileSystemException(Exception exception)
        {
            return exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is System.Security.SecurityException ||
                exception is NotSupportedException;
        }

        private sealed class ModConfig
        {
            public string ModName { get; set; } = String.Empty;
            public string ModAuthor { get; set; } = String.Empty;
            public string ModVersion { get; set; } = String.Empty;
            public bool IsEnabled { get; set; } = true;
            public bool CanScan { get; set; } = true;
            public bool IsComplete { get; set; } = true;
            public bool IsEdenProjectCore { get; set; }
            public bool RequiresEdenProjectCore { get; set; }
            public bool HasValidEdenCoreSignature { get; set; }
            public List<string> ContentRoots { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();
        }

        private sealed class ModScanContext
        {
            public string ModRoot { get; set; } = String.Empty;
            public ModConfig Config { get; set; }
            public bool IsActiveByLoader { get; set; }
        }

        private sealed class ResolvedVirtualAsset
        {
            public string Path { get; set; } = String.Empty;
            public bool Exists { get; set; }
            public bool FromGame { get; set; }
        }

        private sealed class SongResourceCandidate
        {
            public int? PvId { get; set; }
            public SongResourceKind Kind { get; set; }
            public string Path { get; set; } = String.Empty;
        }

        private sealed class VirtualAssetResolver
        {
            private readonly string[] contentRoots;
            private readonly HashSet<string> gameAssets;

            public VirtualAssetResolver(string modsFolder, IEnumerable<string> contentRoots)
            {
                this.contentRoots = (contentRoots ?? Enumerable.Empty<string>())
                    .Where(Directory.Exists)
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                gameAssets = ReadGameAssetIndex(modsFolder);
            }

            public bool TryResolve(
                string reference,
                bool allowVideoExtensionFallback,
                out string path,
                out bool fromGame)
            {
                path = String.Empty;
                fromGame = false;
                var normalized = NormalizeVirtualReference(reference);
                if (String.IsNullOrWhiteSpace(normalized) ||
                    Path.IsPathRooted(normalized) ||
                    normalized.Split('/').Any(part => part.Equals("..", StringComparison.Ordinal)))
                {
                    return false;
                }

                foreach (var contentRoot in contentRoots)
                {
                    var candidate = Path.GetFullPath(Path.Combine(
                        contentRoot,
                        normalized.Replace('/', Path.DirectorySeparatorChar)));
                    if (!IsUnderRoot(contentRoot, candidate))
                        continue;
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                        return true;
                    }

                    if (!allowVideoExtensionFallback)
                        continue;
                    foreach (var extension in new[] { ".usm", ".mp4" })
                    {
                        var converted = Path.ChangeExtension(candidate, extension);
                        if (IsUnderRoot(contentRoot, converted) && File.Exists(converted))
                        {
                            path = converted;
                            return true;
                        }
                    }
                }

                foreach (var gameReference in GetGameReferenceCandidates(normalized, allowVideoExtensionFallback))
                {
                    if (!gameAssets.Contains(gameReference))
                        continue;
                    path = "game://" + gameReference;
                    fromGame = true;
                    return true;
                }
                return false;
            }

            private static IEnumerable<string> GetGameReferenceCandidates(
                string normalized,
                bool allowVideoExtensionFallback)
            {
                var suffixes = allowVideoExtensionFallback
                    ? new[] { normalized, Path.ChangeExtension(normalized, ".usm").Replace('\\', '/'), Path.ChangeExtension(normalized, ".mp4").Replace('\\', '/') }
                    : new[] { normalized };
                foreach (var suffix in suffixes.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    yield return suffix;
                    yield return "rom_steam/" + suffix;
                    yield return "rom_ps4/" + suffix;
                    yield return "rom_steam_region/" + suffix;
                    yield return "rom_steam_region_dlc/" + suffix;
                }
            }

            private static HashSet<string> ReadGameAssetIndex(string modsFolder)
            {
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var gameRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsFolder)));
                    if (String.IsNullOrWhiteSpace(gameRoot))
                        return result;

                    foreach (var fileName in new[]
                    {
                        "diva_main.cpk",
                        "diva_dlc00.cpk",
                        "diva_main_region.cpk",
                        "diva_dlc00_region.cpk"
                    })
                    {
                        var cpkPath = Path.Combine(gameRoot, fileName);
                        if (!File.Exists(cpkPath))
                            continue;
                        using var archive = new CpkArchive();
                        archive.Load(cpkPath);
                        foreach (var file in archive.FileNames)
                            result.Add(NormalizeVirtualReference(file));
                    }
                }
                catch (Exception exception) when (
                    IsFileSystemException(exception) ||
                    exception is InvalidDataException ||
                    exception is ArgumentException)
                {
                    // A missing or unreadable CPK only disables stock-resource inheritance.
                }
                return result;
            }
        }

        private sealed class LoaderActivation
        {
            public bool LoaderEnabled { get; set; } = true;
            public bool RestrictsMods { get; set; }
            public HashSet<string> PriorityPaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public IEnumerable<string> GetPriorityModRoots()
            {
                return RestrictsMods
                    ? PriorityPaths.Where(Directory.Exists)
                    : Enumerable.Empty<string>();
            }

            public bool IsActive(string modRoot)
            {
                if (!LoaderEnabled)
                    return false;
                if (!RestrictsMods)
                    return true;

                try
                {
                    var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modRoot));
                    return PriorityPaths.Contains(fullRoot);
                }
                catch (Exception exception) when (
                    IsFileSystemException(exception) ||
                    exception is ArgumentException)
                {
                    return false;
                }
            }
        }

        private sealed class FlatDatabase
        {
            public bool IsComplete { get; set; } = true;
            public string Hash { get; set; } = String.Empty;
            public DateTime LastWriteTimeUtc { get; set; }
            public List<FlatSong> Songs { get; } = new List<FlatSong>();
            public List<string> Warnings { get; } = new List<string>();
        }

        private sealed class FlatSong
        {
            private readonly Dictionary<string, FlatSongAlias> aliases =
                new Dictionary<string, FlatSongAlias>(StringComparer.Ordinal);

            public int PvId { get; set; }
            public string RawPvId { get; set; } = String.Empty;
            public IReadOnlyList<string> RawPvIds { get; private set; } = Array.Empty<string>();
            public Dictionary<string, string> Fields { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public void AddField(string rawPvId, string key, string value)
            {
                if (!aliases.TryGetValue(rawPvId, out var alias))
                {
                    alias = new FlatSongAlias(rawPvId, aliases.Count);
                    aliases.Add(rawPvId, alias);
                }
                alias.Fields[key] = value;
            }

            public void FinalizeAliases()
            {
                var orderedAliases = aliases.Values
                    .OrderByDescending(alias => alias.HasAnyField("song_name", "name"))
                    .ThenByDescending(alias => alias.HasAnyField("song_file_name"))
                    .ThenByDescending(alias => alias.Fields.Count(field =>
                        !field.Key.StartsWith("difficulty.", StringComparison.OrdinalIgnoreCase)))
                    .ThenByDescending(alias => alias.Fields.Count)
                    .ThenBy(alias => alias.Order)
                    .ToArray();

                RawPvIds = orderedAliases.Select(alias => alias.RawPvId).ToArray();
                RawPvId = RawPvIds.FirstOrDefault() ?? PvId.ToString(CultureInfo.InvariantCulture);
                foreach (var alias in orderedAliases)
                {
                    foreach (var field in alias.Fields)
                    {
                        if (!Fields.ContainsKey(field.Key))
                            Fields.Add(field.Key, field.Value);
                    }
                }
            }
        }

        private sealed class FlatSongAlias
        {
            public FlatSongAlias(string rawPvId, int order)
            {
                RawPvId = rawPvId;
                Order = order;
            }

            public string RawPvId { get; }
            public int Order { get; }
            public Dictionary<string, string> Fields { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public bool HasAnyField(params string[] keys)
            {
                return keys.Any(Fields.ContainsKey);
            }
        }

        private sealed class NewClassicsDatabase
        {
            public bool IsComplete { get; set; } = true;
            public string Path { get; set; } = String.Empty;
            public string Hash { get; set; } = String.Empty;
            public DateTime LastWriteTimeUtc { get; set; }
            public Dictionary<int, NewClassicsSong> Songs { get; } =
                new Dictionary<int, NewClassicsSong>();
            public List<string> Warnings { get; } = new List<string>();
        }

        private sealed class NewClassicsSong
        {
            public int PvId { get; set; }
            public List<SongDifficulty> Difficulties { get; } = new List<SongDifficulty>();
        }

        private sealed class FileSnapshot
        {
            public string Text { get; set; } = String.Empty;
            public string Hash { get; set; } = String.Empty;
            public DateTime LastWriteTimeUtc { get; set; }
        }
    }
}
