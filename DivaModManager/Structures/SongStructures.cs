using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DivaModManager
{
    public enum SongFormat
    {
        Legacy,
        NewClassics,
        LegacyWithNewClassics,
        AdditionalDifficulty,
        OrphanResources
    }

    public enum SongResourceKind
    {
        Chart,
        Audio,
        Video,
        Artwork,
        AdditionalParameter
    }

    public enum SongDifficultySource
    {
        LegacyDatabase,
        NewClassicsDatabase,
        DetectedChart
    }

    public enum SongRunStatus
    {
        Ready,
        Warning,
        Broken
    }

    public sealed class SongDifficulty
    {
        public string Name { get; internal set; } = String.Empty;
        public string NormalizedName { get; internal set; } = String.Empty;
        public bool IsExtra { get; internal set; }
        public int Index { get; internal set; }
        public string Level { get; internal set; } = String.Empty;
        public decimal? NumericLevel { get; internal set; }
        public string LevelDisplay => NumericLevel.HasValue
            ? NumericLevel.Value.ToString("0.#", CultureInfo.InvariantCulture)
            : "?";
        public string Style { get; internal set; } = String.Empty;
        public string Charter { get; internal set; } = String.Empty;
        public string ScriptReference { get; internal set; } = String.Empty;
        public string ScriptPath { get; internal set; } = String.Empty;
        public bool ScriptExists { get; internal set; }
        public bool IsAvailableFromGame { get; internal set; }
        public bool UsesInheritedScript { get; internal set; }
        public SongDifficultySource Source { get; internal set; }
        public bool IsDeclaredByNewClassicsDatabase { get; internal set; }
        public bool HasDeclaredLegacyLength { get; internal set; }
        public int DeclaredLegacyLength { get; internal set; }
        public bool IsOfficialLegacyExtension { get; internal set; }

        public string DisplayName
        {
            get
            {
                var parts = new List<string>();
                parts.Add(LevelDisplay);
                if (!String.IsNullOrWhiteSpace(Style))
                    parts.Add(Style);
                if (!String.IsNullOrWhiteSpace(Charter))
                    parts.Add(Charter);

                var suffix = parts.Count == 0 ? String.Empty : $" ({String.Join(", ", parts)})";
                return $"{ToDisplayName(NormalizedName)}{suffix}";
            }
        }

        private static string ToDisplayName(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "未知";

            return String.Join(" ", value
                .Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Length == 1
                    ? part.ToUpperInvariant()
                    : Char.ToUpperInvariant(part[0]) + part.Substring(1).ToLowerInvariant()));
        }
    }

    public sealed class SongSourcePath
    {
        public string ModName { get; internal set; } = String.Empty;
        public string SongName { get; internal set; } = String.Empty;
        public string ModRoot { get; internal set; } = String.Empty;
        public string ContentRoot { get; internal set; } = String.Empty;
        public string SourcePath { get; internal set; } = String.Empty;

        public string DisplayName
        {
            get
            {
                if (String.IsNullOrWhiteSpace(SongName))
                    return ModName;
                if (String.IsNullOrWhiteSpace(ModName))
                    return SongName;
                return $"{ModName} · {SongName}";
            }
        }
    }

    public sealed class SongOrphanResource
    {
        public int PvId { get; internal set; }
        public SongResourceKind Kind { get; internal set; }
        public string Path { get; internal set; } = String.Empty;
        public string RelativePath { get; internal set; } = String.Empty;
        public string DisplayName { get; internal set; } = String.Empty;
    }

    public sealed class SongEntry
    {
        private IReadOnlyList<SongDifficulty> difficulties = Array.Empty<SongDifficulty>();
        private IReadOnlyList<string> rawPvIds = Array.Empty<string>();
        private IReadOnlyList<string> referencedAssetPaths = Array.Empty<string>();
        private IReadOnlyList<string> warnings = Array.Empty<string>();
        private IReadOnlyList<string> alternateAudioReferences = Array.Empty<string>();
        private IReadOnlyList<string> alternateAudioPaths = Array.Empty<string>();
        private IReadOnlyList<bool> alternateAudioAvailability = Array.Empty<bool>();
        private IReadOnlyList<string> videoReferences = Array.Empty<string>();
        private IReadOnlyList<string> videoPaths = Array.Empty<string>();
        private IReadOnlyList<bool> videoAvailability = Array.Empty<bool>();
        private IReadOnlyList<string> artworkPaths = Array.Empty<string>();
        private IReadOnlyList<string> additionalParameterPaths = Array.Empty<string>();
        private IReadOnlyList<string> explicitAssetPaths = Array.Empty<string>();
        private IReadOnlyList<string> localChartOverlayPaths = Array.Empty<string>();
        private IReadOnlyList<string> runStatusReasons = Array.Empty<string>();
        private IReadOnlyList<SongSourcePath> idConflictSources = Array.Empty<SongSourcePath>();
        private IReadOnlyList<SongSourcePath> patchSources = Array.Empty<SongSourcePath>();
        private IReadOnlyList<SongOrphanResource> orphanResources = Array.Empty<SongOrphanResource>();
        private SongRunStatus automaticRunStatus = SongRunStatus.Ready;
        private SongRunStatus? manualRunStatusOverride;

        public string ModName { get; internal set; } = String.Empty;
        public string ModAuthor { get; internal set; } = String.Empty;
        public string ModVersion { get; internal set; } = String.Empty;
        public string ModRoot { get; internal set; } = String.Empty;
        public string ModPath => ModRoot;
        public bool ModEnabled { get; internal set; } = true;
        public string ContentRoot { get; internal set; } = String.Empty;
        public string DatabasePath { get; internal set; } = String.Empty;
        public string NewClassicsDatabasePath { get; internal set; } = String.Empty;
        public string DatabaseHash { get; internal set; } = String.Empty;
        public DateTime DatabaseLastWriteTimeUtc { get; internal set; }
        public string NewClassicsDatabaseHash { get; internal set; } = String.Empty;
        public DateTime NewClassicsDatabaseLastWriteTimeUtc { get; internal set; }

        public int PvId { get; internal set; }
        public string RawPvId { get; internal set; } = String.Empty;
        public string RawId => RawPvId;
        public IReadOnlyList<string> RawPvIds
        {
            get => rawPvIds;
            internal set => rawPvIds = value ?? Array.Empty<string>();
        }
        public string SongName { get; internal set; } = String.Empty;
        public string SongNameEnglish { get; internal set; } = String.Empty;
        public string SongNameReading { get; internal set; } = String.Empty;
        public string Name => SongName;
        public string AuthorSummary { get; internal set; } = String.Empty;
        public string Authors => AuthorSummary;

        public SongFormat Format { get; internal set; }
        public string FormatDisplayName
        {
            get
            {
                if (Difficulties.Any(difficulty => difficulty.IsOfficialLegacyExtension))
                    return "Eden Extra Extreme Extension";
                if (Format == SongFormat.AdditionalDifficulty &&
                    IsMega39PlusOfficialPvId &&
                    !String.IsNullOrWhiteSpace(NewClassicsDatabasePath))
                {
                    return "New Classics Extension";
                }
                if (IsSongPatch && IsMega39PlusOfficialPvId &&
                    Format != SongFormat.AdditionalDifficulty)
                    return "Official Song Patch";
                return Format switch
                {
                    SongFormat.Legacy => "Legacy",
                    SongFormat.NewClassics => "New Classics",
                    SongFormat.LegacyWithNewClassics => "Legacy + New Classics",
                    SongFormat.AdditionalDifficulty => "Additional Difficulty",
                    SongFormat.OrphanResources => "废案资源",
                    _ => Format.ToString()
                };
            }
        }

        public IReadOnlyList<SongDifficulty> Difficulties
        {
            get => difficulties;
            internal set => difficulties = value ?? Array.Empty<SongDifficulty>();
        }

        public string DifficultiesDisplay => Difficulties.Count == 0
            ? "无"
            : String.Join(", ", Difficulties.Select(difficulty => difficulty.DisplayName));

        public string AudioReference { get; internal set; } = String.Empty;
        public string AudioPath { get; internal set; } = String.Empty;
        public bool AudioExists { get; internal set; }
        public IReadOnlyList<string> AlternateAudioReferences
        {
            get => alternateAudioReferences;
            internal set => alternateAudioReferences = value ?? Array.Empty<string>();
        }
        public IReadOnlyList<string> AlternateAudioPaths
        {
            get => alternateAudioPaths;
            internal set => alternateAudioPaths = value ?? Array.Empty<string>();
        }
        public IReadOnlyList<bool> AlternateAudioAvailability
        {
            get => alternateAudioAvailability;
            internal set => alternateAudioAvailability = value ?? Array.Empty<bool>();
        }
        public string VideoReference { get; internal set; } = String.Empty;
        public string VideoPath { get; internal set; } = String.Empty;
        public bool VideoExists { get; internal set; }
        public IReadOnlyList<string> VideoReferences
        {
            get => videoReferences;
            internal set => videoReferences = value ?? Array.Empty<string>();
        }
        public IReadOnlyList<string> VideoPaths
        {
            get => videoPaths;
            internal set => videoPaths = value ?? Array.Empty<string>();
        }
        public IReadOnlyList<bool> VideoAvailability
        {
            get => videoAvailability;
            internal set => videoAvailability = value ?? Array.Empty<bool>();
        }
        public bool Uses3dPv =>
            !IsSongPatch &&
            Format != SongFormat.AdditionalDifficulty &&
            String.IsNullOrWhiteSpace(VideoReference) &&
            !VideoReferences.Any(reference => !String.IsNullOrWhiteSpace(reference));
        public string ArtworkPath { get; internal set; } = String.Empty;
        public string CoverPath => ArtworkPath;
        public bool CoverExists { get; internal set; }
        public bool ThumbnailExists { get; internal set; }
        public bool JacketExists { get; internal set; }
        public bool BackgroundExists { get; internal set; }
        public bool ArtworkComplete => ThumbnailExists && JacketExists && BackgroundExists;
        public IReadOnlyList<string> ArtworkPaths
        {
            get => artworkPaths;
            internal set => artworkPaths = value ?? Array.Empty<string>();
        }
        public string AdditionalParameterPath { get; internal set; } = String.Empty;
        public IReadOnlyList<string> AdditionalParameterPaths
        {
            get => additionalParameterPaths;
            internal set => additionalParameterPaths = value ?? Array.Empty<string>();
        }

        public IReadOnlyList<string> ExplicitAssetPaths
        {
            get => explicitAssetPaths;
            internal set => explicitAssetPaths = value ?? Array.Empty<string>();
        }

        public IReadOnlyList<string> LocalChartOverlayPaths
        {
            get => localChartOverlayPaths;
            internal set => localChartOverlayPaths = value ?? Array.Empty<string>();
        }

        public IReadOnlyList<string> ReferencedAssetPaths
        {
            get => referencedAssetPaths;
            internal set => referencedAssetPaths = value ?? Array.Empty<string>();
        }

        public string AssetStatus { get; internal set; } = String.Empty;

        public bool IsSongPatch { get; internal set; }
        public bool IsOrphanResourceEntry { get; internal set; }
        public IReadOnlyList<SongOrphanResource> OrphanResources
        {
            get => orphanResources;
            internal set => orphanResources = value ?? Array.Empty<SongOrphanResource>();
        }
        public bool HasOrphanResources => OrphanResources.Count > 0;
        public bool IsEdenProjectCore { get; internal set; }
        internal bool HasValidEdenCoreSignature { get; set; }
        public bool RequiresEdenProjectCore { get; internal set; }
        public bool IsMega39PlusOfficialPvId { get; internal set; }
        public bool HasInvalidOfficialChartOverride { get; internal set; }
        public SongRunStatus AutomaticRunStatus => automaticRunStatus;
        public SongRunStatus? ManualRunStatusOverride
        {
            get => manualRunStatusOverride;
            internal set => manualRunStatusOverride = value;
        }
        public bool HasManualRunStatusOverride => ManualRunStatusOverride.HasValue;
        public SongRunStatus RunStatus
        {
            get => ManualRunStatusOverride ?? AutomaticRunStatus;
            internal set => automaticRunStatus = value;
        }
        public IReadOnlyList<string> RunStatusReasons
        {
            get => runStatusReasons;
            internal set => runStatusReasons = value ?? Array.Empty<string>();
        }
        public string RunStatusReasonsDisplay => String.Join("; ", RunStatusReasons);
        public string AutomaticRunStatusReasonsDisplay => RunStatusReasonsDisplay;

        public IReadOnlyList<string> Warnings
        {
            get => warnings;
            internal set => warnings = value ?? Array.Empty<string>();
        }

        public string WarningsDisplay => String.Join("; ", Warnings);
        public string SearchText { get; internal set; } = String.Empty;
        public bool HasIdConflict { get; internal set; }
        public IReadOnlyList<SongSourcePath> IdConflictSources
        {
            get => idConflictSources;
            internal set => idConflictSources = value ?? Array.Empty<SongSourcePath>();
        }
        public IReadOnlyList<SongSourcePath> PatchSources
        {
            get => patchSources;
            internal set => patchSources = value ?? Array.Empty<SongSourcePath>();
        }
        public bool HasSongPatches => PatchSources.Count > 0;
    }

    internal sealed class SongModScanResult
    {
        public SongModScanResult(IReadOnlyList<SongEntry> entries, bool isComplete)
        {
            Entries = entries ?? Array.Empty<SongEntry>();
            IsComplete = isComplete;
        }

        public IReadOnlyList<SongEntry> Entries { get; }
        public bool IsComplete { get; }
    }
}
