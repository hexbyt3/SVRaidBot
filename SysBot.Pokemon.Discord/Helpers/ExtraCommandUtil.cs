using Discord;
using Discord.Commands;
using Discord.WebSocket;
using PKHeX.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    public class ExtraCommandUtil<T> where T : PKM, new()
    {
        private static readonly Dictionary<ulong, ReactMessageContents> ReactMessageDict = new();
        private static bool DictWipeRunning = false;

        private class ReactMessageContents
        {
            public List<string> Pages { get; set; } = new();
            public EmbedBuilder Embed { get; set; } = new();
            public ulong MessageID { get; set; }
            public DateTime EntryTime { get; set; }
        }

        public async Task ListUtil(SocketCommandContext ctx, string nameMsg, string entry)
        {
            List<string> pageContent = ListUtilPrep(entry);
            bool canReact = ctx.Guild.CurrentUser.GetPermissions(ctx.Channel as IGuildChannel).AddReactions;
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
                bool exists = ReactMessageDict.TryGetValue(ctx.User.Id, out _);
                if (exists)
                    ReactMessageDict[ctx.User.Id] = new() { Embed = embed, Pages = pageContent, MessageID = msg.Id, EntryTime = DateTime.Now };
                else ReactMessageDict.Add(ctx.User.Id, new() { Embed = embed, Pages = pageContent, MessageID = msg.Id, EntryTime = DateTime.Now });

                IEmote[] reactions = { new Emoji("⬅️"), new Emoji("➡️"), new Emoji("⬆️"), new Emoji("⬇️") };
                _ = Task.Run(async () => await msg.AddReactionsAsync(reactions).ConfigureAwait(false));
                if (!DictWipeRunning)
                    _ = Task.Run(DictWipeMonitor);
            }
        }

        private static async Task DictWipeMonitor()
        {
            DictWipeRunning = true;
            while (true)
            {
                await Task.Delay(10_000).ConfigureAwait(false);
                for (int i = 0; i < ReactMessageDict.Count; i++)
                {
                    var entry = ReactMessageDict.ElementAt(i);
                    var delta = (DateTime.Now - entry.Value.EntryTime).TotalSeconds;
                    if (delta > 90.0)
                        ReactMessageDict.Remove(entry.Key);
                }
            }
        }

        public static Task HandleReactionAsync(Cacheable<IUserMessage, ulong> cachedMsg, Cacheable<IMessageChannel, ulong> ch, SocketReaction reaction)
        {
            _ = Task.Run(async () =>
            {
                IEmote[] reactions = { new Emoji("⬅️"), new Emoji("➡️"), new Emoji("⬆️"), new Emoji("⬇️") };
                if (!reactions.Contains(reaction.Emote))
                    return;

                if (!ch.HasValue || ch.Value is IDMChannel)
                    return;

                IUserMessage msg;
                if (!cachedMsg.HasValue)
                    msg = await cachedMsg.GetOrDownloadAsync().ConfigureAwait(false);
                else msg = cachedMsg.Value;

                bool process = msg.Embeds.First().Fields[0].Name.Contains("list");
                if (!process || !reaction.User.IsSpecified)
                    return;

                var user = reaction.User.Value;
                if (user.IsBot || !ReactMessageDict.ContainsKey(user.Id))
                    return;

                bool invoker = msg.Embeds.First().Fields[0].Name == ReactMessageDict[user.Id].Embed.Fields[0].Name;
                if (!invoker)
                    return;

                var contents = ReactMessageDict[user.Id];
                bool oldMessage = msg.Id != contents.MessageID;
                if (oldMessage)
                    return;

                int page = contents.Pages.IndexOf((string)contents.Embed.Fields[0].Value);
                if (page == -1)
                    return;

                if (reaction.Emote.Name == reactions[0].Name || reaction.Emote.Name == reactions[1].Name)
                {
                    if (reaction.Emote.Name == reactions[0].Name)
                    {
                        if (page == 0)
                            page = contents.Pages.Count - 1;
                        else page--;
                    }
                    else
                    {
                        if (page + 1 == contents.Pages.Count)
                            page = 0;
                        else page++;
                    }

                    contents.Embed.Fields[0].Value = contents.Pages[page];
                    contents.Embed.Footer.Text = $"Page {page + 1} of {contents.Pages.Count}";
                    await msg.RemoveReactionAsync(reactions[reaction.Emote.Name == reactions[0].Name ? 0 : 1], user).ConfigureAwait(false);
                    await msg.ModifyAsync(msg => msg.Embed = contents.Embed.Build()).ConfigureAwait(false);
                }
                else if (reaction.Emote.Name == reactions[2].Name || reaction.Emote.Name == reactions[3].Name)
                {
                    List<string> tempList = new();
                    foreach (var p in contents.Pages)
                    {
                        var split = p.Replace(", ", ",").Split(',');
                        tempList.AddRange(split);
                    }

                    var tempEntry = string.Join(", ", reaction.Emote.Name == reactions[2].Name ? tempList.OrderBy(x => x.Split(' ')[1]) : tempList.OrderByDescending(x => x.Split(' ')[1]));
                    contents.Pages = ListUtilPrep(tempEntry);
                    contents.Embed.Fields[0].Value = contents.Pages[page];
                    contents.Embed.Footer.Text = $"Page {page + 1} of {contents.Pages.Count}";
                    await msg.RemoveReactionAsync(reactions[reaction.Emote.Name == reactions[2].Name ? 2 : 3], user).ConfigureAwait(false);
                    await msg.ModifyAsync(msg => msg.Embed = contents.Embed.Build()).ConfigureAwait(false);
                }
            });
            return Task.CompletedTask;
        }

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