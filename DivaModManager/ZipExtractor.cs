using Onova.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SevenZipExtractor;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace DivaModManager
{
    public class ZipExtractor : IPackageExtractor
    {
        public Task ExtractPackageAsync(string sourceFilePath, string destDirPath,
            IProgress<double> progress = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (Path.GetExtension(sourceFilePath).Equals(".7z", StringComparison.InvariantCultureIgnoreCase))
                {
                    using (var archive = new ArchiveFile(sourceFilePath))
                    {
                        archive.Extract(destDirPath);
                    }
                }
                else
                {
                    using (Stream stream = File.OpenRead(sourceFilePath))
                    using (var reader = ReaderFactory.OpenReader(stream, ReaderOptions.ForExternalStream))
                    {
                        while (reader.MoveToNextEntry())
                        {
                            if (!reader.Entry.IsDirectory)
                            {
                                reader.WriteEntryToDirectory(destDirPath, new ExtractionOptions()
                                {
                                    ExtractFullPath = true,
                                    Overwrite = true
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Global.logger.WriteLine($"解压程序更新失败：{exception.Message}", LoggerType.Error);
                throw;
            }
            finally
            {
                File.Delete(sourceFilePath);
            }

            return Task.CompletedTask;
        }

    }
}
