using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Tomlyn;
using Tomlyn.Model;

namespace DivaModManager
{
    public static class ModClassifier
    {
        private const string CustomSong = "Custom Song";
        private const string AdditionalDifficulties = "Additional Difficulties";
        private const string Module = "Module";
        private const string Accessory = "Accessory";
        private const string SoundReplacement = "Sound Replacement";
        private const string Cover = "Cover";
        private const string Plugin = "Plugin";
        private const string UserInterface = "User Interface";
        private const string Translation = "Translation";
        private const string Other = "Other/Misc";

        private static readonly string[] CategoryOrder =
        {
            CustomSong,
            AdditionalDifficulties,
            Module,
            SoundReplacement,
            Cover,
            Plugin,
            Accessory,
            UserInterface,
            Translation,
            Other
        };

        private static readonly string[] NewClassicsDifficultyNames =
        {
            "easy", "normal", "hard", "extreme", "encore",
            "ex_easy", "ex_normal", "ex_hard", "ex_extreme", "ex_encore"
        };

        public static ModClassification Classify(string modRoot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
                return ModClassification.Unknown("Mod folder is unavailable");

            var evidence = new List<string>();
            var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rootConfig = ReadRootConfig(modRoot, evidence);
            var includeRoots = GetIncludeRoots(modRoot, rootConfig, out var restrictToIncludeRoots);
            var files = EnumerateFilesOnce(modRoot, evidence, cancellationToken);
            if (files == null)
                return ModClassification.Unknown(evidence.ToArray());

            var contentFiles = files
                .Where(file => IsIncluded(file.RelativePath, includeRoots, restrictToIncludeRoots))
                .ToList();
            var romFiles = contentFiles.Where(file => file.RomRelativePath != null).ToList();
            var legacyDatabases = romFiles.Where(file => EqualsPath(file.RomRelativePath, "mod_pv_db.txt")).ToList();
            var newClassicsDatabases = romFiles.Where(file => EqualsPath(file.RomRelativePath, "nc_db.toml")).ToList();
            var chartFiles = romFiles.Where(IsChart).ToList();
            var songAudioFiles = romFiles.Where(file =>
                HasPathPrefix(file.RomRelativePath, "sound/song/") &&
                EqualsExtension(file.FullPath, ".ogg")).ToList();
            var movieFiles = romFiles.Where(file => HasPathPrefix(file.RomRelativePath, "movie/")).ToList();
            var bgmFiles = romFiles.Where(file =>
                HasPathPrefix(file.RomRelativePath, "sound/bgm/") &&
                EqualsExtension(file.FullPath, ".ogg")).ToList();

            var hasLegacyDatabase = legacyDatabases.Count > 0;
            var hasNewClassicsDatabase = newClassicsDatabases.Count > 0;
            var hasCharts = chartFiles.Count > 0;
            var hasSongAudio = songAudioFiles.Count > 0;
            var hasMovie = movieFiles.Count > 0;
            var hasSongMedia = hasSongAudio || hasMovie;

            AddFlagEvidence(evidence, hasLegacyDatabase, "rom/mod_pv_db.txt");
            AddFlagEvidence(evidence, hasNewClassicsDatabase, "rom/nc_db.toml");
            AddFlagEvidence(evidence, hasCharts, "DSC chart scripts");
            AddFlagEvidence(evidence, hasSongAudio, "song audio");
            AddFlagEvidence(evidence, hasMovie, "PV movie assets");

            var ncInfo = InspectNewClassicsDatabases(newClassicsDatabases, evidence, cancellationToken);
            var chartFormats = InspectChartFormats(chartFiles, evidence, cancellationToken);
            var hasNewClassicsChart = chartFormats.Contains(ChartFormat.NewClassics);
            var hasNewClassicsContent = hasNewClassicsDatabase || hasNewClassicsChart;

            var validPluginDlls = GetConfiguredPluginDlls(modRoot, rootConfig, files, evidence);
            if (validPluginDlls.Count > 0)
                AddScore(scores, Plugin, 100);

            var metaTags = ReadMetaTags(modRoot, evidence);
            var hasModuleTable = romFiles.Any(file => EqualsPath(file.RomRelativePath, "mod_gm_module_tbl.farc"));
            var hasCustomizeTable = romFiles.Any(file => EqualsPath(file.RomRelativePath, "mod_gm_customize_item_tbl.farc"));
            var hasCharacterItemTable = romFiles.Any(file => EqualsPath(file.RomRelativePath, "mod_chritm_prop.farc"));
            if (hasModuleTable)
            {
                AddScore(scores, Module, 110);
                AddUnique(evidence, "module database tables");
            }
            else if (hasCustomizeTable || (hasCharacterItemTable && metaTags.Contains("module")))
            {
                AddScore(scores, Accessory, 80);
                AddUnique(evidence, "customization item database");
            }
            else if (metaTags.Contains("module"))
            {
                AddScore(scores, Module, 65);
                AddUnique(evidence, "meta.json module tag");
            }

            var hasStrongUiAssets = romFiles.Any(IsStrongUiAsset);
            if (hasStrongUiAssets)
            {
                AddScore(scores, UserInterface, 85);
                AddUnique(evidence, "menu UI assets");
            }

            if (bgmFiles.Count > 0 && !hasLegacyDatabase && !hasNewClassicsContent)
            {
                AddScore(scores, SoundReplacement, 100);
                AddUnique(evidence, "BGM replacement audio");
            }

            if (hasSongAudio && !hasLegacyDatabase && !hasNewClassicsContent && !hasCharts && !hasMovie)
            {
                AddScore(scores, Cover, 85);
                AddUnique(evidence, "song audio without a PV database or chart");
            }

            var strongCustomSong = hasSongMedia && (hasLegacyDatabase || hasNewClassicsContent || hasCharts);
            var mediaOnlyCustomSong = hasSongAudio && hasMovie;
            if (strongCustomSong)
                AddScore(scores, CustomSong, 115);
            else if (mediaOnlyCustomSong)
                AddScore(scores, CustomSong, 80);
            else if (hasLegacyDatabase && !hasCharts && !hasSongMedia && !hasModuleTable)
                AddScore(scores, CustomSong, 70);

            var hasPatchAssetFamily = romFiles.Any(IsPatchAssetFamily);
            if (!hasSongMedia &&
                (hasNewClassicsContent ||
                    (hasCharts && !hasModuleTable && (hasLegacyDatabase || !hasPatchAssetFamily))) &&
                (hasCharts || hasNewClassicsDatabase))
            {
                var hasValidNewClassicsSignal = !hasNewClassicsDatabase ||
                    ncInfo.ValidSongCount > 0 ||
                    hasNewClassicsChart;
                AddScore(scores, AdditionalDifficulties,
                    hasCharts && hasValidNewClassicsSignal ? 110 : 80);
            }

            var hasTranslationFiles = romFiles.Any(IsTranslationFile);
            var hasStrongContentCategory = scores.Any(pair => pair.Value >= 80 &&
                !pair.Key.Equals(Plugin, StringComparison.OrdinalIgnoreCase));
            if (hasTranslationFiles && !hasStrongContentCategory)
            {
                AddScore(scores, Translation, 75);
                AddUnique(evidence, "localized text or subtitle assets");
            }

            var detected = scores
                .Where(pair => pair.Value >= 60)
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => Array.IndexOf(CategoryOrder, pair.Key))
                .Select(pair => pair.Key)
                .ToList();

            if (detected.Count == 0 && romFiles.Any(file => !IsPlaceholderFile(file)))
            {
                detected.Add(Other);
                scores[Other] = 45;
                AddUnique(evidence, "game asset overrides without a reliable category signature");
            }

            if (detected.Count == 0)
                return ModClassification.Unknown(evidence.Count == 0
                    ? new[] { "No reliable structural signature found" }
                    : evidence.ToArray());

            var primary = detected[0];
            var primaryScore = scores[primary];
            var confidence = primaryScore >= 90
                ? ModClassificationConfidence.High
                : primaryScore >= 60
                    ? ModClassificationConfidence.Medium
                    : ModClassificationConfidence.Low;
            var formatVariant = BuildFormatVariant(
                hasLegacyDatabase,
                hasNewClassicsDatabase,
                hasNewClassicsChart,
                ncInfo.Styles,
                chartFormats);

            return new ModClassification(primary, detected, formatVariant, confidence, evidence);
        }

        private static List<FileEntry> EnumerateFilesOnce(
            string modRoot,
            List<string> evidence,
            CancellationToken cancellationToken)
        {
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                var files = new List<FileEntry>();
                foreach (var fullPath in Directory.EnumerateFiles(modRoot, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(new FileEntry(modRoot, fullPath));
                }
                return files;
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                AddUnique(evidence, $"File scan failed: {exception.Message}");
                return null;
            }
        }

        private static TomlTable ReadRootConfig(string modRoot, List<string> evidence)
        {
            var path = Path.Combine(modRoot, "config.toml");
            if (!File.Exists(path))
                return null;

            try
            {
                var text = File.ReadAllText(path);
                if (Toml.TryToModel(text, out TomlTable config, out var diagnostics))
                    return config;

                if (diagnostics.Count > 0)
                    AddUnique(evidence, $"config.toml parse warning: {diagnostics[0].Message}");
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                AddUnique(evidence, $"config.toml read warning: {exception.Message}");
            }

            return null;
        }

        private static IReadOnlyList<string> GetIncludeRoots(
            string modRoot,
            TomlTable config,
            out bool restrictToIncludeRoots)
        {
            if (config == null || !config.TryGetValue("include", out var includeValue))
            {
                restrictToIncludeRoots = false;
                return Array.Empty<string>();
            }

            restrictToIncludeRoots = true;
            if (includeValue is string || includeValue is not IEnumerable)
                return Array.Empty<string>();

            var roots = ExtractStrings(includeValue)
                .Select(value => NormalizeIncludeRoot(modRoot, value))
                .Where(path => path != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return roots;
        }

        private static bool IsIncluded(
            string relativePath,
            IReadOnlyList<string> includeRoots,
            bool restrictToIncludeRoots)
        {
            if (!restrictToIncludeRoots || includeRoots.Any(String.IsNullOrEmpty))
                return true;
            if (includeRoots.Count == 0)
                return false;

            return includeRoots.Any(root =>
                relativePath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> GetConfiguredPluginDlls(
            string modRoot,
            TomlTable config,
            IReadOnlyCollection<FileEntry> files,
            List<string> evidence)
        {
            var result = new List<string>();
            if (config == null || !config.TryGetValue("dll", out var dllValue) || dllValue is string)
                return result;

            var knownPaths = new HashSet<string>(files.Select(file => file.FullPath), StringComparer.OrdinalIgnoreCase);
            foreach (var configuredPath in ExtractStrings(dllValue))
            {
                if (String.IsNullOrWhiteSpace(configuredPath) ||
                    !configuredPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var relativePath = configuredPath
                        .Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(modRoot, relativePath));
                    if (!IsUnderRoot(modRoot, fullPath) || !knownPaths.Contains(fullPath) || !File.Exists(fullPath))
                        continue;

                    result.Add(configuredPath);
                }
                catch (Exception exception) when (IsFileSystemException(exception) || exception is ArgumentException)
                {
                    AddUnique(evidence, $"Invalid configured DLL path: {configuredPath}");
                }
            }

            if (result.Count > 0)
                AddUnique(evidence, $"configured plugin DLL{(result.Count == 1 ? String.Empty : "s")}: {String.Join(", ", result)}");
            return result;
        }

        private static HashSet<string> ReadMetaTags(string modRoot, List<string> evidence)
        {
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(modRoot, "meta.json");
            if (!File.Exists(path))
                return tags;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("tags", out var tagsElement) ||
                    tagsElement.ValueKind != JsonValueKind.Array)
                    return tags;

                foreach (var tag in tagsElement.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String && !String.IsNullOrWhiteSpace(tag.GetString()))
                        tags.Add(tag.GetString());
                }
            }
            catch (Exception exception) when (IsFileSystemException(exception) ||
                exception is JsonException ||
                exception is InvalidOperationException)
            {
                AddUnique(evidence, $"meta.json parse warning: {exception.Message}");
            }

            return tags;
        }

        private static NewClassicsInfo InspectNewClassicsDatabases(
            IReadOnlyCollection<FileEntry> databaseFiles,
            List<string> evidence,
            CancellationToken cancellationToken)
        {
            var info = new NewClassicsInfo();
            foreach (var file in databaseFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var text = File.ReadAllText(file.FullPath);
                    if (!Toml.TryToModel(text, out TomlTable model, out var diagnostics))
                    {
                        if (diagnostics.Count > 0)
                            AddUnique(evidence, $"nc_db.toml parse warning: {diagnostics[0].Message}");
                        continue;
                    }

                    InspectValidNewClassicsSongs(model, info);
                }
                catch (Exception exception) when (IsFileSystemException(exception))
                {
                    AddUnique(evidence, $"nc_db.toml read warning: {exception.Message}");
                }
            }

            if (databaseFiles.Count > 0 && info.ValidSongCount == 0)
                AddUnique(evidence, "nc_db.toml has no valid positive song IDs");
            else if (info.ValidSongCount > 0)
                AddUnique(evidence, $"{info.ValidSongCount} New Classics song definition{(info.ValidSongCount == 1 ? String.Empty : "s")}");
            return info;
        }

        private static void InspectValidNewClassicsSongs(TomlTable model, NewClassicsInfo info)
        {
            if (!model.TryGetValue("songs", out var songsValue) || songsValue is not IEnumerable songs)
                return;

            foreach (var songValue in songs)
            {
                if (songValue is not TomlTable song ||
                    !song.TryGetValue("id", out var idValue) ||
                    idValue is not long id ||
                    id <= 0 ||
                    id > Int32.MaxValue)
                    continue;

                info.ValidSongCount++;
                CollectNewClassicsStyles(song, info.Styles);
            }
        }

        private static void CollectNewClassicsStyles(TomlTable song, ISet<string> styles)
        {
            foreach (var difficultyName in NewClassicsDifficultyNames)
            {
                if (!song.TryGetValue(difficultyName, out var chartsValue) || chartsValue is not IEnumerable charts)
                    continue;

                foreach (var chartValue in charts)
                {
                    if (chartValue is TomlTable chart &&
                        chart.TryGetValue("style", out var styleValue) &&
                        styleValue is string style &&
                        (style == "ARCADE" || style == "CONSOLE" || style == "MIXED"))
                        styles.Add(style);
                }
            }
        }

        private static HashSet<ChartFormat> InspectChartFormats(
            IReadOnlyCollection<FileEntry> chartFiles,
            List<string> evidence,
            CancellationToken cancellationToken)
        {
            var formats = new HashSet<ChartFormat>();
            foreach (var file in chartFiles
                .OrderBy(file => GetChartSamplePriority(file.RomRelativePath))
                .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(64))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var stream = File.Open(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var bytes = new byte[4];
                    if (stream.Read(bytes, 0, bytes.Length) != bytes.Length)
                        continue;

                    var signature = BitConverter.ToUInt32(bytes, 0);
                    formats.Add(signature switch
                    {
                        0x12020220 => ChartFormat.F,
                        0x43535650 => ChartFormat.F2X,
                        0x25061313 => ChartFormat.NewClassics,
                        0x14050921 => ChartFormat.FutureTone,
                        _ => ChartFormat.Other
                    });
                    if (formats.Count >= 5)
                        break;
                }
                catch (Exception exception) when (IsFileSystemException(exception))
                {
                    AddUnique(evidence, $"DSC header read warning: {exception.Message}");
                }
            }

            var names = formats
                .Where(format => format != ChartFormat.Other)
                .Select(GetChartFormatName)
                .ToList();
            if (names.Count > 0)
                AddUnique(evidence, $"chart format: {String.Join(", ", names)}");
            return formats;
        }

        private static string BuildFormatVariant(
            bool hasLegacyDatabase,
            bool hasNewClassicsDatabase,
            bool hasNewClassicsChart,
            ISet<string> newClassicsStyles,
            ISet<ChartFormat> chartFormats)
        {
            var parts = new List<string>();
            if (hasLegacyDatabase)
                parts.Add("Legacy");

            if (hasNewClassicsDatabase || hasNewClassicsChart)
            {
                var styles = new List<string>();
                if (newClassicsStyles.Contains("CONSOLE"))
                    styles.Add("Console");
                if (newClassicsStyles.Contains("MIXED"))
                    styles.Add("Mixed");
                if (styles.Count == 0 && newClassicsStyles.Contains("ARCADE"))
                    styles.Add("Arcade");

                parts.Add(styles.Count == 0
                    ? "New Classics"
                    : $"New Classics ({String.Join(", ", styles)})");
            }

            if (parts.Count == 0)
            {
                foreach (var format in new[] { ChartFormat.F, ChartFormat.F2X, ChartFormat.FutureTone })
                {
                    if (chartFormats.Contains(format))
                        parts.Add(GetChartFormatName(format) + " chart");
                }
            }

            return String.Join(" + ", parts);
        }

        private static string GetChartFormatName(ChartFormat format)
        {
            return format switch
            {
                ChartFormat.F => "F",
                ChartFormat.F2X => "F 2nd/X",
                ChartFormat.NewClassics => "New Classics",
                ChartFormat.FutureTone => "Future Tone",
                _ => "Other"
            };
        }

        private static bool IsChart(FileEntry file)
        {
            if (!EqualsExtension(file.FullPath, ".dsc") || file.RomRelativePath == null)
                return false;

            return HasPathPrefix(file.RomRelativePath, "script/") ||
                HasPathPrefix(file.RomRelativePath, "script_nc/") ||
                HasPathPrefix(file.RomRelativePath, "script_pv/");
        }

        private static bool IsStrongUiAsset(FileEntry file)
        {
            if (!HasPathPrefix(file.RomRelativePath, "2d/"))
                return false;

            var name = Path.GetFileName(file.RomRelativePath);
            return name.StartsWith("spr_nswgam_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("aet_nswgam_", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("spr_gam_cmn.farc", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("aet_gam_cmn.bin", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPatchAssetFamily(FileEntry file)
        {
            var path = file.RomRelativePath;
            return HasPathPrefix(path, "light_param/") ||
                HasPathPrefix(path, "ibl/") ||
                HasPathPrefix(path, "objset/") ||
                HasPathPrefix(path, "auth_3d/") ||
                HasPathPrefix(path, "rob/") ||
                HasPathPrefix(path, "osage_play_data/") ||
                HasPathPrefix(path, "skin_param/") ||
                HasPathPrefix(path, "stage/") ||
                HasPathPrefix(path, "add_param/") ||
                EqualsPath(path, "pv_field.txt") ||
                EqualsPath(path, "mod_pv_field.txt") ||
                EqualsPath(path, "mod_stage_data.bin");
        }

        private static bool IsPlaceholderFile(FileEntry file)
        {
            var name = Path.GetFileName(file.RomRelativePath);
            return name.Equals(".gitkeep", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".keep", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetChartSamplePriority(string path)
        {
            if (HasPathPrefix(path, "script_nc/"))
                return 0;
            if (HasPathPrefix(path, "script/"))
                return 1;
            return 2;
        }

        private static bool IsTranslationFile(FileEntry file)
        {
            return HasPathPrefix(file.RomRelativePath, "lang2/") ||
                HasPathPrefix(file.RomRelativePath, "subtitles/");
        }

        private static IEnumerable<string> ExtractStrings(object value)
        {
            if (value is string text)
            {
                yield return text;
                yield break;
            }

            if (value is not IEnumerable sequence)
                yield break;

            foreach (var item in sequence)
            {
                if (item is string itemText)
                    yield return itemText;
            }
        }

        private static string NormalizeIncludeRoot(string modRoot, string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                var normalizedValue = value.Trim()
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                var fullRoot = Path.GetFullPath(modRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalizedValue))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
                    return String.Empty;
                if (!IsUnderRoot(fullRoot, fullPath))
                    return null;

                return Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/').Trim('/');
            }
            catch (Exception exception) when (IsFileSystemException(exception) || exception is ArgumentException)
            {
                return null;
            }
        }

        private static bool IsUnderRoot(string root, string path)
        {
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasPathPrefix(string path, string prefix)
        {
            return path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EqualsPath(string left, string right)
        {
            return left != null && left.Equals(right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EqualsExtension(string path, string extension)
        {
            return Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddScore(IDictionary<string, int> scores, string category, int score)
        {
            if (!scores.TryGetValue(category, out var existing) || score > existing)
                scores[category] = score;
        }

        private static void AddFlagEvidence(ICollection<string> evidence, bool condition, string text)
        {
            if (condition)
                AddUnique(evidence, text);
        }

        private static void AddUnique(ICollection<string> values, string value)
        {
            if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
                values.Add(value);
        }

        private static bool IsFileSystemException(Exception exception)
        {
            return exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is PathTooLongException ||
                exception is NotSupportedException;
        }

        private sealed class FileEntry
        {
            public FileEntry(string root, string fullPath)
            {
                FullPath = Path.GetFullPath(fullPath);
                RelativePath = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
                RomRelativePath = GetRomRelativePath(RelativePath);
            }

            public string FullPath { get; }
            public string RelativePath { get; }
            public string RomRelativePath { get; }

            private static string GetRomRelativePath(string relativePath)
            {
                var segments = relativePath.Split('/');
                for (var index = segments.Length - 2; index >= 0; index--)
                {
                    if (segments[index].Equals("rom", StringComparison.OrdinalIgnoreCase))
                        return String.Join("/", segments.Skip(index + 1));
                }
                return null;
            }
        }

        private sealed class NewClassicsInfo
        {
            public int ValidSongCount { get; set; }
            public HashSet<string> Styles { get; } = new HashSet<string>(StringComparer.Ordinal);
        }

        private enum ChartFormat
        {
            Other,
            F,
            F2X,
            NewClassics,
            FutureTone
        }
    }
}
