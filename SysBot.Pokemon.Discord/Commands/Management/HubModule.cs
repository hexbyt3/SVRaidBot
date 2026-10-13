using Discord;
using Discord.Commands;
using PKHeX.Core;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    public class HubModule<T> : ModuleBase<SocketCommandContext> where T : PKM, new()
    {
        [Command("status")]
        [Alias("stats")]
        [Summary("Gets the status of the bot environment.")]
        public async Task GetStatusAsync()
        {
            var builder = new EmbedBuilder
            {
                Color = Color.Gold,
            };

            var runner = SysCord<T>.Runner;
            var allBots = runner.Bots.ConvertAll(z => z.Bot);
            var botCount = allBots.Count;
            builder.AddField(x =>
            {
                x.Name = "Summary";
                x.Value =
                    $"Bot Count: {botCount}\n" +
                    $"Bot State: {SummarizeBots(allBots)}\n";
                x.IsInline = false;
            });

            await ReplyAsync("Bot Status", false, builder.Build()).ConfigureAwait(false);
        }

        private static string SummarizeBots(IReadOnlyCollection<RoutineExecutor<PokeBotState>> bots)
        {
            if (bots.Count == 0)
                return "No bots configured.";
            var summaries = bots.Select(z => $"- {z.GetSummary()}");
            return Environment.NewLine + string.Join(Environment.NewLine, summaries);
        }
    }
}