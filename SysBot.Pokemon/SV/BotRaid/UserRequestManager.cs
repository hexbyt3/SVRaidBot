using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

public class UserRequestManager
{
    private Dictionary<ulong, UserRequestInfo> userRequests;
    private readonly string filePath;

    public UserRequestManager()
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory; // Or another method to determine the base directory
        var directoryPath = Path.Combine(baseDirectory, "raidfilessv");
        Directory.CreateDirectory(directoryPath);
        filePath = Path.Combine(directoryPath, "user_requests.json");
        lock (FileLock)
            Load();
    }

    private void Load()
    {
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            userRequests = JsonConvert.DeserializeObject<Dictionary<ulong, UserRequestInfo>>(json) ?? new Dictionary<ulong, UserRequestInfo>();
        }
        else
        {
            userRequests = new Dictionary<ulong, UserRequestInfo>();
            Save();
        }
    }

    public void Save()
    {
        var json = JsonConvert.SerializeObject(userRequests);
        File.WriteAllText(filePath, json);
    }

    // Commands run on several threads and each one reads and writes the same file.
    private static readonly object FileLock = new();

    /// <summary>
    /// Whether the user may request now. Does not count anything; call
    /// <see cref="RecordRequest"/> once the request is actually queued.
    /// </summary>
    public bool CanRequest(ulong userId, int limit, int cooldown, out TimeSpan remainingCooldown)
    {
        remainingCooldown = TimeSpan.Zero;
        lock (FileLock)
        {
            Load();
            if (!userRequests.TryGetValue(userId, out var info) || info.RequestCount < limit)
                return true;

            var timeSinceLimit = DateTime.UtcNow - info.RequestLimitTime;
            if (timeSinceLimit < TimeSpan.FromMinutes(cooldown))
            {
                remainingCooldown = TimeSpan.FromMinutes(cooldown) - timeSinceLimit;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Counts a request that made it into the queue.
    /// </summary>
    public void RecordRequest(ulong userId, int limit, int cooldown)
    {
        lock (FileLock)
        {
            Load();
            if (!userRequests.TryGetValue(userId, out var info))
            {
                info = new UserRequestInfo { UserId = userId };
                userRequests.Add(userId, info);
            }
            else if (info.RequestCount >= limit && DateTime.UtcNow - info.RequestLimitTime >= TimeSpan.FromMinutes(cooldown))
            {
                info.RequestCount = 0; // Reset count after cooldown
            }

            info.RequestCount++;
            if (info.RequestCount == limit)
                info.RequestLimitTime = DateTime.UtcNow;

            Save();
        }
    }
}

public class UserRequestInfo
{
    public ulong UserId { get; set; }
    public int RequestCount { get; set; }
    public DateTime RequestLimitTime { get; set; }
}