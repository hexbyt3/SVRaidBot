using Discord;
using Discord.Net;
using Discord.Rest;
using SysBot.Base;
using System;
using System.Threading.Tasks;
using static Discord.GatewayIntents;

namespace SysBot.Pokemon.Discord;

/// <summary>
/// Decides which gateway intents the bot asks for, and which gateway close codes are worth retrying.
/// Discord closes with 4014 when a privileged intent is requested that the app no longer has, and
/// every retry is a fresh IDENTIFY; past 1000 a day Discord resets the bot token.
/// </summary>
public static class GatewayIntentPolicy
{
    // Reactions carry the raid-code ✅ DMs and the raidinfo queue button.
    public const GatewayIntents Full = Guilds | GuildMessages | GuildMessageReactions | DirectMessages | MessageContent;
    public const GatewayIntents WithoutMessageContent = Full & ~MessageContent;

    private const ApplicationFlags MessageContentFlags =
        ApplicationFlags.GatewayMessageContent | ApplicationFlags.GatewayMessageContentLimited;

    // Close codes Discord documents as "Reconnect: false".
    private static readonly int[] FatalCloseCodes = [4004, 4010, 4011, 4012, 4013, 4014];

    public static GatewayIntents IntentsFor(ApplicationFlags flags)
        => (flags & MessageContentFlags) != 0 ? Full : WithoutMessageContent;

    public static int? GetCloseCode(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is WebSocketClosedException closed)
                return closed.CloseCode;
        }
        return null;
    }

    public static bool IsFatalClose(Exception? ex)
        => GetCloseCode(ex) is int code && Array.IndexOf(FatalCloseCodes, code) >= 0;

    public static string DescribeFatalClose(int code) => code switch
    {
        4004 => "Discord rejected the bot token (4004). Reset the token in the Discord Developer Portal, put the new one in the Discord settings, and restart.",
        4014 => "Discord refused a privileged intent (4014) even with Message Content already off. Check the intents in the Discord Developer Portal and restart.",
        _ => $"Discord closed the connection with code {code}, which cannot be fixed by retrying.",
    };

    /// <summary>
    /// Reads the app's flags over REST before the gateway connects, so a bot whose Message Content
    /// intent was revoked still comes online and answers @mentions and DMs.
    /// </summary>
    public static async Task<GatewayIntents> ResolveAsync(string token)
    {
        try
        {
            using var rest = new DiscordRestClient();
            await rest.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
            var app = await rest.GetApplicationInfoAsync().ConfigureAwait(false);
            var intents = IntentsFor(app.Flags);
            if (intents == WithoutMessageContent)
            {
                LogUtil.LogError("This bot does not have the Message Content intent in the Discord Developer Portal. " +
                    "Running in mention-only mode: commands work when the message @mentions the bot, and in DMs. " +
                    "To bring back prefix commands, get Message Content Intent approved again in the Discord Developer Portal (bots with 10,000+ users must apply for Privileged Intent review), then restart.", "Discord");
            }
            return intents;
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Could not read the bot's intents from Discord ({ex.Message}); requesting all of them.", "Discord");
            return Full;
        }
    }
}
