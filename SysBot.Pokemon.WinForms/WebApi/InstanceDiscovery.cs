using SysBot.Base;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SysBot.Pokemon.WinForms.WebApi;

public readonly record struct OtherCopy(int ProcessId, int Port);

/// <summary>
/// Finds the other copies of the program running on this PC. Each copy writes
/// SVRaidBot_&lt;pid&gt;.port, holding its control port, both next to its exe (where
/// older versions look) and in a shared folder under %LOCALAPPDATA%. The shared folder
/// also finds copies that run elevated or under another exe name, whose folder cannot
/// be read from their process.
/// </summary>
public static class InstanceDiscovery
{
    private const string Prefix = "SVRaidBot_";
    private const string Extension = ".port";
    private static readonly string[] ProcessNames = ["SVRaidBot", "SysBot.Pokemon.WinForms", "SysBot"];
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);

    private static readonly object CacheLock = new();
    private static List<OtherCopy> _cached = [];
    private static DateTime _cachedAt = DateTime.MinValue;

    public static string SharedFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVRaidBot", "instances");

    private static string ExeFolder => Path.GetDirectoryName(Environment.ProcessPath) ?? Program.WorkingDirectory;

    private static string FileName(int processId) => $"{Prefix}{processId}{Extension}";

    public static void Register(int port)
    {
        foreach (var folder in new[] { ExeFolder, SharedFolder })
        {
            try
            {
                Directory.CreateDirectory(folder);
                DeleteStaleFiles(folder);
                File.WriteAllText(Path.Combine(folder, FileName(Environment.ProcessId)), port.ToString());
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Could not write the port file in {folder}: {ex.Message}", "WebServer");
            }
        }
    }

    public static void Unregister()
    {
        foreach (var folder in new[] { ExeFolder, SharedFolder })
        {
            try
            {
                File.Delete(Path.Combine(folder, FileName(Environment.ProcessId)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogUtil.LogError($"Could not remove the port file in {folder}: {ex.Message}", "WebServer");
            }
        }
    }

    public static IReadOnlyList<OtherCopy> FindOthers()
    {
        lock (CacheLock)
        {
            if (DateTime.UtcNow - _cachedAt < CacheFor)
                return _cached;

            var found = new Dictionary<int, int>();
            ReadSharedFolder(found);
            ReadLegacyFolders(found);

            _cached = [.. found.Select(f => new OtherCopy(f.Key, f.Value)).OrderBy(c => c.Port)];
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    private static void ReadSharedFolder(Dictionary<int, int> found)
    {
        if (!Directory.Exists(SharedFolder))
            return;

        foreach (var file in Directory.EnumerateFiles(SharedFolder, $"{Prefix}*{Extension}"))
        {
            if (!TryGetProcessId(file, out int pid) || pid == Environment.ProcessId)
                continue;
            if (!IsLiveCopy(pid, File.GetLastWriteTime(file), requireOurName: false))
            {
                TryDelete(file);
                continue;
            }
            if (TryReadPort(file, out int port))
                found[pid] = port;
        }
    }

    // Copies older than the shared folder only write next to their own exe.
    private static void ReadLegacyFolders(Dictionary<int, int> found)
    {
        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Could not list {name} processes: {ex.Message}", "WebServer");
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId || found.ContainsKey(process.Id))
                        continue;
                    try
                    {
                        // Throws for a copy running elevated; those are found through the shared folder.
                        var exePath = process.MainModule?.FileName;
                        var folder = Path.GetDirectoryName(exePath);
                        if (folder != null && TryReadPort(Path.Combine(folder, FileName(process.Id)), out int port))
                            found[process.Id] = port;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
    }

    // A copy that crashed or was ended from Task Manager never removes its own file.
    private static void DeleteStaleFiles(string folder)
    {
        bool requireOurName = folder != SharedFolder;
        foreach (var file in Directory.EnumerateFiles(folder, $"{Prefix}*{Extension}"))
        {
            if (TryGetProcessId(file, out int pid) && pid != Environment.ProcessId
                && !IsLiveCopy(pid, File.GetLastWriteTime(file), requireOurName))
                TryDelete(file);
        }
    }

    // Windows reuses process ids, so the id must belong to a process that was already
    // running when the file was written. Next to the exe it must also be this program;
    // in the shared folder a renamed copy counts too.
    private static bool IsLiveCopy(int pid, DateTime written, bool requireOurName)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (requireOurName && !ProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                return false;
            return process.StartTime <= written.AddMinutes(1);
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

    private static bool TryGetProcessId(string file, out int pid)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        return int.TryParse(name.AsSpan(Prefix.Length), out pid);
    }

    private static bool TryReadPort(string file, out int port)
    {
        port = 0;
        try
        {
            return File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out port) && port is > 0 and <= IPEndPoint.MaxPort;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUtil.LogError($"Could not remove old port file {Path.GetFileName(file)}: {ex.Message}", "WebServer");
        }
    }

    public static bool IsListening(int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends one command to another copy's control port. Any failure comes back as
    /// "ERROR: ..." so callers never mistake it for an answer.
    /// </summary>
    public static string Query(int port, string command)
    {
        try
        {
            using var client = new TcpClient { ReceiveTimeout = 5000, SendTimeout = 5000 };
            if (!client.ConnectAsync(IPAddress.Loopback, port).Wait(TimeSpan.FromSeconds(2)))
                return "ERROR: could not connect";

            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8);

            writer.WriteLine(command);
            return reader.ReadLine() ?? "ERROR: no response";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.GetBaseException().Message}";
        }
    }
}
