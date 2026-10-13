using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    /// <summary>
    /// Requires an assigned role in order to accept commands. Can be used by sudo users if satisfied.
    /// </summary>
    public sealed class RequireRoleAccessAttribute : PreconditionAttribute
    {
        private readonly string _name;

        public RequireRoleAccessAttribute(string name) => _name = name;

        public override Task<PreconditionResult> CheckPermissionsAsync(ICommandContext context, CommandInfo command, IServiceProvider services)
            => Task.FromResult(Check(context.User, _name));

        /// <summary>
        /// The role check on its own, for paths that run a command without the command pipeline (the raidinfo ✅).
        /// </summary>
        public static PreconditionResult Check(IUser user, string roleList)
        {
            var mgr = SysCordSettings.Manager;
            if (mgr.Config.AllowGlobalSudo && mgr.CanUseSudo(user.Id))
                return PreconditionResult.FromSuccess();

            if (mgr.AllowsEveryone(roleList))
                return PreconditionResult.FromSuccess();

            // Roles only exist in a guild.
            if (user is not SocketGuildUser gUser)
                return PreconditionResult.FromError("You must be sending the message from a guild to run this command.");

            var roles = gUser.Roles.Select(z => z.Name).ToList();
            if (mgr.CanUseSudo(roles) || mgr.GetHasRoleAccess(roleList, roles))
                return PreconditionResult.FromSuccess();

            return PreconditionResult.FromError("You do not have the required role to run this command.");
        }
    }
}
