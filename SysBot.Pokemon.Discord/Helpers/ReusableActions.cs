using Discord.WebSocket;
using SysBot.Base;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    public static class ReusableActions
    {
        public static async Task EchoAndReply(this ISocketMessageChannel channel, string msg)
        {
            EchoUtil.Echo(msg);
            await channel.SendMessageAsync(msg).ConfigureAwait(false);
        }

        public static string StripCodeBlock(string str) => str.Replace("`\n", "").Replace("\n`", "").Replace("`", "").Trim();
    }
}