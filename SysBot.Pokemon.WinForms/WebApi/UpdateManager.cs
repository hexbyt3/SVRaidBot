using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysBot.Base;
using SysBot.Pokemon.SV.BotRaid.Helpers;

namespace SysBot.Pokemon.WinForms.WebApi;

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

    public static async Task<UpdateAllResult> StartUpdateProcessAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();

        var (updateAvailable, _, latestVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
        if (string.IsNullOrEmpty(latestVersion))
        {
            result.UpdatesFailed = 1;
            result.InstanceResults.Add(new InstanceUpdateResult
            {
                Error = "Failed to fetch latest version"
            });
            return result;
        }

        var instances = GetAllInstances(currentPort);
        result.TotalInstances = instances.Count;

        var instancesNeedingUpdate = new List<(int ProcessId, int Port, string Version)>();

        foreach (var instance in instances)
        {
            if (instance.Version != latestVersion)
            {
                instancesNeedingUpdate.Add(instance);
                result.UpdatesNeeded++;
                result.InstanceResults.Add(new InstanceUpdateResult
                {
                    Port = instance.Port,
                    ProcessId = instance.ProcessId,
                    CurrentVersion = instance.Version,
                    LatestVersion = latestVersion,
                    NeedsUpdate = true
                });
            }
        }

        if (instancesNeedingUpdate.Count == 0)
        {
            return result;
        }

        // Start idling all bots
        LogUtil.LogInfo($"Idling all bots across {instancesNeedingUpdate.Count} instances before updates...", "UpdateManager");

        foreach (var (processId, port, version) in instancesNeedingUpdate)
        {
            if (processId == Environment.ProcessId)
            {
                mainForm.RunOnUiThread(() => mainForm.SendAll(BotControlCommand.Idle));
            }
            else
            {
                var idleResponse = InstanceDiscovery.Query(port, "IDLEALL");
                if (idleResponse.StartsWith("ERROR"))
                {
                    LogUtil.LogError($"Failed to send idle command to port {port}", "UpdateManager");
                }
            }
        }

        // Return immediately after starting the idle process
        return result;
    }

    public static async Task<UpdateAllResult> ProceedWithUpdatesAsync(Main mainForm, int currentPort)
    {
        var result = new UpdateAllResult();

        var (updateAvailable, _, latestVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
        var instances = GetAllInstances(currentPort);
        var instancesNeedingUpdate = instances.Where(i => i.Version != latestVersion).ToList();

        result.TotalInstances = instances.Count;
        result.UpdatesNeeded = instancesNeedingUpdate.Count;

        var sortedInstances = instancesNeedingUpdate
            .Where(i => i.ProcessId != Environment.ProcessId)
            .Concat(instancesNeedingUpdate.Where(i => i.ProcessId == Environment.ProcessId))
            .ToList();

        foreach (var (processId, port, currentVersion) in sortedInstances)
        {
            var instanceResult = new InstanceUpdateResult
            {
                Port = port,
                ProcessId = processId,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                NeedsUpdate = true
            };

            try
            {
                if (processId == Environment.ProcessId)
                {
                    var updateForm = new UpdateForm(false, latestVersion, true);
                    mainForm.BeginInvoke((MethodInvoker)(() =>
                    {
                        updateForm.PerformUpdate();
                    }));

                    instanceResult.UpdateStarted = true;
                    result.UpdatesStarted++;
                    LogUtil.LogInfo("Master instance update triggered", "UpdateManager");
                }
                else
                {
                    LogUtil.LogInfo($"Triggering update for instance on port {port}...", "UpdateManager");
                    var updateResponse = InstanceDiscovery.Query(port, "UPDATE");
                    if (!updateResponse.StartsWith("ERROR"))
                    {
                        instanceResult.UpdateStarted = true;
                        result.UpdatesStarted++;
                        LogUtil.LogInfo($"Update triggered for instance on port {port}", "UpdateManager");
                        await Task.Delay(5000);
                    }
                    else
                    {
                        instanceResult.Error = "Failed to start update";
                        result.UpdatesFailed++;
                    }
                }
            }
            catch (Exception ex)
            {
                instanceResult.Error = ex.Message;
                result.UpdatesFailed++;
                LogUtil.LogError($"Error updating instance on port {port}: {ex.Message}", "UpdateManager");
            }

            result.InstanceResults.Add(instanceResult);
        }

        return result;
    }

    private static List<(int ProcessId, int Port, string Version)> GetAllInstances(int currentPort)
    {
        var instances = new List<(int, int, string)> { (Environment.ProcessId, currentPort, SVRaidBot.Version) };
        foreach (var copy in InstanceDiscovery.FindOthers())
        {
            var version = InstanceDiscovery.Query(copy.Port, "VERSION");
            if (!version.StartsWith("ERROR"))
                instances.Add((copy.ProcessId, copy.Port, version.Trim()));
        }
        return instances;
    }
}
