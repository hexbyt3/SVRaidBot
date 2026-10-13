using Discord;
using Discord.WebSocket;
using SysBot.Base;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord.Helpers
{
    public class ReactionService
    {
        private readonly ConcurrentDictionary<ulong, Func<SocketReaction, Task>> _reactionActions = new();

        public ReactionService(DiscordSocketClient client)
        {
            client.ReactionAdded += OnReactionAddedAsync;
        }

        public void AddReactionHandler(ulong messageId, Func<SocketReaction, Task> handler)
        {
            _reactionActions[messageId] = handler;
        }

        /// <summary>Returns false when the handler was already gone, so only one caller acts on it.</summary>
        public bool RemoveReactionHandler(ulong messageId) => _reactionActions.TryRemove(messageId, out _);

        // Handlers send messages; running them on the gateway thread would stall every other event.
        private Task OnReactionAddedAsync(Cacheable<IUserMessage, ulong> cachedMessage, Cacheable<IMessageChannel, ulong> cachedChannel, SocketReaction reaction)
        {
            if (!_reactionActions.TryGetValue(reaction.MessageId, out var handler))
                return Task.CompletedTask;

            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(reaction).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogUtil.LogSafe(ex, nameof(ReactionService));
                }
            });
            return Task.CompletedTask;
        }
    }
}
