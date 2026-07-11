using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DivaModManager
{
    internal readonly struct Mega39PvField
    {
        public Mega39PvField(
            int pvId,
            string rawPvId,
            string key,
            string value,
            int separatorIndex,
            int valueStart,
            int valueEnd)
        {
            PvId = pvId;
            RawPvId = rawPvId;
            Key = key;
            Value = value;
            SeparatorIndex = separatorIndex;
            ValueStart = valueStart;
            ValueEnd = valueEnd;
        }

        public int PvId { get; }
        public string RawPvId { get; }
        public string Key { get; }
        public string Value { get; }
        public int SeparatorIndex { get; }
        public int ValueStart { get; }
        public int ValueEnd { get; }

        public string ReplaceValue(string originalLine, string value)
        {
            return originalLine.Substring(0, ValueStart) + value + originalLine.Substring(ValueEnd);
        }
    }

    internal static class Mega39PvDatabaseSyntax
    {
        private static readonly Regex FieldPattern = new Regex(
            @"^pv_(\d+)\.(.+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex IndexedFieldPattern = new Regex(
            @"^(.+)\.(\d+)\.(.+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static bool TryParse(string line, out Mega39PvField field)
        {
            field = default;
            if (line == null)
                return false;

            var separator = line.IndexOf('=');
            if (separator < 0)
                return false;

            var left = line.Substring(0, separator).Trim().TrimStart('\uFEFF');
            var match = FieldPattern.Match(left);
            if (!match.Success ||
                !Int32.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var pvId))
                return false;

            var valueStart = separator + 1;
            while (valueStart < line.Length && Char.IsWhiteSpace(line[valueStart]))
                valueStart++;
            var valueEnd = line.Length;
            while (valueEnd > valueStart && Char.IsWhiteSpace(line[valueEnd - 1]))
                valueEnd--;

            field = new Mega39PvField(
                pvId,
                match.Groups[1].Value,
                match.Groups[2].Value,
                line.Substring(valueStart, valueEnd - valueStart),
                separator,
                valueStart,
                valueEnd);
            return true;
        }

        public static bool TryGetIndexedField(
            string key,
            out string collectionKey,
            out int index)
        {
            collectionKey = String.Empty;
            index = -1;
            if (String.IsNullOrWhiteSpace(key))
                return false;

            var match = IndexedFieldPattern.Match(key.Trim());
            if (!match.Success ||
                !Int32.TryParse(
                    match.Groups[2].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out index))
            {
                return false;
            }

            collectionKey = match.Groups[1].Value;
            return true;
        }

        public static bool TryGetDeclaredLength(
            IReadOnlyDictionary<string, string> fields,
            string collectionKey,
            out int length,
            out bool isDeclared)
        {
            length = 0;
            isDeclared = false;
            if (fields == null || String.IsNullOrWhiteSpace(collectionKey))
                return false;

            if (!fields.TryGetValue(collectionKey + ".length", out var rawLength))
                return false;

            isDeclared = true;
            return Int32.TryParse(
                    rawLength?.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out length) &&
                length >= 0;
        }

        public static bool IsIndexedFieldActive(
            IReadOnlyDictionary<string, string> fields,
            string key,
            out string invalidLengthKey)
        {
            invalidLengthKey = String.Empty;
            if (!TryGetIndexedField(key, out var collectionKey, out var index))
                return true;

            if (TryGetDeclaredLength(fields, collectionKey, out var length, out var isDeclared))
                return index < length;

            if (isDeclared)
                invalidLengthKey = collectionKey + ".length";
            return true;
        }
    }
}
