using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SysBot.Base;
using SysBot.Pokemon.SV.BotRaid.Helpers;

namespace SysBot.Pokemon.WinForms.WebApi;

public class BotServer(Main mainForm, int port = 9090, int tcpPort = 9091) : IDisposable
{
    private HttpListener? _listener;
    private Thread? _listenerThread;
    private readonly int _port = port;
    private readonly int _tcpPort = tcpPort;
    private readonly Main _mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));
    private volatile bool _running;
    private readonly Lazy<string> _htmlTemplate = new(() => LoadEmbeddedResource("BotControlPanel.html"));
    private readonly string[] _machineNames = GetMachineNames();

    private static readonly TimeSpan ScanCacheFor = TimeSpan.FromSeconds(2);
    private readonly object _scanLock = new();
    private List<BotInstance> _remoteInstances = [];
    private DateTime _scannedAt = DateTime.MinValue;

    private static string LoadEmbeddedResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullResourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' not found.");

        using var stream = assembly.GetManifestResourceStream(fullResourceName)
            ?? throw new FileNotFoundException($"Could not load embedded resource '{fullResourceName}'");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] GetMachineNames()
    {
        var names = new List<string> { Environment.MachineName };
        try
        {
            names.Add(Dns.GetHostName());
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            names.Add(properties.HostName);
            if (!string.IsNullOrWhiteSpace(properties.DomainName))
                names.Add($"{properties.HostName}.{properties.DomainName}");
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Could not read this PC's host names: {ex.Message}", "WebServer");
        }
        names.AddRange(names.Select(n => $"{n}.local").ToList());
        return [.. names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public void Start()
    {
        if (_running) return;

        _listener = new HttpListener();
        try
        {
            try
            {
                _listener.Prefixes.Add($"http://+:{_port}/");
                _listener.Start();
                LogUtil.LogInfo($"Web server listening on all interfaces at port {_port}", "WebServer");
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                _listener.Close();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();

                LogUtil.LogError("Web server requires administrator privileges for network access. Currently limited to localhost only.", "WebServer");
                LogUtil.LogInfo("To enable network access, either:", "WebServer");
                LogUtil.LogInfo("1. Run this application as Administrator", "WebServer");
                LogUtil.LogInfo($"2. Or run this command as admin: netsh http add urlacl url=http://+:{_port}/ user=Everyone", "WebServer");
            }
        }
        catch
        {
            _listener.Close();
            _listener = null;
            throw;
        }

        _running = true;
        _listenerThread = new Thread(Listen)
        {
            IsBackground = true,
            Name = "BotWebServer"
        };
        _listenerThread.Start();
    }

    public void Stop()
    {
        if (!_running) return;

        try
        {
            _running = false;
            _listener?.Stop();
            _listenerThread?.Join(5000);
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error stopping web server: {ex.Message}", "WebServer");
        }
    }

    private void Listen()
    {
        var listener = _listener!;
        while (_running)
        {
            HttpListenerContext context;
            try
            {
                context = listener.GetContext();
            }
            catch (Exception ex) when (!_running || ex is ObjectDisposedException || ex is HttpListenerException { ErrorCode: 995 })
            {
                break;
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Error in listener: {ex.Message}", "WebServer");
                if (!listener.IsListening)
                    break;
                continue;
            }

            _ = Task.Run(() => HandleRequest(context));
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (!PanelRules.IsAllowedHost(request.Headers["Host"], _machineNames))
            {
                response.StatusCode = 400;
                return;
            }

            // The panel is served from this server, so it never needs CORS. Requiring a
            // JSON body on POST stops another web page from driving the bots with a plain
            // form or text/plain post, which a browser sends without asking first.
            var localPath = request.Url?.LocalPath ?? string.Empty;
            bool changesSomething = localPath.EndsWith("/command") || localPath is "/api/bot/command/all" or "/api/bot/update/all";
            if (changesSomething && request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                return;
            }

            string body = string.Empty;
            if (request.HttpMethod == "POST")
            {
                if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
                {
                    response.StatusCode = 415;
                    return;
                }
                var read = await PanelRules.ReadBodyAsync(request.InputStream, request.ContentLength64);
                if (read == null)
                {
                    response.StatusCode = 413;
                    return;
                }
                body = read;
            }

            string? responseString = localPath switch
            {
                "/" => _htmlTemplate.Value,
                "/api/bot/instances" => GetInstances(),
                var path when path.StartsWith("/api/bot/instances/") && path.EndsWith("/command") =>
                    RunCommand(body, ExtractPort(path)),
                "/api/bot/command/all" => RunAllCommand(body),
                "/api/bot/update/check" => await CheckForUpdates(),
                "/api/bot/update/idle-status" => GetIdleStatus(),
                "/api/bot/update/all" => await UpdateAllInstances(body),
                _ => null
            };

            if (responseString == null)
            {
                response.StatusCode = 404;
                responseString = "Not Found";
            }
            else
            {
                response.StatusCode = 200;
                response.ContentType = localPath == "/" ? "text/html" : "application/json";
            }

            var buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // The browser went away mid-response.
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error processing request: {ex.Message}", "WebServer");
            try
            {
                response.StatusCode = 500;
            }
            catch (InvalidOperationException)
            {
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    private async Task<string> UpdateAllInstances(string body)
    {
        try
        {
            var stage = "start";
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    var requestData = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
                    if (requestData?.TryGetValue("stage", out var requested) == true)
                        stage = requested;
                }
                catch (JsonException)
                {
                }
            }

            if (stage == "proceed")
            {
                var result = await UpdateManager.ProceedWithUpdatesAsync(_mainForm, _tcpPort);
                return JsonSerializer.Serialize(new
                {
                    Stage = "updating",
                    Success = result.UpdatesFailed == 0 && result.UpdatesNeeded > 0,
                    result.TotalInstances,
                    result.UpdatesNeeded,
                    result.UpdatesStarted,
                    result.UpdatesFailed,
                    Results = result.InstanceResults
                });
            }

            var started = await UpdateManager.StartUpdateProcessAsync(_mainForm, _tcpPort);
            return JsonSerializer.Serialize(new
            {
                Stage = "idling",
                Success = started.UpdatesFailed == 0,
                started.TotalInstances,
                started.UpdatesNeeded,
                Results = started.InstanceResults
            });
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    internal sealed record IdleBot(string Name, string Status);

    internal sealed record InstanceIdleStatus(int Port, int ProcessId, int TotalBots, int IdleBots, List<IdleBot> NonIdleBots, bool AllIdle)
    {
        public static InstanceIdleStatus From(int port, int processId, IReadOnlyCollection<BotInfo> bots)
        {
            var busy = bots.Where(b => !PanelRules.IsIdle(b.Status)).Select(b => new IdleBot(b.Name, b.Status)).ToList();
            return new InstanceIdleStatus(port, processId, bots.Count, bots.Count - busy.Count, busy, busy.Count == 0);
        }
    }

    private string GetIdleStatus()
    {
        try
        {
            // While an update waits, only the copies it will update count.
            var updating = UpdateManager.UpdatingProcessIds;
            bool counts(int processId) => updating == null || updating.Contains(processId);

            var instances = new List<InstanceIdleStatus>();
            if (counts(Environment.ProcessId))
                instances.Add(InstanceIdleStatus.From(_tcpPort, Environment.ProcessId, LocalBots()));

            foreach (var instance in ScanRemoteInstances().Where(i => i.IsOnline && counts(i.ProcessId)))
            {
                if (TryListBots(instance.Port, out var bots))
                    instances.Add(InstanceIdleStatus.From(instance.Port, instance.ProcessId, bots));
            }

            return JsonSerializer.Serialize(new
            {
                Instances = instances,
                TotalBots = instances.Sum(i => i.TotalBots),
                TotalIdleBots = instances.Sum(i => i.IdleBots),
                AllBotsIdle = instances.All(i => i.AllIdle)
            });
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(ex.Message);
        }
    }

    private static async Task<string> CheckForUpdates()
    {
        try
        {
            var latest = await UpdateChecker.GetLatestAsync();
            return JsonSerializer.Serialize(new
            {
                version = latest?.Tag ?? "Unknown",
                changelog = latest?.Body ?? "Failed to fetch the latest release information.",
                available = latest != null && UpdateRules.IsNewer(latest.Tag, SVRaidBot.Version)
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                version = "Unknown",
                changelog = "Unable to fetch update information",
                available = false,
                error = ex.Message
            });
        }
    }

    private static int ExtractPort(string path)
    {
        var parts = path.Split('/');
        return parts.Length > 4 && int.TryParse(parts[4], out var port) ? port : 0;
    }

    public static string InstanceName(ProgramConfig? config) =>
        string.IsNullOrEmpty(config?.Hub?.BotName) ? "SVRaidBot" : config.Hub.BotName;

    private List<BotInfo> LocalBots() => [.. _mainForm.BotSources().Select(BotInfo.From)];

    private string GetInstances()
    {
        var instances = new List<BotInstance> { CreateLocalInstance() };
        instances.AddRange(ScanRemoteInstances());
        return JsonSerializer.Serialize(new { Instances = instances });
    }

    private BotInstance CreateLocalInstance()
    {
        var config = _mainForm.Config;
        var bots = LocalBots();
        return new BotInstance
        {
            ProcessId = Environment.ProcessId,
            Name = InstanceName(config),
            Port = _tcpPort,
            Version = SVRaidBot.Version,
            Mode = config?.Mode.ToString() ?? "Unknown",
            BotCount = bots.Count,
            IsOnline = true,
            BotStatuses = [.. bots.Select(b => new BotStatusInfo { Name = b.Name, Status = b.Status })]
        };
    }

    // The panel and the update screens poll this every few seconds; asking every
    // copy over TCP each time is wasted work when nothing could have changed.
    private List<BotInstance> ScanRemoteInstances()
    {
        lock (_scanLock)
        {
            if (DateTime.UtcNow - _scannedAt < ScanCacheFor)
                return _remoteInstances;

            _remoteInstances = [.. InstanceDiscovery.FindOthers().Select(c => DescribeCopy(c.ProcessId, c.Port))];
            _scannedAt = DateTime.UtcNow;
            return _remoteInstances;
        }
    }

    private static BotInstance DescribeCopy(int processId, int port)
    {
        var instance = new BotInstance
        {
            ProcessId = processId,
            Name = "SVRaidBot",
            Port = port,
            Version = "Unknown",
            Mode = "Unknown",
        };

        var info = InstanceDiscovery.Query(port, "INFO");
        if (!info.StartsWith('{'))
            return instance;

        instance.IsOnline = true;
        try
        {
            using var doc = JsonDocument.Parse(info);
            var root = doc.RootElement;
            if (root.TryGetProperty("Version", out var version))
                instance.Version = version.GetString() ?? "Unknown";
            if (root.TryGetProperty("Mode", out var mode))
                instance.Mode = mode.GetString() ?? "Unknown";
            if (root.TryGetProperty("Name", out var name))
                instance.Name = name.GetString() ?? "SVRaidBot";
        }
        catch (JsonException)
        {
        }

        if (TryListBots(port, out var bots))
        {
            instance.BotCount = bots.Count;
            instance.BotStatuses = [.. bots.Select(b => new BotStatusInfo { Name = b.Name, Status = b.Status })];
        }
        return instance;
    }

    private static bool TryListBots(int port, out List<BotInfo> bots)
    {
        bots = [];
        var response = InstanceDiscovery.Query(port, "LISTBOTS");
        if (!response.StartsWith('{'))
            return false;
        try
        {
            bots = JsonSerializer.Deserialize<BotList>(response)?.Bots ?? [];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly HashSet<string> PanelCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "start", "stop", "idle", "resume", "restart", "reboot", "refreshmap", "screenon", "screenoff",
    };

    private bool IsKnownInstance(int port) => ScanRemoteInstances().Any(i => i.Port == port);

    private static BotCommandRequest? ReadCommand(string body)
    {
        try
        {
            var request = JsonSerializer.Deserialize<BotCommandRequest>(body);
            return request != null && PanelCommands.Contains(request.Command) ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string RunCommand(string body, int port)
    {
        var commandRequest = ReadCommand(body);
        if (commandRequest == null)
            return CreateErrorResponse("Invalid command request");

        if (port == _tcpPort)
            return JsonSerializer.Serialize(RunLocalCommand(commandRequest.Command));

        if (!IsKnownInstance(port))
            return CreateErrorResponse("Unknown instance");

        var result = InstanceDiscovery.Query(port, $"{commandRequest.Command}All".ToUpperInvariant());
        return JsonSerializer.Serialize(new CommandResponse
        {
            Success = !result.StartsWith("ERROR"),
            Message = result,
            Port = port,
            Command = commandRequest.Command,
        });
    }

    private string RunAllCommand(string body)
    {
        var commandRequest = ReadCommand(body);
        if (commandRequest == null)
            return CreateErrorResponse("Invalid command request");

        var local = RunLocalCommand(commandRequest.Command);
        local.InstanceName = InstanceName(_mainForm.Config);
        var results = new List<CommandResponse> { local };

        foreach (var instance in ScanRemoteInstances().Where(i => i.IsOnline))
        {
            var result = InstanceDiscovery.Query(instance.Port, $"{commandRequest.Command}All".ToUpperInvariant());
            results.Add(new CommandResponse
            {
                Success = !result.StartsWith("ERROR"),
                Message = result,
                Port = instance.Port,
                Command = commandRequest.Command,
                InstanceName = instance.Name
            });
        }

        return JsonSerializer.Serialize(new BatchCommandResponse
        {
            Results = results,
            TotalInstances = results.Count,
            SuccessfulCommands = results.Count(r => r.Success)
        });
    }

    private CommandResponse RunLocalCommand(string command)
    {
        var cmd = MapCommand(command);
        bool sent = cmd != BotControlCommand.None && _mainForm.RunOnUiThread(() => _mainForm.SendAll(cmd));
        return new CommandResponse
        {
            Success = sent,
            Message = sent ? $"Command {command} sent successfully" : $"Error: command {command} could not be sent",
            Port = _tcpPort,
            Command = command,
        };
    }

    private static BotControlCommand MapCommand(string webCommand)
    {
        return webCommand.ToLowerInvariant() switch
        {
            "start" => BotControlCommand.Start,
            "stop" => BotControlCommand.Stop,
            "idle" => BotControlCommand.Idle,
            "resume" => BotControlCommand.Resume,
            "restart" => BotControlCommand.Restart,
            "reboot" => BotControlCommand.RebootAndStop,
            "refreshmap" => BotControlCommand.RefreshMap,
            "screenon" => BotControlCommand.ScreenOnAll,
            "screenoff" => BotControlCommand.ScreenOffAll,
            _ => BotControlCommand.None
        };
    }

    private static string CreateErrorResponse(string message)
    {
        return JsonSerializer.Serialize(new CommandResponse
        {
            Success = false,
            Message = $"Error: {message}"
        });
    }

    public void Dispose()
    {
        Stop();
        _listener?.Close();
        GC.SuppressFinalize(this);
    }
}

public class BotInstance
{
    public int ProcessId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Version { get; set; } = string.Empty;
    public int BotCount { get; set; }
    public string Mode { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public List<BotStatusInfo>? BotStatuses { get; set; }
}

public class BotStatusInfo
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public record BotList(List<BotInfo> Bots);

public class BotInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RoutineType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ConnectionType { get; set; } = string.Empty;
    public string IP { get; set; } = string.Empty;
    public int Port { get; set; }

    public static BotInfo From(BotSource<PokeBotState> source)
    {
        var state = source.Bot.Config;
        var connection = state.Connection;
        return new BotInfo
        {
            Id = $"{connection.IP}:{connection.Port}",
            Name = connection.IP,
            RoutineType = state.InitialRoutine.ToString(),
            Status = BotStatus.Read(source),
            ConnectionType = connection.Protocol.ToString(),
            IP = connection.IP,
            Port = connection.Port
        };
    }
}

public class BotCommandRequest
{
    public string Command { get; set; } = string.Empty;
}

public class CommandResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Command { get; set; } = string.Empty;
    public string? InstanceName { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class BatchCommandResponse
{
    public List<CommandResponse> Results { get; set; } = [];
    public int TotalInstances { get; set; }
    public int SuccessfulCommands { get; set; }
}
