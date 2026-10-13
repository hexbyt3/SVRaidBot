using System.ComponentModel;
using static SysBot.Pokemon.RotatingRaidSettingsSV;

namespace SysBot.Pokemon
{
    public class DiscordSettings
    {
        private const string Startup = nameof(Startup);
        private const string Operation = nameof(Operation);
        private const string Channels = nameof(Channels);
        private const string Roles = nameof(Roles);
        private const string Users = nameof(Users);

        public override string ToString() => "Discord Integration Settings";

        // Startup

        [Category(Startup), Description("Bot login token.")]
        public string Token { get; set; } = string.Empty;

        [Category(Startup), Description("Bot command prefix. Without the Message Content intent (mention-only mode) the prefix works only in DMs; in servers, commands start with an @mention of the bot.")]
        public string CommandPrefix { get; set; } = "$";

        [Category(Startup), Description("Toggle to handle commands asynchronously or synchronously.")]
        public bool AsyncCommands { get; set; }

        [Category(Startup), Description("Custom Status for playing a game.")]
        public string BotGameStatus { get; set; } = "Hosting S/V Raids";

        [Category(Operation), Description("Custom message the bot will reply with when a user says hello to it. Use string formatting to mention the user in the reply.")]
        public string HelloResponse { get; set; } = "Hello, {0}!  I'm online!";

        // Whitelists
        private RemoteControlAccessList _roleRaidRequest = new();

        [Category(Roles), Description("Users with these roles are allowed to enter the Raid queue. Leave it empty to let everyone request raids.")]
        public RemoteControlAccessList RoleRaidRequest
        {
            get => _roleRaidRequest;
            set
            {
                value ??= new();
                _roleRaidRequest = value;
                // Empty means everyone; older configs saved this as false while nothing checked it.
                value.AllowIfEmpty = true;
            }
        }

        [Browsable(false)]
        [Category(Roles), Description("Users with this role are allowed to remotely control the console (if running as Remote Control Bot.")]
        public RemoteControlAccessList RoleRemoteControl { get; set; } = new() { AllowIfEmpty = false };

        [Category(Roles), Description("Users with this role are allowed to bypass command restrictions.")]
        public RemoteControlAccessList RoleSudo { get; set; } = new() { AllowIfEmpty = false };

        // Operation
        [Category(Users), Description("Users with these user IDs cannot use the bot.")]
        public RemoteControlAccessList UserBlacklist { get; set; } = new();

        [Category(Channels), Description("Channels with these IDs are the only channels where the bot acknowledges commands.")]
        public RemoteControlAccessList ChannelWhitelist { get; set; } = new();

        [Category(Users), Description("Comma separated Discord user IDs that will have sudo access to the Bot Hub.")]
        public RemoteControlAccessList GlobalSudoList { get; set; } = new();

        [Category(Users), Description("Disabling this will remove global sudo support.")]
        public bool AllowGlobalSudo { get; set; } = true;

        [Category(Channels), Description("Channel IDs that will echo the log bot data.")]
        public RemoteControlAccessList LoggingChannels { get; set; } = new();

        [Category(Channels), Description("Raid Embed Channels.")]
        public RemoteControlAccessList EchoChannels { get; set; } = new();

        public AnnouncementSettingsCategory AnnouncementSettings { get; set; } = new();

        [Category(Operation), TypeConverter(typeof(CategoryConverter<AnnouncementSettingsCategory>))]
        public class AnnouncementSettingsCategory
        {
            public override string ToString() => "Announcement Settings";

            [Category("Embed Settings"), Description("Thumbnail option for announcements.")]
            public ThumbnailOption AnnouncementThumbnailOption { get; set; } = ThumbnailOption.Gengar;

            [Category("Embed Settings"), Description("Custom thumbnail URL for announcements.")]
            public string CustomAnnouncementThumbnailUrl { get; set; } = string.Empty;

            public EmbedColorOption AnnouncementEmbedColor { get; set; } = EmbedColorOption.Blue;

            [Category("Embed Settings"), Description("Enable random thumbnail selection for announcements.")]
            public bool RandomAnnouncementThumbnail { get; set; } = false;

            [Category("Embed Settings"), Description("Enable random color selection for announcements.")]
            public bool RandomAnnouncementColor { get; set; } = false;
        }
    }
}