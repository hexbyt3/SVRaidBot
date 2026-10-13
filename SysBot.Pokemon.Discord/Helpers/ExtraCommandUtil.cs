using Discord;
using Discord.Commands;
using Discord.WebSocket;
using PKHeX.Core;
using SysBot.Base;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    public class ExtraCommandUtil<T> where T : PKM, new()
    {
        private static readonly ConcurrentDictionary<ulong, ReactMessageContents> ReactMessageDict = new();
        private static int DictWipeRunning;

        private static readonly IEmote[] PageReactions = [new Emoji("⬅️"), new Emoji("➡️")];
        private static readonly IEmote[] SortReactions = [new Emoji("⬆️"), new Emoji("⬇️")];

        private class ReactMessageContents
        {
            public List<string> Pages { get; set; } = new();
            public EmbedBuilder Embed { get; set; } = new();
            public ulong MessageID { get; set; }
            public DateTime EntryTime { get; set; }
            public readonly SemaphoreSlim Gate = new(1, 1);
        }

        public async Task ListUtil(SocketCommandContext ctx, string nameMsg, string entry)
        {
            List<string> pageContent = ListUtilPrep(entry);
            bool canReact = ctx.Guild is not null && ctx.Guild.CurrentUser.GetPermissions(ctx.Channel as IGuildChannel).AddReactions;
            var embed = new EmbedBuilder { Color = GetBorderColor(false) }.AddField(x =>
            {
                x.Name = nameMsg;
                x.Value = pageContent[0];
                x.IsInline = false;
            }).WithFooter(x =>
            {
                x.IconUrl = "https://i.imgur.com/nXNBrlr.png";
                x.Text = $"Page 1 of {pageContent.Count}";
            });

            if (!canReact && pageContent.Count > 1)
            {
                embed.AddField(x =>
                {
                    x.Name = "Missing \"Add Reactions\" Permission";
                    x.Value = "Displaying only the first page of the list due to embed field limits.";
                });
            }

            var msg = await ctx.Message.Channel.SendMessageAsync(embed: embed.Build()).ConfigureAwait(false);
            if (pageContent.Count > 1 && canReact)
            {
                ReactMessageDict[ctx.User.Id] = new() { Embed = embed, Pages = pageContent, MessageID = msg.Id, EntryTime = DateTime.Now };

                // Only comma lists hold one item per entry; sorting the lines of a multi-line list would scramble it.
                IEmote[] reactions = IsCommaList(entry) ? [.. PageReactions, .. SortReactions] : PageReactions;
                _ = Task.Run(async () =>
                {
                    try { await msg.AddReactionsAsync(reactions).ConfigureAwait(false); }
                    catch (Exception ex) { LogUtil.LogSafe(ex, nameof(ListUtil)); }
                });
                if (Interlocked.CompareExchange(ref DictWipeRunning, 1, 0) == 0)
                    _ = Task.Run(DictWipeMonitor);
            }
        }

        private static async Task DictWipeMonitor()
        {
            while (true)
            {
                await Task.Delay(10_000).ConfigureAwait(false);
                foreach (var entry in ReactMessageDict)
                {
                    if ((DateTime.Now - entry.Value.EntryTime).TotalSeconds > 90.0)
                        ReactMessageDict.TryRemove(entry);
                }
            }
        }

        public static Task HandleReactionAsync(Cacheable<IUserMessage, ulong> cachedMsg, Cacheable<IMessageChannel, ulong> ch, SocketReaction reaction)
        {
            if (!ReactMessageDict.TryGetValue(reaction.UserId, out var contents) || contents.MessageID != reaction.MessageId)
                return Task.CompletedTask;
            if (!PageReactions.Contains(reaction.Emote) && !SortReactions.Contains(reaction.Emote))
                return Task.CompletedTask;

            _ = Task.Run(async () =>
            {
                await contents.Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var msg = await cachedMsg.GetOrDownloadAsync().ConfigureAwait(false);
                    if (msg is null)
                        return;
                    await TurnPageAsync(msg, contents, reaction).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogUtil.LogSafe(ex, nameof(HandleReactionAsync));
                }
                finally
                {
                    contents.Gate.Release();
                }
            });
            return Task.CompletedTask;
        }

        private static async Task TurnPageAsync(IUserMessage msg, ReactMessageContents contents, SocketReaction reaction)
        {
            int page = contents.Pages.IndexOf((string)contents.Embed.Fields[0].Value);
            if (page == -1)
                return;

            var name = reaction.Emote.Name;
            if (name == PageReactions[0].Name)
                page = page == 0 ? contents.Pages.Count - 1 : page - 1;
            else if (name == PageReactions[1].Name)
                page = page + 1 == contents.Pages.Count ? 0 : page + 1;
            else
            {
                var items = contents.Pages.SelectMany(p => p.Split(',')).Select(x => x.Trim()).Where(x => x.Length > 0);
                var sorted = name == SortReactions[0].Name ? items.OrderBy(SortKey) : items.OrderByDescending(SortKey);
                contents.Pages = ListUtilPrep(string.Join(", ", sorted));
                page = Math.Clamp(page, 0, contents.Pages.Count - 1);
            }

            contents.Embed.Fields[0].Value = contents.Pages[page];
            contents.Embed.Footer.Text = $"Page {page + 1} of {contents.Pages.Count}";
            await msg.RemoveReactionAsync(reaction.Emote, reaction.UserId).ConfigureAwait(false);
            await msg.ModifyAsync(m => m.Embed = contents.Embed.Build()).ConfigureAwait(false);
        }

        // Entries look like "1. Name"; sort on what follows the first space, or the whole entry when there is none.
        private static string SortKey(string item) => item.Split(' ', 2) is [_, var rest] ? rest : item;

        private static bool IsCommaList(string entry) => entry.Contains(',');

        private static List<string> SpliceAtWord(string entry, int start, int length)
        {
            int counter = 0;
            List<string> list = new();
            var temp = entry.Contains(',') ? entry.Split(',').Skip(start) : entry.Contains('|') ? entry.Split('|').Skip(start) : entry.Split('\n').Skip(start);

            if (entry.Length < length)
            {
                list.Add(entry ?? "");
                return list;
            }

            foreach (var line in temp)
            {
                counter += line.Length + 2;
                if (counter < length)
                    list.Add(line.Trim());
                else break;
            }
            return list;
        }

        private static List<string> ListUtilPrep(string entry)
        {
            List<string> pageContent = new();
            if (entry.Length > 1024)
            {
                var index = 0;
                while (true)
                {
                    var splice = SpliceAtWord(entry, index, 1024);
                    if (splice.Count == 0)
                        break;

                    index += splice.Count;
                    pageContent.Add(string.Join(entry.Contains(',') ? ", " : entry.Contains('|') ? " | " : "\n", splice));
                }
            }
            else pageContent.Add(entry == "" ? "No results found." : entry);
            return pageContent;
        }

        public Color GetBorderColor(bool gift, PKM? pkm = null)
        {
            bool swsh = typeof(T) == typeof(PK8);
            if (pkm is null && swsh)
                return gift ? Color.Purple : Color.Blue;
            else if (pkm is null && !swsh)
                return gift ? Color.DarkPurple : Color.DarkBlue;
            else if (pkm is not null && swsh)
                return (pkm.IsShiny && pkm.FatefulEncounter) || pkm.ShinyXor == 0 ? Color.Gold : pkm.IsShiny ? Color.LightOrange : Color.Teal;
            else if (pkm is not null && !swsh)
                return (pkm.IsShiny && pkm.FatefulEncounter) || pkm.ShinyXor == 0 ? Color.Gold : pkm.IsShiny ? Color.DarkOrange : Color.DarkTeal;
            throw new NotImplementedException();
        }
    }
}