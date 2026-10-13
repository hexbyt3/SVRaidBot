using Discord;
using Discord.Commands;
using PKHeX.Core;
using SysBot.Base;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    public class BotModule<T> : ModuleBase<SocketCommandContext> where T : PKM, new()
    {
        [Command("botStatus")]
        [Summary("Gets the status of the bots.")]
        [RequireSudo]
        public async Task GetStatusAsync()
        {
            var me = SysCord<T>.Runner;
            var bots = me.Bots.Select(z => z.Bot).OfType<PokeRoutineExecutorBase>().ToArray();
            if (bots.Length == 0)
            {
                await ReplyAsync("No bots configured.").ConfigureAwait(false);
                return;
            }

            var summaries = bots.Select(GetDetailedSummary);
            var lines = string.Join(Environment.NewLine, summaries);
            await ReplyAsync(Format.Code(lines)).ConfigureAwait(false);
        }

        // One Switch per program, so the runner's only bot is the one to act on.
        private async Task<BotSource<PokeBotState>?> GetBotAsync()
        {
            var bot = SysCord<T>.Runner.Bots.FirstOrDefault();
            if (bot == null)
                await ReplyAsync("No bot is configured.").ConfigureAwait(false);
            return bot;
        }

        private static string GetDetailedSummary(PokeRoutineExecutorBase z)
        {
            return $"- {z.Connection.Name} | {z.Connection.Label} - {z.Config.CurrentRoutineType} ~ {z.LastTime:hh:mm:ss} | {z.LastLogged}";
        }

        [Command("botStart")]
        [Summary("Starts the currently running bot.")]
        [RequireSudo]
        public async Task StartBotAsync()
        {
            if (await GetBotAsync().ConfigureAwait(false) is not { } bot)
                return;

            bot.Start();
            await ReplyAsync($"Bot has been started.").ConfigureAwait(false);
        }

        [Command("botStop")]
        [Summary("Stops the currently running bot.")]
        [RequireSudo]
        public async Task StopBotAsync()
        {
            if (await GetBotAsync().ConfigureAwait(false) is not { } bot)
                return;

            bot.Stop();
            await ReplyAsync($"Bot has been stopped.").ConfigureAwait(false);
        }

        [Command("botIdle")]
        [Alias("botPause")]
        [Summary("Commands the currently running bot to Idle.")]
        [RequireSudo]
        public async Task IdleBotAsync()
        {
            if (await GetBotAsync().ConfigureAwait(false) is not { } bot)
                return;

            bot.Pause();
            await ReplyAsync($"Bot has been set to idle.").ConfigureAwait(false);
        }

        [Command("botChange")]
        [Summary("Changes the routine of the currently running bot (trades).")]
        [RequireSudo]
        public async Task ChangeTaskAsync([Summary("Routine enum name")] PokeRoutineType task)
        {
            if (await GetBotAsync().ConfigureAwait(false) is not { } bot)
                return;

            bot.Bot.Config.Initialize(task);
            await ReplyAsync($"Bot has changed its routine to {task}.").ConfigureAwait(false);
        }

        [Command("botRestart")]
        [Summary("Restarts the currently running bot(s).")]
        [RequireSudo]
        public async Task RestartBotAsync()
        {
            if (await GetBotAsync().ConfigureAwait(false) is not { } bot)
                return;

            var c = bot.Bot.Connection;
            c.Reset();
            bot.Start();
            await ReplyAsync($"Bot has been restarted.").ConfigureAwait(false);
        }
    }
}