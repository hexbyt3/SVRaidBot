using Discord;
using Discord.Commands;
using Discord.WebSocket;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    public static class EmbedColorConverter
    {
        public static Color ToDiscordColor(this EmbedColorOption colorOption)
        {
            return colorOption switch
            {
                EmbedColorOption.Blue => Color.Blue,
                EmbedColorOption.Green => Color.Green,
                EmbedColorOption.Red => Color.Red,
                EmbedColorOption.Gold => Color.Gold,
                EmbedColorOption.Purple => Color.Purple,
                EmbedColorOption.Teal => Color.Teal,
                EmbedColorOption.Orange => Color.Orange,
                EmbedColorOption.Magenta => Color.Magenta,
                EmbedColorOption.LightGrey => Color.LightGrey,
                EmbedColorOption.DarkGrey => Color.DarkGrey,
                _ => Color.Blue,  // Default to Blue if somehow an undefined enum value is used
            };
        }
    }

    public class EchoModule : ModuleBase<SocketCommandContext>
    {
        private static DiscordSettings Settings { get; set; } = default!;

        private class EchoChannel
        {
            public readonly ulong ChannelID;
            public readonly string ChannelName;
            public readonly Action<string> Action;
            public readonly Func<byte[]?, string, EmbedBuilder, Task<IUserMessage>> RaidAction;

            public EchoChannel(ulong channelId, string channelName, Action<string> action, Func<byte[]?, string, EmbedBuilder, Task<IUserMessage>> raidAction)
            {
                ChannelID = channelId;
                ChannelName = channelName;
                Action = action;
                RaidAction = raidAction;
            }
        }

        private static readonly Dictionary<ulong, EchoChannel> Channels = new();

        public static void RestoreChannels(DiscordSocketClient discord, DiscordSettings cfg)
        {
            Settings = cfg;
            foreach (var ch in cfg.EchoChannels)
            {
                if (discord.GetChannel(ch.ID) is ISocketMessageChannel c)
                    AddEchoChannel(c, ch.ID);
            }
            // EchoUtil.Echo("Added echo notification to Discord channel(s) on Bot startup.");
        }

        [Command("Announce", RunMode = RunMode.Async)]
        [Alias("announce")]
        [Summary("Sends an announcement to all EchoChannels added by the aec command.")]
        [RequireOwner]
        public async Task AnnounceAsync([Remainder] string announcement)
        {
            var unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var formattedTimestamp = $"<t:{unixTimestamp}:F>";
            var embedColor = Settings.AnnouncementSettings.RandomAnnouncementColor ? GetRandomColor() : Settings.AnnouncementSettings.AnnouncementEmbedColor.ToDiscordColor();
            var thumbnailUrl = Settings.AnnouncementSettings.RandomAnnouncementThumbnail ? GetRandomThumbnail() : GetSelectedThumbnail();

            var embedDescription = $"## {announcement}\n\n**Sent: {formattedTimestamp}**";

            var embed = new EmbedBuilder
            {
                Color = embedColor,
                Description = embedDescription
            }
            .WithTitle("Important Announcement!")
            .WithThumbnailUrl(thumbnailUrl)
            .Build();

            var client = Context.Client;
            foreach (var channelEntry in Channels)
            {
                var channelId = channelEntry.Key;
                var channel = client.GetChannel(channelId) as ISocketMessageChannel;
                if (channel == null)
                {
                    LogUtil.LogError($"Failed to find or access channel {channelId}", nameof(AnnounceAsync));
                    continue;
                }

                try
                {
                    await channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Failed to send announcement to channel {channel.Name}: {ex.Message}", nameof(AnnounceAsync));
                }
            }
            var confirmationMessage = await ReplyAsync("Announcement sent to all EchoChannels.").ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            try
            {
                await confirmationMessage.DeleteAsync().ConfigureAwait(false);
                await Context.Message.DeleteAsync().ConfigureAwait(false);
            }
            catch (global::Discord.Net.HttpException)
            {
                // No Manage Messages here, or it's a DM; the announcement already went out.
            }
        }

        private Color GetRandomColor()
        {
            // Generate a random color
            var random = new Random();
            var colors = Enum.GetValues(typeof(EmbedColorOption)).Cast<EmbedColorOption>().ToList();
            return colors[random.Next(colors.Count)].ToDiscordColor();
        }

        private string GetRandomThumbnail()
        {
            // Define a list of available thumbnail URLs
            var thumbnailOptions = new List<string>
    {
        "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/gengarmegaphone.png",
        "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/pikachumegaphone.png",
        "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/umbreonmegaphone.png",
        "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/sylveonmegaphone.png",
        "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/charmandermegaphone.png",
        "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/jigglypuffmegaphone.png",
        "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/flareonmegaphone.png",
    };

            // Generate a random index and return the corresponding URL
            var random = new Random();
            return thumbnailOptions[random.Next(thumbnailOptions.Count)];
        }

        private string GetSelectedThumbnail()
        {
            // Use the custom thumbnail URL if it's not empty; otherwise, use the selected option
            if (!string.IsNullOrEmpty(Settings.AnnouncementSettings.CustomAnnouncementThumbnailUrl))
            {
                return Settings.AnnouncementSettings.CustomAnnouncementThumbnailUrl;
            }
            else
            {
                return GetUrlFromThumbnailOption(Settings.AnnouncementSettings.AnnouncementThumbnailOption);
            }
        }

        private string GetUrlFromThumbnailOption(ThumbnailOption option)
        {
            switch (option)
            {
                case ThumbnailOption.Gengar:
                    return "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/gengarmegaphone.png";

                case ThumbnailOption.Pikachu:
                    return "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/pikachumegaphone.png";

                case ThumbnailOption.Umbreon:
                    return "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/umbreonmegaphone.png";

                case ThumbnailOption.Sylveon:
                    return "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/sylveonmegaphone.png";

                case ThumbnailOption.Charmander:
                    return "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/charmandermegaphone.png";

                case ThumbnailOption.Jigglypuff:
                    return "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/jigglypuffmegaphone.png";

                case ThumbnailOption.Flareon:
                    return "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/flareonmegaphone.png";

                default:
                    return "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/gengarmegaphone.png";
            }
        }

        [Command("addEmbedChannel")]
        [Alias("aec")]
        [Summary("Makes the bot post raid embeds to the channel.")]
        [RequireSudo]
        public async Task AddEchoAsync()
        {
            var c = Context.Channel;
            var cid = c.Id;
            if (Channels.TryGetValue(cid, out _))
            {
                await ReplyAsync("Already notifying here.").ConfigureAwait(false);
                return;
            }

            AddEchoChannel(c, cid);

            // Add to discord global loggers (saves on program close)
            SysCordSettings.Settings.EchoChannels.AddIfNew(new[] { GetReference(Context.Channel) });
            await ReplyAsync("Added Raid Embed output to this channel!").ConfigureAwait(false);
        }

        private static async Task<bool> SendMessageWithRetry(ISocketMessageChannel c, string message, int maxRetries = 3)
        {
            int retryCount = 0;
            while (retryCount < maxRetries)
            {
                try
                {
                    await c.SendMessageAsync(message).ConfigureAwait(false);
                    return true; // Successfully sent the message, exit the loop.
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Failed to send message to channel '{c.Name}' (Attempt {retryCount + 1}): {ex.Message}", nameof(AddEchoChannel));
                    retryCount++;
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false); // Wait for 5 seconds before retrying.
                }
            }
            return false; // Reached max number of retries without success.
        }

        private static async Task<IUserMessage> RaidEmbedAsync(ISocketMessageChannel c, byte[]? bytes, string fileName, EmbedBuilder embed, int maxRetries = 2)
        {
            int retryCount = 0;
            while (retryCount < maxRetries)
            {
                try
                {
                    IUserMessage message;
                    if (bytes is not null && bytes.Length > 0)
                    {
                        message = await c.SendFileAsync(new MemoryStream(bytes), fileName, "", false, embed: embed.Build()).ConfigureAwait(false);
                    }
                    else
                    {
                        message = await c.SendMessageAsync("", false, embed.Build()).ConfigureAwait(false);
                    }
                    return message; // Return the message object instead of a boolean
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Failed to send embed to channel '{c.Name}' (Attempt {retryCount + 1}): {ex.Message}", nameof(AddEchoChannel));
                    retryCount++;
                    if (retryCount < maxRetries)
                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false); // Wait for a second before retrying.
                }
            }
            return null!; // Return null if we couldn't send the message after all retries
        }

        private static void AddEchoChannel(ISocketMessageChannel c, ulong cid)
        {
            Action<string> l = async (msg) => await SendMessageWithRetry(c, msg).ConfigureAwait(false);

            // Create a properly typed Func that returns Task<IUserMessage>
            Func<byte[]?, string, EmbedBuilder, Task<IUserMessage>> rb = (bytes, fileName, embed) =>
                RaidEmbedAsync(c, bytes, fileName, embed);

            EchoUtil.Forwarders.Add(l);
            EchoUtil.RaidForwarders.Add(rb);
            var entry = new EchoChannel(cid, c.Name, l, rb);
            Channels.Add(cid, entry);
        }

        public static bool IsEchoChannel(ISocketMessageChannel c)
        {
            var cid = c.Id;
            return Channels.TryGetValue(cid, out _);
        }

        [Command("echoInfo")]
        [Summary("Dumps the special message (Echo) settings.")]
        [RequireSudo]
        public async Task DumpEchoInfoAsync()
        {
            if (Channels.Count == 0)
            {
                await ReplyAsync("Not posting raid embeds to any channel.").ConfigureAwait(false);
                return;
            }
            var lines = Channels.Values.Select(c => $"#{c.ChannelName} ({c.ChannelID})");
            await ReplyAsync($"Posting raid embeds to:\n{string.Join("\n", lines)}").ConfigureAwait(false);
        }

        [Command("echoClear")]
        [Alias("rec")]
        [Summary("Clears the special message echo settings in that specific channel.")]
        [RequireSudo]
        public async Task ClearEchosAsync()
        {
            var id = Context.Channel.Id;
            if (!Channels.TryGetValue(id, out var echo))
            {
                await ReplyAsync("Not echoing in this channel.").ConfigureAwait(false);
                return;
            }
            EchoUtil.Forwarders.Remove(echo.Action);
            EchoUtil.RaidForwarders.Remove(echo.RaidAction);
            Channels.Remove(Context.Channel.Id);
            SysCordSettings.Settings.EchoChannels.RemoveAll(z => z.ID == id);
            await ReplyAsync($"Echoes cleared from channel: {Context.Channel.Name}").ConfigureAwait(false);
        }

        [Command("echoClearAll")]
        [Alias("raec")]
        [Summary("Clears all the special message Echo channel settings.")]
        [RequireSudo]
        public async Task ClearEchosAllAsync()
        {
            foreach (var l in Channels)
            {
                var entry = l.Value;
                await ReplyAsync($"Echoing cleared from {entry.ChannelName} ({entry.ChannelID})!").ConfigureAwait(false);
                EchoUtil.Forwarders.Remove(entry.Action);
            }
            EchoUtil.Forwarders.RemoveAll(y => Channels.Select(x => x.Value.Action).Contains(y));
            EchoUtil.RaidForwarders.RemoveAll(y => Channels.Select(x => x.Value.RaidAction).Contains(y));
            Channels.Clear();
            SysCordSettings.Settings.EchoChannels.Clear();
            await ReplyAsync("Echoes cleared from all channels!").ConfigureAwait(false);
        }

        private RemoteControlAccess GetReference(IChannel channel) => new()
        {
            ID = channel.Id,
            Name = channel.Name,
            Comment = $"Added by {Context.User.Username} on {DateTime.Now:yyyy.MM.dd-hh:mm:ss}",
        };
    }
}