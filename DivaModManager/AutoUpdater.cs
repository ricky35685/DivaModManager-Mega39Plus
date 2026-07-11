using System;
using System.Windows;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Onova;
using Onova.Services;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using DivaModManager.UI;
using Octokit;
using Onova.Models;
using System.Windows.Media.Imaging;

namespace DivaModManager
{
    public class AutoUpdater
    {
        private static ProgressBox progressBox;
        private static GitHubClient client = new GitHubClient(new ProductHeaderValue("DivaModManager"));
        private static HttpClient httpClient = new();

        public static async Task<bool> CheckForDMMUpdate(CancellationTokenSource cancellationToken)
        {
            // Get Version Number
            var localVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            if (!TryGetUpdateRepository(out var owner, out var repo))
            {
                Global.logger.WriteLine(
                    "This community build has no configured self-update repository.",
                    LoggerType.Info);
                return false;
            }

            try
            {
                Release release = await client.Repository.Release.GetLatest(owner, repo);
                Match onlineVersionMatch = Regex.Match(
                    release.TagName ?? String.Empty,
                    @"(?<!\d)(?<version>\d+(?:\.\d+){1,3})(?!\d)");
                string onlineVersion = onlineVersionMatch.Success
                    ? onlineVersionMatch.Groups["version"].Value
                    : null;
                if (UpdateAvailable(onlineVersion, localVersion))
                {
                    ChangelogBox notification = new ChangelogBox(release, "Diva Mod Manager", $"A new version of Diva Mod Manager is available (v{onlineVersion})!", null, false);
                    notification.ShowDialog();
                    notification.Activate();
                    if (notification.YesNo)
                    {
                        var releaseAsset = release.Assets.FirstOrDefault(asset =>
                                asset.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) &&
                                !asset.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase)) ??
                            release.Assets.FirstOrDefault(asset =>
                                asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                !asset.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase));
                        if (releaseAsset == null)
                        {
                            Global.logger.WriteLine(
                                "The release does not contain a supported Windows runtime ZIP.",
                                LoggerType.Error);
                            return false;
                        }

                        string downloadUrl = releaseAsset.BrowserDownloadUrl;
                        string fileName = releaseAsset.Name;
                        // Download the update
                        if (!await DownloadDMM(
                                downloadUrl,
                                fileName,
                                onlineVersion,
                                new Progress<DownloadProgress>(ReportUpdateProgress),
                                cancellationToken))
                        {
                            return false;
                        }
                        // Notify that the update is about to happen
                        MessageBox.Show($"Finished downloading {fileName}!\nDiva Mod Manager will now restart.", "Notification", MessageBoxButton.OK);
                        // Update DMM
                        UpdateManager updateManager = new UpdateManager(AssemblyMetadata.FromAssembly(Assembly.GetEntryAssembly(), Process.GetCurrentProcess().MainModule.FileName), 
                            new LocalPackageResolver($"{Global.assemblyLocation}{Global.s}Downloads{Global.s}DMMUpdate"), new ZipExtractor());
                        if (!Version.TryParse(onlineVersion, out Version version))
                        {
                            MessageBox.Show($"Error parsing {onlineVersion}!\nCancelling update.", "Notification", MessageBoxButton.OK);
                            return false;
                        }
                        // Updates and restarts DMM
                        await updateManager.PrepareUpdateAsync(version);
                        updateManager.LaunchUpdater(version);
                        return true;
                    }
                    else
                        Global.logger.WriteLine("Update for Diva Mod Manager cancelled.", LoggerType.Info);
                }
                else
                    Global.logger.WriteLine("No update for Diva Mod Manager available.", LoggerType.Info);
            }
            catch (Exception ex)
            {
                Global.logger.WriteLine(ex.Message, LoggerType.Error);
            }
            return false;
        }

        private static bool TryGetUpdateRepository(out string owner, out string repository)
        {
            var metadata = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(attribute =>
                    !String.IsNullOrWhiteSpace(attribute.Key) &&
                    !String.IsNullOrWhiteSpace(attribute.Value))
                .ToDictionary(
                    attribute => attribute.Key,
                    attribute => attribute.Value,
                    StringComparer.OrdinalIgnoreCase);
            metadata.TryGetValue("DmmUpdateOwner", out owner);
            metadata.TryGetValue("DmmUpdateRepository", out repository);
            return !String.IsNullOrWhiteSpace(owner) &&
                !String.IsNullOrWhiteSpace(repository);
        }
        private static async Task<bool> DownloadDMM(string uri, string fileName, string version, Progress<DownloadProgress> progress, CancellationTokenSource cancellationToken)
        {
            var updateDirectory = Path.Combine(Global.assemblyLocation, "Downloads", "DMMUpdate");
            var temporaryPath = Path.Combine(updateDirectory, $"{version}.download");
            var packagePath = Path.Combine(updateDirectory, $"{version}.zip");
            try
            {
                // Create the downloads folder if necessary
                Directory.CreateDirectory(updateDirectory);
                File.Delete(temporaryPath);
                progressBox = new ProgressBox(cancellationToken);
                progressBox.progressBar.Value = 0;
                progressBox.progressText.Text = $"Downloading {fileName}";
                progressBox.Title = "Diva Mod Manager Update Progress";
                progressBox.finished = false;
                progressBox.Show();
                progressBox.Activate();
                // Write and download the file
                using (var fs = new FileStream(temporaryPath, System.IO.FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await httpClient.DownloadAsync(uri, fs, fileName, progress, cancellationToken.Token);
                }
                File.Move(temporaryPath, packagePath, true);
                progressBox.Close();
                return true;
            }
            catch (OperationCanceledException)
            {
                // Remove the file is it will be a partially downloaded one and close up
                File.Delete(temporaryPath);
                if (progressBox != null)
                {
                    progressBox.finished = true;
                    progressBox.Close();
                }
                return false;
            }
            catch (Exception e)
            {
                File.Delete(temporaryPath);
                Console.WriteLine($"[ERROR] Error whilst downloading {fileName} {e.Message}");
                if (progressBox != null)
                {
                    progressBox.finished = true;
                    progressBox.Close();
                }
                return false;
            }
        }
        private static void ReportUpdateProgress(DownloadProgress progress)
        {
            if (progress.Percentage == 1)
            {
                progressBox.finished = true;
            }
            progressBox.progressBar.Value = progress.Percentage * 100;
            progressBox.taskBarItem.ProgressValue = progress.Percentage;
            progressBox.progressTitle.Text = $"Downloading {progress.FileName}...";
            progressBox.progressText.Text = $"{Math.Round(progress.Percentage * 100, 2)}% " +
                $"({StringConverters.FormatSize(progress.DownloadedBytes)} of {StringConverters.FormatSize(progress.TotalBytes)})";
        }
        private static bool UpdateAvailable(string onlineVersion, string localVersion)
        {
            if (onlineVersion is null || localVersion is null)
            {
                return false;
            }
            string[] onlineVersionParts = onlineVersion.Split('.');
            string[] localVersionParts = localVersion.Split('.');
            // Pad the version if one has more parts than another (e.g. 1.2.1 and 1.2)
            if (onlineVersionParts.Length > localVersionParts.Length)
            {
                for (int i = localVersionParts.Length; i < onlineVersionParts.Length; i++)
                {
                    localVersionParts = localVersionParts.Append("0").ToArray();
                }
            }
            else if (localVersionParts.Length > onlineVersionParts.Length)
            {
                for (int i = onlineVersionParts.Length; i < localVersionParts.Length; i++)
                {
                    onlineVersionParts = onlineVersionParts.Append("0").ToArray();
                }
            }
            // Decide whether the online version is new than local
            for (int i = 0; i < onlineVersionParts.Length; i++)
            {
                if (!int.TryParse(onlineVersionParts[i], out _))
                {
                    MessageBox.Show($"Couldn't parse {onlineVersion}");
                    return false;
                }
                if (!int.TryParse(localVersionParts[i], out _))
                {
                    MessageBox.Show($"Couldn't parse {localVersion}");
                    return false;
                }
                if (int.Parse(onlineVersionParts[i]) > int.Parse(localVersionParts[i]))
                {
                    return true;
                }
                else if (int.Parse(onlineVersionParts[i]) != int.Parse(localVersionParts[i]))
                {
                    return false;
                }
            }
            return false;
        }
    }
}
