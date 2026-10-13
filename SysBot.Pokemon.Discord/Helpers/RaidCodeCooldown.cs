using System;
using System.Collections.Concurrent;

namespace SysBot.Pokemon.Discord.Helpers;

/// <summary>
/// One raid-code DM per user per raid message every <see cref="Window"/>, so toggling ✅ can't spam DMs.
/// </summary>
public sealed class RaidCodeCooldown
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
    private const int PruneAbove = 1000;

    private readonly ConcurrentDictionary<(ulong MessageId, ulong UserId), DateTime> _lastSent = new();

    public bool TryStart(ulong messageId, ulong userId, DateTime now)
    {
        if (_lastSent.Count > PruneAbove)
            Prune(now);

        var key = (messageId, userId);
        while (true)
        {
            if (!_lastSent.TryGetValue(key, out var last))
            {
                if (_lastSent.TryAdd(key, now))
                    return true;
                continue;
            }

            if (now - last < Window)
                return false;
            if (_lastSent.TryUpdate(key, now, last))
                return true;
        }
    }

    private void Prune(DateTime now)
    {
        foreach (var entry in _lastSent)
        {
            if (now - entry.Value >= Window)
                _lastSent.TryRemove(entry);
        }
    }
}
