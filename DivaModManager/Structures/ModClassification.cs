using System;
using System.Collections.Generic;

namespace DivaModManager
{
    public enum ModClassificationConfidence
    {
        None,
        Low,
        Medium,
        High
    }

    public sealed class ModClassification
    {
        public ModClassification(
            string primaryCategory,
            IEnumerable<string> detectedCategories,
            string formatVariant,
            ModClassificationConfidence confidence,
            IEnumerable<string> evidence)
        {
            PrimaryCategory = primaryCategory ?? String.Empty;
            DetectedCategories = Array.AsReadOnly(new List<string>(detectedCategories ?? Array.Empty<string>()).ToArray());
            FormatVariant = formatVariant ?? String.Empty;
            Confidence = confidence;
            Evidence = Array.AsReadOnly(new List<string>(evidence ?? Array.Empty<string>()).ToArray());
        }

        public string PrimaryCategory { get; }
        public IReadOnlyList<string> DetectedCategories { get; }
        public string FormatVariant { get; }
        public ModClassificationConfidence Confidence { get; }
        public IReadOnlyList<string> Evidence { get; }
        public bool IsUnknown => DetectedCategories.Count == 0;

        public static ModClassification Unknown(params string[] evidence)
        {
            return new ModClassification(
                String.Empty,
                Array.Empty<string>(),
                String.Empty,
                ModClassificationConfidence.None,
                evidence ?? Array.Empty<string>());
        }
    }
}
