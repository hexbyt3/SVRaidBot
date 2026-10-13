using Discord.Commands;
using System;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    /// <summary>
    /// Same check as <see cref="RequireRoleAccessAttribute"/>, for the commands that put users in the raid queue.
    /// </summary>
    public sealed class RequireQueueRoleAttribute : PreconditionAttribute
    {
        private readonly string _name;

        public RequireQueueRoleAttribute(string name) => _name = name;

        public override Task<PreconditionResult> CheckPermissionsAsync(ICommandContext context, CommandInfo command, IServiceProvider services)
            => Task.FromResult(RequireRoleAccessAttribute.Check(context.User, _name));
    }
}
