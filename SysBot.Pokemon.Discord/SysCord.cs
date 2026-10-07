using Discord;
using Discord.Commands;
using Discord.Rest;
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
        public static DiscordSettings? Settings => Manager.Config;
        public static PokeRaidHubConfig? HubConfig { get; internal set; } = default!;
    }

    public sealed class SysCord<T> where T : PKM, new()
    {
        public static PokeBotRunner<T>? Runner { get; private set; } = default!;
        public static RestApplication? App { get; private set; } = default!;

        public static SysCord<T>? Instance { get; private set; }
        public static ReactionService? ReactionService { get; private set; }
        private readonly DiscordSocketClient _client;
        private readonly DiscordManager Manager;
        public readonly PokeRaidHub<T> Hub;
        private const int MaxReconnectDelay = 60000; // 1 minute
        private int _reconnectAttempts = 0;
        // Keep the CommandService and DI container around for use with commands.
        // These two types require you install the Discord.Net.Commands package.
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
            SysCordSettings.HubConfig = Hub.Config;

            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Info,
                GatewayIntents = GatewayIntents.Guilds
                       | GatewayIntents.GuildMessages
                       | GatewayIntents.DirectMessages
                       | GatewayIntents.MessageContent
                       | GatewayIntents.GuildMessageReactions
                       | GatewayIntents.GuildMembers,
                MessageCacheSize = 500,
                AlwaysDownloadUsers = true,
                ConnectionTimeout = 30000,
            });
            _client.Disconnected += (ex) => HandleDisconnect(ex);

            _commands = new CommandService(new CommandServiceConfig
            {
                // Again, log level:
                LogLevel = LogSeverity.Info,

                // This makes commands get run on the task thread pool instead on the websocket read thread.
                // This ensures long running logic can't block the websocket connection.
                DefaultRunMode = Hub.Config.Discord.AsyncCommands ? RunMode.Async : RunMode.Sync,

                // There's a few more properties you can set,
                // for example, case-insensitive commands.
                CaseSensitiveCommands = false,
            });

            // Subscribe the logging handler to both the client and the CommandService.
            _client.Log += Log;
            _commands.Log += Log;

            // Setup your DI container.
            _services = ConfigureServices();
            Instance = this;
            ReactionService = new ReactionService(_client);
        }

        public DiscordSocketClient GetClient()
        {
            return _client;
        }

        // If any services require the client, or the CommandService, or something else you keep on hand,
        // pass them as parameters into this method as needed.
        // If this method is getting pretty long, you can separate it out into another file using partials.
        private static IServiceProvider ConfigureServices()
        {
            var map = new ServiceCollection();//.AddSingleton(new SomeServiceClass());

            // When all your required services are in the collection, build the container.
            // Tip: There's an overload taking in a 'validateScopes' bool to make sure
            // you haven't made any mistakes in your dependency graph.
            return map.BuildServiceProvider();
        }

        // Example of a logging handler. This can be reused by add-ons
        // that ask for a Func<LogMessage, Task>.

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

        private Task HandleDisconnect(Exception ex)
        {
            if (ex is GatewayReconnectException)
            {
                // Discord is telling us to reconnect, so we don't need to handle it ourselves
                return Task.CompletedTask;
            }

            // Log the disconnection
            Log(new LogMessage(LogSeverity.Warning, "Gateway", $"Disconnected: {ex.Message}"));

            // Use Task.Run to avoid blocking
            Task.Run(async () =>
            {
                var delay = Math.Min(MaxReconnectDelay, 1000 * Math.Pow(2, _reconnectAttempts));
                await Task.Delay((int)delay);

                try
                {
                    await _client.StartAsync();
                    _reconnectAttempts = 0;
                    Log(new LogMessage(LogSeverity.Info, "Gateway", "Reconnected successfully"));
                }
                catch (Exception reconnectEx)
                {
                    _reconnectAttempts++;
                    Log(new LogMessage(LogSeverity.Error, "Gateway", $"Failed to reconnect: {reconnectEx.Message}"));
                }
            });

            return Task.CompletedTask;
        }

        public async Task MainAsync(string apiToken, CancellationToken token)
        {
            // Centralize the logic for commands into a separate method.
            await InitCommands().ConfigureAwait(false);

            // Login and connect.
            await _client.LoginAsync(TokenType.Bot, apiToken).ConfigureAwait(false);
            await _client.StartAsync().ConfigureAwait(false);

            var app = await _client.GetApplicationInfoAsync().ConfigureAwait(false);
            Manager.Owner = app.Owner.Id;
            App = app;

            EchoUtil.OwnerForwarders.Clear();
            EchoUtil.OwnerForwarders.Add(msg => app.Owner.SendMessageAsync(msg));

            // Start the connection status check
            _ = CheckConnectionStatus(token);

            // Wait infinitely so your bot actually stays connected.
            await MonitorStatusAsync(token).ConfigureAwait(false);
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
            var modules = _commands.Modules.ToList();

            foreach (var module in modules)
            {
                var name = module.Name;
                name = name.Replace("Module", "");
                var gen = name.IndexOf('`');
                if (gen != -1)
                    name = name[..gen];
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
                                Log(new LogMessage(LogSeverity.Info, "RaidEmbed", $"Added green check reaction to message {messageId} in channel {channelId}"));
                            }
                            else
                            {
                                await message.AddReactionAsync(new Emoji("🚫"));
                                Log(new LogMessage(LogSeverity.Info, "RaidEmbed", $"Added red X reaction to message {messageId} in channel {channelId}"));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(new LogMessage(LogSeverity.Error, "RaidEmbed", $"Error updating reactions: {ex.Message}"));
                }
            };

            // Subscribe a handler to see if a message invokes a command.
            _client.Ready += LoadLoggingAndEcho;
            _client.MessageReceived += HandleMessageAsync;
            _client.ReactionAdded += HandleReactionAddedAsync;
            _client.ReactionAdded += ExtraCommandUtil<T>.HandleReactionAsync;
        }

        private async Task HandleReactionAddedAsync(Cacheable<IUserMessage, ulong> cachedMessage,
            Cacheable<IMessageChannel, ulong> originChannel, SocketReaction reaction)
        {
            // Ignore reactions from bots (including our own)
            if (reaction.User.Value.IsBot)
                return;
            if (reaction.Emote.Name != "✅")
                return;
            if (!SharedRaidCodeHandler.IsActiveRaidMessage(reaction.MessageId))
                return;

            var code = SharedRaidCodeHandler.GetRaidCodeForMessage(reaction.MessageId);
            if (string.IsNullOrEmpty(code))
                return;
            var raidInfoDict = SharedRaidCodeHandler.GetRaidInfoDict(reaction.MessageId);
            try
            {
                var dmChannel = await reaction.User.Value.CreateDMChannelAsync();
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
                    Log(new LogMessage(LogSeverity.Warning, "RaidCode", $"Failed to remove reaction: {ex.Message}"));
                }
            }
            catch (Exception ex)
            {
                Log(new LogMessage(LogSeverity.Error, "RaidCode", $"Failed to send raid code DM: {ex.Message}"));
            }
        }

        private async Task HandleMessageAsync(SocketMessage arg)
        {
            // Bail out if it's a System Message.
            if (arg is not SocketUserMessage msg)
                return;

            // We don't want the bot to respond to itself or other bots.
            if (msg.Author.Id == _client.CurrentUser.Id || msg.Author.IsBot)
                return;

            // Create a number to track where the prefix ends and the command begins
            int pos = 0;
            if (msg.HasStringPrefix(Hub.Config.Discord.CommandPrefix, ref pos))
            {
                bool handled = await TryHandleCommandAsync(msg, pos).ConfigureAwait(false);
                if (handled)
                    return;
            }
        }

        private async Task<bool> TryHandleCommandAsync(SocketUserMessage msg, int pos)
        {
            // Create a Command Context.
            var context = new SocketCommandContext(_client, msg);

            // Check Permission
            var mgr = Manager;
            if (!mgr.CanUseCommandUser(msg.Author.Id))
            {
                await msg.Channel.SendMessageAsync("You are not permitted to use this command.").ConfigureAwait(false);
                return true;
            }
            if (!mgr.CanUseCommandChannel(msg.Channel.Id) && msg.Author.Id != mgr.Owner)
            {
                await msg.Channel.SendMessageAsync("You can't use that command here.").ConfigureAwait(false);
                return true;
            }

            // Execute the command. (result does not indicate a return value,
            // rather an object stating if the command executed successfully).
            var guild = msg.Channel is SocketGuildChannel g ? g.Guild.Name : "Unknown Guild";
            await Log(new LogMessage(LogSeverity.Info, "Command", $"Executing command from {guild}#{msg.Channel.Name}:@{msg.Author.Username}. Content: {msg}")).ConfigureAwait(false);
            var result = await _commands.ExecuteAsync(context, pos, _services).ConfigureAwait(false);

            if (result.Error == CommandError.UnknownCommand)
                return false;

            // Uncomment the following lines if you want the bot
            // to send a message if it failed.
            // This does not catch errors from commands with 'RunMode.Async',
            // subscribe a handler for '_commands.CommandExecuted' to see those.
            if (!result.IsSuccess)
                await msg.Channel.SendMessageAsync(result.ErrorReason).ConfigureAwait(false);
            return true;
        }

        private async Task MonitorStatusAsync(CancellationToken token)
        {
            UserStatus state = UserStatus.Idle;
            while (!token.IsCancellationRequested)
            {
                var active = UserStatus.Online;
                if (active != state)
                {
                    state = active;
                    await _client.SetStatusAsync(state).ConfigureAwait(false);
                }
                await Task.Delay(20_000, token).ConfigureAwait(false);
            }
        }

        private async Task LoadLoggingAndEcho()
        {
            if (MessageChannelsLoaded)
                return;

            // Restore Echoes
            EchoModule.RestoreChannels(_client, Hub.Config.Discord);

            // Restore Logging
            LogModule.RestoreLogging(_client, Hub.Config.Discord);

            // Don't let it load more than once in case of Discord hiccups.
            await Log(new LogMessage(LogSeverity.Info, "LoadLoggingAndEcho()", "Logging and Echo channels loaded!")).ConfigureAwait(false);
            MessageChannelsLoaded = true;

            var game = Hub.Config.Discord.BotGameStatus;
            if (!string.IsNullOrWhiteSpace(game))
                await _client.SetGameAsync(game).ConfigureAwait(false);
        }

        private async Task CheckConnectionStatus(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_client.ConnectionState == ConnectionState.Disconnected)
                {
                    Log(new LogMessage(LogSeverity.Warning, "Gateway", "Detected disconnected state, attempting to reconnect..."));
                    try
                    {
                        await _client.StartAsync();
                        Log(new LogMessage(LogSeverity.Info, "Gateway", "Reconnected successfully"));
                    }
                    catch (Exception ex)
                    {
                        Log(new LogMessage(LogSeverity.Error, "Gateway", $"Failed to reconnect: {ex.Message}"));
                    }
                }
                await Task.Delay(60000, token); // Check every minute
            }
        }
    }
}