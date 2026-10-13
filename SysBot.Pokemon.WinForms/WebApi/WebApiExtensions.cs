using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysBot.Base;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using SysBot.Pokemon.WinForms.WebApi;

namespace SysBot.Pokemon.WinForms;

public static class WebApiExtensions
{
    private static BotServer? _server;
    private static TcpListener? _tcp;
    private static CancellationTokenSource? _cts;
    private static CancellationTokenSource? _monitorCts;
    private static Main? _main;

    private const int WebPort = 9090;
    private static int _tcpPort;

    public static void InitWebServer(this Main mainForm)
    {
        _main = mainForm;

        try
        {
            StartTcp();
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Could not open the control port for the web panel: {ex.Message}", "WebServer");
            return;
        }

        // The port file is written only once the port is really ours.
        InstanceDiscovery.Register(_tcpPort);
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // Another copy already serves the panel, or 9090 belongs to something else; either
        // way this copy stays reachable through it and takes over if 9090 frees up.
        if (InstanceDiscovery.IsListening(WebPort, 500) || !TryStartWebServer())
            StartMasterMonitor();
    }

    private static void OnProcessExit(object? sender, EventArgs e) => InstanceDiscovery.Unregister();

    private static bool TryStartWebServer()
    {
        TryAddUrlReservation(WebPort);
        var server = new BotServer(_main!, WebPort, _tcpPort);
        try
        {
            server.Start();
            _server = server;
            return true;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Could not start the web panel on port {WebPort}: {ex.Message}", "WebServer");
            server.Dispose();
            return false;
        }
    }

    private static void StartMasterMonitor()
    {
        var cts = new CancellationTokenSource();
        _monitorCts = cts;

        Task.Run(async () =>
        {
            var random = new Random();
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    // Randomized so several copies don't all try to take over at once.
                    await Task.Delay(10_000 + random.Next(5_000), cts.Token);
                    if (InstanceDiscovery.IsListening(WebPort, 500))
                        continue;

                    LogUtil.LogInfo("The web panel on port 9090 is down. Trying to take it over...", "WebServer");
                    await Task.Delay(random.Next(1_000, 3_000), cts.Token);
                    if (InstanceDiscovery.IsListening(WebPort, 500) || !TryStartWebServer())
                        continue;

                    LogUtil.LogInfo($"This copy now serves the web panel at http://localhost:{WebPort}", "WebServer");
                    _main?.RunOnUiThread(() => MessageBox.Show(
                        $"This instance has taken over as the master web server.\n\nWeb interface available at:\nhttp://localhost:{WebPort}",
                        "Master Server Takeover", MessageBoxButtons.OK, MessageBoxIcon.Information));
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Error in master monitor: {ex.Message}", "WebServer");
                }
            }
        });
    }

    private static void TryAddUrlReservation(int port)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"http add urlacl url=http://+:{port}/ user=Everyone",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            process?.WaitForExit(5000);
        }
        catch
        {
            // Without the reservation the server falls back to localhost.
        }
    }

    // Binding to port 0 lets Windows hand out a free port, so two copies starting
    // together can no longer both pick the same "free" one.
    private static void StartTcp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _tcp = listener;
        _tcpPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var cts = new CancellationTokenSource();
        _cts = cts;
        Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(() => HandleClient(client));
                }
                catch (Exception) when (cts.Token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"TCP listener error: {ex.Message}", "TCP");
                }
            }
        });
    }

    private static async Task HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                var command = await reader.ReadLineAsync();
                if (!string.IsNullOrEmpty(command))
                {
                    await writer.WriteLineAsync(ProcessCommand(command));
                    // Gives the other copy time to read the answer before the socket closes.
                    await Task.Delay(100);
                }
            }
        }
        catch (Exception ex) when (ex is not IOException { InnerException: SocketException })
        {
            LogUtil.LogError($"Error handling TCP client: {ex.Message}", "TCP");
        }
    }

    private static string ProcessCommand(string command)
    {
        if (_main == null)
            return "ERROR: Main form not initialized";

        var cmd = command.Split(':')[0].ToUpperInvariant();
        return cmd switch
        {
            "STARTALL" => ExecuteGlobalCommand(BotControlCommand.Start),
            "STOPALL" => ExecuteGlobalCommand(BotControlCommand.Stop),
            "IDLEALL" => ExecuteGlobalCommand(BotControlCommand.Idle),
            "RESUMEALL" => ExecuteGlobalCommand(BotControlCommand.Resume),
            "RESTARTALL" => ExecuteGlobalCommand(BotControlCommand.Restart),
            "REBOOTALL" => ExecuteGlobalCommand(BotControlCommand.RebootAndStop),
            "REFRESHMAPALL" => ExecuteGlobalCommand(BotControlCommand.RefreshMap),
            "SCREENONALL" => ExecuteGlobalCommand(BotControlCommand.ScreenOnAll),
            "SCREENOFFALL" => ExecuteGlobalCommand(BotControlCommand.ScreenOffAll),
            "LISTBOTS" => GetBotsList(),
            "INFO" => GetInstanceInfo(),
            "VERSION" => SVRaidBot.Version,
            "UPDATE" => TriggerUpdate(),
            _ => $"ERROR: Unknown command '{cmd}'"
        };
    }

    private static string TriggerUpdate()
    {
        var main = _main!;
        bool queued = main.RunOnUiThread(async () =>
        {
            try
            {
                var (updateAvailable, _, newVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
                if (updateAvailable)
                {
                    var updateForm = new UpdateForm(false, newVersion, true);
                    updateForm.PerformUpdate();
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Update failed: {ex.Message}", "Update");
            }
        });
        return queued ? "OK: Update triggered" : "ERROR: Window is closing";
    }

    private static string ExecuteGlobalCommand(BotControlCommand command)
    {
        var main = _main!;
        return main.RunOnUiThread(() => main.SendAll(command))
            ? $"OK: {command} command sent to all bots"
            : $"ERROR: Failed to execute {command} - the window is closing";
    }

    private static string GetBotsList()
    {
        try
        {
            var bots = _main!.BotSources().Select(BotInfo.From).ToList();
            return JsonSerializer.Serialize(new BotList(bots));
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"GetBotsList error: {ex.Message}", "WebAPI");
            return $"ERROR: Failed to get bots list - {ex.Message}";
        }
    }

    private static string GetInstanceInfo()
    {
        try
        {
            var config = _main!.Config;
            return JsonSerializer.Serialize(new
            {
                Version = SVRaidBot.Version,
                Mode = config?.Mode.ToString() ?? "Unknown",
                Name = BotServer.InstanceName(config),
                Environment.ProcessId,
                Port = _tcpPort
            });
        }
        catch (Exception ex)
        {
            return $"ERROR: Failed to get instance info - {ex.Message}";
        }
    }

    public static void StopWebServer(this Main mainForm)
    {
        try
        {
            _monitorCts?.Cancel();
            _cts?.Cancel();
            _tcp?.Stop();
            _server?.Dispose();
            InstanceDiscovery.Unregister();
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error stopping web server: {ex.Message}", "WebServer");
        }
    }
}
