using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

public class UserRequestManager
{
    // Commands run on several threads and each makes its own manager, so the
    // counts are kept once for the program and written back under this lock.
    private static readonly object FileLock = new();
    private static Dictionary<ulong, UserRequestInfo>? userRequests;

    private static readonly string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "raidfilessv", "user_requests.json");

    private static Dictionary<ulong, UserRequestInfo> Requests
    {
        get
        {
            if (userRequests is not null)
                return userRequests;

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            userRequests = File.Exists(filePath)
                ? JsonConvert.DeserializeObject<Dictionary<ulong, UserRequestInfo>>(File.ReadAllText(filePath)) ?? []
                : [];
            return userRequests;
        }
    }

    private static void Save() => File.WriteAllText(filePath, JsonConvert.SerializeObject(Requests));

    /// <summary>
    /// Whether the user may request now. Does not count anything; call
    /// <see cref="RecordRequest"/> once the request is actually queued.
    /// </summary>
    public bool CanRequest(ulong userId, int limit, int cooldown, out TimeSpan remainingCooldown)
    {
        remainingCooldown = TimeSpan.Zero;
        if (limit <= 0)
            return true;

        lock (FileLock)
        {
            if (!Requests.TryGetValue(userId, out var info) || info.RequestCount < limit)
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
        if (limit <= 0)
            return;

        lock (FileLock)
        {
            if (!Requests.TryGetValue(userId, out var info))
            {
                info = new UserRequestInfo { UserId = userId };
                Requests.Add(userId, info);
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