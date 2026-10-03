using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
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
    private static int _tcpPort = 0;

    public static void InitWebServer(this Main mainForm)
    {
        _main = mainForm;

        try
        {
            if (IsPortInUse(WebPort))
            {
                _tcpPort = FindAvailablePort(9091);
                StartTcpOnly();

                // Start monitoring for master failure
                StartMasterMonitor();
                return;
            }

            // Try to add URL reservation for network access
            TryAddUrlReservation(WebPort);

            _tcpPort = FindAvailablePort(9091);
            StartFullServer();
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to initialize web server: {ex.Message}", "WebServer");
        }
    }

    private static void StartMasterMonitor()
    {
        _monitorCts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            var random = new Random();

            while (!_monitorCts.Token.IsCancellationRequested)
            {
                try
                {
                    // Check every 10-15 seconds (randomized to prevent race conditions)
                    await Task.Delay(10000 + random.Next(5000), _monitorCts.Token);

                    if (!IsPortInUse(WebPort))
                    {
                        LogUtil.LogInfo("Master web server is down. Attempting to take over...", "WebServer");

                        // Wait a random amount to reduce race conditions between multiple slaves
                        await Task.Delay(random.Next(1000, 3000));

                        // Double-check that no one else took over
                        if (!IsPortInUse(WebPort))
                        {
                            TryTakeOverAsMaster();
                            break; // Stop monitoring once we've taken over
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogUtil.LogError($"Error in master monitor: {ex.Message}", "WebServer");
                }
            }
        }, _monitorCts.Token);
    }

    private static void TryTakeOverAsMaster()
    {
        try
        {
            // Try to add URL reservation
            TryAddUrlReservation(WebPort);

            // Start the web server
            _server = new BotServer(_main!, WebPort, _tcpPort);
            _server.Start();

            // Stop the monitor since we're now the master
            _monitorCts?.Cancel();
            _monitorCts = null;

            LogUtil.LogInfo($"Successfully took over as master web server on port {WebPort}", "WebServer");
            LogUtil.LogInfo($"Web interface is now available at http://localhost:{WebPort}", "WebServer");

            // Show notification to user if possible
            if (_main != null)
            {
                _main.BeginInvoke((MethodInvoker)(() =>
                {
                    System.Windows.Forms.MessageBox.Show(
                        $"This instance has taken over as the master web server.\n\nWeb interface available at:\nhttp://localhost:{WebPort}",
                        "Master Server Takeover",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                }));
            }
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to take over as master: {ex.Message}", "WebServer");

            // If we failed, go back to monitoring
            StartMasterMonitor();
        }
    }

    private static bool TryAddUrlReservation(int port)
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
            return process?.ExitCode == 0;
        }
        catch
        {
            // If we can't add the reservation, the server will fall back to localhost
            return false;
        }
    }

    private static void StartTcpOnly()
    {
        CreatePortFile();
        StartTcp();
    }

    private static void StartFullServer()
    {
        CreatePortFile();
        _server = new BotServer(_main!, WebPort, _tcpPort);
        _server.Start();
        StartTcp();
    }

    private static void StartTcp()
    {
        _cts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            try
            {
                _tcp = new TcpListener(System.Net.IPAddress.Loopback, _tcpPort);
                _tcp.Start();

                while (!_cts.Token.IsCancellationRequested)
                {
                    var tcpTask = _tcp.AcceptTcpClientAsync();
                    var tcs = new TaskCompletionSource<bool>();

                    using (var registration = _cts.Token.Register(() => tcs.SetCanceled()))
                    {
                        var completedTask = await Task.WhenAny(tcpTask, tcs.Task);
                        if (completedTask == tcpTask && tcpTask.IsCompletedSuccessfully)
                        {
                            _ = Task.Run(() => HandleClient(tcpTask.Result));
                        }
                    }
                }
            }
            catch (Exception ex) when (!_cts.Token.IsCancellationRequested)
            {
                LogUtil.LogError($"TCP listener error: {ex.Message}", "TCP");
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
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var command = await reader.ReadLineAsync();
                if (!string.IsNullOrEmpty(command))
                {
                    var response = ProcessCommand(command);
                    await writer.WriteLineAsync(response);
                    await stream.FlushAsync();
                    await Task.Delay(100);
                }
            }
        }
        catch (Exception ex) when (!(ex is IOException { InnerException: SocketException }))
        {
            LogUtil.LogError($"Error handling TCP client: {ex.Message}", "TCP");
        }
    }

    private static string ProcessCommand(string command)
    {
        if (_main == null)
            return "ERROR: Main form not initialized";

        var parts = command.Split(':');
        var cmd = parts[0].ToUpperInvariant();
        var botId = parts.Length > 1 ? parts[1] : null;

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
            "STATUS" => GetBotStatuses(botId),
            "ISREADY" => CheckReady(),
            "INFO" => GetInstanceInfo(),
            "VERSION" => SVRaidBot.Version,
            "UPDATE" => TriggerUpdate(),
            _ => $"ERROR: Unknown command '{cmd}'"
        };
    }

    private static string TriggerUpdate()
    {
        try
        {
            if (_main == null)
                return "ERROR: Main form not initialized";

            _main.BeginInvoke((MethodInvoker)(async () =>
            {
                var (updateAvailable, _, newVersion) = await UpdateChecker.CheckForUpdatesAsync(false);
                if (updateAvailable)
                {
                    var updateForm = new UpdateForm(false, newVersion, true);
                    updateForm.PerformUpdate();
                }
            }));

            return "OK: Update triggered";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private static string ExecuteGlobalCommand(BotControlCommand command)
    {
        try
        {
            _main!.BeginInvoke((MethodInvoker)(() =>
            {
                var sendAllMethod = _main.GetType().GetMethod("SendAll",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sendAllMethod?.Invoke(_main, new object[] { command });
            }));

            return $"OK: {command} command sent to all bots";
        }
        catch (Exception ex)
        {
            return $"ERROR: Failed to execute {command} - {ex.Message}";
        }
    }

    private static string GetBotsList()
    {
        try
        {
            var botList = new List<object>();
            var config = GetConfig();
            var controllers = GetBotControllers();

            // If no controllers found in UI, try to get from Bots property
            if (controllers.Count == 0)
            {
                var botsProperty = _main!.GetType().GetProperty("Bots",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (botsProperty?.GetValue(_main) is List<PokeBotState> bots)
                {
                    foreach (var bot in bots)
                    {
                        botList.Add(new
                        {
                            Id = $"{bot.Connection.IP}:{bot.Connection.Port}",
                            Name = bot.Connection.IP,
                            RoutineType = bot.InitialRoutine.ToString(),
                            Status = "Unknown",
                            ConnectionType = bot.Connection.Protocol.ToString(),
                            bot.Connection.IP,
                            bot.Connection.Port
                        });
                    }

                    return System.Text.Json.JsonSerializer.Serialize(new { Bots = botList });
                }
            }

            // Use controllers if available
            foreach (var controller in controllers)
            {
                var state = controller.State;
                var botName = GetBotName(state, config);
                var status = controller.ReadBotState();

                botList.Add(new
                {
                    Id = $"{state.Connection.IP}:{state.Connection.Port}",
                    Name = botName,
                    RoutineType = state.InitialRoutine.ToString(),
                    Status = status,
                    ConnectionType = state.Connection.Protocol.ToString(),
                    state.Connection.IP,
                    state.Connection.Port
                });
            }

            return System.Text.Json.JsonSerializer.Serialize(new { Bots = botList });
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"GetBotsList error: {ex.Message}", "WebAPI");
            return $"ERROR: Failed to get bots list - {ex.Message}";
        }
    }

    private static string GetBotStatuses(string? botId)
    {
        try
        {
            var config = GetConfig();
            var controllers = GetBotControllers();

            if (string.IsNullOrEmpty(botId))
            {
                var statuses = controllers.Select(c => new
                {
                    Id = $"{c.State.Connection.IP}:{c.State.Connection.Port}",
                    Name = GetBotName(c.State, config),
                    Status = c.ReadBotState()
                }).ToList();

                return System.Text.Json.JsonSerializer.Serialize(statuses);
            }

            var botController = controllers.FirstOrDefault(c =>
                $"{c.State.Connection.IP}:{c.State.Connection.Port}" == botId);

            return botController?.ReadBotState() ?? "ERROR: Bot not found";
        }
        catch (Exception ex)
        {
            return $"ERROR: Failed to get status - {ex.Message}";
        }
    }

    private static string CheckReady()
    {
        try
        {
            var controllers = GetBotControllers();
            var hasRunningBots = controllers.Any(c => c.GetBot()?.IsRunning ?? false);
            return hasRunningBots ? "READY" : "NOT_READY";
        }
        catch
        {
            return "NOT_READY";
        }
    }

    private static string GetInstanceInfo()
    {
        try
        {
            var config = GetConfig();
            var version = GetVersion();
            var mode = config?.Mode.ToString() ?? "Unknown";
            var name = GetInstanceName(config, mode);

            var info = new
            {
                Version = version,
                Mode = mode,
                Name = name,
                Environment.ProcessId,
                Port = _tcpPort
            };

            return System.Text.Json.JsonSerializer.Serialize(info);
        }
        catch (Exception ex)
        {
            return $"ERROR: Failed to get instance info - {ex.Message}";
        }
    }

    private static List<BotController> GetBotControllers()
    {
        var flpBotsField = _main!.GetType().GetField("FLP_Bots",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (flpBotsField?.GetValue(_main) is FlowLayoutPanel flpBots)
        {
            return flpBots.Controls.OfType<BotController>().ToList();
        }

        return new List<BotController>();
    }

    private static ProgramConfig? GetConfig()
    {
        var configProp = _main?.GetType().GetProperty("Config",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return configProp?.GetValue(_main) as ProgramConfig;
    }

    private static string GetBotName(PokeBotState state, ProgramConfig? config)
    {
        // Always return IP address as the bot name
        return state.Connection.IP;
    }

    private static string GetVersion()
    {
        return SVRaidBot.Version;
    }

    private static string GetInstanceName(ProgramConfig? config, string mode)
    {
        if (!string.IsNullOrEmpty(config?.Hub?.BotName))
            return config.Hub.BotName;

        return mode switch
        {
            "LGPE" => "LGPE",
            "BDSP" => "BDSP",
            "SWSH" => "SWSH",
            "SV" => "SV",
            "LA" => "LA",
            _ => "SVRaidBot"
        };
    }

    private static void CreatePortFile()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var exeDir = Path.GetDirectoryName(exePath) ?? Program.WorkingDirectory;
            DeleteStalePortFiles(exeDir);
            var portFile = Path.Combine(exeDir, $"SVRaidBot_{Environment.ProcessId}.port");
            File.WriteAllText(portFile, _tcpPort.ToString());
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to create port file: {ex.Message}", "WebServer");
        }
    }

    private static void OnProcessExit(object? sender, EventArgs e) => CleanupPortFile();

    // A copy that crashed or was ended from Task Manager never removes its own file,
    // so every start clears the files of copies that are no longer running.
    private static void DeleteStalePortFiles(string exeDir)
    {
        var self = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var file in Directory.EnumerateFiles(exeDir, "SVRaidBot_*.port"))
        {
            try
            {
                var idText = Path.GetFileNameWithoutExtension(file)["SVRaidBot_".Length..];
                if (!int.TryParse(idText, out int pid) || pid == self.Id || IsLiveCopy(pid, self.ProcessName, File.GetLastWriteTime(file)))
                    continue;
                File.Delete(file);
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Could not remove old port file {Path.GetFileName(file)}: {ex.Message}", "WebServer");
            }
        }
    }

    // Windows reuses process ids, so the id must belong to this program and to a
    // process that was already running when the file was written.
    private static bool IsLiveCopy(int pid, string processName, DateTime written)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)
                && process.StartTime <= written.AddMinutes(1);
        }
        catch (ArgumentException)
        {
            return false; // no process with that id
        }
        catch
        {
            return true; // can't tell; leave it alone
        }
    }

    private static void CleanupPortFile()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var exeDir = Path.GetDirectoryName(exePath) ?? Program.WorkingDirectory;
            var portFile = Path.Combine(exeDir, $"SVRaidBot_{Environment.ProcessId}.port");

            if (File.Exists(portFile))
                File.Delete(portFile);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Failed to cleanup port file: {ex.Message}", "WebServer");
        }
    }

    private static int FindAvailablePort(int startPort)
    {
        for (int port = startPort; port < startPort + 100; port++)
        {
            if (!IsPortInUse(port))
                return port;
        }
        throw new InvalidOperationException("No available ports found");
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
            var response = client.GetAsync($"http://localhost:{port}/api/bot/instances").Result;
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Also check TCP
            try
            {
                using var tcpClient = new TcpClient();
                var result = tcpClient.BeginConnect("127.0.0.1", port, null, null);
                var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200));
                if (success)
                {
                    tcpClient.EndConnect(result);
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void StopWebServer(this Main mainForm)
    {
        try
        {
            _cts?.Cancel();
            _tcp?.Stop();
            _server?.Dispose();
            CleanupPortFile();
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error stopping web server: {ex.Message}", "WebServer");
        }
    }
}