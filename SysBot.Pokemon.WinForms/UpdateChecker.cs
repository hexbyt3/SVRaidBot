using Newtonsoft.Json;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SysBot.Pokemon.WinForms
{
    public static class UpdateChecker
    {
        private const string RepositoryOwner = "hexbyt3";
        private const string RepositoryName = "SVRaidBot";

        public sealed record Release(string Tag, string? Body, string? AssetName, string? DownloadUrl, string? Digest)
        {
            public static Release ForTag(string tag, string? digest, string assetName) =>
                new(tag, null, assetName, $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}", digest);
        }

        // GitHub allows 60 unauthenticated API calls an hour per IP, shared by every copy
        // on the PC. One list call answers both "what is latest" and "is it required", and
        // the web panel's update screens reuse it for a few minutes.
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
        private static readonly SemaphoreSlim FetchLock = new(1, 1);
        private static List<ReleaseInfo>? _releases;
        private static DateTime _fetchedAt = DateTime.MinValue;

        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("User-Agent", "SVRaidBot");
            return client;
        }

        public static async Task<(bool UpdateAvailable, bool UpdateRequired, string NewVersion)> CheckForUpdatesAsync(bool forceShow = false)
        {
            var releases = await GetReleasesAsync(refresh: forceShow);
            var latest = Latest(releases);
            if (releases == null || latest == null)
            {
                if (forceShow)
                {
                    MessageBox.Show("Failed to fetch release information. Please check your internet connection.",
                        "Update Check Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return (false, false, string.Empty);
            }

            bool updateAvailable = UpdateRules.IsNewer(latest.TagName, SVRaidBot.Version);
            bool updateRequired = updateAvailable && UpdateRules.IsRequired(releases.Select(r => (r.TagName, r.Body, r.Prerelease)), SVRaidBot.Version);
            string newVersion = latest.TagName ?? string.Empty;

            if (forceShow)
            {
                using UpdateForm updateForm = new(updateRequired, newVersion, updateAvailable);
                updateForm.ShowDialog();
            }

            return (updateAvailable, updateRequired, newVersion);
        }

        public static async Task<Release?> GetLatestAsync(bool refresh = false)
        {
            var latest = Latest(await GetReleasesAsync(refresh));
            if (string.IsNullOrWhiteSpace(latest?.TagName))
                return null;

            var asset = latest.Assets?.FirstOrDefault(a => a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
            return new Release(latest.TagName, latest.Body, asset?.Name, asset?.BrowserDownloadUrl, asset?.Digest);
        }

        public static async Task<string> FetchChangelogAsync()
        {
            var latest = await GetLatestAsync();
            return latest?.Body ?? "Failed to fetch the latest release information.";
        }

        // Same pick as GitHub's releases/latest: the newest published release that is not a prerelease.
        private static ReleaseInfo? Latest(List<ReleaseInfo>? releases) =>
            releases?.Where(r => !r.Prerelease && !r.Draft).OrderByDescending(r => r.CreatedAt).FirstOrDefault();

        private static async Task<List<ReleaseInfo>?> GetReleasesAsync(bool refresh)
        {
            await FetchLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!refresh && _releases != null && DateTime.UtcNow - _fetchedAt < CacheFor)
                    return _releases;

                var url = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases?per_page=50";
                using var response = await Client.GetAsync(url).ConfigureAwait(false);
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"GitHub API Error: {response.StatusCode} - {content}");
                    return _releases;
                }

                _releases = JsonConvert.DeserializeObject<List<ReleaseInfo>>(content) ?? [];
                _fetchedAt = DateTime.UtcNow;
                return _releases;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching releases: {ex.Message}");
                return _releases;
            }
            finally
            {
                FetchLock.Release();
            }
        }

        private sealed class ReleaseInfo
        {
            [JsonProperty("tag_name")]
            public string? TagName { get; set; }

            [JsonProperty("prerelease")]
            public bool Prerelease { get; set; }

            [JsonProperty("draft")]
            public bool Draft { get; set; }

            [JsonProperty("created_at")]
            public DateTime? CreatedAt { get; set; }

            [JsonProperty("assets")]
            public List<AssetInfo>? Assets { get; set; }

            [JsonProperty("body")]
            public string? Body { get; set; }
        }

        private sealed class AssetInfo
        {
            [JsonProperty("name")]
            public string? Name { get; set; }

            [JsonProperty("browser_download_url")]
            public string? BrowserDownloadUrl { get; set; }

            [JsonProperty("digest")]
            public string? Digest { get; set; }
        }
    }
}
