using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon.Discord.Helpers;
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    public static class SysCordSettings
    {
        public static DiscordManager Manager { get; internal set; } = default!;
        public static DiscordSettings Settings => Manager.Config;

        /// <summary>True when Discord withheld the Message Content intent, so only @mentions and DMs reach the bot.</summary>
        public static bool MentionOnly { get; internal set; }

        /// <summary>How to write <paramref name="command"/> so it reaches the bot: prefixed, or after an @mention in mention-only mode.</summary>
        public static string CommandUsage(IUser bot, string command)
            => MentionOnly ? $"{bot.Mention} `{command}`" : $"`{Settings.CommandPrefix}{command}`";
    }

    public sealed class SysCord<T> where T : PKM, new()
    {
        public static PokeBotRunner<T> Runner { get; private set; } = default!;

        public static ReactionService ReactionService { get; private set; } = default!;
        private DiscordSocketClient _client = default!;
        private readonly DiscordManager Manager;
        public readonly PokeRaidHub<T> Hub;
        private const int MaxReconnectDelay = 60000; // 1 minute
        private int _reconnectAttempts;
        private volatile bool _stoppedForGood;
        private string _apiToken = string.Empty;
        private readonly RaidCodeCooldown _codeCooldown = new();
        private readonly CommandService _commands;
        private readonly IServiceProvider _services;

        // Track loading of Echo/Logging channels so they aren't loaded multiple times.
        private bool MessageChannelsLoaded { get; set; }

        public SysCord(PokeBotRunner<T> runner)
        {
            Runner = runner;
            Hub = runner.Hub;
            Manager = new DiscordManager(Hub.Config.Discord);
            SysCordSettings.Manager = Manager;

            _commands = new CommandService(new CommandServiceConfig
            {
                LogLevel = LogSeverity.Info,
                // Async runs commands on the thread pool so long ones can't block the websocket.
                DefaultRunMode = Hub.Config.Discord.AsyncCommands ? RunMode.Async : RunMode.Sync,
                CaseSensitiveCommands = false,
            });
            _commands.Log += Log;
            _commands.CommandExecuted += OnCommandExecutedAsync;
            _services = ConfigureServices();
        }

        private void CreateClient(GatewayIntents intents)
        {
            SysCordSettings.MentionOnly = (intents & GatewayIntents.MessageContent) == 0;
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Info,
                GatewayIntents = intents,
                MessageCacheSize = 500,
                ConnectionTimeout = 30000,
            });
            _client.Log += Log;
            _client.Connected += OnConnected;
            _client.Disconnected += HandleDisconnect;
            _client.Ready += LoadLoggingAndEcho;
            _client.MessageReceived += HandleMessageAsync;
            _client.ReactionAdded += HandleReactionAddedAsync;
            _client.ReactionAdded += ExtraCommandUtil<T>.HandleReactionAsync;
            ReactionService = new ReactionService(_client);
        }

        // Discord checks intents when the bot (re)connects, so a bot that crosses 10,000 users
        // while running learns it lost Message Content as a 4014 on its next reconnect. Intents
        // are fixed per client, so connect a new one without it. The old one stays logged in
        // for REST, which the restored log and echo channels still send through.
        private async Task SwitchToMentionOnlyAsync()
        {
            var old = _client;
            old.Disconnected -= HandleDisconnect;
            old.MessageReceived -= HandleMessageAsync;
            old.ReactionAdded -= HandleReactionAddedAsync;
            old.ReactionAdded -= ExtraCommandUtil<T>.HandleReactionAsync;
            try { await old.StopAsync().ConfigureAwait(false); } catch { }

            LogUtil.LogError("Discord took away the Message Content intent (4014). Switching to mention-only mode: commands work when the message @mentions the bot, and in DMs.", "Discord");
            CreateClient(GatewayIntentPolicy.WithoutMessageContent);
            try
            {
                await _client.LoginAsync(TokenType.Bot, _apiToken).ConfigureAwait(false);
                await _client.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Could not reconnect in mention-only mode: {ex.Message}", "Discord");
            }
        }

        private static IServiceProvider ConfigureServices() => new ServiceCollection().BuildServiceProvider();

        private static Task Log(LogMessage msg)
        {
            var text = $"[{msg.Severity,8}] {msg.Source}: {msg.Message} {msg.Exception}";
            Console.ForegroundColor = GetTextColor(msg.Severity);
            Console.WriteLine($"{DateTime.Now,-19} {text}");
            Console.ResetColor();

            LogUtil.LogText($"SysCord: {text}");

            return Task.CompletedTask;
        }

        private static ConsoleColor GetTextColor(LogSeverity sv) => sv switch
        {
            LogSeverity.Critical => ConsoleColor.Red,
            LogSeverity.Error => ConsoleColor.Red,

            LogSeverity.Warning => ConsoleColor.Yellow,
            LogSeverity.Info => ConsoleColor.White,

            LogSeverity.Verbose => ConsoleColor.DarkGray,
            LogSeverity.Debug => ConsoleColor.DarkGray,
            _ => Console.ForegroundColor,
        };

        private Task OnConnected()
        {
            Interlocked.Exchange(ref _reconnectAttempts, 0);
            return Task.CompletedTask;
        }

        private Task HandleDisconnect(Exception ex)
        {
            if (GatewayIntentPolicy.GetCloseCode(ex) == 4014 && !SysCordSettings.MentionOnly)
            {
                _ = Task.Run(SwitchToMentionOnlyAsync);
                return Task.CompletedTask;
            }

            if (GatewayIntentPolicy.GetCloseCode(ex) is int code && GatewayIntentPolicy.IsFatalClose(ex))
            {
                // Retrying a non-reconnectable close only burns IDENTIFYs; 1000 a day resets the token.
                _stoppedForGood = true;
                LogUtil.LogError($"Discord connection stopped. {GatewayIntentPolicy.DescribeFatalClose(code)}", "Discord");
                _ = Task.Run(async () =>
                {
                    try { await _client.StopAsync().ConfigureAwait(false); } catch { }
                });
                return Task.CompletedTask;
            }

            // Discord asked for the reconnect and Discord.Net handles it.
            if (ex is GatewayReconnectException)
                return Task.CompletedTask;

            Log(new LogMessage(LogSeverity.Warning, "Gateway", $"Disconnected: {ex.Message}"));

            _ = Task.Run(async () =>
            {
                var attempt = Interlocked.Increment(ref _reconnectAttempts);
                var delay = Math.Min(MaxReconnectDelay, 1000 * Math.Pow(2, attempt - 1));
                await Task.Delay((int)delay).ConfigureAwait(false);
                await TryRestartAsync().ConfigureAwait(false);
            });

            return Task.CompletedTask;
        }

        // Discord.Net reconnects on its own after most drops; only a client that has fully stopped needs StartAsync.
        private async Task TryRestartAsync()
        {
            if (_stoppedForGood || _client.ConnectionState != ConnectionState.Disconnected)
                return;

            try
            {
                await _client.StartAsync().ConfigureAwait(false);
                await Log(new LogMessage(LogSeverity.Info, "Gateway", "Reconnect started.")).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Log(new LogMessage(LogSeverity.Error, "Gateway", $"Failed to reconnect: {ex.Message}")).ConfigureAwait(false);
            }
        }

        public async Task MainAsync(string apiToken, CancellationToken token)
        {
            try
            {
                _apiToken = apiToken;
                var intents = await GatewayIntentPolicy.ResolveAsync(apiToken).ConfigureAwait(false);
                CreateClient(intents);
                await InitCommands().ConfigureAwait(false);

                await _client.LoginAsync(TokenType.Bot, apiToken).ConfigureAwait(false);
                await _client.StartAsync().ConfigureAwait(false);

                var app = await _client.GetApplicationInfoAsync().ConfigureAwait(false);
                Manager.Owner = app.Owner.Id;

                EchoUtil.OwnerForwarders.Clear();
                EchoUtil.OwnerForwarders.Add(msg => app.Owner.SendMessageAsync(msg));

                await CheckConnectionStatus(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"The Discord bot could not start: {ex.Message}", "Discord");
                LogUtil.LogSafe(ex, "Discord");
            }
        }

        public async Task InitCommands()
        {
            var assembly = Assembly.GetExecutingAssembly();

            await _commands.AddModulesAsync(assembly, _services).ConfigureAwait(false);
            var genericTypes = assembly.DefinedTypes.Where(z => z.IsSubclassOf(typeof(ModuleBase<SocketCommandContext>)) && z.IsGenericType);
            foreach (var t in genericTypes)
            {
                var genModule = t.MakeGenericType(typeof(T));
                await _commands.AddModuleAsync(genModule, _services).ConfigureAwait(false);
            }
            SharedRaidCodeHandler.UpdateMessageReactionCallback = async (messageId, channelId, isActive) =>
            {
                try
                {
                    var channel = await _client.GetChannelAsync(channelId) as IMessageChannel;
                    if (channel != null)
                    {
                        var message = await channel.GetMessageAsync(messageId) as IUserMessage;
                        if (message != null)
                        {
                            await message.RemoveAllReactionsAsync();

                            if (isActive)
                            {
                                await message.AddReactionAsync(new Emoji("✅"));
                                await Log(new LogMessage(LogSeverity.Info, "RaidEmbed", $"Added green check reaction to message {messageId} in channel {channelId}")).ConfigureAwait(false);
                            }
                            else
                            {
                                await message.AddReactionAsync(new Emoji("🚫"));
                                await Log(new LogMessage(LogSeverity.Info, "RaidEmbed", $"Added red X reaction to message {messageId} in channel {channelId}")).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    await Log(new LogMessage(LogSeverity.Error, "RaidEmbed", $"Error updating reactions: {ex.Message}")).ConfigureAwait(false);
                }
            };
        }

        private Task HandleReactionAddedAsync(Cacheable<IUserMessage, ulong> cachedMessage,
            Cacheable<IMessageChannel, ulong> originChannel, SocketReaction reaction)
        {
            if (reaction.UserId == _client.CurrentUser.Id || reaction.Emote.Name != "✅")
                return Task.CompletedTask;
            if (!SharedRaidCodeHandler.IsActiveRaidMessage(reaction.MessageId))
                return Task.CompletedTask;
            if (!_codeCooldown.TryStart(reaction.MessageId, reaction.UserId, DateTime.UtcNow))
                return Task.CompletedTask;

            _ = Task.Run(() => SendRaidCodeAsync(originChannel, reaction));
            return Task.CompletedTask;
        }

        private async Task SendRaidCodeAsync(Cacheable<IMessageChannel, ulong> originChannel, SocketReaction reaction)
        {
            var code = SharedRaidCodeHandler.GetRaidCodeForMessage(reaction.MessageId);
            if (string.IsNullOrEmpty(code))
                return;
            var raidInfoDict = SharedRaidCodeHandler.GetRaidInfoDict(reaction.MessageId);
            try
            {
                // Reactions from members outside the cache arrive without a user object.
                var user = reaction.User.IsSpecified
                    ? reaction.User.Value
                    : await ((IDiscordClient)_client).GetUserAsync(reaction.UserId, CacheMode.AllowDownload).ConfigureAwait(false);
                if (user is null || user.IsBot)
                    return;

                var dmChannel = await user.CreateDMChannelAsync().ConfigureAwait(false);
                bool isShiny = raidInfoDict != null &&
                              raidInfoDict.TryGetValue("IsShiny", out var shinyStr) &&
                              bool.TryParse(shinyStr, out var shinyBool) &&
                              shinyBool;

                var embedColor = isShiny ? Color.Gold : Color.Blue;
                string thumbnailUrl = raidInfoDict?.TryGetValue("ThumbnailUrl", out var thumbUrl) == true && !string.IsNullOrEmpty(thumbUrl)
                    ? thumbUrl
                    : "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/combat.png";

                var embed = new EmbedBuilder()
                {
                    Color = embedColor,
                    Title = $"**Raid Code: {code}**",
                    Description = "Please join quickly! The raid will start soon.",
                    ThumbnailUrl = thumbnailUrl
                };

                if (raidInfoDict != null)
                {
                    string raidTitle = raidInfoDict.TryGetValue("RaidTitle", out var title) ? title : "Pokémon Raid";
                    string teraIconUrl = raidInfoDict.TryGetValue("TeraIconUrl", out var iconUrl) ? iconUrl : "";

                    embed.WithAuthor(new EmbedAuthorBuilder()
                    {
                        Name = raidTitle,
                        IconUrl = teraIconUrl
                    });

                    StringBuilder statsBuilder = new StringBuilder();
                    if (raidInfoDict.TryGetValue("Level", out var level)) statsBuilder.AppendLine($"**Level**: {level}");
                    if (raidInfoDict.TryGetValue("Gender", out var gender)) statsBuilder.AppendLine($"**Gender**: {gender}");
                    if (raidInfoDict.TryGetValue("Nature", out var nature)) statsBuilder.AppendLine($"**Nature**: {nature}");
                    if (raidInfoDict.TryGetValue("Ability", out var ability)) statsBuilder.AppendLine($"**Ability**: {ability}");
                    if (raidInfoDict.TryGetValue("IVs", out var ivs)) statsBuilder.AppendLine($"**IVs**: {ivs}");
                    if (raidInfoDict.TryGetValue("Scale", out var scale)) statsBuilder.AppendLine($"**Scale**: {scale}");

                    if (statsBuilder.Length > 0)
                    {
                        embed.AddField("**__Stats__**", statsBuilder.ToString(), true);
                    }
                    StringBuilder movesBuilder = new StringBuilder();
                    if (raidInfoDict.TryGetValue("Moves", out var moves) && !string.IsNullOrEmpty(moves))
                    {
                        movesBuilder.AppendLine(moves);

                        if (raidInfoDict.TryGetValue("ExtraMoves", out var extraMoves) && !string.IsNullOrEmpty(extraMoves))
                        {
                            movesBuilder.AppendLine("**Extra Moves:**");
                            movesBuilder.AppendLine(extraMoves);
                        }
                    }

                    if (movesBuilder.Length > 0)
                    {
                        embed.AddField("**__Moves__**", movesBuilder.ToString(), true);
                    }
                    else
                    {
                        embed.AddField("**__Moves__**", "No Moves To Display", true);
                    }
                    if (raidInfoDict.TryGetValue("DifficultyLevel", out var diffLevelStr) &&
                        int.TryParse(diffLevelStr, out var diffLevel) &&
                        diffLevel == 7 &&
                        raidInfoDict.TryGetValue("RaidMechanics", out var mechanics) &&
                        !string.IsNullOrEmpty(mechanics))
                    {
                        embed.AddField("**__7★ Raid Mechanics__**", mechanics, false);
                    }
                }

                embed.WithFooter(new EmbedFooterBuilder()
                {
                    Text = $"Code requested at {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                });
                await dmChannel.SendMessageAsync(embed: embed.Build());

                try
                {
                    var channel = await _client.GetChannelAsync(originChannel.Id) as IMessageChannel;
                    if (channel != null)
                    {
                        var message = await channel.GetMessageAsync(reaction.MessageId) as IUserMessage;
                        if (message != null)
                        {
                            await message.RemoveReactionAsync(reaction.Emote, reaction.UserId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Just log but don't interrupt - removing the reaction is just for cleanliness
                    await Log(new LogMessage(LogSeverity.Warning, "RaidCode", $"Failed to remove reaction: {ex.Message}")).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await Log(new LogMessage(LogSeverity.Error, "RaidCode", $"Failed to send raid code DM: {ex.Message}")).ConfigureAwait(false);
            }
        }

        private async Task HandleMessageAsync(SocketMessage arg)
        {
            if (arg is not SocketUserMessage msg)
                return;

            if (msg.Author.Id == _client.CurrentUser.Id || msg.Author.IsBot)
                return;

            int pos = 0;
            if (msg.HasStringPrefix(Hub.Config.Discord.CommandPrefix, ref pos) || msg.HasMentionPrefix(_client.CurrentUser, ref pos))
                await TryHandleCommandAsync(msg, pos).ConfigureAwait(false);
        }

        private async Task TryHandleCommandAsync(SocketUserMessage msg, int pos)
        {
            var context = new SocketCommandContext(_client, msg);

            // "$5 shipping?" starts with the prefix too; only real commands get an answer.
            if (!_commands.Search(context, pos).IsSuccess)
                return;

            var mgr = Manager;
            if (!mgr.CanUseCommandUser(msg.Author.Id))
                return;

            if (msg.Channel is not IDMChannel && !mgr.CanUseCommandChannel(msg.Channel.Id) && msg.Author.Id != mgr.Owner)
            {
                await msg.Channel.SendMessageAsync("You can't use that command here.").ConfigureAwait(false);
                return;
            }

            var guild = msg.Channel is SocketGuildChannel g ? g.Guild.Name : "Unknown Guild";
            await Log(new LogMessage(LogSeverity.Info, "Command", $"Executing command from {guild}#{msg.Channel.Name}:@{msg.Author.Username}. Content: {msg}")).ConfigureAwait(false);
            await _commands.ExecuteAsync(context, pos, _services).ConfigureAwait(false);
        }

        // Raised for sync and async commands alike, so failures from either mode end up here.
        private static async Task OnCommandExecutedAsync(Optional<CommandInfo> command, ICommandContext context, IResult result)
        {
            if (result.IsSuccess || result.Error == CommandError.UnknownCommand)
                return;

            if (result is ExecuteResult { Exception: { } ex })
            {
                var name = command.IsSpecified ? command.Value.Name : "unknown";
                LogUtil.LogError($"Command {name} failed: {ex.Message}", "Discord");
                LogUtil.LogSafe(ex, "Discord");
            }

            try
            {
                await context.Channel.SendMessageAsync(result.ErrorReason).ConfigureAwait(false);
            }
            catch (Exception sendEx)
            {
                LogUtil.LogSafe(sendEx, "Discord");
            }
        }

        private async Task LoadLoggingAndEcho()
        {
            // Don't let it load more than once in case of Discord hiccups.
            if (!MessageChannelsLoaded)
            {
                EchoModule.RestoreChannels(_client, Hub.Config.Discord);
                LogModule.RestoreLogging(_client, Hub.Config.Discord);
                await Log(new LogMessage(LogSeverity.Info, "LoadLoggingAndEcho()", "Logging and Echo channels loaded!")).ConfigureAwait(false);
                MessageChannelsLoaded = true;
            }

            await _client.SetStatusAsync(UserStatus.Online).ConfigureAwait(false);

            var game = Hub.Config.Discord.BotGameStatus;
            if (!string.IsNullOrWhiteSpace(game))
                await _client.SetGameAsync(game).ConfigureAwait(false);
        }

        private async Task CheckConnectionStatus(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(60000, token).ConfigureAwait(false);
                if (_client.ConnectionState == ConnectionState.Disconnected && !_stoppedForGood)
                {
                    await Log(new LogMessage(LogSeverity.Warning, "Gateway", "Detected disconnected state, attempting to reconnect...")).ConfigureAwait(false);
                    await TryRestartAsync().ConfigureAwait(false);
                }
            }
        }
    }
}