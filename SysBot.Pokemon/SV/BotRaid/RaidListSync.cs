using System.Collections.Generic;
using static SysBot.Pokemon.RotatingRaidSettingsSV;

namespace SysBot.Pokemon
{
    /// <summary>
    /// The raid list is shared by the hosting loop and Discord commands, which run
    /// on different threads. Every change to it, and every read that walks it from
    /// a command, goes through this lock. Commands never remove an entry outright
    /// while a bot is running, because the loop tracks the hosted raid by position:
    /// they mark it with <see cref="RotatingRaidParameters.PendingRemoval"/> and the
    /// loop removes it between raids.
    /// </summary>
    public static class RaidListSync
    {
        public static readonly object Gate = new();

        public static List<RotatingRaidParameters> Snapshot(List<RotatingRaidParameters> list)
        {
            lock (Gate)
                return [.. list];
        }

        /// <summary>
        /// Removes every raid marked for removal and keeps the rotation where it
        /// was. The next pick starts after <paramref name="currentIndex"/>, so it
        /// steps back past every raid removed at or before it.
        /// </summary>
        public static int RemovePending(List<RotatingRaidParameters> list, ref int currentIndex)
        {
            lock (Gate)
            {
                int shift = 0;
                for (int i = 0; i <= currentIndex && i < list.Count; i++)
                {
                    if (list[i].PendingRemoval)
                        shift++;
                }

                int removed = list.RemoveAll(p => p.PendingRemoval);
                if (removed > 0)
                {
                    currentIndex -= shift;
                    if (currentIndex < 0)
                        currentIndex = System.Math.Max(0, list.Count - 1);
                }
                return removed;
            }
        }
    }
}
