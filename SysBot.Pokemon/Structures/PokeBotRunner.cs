using PKHeX.Core;
using System;
using SysBot.Base;
using System.Collections.Generic;
using System.Threading;

namespace SysBot.Pokemon
{
    public interface IPokeBotRunner
    {
        PokeRaidHubConfig Config { get; }
        bool RunOnce { get; }
        bool IsRunning { get; }
        IList<BotSource<PokeBotState>> Bots { get; }
        void StartAll();

        void StopAll();

        void InitializeStart();

        void Add(PokeRoutineExecutorBase newbot);

        void Remove(IConsoleBotConfig state, bool callStop);

        BotSource<PokeBotState>? GetBot(PokeBotState state);

        PokeRoutineExecutorBase CreateBotFromConfig(PokeBotState cfg);

        bool SupportsRoutine(PokeRoutineType pokeRoutineType);
    }

    public abstract class PokeBotRunner<T> : BotRunner<PokeBotState>, IPokeBotRunner where T : PKM, new()
    {
        public readonly PokeRaidHub<T> Hub;
        private readonly BotFactory<T> Factory;

        public PokeRaidHubConfig Config => Hub.Config;
        IList<BotSource<PokeBotState>> IPokeBotRunner.Bots => base.Bots;
        protected PokeBotRunner(PokeRaidHub<T> hub, BotFactory<T> factory)
        {
            Hub = hub;
            Factory = factory;
        }

        protected PokeBotRunner(PokeRaidHubConfig config, BotFactory<T> factory)
        {
            Factory = factory;
            Hub = new PokeRaidHub<T>(config);
        }

        protected virtual void AddIntegrations()
        { }

        public const string OneSwitchPerProgram =
            "SVRaidBot runs one Switch per program. To host on another Switch, copy SVRaidBot into its own folder and run that copy.";

        public override void Add(RoutineExecutor<PokeBotState> bot)
        {
            // Raid state (map, event data, the shared raid list) belongs to the
            // program, not the bot, so two Switches in one program would mix them up.
            if (Bots.Count > 0)
                throw new ArgumentException(OneSwitchPerProgram);
            base.Add(bot);
            if (bot is PokeRoutineExecutorBase b && b.Config.InitialRoutine.IsRaidBot())
                Hub.Bots.Add(b);
        }

        public override bool Remove(IConsoleBotConfig cfg, bool callStop)
        {
            var bot = GetBot(cfg)?.Bot;
            if (bot is PokeRoutineExecutorBase b && b.Config.InitialRoutine.IsRaidBot())
                Hub.Bots.Remove(b);
            return base.Remove(cfg, callStop);
        }

        public override void StartAll()
        {
            InitializeStart();

            if (!Hub.Config.SkipConsoleBotCreation)
                base.StartAll();
        }

        public override void InitializeStart()
        {
            if (RunOnce)
                return;

            AutoLegalityWrapper.EnsureInitialized(Hub.Config.Legality);

            AddIntegrations();

            base.InitializeStart();
        }

        public override void StopAll()
        {
            base.StopAll();

            // bots currently don't de-register
            Thread.Sleep(100);
        }

        public override void PauseAll()
        {
            if (!Hub.Config.SkipConsoleBotCreation)
                base.PauseAll();
        }

        public override void ResumeAll()
        {
            if (!Hub.Config.SkipConsoleBotCreation)
                base.ResumeAll();
        }

        public PokeRoutineExecutorBase CreateBotFromConfig(PokeBotState cfg) => Factory.CreateBot(Hub, cfg);

        public BotSource<PokeBotState>? GetBot(PokeBotState state) => base.GetBot(state);

        void IPokeBotRunner.Remove(IConsoleBotConfig state, bool callStop) => Remove(state, callStop);

        public void Add(PokeRoutineExecutorBase newbot) => Add((RoutineExecutor<PokeBotState>)newbot);

        public bool SupportsRoutine(PokeRoutineType t) => Factory.SupportsRoutine(t);
    }
}