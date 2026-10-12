using PKHeX.Core;
using System.Collections.Concurrent;

namespace SysBot.Pokemon
{
    /// <summary>
    /// Centralizes logic for Raid bot coordination.
    /// </summary>
    /// <typeparam name="T">Type of <see cref="PKM"/> to distribute.</typeparam>
    public class PokeRaidHub<T> where T : PKM, new()
    {
        public PokeRaidHub(PokeRaidHubConfig config)
        {
            Config = config;
        }

        public readonly PokeRaidHubConfig Config;

        /// <summary> Raid Bots only, used to delegate multi-player tasks </summary>
        public readonly ConcurrentPool<PokeRoutineExecutorBase> Bots = new();

        public bool RaidBotsReady => !Bots.All(z => z.Config.CurrentRoutineType == PokeRoutineType.Idle);
    }
}