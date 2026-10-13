using Discord;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SysBot.Base
{
    public static class EchoUtil
    {
        public static readonly SyncList<Action<string>> Forwarders = new();
        public static readonly SyncList<Func<byte[]?, string, EmbedBuilder, Task<IUserMessage>>> RaidForwarders = new();
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

        public static async Task<List<IUserMessage>> RaidEmbed(byte[]? bytes, string fileName, EmbedBuilder embeds)
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