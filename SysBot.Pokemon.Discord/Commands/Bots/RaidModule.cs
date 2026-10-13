using Discord;
using Discord.Commands;
using Discord.WebSocket;
using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon.Discord.Helpers;
using SysBot.Pokemon.SV.BotRaid;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Pokemon.RotatingRaidSettingsSV;
using static SysBot.Pokemon.SV.BotRaid.RotatingRaidBotSV;

namespace SysBot.Pokemon.Discord.Commands.Bots
{
    [Summary("Requests, lists and manages raids.")]
    public class RaidModule<T> : ModuleBase<SocketCommandContext> where T : PKM, new()
    {
        private readonly PokeRaidHub<T> Hub = SysCord<T>.Runner.Hub;

        private List<RotatingRaidParameters> RaidList => Hub.Config.RotatingRaidSV.ActiveRaids;

        private const string WebRequestLocked = "That is a GenPKM member's request. It finishes or times out on its own, and the member is kept up to date on the site.";

        // Same title RotatingRaidBotSV gives the raids it rolls itself.
        private const int MinutesPerRaid = 6;
        private const int MaxInvitedUsers = 3;

        private bool IsHosting => Hub.Bots.CountWhere(b => b.Config.CurrentRoutineType != PokeRoutineType.Idle) > 0;

        private sealed record RaidBuild(PK9 Pokemon, Embed Embed, TeraCrystalType Crystal, int GroupId, bool IsEvent);

        /// <summary>
        /// Takes a raid out of the list. While a bot is hosting, the raid is only
        /// marked and the hosting loop removes it between raids, so the raid it is
        /// tracking by position never shifts under it.
        /// </summary>
        private void RemoveRaid(RotatingRaidParameters raid)
        {
            lock (RaidListSync.Gate)
            {
                if (IsHosting)
                {
                    raid.ActiveInRotation = false;
                    raid.PendingRemoval = true;
                }
                else
                {
                    RaidList.Remove(raid);
                }
            }
        }

        // Without Manage Messages, or in a DM, or after someone else deleted it first.
        private static async Task TryDeleteAsync(IMessage message)
        {
            try
            {
                await message.DeleteAsync().ConfigureAwait(false);
            }
            catch (global::Discord.Net.HttpException)
            {
            }
        }

        private Task TryDeleteCommandMessage() => TryDeleteAsync(Context.Message);

        private static TeraRaidMapParent CurrentMap() => IsBlueberry ? TeraRaidMapParent.Blueberry : (IsKitakami ? TeraRaidMapParent.Kitakami : TeraRaidMapParent.Paldea);

        /// <summary>
        /// Checks a seed request and rolls the raid it describes. Replies with the problem and returns null when it can't be raided.
        /// </summary>
        private async Task<RaidBuild?> BuildRaidAsync(string seed, int level, int storyProgressLevel, string? speciesName, int queuePosition, bool requireSpeciesFor7)
        {
            if (!WebRaidRules.IsValidSeed(seed))
            {
                await ReplyAsync("Invalid seed format. Please enter a seed of exactly 8 hexadecimal digits.").ConfigureAwait(false);
                return null;
            }
            if (RaidRequestRules.CheckLevel(level, storyProgressLevel) is { } problem)
            {
                await ReplyAsync(problem).ConfigureAwait(false);
                return null;
            }

            // The raid bot swaps in a new map when events change; read it once.
            var events = SpeciesToGroupIDMap;
            bool isEvent = !string.IsNullOrEmpty(speciesName);
            if (requireSpeciesFor7 && level == 7 && storyProgressLevel == 6 && !isEvent)
            {
                await ReplyAsync($"For 7★ raids, please specify the species name. Available species: {string.Join(", ", events.Keys)}").ConfigureAwait(false);
                return null;
            }

            var map = CurrentMap();
            if (isEvent && map != TeraRaidMapParent.Paldea)
            {
                await ReplyAsync("Events can only be run in the Paldea map.").ConfigureAwait(false);
                return null;
            }

            int groupId = -1;
            var crystal = WebRaidRules.CrystalFor(level, isEvent);
            if (isEvent)
            {
                if (!events.TryGetValue(speciesName!, out var groups) || groups.Count == 0)
                {
                    await ReplyAsync("Species name not recognized or not associated with an active event. Please check the name and try again.").ConfigureAwait(false);
                    return null;
                }
                groupId = groups[0].GroupID;

                // Might events are 7★ and Distribution events are 1-5★; the event decides, not the level typed.
                if (EventCrystalFor(speciesName!, level) is not { } eventCrystal)
                {
                    string kind = EventCrystalFor(speciesName!, 7) is not null ? "a 7★ Might raid" : "a 1-5★ event raid";
                    await ReplyAsync($"The {speciesName} event is {kind}, not {level}★. Please request it at that difficulty.").ConfigureAwait(false);
                    return null;
                }
                crystal = eventCrystal;
            }

            var toggles = Hub.Config.RotatingRaidSV.EmbedToggles;
            try
            {
                var (pk, embed) = RaidInfoCommand(seed, (int)crystal, map, storyProgressLevel, groupId, toggles.RewardsToShow,
                    toggles.MoveTypeEmojis, toggles.CustomTypeEmojis, queuePosition, isEvent, (int)toggles.EmbedLanguage);
                return new RaidBuild(pk, embed, crystal, groupId, isEvent);
            }
            catch (Exception ex)
            {
                LogUtil.LogSafe(ex, nameof(RaidModule<T>));
                await ReplyAsync($"I couldn't build a raid from that seed: {ex.Message}. Check the seed, difficulty and story progress, then try again.").ConfigureAwait(false);
                return null;
            }
        }

        private static RotatingRaidParameters NewRaid(RaidBuild raid, string seed, int level, int storyProgressLevel)
        {
            var descriptionPath = Path.Combine("raidfilessv", "bodyparam.txt");
            return new RotatingRaidParameters
            {
                CrystalType = raid.Crystal,
                DifficultyLevel = level,
                Description = [File.Exists(descriptionPath) ? File.ReadAllText(descriptionPath) : string.Empty],
                // A Ditto raid copies the party lead, so it gets a harmless one.
                PartyPK = raid.Pokemon.Species == (ushort)Species.Ditto ? ["Happiny", "Shiny: Yes", "Level: 1"] : [""],
                Species = (Species)raid.Pokemon.Species,
                SpeciesForm = raid.Pokemon.Form,
                StoryProgress = (GameProgressEnum)RaidRequestRules.ToGameProgress(storyProgressLevel),
                Seed = seed,
                IsCoded = true,
                IsShiny = raid.Pokemon.IsShiny,
            };
        }

        [Command("raidinfo")]
        [Alias("ri", "rv")]
        [Summary("Displays basic Raid Info of the provided seed.")]
        public async Task RaidSeedInfoAsync(
            string seedValue,
            int level,
            int storyProgressLevel = 6,
            string? speciesName = null)
        {
            var raid = await BuildRaidAsync(seedValue, level, storyProgressLevel, speciesName, 0, requireSpeciesFor7: true).ConfigureAwait(false);
            if (raid is null)
                return;

            var instructionMessage = await ReplyAsync("React with ✅ to add the raid to the queue.").ConfigureAwait(false);
            var message = await ReplyAsync(embed: raid.Embed).ConfigureAwait(false);
            var checkmarkEmoji = new Emoji("✅");
            await message.AddReactionAsync(checkmarkEmoji).ConfigureAwait(false);

            SysCord<T>.ReactionService.AddReactionHandler(message.Id, async (reaction) =>
            {
                if (reaction.UserId != Context.User.Id || reaction.Emote.Name != checkmarkEmoji.Name)
                    return;
                // A double tap delivers two reactions; only the first one queues the raid.
                if (!SysCord<T>.ReactionService.RemoveReactionHandler(reaction.MessageId))
                    return;

                // The ✅ skips the command pipeline, so the ra role check runs here.
                var access = RequireRoleAccessAttribute.Check(Context.User, nameof(DiscordManager.RolesRaid));
                if (!access.IsSuccess)
                {
                    await ReplyAsync(access.ErrorReason).ConfigureAwait(false);
                    return;
                }
                await AddNewRaidParamNext(seedValue, level, storyProgressLevel, speciesName).ConfigureAwait(false);
            });
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
                SysCord<T>.ReactionService.RemoveReactionHandler(message.Id);
                await TryDeleteAsync(message).ConfigureAwait(false);
                await TryDeleteAsync(instructionMessage).ConfigureAwait(false);
            });
            await TryDeleteCommandMessage().ConfigureAwait(false);
        }

        [Command("banOT")]
        [Alias("ban")]
        [RequireSudo]
        [Summary("Bans a user with the specified OT from participating in raids.")]
        public async Task BanUserAsync(string ot)
        {
            // Load the player data from the file.
            var baseDirectory = AppContext.BaseDirectory;
            var storage = new PlayerDataStorage(baseDirectory);
            var playerData = storage.LoadPlayerData();

            // Filter out player data that matches the OT.
            var matchedPlayers = playerData.Where(pd => pd.Value.OT.Equals(ot, StringComparison.OrdinalIgnoreCase)).ToList();

            // Check if there are duplicates.
            if (matchedPlayers.Count > 1)
            {
                await ReplyAsync($"Multiple players with OT '{ot}' found. Ban skipped. Please review manually.");
                return;
            }

            // If no player is found, notify and return.
            if (matchedPlayers.Count == 0)
            {
                await ReplyAsync($"No player with OT '{ot}' found.");
                return;
            }

            // Get the player's NID to ban.
            var playerToBan = matchedPlayers.First();
            ulong nidToBan = playerToBan.Key;

            // Check if the NID is already in the ban list.
            if (Hub.Config.RotatingRaidSV.RaiderBanList.Contains(nidToBan))
            {
                await ReplyAsync($"Player with OT '{ot}' is already banned.");
                return;
            }

            Hub.Config.RotatingRaidSV.RaiderBanList.AddIfNew(new[] { GetReference(nidToBan, ot, "") });

            await ReplyAsync($"Player with OT '{ot}' has been banned.");
        }

        [Command("banNID")]
        [Alias("ban")]
        [RequireSudo]
        [Summary("Bans a user with the specified NID from participating in raids.")]
        public async Task BanUserAsync(ulong nid, [Remainder] string comment)
        {
            var ot = string.Empty;
            try
            {
                var baseDirectory = AppContext.BaseDirectory;
                var storage = new PlayerDataStorage(baseDirectory);
                var playerData = storage.LoadPlayerData();
                var matchedNID = playerData.Where(pd => pd.Key.Equals(nid));
                ot = matchedNID.First().Value.OT;
            }
            catch
            {
                ot = "Unknown";
            }

            Hub.Config.RotatingRaidSV.RaiderBanList.AddIfNew(new[] { GetReference(nid, ot, comment) });
            await ReplyAsync("Done.").ConfigureAwait(false);
        }

        [Command("limitrequests")]
        [Alias("lr")]
        [Summary("Sets the limit on the number of requests a user can make.")]
        [RequireSudo]
        public async Task SetLimitRequestsAsync([Summary("The new limit for requests. Set to 0 to disable.")] int newLimit)
        {
            var settings = Hub.Config.RotatingRaidSV.RaidSettings;
            settings.LimitRequests = newLimit;

            await ReplyAsync($"LimitRequests updated to {newLimit}.").ConfigureAwait(false);
        }

        [Command("limitrequeststime")]
        [Alias("lrt")]
        [Summary("Sets the time users must wait once their request limit is reached.")]
        [RequireSudo]
        public async Task SetLimitRequestsTimeAsync([Summary("The new time in minutes. Set to 0 to disable.")] int newTime)
        {
            var settings = Hub.Config.RotatingRaidSV.RaidSettings;
            settings.LimitRequestsTime = newTime;

            await ReplyAsync($"LimitRequestsTime updated to {newTime} minutes.").ConfigureAwait(false);
        }

        [Command("addlimitbypass")]
        [Alias("alb")]
        [Summary("Adds a user or role to the bypass list for request limits.")]
        [RequireSudo]
        [RequireContext(ContextType.Guild)]
        public async Task AddBypassLimitAsync([Remainder] string mention)
        {
            string type;
            string nameToAdd;
            if (MentionUtils.TryParseUser(mention, out ulong idToAdd))
            {
                // The mentioned user comes with the message even when the member list isn't cached.
                var user = (IUser?)Context.Guild.GetUser(idToAdd) ?? Context.MentionedUsersExceptBot().FirstOrDefault(u => u.Id == idToAdd);
                nameToAdd = user?.Username ?? "Unknown User";
                type = "User";
            }
            else if (MentionUtils.TryParseRole(mention, out idToAdd))
            {
                var role = Context.Guild.GetRole(idToAdd);
                nameToAdd = role?.Name ?? "Unknown Role";
                type = "Role";
            }
            else
            {
                await ReplyAsync("Invalid user or role.").ConfigureAwait(false);
                return;
            }

            if (Hub.Config.RotatingRaidSV.RaidSettings.BypassLimitRequests.TryAdd(idToAdd, nameToAdd))
                await ReplyAsync($"Added {type} '{nameToAdd}' to bypass list.").ConfigureAwait(false);
            else
                await ReplyAsync($"{type} '{nameToAdd}' is already in the bypass list.").ConfigureAwait(false);
        }

        [Command("repeek")]
        [Summary("Take and send a screenshot from the currently configured Switch.")]
        [RequireOwner]
        public async Task RePeek()
        {
            var bot = SysCord<T>.Runner.Bots.FirstOrDefault();
            if (bot == null)
            {
                await ReplyAsync("No bot is configured.").ConfigureAwait(false);
                return;
            }

            byte[] bytes;
            try
            {
                bytes = await bot.Bot.Connection.PixelPeek(CancellationToken.None).ConfigureAwait(false) ?? [];
            }
            catch (Exception ex)
            {
                await ReplyAsync($"Error while fetching pixels: {ex.Message}");
                return;
            }

            if (bytes.Length == 0)
            {
                await ReplyAsync("No screenshot data received.");
                return;
            }

            using MemoryStream ms = new(bytes);
            var img = "cap.jpg";
            var embed = new EmbedBuilder { ImageUrl = $"attachment://{img}", Color = Color.Purple }
                .WithFooter(new EmbedFooterBuilder { Text = $"Here's your screenshot." });

            await Context.Channel.SendFileAsync(ms, img, embed: embed.Build());
        }

        [Command("addRaidParams")]
        [Alias("arp")]
        [Summary("Adds new raid parameter.")]
        [RequireSudo]
        public async Task AddNewRaidParam(
            [Summary("Seed")] string seed,
            [Summary("Difficulty Level (1-7)")] int level,
            [Summary("Story Progress Level")] int storyProgressLevel = 6,
            [Summary("Species Name (Optional)")] string? speciesName = null)
        {
            var raid = await BuildRaidAsync(seed, level, storyProgressLevel, speciesName, 0, requireSpeciesFor7: false).ConfigureAwait(false);
            if (raid is null)
                return;

            var newparam = NewRaid(raid, seed, level, storyProgressLevel);
            newparam.AddedByRACommand = false;
            newparam.Title = $"{(Species)raid.Pokemon.Species}";
            newparam.GroupID = raid.GroupId != -1 ? raid.GroupId : null;

            lock (RaidListSync.Gate)
                RaidList.Add(newparam);
            await TryDeleteCommandMessage().ConfigureAwait(false);
            await ReplyAsync("Your new raid has been added.", embed: raid.Embed).ConfigureAwait(false);
        }

        [Command("listevents")]
        [Alias("le")]
        [Summary("Lists all found event raids stored in the SpeciesToGroupID dictionary.")]
        [RequireSudo]
        public async Task ListEventRaids()
        {
            var events = SpeciesToGroupIDMap;
            if (events == null || events.Count == 0)
            {
                await ReplyAsync("No raids found in the SpeciesToGroupIDMap.").ConfigureAwait(false);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("**Found Raids:**");

            foreach (var (speciesName, groupIDAndIndices) in events)
            {
                sb.AppendLine($"\n**{speciesName}:**");

                foreach (var (GroupID, Index, DenIdentifier) in groupIDAndIndices)
                {
                    sb.AppendLine($"- GroupID: `{GroupID}`, Index: `{Index}`, DenIdentifier: `{DenIdentifier}`");
                }
            }

            if (sb.Length > 2000)
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
                await Context.Channel.SendFileAsync(stream, "RaidsList.txt", "The list is too long, so I've attached it as a file.").ConfigureAwait(false);
            }
            else
            {
                await ReplyAsync(sb.ToString()).ConfigureAwait(false);
            }
        }

        [Command("addUserRaid")]
        [Alias("aur", "ra")]
        [Summary("Adds new raid parameter next in the queue.")]
        [RequireQueueRole(nameof(DiscordManager.RolesRaid))]
        public async Task AddNewRaidParamNext(
            [Summary("Seed")] string seed,
            [Summary("Difficulty Level (1-7)")] int level,
            [Summary("Story Progress Level")] int storyProgressLevel = 6,
            [Summary("Species Name or User Mention (Optional)")] string? speciesNameOrUserMention = null,
            [Summary("Up to 3 users to invite to a private raid (Optional)")][Remainder] string? invites = null)
        {
            var raidSettings = Hub.Config.RotatingRaidSV.RaidSettings;
            if (raidSettings.DisableRequests)
            {
                await ReplyAsync("Raid Requests are currently disabled by the host.").ConfigureAwait(false);
                return;
            }
            if (RaidRequestRules.CheckLevel(level, storyProgressLevel) is { } problem)
            {
                await ReplyAsync(problem).ConfigureAwait(false);
                return;
            }

            bool startsWithMention = speciesNameOrUserMention != null && MentionUtils.TryParseUser(speciesNameOrUserMention, out _);
            string? speciesName = startsWithMention ? null : speciesNameOrUserMention;

            // Only mentions typed in the arguments count: a reply-with-ping also lands in
            // MentionedUsers. They come with the message, so uncached members still resolve.
            var typedIds = $"{speciesNameOrUserMention} {invites}"
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => MentionUtils.TryParseUser(word, out var id) ? id : 0)
                .Where(id => id != 0)
                .ToHashSet();
            var invited = Context.MentionedUsersExceptBot()
                .Where(u => typedIds.Contains(u.Id) && u.Id != Context.User.Id && !u.IsBot)
                .ToList();
            if (startsWithMention && invited.Count == 0)
            {
                await ReplyAsync("I couldn't find anyone to invite in that message. Mention the users you want in your private raid.").ConfigureAwait(false);
                return;
            }
            if (invited.Count > 0 && !raidSettings.PrivateRaidsEnabled)
            {
                await ReplyAsync("Private raids are currently disabled by the host.").ConfigureAwait(false);
                return;
            }
            if (invited.Count > MaxInvitedUsers)
            {
                await ReplyAsync($"You can only mention up to {MaxInvitedUsers} users for a private raid.").ConfigureAwait(false);
                return;
            }

            var userId = Context.User.Id;
            if (RaidListSync.Snapshot(RaidList).Any(r => r.RequestedByUserID == userId && !r.PendingRemoval))
            {
                await ReplyAsync("You already have an existing raid request in the queue.").ConfigureAwait(false);
                return;
            }

            var userRequestManager = new UserRequestManager();
            var userRoles = (Context.User as SocketGuildUser)?.Roles.Select(r => r.Id) ?? [];
            bool limited = !raidSettings.BypassLimitRequests.ContainsKey(userId) &&
                !userRoles.Any(raidSettings.BypassLimitRequests.ContainsKey);

            if (limited && !userRequestManager.CanRequest(userId, raidSettings.LimitRequests, raidSettings.LimitRequestsTime, out var remainingCooldown))
            {
                string responseMessage = $"You have reached your request limit. Please wait {remainingCooldown.TotalMinutes:N0} minutes before making another request.";
                if (!string.IsNullOrWhiteSpace(raidSettings.LimitRequestMsg))
                    responseMessage += $"\n{raidSettings.LimitRequestMsg}";

                await ReplyAsync(responseMessage).ConfigureAwait(false);
                return;
            }

            var raid = await BuildRaidAsync(seed, level, storyProgressLevel, speciesName, 1, requireSpeciesFor7: true).ConfigureAwait(false);
            if (raid is null)
                return;

            var botPrefix = SysCordSettings.MentionOnly ? $"{Context.Client.CurrentUser.Mention} " : SysCordSettings.Settings.CommandPrefix;
            var newparam = NewRaid(raid, seed, level, storyProgressLevel);
            newparam.GroupID = raid.GroupId;
            newparam.AddedByRACommand = true;
            newparam.RequestCommand = $"{botPrefix}ra {seed} {level} {storyProgressLevel}{(raid.IsEvent ? $" {speciesName}" : "")}";
            newparam.RequestedByUserID = userId;
            newparam.Title = $"{Context.User.Username}'s Requested Raid{(raid.IsEvent ? $" ({speciesName} Event Raid)" : "")}";
            newparam.RaidUpNext = false;
            newparam.User = Context.User;
            newparam.MentionedUsers = invited;

            bool alreadyQueued;
            lock (RaidListSync.Gate)
            {
                alreadyQueued = RaidList.Any(r => r.RequestedByUserID == userId && !r.PendingRemoval);
                if (!alreadyQueued)
                {
                    // After the raid being hosted, behind any requests already waiting there
                    int insertPosition = Math.Min(RotationCount + 1, RaidList.Count);
                    while (insertPosition < RaidList.Count && RaidList[insertPosition].AddedByRACommand)
                        insertPosition++;
                    newparam.RaidUpNext = insertPosition == RotationCount + 1;
                    RaidList.Insert(insertPosition, newparam);
                }
            }
            if (alreadyQueued)
            {
                await ReplyAsync("You already have an existing raid request in the queue.").ConfigureAwait(false);
                return;
            }

            if (limited)
                userRequestManager.RecordRequest(userId, raidSettings.LimitRequests, raidSettings.LimitRequestsTime);

            int queuePosition = CalculateEffectiveQueuePosition(userId, RotationCount);
            var queuePositionMessage = queuePosition > 0
                ? $"You are currently {queuePosition} in the queue with an estimated wait time of {queuePosition * MinutesPerRaid} minutes."
                : "Your raid request is up next!";

            await ReplyAsync($"{Context.User.Mention}, added your raid to the queue! I'll DM you when it's about to start.", embed: raid.Embed).ConfigureAwait(false);

            foreach (var user in invited)
            {
                try
                {
                    await user.SendMessageAsync($"{Context.User.Username} invited you to a private raid! I'll DM you the code when it's about to start.", false, raid.Embed).ConfigureAwait(false);
                }
                catch
                {
                    await ReplyAsync($"Failed to send DM to {user.Mention}. Please make sure their DMs are open.").ConfigureAwait(false);
                }
            }
            try
            {
                await Context.User.SendMessageAsync($"Here's your raid information:\n{queuePositionMessage}\nYour request command: `{newparam.RequestCommand}`", false, raid.Embed).ConfigureAwait(false);
            }
            catch
            {
                await ReplyAsync("Failed to send DM. Please make sure your DMs are open.").ConfigureAwait(false);
            }
        }

        [Command("addRaidPK")]
        [Alias("rp")]
        [Summary("Adds provided showdown set Pokémon to the users Raid in Queue.")]
        public async Task AddRaidPK([Summary("Showdown Set")][Remainder] string content)
        {
            content = ReusableActions.StripCodeBlock(content);
            var set = new ShowdownSet(content);
            var template = AutoLegalityWrapper.GetTemplate(set);
            if (set.InvalidLines.Count != 0 || set.Species <= 0)
            {
                var msg = $"Unable to parse Showdown Set:\n{string.Join("\n", set.InvalidLines)}";
                await ReplyAsync(msg).ConfigureAwait(false);
                return;
            }

            try
            {
                var sav = AutoLegalityWrapper.GetTrainerInfo<T>();
                var pkm = sav.GetLegal(template, out var result);
                var la = new LegalityAnalysis(pkm);
                var spec = GameInfo.Strings.Species[template.Species];
                pkm = EntityConverter.ConvertToType(pkm, typeof(T), out _) ?? pkm;
                if (pkm is not T pk || !la.Valid)
                {
                    var reason = result == "Timeout" ? $"That {spec} set took too long to generate." : $"I wasn't able to create a {spec} from that set.";
                    var imsg = $"Oops! {reason}";
                    await ReplyAsync(imsg).ConfigureAwait(false);
                    return;
                }

                var userId = Context.User.Id;
                var raidToUpdate = RaidListSync.Snapshot(RaidList).FirstOrDefault(r => r.RequestedByUserID == userId && !r.PendingRemoval);
                string[] partyPK = content.Split('\n', StringSplitOptions.RemoveEmptyEntries); // Remove empty lines
                if (raidToUpdate != null)
                {
                    raidToUpdate.PartyPK = partyPK;
                    await TryDeleteCommandMessage().ConfigureAwait(false);
                    var embed = await RPEmbed.PokeEmbedAsync(pkm, Context.User.Username);
                    await ReplyAsync(embed: embed).ConfigureAwait(false);
                }
                else
                {
                    var msg = "You don't have a raid in queue!";
                    await ReplyAsync(msg).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogSafe(ex, nameof(RaidModule<T>));
                var msg = $"Oops! An unexpected problem happened with this Showdown Set:\n```{string.Join("\n", set.GetSetLines())}```";
                await ReplyAsync(msg).ConfigureAwait(false);
            }
        }

        [Command("addRaidPK")]
        [Alias("rp")]
        [Summary("Adds provided showdown set Pokémon to the users Raid in Queue.")]
        public async Task AddRaidPK()
        {
            var attachment = Context.Message.Attachments.FirstOrDefault();
            if (attachment == default)
            {
                await ReplyAsync("No attachment provided!").ConfigureAwait(false);
                return;
            }

            var att = await NetUtil.DownloadPKMAsync(attachment).ConfigureAwait(false);
            var pk = GetRequest(att);
            if (pk == null)
            {
                await ReplyAsync("Attachment provided is not compatible with this module!").ConfigureAwait(false);
                return;
            }

            var userId = Context.User.Id;
            var raidToUpdate = RaidListSync.Snapshot(RaidList).FirstOrDefault(r => r.RequestedByUserID == userId && !r.PendingRemoval);
            if (raidToUpdate == null)
            {
                await ReplyAsync("You don't have a raid in queue!").ConfigureAwait(false);
                return;
            }

            var set = ShowdownParsing.GetShowdownText(pk);
            raidToUpdate.PartyPK = set.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await TryDeleteCommandMessage().ConfigureAwait(false);
            var embed = await RPEmbed.PokeEmbedAsync(pk, Context.User.Username);
            await ReplyAsync(embed: embed).ConfigureAwait(false);
        }

        [Command("raidQueueStatus")]
        [Alias("rqs")]
        [Summary("Checks the number of raids before the user's request and gives an ETA.")]
        public async Task CheckQueueStatus()
        {
            var userId = Context.User.Id;
            var userRequestIndex = RaidListSync.Snapshot(RaidList).FindIndex(r => r.RequestedByUserID == userId && !r.PendingRemoval && !r.Title.Contains(MysteryRaidTitle));

            var embed = new EmbedBuilder { Title = "Queue Status" };
            if (userRequestIndex == -1)
            {
                embed.Color = Color.Red;
                embed.Description = $"{Context.User.Mention}, you do not have a raid request in the queue.";
            }
            else
            {
                int raidsBeforeUser = CalculateEffectiveQueuePosition(userId, RotationCount);
                if (raidsBeforeUser <= 0)
                {
                    embed.Color = Color.Green;
                    embed.Description = $"{Context.User.Mention}, your raid request is up next!";
                }
                else
                {
                    embed.Color = Color.Orange;
                    embed.Description = $"{Context.User.Mention}, here's the status of your raid request:";
                    embed.AddField("Raids Before Yours", raidsBeforeUser.ToString(), true);
                    embed.AddField("Estimated Time", $"{raidsBeforeUser * MinutesPerRaid} minutes", true);
                }
            }

            await TryDeleteCommandMessage().ConfigureAwait(false);
            await ReplyAsync(embed: embed.Build()).ConfigureAwait(false);
        }

        private int CalculateEffectiveQueuePosition(ulong userId, int currentPosition)
        {
            int effectivePosition = 0;
            bool userRequestFound = false;
            var raids = RaidListSync.Snapshot(RaidList).Where(r => !r.PendingRemoval).ToList();
            if (raids.Count == 0)
                return 0;
            currentPosition = Math.Clamp(currentPosition, 0, raids.Count - 1);

            for (int i = currentPosition; i < raids.Count + currentPosition; i++)
            {
                var raid = raids[i % raids.Count];
                if (raid.AddedByRACommand && !raid.Title.Contains(MysteryRaidTitle))
                {
                    if (raid.RequestedByUserID == userId)
                    {
                        userRequestFound = true;
                        break;
                    }
                    effectivePosition++;
                }
            }

            // If the user's request was not found after the current position, count from the beginning
            if (!userRequestFound)
            {
                for (int i = 0; i < currentPosition; i++)
                {
                    var raid = raids[i];
                    if (raid.AddedByRACommand && !raid.Title.Contains(MysteryRaidTitle))
                    {
                        if (raid.RequestedByUserID == userId)
                            break;
                        effectivePosition++;
                    }
                }
            }

            return effectivePosition;
        }

        [Command("raidQueueClear")]
        [Alias("rqc")]
        [Summary("Removes the raid added by the user.")]
        public async Task RemoveOwnRaidParam()
        {
            var userId = Context.User.Id;

            var userRaid = RaidListSync.Snapshot(RaidList).FirstOrDefault(r => r.RequestedByUserID == userId && r.AddedByRACommand && !r.PendingRemoval);
            if (userRaid == null)
            {
                await ReplyAsync("You don't have a raid added.").ConfigureAwait(false);
                return;
            }

            if (userRaid.RaidUpNext)
            {
                await ReplyAsync("Your raid request is up next and cannot be canceled at this time.").ConfigureAwait(false);
                return;
            }

            RemoveRaid(userRaid);
            await TryDeleteCommandMessage().ConfigureAwait(false);
            await ReplyAsync("Cleared your Raid from the queue.").ConfigureAwait(false);
        }

        /// <summary>
        /// The raid at <paramref name="index"/> when a sudo command may edit it; otherwise replies why not and returns null.
        /// </summary>
        private async Task<RotatingRaidParameters?> GetEditableRaidAsync(int index)
        {
            var list = RaidListSync.Snapshot(RaidList);
            if (index < 0 || index >= list.Count)
            {
                await ReplyAsync("Invalid raid parameter index.").ConfigureAwait(false);
                return null;
            }

            var raid = list[index];
            if (raid.WebRequest is not null)
            {
                await ReplyAsync(WebRequestLocked).ConfigureAwait(false);
                return null;
            }
            return raid;
        }

        [Command("removeRaidParams")]
        [Alias("rrp")]
        [Summary("Removes a raid parameter.")]
        [RequireSudo]
        public async Task RemoveRaidParam([Summary("Seed Index")] int index)
        {
            if (await GetEditableRaidAsync(index).ConfigureAwait(false) is not { } raid)
                return;

            RemoveRaid(raid);
            await ReplyAsync($"Raid for {raid.Title} | {raid.Seed:X8} has been removed!").ConfigureAwait(false);
        }

        [Command("toggleRaidParams")]
        [Alias("trp")]
        [Summary("Toggles raid parameter.")]
        [RequireSudo]
        public async Task ToggleRaidParam([Summary("Seed Index")] int index)
        {
            if (await GetEditableRaidAsync(index).ConfigureAwait(false) is not { } raid)
                return;

            // A request switched off would sit in the list forever and block its owner from asking again.
            if (raid.AddedByRACommand && raid.ActiveInRotation && !raid.Title.Contains(MysteryRaidTitle))
            {
                RemoveRaid(raid);
                await ReplyAsync($"Request {raid.Title} | {raid.Seed:X8} was canceled and removed from the queue.").ConfigureAwait(false);
                return;
            }
            raid.ActiveInRotation = !raid.ActiveInRotation;
            var m = raid.ActiveInRotation ? "enabled" : "disabled";
            await ReplyAsync($"Raid for {raid.Title} | {raid.Seed:X8} has been {m}!").ConfigureAwait(false);
        }

        [Command("togglecodeRaidParams")]
        [Alias("tcrp")]
        [Summary("Toggles code raid parameter.")]
        [RequireSudo]
        public async Task ToggleCodeRaidParam([Summary("Seed Index")] int index)
        {
            if (await GetEditableRaidAsync(index).ConfigureAwait(false) is not { } raid)
                return;

            raid.IsCoded = !raid.IsCoded;
            var m = raid.IsCoded ? "coded" : "uncoded";
            await ReplyAsync($"Raid for {raid.Title} | {raid.Seed:X8} is now {m}!").ConfigureAwait(false);
        }

        [Command("changeRaidParamTitle")]
        [Alias("crpt")]
        [Summary("Changes the title of a  raid parameter.")]
        [RequireSudo]
        public async Task ChangeRaidParamTitle([Summary("Seed Index")] int index, [Summary("Title")] string title)
        {
            if (await GetEditableRaidAsync(index).ConfigureAwait(false) is not { } raid)
                return;

            var oldTitle = raid.Title;
            raid.Title = title;
            await ReplyAsync($"Raid Title for {oldTitle} | {raid.Seed:X8} has been changed to: {title}!").ConfigureAwait(false);
        }

        [Command("viewraidList")]
        [Alias("vrl", "rotatinglist")]
        [Summary("Prints the raids in the current collection.")]
        public async Task GetRaidListAsync()
        {
            const string title = "Raid List";
            var list = RaidListSync.Snapshot(RaidList);
            var lines = list.Select((raid, i) => $"{i}.) {raid.Title} - {raid.Seed} - Status: {(raid.ActiveInRotation ? "Active" : "Inactive")}").ToList();
            var header = $"These are the raids currently in the list (total: {list.Count}):";

            var fields = RaidRequestRules.PackIntoFields(lines);
            static string FieldName(int i) => $"Raid List - Part {i + 1}";
            if (!RaidRequestRules.FitsInOneEmbed(fields, FieldName, title))
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
                await Context.Channel.SendFileAsync(stream, "RaidList.txt", $"{header} The list is too long for an embed, so I've attached it as a file.").ConfigureAwait(false);
                return;
            }

            var embed = new EmbedBuilder { Title = title };
            for (int i = 0; i < fields.Count; i++)
                embed.AddField(FieldName(i), fields[i], false);
            await ReplyAsync(header, embed: embed.Build()).ConfigureAwait(false);
        }

        [Command("toggleRaidPK")]
        [Alias("trpk")]
        [Summary("Toggles raid parameter.")]
        [RequireSudo]
        public async Task ToggleRaidParamPK([Summary("Seed Index")] int index, [Summary("Showdown Set")][Remainder] string content)
        {
            if (await GetEditableRaidAsync(index).ConfigureAwait(false) is not { } raid)
                return;

            raid.PartyPK = [content];
            await ReplyAsync($"RaidPK for {raid.Title} | {raid.Seed:X8} has been updated to:\n{content}!").ConfigureAwait(false);
        }

        [Command("raidhelp")]
        [Alias("rh")]
        [Summary("Prints the raid help command list.")]
        public async Task GetRaidHelpListAsync()
        {
            var bot = Context.Client.CurrentUser;
            string Use(string command) => SysCordSettings.CommandUsage(bot, command);
            var msg = string.Join("\n",
                $"{Use("ban <OT>")} or {Use("banNID <NID> <comment>")} - Ban a raider. Sudo only.",
                $"{Use("vrl")} - View all raids in the list.",
                $"{Use("arp <Seed> <Difficulty> <StoryProgress> [Species]")} - Add a raid to the collection.",
                $"{Use("rrp <Index>")} - Remove a raid from the collection.",
                $"{Use("trp <Index>")} - Toggle a raid Active/Inactive.",
                $"{Use("tcrp <Index>")} - Toggle a raid Coded/Uncoded.",
                $"{Use("trpk <Index> <ShowdownSet>")} - Set the PartyPK for a raid.",
                $"{Use("crpt <Index> <Title>")} - Set the title for a raid.");
            var embed = new EmbedBuilder().AddField("Raid Help Commands", msg, false);
            await ReplyAsync("Here's your raid help!", embed: embed.Build()).ConfigureAwait(false);
        }

        [Command("unbanrotatingraider")]
        [Alias("ubrr")]
        [Summary("Removes the specificed NID from the banlist for Raids in SV.")]
        [RequireSudo]
        public async Task UnbanRotatingRaider([Summary("Removes the specificed NID from the banlist for Raids in SV.")] string nid)
        {
            var banList = Hub.Config.RotatingRaidSV.RaiderBanList;
            if (!ulong.TryParse(nid, out var id) || banList.Find(id) is not { } banned)
            {
                await ReplyAsync($"{Context.User.Mention} no user found with that NID.").ConfigureAwait(false);
                return;
            }

            banList.RemoveAll(z => z.ID == id);
            await ReplyAsync($"{Context.User.Mention} user {banned.Name} - {banned.ID} has been unbanned.").ConfigureAwait(false);
        }

        private RemoteControlAccess GetReference(ulong id, string name, string comment) => new()
        {
            ID = id,
            Name = name,
            Comment = "Banned on " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" + $"({comment})"
        };

        private static T? GetRequest(Download<PKM> dl)
        {
            if (!dl.Success)
                return null;
            return dl.Data switch
            {
                null => null,
                T pk => pk,
                _ => EntityConverter.ConvertToType(dl.Data, typeof(T), out _) as T,
            };
        }
    }
}
