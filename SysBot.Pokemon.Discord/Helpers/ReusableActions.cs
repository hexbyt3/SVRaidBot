using Discord.Commands;
using Discord.WebSocket;
using SysBot.Base;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    public static class ReusableActions
    {
        public static async Task EchoAndReply(this ISocketMessageChannel channel, string msg)
        {
            // An echo channel already gets the message from EchoUtil.
            EchoUtil.Echo(msg);
            if (!EchoModule.IsEchoChannel(channel))
                await channel.SendMessageAsync(msg).ConfigureAwait(false);
        }

        // "@Bot addSudo @user" puts the bot in MentionedUsers too; without Message Content a mention is how it gets addressed.
        public static IReadOnlyList<SocketUser> MentionedUsersExceptBot(this SocketCommandContext context)
            => [.. context.Message.MentionedUsers.Where(u => u.Id != context.Client.CurrentUser.Id)];

        public static string StripCodeBlock(string str) => str.Replace("`\n", "").Replace("\n`", "").Replace("`", "").Trim();
    }
}
