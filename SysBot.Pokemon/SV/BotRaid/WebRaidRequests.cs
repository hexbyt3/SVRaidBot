using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SysBot.Pokemon.SV.BotRaid
{
    /// <summary>
    /// A raid a GenPKM member asked for on genpkm.com/raid-request.
    /// The lease proves to the site that this bot still holds it.
    /// </summary>
    public sealed class WebRaidRequest
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("lease")] public string Lease { get; set; } = string.Empty;
        [JsonPropertyName("seed")] public string Seed { get; set; } = string.Empty;
        [JsonPropertyName("stars")] public int Stars { get; set; }
        [JsonPropertyName("storyProgress")] public int StoryProgress { get; set; }
        [JsonPropertyName("species")] public string? Species { get; set; }
        [JsonPropertyName("isEvent")] public bool IsEvent { get; set; }
        [JsonPropertyName("requesterName")] public string RequesterName { get; set; } = string.Empty;

        [JsonIgnore] public object? Host { get; set; }
        [JsonIgnore] public string LastStatus { get; set; } = "claimed";
        [JsonIgnore] public bool Lost { get; set; }
        [JsonIgnore] public bool Finished { get; set; }
        [JsonIgnore] public (string Status, string Message)? OutcomeOverride { get; set; }

        public bool IsHosted => LastStatus is "lobby" or "battling";
    }

    public sealed record WebRaidBotInfo(string BotId, string BotName, string Version, string Game, string Map, IReadOnlyList<string> EventSpecies);

    public enum WebClaimResult
    {
        Claimed,
        NothingWaiting,
        NotApproved,
        Unreachable,
    }

    /// <summary>
    /// Talks to genpkm.com's raid request queue. Claims are made inline (the bot
    /// is about to pick its next raid and needs the answer), while progress
    /// reports go through one ordered background queue so a slow or offline
    /// site never stalls hosting, and the site always sees steps in order.
    /// </summary>
    public sealed class WebRaidRequestClient
    {
        private const string BaseUrl = "https://genpkm.com/api/v1/raid_requests/bot/";
        private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

        private readonly HttpClient _http;
        private readonly Action<string> _log;
        private readonly Channel<(WebRaidRequest Request, Update Body)> _reports = Channel.CreateUnbounded<(WebRaidRequest, Update)>();
        private int _pendingReports;

        public WebRaidRequestClient(Action<string> log, HttpClient? http = null)
        {
            _log = log;
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _ = Task.Run(SendReports);
        }

        public async Task<(WebClaimResult Result, WebRaidRequest? Request)> TryClaim(WebRaidBotInfo bot, CancellationToken token)
        {
            var body = new
            {
                botId = bot.BotId,
                botName = bot.BotName,
                version = bot.Version,
                game = bot.Game,
                map = bot.Map,
                eventSpecies = bot.EventSpecies,
            };

            try
            {
                using var response = await _http.PostAsJsonAsync(BaseUrl + "claim.php", body, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    return (WebClaimResult.NotApproved, null);
                if (!response.IsSuccessStatusCode)
                {
                    _log($"GenPKM raid requests: the site answered {(int)response.StatusCode} when asking for work.");
                    return (WebClaimResult.Unreachable, null);
                }

                // The site has already assigned the request by now, so the reply is read
                // even when hosting is stopping; otherwise the request would sit claimed
                // until its lease ran out. The client's own timeout still bounds it.
                var reply = await response.Content.ReadFromJsonAsync<ClaimReply>(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(reply?.Notice) && reply.Notice != _lastNotice)
                    _log($"GenPKM raid requests: {reply.Notice}");
                _lastNotice = reply?.Notice;
                if (reply?.Request is null)
                    return (WebClaimResult.NothingWaiting, null);
                if (token.IsCancellationRequested)
                {
                    Report(reply.Request, "released", "The raid bot stopped before it could host your raid. You are back in line for the next free bot.");
                    token.ThrowIfCancellationRequested();
                }
                return (WebClaimResult.Claimed, reply.Request);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
            {
                _log($"GenPKM raid requests: could not reach the site ({ex.Message}).");
                return (WebClaimResult.Unreachable, null);
            }
        }

        /// <summary>
        /// Queues a progress report. Terminal reports mark the request finished
        /// so nothing later is sent for it.
        /// </summary>
        public void Report(WebRaidRequest request, string status, string message = "", string? code = null)
        {
            if (request.Finished || request.Lost)
                return;
            if (status is "done" or "missed" or "failed" or "released" or "rejected")
                request.Finished = true;
            request.LastStatus = status;

            Interlocked.Increment(ref _pendingReports);
            _reports.Writer.TryWrite((request, new Update(request.Id, request.Lease, status, message, code)));
        }

        /// <summary>
        /// Sends a report and waits for the answer, after everything queued
        /// before it. Returns false when the site says the request is no longer
        /// ours, true when it accepted, and null when it could not be reached.
        /// </summary>
        public async Task<bool?> Confirm(WebRaidRequest request, string status, string message, CancellationToken token)
        {
            await Flush(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (request.Lost)
                return false;
            if (request.Finished)
                return null;

            try
            {
                using var response = await _http.PostAsJsonAsync(BaseUrl + "update.php",
                    new Update(request.Id, request.Lease, status, message, null), token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    request.Lost = true;
                    _log($"GenPKM raid request #{request.Id} is no longer ours (the site gave it back to the queue or ended it).");
                    return false;
                }
                if (!response.IsSuccessStatusCode)
                    return null;
                request.LastStatus = status;
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Waits for queued reports to go out.
        /// </summary>
        public async Task Flush(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _pendingReports) > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(100).ConfigureAwait(false);
        }

        private async Task SendReports()
        {
            await foreach (var (request, update) in _reports.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await SendWithRetry(request, update).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log($"GenPKM raid request #{request.Id}: the '{update.Status}' update failed ({ex.Message}).");
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingReports);
                }
            }
        }

        private async Task SendWithRetry(WebRaidRequest request, Update update)
        {
            if (request.Lost)
                return;

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var response = await _http.PostAsJsonAsync(BaseUrl + "update.php", update).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return;

                    if (response.StatusCode == HttpStatusCode.Conflict)
                    {
                        request.Lost = true;
                        _log($"GenPKM raid request #{request.Id} is no longer ours (the site gave it back to the queue or ended it).");
                        return;
                    }

                    if ((int)response.StatusCode is >= 400 and < 500)
                    {
                        _log($"GenPKM raid request #{request.Id}: the site refused the '{update.Status}' update ({(int)response.StatusCode}).");
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    if (attempt >= RetryDelays.Length)
                    {
                        _log($"GenPKM raid request #{request.Id}: could not send the '{update.Status}' update ({ex.Message}).");
                        return;
                    }
                }

                if (attempt >= RetryDelays.Length)
                {
                    _log($"GenPKM raid request #{request.Id}: the site kept failing the '{update.Status}' update, so it was dropped.");
                    return;
                }
                await Task.Delay(RetryDelays[attempt]).ConfigureAwait(false);
            }
        }

        private string? _lastNotice;

        private sealed class ClaimReply
        {
            [JsonPropertyName("request")] public WebRaidRequest? Request { get; set; }
            [JsonPropertyName("notice")] public string? Notice { get; set; }
        }

        private sealed record Update(
            [property: JsonPropertyName("id")] long Id,
            [property: JsonPropertyName("lease")] string Lease,
            [property: JsonPropertyName("status")] string Status,
            [property: JsonPropertyName("message")] string Message,
            [property: JsonPropertyName("code")] string? Code);
    }

    /// <summary>
    /// Compares Pokémon names by letters and digits only, ignoring case, so
    /// "Chien-Pao", "Chien Pao" and "ChienPao" are the same event species.
    /// </summary>
    public sealed class EventSpeciesComparer : IEqualityComparer<string>
    {
        public static readonly EventSpeciesComparer Instance = new();

        public bool Equals(string? x, string? y) => Key(x) == Key(y);

        public int GetHashCode(string obj) => Key(obj).GetHashCode(StringComparison.Ordinal);

        private static string Key(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            var buffer = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c))
                    buffer.Append(char.ToLowerInvariant(c));
            }
            return buffer.ToString();
        }
    }

    /// <summary>
    /// Rules shared by the claim path and its tests.
    /// </summary>
    public static class WebRaidRules
    {
        public static bool IsValidSeed(string? seed) =>
            seed is { Length: 8 } && uint.TryParse(seed, System.Globalization.NumberStyles.AllowHexSpecifier, null, out _);

        /// <summary>Same matrix as the Discord request command: story progress 3-6, 3★ minimum.</summary>
        public static bool StarsFitProgress(int stars, int storyProgress) => storyProgress switch
        {
            6 => stars is >= 3 and <= 7,
            5 => stars is >= 3 and <= 5,
            4 => stars is >= 3 and <= 4,
            3 => stars == 3,
            _ => false,
        };

        /// <summary>
        /// The id a bot claims under: the install's own id plus a short hash of the
        /// console address, so one config copied to a second Switch never collides.
        /// Also returns the install id to keep: a fresh one when the stored value is
        /// not a GUID (a hand-edited "1" used to throw on every claim).
        /// </summary>
        public static (string BotId, string InstallId) BotId(string? storedInstallId, string consoleAddress)
        {
            var install = Guid.TryParseExact(storedInstallId, "N", out _) ? storedInstallId! : Guid.NewGuid().ToString("N");
            // The console's address stays on this PC; only a short hash of it is sent.
            var console = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(consoleAddress)))[..12];
            return ($"{install[..16]}-{console}", install);
        }

        public static TeraCrystalType CrystalFor(int stars, bool isEvent) => stars switch
        {
            7 => TeraCrystalType.Might,
            6 => TeraCrystalType.Black,
            _ => isEvent ? TeraCrystalType.Distribution : TeraCrystalType.Base,
        };

        /// <summary>
        /// Compares a member-typed Pokémon name with the generated species,
        /// ignoring case, spaces and punctuation ("Mr. Mime" vs MrMime). A form
        /// word the member added ("Paldean Tauros") still matches.
        /// </summary>
        public static bool SameSpecies(string expected, string actual)
        {
            var want = Normalize(expected);
            var have = Normalize(actual);
            return have.Length > 0 && want.Contains(have, StringComparison.Ordinal);
        }

        private static string Normalize(string name)
        {
            var buffer = new System.Text.StringBuilder(name.Length);
            foreach (var c in name.Normalize(System.Text.NormalizationForm.FormD))
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                    continue;
                if (c == '♀')
                    buffer.Append('f');
                else if (c == '♂')
                    buffer.Append('m');
                else if (char.IsLetterOrDigit(c))
                    buffer.Append(char.ToLowerInvariant(c));
            }
            return buffer.ToString();
        }
    }
}
