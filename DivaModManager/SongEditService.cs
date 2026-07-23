using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;
using Tomlyn;
using Tomlyn.Model;

namespace DivaModManager
{
    public sealed class SongMetadataUpdate
    {
        public string SongName { get; init; }
        public string SongNameEnglish { get; init; }
        public string SongNameReading { get; init; }
    }

    public sealed class SongEditResult
    {
        private SongEditResult(bool success, string message, string backupPath = null, string installedModPath = null)
        {
            Success = success;
            Message = message ?? String.Empty;
            BackupPath = backupPath;
            InstalledModPath = installedModPath;
        }

        public bool Success { get; }
        public string Message { get; }
        public string BackupPath { get; }
        public string InstalledModPath { get; }

        public static SongEditResult Succeeded(string message, string backupPath = null, string installedModPath = null)
        {
            return new SongEditResult(true, message, backupPath, installedModPath);
        }

        public static SongEditResult Failed(string message, string backupPath = null)
        {
            return new SongEditResult(false, message, backupPath);
        }
    }

    /// <summary>
    /// Performs conservative, reversible edits to Mega Mix+ song mods.
    /// It intentionally does not interpret AFT/PPD databases or synthesize sprite archives.
    /// </summary>
    public sealed class SongEditService
    {
        private static readonly SemaphoreSlim WriteLock = new SemaphoreSlim(1, 1);
        private static readonly Regex NewClassicsSongHeaderRegex = new Regex(
            @"^\s*\[\[songs\]\]\s*(?:#.*)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly string _backupRoot;

        public SongEditService(string backupRoot = null)
        {
            _backupRoot = String.IsNullOrWhiteSpace(backupRoot)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DivaModManager",
                    "Backups",
                    "Songs")
                : Path.GetFullPath(backupRoot);
        }

        public Task<SongEditResult> UpdateMetadataAsync(
            SongEntry song,
            SongMetadataUpdate update,
            CancellationToken cancellationToken = default)
        {
            return RunLockedAsync(() => UpdateMetadata(song, update, cancellationToken), cancellationToken);
        }

        public Task<SongEditResult> DeleteSongAsync(
            SongEntry song,
            CancellationToken cancellationToken = default)
        {
            return RunLockedAsync(() => DeleteSong(song, cancellationToken), cancellationToken);
        }

        public Task<SongEditResult> ReplaceArtworkAsync(
            SongEntry song,
            string preparedFarcPath,
            CancellationToken cancellationToken = default)
        {
            return RunLockedAsync(() => ReplaceArtwork(song, preparedFarcPath, cancellationToken), cancellationToken);
        }

        internal Task<SongEditResult> ReplaceArtworkArchiveAsync(
            SongEntry song,
            string archivePath,
            string ownerContentRoot,
            string expectedHash,
            byte[] replacementBytes,
            string artworkLabel,
            CancellationToken cancellationToken = default)
        {
            return RunLockedAsync(
                () => ReplaceArtworkArchive(
                    song,
                    archivePath,
                    ownerContentRoot,
                    expectedHash,
                    replacementBytes,
                    artworkLabel,
                    cancellationToken),
                cancellationToken);
        }

        public Task<SongEditResult> ImportSongPackageAsync(
            string sourcePath,
            string modsFolder,
            CancellationToken cancellationToken = default)
        {
            return RunLockedAsync(() => ImportSongPackage(sourcePath, modsFolder, cancellationToken), cancellationToken);
        }

        private SongEditResult UpdateMetadata(
            SongEntry song,
            SongMetadataUpdate update,
            CancellationToken cancellationToken)
        {
            if (song == null)
                return SongEditResult.Failed("没有选中歌曲。");
            if (song.IsOrphanResourceEntry)
                return SongEditResult.Failed("废案资源没有歌曲数据库条目，不能修改歌名。");
            if (song.IsSongPatch)
                return SongEditResult.Failed(
                    "此条目是歌曲补丁，不能修改歌名。补丁会复用或扩展其他歌曲，请打开其模组目录进行管理。");
            if (update == null)
                return SongEditResult.Failed("没有提供歌曲信息。");
            if (String.IsNullOrWhiteSpace(song.DatabasePath) || !File.Exists(song.DatabasePath))
                return SongEditResult.Failed("此条目没有可编辑的 MEGA39+ PV 数据库。New Classics 追加难度不包含歌名。");

            string backupPath = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateSongPath(song, song.DatabasePath);
                ValidateMetadataValue(update.SongName, "歌曲名");
                ValidateMetadataValue(update.SongNameEnglish, "英文歌曲名");
                ValidateMetadataValue(update.SongNameReading, "歌曲名读音");
                EnsureDatabaseUnchanged(song);

                var document = TextDocument.Load(song.DatabasePath);
                var updatedText = document.Text;
                updatedText = SetPvValue(updatedText, song.PvId, song.RawPvId, "song_name", update.SongName);
                updatedText = SetPvValue(updatedText, song.PvId, song.RawPvId, "song_name_en", update.SongNameEnglish);
                updatedText = SetPvValue(updatedText, song.PvId, song.RawPvId, "song_name_reading", update.SongNameReading);

                if (String.Equals(updatedText, document.Text, StringComparison.Ordinal))
                    return SongEditResult.Succeeded("歌曲信息没有变化。");

                backupPath = CreateBackupDirectory(song, "metadata");
                BackupFile(song.DatabasePath, song.ContentRoot, backupPath);
                WriteManifest(backupPath, "UpdateMetadata", song, new[] { song.DatabasePath });
                cancellationToken.ThrowIfCancellationRequested();
                EnsureSnapshotUnchanged(song.DatabasePath, document);
                WriteAtomically(song.DatabasePath, document.Encode(updatedText));

                return SongEditResult.Succeeded("歌曲名已保存，并已创建备份。", backupPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return SongEditResult.Failed($"无法保存歌曲信息：{UserErrorMessage.From(exception)}", backupPath);
            }
        }

        private SongEditResult DeleteSong(SongEntry song, CancellationToken cancellationToken)
        {
            if (song == null)
                return SongEditResult.Failed("没有选中歌曲。");
            if (song.IsOrphanResourceEntry)
                return SongEditResult.Failed(
                    "废案资源不会随“删除歌曲”处理。请先核对资源路径，再在模组目录中手动管理。");
            if (song.IsSongPatch)
                return SongEditResult.Failed(
                    "此条目是歌曲补丁，不能作为独立歌曲删除。请打开其模组目录管理补丁，以免破坏它与原曲的关系。");
            if (String.IsNullOrWhiteSpace(song.DatabasePath) && String.IsNullOrWhiteSpace(song.NewClassicsDatabasePath))
                return SongEditResult.Failed("此条目没有可删除的 MEGA39+ 数据库记录。");

            var changedDocuments = new Dictionary<string, TextDocument>(StringComparer.OrdinalIgnoreCase);
            var replacementText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string backupPath = null;
            var movedAssets = new List<(string Original, string Backup)>();
            var writtenDocuments = new List<string>();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!String.IsNullOrWhiteSpace(song.DatabasePath) && File.Exists(song.DatabasePath))
                {
                    ValidateSongPath(song, song.DatabasePath);
                    EnsureDatabaseUnchanged(song);
                    var document = TextDocument.Load(song.DatabasePath);
                    var updated = RemovePvRecord(document.Text, song.PvId, out var removedLineCount);
                    if (removedLineCount == 0)
                        return SongEditResult.Failed("未找到与所选 PV ID 精确匹配的 Legacy 数据库记录，未删除任何文件。");
                    if (!String.Equals(updated, document.Text, StringComparison.Ordinal))
                    {
                        changedDocuments.Add(song.DatabasePath, document);
                        replacementText.Add(song.DatabasePath, updated);
                    }
                }

                if (!String.IsNullOrWhiteSpace(song.NewClassicsDatabasePath) &&
                    File.Exists(song.NewClassicsDatabasePath))
                {
                    ValidateSongPath(song, song.NewClassicsDatabasePath);
                    EnsureFileUnchanged(song.NewClassicsDatabasePath, song.NewClassicsDatabaseHash);
                    var document = TextDocument.Load(song.NewClassicsDatabasePath);
                    var updated = RemoveNewClassicsSong(document.Text, song.PvId, out var removedBlockCount);
                    if (song.Format != SongFormat.Legacy && removedBlockCount == 0)
                        return SongEditResult.Failed("未找到与所选 PV ID 精确匹配的 New Classics 数据块，未删除任何文件。");
                    if (!String.Equals(updated, document.Text, StringComparison.Ordinal))
                    {
                        changedDocuments.Add(song.NewClassicsDatabasePath, document);
                        replacementText.Add(song.NewClassicsDatabasePath, updated);
                    }
                }

                if (changedDocuments.Count == 0)
                    return SongEditResult.Failed("未找到与所选 PV ID 精确匹配的数据库记录，未删除任何文件。");

                var removableAssets = FindExclusiveAssets(song, cancellationToken);
                backupPath = CreateBackupDirectory(song, "delete");
                foreach (var path in changedDocuments.Keys.Concat(removableAssets).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(path))
                        BackupFile(path, song.ContentRoot, backupPath);
                }
                WriteManifest(
                    backupPath,
                    "DeleteSong",
                    song,
                    changedDocuments.Keys.Concat(removableAssets));

                // Database records are removed before assets so a partially completed operation cannot load them.
                foreach (var pair in changedDocuments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureSnapshotUnchanged(pair.Key, pair.Value);
                    WriteAtomically(pair.Key, pair.Value.Encode(replacementText[pair.Key]));
                    writtenDocuments.Add(pair.Key);
                }

                foreach (var assetPath in removableAssets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(assetPath))
                        continue;
                    var backupFile = GetBackupFilePath(assetPath, song.ContentRoot, backupPath);
                    if (!ComputeSha256(assetPath).Equals(ComputeSha256(backupFile), StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"资源在备份后被其他程序修改：{Path.GetFileName(assetPath)}");
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile));
                    File.Move(assetPath, backupFile, true);
                    movedAssets.Add((assetPath, backupFile));
                }

                return SongEditResult.Succeeded(
                    $"已删除 PV {song.PvId} 的数据库记录和 {movedAssets.Count} 个独占资源。共享资源未改动。",
                    backupPath);
            }
            catch (OperationCanceledException)
            {
                var rollbackErrors = RollBackDelete(changedDocuments, writtenDocuments, movedAssets);
                if (rollbackErrors.Count > 0)
                    throw new IOException($"操作已取消，但回滚不完整。请从备份恢复：{backupPath}. {String.Join("; ", rollbackErrors)}");
                throw;
            }
            catch (Exception exception)
            {
                var rollbackErrors = RollBackDelete(changedDocuments, writtenDocuments, movedAssets);
                if (rollbackErrors.Count == 0)
                    return SongEditResult.Failed($"删除失败，已回滚已执行的修改：{UserErrorMessage.From(exception)}", backupPath);
                return SongEditResult.Failed(
                    $"删除失败且回滚不完整：{UserErrorMessage.From(exception)} 请从备份手动恢复。{String.Join("；", rollbackErrors)}",
                    backupPath);
            }
        }

        private SongEditResult ReplaceArtwork(
            SongEntry song,
            string preparedFarcPath,
            CancellationToken cancellationToken)
        {
            if (song == null)
                return SongEditResult.Failed("没有选中歌曲。");
            if (song.IsOrphanResourceEntry)
                return SongEditResult.Failed("废案资源为只读，不能作为歌曲封面替换目标。");
            if (String.IsNullOrWhiteSpace(preparedFarcPath) || !File.Exists(preparedFarcPath))
                return SongEditResult.Failed("请选择已由 MEGA39+ 工具生成的 FARC 封面资源。");
            if (!Path.GetExtension(preparedFarcPath).Equals(".farc", StringComparison.OrdinalIgnoreCase))
                return SongEditResult.Failed("封面必须是 MEGA39+ 可用的 .farc 资源，普通图片不能直接写入游戏。");
            if (String.IsNullOrWhiteSpace(song.ArtworkPath) || !File.Exists(song.ArtworkPath))
                return SongEditResult.Failed("此歌曲没有现有的 spr_sel_pv<ID>.farc，无法在不重写 Sprite DB 的情况下安全替换。");

            string backupPath = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateSongPath(song, song.ArtworkPath);
                var preparedBytes = File.ReadAllBytes(preparedFarcPath);
                ValidatePreparedArtwork(preparedBytes, song.ArtworkPath);
                var originalBytes = File.ReadAllBytes(song.ArtworkPath);

                backupPath = CreateBackupDirectory(song, "artwork");
                BackupFile(song.ArtworkPath, song.ContentRoot, backupPath);
                WriteManifest(backupPath, "ReplaceArtwork", song, new[] { song.ArtworkPath });
                cancellationToken.ThrowIfCancellationRequested();
                if (!ComputeSha256(song.ArtworkPath).Equals(ComputeSha256(originalBytes), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("主封面在备份后已被其他程序修改，操作已停止。");
                WriteAtomically(song.ArtworkPath, preparedBytes);

                return SongEditResult.Succeeded(
                    "主封面 FARC 已替换。缩略图资源未自动改写，以免破坏共享 Sprite DB。",
                    backupPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return SongEditResult.Failed($"无法替换封面资源：{UserErrorMessage.From(exception)}", backupPath);
            }
        }

        private SongEditResult ReplaceArtworkArchive(
            SongEntry song,
            string archivePath,
            string ownerContentRoot,
            string expectedHash,
            byte[] replacementBytes,
            string artworkLabel,
            CancellationToken cancellationToken)
        {
            if (song == null)
                return SongEditResult.Failed("没有选中歌曲。");
            if (song.IsOrphanResourceEntry)
                return SongEditResult.Failed("废案资源为只读，不能作为歌曲图片替换目标。");
            if (String.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                return SongEditResult.Failed("找不到要修改的 MEGA39+ 贴图 FARC。");
            if (replacementBytes == null || replacementBytes.Length == 0)
                return SongEditResult.Failed("没有生成可写入的贴图数据。");
            if (String.IsNullOrWhiteSpace(ownerContentRoot) || !Directory.Exists(ownerContentRoot))
                return SongEditResult.Failed("贴图所属的模组目录不存在。");

            string backupPath = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidatePathUnderRoot(song.ModRoot, ownerContentRoot);
                ValidatePathUnderRoot(ownerContentRoot, archivePath);
                EnsureFileUnchanged(archivePath, expectedHash);

                var originalBytes = File.ReadAllBytes(archivePath);
                backupPath = CreateBackupDirectory(song, "artwork-image");
                BackupFile(archivePath, ownerContentRoot, backupPath);
                WriteManifest(backupPath, "ReplaceArtworkImage", song, new[] { archivePath });

                cancellationToken.ThrowIfCancellationRequested();
                if (!ComputeSha256(archivePath).Equals(
                    ComputeSha256(originalBytes),
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("贴图 FARC 在备份后已被其他程序修改，请刷新后重试。");
                }

                WriteAtomically(archivePath, replacementBytes);
                return SongEditResult.Succeeded(
                    $"{(String.IsNullOrWhiteSpace(artworkLabel) ? "歌曲图片" : artworkLabel)}已替换；同一图集中的其他图片保持不变。",
                    backupPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return SongEditResult.Failed($"无法替换歌曲图片：{UserErrorMessage.From(exception)}", backupPath);
            }
        }

        private SongEditResult ImportSongPackage(
            string sourcePath,
            string modsFolder,
            CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(sourcePath) || (!Directory.Exists(sourcePath) && !File.Exists(sourcePath)))
                return SongEditResult.Failed("请选择 MEGA39+ 歌曲包目录或 ZIP 文件。");
            if (String.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder))
                return SongEditResult.Failed("Mod 目录不存在，请先完成管理器设置。");

            var fullModsFolder = Path.GetFullPath(modsFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var importWorkspace = Path.Combine(_backupRoot, ".import-" + Guid.NewGuid().ToString("N"));
            string packageRoot = null;
            string stagingPath = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(importWorkspace);

                if (File.Exists(sourcePath))
                {
                    var extension = Path.GetExtension(sourcePath);
                    if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
                        return SongEditResult.Failed("歌曲包文件必须是 ZIP、7z 或 RAR。");
                    ExtractArchiveSafely(sourcePath, importWorkspace, cancellationToken);
                    packageRoot = FindPackageRoot(importWorkspace);
                }
                else
                {
                    packageRoot = FindPackageRoot(Path.GetFullPath(sourcePath));
                }

                if (packageRoot == null)
                    return SongEditResult.Failed("未找到 config.toml 和 rom/mod_pv_db.txt（或 mod_nc_pv_db.txt）。这不是完整的 MEGA39+ 歌曲包。");
                if (IsUnderRoot(fullModsFolder, packageRoot))
                    return SongEditResult.Failed("所选歌曲包已位于当前 Mods 目录中。");

                ValidatePackageIncludes(packageRoot);
                var importedSongs = ValidateSongPackage(packageRoot);
                ValidateImportConflicts(importedSongs, fullModsFolder, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var fallbackName = File.Exists(sourcePath)
                    ? Path.GetFileNameWithoutExtension(sourcePath)
                    : new DirectoryInfo(packageRoot).Name;
                var desiredName = SanitizeFileName(ReadPackageName(packageRoot) ?? fallbackName);
                if (String.IsNullOrWhiteSpace(desiredName))
                    desiredName = "Imported Song";
                var targetPath = GetUniqueDirectoryPath(Path.Combine(fullModsFolder, desiredName));
                var stagingParent = Path.GetDirectoryName(fullModsFolder);
                stagingPath = Path.Combine(stagingParent, ".dmm-song-import-" + Guid.NewGuid().ToString("N"));
                CopyDirectorySafely(packageRoot, stagingPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(stagingPath, targetPath);
                stagingPath = null;

                return SongEditResult.Succeeded(
                    $"歌曲包已作为独立 Mod 导入：{Path.GetFileName(targetPath)}",
                    installedModPath: targetPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return SongEditResult.Failed($"导入歌曲包失败：{UserErrorMessage.From(exception)}");
            }
            finally
            {
                TryDeleteDirectory(stagingPath);
                TryDeleteDirectory(importWorkspace);
            }
        }

        private async Task<SongEditResult> RunLockedAsync(
            Func<SongEditResult> operation,
            CancellationToken cancellationToken)
        {
            await WriteLock.WaitAsync(cancellationToken);
            try
            {
                return await Task.Run(operation, cancellationToken);
            }
            finally
            {
                WriteLock.Release();
            }
        }

        private static string SetPvValue(
            string text,
            int pvId,
            string primaryRawId,
            string key,
            string value)
        {
            if (value == null)
                return text;

            var lines = SplitLines(text);
            var prefix = $"pv_{primaryRawId}.{key}=";
            var found = false;

            foreach (var line in lines)
            {
                if (!Mega39PvDatabaseSyntax.TryParse(line.Content, out var field) ||
                    field.PvId != pvId ||
                    !field.Key.Equals(key, StringComparison.Ordinal))
                    continue;
                line.Content = field.ReplaceValue(line.Content, value);
                found = true;
            }

            if (!found)
            {
                var insertIndex = lines.FindLastIndex(line =>
                    Mega39PvDatabaseSyntax.TryParse(line.Content, out var field) &&
                    field.PvId == pvId);
                if (insertIndex < 0)
                    throw new InvalidDataException($"数据库中没有 PV {pvId} 的记录。");

                var newline = DetectNewline(lines);
                if (String.IsNullOrEmpty(lines[insertIndex].Terminator))
                    lines[insertIndex].Terminator = newline;
                lines.Insert(insertIndex + 1, new TextLine(prefix + value, newline));
            }

            return JoinLines(lines);
        }

        private static string RemovePvRecord(string text, int pvId, out int removedLineCount)
        {
            var lines = SplitLines(text);
            removedLineCount = lines.RemoveAll(line =>
                Mega39PvDatabaseSyntax.TryParse(line.Content, out var field) &&
                field.PvId == pvId);
            return JoinLines(lines);
        }

        private static string RemoveNewClassicsSong(string text, int pvId, out int removedBlockCount)
        {
            var lines = SplitLines(text);
            var remove = new bool[lines.Count];
            removedBlockCount = 0;

            for (var index = 0; index < lines.Count; index++)
            {
                if (!NewClassicsSongHeaderRegex.IsMatch(lines[index].Content))
                    continue;

                var end = index + 1;
                while (end < lines.Count && !NewClassicsSongHeaderRegex.IsMatch(lines[end].Content))
                    end++;

                int? blockId = null;
                for (var blockIndex = index + 1; blockIndex < end; blockIndex++)
                {
                    if (TryParseTomlIdLine(lines[blockIndex].Content, out var parsedId))
                    {
                        blockId = parsedId;
                        break;
                    }
                }

                if (blockId == pvId)
                {
                    removedBlockCount++;
                    for (var blockIndex = index; blockIndex < end; blockIndex++)
                        remove[blockIndex] = true;
                }
                index = end - 1;
            }

            var kept = new List<TextLine>();
            for (var index = 0; index < lines.Count; index++)
            {
                if (!remove[index])
                    kept.Add(lines[index]);
            }
            return JoinLines(kept);
        }

        private static bool TryParseTomlIdLine(string line, out int result)
        {
            result = 0;
            var separator = line.IndexOf('=');
            if (separator < 0 || !line.Substring(0, separator).Trim().Equals("id", StringComparison.Ordinal))
                return false;

            var raw = line.Substring(separator + 1);
            var comment = raw.IndexOf('#');
            if (comment >= 0)
                raw = raw.Substring(0, comment);
            raw = raw.Trim().Replace("_", String.Empty);
            if (raw.Length == 0)
                return false;

            try
            {
                long value;
                var sign = 1;
                if (raw[0] == '+' || raw[0] == '-')
                {
                    if (raw[0] == '-')
                        sign = -1;
                    raw = raw.Substring(1);
                }

                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    value = Convert.ToInt64(raw.Substring(2), 16) * sign;
                else if (raw.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
                    value = Convert.ToInt64(raw.Substring(2), 8) * sign;
                else if (raw.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                    value = Convert.ToInt64(raw.Substring(2), 2) * sign;
                else if (!Int64.TryParse(
                    (sign < 0 ? "-" : String.Empty) + raw,
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value))
                    return false;

                if (value <= 0 || value > Int32.MaxValue)
                    return false;
                result = (int)value;
                return true;
            }
            catch (Exception exception) when (exception is FormatException || exception is OverflowException)
            {
                return false;
            }
        }

        private static IReadOnlyList<string> FindExclusiveAssets(
            SongEntry song,
            CancellationToken cancellationToken)
        {
            SongModScanResult scan;
            try
            {
                scan = SongCatalogService.ScanModForEditing(song.ModRoot, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return Array.Empty<string>();
            }
            if (!scan.IsComplete)
                return Array.Empty<string>();

            var matches = scan.Entries.Where(entry => IsSameSongEntry(entry, song)).ToArray();
            if (matches.Length != 1)
                return Array.Empty<string>();
            var currentSong = matches[0];

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assetPath in currentSong.ReferencedAssetPaths ?? Array.Empty<string>())
            {
                if (!String.IsNullOrWhiteSpace(assetPath))
                    candidates.Add(Path.GetFullPath(assetPath));
            }
            if (!String.IsNullOrWhiteSpace(currentSong.ArtworkPath))
                candidates.Add(Path.GetFullPath(currentSong.ArtworkPath));

            candidates.RemoveWhere(path =>
                Path.GetFileName(path).Equals("mod_spr_db.bin", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path));

            var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var other in scan.Entries)
            {
                if (IsSameSongEntry(other, song))
                    continue;
                foreach (var path in other.ReferencedAssetPaths ?? Array.Empty<string>())
                {
                    if (!String.IsNullOrWhiteSpace(path))
                        shared.Add(Path.GetFullPath(path));
                }
                if (!String.IsNullOrWhiteSpace(other.ArtworkPath))
                    shared.Add(Path.GetFullPath(other.ArtworkPath));
            }

            candidates.ExceptWith(shared);
            foreach (var path in candidates)
                ValidateSongPath(song, path);
            return candidates.ToArray();
        }

        private static bool IsSameSongEntry(SongEntry left, SongEntry right)
        {
            return left.PvId == right.PvId &&
                PathEquals(left.ModRoot, right.ModRoot) &&
                PathEquals(left.ContentRoot, right.ContentRoot) &&
                OptionalPathEquals(left.DatabasePath, right.DatabasePath) &&
                OptionalPathEquals(left.NewClassicsDatabasePath, right.NewClassicsDatabasePath);
        }

        private static bool OptionalPathEquals(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right))
                return String.IsNullOrWhiteSpace(left) && String.IsNullOrWhiteSpace(right);
            return PathEquals(left, right);
        }

        private static void ValidatePreparedArtwork(byte[] bytes, string destinationPath)
        {
            if (bytes.Length < 32 ||
                bytes[0] != (byte)'F' || bytes[1] != (byte)'A' || bytes[2] != (byte)'r' || bytes[3] != (byte)'C')
                throw new InvalidDataException("文件没有 MEGA39+ 压缩 FArC 文件头。");

            var headerLength = Math.Min(bytes.Length, 8192);
            var headerText = Encoding.ASCII.GetString(bytes, 0, headerLength);
            var expectedInternalName = Path.GetFileNameWithoutExtension(destinationPath) + ".bin";
            if (headerText.IndexOf(expectedInternalName, StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidDataException($"FARC 内部资源不是 {expectedInternalName}，可能属于另一首歌曲。");
        }

        private static IReadOnlyList<SongEntry> ValidateSongPackage(string packageRoot)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(packageRoot));
            if (String.IsNullOrWhiteSpace(parent))
                throw new InvalidDataException("歌曲包根目录无效。");
            var songs = SongCatalogService.ScanModsWithoutArtwork(parent)
                .Where(song => PathEquals(song.ModRoot, packageRoot))
                .ToArray();
            if (songs.Length == 0)
                throw new InvalidDataException("歌曲包中没有可识别的 MEGA39+ 歌曲或追加难度。");

            foreach (var song in songs)
            {
                var missingCharts = song.Difficulties
                    .Where(difficulty =>
                        !difficulty.ScriptExists &&
                        (!String.IsNullOrWhiteSpace(difficulty.ScriptReference) ||
                            difficulty.HasDeclaredLegacyLength))
                    .Select(difficulty => String.IsNullOrWhiteSpace(difficulty.ScriptReference)
                        ? $"{difficulty.NormalizedName}[{difficulty.Index}]"
                        : difficulty.ScriptReference)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (missingCharts.Length > 0)
                    throw new InvalidDataException($"PV {song.PvId} 缺少已声明的谱面：{String.Join(", ", missingCharts)}");

                if (song.HasInvalidOfficialChartOverride)
                {
                    var detail = String.IsNullOrWhiteSpace(song.RunStatusReasonsDisplay)
                        ? String.Empty
                        : $" 诊断：{song.RunStatusReasonsDisplay}";
                    throw new InvalidDataException(
                        $"PV {song.PvId} 包含 Legacy 或未在 nc_db.toml 注册的官曲谱面。{detail}");
                }

                if (song.Format == SongFormat.AdditionalDifficulty)
                {
                    if (!song.Difficulties.Any(difficulty => difficulty.ScriptExists))
                        throw new InvalidDataException($"PV {song.PvId} 没有可用的 MEGA39+ DSC 追加难度谱面。");
                    continue;
                }
                if (String.IsNullOrWhiteSpace(song.SongName))
                    throw new InvalidDataException($"PV {song.PvId} 缺少 song_name。");
                if (String.IsNullOrWhiteSpace(song.AudioReference) || !song.AudioExists)
                    throw new InvalidDataException($"PV {song.PvId} 缺少数据库所引用的音频文件。");
                if (!song.Difficulties.Any(difficulty => difficulty.ScriptExists))
                    throw new InvalidDataException($"PV {song.PvId} 没有可用的 MEGA39+ DSC 谱面。");

                ValidateDeclaredPackageAssets(song);
            }

            var duplicateIds = songs.GroupBy(song => song.PvId)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateIds.Length > 0)
                throw new InvalidDataException($"歌曲包内部存在重复 PV ID：{String.Join(", ", duplicateIds)}");
            return songs;
        }

        private static void ValidatePackageIncludes(string packageRoot)
        {
            var configPath = Path.Combine(packageRoot, "config.toml");
            if (!Toml.TryToModel(File.ReadAllText(configPath), out TomlTable config, out var diagnostics))
            {
                throw new InvalidDataException("config.toml 无法解析：TOML 格式无效或语法有误。");
            }
            if (!config.TryGetValue("include", out var includeValue))
                return;
            if (includeValue is string || includeValue is not System.Collections.IEnumerable includes)
                throw new InvalidDataException("config.toml 的 include 必须是路径数组。");

            foreach (var value in includes)
            {
                if (value is not string include || String.IsNullOrWhiteSpace(include))
                    throw new InvalidDataException("config.toml 的 include 包含无效路径项。");

                try
                {
                    var normalized = include
                        .Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(normalized))
                        throw new InvalidDataException($"config.toml 的 include 不能使用绝对路径：{include}");
                    var includeRoot = Path.GetFullPath(Path.Combine(packageRoot, normalized));
                    if (!IsUnderRoot(packageRoot, includeRoot))
                        throw new InvalidDataException($"config.toml 的 include 越过歌曲包目录：{include}");
                }
                catch (InvalidDataException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is ArgumentException ||
                    exception is NotSupportedException)
                {
                    throw new InvalidDataException($"config.toml 的 include 路径无效：{include}", exception);
                }
            }
        }

        private static void ValidateDeclaredPackageAssets(SongEntry song)
        {
            if (String.IsNullOrWhiteSpace(song.DatabasePath) || !File.Exists(song.DatabasePath))
                return;

            var fields = File.ReadLines(song.DatabasePath)
                .Select(line => Mega39PvDatabaseSyntax.TryParse(line, out var field)
                    ? (Mega39PvField?)field
                    : null)
                .Where(field => field.HasValue && field.Value.PvId == song.PvId)
                .Select(field => field.Value)
                .ToArray();
            var fieldsByKey = fields
                .GroupBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().Value,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var field in fields)
            {
                if (!IsDeclaredPackageAsset(field.Key, field.Value) ||
                    !Mega39PvDatabaseSyntax.IsIndexedFieldActive(
                        fieldsByKey,
                        field.Key,
                        out _))
                    continue;

                var path = ResolveDeclaredPackageAsset(song.ContentRoot, field.Key, field.Value);
                if (File.Exists(path))
                    continue;

                if (field.Key.StartsWith("another_song.", StringComparison.OrdinalIgnoreCase) &&
                    field.Key.EndsWith(".song_file_name", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (song.IsEdenProjectCore && song.IsMega39PlusOfficialPvId)
                {
                    if (field.Key.Equals("song_file_name", StringComparison.OrdinalIgnoreCase) &&
                        SongCatalogService.IsGameStockMediaReference(field.Value, "sound/song"))
                    {
                        continue;
                    }
                    if (IsVideoReferenceField(field.Key) &&
                        SongCatalogService.IsGameStockMediaReference(field.Value, "movie"))
                    {
                        continue;
                    }
                    if (field.Key.StartsWith("difficulty.", StringComparison.OrdinalIgnoreCase) &&
                        field.Key.EndsWith(".script_file_name", StringComparison.OrdinalIgnoreCase) &&
                        song.Difficulties.Any(difficulty =>
                            difficulty.ScriptReference.Equals(field.Value, StringComparison.OrdinalIgnoreCase) &&
                            (difficulty.IsAvailableFromGame ||
                                SongCatalogService.IsKnownEdenProjectOfficialExtension(song, difficulty))))
                    {
                        continue;
                    }
                }

                if (IsVideoReferenceField(field.Key))
                {
                    var convertedVideoFound = false;
                    foreach (var extension in new[] { ".usm", ".mp4" })
                    {
                        var convertedPath = Path.ChangeExtension(path, extension);
                        if (IsUnderRoot(song.ContentRoot, convertedPath) && File.Exists(convertedPath))
                        {
                            convertedVideoFound = true;
                            break;
                        }
                    }
                    if (convertedVideoFound)
                        continue;
                }

                var assetKind = field.Key.EndsWith("song_file_name", StringComparison.OrdinalIgnoreCase)
                    ? "音频"
                    : IsVideoReferenceField(field.Key)
                        ? "视频"
                        : field.Key.EndsWith("script_file_name", StringComparison.OrdinalIgnoreCase)
                            ? "谱面"
                            : "资源";
                throw new InvalidDataException(
                    $"PV {song.PvId} 缺少数据库所引用的{assetKind}文件：{field.Value}");
            }
        }

        private static bool IsDeclaredPackageAsset(string key, string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim().Replace('\\', '/');
            return normalized.StartsWith("rom/", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("song_file_name", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("another_song.", StringComparison.OrdinalIgnoreCase) &&
                    key.EndsWith(".song_file_name", StringComparison.OrdinalIgnoreCase) ||
                IsVideoReferenceField(key) ||
                key.StartsWith("difficulty.", StringComparison.OrdinalIgnoreCase) &&
                    key.EndsWith(".script_file_name", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVideoReferenceField(string key)
        {
            return key.Equals("movie_file_name", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("movie_list.", StringComparison.OrdinalIgnoreCase) &&
                    key.EndsWith(".name", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("difficulty.", StringComparison.OrdinalIgnoreCase) &&
                    key.EndsWith(".movie_file_name", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveDeclaredPackageAsset(
            string contentRoot,
            string key,
            string reference)
        {
            try
            {
                var normalized = reference.Trim()
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                var path = Path.GetFullPath(Path.Combine(contentRoot, normalized));
                if (!IsUnderRoot(contentRoot, path))
                    throw new InvalidDataException(
                        $"数据库字段 {key} 引用了歌曲包之外的路径：{reference}");
                return path;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is ArgumentException ||
                exception is NotSupportedException)
            {
                throw new InvalidDataException(
                    $"数据库字段 {key} 包含无效资源路径：{reference}",
                    exception);
            }
        }

        private static void ValidateImportConflicts(
            IReadOnlyCollection<SongEntry> importedSongs,
            string modsFolder,
            CancellationToken cancellationToken)
        {
            var installedIds = new HashSet<int>(SongCatalogService.ScanModsWithoutArtwork(modsFolder, cancellationToken)
                .Where(song => song.Format != SongFormat.AdditionalDifficulty)
                .Select(song => song.PvId));
            var conflicts = importedSongs
                .Where(song => song.Format != SongFormat.AdditionalDifficulty && installedIds.Contains(song.PvId))
                .Select(song => song.PvId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            if (conflicts.Length > 0)
                throw new InvalidDataException(
                    $"以下 PV ID 已被安装的歌曲占用：{String.Join(", ", conflicts.Take(20))}" +
                    (conflicts.Length > 20 ? " ..." : String.Empty));
        }

        private static string FindPackageRoot(string root)
        {
            if (!Directory.Exists(root))
                return null;
            if (IsPackageRoot(root))
                return Path.GetFullPath(root);

            var candidates = new List<string>();
            var pending = new Queue<(DirectoryInfo Directory, int Depth)>();
            pending.Enqueue((new DirectoryInfo(root), 0));
            while (pending.Count > 0)
            {
                var item = pending.Dequeue();
                if (item.Depth >= 3)
                    continue;
                foreach (var child in item.Directory.GetDirectories())
                {
                    if (IsReparsePoint(child))
                        continue;
                    if (IsPackageRoot(child.FullName))
                        candidates.Add(child.FullName);
                    else
                        pending.Enqueue((child, item.Depth + 1));
                }
            }
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static bool IsPackageRoot(string root)
        {
            var configPath = Path.Combine(root, "config.toml");
            if (!File.Exists(configPath))
                return false;
            try
            {
                if (!Toml.TryToModel(File.ReadAllText(configPath), out TomlTable config, out _))
                    return false;

                var contentRoots = new List<string>();
                if (!config.TryGetValue("include", out var includeValue))
                    contentRoots.Add(Path.GetFullPath(root));
                else if (includeValue is not string && includeValue is System.Collections.IEnumerable includes)
                {
                    foreach (var value in includes)
                    {
                        if (value is not string include)
                            continue;
                        var normalized = include.Replace('/', Path.DirectorySeparatorChar)
                            .Replace('\\', Path.DirectorySeparatorChar);
                        var contentRoot = Path.GetFullPath(Path.Combine(root, normalized));
                        if (IsUnderRoot(root, contentRoot))
                            contentRoots.Add(contentRoot);
                    }
                }

                return contentRoots.Any(contentRoot =>
                {
                    var rom = Path.Combine(contentRoot, "rom");
                    return Directory.Exists(rom) &&
                        (File.Exists(Path.Combine(rom, "mod_pv_db.txt")) ||
                         File.Exists(Path.Combine(rom, "mod_nc_pv_db.txt")) ||
                         File.Exists(Path.Combine(rom, "nc_db.toml")));
                });
            }
            catch (Exception exception) when (IsFileSystemException(exception) || exception is ArgumentException)
            {
                return false;
            }
        }

        private static string ReadPackageName(string packageRoot)
        {
            try
            {
                var configText = File.ReadAllText(Path.Combine(packageRoot, "config.toml"));
                if (Toml.TryToModel(configText, out TomlTable config, out _) &&
                    config.TryGetValue("name", out var value) &&
                    value is string name &&
                    !String.IsNullOrWhiteSpace(name))
                    return name.Trim();
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
            }
            return null;
        }

        private static void ExtractArchiveSafely(
            string archivePath,
            string destinationRoot,
            CancellationToken cancellationToken)
        {
            if (Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ExtractZipSafely(archivePath, destinationRoot, cancellationToken);
                return;
            }

            if (Path.GetExtension(archivePath).Equals(".7z", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = SevenZipArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath);
                using var sevenZipReader = archive.ExtractAllEntries();
                ExtractReaderSafely(sevenZipReader, destinationRoot, cancellationToken);
                return;
            }

            using var stream = File.OpenRead(archivePath);
            using var archiveReader = ReaderFactory.OpenReader(stream, ReaderOptions.ForExternalStream);
            ExtractReaderSafely(archiveReader, destinationRoot, cancellationToken);
        }

        private static void ExtractReaderSafely(
            SharpCompress.Readers.IReader reader,
            string destinationRoot,
            CancellationToken cancellationToken)
        {
            const long maximumExpandedBytes = 100L * 1024 * 1024 * 1024;
            const int maximumFiles = 100000;
            long expandedBytes = 0;
            var fileCount = 0;
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsDirectory)
                    continue;
                if (entry.IsEncrypted)
                    throw new InvalidDataException("歌曲包已加密，无法导入。");
                if (!String.IsNullOrWhiteSpace(entry.LinkTarget))
                    throw new InvalidDataException("歌曲包包含符号链接，拒绝导入。");
                fileCount++;
                expandedBytes = checked(expandedBytes + Math.Max(0, entry.Size));
                if (fileCount > maximumFiles || expandedBytes > maximumExpandedBytes)
                    throw new InvalidDataException("歌曲包解压后的文件数量或大小超过安全限制。");

                var destination = GetSafeArchiveDestination(destinationRoot, entry.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                reader.WriteEntryTo(output);
            }
        }

        private static void ExtractZipSafely(
            string zipPath,
            string destinationRoot,
            CancellationToken cancellationToken)
        {
            const long maximumExpandedBytes = 100L * 1024 * 1024 * 1024;
            const int maximumFiles = 100000;
            long expandedBytes = 0;
            var fileCount = 0;
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (String.IsNullOrEmpty(entry.FullName))
                    continue;
                var destination = GetSafeArchiveDestination(destinationRoot, entry.FullName);
                if (String.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                fileCount++;
                expandedBytes = checked(expandedBytes + Math.Max(0, entry.Length));
                if (fileCount > maximumFiles || expandedBytes > maximumExpandedBytes)
                    throw new InvalidDataException("歌曲包解压后的文件数量或大小超过安全限制。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                entry.ExtractToFile(destination, false);
            }
        }

        private static string GetSafeArchiveDestination(string destinationRoot, string entryPath)
        {
            if (String.IsNullOrWhiteSpace(entryPath) || Path.IsPathRooted(entryPath))
                throw new InvalidDataException("歌曲包包含无效的绝对路径。");
            var normalized = entryPath.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, normalized));
            if (!IsUnderRoot(destinationRoot, destination))
                throw new InvalidDataException("歌曲包包含越界路径。");
            return destination;
        }

        private static void CopyDirectorySafely(
            string sourceRoot,
            string destinationRoot,
            CancellationToken cancellationToken)
        {
            var source = new DirectoryInfo(sourceRoot);
            if (IsReparsePoint(source))
                throw new IOException("歌曲包根目录不能是符号链接或联接点。");
            CopyDirectoryNode(source, destinationRoot, cancellationToken);
        }

        private static void CopyDirectoryNode(
            DirectoryInfo source,
            string destination,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in source.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"歌曲包包含符号链接：{file.Name}");
                file.CopyTo(Path.Combine(destination, file.Name), false);
            }
            foreach (var directory in source.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(directory))
                    throw new IOException($"歌曲包包含符号链接或联接点：{directory.Name}");
                CopyDirectoryNode(directory, Path.Combine(destination, directory.Name), cancellationToken);
            }
        }

        private string CreateBackupDirectory(SongEntry song, string operation)
        {
            var modName = SanitizeFileName(song.ModName);
            var folderName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{operation}-{modName}-pv{song.PvId}-{Guid.NewGuid():N}";
            var path = Path.Combine(_backupRoot, folderName);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void BackupFile(string sourcePath, string contentRoot, string backupPath)
        {
            var destination = GetBackupFilePath(sourcePath, contentRoot, backupPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(sourcePath, destination, true);
        }

        private static string GetBackupFilePath(string sourcePath, string contentRoot, string backupPath)
        {
            ValidatePathUnderRoot(contentRoot, sourcePath);
            var relative = Path.GetRelativePath(contentRoot, sourcePath);
            return Path.Combine(backupPath, "content", relative);
        }

        private static void WriteManifest(
            string backupPath,
            string operation,
            SongEntry song,
            IEnumerable<string> paths)
        {
            var files = paths
                .Where(path => !String.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new
                {
                    Path = Path.GetFullPath(path),
                    Exists = File.Exists(path),
                    Sha256 = File.Exists(path) ? ComputeSha256(path) : null
                })
                .ToArray();
            var manifest = new
            {
                Version = 1,
                CreatedUtc = DateTime.UtcNow,
                Operation = operation,
                song.ModName,
                song.PvId,
                song.RawPvId,
                song.ModRoot,
                song.ContentRoot,
                Files = files
            };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(backupPath, "manifest.json"), json, new UTF8Encoding(false));
        }

        private static IReadOnlyList<string> RollBackDelete(
            IDictionary<string, TextDocument> changedDocuments,
            IEnumerable<string> writtenDocuments,
            IEnumerable<(string Original, string Backup)> movedAssets)
        {
            var errors = new List<string>();
            foreach (var moved in movedAssets.Reverse())
            {
                try
                {
                    if (File.Exists(moved.Backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(moved.Original));
                        File.Move(moved.Backup, moved.Original, true);
                    }
                }
                catch (Exception exception)
                {
                    errors.Add($"无法恢复 {moved.Original}：{UserErrorMessage.From(exception)}");
                }
            }

            foreach (var path in writtenDocuments.Reverse())
            {
                try
                {
                    WriteAtomically(path, changedDocuments[path].OriginalBytes);
                }
                catch (Exception exception)
                {
                    errors.Add($"无法恢复 {path}：{UserErrorMessage.From(exception)}");
                }
            }
            return errors;
        }

        private static void EnsureDatabaseUnchanged(SongEntry song)
        {
            EnsureFileUnchanged(song.DatabasePath, song.DatabaseHash);
        }

        private static void EnsureFileUnchanged(string path, string expectedHash)
        {
            if (String.IsNullOrWhiteSpace(expectedHash))
                return;
            var currentHash = ComputeSha256(path);
            if (!currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("数据库在扫描后已被其他程序修改，请刷新歌曲列表后重试。");
        }

        private static void EnsureSnapshotUnchanged(string path, TextDocument snapshot)
        {
            var expectedHash = ComputeSha256(snapshot.OriginalBytes);
            if (!ComputeSha256(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("数据库在备份后已被其他程序修改，操作已停止。");
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream));
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(bytes));
        }

        private static void ValidateSongPath(SongEntry song, string path)
        {
            if (String.IsNullOrWhiteSpace(song.ModRoot) || String.IsNullOrWhiteSpace(song.ContentRoot))
                throw new InvalidDataException("歌曲内容根目录无效。");
            ValidatePathUnderRoot(song.ModRoot, song.ContentRoot);
            ValidateDirectoryChain(song.ModRoot, song.ContentRoot);
            ValidatePathUnderRoot(song.ContentRoot, path);
        }

        private static void ValidateDirectoryChain(string root, string targetDirectory)
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullTarget = Path.GetFullPath(targetDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsUnderRoot(fullRoot, fullTarget))
                throw new InvalidDataException("歌曲内容目录越过 Mod 根目录。");

            var current = new DirectoryInfo(fullRoot);
            if (current.Exists && IsReparsePoint(current))
                throw new IOException("Mod 或 include 路径包含符号链接或联接点，拒绝执行写入。");
            var relative = Path.GetRelativePath(fullRoot, fullTarget);
            if (relative == ".")
                return;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (String.IsNullOrWhiteSpace(segment))
                    continue;
                current = new DirectoryInfo(Path.Combine(current.FullName, segment));
                if (current.Exists && IsReparsePoint(current))
                    throw new IOException("Mod 或 include 路径包含符号链接或联接点，拒绝执行写入。");
            }
        }

        private static void ValidatePathUnderRoot(string root, string path)
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            if (!IsUnderRoot(fullRoot, fullPath))
                throw new InvalidDataException("资源路径越过歌曲内容目录。");

            var targetFile = new FileInfo(fullPath);
            if (targetFile.Exists && IsReparsePoint(targetFile))
                throw new IOException("资源文件是符号链接或重解析点，拒绝执行写入。");

            var current = targetFile.Directory;
            while (current != null && IsUnderRoot(fullRoot, current.FullName))
            {
                if (current.Exists && IsReparsePoint(current))
                    throw new IOException("资源路径包含符号链接或联接点，拒绝执行写入。");
                if (PathEquals(current.FullName, fullRoot))
                    break;
                current = current.Parent;
            }
        }

        private static bool IsUnderRoot(string root, string path)
        {
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedPath = Path.GetFullPath(path);
            return PathEquals(normalizedRoot, normalizedPath) ||
                normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathEquals(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right))
                return false;
            return Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReparsePoint(FileSystemInfo entry)
        {
            return (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        }

        private static bool IsFileSystemException(Exception exception)
        {
            return exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is PathTooLongException ||
                exception is NotSupportedException;
        }

        private static void ValidateMetadataValue(string value, string label)
        {
            if (value != null && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\0') >= 0))
                throw new InvalidDataException($"{label}不能包含换行或 NUL 字符。");
        }

        private static void WriteAtomically(string path, byte[] bytes)
        {
            var tempPath = Path.Combine(
                Path.GetDirectoryName(path),
                "." + Path.GetFileName(path) + ".dmm-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, path, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        private static string GetUniqueDirectoryPath(string desiredPath)
        {
            if (!Directory.Exists(desiredPath) && !File.Exists(desiredPath))
                return desiredPath;
            for (var index = 2; index < Int32.MaxValue; index++)
            {
                var candidate = desiredPath + " (" + index + ")";
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                    return candidate;
            }
            throw new IOException("无法生成唯一的 Mod 文件夹名称。");
        }

        private static string SanitizeFileName(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return String.Empty;
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (var character in value.Trim())
                builder.Append(invalid.Contains(character) ? '_' : character);
            return builder.ToString().TrimEnd('.', ' ');
        }

        private static void TryDeleteDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                return;
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static List<TextLine> SplitLines(string text)
        {
            var lines = new List<TextLine>();
            var start = 0;
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] != '\r' && text[index] != '\n')
                    continue;
                var terminatorLength = text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
                lines.Add(new TextLine(text.Substring(start, index - start), text.Substring(index, terminatorLength)));
                index += terminatorLength - 1;
                start = index + 1;
            }
            if (start < text.Length || text.Length == 0)
                lines.Add(new TextLine(text.Substring(start), String.Empty));
            return lines;
        }

        private static string JoinLines(IEnumerable<TextLine> lines)
        {
            var builder = new StringBuilder();
            foreach (var line in lines)
                builder.Append(line.Content).Append(line.Terminator);
            return builder.ToString();
        }

        private static string DetectNewline(IReadOnlyList<TextLine> lines)
        {
            return lines.Select(line => line.Terminator).FirstOrDefault(value => value.Length > 0) ?? Environment.NewLine;
        }

        private sealed class TextLine
        {
            public TextLine(string content, string terminator)
            {
                Content = content;
                Terminator = terminator;
            }

            public string Content { get; set; }
            public string Terminator { get; set; }
        }

        private sealed class TextDocument
        {
            private TextDocument(byte[] originalBytes, Encoding encoding, byte[] preamble, string text)
            {
                OriginalBytes = originalBytes;
                Encoding = encoding;
                Preamble = preamble;
                Text = text;
            }

            public byte[] OriginalBytes { get; }
            public Encoding Encoding { get; }
            public byte[] Preamble { get; }
            public string Text { get; }

            public static TextDocument Load(string path)
            {
                var bytes = File.ReadAllBytes(path);
                var (encoding, preambleLength) = DetectEncoding(bytes);
                var preamble = bytes.Take(preambleLength).ToArray();
                var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
                return new TextDocument(bytes, encoding, preamble, text);
            }

            public byte[] Encode(string text)
            {
                var body = Encoding.GetBytes(text);
                if (Preamble.Length == 0)
                    return body;
                var result = new byte[Preamble.Length + body.Length];
                Buffer.BlockCopy(Preamble, 0, result, 0, Preamble.Length);
                Buffer.BlockCopy(body, 0, result, Preamble.Length, body.Length);
                return result;
            }

            private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
            {
                if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                    return (new UTF32Encoding(true, false, true), 4);
                if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                    return (new UTF32Encoding(false, false, true), 4);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    return (new UTF8Encoding(false, true), 3);
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                    return (new UnicodeEncoding(true, false, true), 2);
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                    return (new UnicodeEncoding(false, false, true), 2);
                return (new UTF8Encoding(false, true), 0);
            }
        }
    }
}
