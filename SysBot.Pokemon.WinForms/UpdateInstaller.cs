using SysBot.Base;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SysBot.Pokemon.WinForms
{
    /// <summary>
    /// Downloads a release, hands it to the swap script and closes the program.
    /// Call it on the UI thread: it ends with Application.Exit.
    /// </summary>
    public static class UpdateInstaller
    {
        // The exe is around 70 MB; HttpClient's default 100 s limit cut slow connections off.
        private static readonly TimeSpan DownloadLimit = TimeSpan.FromMinutes(15);

        public static async Task InstallAsync(UpdateChecker.Release release)
        {
            if (string.IsNullOrWhiteSpace(release.DownloadUrl))
                throw new InvalidOperationException($"Release {release.Tag} has no program to download.");

            Main.IsUpdating = true;
            try
            {
                LogUtil.LogInfo($"Downloading {release.Tag}...", "Update");
                var downloadedPath = await DownloadAsync(release);

                string batchPath = UpdateScript.PathFor(Environment.ProcessId);
                File.WriteAllText(batchPath, UpdateScript.Build(Environment.ProcessId));
                using (Process.Start(UpdateScript.StartInfo(batchPath, Application.ExecutablePath, downloadedPath)))
                {
                }

                LogUtil.LogInfo($"Installing {release.Tag}; SVRaidBot will restart.", "Update");
                Application.Exit();
            }
            catch
            {
                Main.IsUpdating = false;
                throw;
            }
        }

        /// <summary>
        /// For updates nobody is watching (the web panel, another copy): failures go to the log.
        /// </summary>
        public static async Task RunAsync(UpdateChecker.Release release)
        {
            try
            {
                await InstallAsync(release);
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Update to {release.Tag} failed: {ex.Message}", "Update");
            }
        }

        private static async Task<string> DownloadAsync(UpdateChecker.Release release)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"SVRaidBot_{Guid.NewGuid()}.exe");
            try
            {
                using var cts = new CancellationTokenSource(DownloadLimit);
                using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                client.DefaultRequestHeaders.Add("User-Agent", "SVRaidBot");

                using (var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    response.EnsureSuccessStatusCode();
                    await using var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await response.Content.CopyToAsync(file, cts.Token);
                }

                if (string.IsNullOrWhiteSpace(release.Digest))
                    LogUtil.LogInfo($"Release {release.Tag} publishes no checksum; installing without one.", "Update");
                else if (!UpdateRules.MatchesDigest(tempPath, release.Digest))
                    throw new InvalidDataException($"The download does not match the checksum GitHub lists for {release.Tag}. Nothing was installed.");

                return tempPath;
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw new TimeoutException($"Downloading {release.Tag} took longer than {DownloadLimit.TotalMinutes:0} minutes.");
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
