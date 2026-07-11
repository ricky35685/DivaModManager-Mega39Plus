using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DivaModManager
{
    internal sealed class SongRunStatusOverrideStore
    {
        private const int CurrentVersion = 1;
        private const string FileName = "song-run-status-overrides.json";

        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

        private readonly object syncRoot = new object();
        private readonly string filePath;
        private Dictionary<string, StoredOverride> overrides;

        public SongRunStatusOverrideStore()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DivaModManager",
                FileName))
        {
        }

        internal SongRunStatusOverrideStore(string filePath)
        {
            if (String.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("必须提供人工运行判定的保存路径。", nameof(filePath));

            this.filePath = Path.GetFullPath(filePath);
            overrides = Load(this.filePath);
        }

        internal string FilePath => filePath;

        internal bool TryGet(SongEntry entry, out SongRunStatus status)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            lock (syncRoot)
            {
                if (overrides.TryGetValue(CreateKey(entry), out var stored))
                {
                    status = stored.Status.Value;
                    return true;
                }
            }

            status = default;
            return false;
        }

        internal void Apply(SongEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            entry.ManualRunStatusOverride = TryGet(entry, out var status)
                ? status
                : null;
        }

        internal void Set(SongEntry entry, SongRunStatus status)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            if (!Enum.IsDefined(typeof(SongRunStatus), status))
                throw new ArgumentOutOfRangeException(nameof(status));

            var stored = CreateStoredOverride(entry, status);
            lock (syncRoot)
            {
                var key = CreateKey(stored);
                if (overrides.TryGetValue(key, out var existing) && existing.Status == status)
                {
                    entry.ManualRunStatusOverride = status;
                    return;
                }

                var updated = new Dictionary<string, StoredOverride>(overrides, StringComparer.Ordinal)
                {
                    [key] = stored
                };
                Save(updated);
                overrides = updated;
                entry.ManualRunStatusOverride = status;
            }
        }

        internal void Clear(SongEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            lock (syncRoot)
            {
                var key = CreateKey(entry);
                if (overrides.ContainsKey(key))
                {
                    var updated = new Dictionary<string, StoredOverride>(overrides, StringComparer.Ordinal);
                    updated.Remove(key);
                    Save(updated);
                    overrides = updated;
                }

                entry.ManualRunStatusOverride = null;
            }
        }

        private static Dictionary<string, StoredOverride> Load(string path)
        {
            var loaded = new Dictionary<string, StoredOverride>(StringComparer.Ordinal);
            if (!File.Exists(path))
                return loaded;

            try
            {
                var document = JsonSerializer.Deserialize<OverrideDocument>(File.ReadAllText(path), JsonOptions);
                if (document?.Version != CurrentVersion || document.Overrides == null)
                    return loaded;

                foreach (var stored in document.Overrides)
                {
                    if (stored == null ||
                        !stored.Status.HasValue ||
                        !Enum.IsDefined(typeof(SongRunStatus), stored.Status.Value) ||
                        String.IsNullOrWhiteSpace(stored.ModRoot) ||
                        String.IsNullOrWhiteSpace(stored.ContentRoot) ||
                        stored.PvId <= 0)
                    {
                        continue;
                    }

                    var normalized = Normalize(stored);
                    loaded[CreateKey(normalized)] = normalized;
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is JsonException ||
                exception is NotSupportedException)
            {
                // A damaged or temporarily unreadable preference file must not block catalog scanning.
            }

            return loaded;
        }

        private void Save(IReadOnlyDictionary<string, StoredOverride> values)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (String.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("人工运行判定文件没有有效的父目录。");

            Directory.CreateDirectory(directory);
            var document = new OverrideDocument
            {
                Version = CurrentVersion,
                Overrides = values.Values
                    .OrderBy(item => item.ModRoot, StringComparer.Ordinal)
                    .ThenBy(item => item.ContentRoot, StringComparer.Ordinal)
                    .ThenBy(item => item.DatabasePath, StringComparer.Ordinal)
                    .ThenBy(item => item.PvId)
                    .ToList()
            };
            var json = JsonSerializer.Serialize(document, JsonOptions);
            var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }

                File.Move(temporaryPath, filePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private static StoredOverride CreateStoredOverride(SongEntry entry, SongRunStatus status)
        {
            return Normalize(new StoredOverride
            {
                ModRoot = entry.ModRoot,
                ContentRoot = entry.ContentRoot,
                DatabasePath = SelectDatabasePath(entry),
                PvId = entry.PvId,
                Status = status
            });
        }

        private static string CreateKey(SongEntry entry)
        {
            return CreateKey(Normalize(new StoredOverride
            {
                ModRoot = entry.ModRoot,
                ContentRoot = entry.ContentRoot,
                DatabasePath = SelectDatabasePath(entry),
                PvId = entry.PvId
            }));
        }

        private static string CreateKey(StoredOverride stored)
        {
            return String.Join("|", new[]
            {
                EncodeKeyPart(stored.ModRoot),
                EncodeKeyPart(stored.ContentRoot),
                EncodeKeyPart(stored.DatabasePath),
                stored.PvId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        private static string EncodeKeyPart(string value)
        {
            value ??= String.Empty;
            return value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
        }

        private static StoredOverride Normalize(StoredOverride stored)
        {
            return new StoredOverride
            {
                ModRoot = NormalizePath(stored.ModRoot),
                ContentRoot = NormalizePath(stored.ContentRoot),
                DatabasePath = NormalizePath(stored.DatabasePath),
                PvId = stored.PvId,
                Status = stored.Status
            };
        }

        private static string SelectDatabasePath(SongEntry entry)
        {
            return !String.IsNullOrWhiteSpace(entry.DatabasePath)
                ? entry.DatabasePath
                : entry.NewClassicsDatabasePath;
        }

        private static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                return String.Empty;

            var normalized = path.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            try
            {
                normalized = Path.GetFullPath(normalized);
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is PathTooLongException)
            {
                // Keep a deterministic textual identity for malformed or virtual paths.
            }

            return normalized
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private sealed class OverrideDocument
        {
            public OverrideDocument()
            {
            }

            public int Version { get; set; }
            public List<StoredOverride> Overrides { get; set; } = new List<StoredOverride>();
        }

        private sealed class StoredOverride
        {
            public StoredOverride()
            {
            }

            public string ModRoot { get; set; } = String.Empty;
            public string ContentRoot { get; set; } = String.Empty;
            public string DatabasePath { get; set; } = String.Empty;
            public int PvId { get; set; }
            public SongRunStatus? Status { get; set; }
        }
    }
}
