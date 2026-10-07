using Discord;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SysBot.Base
{
    public static class EchoUtil
    {
        public static readonly SyncList<Action<string>> Forwarders = new();
        public static readonly SyncList<Action<string, Embed>> EmbedForwarders = new();
        public static readonly SyncList<Func<byte[], string, EmbedBuilder, Task<IUserMessage>>> RaidForwarders = new();
        public static readonly SyncList<Func<string, Task>> OwnerForwarders = new();

        /// <summary>
        /// Tells the bot's owner about a problem only someone at the console can fix.
        /// Raid channels never see these.
        /// </summary>
        public static void AlertOwner(string message)
        {
            foreach (var fwd in OwnerForwarders)
                _ = SendAlert(fwd, message);
            LogUtil.LogInfo(message, "Alert");
        }

        private static async Task SendAlert(Func<string, Task> fwd, string message)
        {
            try
            {
                await fwd(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogUtil.LogInfo($"Could not alert the owner: {ex.Message}", "Alert");
            }
        }

        public static void Echo(string message)
        {
            foreach (var fwd in Forwarders)
            {
                try
                {
                    fwd(message);
                }
                catch (Exception ex)
                {
                    LogUtil.LogInfo($"Exception: {ex} occurred while trying to echo: {message} to the forwarder: {fwd}", "Echo");
                    LogUtil.LogSafe(ex, "Echo");
                }
            }
            LogUtil.LogInfo(message, "Echo");
        }

        public static void EchoEmbed(string ping, string message, string url, string markurl, bool result)
        {
            foreach (var fwd in EmbedForwarders)
            {
                try
                {
                    if (!result)
                    {
                        ping = string.Empty;
                        if (string.IsNullOrEmpty(markurl))
                            markurl = $"https://i.imgur.com/t2M8qF4.png";
                        else
                            markurl = $"https://i.imgur.com/t2M8qF4.png";
                    }
                    else if (result)
                    {
                        if (string.IsNullOrEmpty(markurl))
                            markurl = "https://i.imgur.com/T8vEiIk.jpg";
                    }
                    var author = new EmbedAuthorBuilder { IconUrl = markurl, Name = result ? "Match found!" : "Unwanted match..." };
                    var embed = new EmbedBuilder
                    {
                        Color = result ? Color.Teal : Color.Red,
                        ThumbnailUrl = url
                    }.WithAuthor(author).WithDescription(message);
                    fwd(ping, embed.Build());
                }
                catch (Exception ex)
                {
                    LogUtil.LogInfo($"Exception: {ex} occurred while trying to echo: {message} to the forwarder: {fwd}", "Echo");
                    LogUtil.LogSafe(ex, "Echo");
                }
            }
            LogUtil.LogInfo(message, "Echo");
        }

        public static async Task<List<IUserMessage>> RaidEmbed(byte[] bytes, string fileName, EmbedBuilder embeds)
        {
            List<IUserMessage> sentMessages = new List<IUserMessage>();
            foreach (var fwd in RaidForwarders)
            {
                try
                {
                    var message = await fwd(bytes, fileName, embeds);
                    if (message != null)
                        sentMessages.Add(message);
                }
                catch (Exception ex)
                {
                    LogUtil.LogInfo($"Exception: {ex} occurred while trying to echo: RaidEmbed to the forwarder: {fwd}", "Echo");
                    LogUtil.LogSafe(ex, "Echo");
                }
            }
            return sentMessages;
        }
    }
}