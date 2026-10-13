using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SysBot.Base;
using SysBot.Pokemon.SV.BotRaid.Helpers;

namespace SysBot.Pokemon.WinForms.WebApi;

/// <summary>
/// "Update all" from the web panel: idle the copies that are behind, wait for the panel
/// to see them idle, then update the other copies and this one last.
/// </summary>
public static class UpdateManager
{
    public class UpdateAllResult
    {
        public int TotalInstances { get; set; }
        public int UpdatesNeeded { get; set; }
        public int UpdatesStarted { get; set; }
        public int UpdatesFailed { get; set; }
        public List<InstanceUpdateResult> InstanceResults { get; set; } = [];
    }

    public class InstanceUpdateResult
    {
        public int Port { get; set; }
        public int ProcessId { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public bool NeedsUpdate { get; set; }
        public bool UpdateStarted { get; set; }
        public string? Error { get; set; }
    }

    private sealed record CopyVersion(int ProcessId, int Port, string Version);

    private static readonly object UpdatingLock = new();
    private static HashSet<int>? _updating;
    private static DateTime _updatingSince;
    private static readonly TimeSpan UpdateWaitLimit = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The copies an update in progress is waiting on, or null when none is. Copies
    /// already on the latest version are not idled, so they must not hold the wait up.
    /// </summary>
    public static IReadOnlySet<int>? UpdatingProcessIds
    {
        get
        {
            // A panel closed mid-wait never sends "proceed"; don't filter forever.
            lock (UpdatingLock)
                return DateTime.UtcNow - _updatingSince < UpdateWaitLimit ? _updating : null;
        }
    }

    private static void SetUpdating(IEnumerable<int>? processIds)
    {
        lock (UpdatingLock)
        {
            _updating = processIds == null ? null : [.. processIds];
            _updatingSince = DateTime.UtcNow;
        }
    }

    public static async Task<UpdateAllResult> StartUpdateProcessAsync(Main mainForm, int currentPort)
    {
        var (latest, all, outdated) = await SurveyAsync(currentPort);
        if (latest == null)
            return FetchFailed();

        var result = new UpdateAllResult { TotalInstances = all.Count, UpdatesNeeded = outdated.Count };
        result.InstanceResults.AddRange(outdated.Select(c => Describe(c, latest)));
        if (outdated.Count == 0)
        {
            SetUpdating(null);
            return result;
        }

        SetUpdating(outdated.Select(c => c.ProcessId));
        LogUtil.LogInfo($"Idling the bots of {outdated.Count} copies before updating to {latest.Tag}...", "UpdateManager");
        foreach (var copy in outdated)
        {
            bool sent = copy.ProcessId == Environment.ProcessId
                ? mainForm.RunOnUiThread(() => mainForm.SendAll(BotControlCommand.Idle))
                : !InstanceDiscovery.Query(copy.Port, "IDLEALL").StartsWith("ERROR");
            if (!sent)
                LogUtil.LogError($"Failed to send idle command to port {copy.Port}", "UpdateManager");
        }
        return result;
    }

    public static async Task<UpdateAllResult> ProceedWithUpdatesAsync(Main mainForm, int currentPort)
    {
        try
        {
            var (latest, all, outdated) = await SurveyAsync(currentPort);
            if (latest == null)
                return FetchFailed();

            var result = new UpdateAllResult { TotalInstances = all.Count, UpdatesNeeded = outdated.Count };

            // This copy goes last: updating it closes the panel's server.
            foreach (var copy in outdated.OrderBy(c => c.ProcessId == Environment.ProcessId))
            {
                var instanceResult = Describe(copy, latest);
                instanceResult.Error = TriggerUpdate(mainForm, copy, latest);
                instanceResult.UpdateStarted = instanceResult.Error == null;
                if (instanceResult.UpdateStarted)
                    result.UpdatesStarted++;
                else
                    result.UpdatesFailed++;
                result.InstanceResults.Add(instanceResult);

                // Spreads the downloads out a little.
                if (instanceResult.UpdateStarted && copy.ProcessId != Environment.ProcessId)
                    await Task.Delay(5000);
            }
            return result;
        }
        finally
        {
            SetUpdating(null);
        }
    }

    private static string? TriggerUpdate(Main mainForm, CopyVersion copy, UpdateChecker.Release latest)
    {
        try
        {
            if (copy.ProcessId == Environment.ProcessId)
            {
                LogUtil.LogInfo("Updating this copy last.", "UpdateManager");
                return mainForm.RunOnUiThread(() => _ = UpdateInstaller.RunAsync(latest)) ? null : "This copy is closing";
            }

            LogUtil.LogInfo($"Triggering update for instance on port {copy.Port}...", "UpdateManager");
            var response = InstanceDiscovery.Query(copy.Port, UpdateRules.UpdateCommand(latest.Tag, latest.Digest, latest.AssetName));
            if (response.StartsWith("ERROR"))
                return $"Failed to start update: {response}";
            LogUtil.LogInfo($"Update triggered for instance on port {copy.Port}", "UpdateManager");
            return null;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error updating instance on port {copy.Port}: {ex.Message}", "UpdateManager");
            return ex.Message;
        }
    }

    private static async Task<(UpdateChecker.Release? Latest, List<CopyVersion> All, List<CopyVersion> Outdated)> SurveyAsync(int currentPort)
    {
        var latest = await UpdateChecker.GetLatestAsync();
        var all = GetAllInstances(currentPort);
        if (latest == null)
            return (null, all, []);

        // A plain != would "update" a copy running a newer build back down to the release.
        var outdated = all.Where(c => UpdateRules.IsNewer(latest.Tag, c.Version)).ToList();
        return (latest, all, outdated);
    }

    private static UpdateAllResult FetchFailed() => new()
    {
        UpdatesFailed = 1,
        InstanceResults = [new InstanceUpdateResult { Error = "Failed to fetch latest version" }]
    };

    private static InstanceUpdateResult Describe(CopyVersion copy, UpdateChecker.Release latest) => new()
    {
        Port = copy.Port,
        ProcessId = copy.ProcessId,
        CurrentVersion = copy.Version,
        LatestVersion = latest.Tag,
        NeedsUpdate = true
    };

    private static List<CopyVersion> GetAllInstances(int currentPort)
    {
        var instances = new List<CopyVersion> { new(Environment.ProcessId, currentPort, SVRaidBot.Version) };
        foreach (var copy in InstanceDiscovery.FindOthers())
        {
            var version = InstanceDiscovery.Query(copy.Port, "VERSION");
            if (version.StartsWith("ERROR"))
                LogUtil.LogError($"The copy on port {copy.Port} did not answer: {version}", "UpdateManager");
            else
                instances.Add(new CopyVersion(copy.ProcessId, copy.Port, version.Trim()));
        }
        return instances;
    }
}
