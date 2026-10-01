using Discord;
using Newtonsoft.Json;
using PKHeX.Core;
using RaidCrawler.Core.Structures;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.RotatingRaidSettingsSV;
using static SysBot.Pokemon.SV.BotRaid.Blocks;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using pkNX.Structures.FlatBuffers;
using SysBot.Pokemon.Helpers;
using SysBot.Pokemon.SV.BotRaid.Language;

namespace SysBot.Pokemon.SV.BotRaid
{
    /// <summary>
    /// Automated bot for hosting rotating raids in Pokemon Scarlet and Violet
    /// </summary>
    public class RotatingRaidBotSV(PokeBotState cfg, PokeRaidHub<PK9> hub) : PokeRoutineExecutor9SV(cfg)
    {
        private readonly PokeRaidHub<PK9> _hub = hub;
        private readonly RotatingRaidSettingsSV _settings = hub.Config.RotatingRaidSV;
        private RemoteControlAccessList RaiderBanList => _settings.RaiderBanList;

        // Store Event Data - map of species to group IDs
        public static Dictionary<string, List<(int GroupID, int Index, string DenIdentifier)>> SpeciesToGroupIDMap { get; set; } =
            new Dictionary<string, List<(int GroupID, int Index, string DenIdentifier)>>(StringComparer.OrdinalIgnoreCase);

        // Shared HTTP client for network operations
        private static readonly HttpClient _httpClient = new();

        /// <summary>
        /// Data structure for raid boss mechanics (shields, special moves, etc.)
        /// </summary>
        private record RaidBossMechanicsInfo
        {
            public byte ShieldHpTrigger { get; init; }
            public byte ShieldTimeTrigger { get; init; }
            public List<(short Action, short Timing, short Value, ushort MoveId)> ExtraActions { get; init; } = [];
        }

        private Dictionary<(ushort Species, short Form), RaidBossMechanicsInfo> _raidBossMechanicsData = [];

        /// <summary>
        /// Information about a player participating in raids
        /// </summary>
        public record PlayerInfo
        {
            public required string OT { get; init; }
            public int RaidCount { get; set; }
        }

        private int _lobbyError;
        private int _raidCount;
        private int _winCount;
        private int _lossCount;
        private int _seedIndexToReplace = -1;
        public static GameProgress GameProgress { get; private set; }
        public static bool? CurrentSpawnsEnabled { get; private set; }
        private int _storyProgress;
        private int _eventProgress;
        private int _emptyRaid;
        private int _lostRaid;
        private bool _firstRun = true;
        private int _teleportRetryCount = 0;
        private int _currentRaidIndex = 0;
        private int _nextConfiguredRaidRotationIndex = 0;
        public static int RotationCount { get; set; }
        private ulong _todaySeed;
        private ulong _overworldOffset;
        private ulong _connectedOffset;
        private ulong _raidBlockPointerP;
        private ulong _raidBlockPointerK;
        private ulong _raidBlockPointerB;
        private RaidMemoryManager _raidMemoryManager;
        private readonly ulong[] _teraNIDOffsets = new ulong[3];
        private string _teraRaidCode = string.Empty;

        private readonly Dictionary<ulong, int> _raidTracker = [];
        private SAV9SV _hostSAV = new();
        private static readonly DateTime StartTime = DateTime.Now;
        public static RaidContainer? Container { get; private set; }
        public static bool IsKitakami { get; private set; }
        public static bool IsBlueberry { get; private set; }
        private static DateTime _timeForRollBackCheck = DateTime.Now;
        private string _denHexSeed = string.Empty;
        private int _seedMismatchCount;
        private bool _shouldRefreshMap;
        public static bool HasErrored { get; set; }
        private bool _isRecoveringFromReboot;
        private volatile bool _isPaused = false;

        private static readonly WebRaidRequestClient WebRequests = new(msg => LogUtil.LogInfo(msg, "GenPKM"));
        private DateTime _nextWebClaimAllowed = DateTime.MinValue;
        private bool _warnedWebNotApproved;
        private bool _warnedWebMultipleBots;

        private int _consecutiveDenFailures = 0;
        private int _lastFailedDenIndex = -1;

        // Constants for teleport retry logic
        private const int MaxTeleportRetries = 3;
        private const float TeleportDistanceThreshold = 2.5f;

        // Constants for den recovery logic
        private const int DenFailuresBeforeForceActivate = 1;
        private const int DenFailuresBeforeMapRefresh = 3;

        // Constants for raid type identification
        private const string MysteryRaidTitle = "Mystery Shiny Raid";
        private const string UserRequestedRaidSuffix = "'s Requested Raid";

        // Region boundary constants for raid index calculations
        private const int PaldeaRaidCount = 69;
        private const int KitakamiRaidCount = 25;
        private const int KitakamiStartIndex = PaldeaRaidCount;
        private const int BlueberryStartIndex = PaldeaRaidCount + KitakamiRaidCount;

        // Cached den locations to avoid repeated JSON loading
        private static readonly Lazy<Dictionary<string, float[]>> CachedPaldeaDenLocations = new(() =>
            LoadDenLocations("SysBot.Pokemon.SV.BotRaid.DenLocations.den_locations_base.json"));
        private static readonly Lazy<Dictionary<string, float[]>> CachedKitakamiDenLocations = new(() =>
            LoadDenLocations("SysBot.Pokemon.SV.BotRaid.DenLocations.den_locations_kitakami.json"));
        private static readonly Lazy<Dictionary<string, float[]>> CachedBlueberryDenLocations = new(() =>
            LoadDenLocations("SysBot.Pokemon.SV.BotRaid.DenLocations.den_locations_blueberry.json"));

        /// <summary>
        /// Main execution loop for the raid bot
        /// </summary>
        /// <param name="token">Cancellation token</param>
        public override async Task MainLoop(CancellationToken token)
        {
            // Clear pause state when starting
            _isPaused = false;

            // Requests saved into config.json by a crash or a killed process come
            // back without their Discord user or web lease, so nobody could be
            // told about them and the site has already moved on.
            int orphaned = _settings.ActiveRaids.RemoveAll(p =>
                p.AddedByRACommand && !p.Title.Contains(MysteryRaidTitle) && p.User is null && p.WebRequest is null);
            if (orphaned > 0)
                Log($"Removed {orphaned} raid request(s) left over from the last session.");

            if (_settings.RaidSettings.GenerateRaidsFromFile)
            {
                await Task.Run(() => GenerateSeedsFromFile(), token).ConfigureAwait(false);
                Log("Done.");
                _settings.RaidSettings.GenerateRaidsFromFile = false;
            }

            if (_settings.MiscSettings.ConfigureRolloverCorrection)
            {
                await RolloverCorrectionSV(token).ConfigureAwait(false);
                return;
            }

            if (NoActiveRaids)
            {
                Log("No active raids configured. Default shiny raids will be added once game data is initialized.");
                // Continue execution instead of returning
            }

            try
            {
                Log("Identifying trainer data of the host console.");
                _hostSAV = await IdentifyTrainer(token).ConfigureAwait(false);
                await InitializeHardware(_settings, token).ConfigureAwait(false);
                Log("Starting main RotatingRaidBot loop.");
                await InnerLoop(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(e.Message);
            }
            finally
            {
                SaveSeeds();
            }

            Log($"Ending {nameof(RotatingRaidBotSV)} loop.");
            await HardStop().ConfigureAwait(false);
        }

        public override void SoftStop()
        {
            _isPaused = true;
            base.SoftStop();
        }

        /// <summary>
        /// Reboots the game and restarts the main loop
        /// </summary>
        /// <param name="t">Cancellation token</param>
        public override async Task RebootAndStop(CancellationToken t)
        {
            await ReOpenGame(new PokeRaidHubConfig(), t).ConfigureAwait(false);
            await HardStop().ConfigureAwait(false);
            await Task.Delay(2_000, t).ConfigureAwait(false);

            if (!t.IsCancellationRequested)
            {
                Log("Restarting the inner loop.");
                await InnerLoop(t).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Refreshes the map by setting a flag
        /// </summary>
        /// <param name="t">Cancellation token</param>
        public override Task RefreshMap(CancellationToken t)
        {
            _shouldRefreshMap = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles storage and persistence of player data
        /// </summary>
        public class PlayerDataStorage
        {
            private readonly string _filePath;

            public PlayerDataStorage(string baseDirectory)
            {
                var directoryPath = Path.Combine(baseDirectory, "raidfilessv");
                Directory.CreateDirectory(directoryPath);
                _filePath = Path.Combine(directoryPath, "player_data.json");

                if (!File.Exists(_filePath))
                {
                    File.WriteAllText(_filePath, "{}");
                }
            }

            public Dictionary<ulong, PlayerInfo> LoadPlayerData() =>
                JsonConvert.DeserializeObject<Dictionary<ulong, PlayerInfo>>(File.ReadAllText(_filePath)) ?? [];

            public void SavePlayerData(Dictionary<ulong, PlayerInfo> data) =>
                File.WriteAllText(_filePath, JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        /// <summary>
        /// Inserts default shiny raids when no active raids are configured
        /// </summary>
        private async Task InsertDefaultShinyRaids(CancellationToken token)
        {
            // Ensure Container is properly initialized before proceeding
            if (Container == null)
            {
                Log("Container is null, attempting to initialize raid data...");
                try
                {
                    await ReadRaids(token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"Failed to initialize raid data: {ex.Message}");
                    return;
                }
            }

            // Use current game state instead of hardcoded values
            var currentGameProgress = GameProgress != GameProgress.None ? GameProgress : GameProgress.Unlocked5Stars;
            var currentRegion = await DetectCurrentRegion(token);

            Log($"Creating default shiny raids with game progress: {currentGameProgress}, region: {currentRegion}");

            // Generate two random shiny raids
            for (int i = 0; i < 2; i++)
            {
                uint randomSeed = GenerateRandomShinySeed();
                string seedValue = randomSeed.ToString("X8");

                // Use current game state for parameters
                int difficultyLevel = currentGameProgress switch
                {
                    GameProgress.Unlocked6Stars => 5, // Use 5-star for reliability
                    GameProgress.Unlocked5Stars => 5,
                    GameProgress.Unlocked4Stars => 4,
                    GameProgress.Unlocked3Stars => 3,
                    _ => 3 // Default to 3-star if unknown
                };

                var crystalType = difficultyLevel == 6 ? TeraCrystalType.Black : TeraCrystalType.Base;
                int contentType = difficultyLevel == 6 ? 1 : 0; // Black crystal raids use contentType 1
                int raidDeliveryGroupID = 0;

                // Retry logic for RaidInfoCommand in case of initial failures
                PK9? pk = null;
                Embed? embed = null;
                int retryCount = 0;
                const int maxRetries = 3;

                while (retryCount < maxRetries && pk == null)
                {
                    try
                    {
                        var result = RaidInfoCommand(
                            seedValue,
                            contentType,
                            currentRegion,
                            (int)currentGameProgress + 1, // RaidInfoCommand expects 1-based progress
                            raidDeliveryGroupID,
                            _settings.EmbedToggles.RewardsToShow,
                            _settings.EmbedToggles.MoveTypeEmojis,
                            _settings.EmbedToggles.CustomTypeEmojis,
                            0,
                            false,
                            (int)_settings.EmbedToggles.EmbedLanguage
                        );

                        pk = result.Item1;
                        embed = result.Item2;

                        // Validate that we got a valid Pokemon (not Species.None)
                        if (pk.Species == 0)
                        {
                            Log($"RaidInfoCommand returned invalid species (0) for seed {seedValue}, attempt {retryCount + 1}");
                            pk = null;
                            retryCount++;

                            if (retryCount < maxRetries)
                            {
                                // Try a different seed
                                randomSeed = GenerateRandomShinySeed();
                                seedValue = randomSeed.ToString("X8");
                                await Task.Delay(500, token); // Small delay before retry
                            }
                        }
                        else
                        {
                            Log($"Successfully generated raid data - Species: {(Species)pk.Species}, Seed: {seedValue}");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error in RaidInfoCommand attempt {retryCount + 1}: {ex.Message}");
                        retryCount++;

                        if (retryCount < maxRetries)
                        {
                            // Try a different seed
                            randomSeed = GenerateRandomShinySeed();
                            seedValue = randomSeed.ToString("X8");
                            await Task.Delay(1000, token); // Delay before retry
                        }
                    }
                }

                // If we still don't have valid data after retries, skip this raid
                if (pk == null || pk.Species == 0)
                {
                    Log($"Failed to generate valid raid data after {maxRetries} attempts, skipping raid {i + 1}");
                    continue;
                }

                // Get the Tera type from the embed for battler selection
                string teraType = embed != null ? ExtractTeraTypeFromEmbed(embed) : "Fairy";
                string[] battlers = GetBattlerForTeraType(teraType);

                // If no specific battler is configured for this Tera type, use a default one
                if (battlers.Length == 0 || string.IsNullOrEmpty(battlers[0]))
                {
                    // Default to a general strong Pokemon
                    battlers = ["Koraidon @ Booster Energy\r\nBall: Master Ball\r\nLevel: 100\r\nShiny: No\r\nAbility: Orichalcum Pulse\r\nEVs: 252 Atk / 4 SpA / 252 Spe\r\nAdamant Nature\r\n.MetLocation=124\r\n.MetLevel=72\r\n.Version=50\r\n- Drain Punch"];
                }

                RotatingRaidParameters newShinyRaid = new()
                {
                    Seed = seedValue,
                    Species = (Species)pk.Species,
                    SpeciesForm = pk.Form,
                    Title = $"Shiny {(Species)pk.Species}",
                    DifficultyLevel = difficultyLevel,
                    StoryProgress = (GameProgressEnum)currentGameProgress,
                    CrystalType = crystalType,
                    IsShiny = true,
                    PartyPK = battlers,
                    ActiveInRotation = true,
                    Action1 = Action1Type.GoAllOut,
                    Action1Delay = 5
                };

                _settings.ActiveRaids.Add(newShinyRaid);
                Log($"Added Default Shiny Raid - Species: {(Species)pk.Species}, Form: {pk.Form}, Seed: {seedValue}, Difficulty: {difficultyLevel}★");
            }

            int successfulRaids = _settings.ActiveRaids.Count(r => r.Title.StartsWith("Shiny"));
            Log($"{successfulRaids} default shiny raids have been added. Bot will continue operating normally.");
        }

        /// <summary>
        /// Generates raid seeds from configuration file
        /// </summary>
        private void GenerateSeedsFromFile()
        {
            const string folder = "raidfilessv";
            Directory.CreateDirectory(folder);

            var prevRotationPath = "raidsv.txt";
            var rotationPath = Path.Combine(folder, "raidsv.txt");

            if (File.Exists(prevRotationPath))
                File.Move(prevRotationPath, rotationPath);

            if (!File.Exists(rotationPath))
            {
                File.WriteAllText(rotationPath, "000091EC-Kricketune-3-6,0000717F-Seviper-3-6");
                Log("Creating a default raidsv.txt file, skipping generation as file is empty.");
                return;
            }
            var prevPath = "bodyparam.txt";
            var filePath = Path.Combine(folder, "bodyparam.txt");

            if (File.Exists(prevPath))
                File.Move(prevPath, filePath);

            var data = string.Empty;
            var prevPk = "pkparam.txt";
            var pkPath = Path.Combine(folder, "pkparam.txt");

            if (File.Exists(prevPk))
                File.Move(prevPk, pkPath);

            if (File.Exists(pkPath))
                data = File.ReadAllText(pkPath);

            DirectorySearch(rotationPath, data);
        }

        /// <summary>
        /// Saves raid seeds to file if enabled
        /// </summary>
        private void SaveSeeds()
        {
            if (!_settings.RaidSettings.SaveSeedsToFile)
                return;

            var raidsToSave = _settings.ActiveRaids.Where(raid => !raid.AddedByRACommand).ToList();

            if (!raidsToSave.Any())
                return;

            var directoryPath = "raidfilessv";
            var fileName = "savedSeeds.txt";
            var savePath = Path.Combine(directoryPath, fileName);

            Directory.CreateDirectory(directoryPath);

            var sb = new StringBuilder();

            foreach (var raid in raidsToSave)
            {
                int storyProgressValue = (int)raid.StoryProgress;
                sb.Append($"{raid.Seed}-{raid.Species}-{raid.DifficultyLevel}-{storyProgressValue},");
            }

            if (sb.Length > 0)
                sb.Length--;

            File.WriteAllText(savePath, sb.ToString());
        }

        private static readonly char[] Separator = [','];
        private static readonly char[] SeparatorArray = ['-'];

        /// <summary>
        /// Parses raid configuration from text files
        /// </summary>
        /// <param name="sDir">Directory containing raid files</param>
        /// <param name="data">Additional Pokemon data</param>
        private void DirectorySearch(string sDir, string data)
        {
            _settings.ActiveRaids.Clear();

            string contents = File.ReadAllText(sDir);
            string[] monInfo = contents.Split(Separator, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < monInfo.Length; i++)
            {
                string[] div = monInfo[i].Split(SeparatorArray, StringSplitOptions.RemoveEmptyEntries);

                if (div.Length != 4)
                {
                    Log($"Error processing entry: {monInfo[i]}. Expected 4 parts but found {div.Length}. Skipping this entry.");
                    continue;
                }

                string monSeed = div[0];
                string monTitle = div[1];

                if (!int.TryParse(div[2], out int difficultyLevel))
                {
                    Log($"Unable to parse difficulty level for entry: {monInfo[i]}");
                    continue;
                }

                if (!int.TryParse(div[3], out int storyProgressLevelFromSeed))
                {
                    Log($"Unable to parse StoryProgressLevel for entry: {monInfo[i]}");
                    continue;
                }

                int convertedStoryProgressLevel = storyProgressLevelFromSeed - 1;

                TeraCrystalType type = difficultyLevel switch
                {
                    6 => TeraCrystalType.Black,
                    7 => TeraCrystalType.Might,
                    _ => TeraCrystalType.Base,
                };

                var param = new RotatingRaidParameters
                {
                    Seed = monSeed,
                    Title = monTitle,
                    Species = RaidExtensions<PK9>.EnumParse<Species>(monTitle),
                    CrystalType = type,
                    PartyPK = [data],
                    DifficultyLevel = difficultyLevel,
                    StoryProgress = (GameProgressEnum)convertedStoryProgressLevel
                };

                _settings.ActiveRaids.Add(param);
                Log($"Parameters generated from text file for {monTitle}.");
            }
        }

        /// <summary>
        /// Main execution loop for handling raids
        /// </summary>
        /// <param name="token">Cancellation token</param>
        private async Task InnerLoop(CancellationToken token)
        {

            try
            {
                bool partyReady;
                _currentRaidIndex = 0;
                var raidsHosted = 0;
                int consecutiveErrors = 0;
                const int maxConsecutiveErrors = 3;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // CHECK IF BOT IS PAUSED
                        if (_isPaused)
                        {
                            Log("Bot is paused. Waiting for resume command...");

                            // Wait while paused
                            while (_isPaused && !token.IsCancellationRequested)
                            {
                                await Task.Delay(1000, token).ConfigureAwait(false);
                            }

                            if (!token.IsCancellationRequested && !_isPaused)
                            {
                                Log("Bot resumed. Continuing raid operations...");
                            }

                            continue;
                        }
                        try
                        {
                            await InitializeSessionOffsets(token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log($"Error initializing session offsets: {ex.Message}");
                            await Task.Delay(2000, token).ConfigureAwait(false);
                            continue;
                        }

                        if (_isRecoveringFromReboot)
                        {
                            Log("Recovering from reboot - ensuring online connectivity before proceeding");
                            if (!await IsOnOverworld(_overworldOffset, token).ConfigureAwait(false))
                            {
                                Log("Not on overworld after reboot, attempting to return to overworld");
                                if (!await RecoverToOverworld(token).ConfigureAwait(false))
                                {
                                    Log("Failed to recover to overworld, retrying the reboot process");
                                    await PerformRebootAndReset(token).ConfigureAwait(false);
                                    continue;
                                }
                            }

                            if (!await ConnectToOnline(_hub.Config, token).ConfigureAwait(false))
                            {
                                Log("Failed to connect online after reboot, retrying the reboot process");
                                await PerformRebootAndReset(token).ConfigureAwait(false);
                                continue;
                            }

                            _isRecoveringFromReboot = false;
                            Log("Successfully recovered online connectivity after reboot");
                        }

                        if (_raidCount == 0)
                        {
                            try
                            {
                                _todaySeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(_raidBlockPointerP, 8, token).ConfigureAwait(false), 0);
                                Log($"Today Seed: {_todaySeed:X8}");
                            }
                            catch (Exception ex)
                            {
                                Log($"Error reading Today Seed: {ex.Message}");
                                consecutiveErrors++;
                                await Task.Delay(2000, token).ConfigureAwait(false);
                                continue;
                            }
                        }

                        try
                        {
                            await ReadRaids(token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log($"Error reading raids: {ex.Message}");
                            consecutiveErrors++;

                            if (consecutiveErrors >= maxConsecutiveErrors)
                            {
                                Log("Multiple failures reading raids, rebooting game");
                                await PerformRebootAndReset(token).ConfigureAwait(false);
                                consecutiveErrors = 0;
                            }

                            await Task.Delay(2000, token).ConfigureAwait(false);
                            continue;
                        }

                        if (NoActiveRaids)
                        {
                            await InsertDefaultShinyRaids(token).ConfigureAwait(false);

                            if (NoActiveRaids)
                            {
                                Log("Failed to generate any raids. Cannot continue - please check your configuration and try again.");
                                return;
                            }
                        }

                        Ensure_currentRaidIndexInBounds();
                        Log($"Preparing parameter for {_settings.ActiveRaids[_currentRaidIndex].Species}");

                        try
                        {
                            var currentSeed = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(_raidBlockPointerP, 8, token).ConfigureAwait(false), 0);

                            if (_todaySeed != currentSeed || _lobbyError >= 2)
                            {
                                if (_todaySeed != currentSeed)
                                {
                                    Log($"Current Today Seed {currentSeed:X8} does not match Starting Today Seed: {_todaySeed:X8}");
                                    _todaySeed = currentSeed;
                                    await OverrideTodaySeed(token).ConfigureAwait(false);
                                    Log("Today Seed has been overridden with the current seed");
                                }

                                if (_lobbyError >= 2)
                                {
                                    string msg = $"Failed to create a lobby {_lobbyError} times";
                                    Log(msg);

                                    if (_consecutiveDenFailures >= DenFailuresBeforeMapRefresh)
                                    {
                                        Log($"Den at index {_seedIndexToReplace} has failed {_consecutiveDenFailures} times. Triggering map refresh.");
                                        _shouldRefreshMap = true;
                                        _consecutiveDenFailures = 0;
                                        _lastFailedDenIndex = -1;
                                        _lobbyError = 0;
                                        await CloseGame(_hub.Config, token).ConfigureAwait(false);
                                        await StartGameRaid(_hub.Config, token).ConfigureAwait(false);
                                        continue;
                                    }
                                    else if (_consecutiveDenFailures >= DenFailuresBeforeForceActivate)
                                    {
                                        Log($"Attempting den force-activation recovery (consecutive failures: {_consecutiveDenFailures})");
                                        await TryForceActivateDen(_seedIndexToReplace, token);
                                        await CloseGame(_hub.Config, token).ConfigureAwait(false);
                                        await StartGameRaid(_hub.Config, token).ConfigureAwait(false);
                                        _lobbyError = 0;
                                        continue;
                                    }
                                    else
                                    {
                                        await CloseGame(_hub.Config, token).ConfigureAwait(false);
                                        await StartGameRaid(_hub.Config, token).ConfigureAwait(false);
                                        _lobbyError = 0;
                                        continue;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Error checking Today Seed: {ex.Message}");
                            consecutiveErrors++;
                            await Task.Delay(2000, token).ConfigureAwait(false);
                            continue;
                        }

                        try
                        {
                            await SwitchConnection.WriteBytesAbsoluteAsync(new byte[32], _teraNIDOffsets[0], token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log($"Error clearing NIDs: {ex.Message}");
                        }

                        int prepareResult;
                        int prepareAttempts = 0;
                        do
                        {
                            try
                            {
                                prepareResult = await PrepareForRaid(token).ConfigureAwait(false);

                                if (prepareResult == 0)
                                {
                                    prepareAttempts++;
                                    Log($"Failed to prepare raid (attempt {prepareAttempts})");

                                    if (prepareAttempts >= 2)
                                    {
                                        Log("Failed to prepare the raid after multiple attempts, rebooting game");
                                        await PerformRebootAndReset(token).ConfigureAwait(false);
                                        break;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Error in PrepareForRaid: {ex.Message}");
                                prepareResult = 0;
                                prepareAttempts++;

                                if (prepareAttempts >= 2)
                                {
                                    Log("Error preparing raid, rebooting game");
                                    await PerformRebootAndReset(token).ConfigureAwait(false);
                                    break;
                                }
                            }
                        } while (prepareResult == 0 && !token.IsCancellationRequested);

                        if (token.IsCancellationRequested)
                            break;

                        if (prepareResult == 2)
                        {
                            consecutiveErrors = 0;
                            continue;
                        }

                        if (!await GetLobbyReady(false, token).ConfigureAwait(false))
                        {
                            continue;
                        }

                        _consecutiveDenFailures = 0;
                        _lastFailedDenIndex = -1;

                        Ensure_currentRaidIndexInBounds();
                        if (_settings.ActiveRaids[_currentRaidIndex].AddedByRACommand)
                        {
                            try
                            {
                                await HandleRACommandRaid(token).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                Log($"Error handling RA command raid: {ex.Message}");
                            }
                        }

                        try
                        {
                            (partyReady, var trainers) = await ReadTrainers(token).ConfigureAwait(false);
                            if (!partyReady)
                            {
                                await HandleEmptyLobby(token).ConfigureAwait(false);
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Error reading trainers: {ex.Message}");
                            consecutiveErrors++;

                            if (consecutiveErrors >= maxConsecutiveErrors)
                            {
                                await PerformRebootAndReset(token).ConfigureAwait(false);
                                consecutiveErrors = 0;
                            }

                            continue;
                        }

                        try
                        {
                            await CompleteRaid(token).ConfigureAwait(false);
                            raidsHosted++;
                            consecutiveErrors = 0;

                            if (raidsHosted == _settings.RaidSettings.TotalRaidsToHost && _settings.RaidSettings.TotalRaidsToHost > 0)
                                break;
                        }
                        catch (Exception ex)
                        {
                            Log($"Error during raid completion: {ex.Message}");
                            consecutiveErrors++;

                            if (consecutiveErrors >= maxConsecutiveErrors)
                            {
                                Log("Multiple raid completion failures, performing reboot");
                                await PerformRebootAndReset(token).ConfigureAwait(false);
                                consecutiveErrors = 0;
                            }
                        }
                    }
                    catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "_0")
                    {
                        Log("Connection error detected. Performing reboot and reset.");
                        await PerformRebootAndReset(token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log($"Unexpected error in InnerLoop: {ex.Message}");
                        consecutiveErrors++;

                        if (consecutiveErrors >= maxConsecutiveErrors)
                        {
                            await PerformRebootAndReset(token).ConfigureAwait(false);
                            consecutiveErrors = 0;
                        }
                        else
                        {
                            await Task.Delay(5000, token).ConfigureAwait(false);
                        }
                    }
                }

                if (_settings.RaidSettings.TotalRaidsToHost > 0 && raidsHosted != 0)
                    Log("Total raids to host has been met.");
            }
            catch (Exception ex)
            {
                Log($"Critical error in InnerLoop: {ex.Message}");
                await PerformRebootAndReset(token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Handles raids added via RA command, sending codes to appropriate users
        /// </summary>
        private async Task HandleRACommandRaid(CancellationToken token)
        {
            var web = CurrentWebRequest;
            if (web != null)
            {
                var webCode = await GetRaidCode(token).ConfigureAwait(false);
                WebRequests.Report(web, "lobby", $"Your lobby is open. The host is {GetWebHostName()}.", webCode);
                Log($"Sent the raid code to GenPKM request #{web.Id}.");
                return;
            }

            var user = _settings.ActiveRaids[_currentRaidIndex].User;
            var mentionedUsers = _settings.ActiveRaids[_currentRaidIndex].MentionedUsers;

            if (!IsFreeForAllRaid())
            {
                try
                {
                    var code = await GetRaidCode(token).ConfigureAwait(false);
                    if (user != null)
                    {
                        await user.SendMessageAsync($"Your Raid Code is **{code}**").ConfigureAwait(false);
                    }

                    foreach (var mentionedUser in mentionedUsers)
                    {
                        await mentionedUser.SendMessageAsync(
                            $"The Raid Code for the private raid you were invited to by {user?.Username ?? "the host"} is **{code}**"
                        ).ConfigureAwait(false);
                        await Task.Delay(300, token).ConfigureAwait(false);
                    }
                }
                catch (Discord.Net.HttpException ex)
                {
                    Log($"Failed to send DM to the user or mentioned users. They might have DMs turned off. Exception: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Handles the case when nobody joins the raid lobby - properly respects all LobbyOption settings
        /// </summary>
        private async Task HandleEmptyLobby(CancellationToken token)
        {
            if (CurrentWebRequest != null)
            {
                // A requested raid gets one lobby. The member can ask again
                // right away, and the bot is free for the next request.
                Log("Nobody joined the GenPKM requested raid. Moving on to the next raid.");
                await SharedRaidCodeHandler.UpdateReactionsOnAllMessages(false, token);
                await SkipRaidOnLosses(token).ConfigureAwait(false);
                _emptyRaid = 0;
                _lostRaid = 0;
                return;
            }

            _emptyRaid++;
            _lostRaid++;

            // Mark embed as inactive since nobody joined
            await SharedRaidCodeHandler.UpdateReactionsOnAllMessages(false, token);

            Log($"Nobody joined the raid. Current counts - Empty: {_emptyRaid}, Lost: {_lostRaid}");
            Log($"Lobby Method: {_settings.LobbyOptions.LobbyMethod}, Empty Limit: {_settings.LobbyOptions.EmptyRaidLimit}, Skip Limit: {_settings.LobbyOptions.SkipRaidLimit}");

            // Handle different lobby methods
            switch (_settings.LobbyOptions.LobbyMethod)
            {
                case LobbyMethodOptions.SkipRaid:
                    if (_lostRaid >= _settings.LobbyOptions.SkipRaidLimit)
                    {
                        Log($"Reached SkipRaidLimit ({_settings.LobbyOptions.SkipRaidLimit}). Moving to next raid!");
                        await SkipRaidOnLosses(token).ConfigureAwait(false);
                        _emptyRaid = 0;  // Reset empty raid counter when skipping
                        _lostRaid = 0;   // Reset lost raid counter when skipping
                        return;
                    }
                    else
                    {
                        Log($"SkipRaid mode: {_lostRaid}/{_settings.LobbyOptions.SkipRaidLimit} - continuing with same raid");
                    }
                    break;

                case LobbyMethodOptions.OpenLobby:
                    if (_emptyRaid >= _settings.LobbyOptions.EmptyRaidLimit)
                    {
                        Log($"Reached EmptyRaidLimit ({_settings.LobbyOptions.EmptyRaidLimit}). Next lobby will be opened to all (Free For All)!");
                    }
                    else
                    {
                        Log($"OpenLobby mode: {_emptyRaid}/{_settings.LobbyOptions.EmptyRaidLimit} - keeping coded lobby");
                    }
                    break;

                case LobbyMethodOptions.ContinueRaid:
                    Log("Continue mode: Will keep hosting the same raid indefinitely");
                    break;

                default:
                    Log($"Unknown lobby method: {_settings.LobbyOptions.LobbyMethod}. Defaulting to Continue behavior.");
                    break;
            }

            // Always attempt to regroup and recover for non-skip scenarios
            await RegroupFromBannedUser(token).ConfigureAwait(false);

            if (!await IsOnOverworld(_overworldOffset, token).ConfigureAwait(false))
            {
                Log("Something went wrong, attempting to recover.");
                await ReOpenGame(_hub.Config, token).ConfigureAwait(false);
                return;
            }

            // Clear trainer OTs
            Log("Clearing stored OTs");
            for (int i = 0; i < 3; i++)
            {
                List<long> ptr = [.. Offsets.Trader2MyStatusPointer];
                ptr[2] += i * 0x30;
                await SwitchConnection.PointerPoke(new byte[16], ptr, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Performs a hard stop of the bot, cleaning up resources
        /// </summary>
        public override async Task HardStop()
        {
            try
            {
                if (Directory.Exists("cache"))
                {
                    Directory.Delete("cache", true);
                }
            }
            catch (Exception)
            {
                // Silently handle directory deletion errors
            }

            foreach (var web in _settings.ActiveRaids.Select(p => p.WebRequest).Where(w => w?.Host == this).ToList())
            {
                if (web!.IsHosted)
                    WebRequests.Report(web, "failed", "The host had to restart its game during your raid. You can request it again now.");
                else
                    WebRequests.Report(web, "released", "The raid bot had to restart before hosting your raid. You are back in line for the next free bot.");
            }

            // Another bot in this program may be holding its own web request; leave it alone.
            _settings.ActiveRaids.RemoveAll(p => p.AddedByRACommand && (p.WebRequest is null || p.WebRequest.Host == this));
            _settings.ActiveRaids.RemoveAll(p => p.Title == MysteryRaidTitle);
            await WebRequests.Flush(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await CleanExit(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Completes a raid after trainers have joined
        /// </summary>
        private async Task CompleteRaid(CancellationToken token)
        {
            try
            {
                var trainers = new List<(ulong, RaidMyStatus)>();
                await SharedRaidCodeHandler.UpdateReactionsOnAllMessages(false, token); // Mark Embed as not active
                Log("Preparing for battle!");

                if (!await EnsureInRaid(token))
                {
                    Log("Failed to enter raid, restarting game.");
                    await ReOpenGame(_hub.Config, token);
                    return; // Exit early without throwing exception
                }

                if (CurrentWebRequest is { } web)
                    WebRequests.Report(web, "battling", "The raid battle has started.");

                var screenshotDelay = (int)_settings.EmbedToggles.ScreenshotTiming;
                await Task.Delay(screenshotDelay, token).ConfigureAwait(false);

                var lobbyTrainersFinal = new List<(ulong, RaidMyStatus)>();
                if (!await UpdateLobbyTrainersFinal(lobbyTrainersFinal, trainers, token))
                {
                    throw new Exception("Failed to update lobby trainers");
                }

                if (!await HandleDuplicatesAndEmbeds(lobbyTrainersFinal, token))
                {
                    throw new Exception("Failed to handle duplicates and embeds");
                }

                await Task.Delay(10_000, token).ConfigureAwait(false);

                // Check if the party is still present after sending embed
                bool partyDipped = lobbyTrainersFinal.Count == 0;
                if (partyDipped)
                {
                    Log("Party has left after joining. Restarting game.");
                    if (CurrentWebRequest is { } dipped)
                        dipped.OutcomeOverride = ("missed", "Everyone left the lobby before the battle started. You can request it again now.");

                    // Skip HandleEndOfRaidActions and go straight to FinalizeRaidCompletion
                    await ReOpenGame(_hub.Config, token);
                    await FinalizeRaidCompletion(trainers, true, token);
                    return;
                }

                if (!await ProcessBattleActions(token))
                {
                    throw new Exception("Failed to process battle actions");
                }

                bool isRaidCompleted = await HandleEndOfRaidActions(token);
                if (!isRaidCompleted)
                {
                    throw new Exception("Raid not completed");
                }

                await FinalizeRaidCompletion(trainers, isRaidCompleted, token);
            }
            catch (Exception ex)
            {
                Log($"Error occurred during raid: {ex.Message}");
                await PerformRebootAndReset(token);
            }
        }

        /// <summary>
        /// Performs a reboot and reset of the game when an error occurs
        /// </summary>
        private async Task PerformRebootAndReset(CancellationToken t)
        {
            var embed = new EmbedBuilder
            {
                Title = "Bot Reset",
                Description = "The bot encountered an issue and is currently resetting. Please stand by.",
                Color = Color.Red,
                ThumbnailUrl = "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/x.png"
            };
            EchoUtil.RaidEmbed(null, "", embed);

            await ReOpenGame(new PokeRaidHubConfig(), t).ConfigureAwait(false);
            await HardStop().ConfigureAwait(false);
            await Task.Delay(2_000, t).ConfigureAwait(false);

            _isRecoveringFromReboot = true;
        }

        /// <summary>
        /// Ensures the bot is in a raid battle
        /// </summary>
        private async Task<bool> EnsureInRaid(CancellationToken token)
        {
            var startTime = DateTime.Now;
            const int timeoutMinutes = 5;

            while (!await IsInRaid(token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested || (DateTime.Now - startTime).TotalMinutes > timeoutMinutes)
                {
                    Log("Timeout reached or cancellation requested, reopening game.");
                    await ReOpenGame(_hub.Config, token);
                    return false;
                }

                if (!await IsConnectedToLobby(token).ConfigureAwait(false))
                {
                    Log("Lost connection to lobby, reopening game.");
                    await ReOpenGame(_hub.Config, token);
                    return false;
                }

                await Click(A, 1_000, token).ConfigureAwait(false);
            }
            return true;
        }

        /// <summary>
        /// Updates the list of trainers in the lobby with their information
        /// </summary>
        public async Task<bool> UpdateLobbyTrainersFinal(List<(ulong, RaidMyStatus)> lobbyTrainersFinal, List<(ulong, RaidMyStatus)> trainers, CancellationToken token)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var storage = new PlayerDataStorage(baseDirectory);
            var playerData = storage.LoadPlayerData();

            // Clear NIDs to refresh player check.
            await SwitchConnection.WriteBytesAbsoluteAsync(new byte[32], _teraNIDOffsets[0], token).ConfigureAwait(false);
            await Task.Delay(5_000, token).ConfigureAwait(false);

            // Loop through trainers again in case someone disconnected.
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var nidOfs = _teraNIDOffsets[i];
                    var data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                    var nid = BitConverter.ToUInt64(data, 0);

                    if (nid == 0)
                        continue;

                    List<long> ptr = [.. Offsets.Trader2MyStatusPointer];
                    ptr[2] += i * 0x30;
                    var trainer = await GetTradePartnerMyStatus(ptr, token).ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(trainer.OT) || _hostSAV.OT == trainer.OT)
                        continue;

                    lobbyTrainersFinal.Add((nid, trainer));

                    if (!playerData.TryGetValue(nid, out var info))
                    {
                        // New player
                        playerData[nid] = new PlayerInfo { OT = trainer.OT, RaidCount = 1 };
                        Log($"New Player: {trainer.OT} | TID: {trainer.DisplayTID} | NID: {nid}.");
                    }
                    else
                    {
                        // Returning player
                        info.RaidCount++;
                        playerData[nid] = info; // Update the info back to the dictionary.
                        Log($"Returning Player: {trainer.OT} | TID: {trainer.DisplayTID} | NID: {nid} | Raids: {info.RaidCount}");
                    }
                }
                catch (IndexOutOfRangeException ex)
                {
                    Log($"Index out of range exception caught: {ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    Log($"An unknown error occurred: {ex.Message}");
                    return false;
                }
            }

            // Save player data after processing all players.
            storage.SavePlayerData(playerData);
            return true;
        }

        /// <summary>
        /// Handles duplicates and sends Discord embeds
        /// </summary>
        private async Task<bool> HandleDuplicatesAndEmbeds(List<(ulong, RaidMyStatus)> lobbyTrainersFinal, CancellationToken token)
        {
            var nidDupe = lobbyTrainersFinal.Select(x => x.Item1).ToList();
            var dupe = lobbyTrainersFinal.Count > 1 && nidDupe.Distinct().Count() == 1;

            if (dupe)
            {
                // We read bad data, reset game to end early and recover.
                const int maxAttempts1 = 3;
                bool success = false;

                for (int attempt = 1; attempt <= maxAttempts1; attempt++)
                {
                    try
                    {
                        await Task.Delay(5_000, token);
                        await EnqueueEmbed(null, "", false, false, false, false, token, true).ConfigureAwait(false);
                        success = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Attempt {attempt} failed with error: {ex.Message}");
                        if (attempt == maxAttempts1)
                        {
                            Log("All attempts failed. Continuing without sending embed.");
                        }
                    }
                }

                if (!success)
                {
                    await ReOpenGame(_hub.Config, token).ConfigureAwait(false);
                    return false;
                }
            }

            var names = lobbyTrainersFinal.Select(x => x.Item2.OT).ToList();
            bool hatTrick = lobbyTrainersFinal.Count == 3 && names.Distinct().Count() == 1;

            bool embedSuccess = false;
            const int maxAttempts2 = 3;

            for (int attempt = 1; attempt <= maxAttempts2; attempt++)
            {
                try
                {
                    await EnqueueEmbed(names, "", hatTrick, false, false, true, token).ConfigureAwait(false);
                    embedSuccess = true;
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Attempt {attempt} failed with error: {ex.Message}");
                    if (attempt == maxAttempts2)
                    {
                        Log("All attempts failed. Continuing without sending embed.");
                    }
                }
            }

            return embedSuccess;
        }

        /// <summary>
        /// Enhanced method to detect disconnections during raid battles
        /// </summary>
        private async Task<bool> ProcessBattleActions(CancellationToken token)
        {
            int nextUpdateMinute = 2;
            DateTime battleStartTime = DateTime.Now;
            bool hasPerformedAction1 = false;

            // Main battle loop
            while (true)
            {
                // Check if raid has completed
                bool stillConnectedToLobby = await IsConnectedToLobby(token).ConfigureAwait(false);
                if (!stillConnectedToLobby)
                {
                    return true; // Return true as this is normal completion
                }

                // Check for timeout (10 minutes)
                TimeSpan timeInBattle = DateTime.Now - battleStartTime;
                if (timeInBattle.TotalMinutes >= 10)
                {
                    Log("Battle exceeded 10 minutes - restarting game.");
                    return false;
                }

                // Action handling
                if (!hasPerformedAction1)
                {
                    int action1DelayInSeconds = _settings.ActiveRaids[_currentRaidIndex].Action1Delay;
                    var action1Name = _settings.ActiveRaids[_currentRaidIndex].Action1;
                    int action1DelayInMilliseconds = action1DelayInSeconds * 1000;
                    Log($"Waiting {action1DelayInSeconds} seconds.");
                    await Task.Delay(action1DelayInMilliseconds, token).ConfigureAwait(false);
                    await MyActionMethod(token).ConfigureAwait(false);
                    Log($"{action1Name} done.");
                    hasPerformedAction1 = true;
                }
                else
                {
                    // Execute raid actions based on configuration
                    switch (_settings.LobbyOptions.Action)
                    {
                        case RaidAction.AFK:
                            await Task.Delay(3_000, token).ConfigureAwait(false);
                            break;

                        case RaidAction.MashA:
                            int mashADelayInMilliseconds = (int)(_settings.LobbyOptions.MashADelay * 1000);
                            await Click(A, mashADelayInMilliseconds, token).ConfigureAwait(false);
                            break;
                    }
                }

                // Status logging
                if (timeInBattle.TotalMinutes >= nextUpdateMinute)
                {
                    Log($"{nextUpdateMinute} minutes have passed. We are still in battle...");
                    nextUpdateMinute += 2;
                }

                // Small delay to prevent tight loops
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Handles actions at the end of a raid
        /// </summary>
        private async Task<bool> HandleEndOfRaidActions(CancellationToken token)
        {
            LobbyFiltersCategory settings = new();

            Log("Raid lobby disbanded!");
            await Task.Delay(1_500 + settings.ExtraTimeLobbyDisband, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(DDOWN, 0_500, token).ConfigureAwait(false);
            await Click(A, 0_500, token).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Finalizes raid completion, updates counters, and prepares for the next raid
        /// </summary>
        private async Task FinalizeRaidCompletion(List<(ulong, RaidMyStatus)> trainers, bool ready, CancellationToken token)
        {
            Log("Returning to overworld...");

            int attempts = 0;
            while (!await IsOnOverworld(_overworldOffset, token).ConfigureAwait(false) && attempts < 10)
            {
                await Click(A, 2_000, token).ConfigureAwait(false);
                attempts++;
            }

            if (!await RecoverToOverworld(token).ConfigureAwait(false))
            {
                Log("Failed to return to overworld after raid, rebooting game");
                await ReOpenGame(_hub.Config, token).ConfigureAwait(false);

                // After rebooting, attempt to continue with the raid rotation.
                // The battle already happened, so a requested raid is done and
                // must not be hosted a second time.
                if (ready)
                {
                    RemoveTemporaryRaidIfNeeded("completed");
                    await SanitizeRotationCount(token).ConfigureAwait(false);
                    await StartGameRaid(_hub.Config, token).ConfigureAwait(false);

                    if (_settings.RaidSettings.KeepDaySeed)
                        await OverrideTodaySeed(token).ConfigureAwait(false);

                    return;
                }
            }

            await CountRaids(trainers, token).ConfigureAwait(false);

            // Remove completed temporary raids BEFORE advancing rotation
            RemoveTemporaryRaidIfNeeded("completed");

            // Create replacement Mystery Raid BEFORE advancing rotation (if needed)
            if (_settings.RaidSettings.MysteryRaids)
            {
                int mysteryRaidCount = _settings.ActiveRaids.Count(raid => raid.Title.Contains(MysteryRaidTitle));
                if (mysteryRaidCount <= 1)
                {
                    try
                    {
                        CreateMysteryRaid();
                    }
                    catch (Exception ex)
                    {
                        Log($"Error in CreateMysteryRaid: {ex.Message}");
                    }
                }
            }

            await SanitizeRotationCount(token).ConfigureAwait(false);

            await EnqueueEmbed(null, "", false, false, true, false, token).ConfigureAwait(false);
            await Task.Delay(0_500, token).ConfigureAwait(false);
            await CloseGame(_hub.Config, token).ConfigureAwait(false);

            if (ready)
            {
                await StartGameRaid(_hub.Config, token).ConfigureAwait(false);
            }
            else
            {
                if (_settings.ActiveRaids.Count > 1)
                {
                    _currentRaidIndex = (_currentRaidIndex + 1) % _settings.ActiveRaids.Count;
                    if (_currentRaidIndex == 0)
                    {
                        Log($"Resetting Rotation Count to {_currentRaidIndex}");
                    }

                    Log($"Moving on to next rotation for {_settings.ActiveRaids[_currentRaidIndex].Species}.");
                    await StartGameRaid(_hub.Config, token).ConfigureAwait(false);
                }
                else
                {
                    await StartGame(_hub.Config, token).ConfigureAwait(false);
                }
            }

            if (_settings.RaidSettings.KeepDaySeed)
                await OverrideTodaySeed(token).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes the configured action in battle
        /// </summary>
        public async Task MyActionMethod(CancellationToken token)
        {
            switch (_settings.ActiveRaids[_currentRaidIndex].Action1)
            {
                case Action1Type.GoAllOut:
                    await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    await Click(A, 0_500, token).ConfigureAwait(false);
                    for (int i = 0; i < 3; i++)
                    {
                        await Click(A, 0_500, token).ConfigureAwait(false);
                    }
                    break;

                case Action1Type.HangTough:
                case Action1Type.HealUp:
                    await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    await Click(A, 0_500, token).ConfigureAwait(false);
                    int ddownTimes = _settings.ActiveRaids[_currentRaidIndex].Action1 == Action1Type.HangTough ? 1 : 2;
                    for (int i = 0; i < ddownTimes; i++)
                    {
                        await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        await Click(A, 0_500, token).ConfigureAwait(false);
                    }
                    break;

                case Action1Type.Move1:
                    await Click(A, 0_500, token).ConfigureAwait(false);
                    for (int i = 0; i < 3; i++)
                    {
                        await Click(A, 0_500, token).ConfigureAwait(false);
                    }
                    break;

                case Action1Type.Move2:
                case Action1Type.Move3:
                case Action1Type.Move4:
                    await Click(A, 0_500, token).ConfigureAwait(false);
                    int moveDdownTimes = _settings.ActiveRaids[_currentRaidIndex].Action1 switch
                    {
                        Action1Type.Move2 => 1,
                        Action1Type.Move3 => 2,
                        _ => 3, // Move4
                    };

                    for (int i = 0; i < moveDdownTimes; i++)
                    {
                        await Click(DDOWN, 0_500, token).ConfigureAwait(false);
                    }

                    for (int i = 0; i < 3; i++)
                    {
                        await Click(A, 0_500, token).ConfigureAwait(false);
                    }
                    break;

                default:
                    throw new InvalidOperationException("Unknown action type!");
            }
        }

        private async Task<bool> IsRaidWon(CancellationToken token)
        {
            try
            {
                if (_seedIndexToReplace == -1)
                {
                    Log("Cannot determine win/loss: Unknown raid index");
                    return false;
                }

                // Use the memory manager to read the seed at the known index
                uint seed = await _raidMemoryManager.ReadSeedAtIndex(_seedIndexToReplace, token);

                // A seed of 0 means the raid was completed (won)
                return seed == 0;
            }
            catch (Exception ex)
            {
                Log($"Error checking raid completion: {ex.Message}");
                return false;
            }
        }

        private async Task CountRaids(List<(ulong, RaidMyStatus)>? trainers, CancellationToken token)
        {
            if (trainers is not null)
            {
                await Task.Delay(1_500, token).ConfigureAwait(false);
                Log("Back in the overworld, checking if we won or lost.");

                bool isWon = await IsRaidWon(token);

                if (isWon)
                {
                    Log("Yay! We defeated the raid!");
                    _winCount++;
                }
                else
                {
                    Log("Dang, we lost the raid.");
                    _lossCount++;
                }
            }
            else
            {
                Log("No trainers available to check win/loss status.");
            }
        }

        /// <summary>
        /// Overrides the today seed to maintain consistent raids
        /// </summary>
        private async Task OverrideTodaySeed(CancellationToken token)
        {
            Log("Attempting to override Today Seed...");

            var todayOverride = BitConverter.GetBytes(_todaySeed);
            var ptr = new List<long>(RaidCrawler.Core.Structures.Offsets.RaidBlockPointerBase.ToArray());

            ptr[3] += 0x8;
            await SwitchConnection.PointerPoke(todayOverride, ptr, token).ConfigureAwait(false);

            Log("Today Seed override complete.");
        }

        private async Task<bool> OverrideSeedIndex(int index, CancellationToken token)
        {
            if (index == -1)
            {
                Log("Index is -1, skipping seed override.");
                return false;
            }

            try
            {
                // Parse the seed safely
                if (!uint.TryParse(_settings.ActiveRaids[_currentRaidIndex].Seed, NumberStyles.AllowHexSpecifier, null, out uint seed))
                {
                    Log($"Invalid seed format: {_settings.ActiveRaids[_currentRaidIndex].Seed}. Must be a valid hexadecimal value.");
                    return false;
                }

                var crystalType = _settings.ActiveRaids[_currentRaidIndex].CrystalType;
                var speciesName = _settings.ActiveRaids[_currentRaidIndex].Species.ToString();
                var groupID = _settings.ActiveRaids[_currentRaidIndex].GroupID;
                string? denIdentifier = null;

                // Adjust crystal type based on region
                if ((IsKitakami || IsBlueberry) && (crystalType == TeraCrystalType.Might || crystalType == TeraCrystalType.Distribution))
                {
                    crystalType = TeraCrystalType.Black;
                    Log("User is not in Paldea. Setting crystal type to Black.");
                }

                // Handle special crystal types (Might/Distribution)
                if (crystalType == TeraCrystalType.Might || crystalType == TeraCrystalType.Distribution)
                {
                    // Find the appropriate den for event raids
                    if (SpeciesToGroupIDMap.TryGetValue(speciesName, out var groupIDAndIndices))
                    {
                        var specificIndexInfo = groupIDAndIndices.FirstOrDefault(x => x.GroupID == groupID);
                        if (specificIndexInfo != default)
                        {
                            index = specificIndexInfo.Index;
                            denIdentifier = specificIndexInfo.DenIdentifier;
                            Log($"Using specific index {index} for GroupID: {groupID}, species: {speciesName}");
                            _seedIndexToReplace = index;
                        }
                    }
                }

                // Use the memory manager to inject the seed
                bool injectionSuccess = await _raidMemoryManager.InjectSeed(index, seed, crystalType, token);
                if (!injectionSuccess)
                {
                    Log($"Failed to inject seed {seed:X8} at index {index}.");
                    return false;
                }

                _seedIndexToReplace = index;
                Log($"Successfully injected seed {seed:X8} at index {index}");

                await TeleportToInjectedDenLocation(index, crystalType, speciesName, groupID, denIdentifier, token);

                return true;
            }
            catch (Exception ex)
            {
                Log($"Error in OverrideSeedIndex: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Teleports to the den location where we injected the seed 
        /// </summary>
        private async Task TeleportToInjectedDenLocation(int index, TeraCrystalType crystalType, string speciesName, int? groupID, string? denIdentifier, CancellationToken token)
        {
            try
            {
                // Get the appropriate location resource based on the current region
                string denLocationResource = IsKitakami
                    ? "SysBot.Pokemon.SV.BotRaid.DenLocations.den_locations_kitakami.json"
                    : IsBlueberry
                        ? "SysBot.Pokemon.SV.BotRaid.DenLocations.den_locations_blueberry.json"
                        : "SysBot.Pokemon.SV.BotRaid.DenLocations.den_locations_base.json";

                var denLocations = LoadDenLocations(denLocationResource);

                // For event raids, use the specific den identifier
                if (crystalType == TeraCrystalType.Might || crystalType == TeraCrystalType.Distribution)
                {
                    if (denIdentifier != null && denLocations.TryGetValue(denIdentifier, out var eventCoordinates))
                    {
                        await TeleportToDen(eventCoordinates[0], eventCoordinates[1], eventCoordinates[2], token);
                        Log($"Successfully teleported to event den: {denIdentifier}");
                        return;
                    }
                }

                // For regular raids, find the den location by matching the index with active raid data
                var currentRegion = await DetectCurrentRegion(token);
                var activeRaids = await GetActiveRaidLocations(currentRegion, token);

                var targetRaid = activeRaids.FirstOrDefault(raid => raid.Index == index);
                if (targetRaid.DenIdentifier != null)
                {
                    await TeleportToDen(targetRaid.Coordinates[0], targetRaid.Coordinates[1], targetRaid.Coordinates[2], token);
                    Log($"Successfully teleported to den: {targetRaid.DenIdentifier} at index {index}");
                }
                else
                {
                    Log($"Could not find coordinates for den at index {index}. No teleportation performed.");
                }
            }
            catch (Exception ex)
            {
                Log($"Error during teleportation: {ex.Message}");
            }
        }

        /// <summary>
        /// Attempts to force activate a den using memory write and IsActive flag
        /// </summary>
        private async Task TryForceActivateDen(int index, CancellationToken token)
        {
            try
            {
                Log($"Attempting to force activate den at index {index}...");

                if (index == -1)
                {
                    Log("Invalid den index. Cannot force activate.");
                    return;
                }

                bool isActive = await _raidMemoryManager.ReadIsActiveFlag(index, token);
                Log($"Den IsActive flag is currently: {isActive}");

                if (!isActive)
                {
                    Log("Den is not marked as active in memory. Forcing IsActive flag to true...");
                    bool flagSet = await _raidMemoryManager.SetIsActiveFlag(index, true, token);

                    if (flagSet)
                    {
                        Log("Successfully set IsActive flag to true. Saving game...");
                        await SaveGame(_hub.Config, token).ConfigureAwait(false);
                        Log("Game saved. Den should now be visible.");
                    }
                    else
                    {
                        Log("Failed to set IsActive flag. Will try re-injecting seed as backup.");
                    }
                }
                else
                {
                    Log("IsActive flag is already true, but den still not appearing. Re-injecting seed...");
                }

                var currentRaid = _settings.ActiveRaids[_currentRaidIndex];
                var seed = uint.Parse(currentRaid.Seed, NumberStyles.AllowHexSpecifier);
                var crystalType = currentRaid.CrystalType;

                bool seedSuccess = await _raidMemoryManager.InjectSeed(index, seed, crystalType, token);
                if (seedSuccess)
                {
                    Log($"Successfully re-injected seed {seed:X8} at index {index}.");

                    if (!isActive)
                    {
                        await _raidMemoryManager.SetIsActiveFlag(index, true, token);
                        Log("Re-applied IsActive flag after seed injection.");
                        await SaveGame(_hub.Config, token).ConfigureAwait(false);
                        Log("Game saved after seed injection.");
                    }

                    await Task.Delay(1_000, token).ConfigureAwait(false);

                    var verifyActive = await _raidMemoryManager.ReadIsActiveFlag(index, token);
                    var verifySeed = await _raidMemoryManager.ReadSeedAtIndex(index, token);
                    Log($"Verification - IsActive: {verifyActive}, Seed: {verifySeed:X8}");
                }
                else
                {
                    Log($"Failed to re-inject seed during force activation.");
                }
            }
            catch (Exception ex)
            {
                Log($"Error during force activate recovery: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a random shiny mystery raid
        /// </summary>
        private void CreateMysteryRaid()
        {
            uint randomSeed = GenerateRandomShinySeed();
            Random random = new();
            var mysteryRaidsSettings = _settings.RaidSettings.MysteryRaidsSettings;

            // Check if any Mystery Raid setting is enabled
            if (!(mysteryRaidsSettings.Unlocked3StarSettings.Enabled || mysteryRaidsSettings.Unlocked4StarSettings.Enabled ||
                  mysteryRaidsSettings.Unlocked5StarSettings.Enabled || mysteryRaidsSettings.Unlocked6StarSettings.Enabled))
            {
                Log("All Mystery Raids options are disabled. Mystery Raids will be turned off.");
                _settings.RaidSettings.MysteryRaids = false;
                return;
            }

            // Create a list of enabled StoryProgressLevels
            var enabledLevels = new List<GameProgress>();
            if (mysteryRaidsSettings.Unlocked3StarSettings.Enabled) enabledLevels.Add(GameProgress.Unlocked3Stars);
            if (mysteryRaidsSettings.Unlocked4StarSettings.Enabled) enabledLevels.Add(GameProgress.Unlocked4Stars);
            if (mysteryRaidsSettings.Unlocked5StarSettings.Enabled) enabledLevels.Add(GameProgress.Unlocked5Stars);
            if (mysteryRaidsSettings.Unlocked6StarSettings.Enabled) enabledLevels.Add(GameProgress.Unlocked6Stars);

            // Randomly pick a StoryProgressLevel from the enabled levels
            GameProgress gameProgress = enabledLevels[random.Next(enabledLevels.Count)];

            // Initialize a list to store possible difficulties
            List<int> possibleDifficulties = [];

            // Determine possible difficulties based on the selected GameProgress
            switch (gameProgress)
            {
                case GameProgress.Unlocked3Stars:
                    if (mysteryRaidsSettings.Unlocked3StarSettings.Allow1StarRaids) possibleDifficulties.Add(1);
                    if (mysteryRaidsSettings.Unlocked3StarSettings.Allow2StarRaids) possibleDifficulties.Add(2);
                    if (mysteryRaidsSettings.Unlocked3StarSettings.Allow3StarRaids) possibleDifficulties.Add(3);
                    break;

                case GameProgress.Unlocked4Stars:
                    if (mysteryRaidsSettings.Unlocked4StarSettings.Allow1StarRaids) possibleDifficulties.Add(1);
                    if (mysteryRaidsSettings.Unlocked4StarSettings.Allow2StarRaids) possibleDifficulties.Add(2);
                    if (mysteryRaidsSettings.Unlocked4StarSettings.Allow3StarRaids) possibleDifficulties.Add(3);
                    if (mysteryRaidsSettings.Unlocked4StarSettings.Allow4StarRaids) possibleDifficulties.Add(4);
                    break;

                case GameProgress.Unlocked5Stars:
                    if (mysteryRaidsSettings.Unlocked5StarSettings.Allow3StarRaids) possibleDifficulties.Add(3);
                    if (mysteryRaidsSettings.Unlocked5StarSettings.Allow4StarRaids) possibleDifficulties.Add(4);
                    if (mysteryRaidsSettings.Unlocked5StarSettings.Allow5StarRaids) possibleDifficulties.Add(5);
                    break;

                case GameProgress.Unlocked6Stars:
                    if (mysteryRaidsSettings.Unlocked6StarSettings.Allow3StarRaids) possibleDifficulties.Add(3);
                    if (mysteryRaidsSettings.Unlocked6StarSettings.Allow4StarRaids) possibleDifficulties.Add(4);
                    if (mysteryRaidsSettings.Unlocked6StarSettings.Allow5StarRaids) possibleDifficulties.Add(5);
                    if (mysteryRaidsSettings.Unlocked6StarSettings.Allow6StarRaids) possibleDifficulties.Add(6);
                    break;
            }

            // Check if there are any enabled difficulty levels
            if (possibleDifficulties.Count == 0)
            {
                Log("No difficulty levels enabled for the selected Story Progress. Mystery Raids will be turned off.");
                _settings.RaidSettings.MysteryRaids = false;
                return;
            }

            // Randomly pick a difficulty level from the possible difficulties
            int randomDifficultyLevel = possibleDifficulties[random.Next(possibleDifficulties.Count)];

            // Determine the crystal type based on difficulty level
            var crystalType = randomDifficultyLevel switch
            {
                >= 1 and <= 5 => TeraCrystalType.Base,
                6 => TeraCrystalType.Black,
                _ => throw new ArgumentException("Invalid difficulty level.")
            };

            string seedValue = randomSeed.ToString("X8");
            int contentType = randomDifficultyLevel == 6 ? 1 : 0;
            TeraRaidMapParent map;

            if (!IsBlueberry && !IsKitakami)
            {
                map = TeraRaidMapParent.Paldea;
            }
            else if (IsKitakami)
            {
                map = TeraRaidMapParent.Kitakami;
            }
            else
            {
                map = TeraRaidMapParent.Blueberry;
            }

            int raidDeliveryGroupID = 0;
            List<string> emptyRewardsToShow = [];
            bool defaultMoveTypeEmojis = false;
            List<MoveTypeEmojiInfo> emptyCustomTypeEmojis = [];
            int defaultQueuePosition = 0;
            bool defaultIsEvent = false;

            (PK9 pk, Embed embed) = RaidInfoCommand(
                seedValue, contentType, map, (int)gameProgress, raidDeliveryGroupID,
                emptyRewardsToShow, defaultMoveTypeEmojis, emptyCustomTypeEmojis,
                defaultQueuePosition, defaultIsEvent, (int)_settings.EmbedToggles.EmbedLanguage
            );

            string teraType = ExtractTeraTypeFromEmbed(embed);
            string[] battlers = GetBattlerForTeraType(teraType);

            RotatingRaidParameters newRandomShinyRaid = new()
            {
                Seed = seedValue,
                Species = Species.None,
                SpeciesForm = pk.Form,
                Title = MysteryRaidTitle,
                AddedByRACommand = true,
                DifficultyLevel = randomDifficultyLevel,
                StoryProgress = (GameProgressEnum)gameProgress,
                CrystalType = crystalType,
                IsShiny = true,
                PartyPK = battlers.Length > 0 ? battlers : [""]
            };

            // Find the last position of a raid added by the RA command
            int lastRaCommandRaidIndex = _settings.ActiveRaids.FindLastIndex(raid => raid.AddedByRACommand);
            int insertPosition = lastRaCommandRaidIndex != -1 ? lastRaCommandRaidIndex + 1 : _currentRaidIndex + 1;

            // Insert the new raid at the determined position
            _settings.ActiveRaids.Insert(insertPosition, newRandomShinyRaid);

            Log($"Added Mystery Raid - Species: {(Species)pk.Species}, Seed: {seedValue}.");
        }

        /// <summary>
        /// Generates a random seed that will always produce a shiny Pokemon
        /// </summary>
        private static uint GenerateRandomShinySeed()
        {
            Random random = new();
            uint seed;

            do
            {
                // Generate a random uint
                byte[] buffer = new byte[4];
                random.NextBytes(buffer);
                seed = BitConverter.ToUInt32(buffer, 0);

                // Use Xoroshiro128Plus directly with the seed
                var xoro = new Xoroshiro128Plus(seed);
                _ = xoro.NextInt(uint.MaxValue); // Skip first value
                uint fakeTID = (uint)xoro.NextInt(uint.MaxValue);
                uint pid = (uint)xoro.NextInt(uint.MaxValue);

                // Use ShinyUtil directly to check
                if (ShinyUtil.GetShinyXor(pid, fakeTID) < 16)
                    break;
            }
            while (true);

            return seed;
        }

        /// <summary>
        /// Extracts the Tera type from an embed
        /// </summary>
        private static string ExtractTeraTypeFromEmbed(Embed embed)
        {
            var statsField = embed.Fields.FirstOrDefault(f => f.Name == "**__Stats__**");
            if (statsField != null)
            {
                var lines = statsField.Value.Split('\n');
                var teraTypeLine = lines.FirstOrDefault(l => l.StartsWith("**TeraType:**"));
                if (teraTypeLine != null)
                {
                    var parts = teraTypeLine.Split(':');
                    if (parts.Length >= 2)
                    {
                        var teraType = parts[1].Trim();
                        teraType = teraType.Replace("*", "").Trim();
                        return teraType;
                    }
                }
            }
            return "Fairy"; // Default value if something goes wrong
        }

        /// <summary>
        /// Gets appropriate battlers for a specific Tera type
        /// </summary>
        private string[] GetBattlerForTeraType(string teraType)
        {
            var battlers = _settings.RaidSettings.MysteryRaidsSettings.TeraTypeBattlers;
            return teraType switch
            {
                "Bug" => battlers.BugBattler,
                "Dark" => battlers.DarkBattler,
                "Dragon" => battlers.DragonBattler,
                "Electric" => battlers.ElectricBattler,
                "Fairy" => battlers.FairyBattler,
                "Fighting" => battlers.FightingBattler,
                "Fire" => battlers.FireBattler,
                "Flying" => battlers.FlyingBattler,
                "Ghost" => battlers.GhostBattler,
                "Grass" => battlers.GrassBattler,
                "Ground" => battlers.GroundBattler,
                "Ice" => battlers.IceBattler,
                "Normal" => battlers.NormalBattler,
                "Poison" => battlers.PoisonBattler,
                "Psychic" => battlers.PsychicBattler,
                "Rock" => battlers.RockBattler,
                "Steel" => battlers.SteelBattler,
                "Water" => battlers.WaterBattler,
                _ => []
            };
        }

        /// <summary>
        /// The GenPKM request behind the raid being hosted now, if it is one
        /// </summary>
        private WebRaidRequest? CurrentWebRequest =>
            _currentRaidIndex >= 0 && _currentRaidIndex < _settings.ActiveRaids.Count
                ? _settings.ActiveRaids[_currentRaidIndex].WebRequest
                : null;

        private string GetWebHostName()
        {
            var name = _settings.RaidSettings.GenPKMHostName;
            if (string.IsNullOrWhiteSpace(name))
                name = _hostSAV.OT;
            return string.IsNullOrWhiteSpace(name) ? "Raid bot" : name.Trim();
        }

        /// <summary>
        /// Stable per install and per console, so the site can tell this bot
        /// apart from a copy of the same config running another Switch.
        /// </summary>
        private string GetWebBotId()
        {
            if (string.IsNullOrEmpty(_settings.RaidSettings.GenPKMBotId))
                _settings.RaidSettings.GenPKMBotId = Guid.NewGuid().ToString("N");

            // The console's address stays on this PC; only a short hash of it is sent.
            var console = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Config.Connection.ToString())))[..12];
            return $"{_settings.RaidSettings.GenPKMBotId[..16]}-{console}";
        }

        private static TeraRaidMapParent CurrentMap =>
            IsBlueberry ? TeraRaidMapParent.Blueberry : IsKitakami ? TeraRaidMapParent.Kitakami : TeraRaidMapParent.Paldea;

        /// <summary>
        /// Asks genpkm.com for a member's raid request when nothing else is
        /// waiting, and queues it as the next raid. Never throws: a missing or
        /// slow site must not interrupt hosting.
        /// </summary>
        private async Task TryTakeWebRequest(CancellationToken token)
        {
            _settings.ActiveRaids.RemoveAll(p => p.WebRequest is { Lost: true } && p.WebRequest.Host == this);

            var raidSettings = _settings.RaidSettings;
            if (!raidSettings.HostGenPKMRequests || raidSettings.DisableRequests || _isPaused)
                return;
            if (DateTime.Now < _nextWebClaimAllowed || FindNextUserRequestedRaid() != -1)
                return;
            if (Container is null || string.IsNullOrEmpty(Container.Game))
                return;

            // Map, game and event data are shared by every raid bot in this
            // program, so with two consoles neither could promise the right map.
            if (_hub.Bots.CountWhere(b => b.Config.CurrentRoutineType != PokeRoutineType.Idle) > 1)
            {
                if (!_warnedWebMultipleBots)
                    Log("GenPKM raid requests are off: they need one raid bot per program window.");
                _warnedWebMultipleBots = true;
                return;
            }

            try
            {
                var map = CurrentMap;
                var info = new WebRaidBotInfo(
                    GetWebBotId(),
                    GetWebHostName(),
                    Helpers.SVRaidBot.Version,
                    Container.Game,
                    map.ToString(),
                    map == TeraRaidMapParent.Paldea ? [.. SpeciesToGroupIDMap.Keys] : []);

                // The previous request's done/missed report must land first, or
                // the site reads this claim as a bot that crashed mid-raid.
                await WebRequests.Flush(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var (result, request) = await WebRequests.TryClaim(info, token).ConfigureAwait(false);
                switch (result)
                {
                    case WebClaimResult.NotApproved:
                        if (!_warnedWebNotApproved)
                            Log("GenPKM raid requests: this host is not approved yet, so it will not receive requests. Checking again every 30 minutes.");
                        _warnedWebNotApproved = true;
                        _nextWebClaimAllowed = DateTime.Now.AddMinutes(30);
                        return;
                    case WebClaimResult.Unreachable:
                        _nextWebClaimAllowed = DateTime.Now.AddMinutes(2);
                        return;
                    case WebClaimResult.NothingWaiting:
                        _warnedWebNotApproved = false;
                        return;
                }

                request!.Host = this;
                var (raid, problem, giveBack) = BuildWebRaid(request, map);
                if (raid is null)
                {
                    Log($"GenPKM request #{request.Id} could not be hosted: {problem}");
                    WebRequests.Report(request, giveBack ? "released" : "rejected", problem);
                    return;
                }

                _settings.ActiveRaids.Add(raid);
                WebRequests.Report(request, "preparing", $"{GetWebHostName()} is restarting its game and loading your seed.");
                Log($"Picked up GenPKM request #{request.Id}: {raid.Species} ({request.Stars}★, seed {request.Seed}) for {request.RequesterName}.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"GenPKM raid requests: {ex.Message}");
                _nextWebClaimAllowed = DateTime.Now.AddMinutes(2);
            }
        }

        /// <summary>
        /// Turns a claimed request into a raid entry after checking it against
        /// this console's game. Returns the reason and whether another bot
        /// could still host it when this one can't.
        /// </summary>
        private (RotatingRaidParameters? Raid, string Problem, bool GiveBack) BuildWebRaid(WebRaidRequest request, TeraRaidMapParent map)
        {
            if (!WebRaidRules.IsValidSeed(request.Seed) || !WebRaidRules.StarsFitProgress(request.Stars, request.StoryProgress))
                return (null, "The seed or difficulty in this request is not valid.", false);

            int groupId = -1;
            if (request.IsEvent)
            {
                if (map != TeraRaidMapParent.Paldea)
                    return (null, "Event raids only run in Paldea.", true);
                if (string.IsNullOrWhiteSpace(request.Species) || !SpeciesToGroupIDMap.TryGetValue(request.Species, out var groups) || groups.Count == 0)
                    return (null, $"This bot no longer has the {request.Species} event.", true);
                groupId = groups[0].GroupID;
            }

            var crystal = WebRaidRules.CrystalFor(request.Stars, request.IsEvent);
            var embedSettings = _settings.EmbedToggles;
            PK9 pk;
            Embed embed;
            try
            {
                (pk, embed) = RaidInfoCommand(request.Seed, (int)crystal, map, request.StoryProgress, groupId,
                    embedSettings.RewardsToShow, embedSettings.MoveTypeEmojis, embedSettings.CustomTypeEmojis,
                    0, request.IsEvent, (int)embedSettings.EmbedLanguage);
            }
            catch (Exception ex)
            {
                return (null, $"Seed {request.Seed} does not make a {request.Stars}★ raid here ({ex.Message}).", false);
            }

            if (pk.Species == 0)
                return (null, "The bot has not finished reading its raid data yet.", true);

            var made = (Species)pk.Species;
            if (!request.IsEvent && !string.IsNullOrWhiteSpace(request.Species) && !WebRaidRules.SameSpecies(request.Species, made.ToString()))
            {
                return (null, $"On {Container!.Game} in {map} with that story progress, seed {request.Seed} makes {made}, not {request.Species}. " +
                    "Check the game, map and story progress you searched with.", false);
            }

            var battlers = GetBattlerForTeraType(ExtractTeraTypeFromEmbed(embed));
            var raid = new RotatingRaidParameters
            {
                CrystalType = crystal,
                Description = [""],
                PartyPK = made == Species.Ditto ? ["Happiny", "Shiny: Yes", "Level: 1"] : battlers.Length > 0 ? battlers : [""],
                Species = made,
                SpeciesForm = pk.Form,
                DifficultyLevel = request.Stars,
                StoryProgress = (GameProgressEnum)(request.StoryProgress - 1),
                Seed = request.Seed.ToUpperInvariant(),
                IsCoded = true,
                IsShiny = pk.IsShiny,
                GroupID = groupId,
                AddedByRACommand = true,
                RequestCommand = $"genpkm.com request #{request.Id}",
                Title = $"A GenPKM Member{UserRequestedRaidSuffix}{(request.IsEvent ? $" ({request.Species} Event Raid)" : "")}",
                WebRequest = request,
            };
            return (raid, string.Empty, false);
        }

        /// <summary>
        /// Determines if the current raid should be treated as free-for-all (no code required)
        /// </summary>
        /// <returns>True if the raid should be free-for-all, false if it should use a raid code</returns>
        private bool IsFreeForAllRaid()
        {
            var currentRaid = _settings.ActiveRaids[_currentRaidIndex];

            // If raid is not coded at all, it's not a "free-for-all" - it's just uncoded
            if (!currentRaid.IsCoded || currentRaid.WebRequest != null)
                return false;

            // If we've hit the empty raid limit and OpenLobby is enabled, make it free-for-all
            if (_emptyRaid >= _settings.LobbyOptions.EmptyRaidLimit &&
                _settings.LobbyOptions.LobbyMethod == LobbyMethodOptions.OpenLobby)
                return true;

            // Otherwise, use the raid code
            return false;
        }

        /// <summary>
        /// Checks if there are no active raids in the rotation
        /// </summary>
        private bool NoActiveRaids => _settings.ActiveRaids.Count == 0;

        /// <summary>
        /// Selects the next raid to host based on priority: User Requests > Mystery Raids > Configured Raids
        /// </summary>
        /// <returns>Index of the raid to host next, or -1 if no raids available</returns>
        private int SelectNextRaidIndex()
        {
            if (NoActiveRaids)
                return -1;

            // Priority 1: User requested raids (highest priority)
            int userRequestIndex = FindNextUserRequestedRaid();
            if (userRequestIndex != -1)
                return userRequestIndex;

            // Priority 2: Mystery raids (if no user requests)
            int mysteryIndex = FindNextMysteryRaid();
            if (mysteryIndex != -1)
                return mysteryIndex;

            // Priority 3: Configured raids (lowest priority)
            return SelectNextConfiguredRaid();
        }

        /// <summary>
        /// Finds the next user-requested raid in the list
        /// </summary>
        private int FindNextUserRequestedRaid()
        {
            return _settings.ActiveRaids.FindIndex(r =>
                r.AddedByRACommand &&
                !r.Title.Contains(MysteryRaidTitle) &&
                r.ActiveInRotation &&
                (r.WebRequest is null || (r.WebRequest.Host == this && !r.WebRequest.Lost)));
        }

        /// <summary>
        /// Finds the next mystery raid in the list
        /// </summary>
        private int FindNextMysteryRaid()
        {
            return _settings.ActiveRaids.FindIndex(r =>
                r.AddedByRACommand &&
                r.Title.Contains(MysteryRaidTitle) &&
                r.ActiveInRotation);
        }

        /// <summary>
        /// Selects the next configured raid based on rotation mode (sequential or random)
        /// </summary>
        private int SelectNextConfiguredRaid()
        {
            var configuredRaids = _settings.ActiveRaids
                .Select((raid, index) => new { raid, index })
                .Where(x => !x.raid.AddedByRACommand && x.raid.ActiveInRotation)
                .ToList();

            if (configuredRaids.Count == 0)
                return -1;

            if (_settings.RaidSettings.RandomRotation)
            {
                // Randomly pick from active configured raids
                var random = new Random();
                return configuredRaids[random.Next(configuredRaids.Count)].index;
            }

            // Sequential rotation through configured raids
            if (_nextConfiguredRaidRotationIndex >= configuredRaids.Count)
                _nextConfiguredRaidRotationIndex = 0;

            var selected = configuredRaids[_nextConfiguredRaidRotationIndex];
            _nextConfiguredRaidRotationIndex++;

            return selected.index;
        }

        /// <summary>
        /// Ensures _currentRaidIndex is within valid bounds
        /// </summary>
        private void Ensure_currentRaidIndexInBounds()
        {
            if (NoActiveRaids)
            {
                _currentRaidIndex = 0;
                return;
            }

            if (_currentRaidIndex >= _settings.ActiveRaids.Count)
            {
                _currentRaidIndex = 0;
            }
        }

        /// <summary>
        /// Checks if the current raid is a temporary raid (Mystery or User Requested) that should be removed after completion
        /// </summary>
        /// <returns>True if the raid is temporary and should be removed</returns>
        private bool IsTemporaryRaid()
        {
            var currentRaid = _settings.ActiveRaids[_currentRaidIndex];

            if (!currentRaid.AddedByRACommand)
                return false;

            bool isMysteryRaid = currentRaid.Title.Contains(MysteryRaidTitle);
            bool isUserRequestedRaid = !isMysteryRaid && currentRaid.Title.Contains(UserRequestedRaidSuffix);

            return isMysteryRaid || isUserRequestedRaid;
        }

        /// <summary>
        /// Removes the current raid from rotation if it's a temporary raid, and adjusts rotation count
        /// </summary>
        /// <param name="reason">Reason for removal (e.g., "completed" or "skipped")</param>
        private void RemoveTemporaryRaidIfNeeded(string reason)
        {
            if (IsTemporaryRaid())
            {
                if (_settings.ActiveRaids[_currentRaidIndex].WebRequest is { } web)
                {
                    var (status, message) = web.OutcomeOverride ?? (reason == "completed"
                        ? ("done", "Your raid is complete. You can request another one now.")
                        : ("missed", "Nobody joined the lobby before it closed. You can request it again now."));
                    WebRequests.Report(web, status, message);
                }

                Log($"Raid for {_settings.ActiveRaids[_currentRaidIndex].Species} was {reason} and will be removed from the rotation list.");
                _settings.ActiveRaids.RemoveAt(_currentRaidIndex);
                Ensure_currentRaidIndexInBounds();
            }
        }

        /// <summary>
        /// Selects the next raid to host using the priority system
        /// </summary>
        private async Task SanitizeRotationCount(CancellationToken token)
        {
            try
            {
                await Task.Delay(50, token).ConfigureAwait(false);

                await TryTakeWebRequest(token).ConfigureAwait(false);

                if (NoActiveRaids)
                {
                    Log("ActiveRaids is empty. Exiting SanitizeRotationCount.");
                    _currentRaidIndex = 0;
                    RotationCount = 0;
                    return;
                }

                int previousIndex = _currentRaidIndex;

                // Use the new priority-based selection system
                _currentRaidIndex = SelectNextRaidIndex();
                RotationCount = _currentRaidIndex;

                if (_currentRaidIndex == -1)
                {
                    Log("No active raids found. Enabling all raids and starting from 0.");
                    // Emergency fallback: re-enable all raids if none are enabled
                    for (int i = 0; i < _settings.ActiveRaids.Count; i++)
                    {
                        _settings.ActiveRaids[i].ActiveInRotation = true;
                    }
                    _currentRaidIndex = SelectNextRaidIndex();
                    if (_currentRaidIndex == -1)
                        _currentRaidIndex = 0;
                    RotationCount = _currentRaidIndex;
                }

                // Update RaidUpNext for the selected raid
                for (int i = 0; i < _settings.ActiveRaids.Count; i++)
                {
                    _settings.ActiveRaids[i].RaidUpNext = i == _currentRaidIndex;
                }

                // Mark first run as complete
                if (_firstRun)
                {
                    _firstRun = false;
                }

                var nextRaid = _settings.ActiveRaids[_currentRaidIndex];
                string raidIdentifier = nextRaid.Title.Contains(MysteryRaidTitle)
                    ? nextRaid.Title
                    : nextRaid.Species.ToString();
                Log($"Next raid in the list: {raidIdentifier} (_currentRaidIndex: {_currentRaidIndex}, Previous: {previousIndex}).");
            }
            catch (Exception ex)
            {
                Log($"Error in SanitizeRotationCount. Resetting _currentRaidIndex to 0. {ex.Message}");
                _currentRaidIndex = 0;
                RotationCount = 0;
            }
        }

        /// <summary>
        /// Finds the next enabled raid index starting from a given index
        /// </summary>
        private int FindNextEnabledRaidIndex(int startIndex)
        {
            for (int i = 0; i < _settings.ActiveRaids.Count; i++)
            {
                int index = (startIndex + i) % _settings.ActiveRaids.Count;
                if (_settings.ActiveRaids[index].ActiveInRotation)
                {
                    return index;
                }
            }
            return -1; // No enabled raids found
        }

        /// <summary>
        /// Finds the next priority raid index starting from the current rotation count
        /// </summary>
        private int FindNextPriorityRaidIndex(int current_currentRaidIndex, List<RotatingRaidParameters> raids)
        {
            if (raids == null || raids.Count == 0)
            {
                return current_currentRaidIndex;
            }

            int count = raids.Count;

            // Priority 1: Check for user-requested RA command raids (highest priority)
            for (int i = 1; i <= count; i++)
            {
                int index = (current_currentRaidIndex + i) % count;
                RotatingRaidParameters raid = raids[index];
                if (raid.ActiveInRotation && raid.AddedByRACommand && !raid.Title.Contains(MysteryRaidTitle))
                {
                    return index; // Prioritize user-requested raids first
                }
            }

            // Priority 2: Check for Mystery Shiny Raids (if no user requests found)
            if (_settings.RaidSettings.MysteryRaids)
            {
                for (int i = 1; i <= count; i++)
                {
                    int index = (current_currentRaidIndex + i) % count;
                    RotatingRaidParameters raid = raids[index];
                    if (raid.ActiveInRotation && raid.Title.Contains(MysteryRaidTitle))
                    {
                        return index; // Prioritize Mystery Shiny Raids over configured raids
                    }
                }
            }

            // Return -1 if no priority raids are found
            return -1;
        }

        /// <summary>
        /// Processes random rotation of raids if enabled
        /// </summary>
        private void ProcessRandomRotation()
        {
            // Turn off RandomRotation if both RandomRotation and MysteryRaid are true
            if (_settings.RaidSettings.RandomRotation && _settings.RaidSettings.MysteryRaids)
            {
                _settings.RaidSettings.RandomRotation = false;
                Log("RandomRotation turned off due to MysteryRaids being active.");
                return;
            }

            // Check the remaining raids for any added by the RA command
            for (var i = 0; i < _settings.ActiveRaids.Count; i++)
            {
                if (_settings.ActiveRaids[i].ActiveInRotation && _settings.ActiveRaids[i].AddedByRACommand)
                {
                    _currentRaidIndex = i;
                    Log($"Setting Rotation Count to {_currentRaidIndex}");
                    return;
                }
            }

            // If no raid added by RA command was found, select a random enabled raid
            var random = new Random();
            var enabledRaids = _settings.ActiveRaids.Where(r => r.ActiveInRotation).ToList();
            if (enabledRaids.Count > 0)
            {
                int randomIndex = random.Next(enabledRaids.Count);
                _currentRaidIndex = _settings.ActiveRaids.IndexOf(enabledRaids[randomIndex]);
                Log($"Setting Rotation Count to {_currentRaidIndex}");
            }
            else
            {
                Log("No enabled raids found for random rotation.");
                _currentRaidIndex = 0;
            }
        }

        /// <summary>
        /// Injects a party Pokemon for the raid
        /// </summary>
        private async Task InjectPartyPk(string battlePk, CancellationToken token)
        {
            var set = new ShowdownSet(battlePk);
            var template = AutoLegalityWrapper.GetTemplate(set);
            PK9 pk = (PK9)_hostSAV.GetLegal(template, out _);
            pk.ResetPartyStats();
            var offset = await SwitchConnection.PointerAll(Offsets.BoxStartPokemonPointer, token).ConfigureAwait(false);
            await SwitchConnection.WriteBytesAbsoluteAsync(pk.EncryptedBoxData, offset, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Prepares for a raid by setting up the game state
        /// </summary>
        private async Task<int> PrepareForRaid(CancellationToken token)
        {
            try
            {
                if (_isPaused)
                {
                    Log("Bot is paused. Skipping raid preparation.");
                    return 0;
                }
                (bool valid, ulong freshOffset) = await ValidatePointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
                if (valid)
                {
                    if (await IsOnOverworld(freshOffset, token).ConfigureAwait(false))
                    {
                        _overworldOffset = freshOffset;
                    }
                    else
                    {
                        Log("Not on overworld (validated pointer check), reopening game.");
                        await ReOpenGame(_hub.Config, token).ConfigureAwait(false);
                        return 0;
                    }
                }
                else
                {
                    Log("Failed to validate overworld pointer, reopening game.");
                    await ReOpenGame(_hub.Config, token).ConfigureAwait(false);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Log($"Error validating overworld pointer: {ex.Message}");
                await ReOpenGame(_hub.Config, token).ConfigureAwait(false);
                return 0;
            }

            // Check if player is near any active den
            var playerLocation = await GetPlayersLocation(token);
            var currentRegion = await DetectCurrentRegion(token);
            var activeRaids = await GetActiveRaidLocations(currentRegion, token);

            if (activeRaids.Count > 0)
            {
                // Find nearest active den
                var nearestRaid = activeRaids
                    .OrderBy(raid => CalculateDistance(playerLocation,
                        (raid.Coordinates[0], raid.Coordinates[1], raid.Coordinates[2])))
                    .First();

                float distance = CalculateDistance(playerLocation,
                    (nearestRaid.Coordinates[0], nearestRaid.Coordinates[1], nearestRaid.Coordinates[2]));

                if (distance > TeleportDistanceThreshold)
                {
                    if (_teleportRetryCount < MaxTeleportRetries)
                    {
                        _teleportRetryCount++;
                        Log($"Player is too far from nearest den (distance: {distance:F2}, threshold: {TeleportDistanceThreshold}). Retry attempt {_teleportRetryCount}/{MaxTeleportRetries}. Restarting game to teleport.");
                        await CloseGame(_hub.Config, token).ConfigureAwait(false);
                        await StartGameRaid(_hub.Config, token).ConfigureAwait(false);
                        return 2;
                    }
                    else
                    {
                        // Max retries exceeded, continue anyway and reset counter
                        Log($"Player is still far from nearest den after {MaxTeleportRetries} retries (distance: {distance:F2}). Continuing anyway...");
                        _teleportRetryCount = 0;
                    }
                }
                else
                {
                    // Player is close enough, reset retry counter
                    _teleportRetryCount = 0;
                    Log($"Player is near den (distance: {distance:F2}, threshold: {TeleportDistanceThreshold}).");
                }
            }
            else
            {
                if (_teleportRetryCount < MaxTeleportRetries)
                {
                    _teleportRetryCount++;
                    Log($"No active dens found. Retry attempt {_teleportRetryCount}/{MaxTeleportRetries}. Restarting game to find and teleport to a valid den.");
                    await CloseGame(_hub.Config, token).ConfigureAwait(false);
                    await StartGameRaid(_hub.Config, token).ConfigureAwait(false);
                    return 2;
                }
                else
                {
                    Log($"No active dens found after {MaxTeleportRetries} retries. Continuing anyway...");
                    _teleportRetryCount = 0;
                }
            }

            if (!await ConnectToOnline(_hub.Config, token))
            {
                // ConnectToOnline already handles retries and waiting periods
                // If it returns false, we've likely tried multiple times and failed
                Log("Failed to connect online after multiple attempts. Returning to preparation.");
                return 0;
            }

            if (_shouldRefreshMap)
            {
                _shouldRefreshMap = false;
                Log("Seeds have mismatched multiple times. Executing map refresh.");

                Log("Starting Refresh map process...");
                await HardStop().ConfigureAwait(false);
                await Task.Delay(2_000, token).ConfigureAwait(false);
                await Click(B, 3_000, token).ConfigureAwait(false);
                await Click(B, 3_000, token).ConfigureAwait(false);
                await GoHome(_hub.Config, token).ConfigureAwait(false);
                await AdvanceDaySV(token).ConfigureAwait(false);
                await SaveGame(_hub.Config, token).ConfigureAwait(false);
                await RecoverToOverworld(token).ConfigureAwait(false);

                // Reset seed mismatch counter
                _seedMismatchCount = 0;

                // Return code 2 to signal the calling method that we've refreshed the map
                Log("Map Refresh Completed. Continuing...");
                return 2;
            }

            var currentSeed = _settings.ActiveRaids[_currentRaidIndex].Seed.ToUpper();

            if (!_denHexSeed.Equals(currentSeed, StringComparison.CurrentCultureIgnoreCase))
            {
                _seedMismatchCount++;
                Log($"Raid Den and Current Seed do not match. Mismatch count: {_seedMismatchCount}");

                if (_seedMismatchCount >= 2)
                {
                    Log("Seeds have mismatched 2 times in a row. Refreshing the map.");
                    _shouldRefreshMap = true;
                    _seedMismatchCount = 0;
                    return 2;
                }

                await Task.Delay(4_000, token).ConfigureAwait(false);
                Log("Injecting correct seed.");
                await CloseGame(_hub.Config, token).ConfigureAwait(false);
                await StartGameRaid(_hub.Config, token).ConfigureAwait(false);
                Log("Seed injected Successfully!");
                return 2;
            }
            else
            {
                _seedMismatchCount = 0;
            }

            if (CurrentWebRequest is { } web)
            {
                // Make sure the request is still ours before opening a lobby for
                // it: after a long pause the site may have handed it to another
                // bot. A re-host after a failed battle start skips this and goes
                // straight to a new lobby code; the site does not accept a step
                // backward.
                if (!web.IsHosted)
                {
                    var ours = await WebRequests.Confirm(web, "preparing",
                        "Your seed is loaded. The lobby opens in a few seconds, so get to the Tera Raid screen.", token).ConfigureAwait(false);
                    if (ours == false)
                    {
                        Log($"GenPKM request #{web.Id} went to another bot while this one was busy. Skipping it.");
                        _settings.ActiveRaids.RemoveAt(_currentRaidIndex);
                        Ensure_currentRaidIndexInBounds();
                        await SanitizeRotationCount(token).ConfigureAwait(false);
                        await CloseGame(_hub.Config, token).ConfigureAwait(false);
                        await StartGameRaid(_hub.Config, token).ConfigureAwait(false);
                        return 2;
                    }
                }
            }
            else if (_settings.ActiveRaids[_currentRaidIndex].AddedByRACommand)
            {
                var user = _settings.ActiveRaids[_currentRaidIndex].User;
                var mentionedUsers = _settings.ActiveRaids[_currentRaidIndex].MentionedUsers;

                if (!IsFreeForAllRaid())
                {
                    try
                    {
                        // Only send the message if it's not a "Free For All"
                        if (user != null)
                        {
                            await user.SendMessageAsync("Get Ready! Your raid is being prepared now!").ConfigureAwait(false);
                        }

                        foreach (var mentionedUser in mentionedUsers)
                        {
                            await mentionedUser.SendMessageAsync($"Get Ready! The raid you were invited to by {user?.Username ?? "the host"} is about to start!").ConfigureAwait(false);
                            await Task.Delay(300, token).ConfigureAwait(false);
                        }
                    }
                    catch (Discord.Net.HttpException ex)
                    {
                        Log($"Failed to send DM to the user or mentioned users. They might have DMs turned off. Exception: {ex.Message}");
                    }
                }
            }

            if (!await RecoverToOverworld(token).ConfigureAwait(false))
                return 0;
            await Task.Delay(0_500, token).ConfigureAwait(false);
            await SwitchPartyPokemon(token).ConfigureAwait(false);
            await Task.Delay(1_500, token).ConfigureAwait(false);
            if (!await RecoverToOverworld(token).ConfigureAwait(false))
                return 0;
            Log("Preparing lobby...");
            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);

            // Select "Don't use a Link Code" if raid is uncoded or free-for-all
            if (!_settings.ActiveRaids[_currentRaidIndex].IsCoded || IsFreeForAllRaid())
            {
                if (IsFreeForAllRaid())
                    Log($"Empty raid limit reached ({_settings.LobbyOptions.EmptyRaidLimit}). Opening this raid to all!");
                await Click(DDOWN, 1_000, token).ConfigureAwait(false);
            }

            await Click(A, 4_000, token).ConfigureAwait(false);
            return 1;
        }

        /// <summary>
        /// Switches to the configured party Pokemon for the raid
        /// </summary>
        private async Task SwitchPartyPokemon(CancellationToken token)
        {
            LobbyFiltersCategory settings = new();
            var len = string.Empty;
            foreach (var l in _settings.ActiveRaids[_currentRaidIndex].PartyPK)
                len += l;

            // Always switch party Pokemon if configured, regardless of empty raid count
            if (len.Length > 1)
            {
                Log("Preparing PartyPK. Sit tight.");
                await Task.Delay(3_000 + settings.ExtraTimePartyPK, token).ConfigureAwait(false);
                await SetCurrentBox(0, token).ConfigureAwait(false);
                var res = string.Join("\n", _settings.ActiveRaids[_currentRaidIndex].PartyPK);

                if (res.Length > 4096)
                    res = res[..4096];

                await InjectPartyPk(res, token).ConfigureAwait(false);

                await Click(X, 2_000, token).ConfigureAwait(false);
                await Click(DRIGHT, 0_500, token).ConfigureAwait(false);
                await SetStick(SwitchStick.LEFT, 0, -32000, 1_000, token).ConfigureAwait(false);
                await SetStick(SwitchStick.LEFT, 0, 0, 0, token).ConfigureAwait(false);

                for (int i = 0; i < 2; i++)
                    await Click(DDOWN, 0_500, token).ConfigureAwait(false);

                await Click(A, 3_500, token).ConfigureAwait(false);
                await Click(Y, 0_500, token).ConfigureAwait(false);
                await Click(DLEFT, 0_800, token).ConfigureAwait(false);
                await Click(Y, 0_500, token).ConfigureAwait(false);
                Log("PartyPK switch successful.");
            }
        }

        /// <summary>
        /// Recovers to the overworld from various game states
        /// </summary>
        private async Task<bool> RecoverToOverworld(CancellationToken token)
        {
            if (await IsOnOverworld(_overworldOffset, token).ConfigureAwait(false))
            {
                return true;
            }

            var attempts = 0;
            const int maxAttempts = 30;

            while (!await IsOnOverworld(_overworldOffset, token).ConfigureAwait(false))
            {
                attempts++;
                if (attempts >= maxAttempts)
                {
                    Log($"Recovery exceeded maximum attempts ({maxAttempts})");
                    break;
                }

                for (int i = 0; i < 20; i++)
                {
                    await Click(B, 1_000, token).ConfigureAwait(false);
                    if (await IsOnOverworld(_overworldOffset, token).ConfigureAwait(false))
                    {
                        Log($"Successfully reached overworld after {attempts} attempts");
                        return true;
                    }
                }
            }

            // We didn't make it for some reason.
            if (!await IsOnOverworld(_overworldOffset, token).ConfigureAwait(false))
            {
                Log("Failed to recover to overworld, rebooting the game.");
                return false;
            }

            await Task.Delay(1_000, token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Rolls back the game time by multiple hours
        /// </summary>
        private async Task RollBackTime(CancellationToken token)
        {
            for (int i = 0; i < 2; i++)
                await Click(B, 0_150, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(DRIGHT, 0_150, token).ConfigureAwait(false);

            await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false); // Enter settings

            await PressAndHold(DDOWN, 2_000, 0_250, token).ConfigureAwait(false); // Scroll to system settings
            await Click(A, 1_250, token).ConfigureAwait(false);

            if (_settings.MiscSettings.UseOvershoot)
            {
                await PressAndHold(DDOWN, _settings.MiscSettings.HoldTimeForRollover, 1_000, token).ConfigureAwait(false);
                await Click(DUP, 0_500, token).ConfigureAwait(false);
            }
            else
            {
                for (int i = 0; i < 39; i++)
                    await Click(DDOWN, 0_100, token).ConfigureAwait(false);
            }

            await Click(A, 1_250, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(DDOWN, 0_150, token).ConfigureAwait(false);

            await Click(A, 0_500, token).ConfigureAwait(false);

            for (int i = 0; i < 3; i++) // Navigate to the hour setting
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            for (int i = 0; i < 5; i++) // Roll back the hour by 5
                await Click(DDOWN, 0_200, token).ConfigureAwait(false);

            for (int i = 0; i < 8; i++) // Mash DRIGHT to confirm
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(A, 0_200, token).ConfigureAwait(false); // Confirm date/time change
            await Click(HOME, 1_000, token).ConfigureAwait(false); // Back to title screen
        }

        /// <summary>
        /// Waits for the lobby to be ready
        /// </summary>
        private async Task<bool> GetLobbyReady(bool recovery, CancellationToken token)
        {
            var x = 0;
            const int maxAttempts = 45;
            const int recoveryMaxAttempts = 15;

            Log("Connecting to lobby...");
            while (!await IsConnectedToLobby(token).ConfigureAwait(false))
            {
                await Click(A, 1_000, token).ConfigureAwait(false);
                x++;

                if (x == recoveryMaxAttempts && recovery)
                {
                    Log("No den here! Rolling again.");
                    return false;
                }

                if (x == maxAttempts)
                {
                    Log("Failed to connect to lobby, restarting game incase we were in battle/bad connection.");
                    _lobbyError++;

                    if (_seedIndexToReplace == _lastFailedDenIndex)
                    {
                        _consecutiveDenFailures++;
                        Log($"Consecutive failures at den index {_seedIndexToReplace}: {_consecutiveDenFailures}");
                    }
                    else
                    {
                        _consecutiveDenFailures = 1;
                        _lastFailedDenIndex = _seedIndexToReplace;
                        Log($"First failure at den index {_seedIndexToReplace}");
                    }

                    await ReOpenGame(_hub.Config, token).ConfigureAwait(false);
                    Log("Attempting to restart routine!");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Gets the raid code from memory
        /// </summary>
        private async Task<string> GetRaidCode(CancellationToken token)
        {
            var data = await SwitchConnection.PointerPeek(6, Offsets.TeraRaidCodePointer, token).ConfigureAwait(false);
            var code = Encoding.ASCII.GetString(data);
            _teraRaidCode = _settings.EmbedToggles.RaidCodeCase == RaidCodeCaseOptions.Uppercase
                ? code.ToUpper()
                : code.ToLower();
            return $"{_teraRaidCode}";
        }

        /// <summary>
        /// Checks if a trainer is banned
        /// </summary>
        private async Task<bool> CheckIfTrainerBanned(RaidMyStatus trainer, ulong nid, int player, CancellationToken token)
        {
            _raidTracker.TryAdd(nid, 0);
            string msg;
            var banResultCFW = RaiderBanList.List.FirstOrDefault(x => x.ID == nid);
            bool isBanned = banResultCFW != default;

            if (isBanned)
            {
                msg = $"{banResultCFW!.Name} was found in the host's ban list.\n{banResultCFW.Comment}";
                Log(msg);
                await CurrentRaidInfo(null, "", false, true, false, false, null, false, token).ConfigureAwait(false);
                await EnqueueEmbed(null, msg, false, true, false, false, token).ConfigureAwait(false);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Cleaned up ReadTrainers to remove duplicate lobby logic
        /// </summary>
        private async Task<(bool, List<(ulong, RaidMyStatus)>)> ReadTrainers(CancellationToken token)
        {
            if (!await IsConnectedToLobby(token))
                return (false, new List<(ulong, RaidMyStatus)>());
            await EnqueueEmbed(null, "", false, false, false, false, token).ConfigureAwait(false);

            List<(ulong, RaidMyStatus)> lobbyTrainers = [];
            TimeSpan wait;

            if (_settings.ActiveRaids[_currentRaidIndex].AddedByRACommand &&
                _settings.ActiveRaids[_currentRaidIndex].Title != MysteryRaidTitle)
            {
                wait = TimeSpan.FromSeconds(160) - TimeSpan.FromMilliseconds((int)_settings.EmbedToggles.RequestEmbedTime);
            }
            else
            {
                wait = TimeSpan.FromSeconds(160);
            }

            var endTime = DateTime.Now + wait;
            bool full = false;

            while (!full && DateTime.Now < endTime)
            {
                if (!await IsConnectedToLobby(token))
                    return (false, lobbyTrainers);

                for (int i = 0; i < 3; i++)
                {
                    var player = i + 2;
                    Log($"Waiting for Player {player} to load...");

                    if (!await IsConnectedToLobby(token))
                        return (false, lobbyTrainers);

                    var nidOfs = _teraNIDOffsets[i];
                    var data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                    var nid = BitConverter.ToUInt64(data, 0);
                    while (nid == 0 && DateTime.Now < endTime)
                    {
                        await Task.Delay(0_500, token).ConfigureAwait(false);

                        if (!await IsConnectedToLobby(token))
                            return (false, lobbyTrainers);

                        data = await SwitchConnection.ReadBytesAbsoluteAsync(nidOfs, 8, token).ConfigureAwait(false);
                        nid = BitConverter.ToUInt64(data, 0);
                    }

                    List<long> ptr = [.. Offsets.Trader2MyStatusPointer];
                    ptr[2] += i * 0x30;
                    var trainer = await GetTradePartnerMyStatus(ptr, token).ConfigureAwait(false);

                    while (trainer.OT.Length == 0 && DateTime.Now < endTime)
                    {
                        await Task.Delay(0_500, token).ConfigureAwait(false);

                        if (!await IsConnectedToLobby(token))
                            return (false, lobbyTrainers);

                        trainer = await GetTradePartnerMyStatus(ptr, token).ConfigureAwait(false);
                    }

                    if (nid != 0 && !string.IsNullOrWhiteSpace(trainer.OT))
                    {
                        if (await CheckIfTrainerBanned(trainer, nid, player, token).ConfigureAwait(false))
                            return (false, lobbyTrainers);
                    }

                    if (lobbyTrainers.Any(x => x.Item1 == nid))
                    {
                        Log($"Duplicate NID detected: {nid}. Skipping...");
                        continue;
                    }

                    if (nid > 0 && trainer.OT.Length > 0)
                        lobbyTrainers.Add((nid, trainer));

                    full = lobbyTrainers.Count == 3;
                    if (full)
                    {
                        List<string> trainerNames = lobbyTrainers.Select(t => t.Item2.OT).ToList();
                        await CurrentRaidInfo(trainerNames, "", false, false, false, false, null, true, token).ConfigureAwait(false);
                    }

                    if (full || DateTime.Now >= endTime)
                        break;
                }
            }

            await Task.Delay(5_000, token).ConfigureAwait(false);

            if (lobbyTrainers.Count == 0)
            {
                Log($"Nobody joined the raid, recovering...");
                return (false, lobbyTrainers);
            }

            _raidCount++;
            Log($"Raid #{_raidCount} is starting!");
            if (_emptyRaid != 0)
                _emptyRaid = 0;
            if (_lostRaid != 0)
                _lostRaid = 0;

            return (true, lobbyTrainers);
        }

        /// <summary>
        /// Checks if connected to a raid lobby
        /// </summary>
        private async Task<bool> IsConnectedToLobby(CancellationToken token)
        {
            try
            {
                var data = await SwitchConnection.ReadBytesMainAsync(Offsets.TeraLobbyIsConnected, 1, token).ConfigureAwait(false);
                bool isConnected = data[0] != 0x00;

                return isConnected;
            }
            catch (Exception ex)
            {
                Log($"Error checking lobby connection: {ex.Message}");
                return false; // Assume not connected if we can't read the memory
            }
        }

        /// <summary>
        /// Checks if currently in a raid battle
        /// </summary>
        private async Task<bool> IsInRaid(CancellationToken token)
        {
            var data = await SwitchConnection.ReadBytesMainAsync(Offsets.LoadedIntoDesiredState, 1, token).ConfigureAwait(false);
            return data[0] == 0x02;
        }

        /// <summary>
        /// Advances the game day by one
        /// </summary>
        private async Task AdvanceDaySV(CancellationToken token)
        {
            var scrollroll = _settings.MiscSettings.DateTimeFormat switch
            {
                DTFormat.DDMMYY => 0,
                DTFormat.YYMMDD => 2,
                _ => 1,
            };

            for (int i = 0; i < 2; i++)
                await Click(B, 0_150, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(DRIGHT, 0_150, token).ConfigureAwait(false);

            await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false);

            await PressAndHold(DDOWN, 2_000, 0_250, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false);

            if (_settings.MiscSettings.UseOvershoot)
            {
                await PressAndHold(DDOWN, _settings.MiscSettings.HoldTimeForRollover, 1_000, token).ConfigureAwait(false);
                await Click(DUP, 0_500, token).ConfigureAwait(false);
            }
            else
            {
                for (int i = 0; i < 39; i++)
                    await Click(DDOWN, 0_100, token).ConfigureAwait(false);
            }

            await Click(A, 1_250, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(DDOWN, 0_150, token).ConfigureAwait(false);

            await Click(A, 0_500, token).ConfigureAwait(false);

            for (int i = 0; i < scrollroll; i++)
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(DUP, 0_200, token).ConfigureAwait(false);

            for (int i = 0; i < 8; i++)
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(A, 0_200, token).ConfigureAwait(false);
            await Click(HOME, 1_000, token).ConfigureAwait(false);

            await Click(A, 0_200, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Corrects the rollover for time-based events
        /// </summary>
        private async Task RolloverCorrectionSV(CancellationToken token)
        {
            var scrollroll = _settings.MiscSettings.DateTimeFormat switch
            {
                DTFormat.DDMMYY => 0,
                DTFormat.YYMMDD => 2,
                _ => 1,
            };

            for (int i = 0; i < 2; i++)
                await Click(B, 0_150, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(DRIGHT, 0_150, token).ConfigureAwait(false);

            await Click(DDOWN, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_150, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false);

            await PressAndHold(DDOWN, 2_000, 0_250, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false);

            if (_settings.MiscSettings.UseOvershoot)
            {
                await PressAndHold(DDOWN, _settings.MiscSettings.HoldTimeForRollover, 1_000, token).ConfigureAwait(false);
                await Click(DUP, 0_500, token).ConfigureAwait(false);
            }
            else
            {
                for (int i = 0; i < 39; i++)
                    await Click(DDOWN, 0_100, token).ConfigureAwait(false);
            }

            await Click(A, 1_250, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(DDOWN, 0_150, token).ConfigureAwait(false);

            await Click(A, 0_500, token).ConfigureAwait(false);

            for (int i = 0; i < scrollroll; i++)
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(DDOWN, 0_200, token).ConfigureAwait(false);

            for (int i = 0; i < 8; i++)
                await Click(DRIGHT, 0_200, token).ConfigureAwait(false);

            await Click(A, 0_200, token).ConfigureAwait(false);
            await Click(HOME, 1_000, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Regroups after a banned user was removed from the lobby
        /// </summary>
        private async Task RegroupFromBannedUser(CancellationToken token)
        {
            Log("Attempting to remake lobby..");
            await Click(B, 2_000, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(A, 3_000, token).ConfigureAwait(false);
            await Click(B, 1_000, token).ConfigureAwait(false);

            while (!await IsOnOverworld(_overworldOffset, token))
            {
                for (int i = 0; i < 8; i++)
                    await Click(B, 1000, token);
            }
        }

        /// <summary>
        /// Initializes offsets for the current session
        /// </summary>
        private async Task InitializeSessionOffsets(CancellationToken token)
        {
            Log("Caching session offsets...");

            // Create tasks for all pointer resolutions
            var pointerTasks = new Dictionary<string, Task<ulong>>
            {
                // Main game pointers
                ["overworld"] = SwitchConnection.PointerAll(Offsets.OverworldPointer, token),
                ["connected"] = SwitchConnection.PointerAll(Offsets.IsConnectedPointer, token),
                ["raidP"] = SwitchConnection.PointerAll(RaidCrawler.Core.Structures.Offsets.RaidBlockPointerBase.ToArray(), token),
                ["raidK"] = SwitchConnection.PointerAll(RaidCrawler.Core.Structures.Offsets.RaidBlockPointerKitakami.ToArray(), token),
                ["raidB"] = SwitchConnection.PointerAll(RaidCrawler.Core.Structures.Offsets.RaidBlockPointerBlueberry.ToArray(), token)
            };

            // NID pointers for trainers
            var nidPointer = new long[] { Offsets.LinkTradePartnerNIDPointer[0], Offsets.LinkTradePartnerNIDPointer[1], Offsets.LinkTradePartnerNIDPointer[2] };
            for (int p = 0; p < _teraNIDOffsets.Length; p++)
            {
                var tempP = p; // Capture loop variable
                var tempPointer = new long[] { nidPointer[0], nidPointer[1], nidPointer[2] + tempP * 0x8 };
                pointerTasks[$"nid{tempP}"] = SwitchConnection.PointerAll(tempPointer, token);
            }

            // Wait for all pointer tasks to complete
            await Task.WhenAll(pointerTasks.Values);

            // Assign results to member variables
            _overworldOffset = pointerTasks["overworld"].Result;
            _connectedOffset = pointerTasks["connected"].Result;
            _raidBlockPointerP = pointerTasks["raidP"].Result;
            _raidBlockPointerK = pointerTasks["raidK"].Result;
            _raidBlockPointerB = pointerTasks["raidB"].Result;

            for (int p = 0; p < _teraNIDOffsets.Length; p++)
            {
                _teraNIDOffsets[p] = pointerTasks[$"nid{p}"].Result;
            }

            _raidMemoryManager = new RaidMemoryManager(SwitchConnection, _raidBlockPointerP, _raidBlockPointerK, _raidBlockPointerB);

            await DetectCurrentRegion(token);

            // First run initialization
            if (_firstRun)
            {
                GameProgress = await ReadGameProgress(token).ConfigureAwait(false);
                Log($"Current Game Progress identified as {GameProgress}.");
                CurrentSpawnsEnabled = (bool?)await ReadBlock(RaidDataBlocks.KWildSpawnsEnabled, CancellationToken.None);
            }

            // Load raid mechanics data
            await LoadRaidBossMechanics(token).ConfigureAwait(false);
            Log("Caching offsets complete!");
        }

        /// <summary>
        /// Detects the current region (Paldea, Kitakami, or Blueberry) and sets appropriate flags.
        /// </summary>
        /// <param name="token">Cancellation token</param>
        /// <returns>TeraRaidMapParent value representing the current region</returns>
        private async Task<TeraRaidMapParent> DetectCurrentRegion(CancellationToken token)
        {
            sbyte fieldID = await ReadEncryptedBlockByte(RaidDataBlocks.KPlayerCurrentFieldID, token).ConfigureAwait(false);

            TeraRaidMapParent mapParent = fieldID switch
            {
                0 => TeraRaidMapParent.Paldea,
                1 => TeraRaidMapParent.Kitakami,
                2 => TeraRaidMapParent.Blueberry,
                _ => TeraRaidMapParent.Paldea
            };

            // Reset region flags
            IsKitakami = false;
            IsBlueberry = false;

            // Set appropriate region flag
            switch (mapParent)
            {
                case TeraRaidMapParent.Kitakami:
                    IsKitakami = true;
                    break;
                case TeraRaidMapParent.Blueberry:
                    IsBlueberry = true;
                    break;
                default:
                    break;
            }

            return mapParent;
        }

        /// <summary>
        /// Validates if an image URL is accessible
        /// </summary>
        private static async Task<bool> IsValidImageUrlAsync(string url)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException ex) when (ex.InnerException is WebException webEx && webEx.Status == WebExceptionStatus.TrustFailure)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly Dictionary<string, string> TypeAdvantages = new()
        {
            { "normal", "Fighting" },
            { "fire", "Water, Ground, Rock" },
            { "water", "Electric, Grass" },
            { "grass", "Flying, Poison, Bug, Fire, Ice" },
            { "electric", "Ground" },
            { "ice", "Fighting, Rock, Steel, Fire" },
            { "fighting", "Flying, Psychic, Fairy" },
            { "poison", "Ground, Psychic" },
            { "ground", "Water, Ice, Grass" },
            { "flying", "Rock, Electric, Ice" },
            { "psychic", "Bug, Ghost, Dark" },
            { "bug", "Flying, Rock, Fire" },
            { "rock", "Fighting, Ground, Steel, Water, Grass" },
            { "ghost", "Ghost, Dark" },
            { "dragon", "Ice, Dragon, Fairy" },
            { "dark", "Fighting, Bug, Fairy" },
            { "steel", "Fighting, Ground, Fire" },
            { "fairy", "Poison, Steel" }
        };

        /// <summary>
        /// Gets type advantage information for a specific tera type
        /// </summary>
        private string GetTypeAdvantage(string teraType)
        {
            string englishTypeName = GetEnglishTypeNameFromLocalized(teraType);

            if (TypeAdvantages.TryGetValue(englishTypeName.ToLower(), out string? advantage))
            {
                return advantage;
            }
            return "Unknown Type";
        }

        /// <summary>
        /// Maps localized type names to English type names for consistent lookup
        /// </summary>
        private string GetEnglishTypeNameFromLocalized(string teraType)
        {
            if (TypeAdvantages.ContainsKey(teraType.ToLower()))
                return teraType.ToLower();
            var englishStrings = GameInfo.GetStrings("en");
            var localizedStrings = GameInfo.GetStrings(((LanguageID)_settings.EmbedToggles.EmbedLanguage).GetLanguageCode());
            for (int i = 0; i < localizedStrings.Types.Count; i++)
            {
                if (string.Equals(teraType, localizedStrings.Types[i], StringComparison.OrdinalIgnoreCase))
                {
                    return englishStrings.Types[i].ToLower();
                }
            }

            // If not found, return the original (it might still work if close enough)
            return teraType.ToLower();
        }

        /// <summary>
        /// Creates and sends an embed with raid information
        /// </summary>
        private async Task EnqueueEmbed(List<string>? names, string message, bool hatTrick, bool disband, bool upnext, bool raidstart, CancellationToken token, bool isRaidStartingEmbed = false)
        {
            string code = string.Empty;

            // If this is a raid ending, starting with players, or disbanding, update reactions first
            if (disband || (names is not null && !upnext) || upnext)
            {
                // Update to red X emoji - do this BEFORE clearing tracking
                await SharedRaidCodeHandler.UpdateReactionsOnAllMessages(false, token);
            }

            // Only clear tracking when starting a new raid or when the raid is specifically over
            if ((!disband && names is null && !upnext && !raidstart) || upnext)
            {
                SharedRaidCodeHandler.ClearAllRaidTracking();
            }

            // Update raid embed information before creating the embed (unless it's a disband or upnext message)
            if (!disband && !upnext && _settings.ActiveRaids.Count > 0 && _currentRaidIndex < _settings.ActiveRaids.Count)
            {
                await UpdateRaidEmbedInfo(token);
            }

            // Determine if the raid should use a code or be "Free For All"
            if (_settings.ActiveRaids[_currentRaidIndex].IsCoded)
            {
                if (IsFreeForAllRaid())
                {
                    code = "Free For All";
                    Log($"Empty raid limit reached ({_settings.LobbyOptions.EmptyRaidLimit}). Opening coded raid to all.");
                }
                else
                {
                    code = await GetRaidCode(token).ConfigureAwait(false);
                }
            }
            else
            {
                // Raid is configured as uncoded - no code needed
                code = string.Empty;
            }

            // A GenPKM request's code belongs to the member who asked for it:
            // no code, no code reaction and no lobby screenshot (the Switch shows
            // the code on screen) may reach Discord or the shared raid feed.
            bool privateCode = _settings.ActiveRaids[_currentRaidIndex].WebRequest != null;

            // Apply delay only if the raid was added by RA command, not a Mystery Shiny Raid, and has a code
            if (_settings.ActiveRaids[_currentRaidIndex].AddedByRACommand &&
                _settings.ActiveRaids[_currentRaidIndex].Title != MysteryRaidTitle &&
                !string.IsNullOrEmpty(code) && code != "Free For All")
            {
                await Task.Delay((int)_settings.EmbedToggles.RequestEmbedTime, token).ConfigureAwait(false);
            }

            // Get the selected language from settings
            var language = _settings.EmbedToggles.EmbedLanguage;

            // Description can only be up to 4096 characters
            var description = _settings.EmbedToggles.RaidEmbedDescription.Length > 0
                ? string.Join("\n", _settings.EmbedToggles.RaidEmbedDescription)
                : "";

            if (description.Length > 4096)
                description = description[..4096];

            if (disband) // Wait for trainer to load before disband
                await Task.Delay(5_000, token).ConfigureAwait(false);

            byte[]? imageBytes = null;
            string fileName = string.Empty;

            // Define a condition for raid starting embeds with countdown 
            bool isRaidStartingWithCountdown = (isRaidStartingEmbed || (!disband && names is null && !upnext && !raidstart && _settings.EmbedToggles.IncludeCountdown));

            // Modify screenshot logic to allow screenshots when showing raid code
            bool shouldTakeScreenshot = _settings.EmbedToggles.TakeScreenshot && !upnext &&
                (!isRaidStartingWithCountdown || (!_settings.EmbedToggles.HideRaidCode && code != "Free For All"));

            if (!privateCode && !disband && names is not null && !upnext && _settings.EmbedToggles.TakeScreenshot)
            {
                try
                {
                    // If we're showing the raid code, wait for it to appear on screen
                    if (!_settings.EmbedToggles.HideRaidCode && code != "Free For All")
                    {
                        await Task.Delay((int)_settings.EmbedToggles.ScreenshotTiming, token).ConfigureAwait(false);
                    }

                    imageBytes = await SwitchConnection.PixelPeek(token).ConfigureAwait(false) ?? Array.Empty<byte>();
                    fileName = $"raidecho{_currentRaidIndex}.jpg";
                }
                catch (Exception ex)
                {
                    Log($"Error while capturing screenshots: {ex.Message}");
                }
            }
            else if (shouldTakeScreenshot && !privateCode)
            {
                try
                {
                    // If we're showing the raid code on initial screenshot, wait for it
                    if (!_settings.EmbedToggles.HideRaidCode && !disband && code != "Free For All")
                    {
                        await Task.Delay((int)_settings.EmbedToggles.ScreenshotTiming, token).ConfigureAwait(false);
                    }

                    imageBytes = await SwitchConnection.PixelPeek(token).ConfigureAwait(false) ?? Array.Empty<byte>();
                    fileName = $"raidecho{_currentRaidIndex}.jpg";
                }
                catch (Exception ex)
                {
                    Log($"Error while fetching pixels: {ex.Message}");
                }
            }

            string disclaimer = _settings.ActiveRaids.Count > 1
                                ? $"notpaldea.net"
                                : "";

            var turl = string.Empty;
            Log($"Rotation Count: {_currentRaidIndex} | Species is {_settings.ActiveRaids[_currentRaidIndex].Species}");
            if (!disband && !upnext && !raidstart)
                Log(privateCode ? "Raid Code sent privately to the GenPKM member." : $"Raid Code is: {code}");
            PK9 pk = new()
            {
                Species = (ushort)_settings.ActiveRaids[_currentRaidIndex].Species,
                Form = (byte)_settings.ActiveRaids[_currentRaidIndex].SpeciesForm
            };
            if (_settings.ActiveRaids[_currentRaidIndex].IsShiny == true)
                pk.SetIsShiny(true);
            else
                pk.SetIsShiny(false);
            if (_settings.ActiveRaids[_currentRaidIndex].SpriteAlternateArt && _settings.ActiveRaids[_currentRaidIndex].IsShiny)
            {
                var altUrl = AltPokeImg(pk);
                try
                {
                    // Check if AltPokeImg URL is valid
                    if (await IsValidImageUrlAsync(altUrl))
                    {
                        turl = altUrl;
                    }
                    else
                    {
                        _settings.ActiveRaids[_currentRaidIndex].SpriteAlternateArt = false;
                        turl = RaidExtensions<PK9>.PokeImg(pk, false, false);
                        Log($"AltPokeImg URL was not valid. Setting SpriteAlternateArt to false.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error while validating alternate image URL: {ex.Message}");
                    _settings.ActiveRaids[_currentRaidIndex].SpriteAlternateArt = false;
                    turl = RaidExtensions<PK9>.PokeImg(pk, false, false);
                }
            }
            else
            {
                turl = RaidExtensions<PK9>.PokeImg(pk, false, false);
            }
            if (_settings.ActiveRaids[_currentRaidIndex].Species is 0)
                turl = "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/combat.png";

            // Fetch the dominant color from the image
            (int R, int G, int B) dominantColor = await RaidExtensions<PK9>.GetDominantColorAsync(turl).ConfigureAwait(false);

            // Use the dominant color, unless it's a disband or hatTrick situation
            var embedColor = disband ? Discord.Color.Red : hatTrick ? Discord.Color.Purple : new Discord.Color(dominantColor.R, dominantColor.G, dominantColor.B);

            TimeSpan duration = new(0, 2, 31);

            // Calculate the future time by adding the duration to the current time
            DateTimeOffset futureTime = DateTimeOffset.Now.Add(duration);

            // Convert the future time to Unix timestamp
            long futureUnixTime = futureTime.ToUnixTimeSeconds();

            // Create the future time message using Discord's timestamp formatting
            string futureTimeMessage = $"**{EmbedLanguageManager.GetLocalizedText("Raid Posting", language)}: <t:{futureUnixTime}:R>**";

            // Initialize the EmbedBuilder object
            var embed = new EmbedBuilder()
            {
                Color = embedColor,
                Description = disband
                    ? message
                    : upnext
                        ? _settings.RaidSettings.TotalRaidsToHost == 0
                            ? $"# {_settings.ActiveRaids[_currentRaidIndex].Title}\n\n{futureTimeMessage}"
                            : $"# {_settings.ActiveRaids[_currentRaidIndex].Title}\n\n{futureTimeMessage}"
                        : raidstart
                            ? ""
                            : description,
                ThumbnailUrl = upnext ? turl : (imageBytes == null ? turl : null),
                ImageUrl = imageBytes != null ? $"attachment://{fileName}" : null,
            };

            // Set title based on condition
            if (disband)
            {
                embed.Title = privateCode
                    ? $"**{EmbedLanguageManager.GetLocalizedText("Raid canceled", language)}**"
                    : $"**{EmbedLanguageManager.GetLocalizedText("Raid canceled", language)}: [{_teraRaidCode}]**";
            }
            else if (upnext && _settings.RaidSettings.TotalRaidsToHost != 0)
            {
                embed.Title = $"{EmbedLanguageManager.GetLocalizedText("Raid Ended - Preparing Next Raid", language)}!";
            }
            else if (upnext && _settings.RaidSettings.TotalRaidsToHost == 0)
            {
                embed.Title = $"{EmbedLanguageManager.GetLocalizedText("Raid Ended - Preparing Next Raid", language)}!";
            }

            if (!raidstart && !upnext && code != "Free For All")
                await CurrentRaidInfo(null, code, false, false, false, false, turl, false, token).ConfigureAwait(false);

            // Only include footer if not posting 'upnext' embed with the 'Preparing Raid' title
            if (!(upnext && _settings.RaidSettings.TotalRaidsToHost == 0))
            {
                string programIconUrl = $"https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/icon4.png";
                int raidsIn_currentRaidIndex = _hub.Config.RotatingRaidSV.ActiveRaids.Count(r => !r.AddedByRACommand);

                // Calculate uptime
                TimeSpan uptime = DateTime.Now - StartTime;

                // Format day/hour/minute labels with appropriate localization
                string dayLabel = uptime.Days == 1
                    ? EmbedLanguageManager.GetLocalizedText("day", language)
                    : EmbedLanguageManager.GetLocalizedText("days", language);
                string hourLabel = uptime.Hours == 1
                    ? EmbedLanguageManager.GetLocalizedText("hour", language)
                    : EmbedLanguageManager.GetLocalizedText("hours", language);
                string minuteLabel = uptime.Minutes == 1
                    ? EmbedLanguageManager.GetLocalizedText("minute", language)
                    : EmbedLanguageManager.GetLocalizedText("minutes", language);

                // Format the uptime string, omitting the part if the value is 0
                string uptimeFormatted = "";
                if (uptime.Days > 0)
                {
                    uptimeFormatted += $"{uptime.Days} {dayLabel} ";
                }
                if (uptime.Hours > 0 || uptime.Days > 0) // Show hours if there are any hours, or if there are days even if hours are 0
                {
                    uptimeFormatted += $"{uptime.Hours} {hourLabel} ";
                }
                if (uptime.Minutes > 0 || uptime.Hours > 0 || uptime.Days > 0) // Show minutes if there are any minutes, or if there are hours/days even if minutes are 0
                {
                    uptimeFormatted += $"{uptime.Minutes} {minuteLabel}";
                }

                // Trim any excess whitespace from the string
                uptimeFormatted = uptimeFormatted.Trim();

                string footerText = $"{EmbedLanguageManager.GetLocalizedText("Completed Raids", language)}: {_raidCount} (W: {_winCount} | L: {_lossCount})\n" +
                                   $"{EmbedLanguageManager.GetLocalizedText("ActiveRaids", language)}: {raidsIn_currentRaidIndex} | " +
                                   $"{EmbedLanguageManager.GetLocalizedText("Uptime", language)}: {uptimeFormatted}\n" +
                                   disclaimer;

                embed.WithFooter(new EmbedFooterBuilder()
                {
                    Text = footerText,
                    IconUrl = programIconUrl
                });
            }

            // Prepare the tera icon URL
            string teraType = RaidEmbedInfoHelpers.RaidSpeciesTeraType;
            string englishTeraType = GetEnglishTypeNameFromLocalized(teraType).ToLower();
            string folderName = _settings.EmbedToggles.SelectedTeraIconType == TeraIconType.Icon1 ? "icon1" : "icon2";
            string teraIconUrl = $"https://raw.githubusercontent.com/hexbyt3/sprites/main/teraicons/{folderName}/{englishTeraType}.png";

            // Only include author (header) if not posting 'upnext' embed with the 'Preparing Raid' title
            if (!(upnext && _settings.RaidSettings.TotalRaidsToHost == 0))
            {
                // Set the author (header) of the embed with the tera icon using localization
                embed.WithLocalizedAuthor(RaidEmbedInfoHelpers.RaidEmbedTitle, teraIconUrl, language);
            }

            if (!disband && !upnext && !raidstart)
            {
                // Build localized stats field
                string statsField = EmbedLocalizationHelper.BuildLocalizedStatsField(
                    language,
                    RaidEmbedInfoHelpers.RaidLevel.ToString(),
                    RaidEmbedInfoHelpers.RaidSpeciesGender,
                    RaidEmbedInfoHelpers.RaidSpeciesNature,
                    RaidEmbedInfoHelpers.RaidSpeciesAbility,
                    RaidEmbedInfoHelpers.RaidSpeciesIVs,
                    RaidEmbedInfoHelpers.ScaleText,
                    RaidEmbedInfoHelpers.ScaleNumber.ToString(),
                    _settings.EmbedToggles.IncludeSeed,
                    _settings.ActiveRaids[_currentRaidIndex].Seed,
                    _settings.ActiveRaids[_currentRaidIndex].DifficultyLevel,
                    (int)_settings.ActiveRaids[_currentRaidIndex].StoryProgress
                );

                embed.AddLocalizedField("**__Stats__**", statsField, true, language);
                embed.AddField("\u200b", "\u200b", true);
            }

            if (!disband && !upnext && !raidstart && _settings.EmbedToggles.IncludeMoves)
            {
                embed.AddLocalizedField("**__Moves__**", string.IsNullOrEmpty($"{RaidEmbedInfoHelpers.ExtraMoves}")
                    ? string.IsNullOrEmpty($"{RaidEmbedInfoHelpers.Moves}")
                        ? EmbedLanguageManager.GetLocalizedText("No Moves To Display", language)
                        : $"{RaidEmbedInfoHelpers.Moves}"
                    : $"{RaidEmbedInfoHelpers.Moves}\n**{EmbedLanguageManager.GetLocalizedText("Extra Moves", language)}:**\n{RaidEmbedInfoHelpers.ExtraMoves}", true, language);
            }

            if (!disband && !upnext && !raidstart && !_settings.EmbedToggles.IncludeMoves)
            {
                embed.AddLocalizedField("**__Special Rewards__**", string.IsNullOrEmpty($"{RaidEmbedInfoHelpers.SpecialRewards}")
                    ? EmbedLanguageManager.GetLocalizedText("No Rewards To Display", language)
                    : $"{RaidEmbedInfoHelpers.SpecialRewards}", true, language);
            }

            // Fetch the type advantage using the static RaidSpeciesTeraType from RaidEmbedInfo
            string typeAdvantage = GetTypeAdvantage(RaidEmbedInfoHelpers.RaidSpeciesTeraType);

            // Only include the Type Advantage if not posting 'upnext' embed with the 'Preparing Raid' title and if the raid isn't starting or disbanding
            if (!disband && !upnext && !raidstart && _settings.EmbedToggles.IncludeTypeAdvantage)
            {
                embed.AddLocalizedField("**__Type Advantage__**", typeAdvantage, true, language);
                embed.AddField("\u200b", "\u200b", true);
            }

            if (!disband && !upnext && !raidstart && _settings.EmbedToggles.IncludeMoves)
            {
                embed.AddLocalizedField("**__Special Rewards__**", string.IsNullOrEmpty($"{RaidEmbedInfoHelpers.SpecialRewards}")
                    ? EmbedLanguageManager.GetLocalizedText("No Rewards To Display", language)
                    : $"{RaidEmbedInfoHelpers.SpecialRewards}", true, language);
            }

            if (!disband && !upnext && !raidstart && _settings.ActiveRaids[_currentRaidIndex].DifficultyLevel == 7)
            {
                // Try to get the raid boss mechanics for 7-star raids
                string mechanicsInfo = GetRaidBossMechanics();
                if (!string.IsNullOrEmpty(mechanicsInfo))
                {
                    embed.AddLocalizedField("**__7★ Raid Mechanics__**", mechanicsInfo, false, language);
                }
            }

            if (!disband && names is null && !upnext)
            {
                if (privateCode)
                {
                    string fieldName = _settings.EmbedToggles.IncludeCountdown
                        ? $"**__{EmbedLanguageManager.GetLocalizedText("Raid Starting", language)}__**:\n**<t:{DateTimeOffset.Now.ToUnixTimeSeconds() + 160}:R>**"
                        : $"**{EmbedLanguageManager.GetLocalizedText("Waiting in lobby", language)}!**";

                    embed.AddField(fieldName, "🔒 Private raid requested on genpkm.com", true);
                }
                else if (code == "Free For All")
                {
                    string fieldName = _settings.EmbedToggles.IncludeCountdown
                        ? $"**__{EmbedLanguageManager.GetLocalizedText("Raid Starting", language)}__**:\n**<t:{DateTimeOffset.Now.ToUnixTimeSeconds() + 160}:R>**"
                        : $"**{EmbedLanguageManager.GetLocalizedText("Waiting in lobby", language)}!**";

                    string fieldValue = $"**{EmbedLanguageManager.GetLocalizedText("Free For All", language)}**";

                    embed.AddField(fieldName, fieldValue, true);
                }
                else if (!_settings.EmbedToggles.HideRaidCode)
                {
                    // Show raid code directly in the embed (old behavior)
                    string fieldName = _settings.EmbedToggles.IncludeCountdown
                        ? $"**__{EmbedLanguageManager.GetLocalizedText("Raid Starting", language)}__**:\n**<t:{DateTimeOffset.Now.ToUnixTimeSeconds() + 160}:R>**"
                        : $"**{EmbedLanguageManager.GetLocalizedText("Waiting in lobby", language)}!**";

                    // Apply raid code case formatting
                    string displayCode = _settings.EmbedToggles.RaidCodeCase == RaidCodeCaseOptions.Uppercase
                        ? code.ToUpper()
                        : code.ToLower();

                    string fieldValue = $"{EmbedLanguageManager.GetLocalizedText("Raid Code", language)}: **{displayCode}**";

                    embed.AddField(fieldName, fieldValue, true);
                }
                else
                {
                    // Keep current behavior with empty field (reactions will be used)
                    string fieldName = _settings.EmbedToggles.IncludeCountdown
                        ? $"**__{EmbedLanguageManager.GetLocalizedText("Raid Starting", language)}__**:\n**<t:{DateTimeOffset.Now.ToUnixTimeSeconds() + 160}:R>**"
                        : $"**{EmbedLanguageManager.GetLocalizedText("Waiting in lobby", language)}!**";

                    embed.AddField(fieldName, "\u200B", true);
                }
            }

            if (!disband && names is not null && !upnext)
            {
                var players = string.Empty;
                if (names.Count == 0)
                    players = EmbedLanguageManager.GetLocalizedText("Our party dipped on us :/", language);
                else
                {
                    // Add host as Player 1
                    players += $"{EmbedLanguageManager.GetLocalizedText("Player", language)} 1 ({EmbedLanguageManager.GetLocalizedText("Host", language)}) - **{_hostSAV.OT}**\n";

                    // Add other players starting at Player 2
                    int i = 2;
                    names.ForEach(x =>
                    {
                        players += $"{EmbedLanguageManager.GetLocalizedText("Player", language)} {i} - **{x}**\n";
                        i++;
                    });
                }

                string fieldName = $"**{EmbedLanguageManager.GetLocalizedText("Raid", language)} #{_raidCount} {EmbedLanguageManager.GetLocalizedText("is starting", language)}!**";
                embed.AddField(fieldName, players);
            }

            if (imageBytes != null)
            {
                embed.ThumbnailUrl = turl;
                embed.WithImageUrl($"attachment://{fileName}");
            }

            // Only add reaction instructions if HideRaidCode is true
            if (!privateCode && !disband && names is null && !upnext && !raidstart && code != "Free For All" && _settings.EmbedToggles.HideRaidCode)
            {
                // Add a field with code information for reaction system
                string fieldName = $"**__{EmbedLanguageManager.GetLocalizedText("Raid Code", language)}__**";
                string fieldValue = $"{EmbedLanguageManager.GetLocalizedText("React with", language)} ✅ " +
                                  $"{EmbedLanguageManager.GetLocalizedText("to receive the raid code via DM", language)}.\n" +
                                  $"{EmbedLanguageManager.GetLocalizedText("The code will be sent to you privately", language)}.";

                embed.AddField(fieldName, fieldValue, false);
            }

            // Send the embed to all channels and get list of sent messages
            var sentMessages = await EchoUtil.RaidEmbed(imageBytes, fileName, embed);

            // Only use reaction system if HideRaidCode is true
            bool isInitialCodedRaidAnnouncement =
                sentMessages.Count > 0 &&
                code != "Free For All" &&
                names is null &&
                !upnext &&
                !raidstart &&
                !disband &&
                !privateCode &&
                _settings.EmbedToggles.HideRaidCode; // Only use reactions if hiding code

            if (isInitialCodedRaidAnnouncement)
            {
                var raidInfoDict = new Dictionary<string, string>
                {
                    ["RaidTitle"] = RaidEmbedInfoHelpers.RaidEmbedTitle,
                    ["TeraType"] = RaidEmbedInfoHelpers.RaidSpeciesTeraType,
                    ["TeraIconUrl"] = teraIconUrl,
                    ["ThumbnailUrl"] = turl,
                    ["IsShiny"] = _settings.ActiveRaids[_currentRaidIndex].IsShiny.ToString(),
                    ["DifficultyLevel"] = _settings.ActiveRaids[_currentRaidIndex].DifficultyLevel.ToString(),
                    ["Level"] = RaidEmbedInfoHelpers.RaidLevel.ToString(),
                    ["Gender"] = RaidEmbedInfoHelpers.RaidSpeciesGender,
                    ["Nature"] = RaidEmbedInfoHelpers.RaidSpeciesNature,
                    ["Ability"] = RaidEmbedInfoHelpers.RaidSpeciesAbility,
                    ["IVs"] = RaidEmbedInfoHelpers.RaidSpeciesIVs,
                    ["Scale"] = $"{RaidEmbedInfoHelpers.ScaleText}({RaidEmbedInfoHelpers.ScaleNumber})",
                    ["Moves"] = RaidEmbedInfoHelpers.Moves,
                    ["ExtraMoves"] = RaidEmbedInfoHelpers.ExtraMoves,
                    ["SpecialRewards"] = RaidEmbedInfoHelpers.SpecialRewards,
                    ["TypeAdvantage"] = typeAdvantage
                };

                if (_settings.ActiveRaids[_currentRaidIndex].DifficultyLevel == 7)
                {
                    raidInfoDict["RaidMechanics"] = GetRaidBossMechanics();
                }

                foreach (var sentMessage in sentMessages)
                {
                    try
                    {
                        SharedRaidCodeHandler.AddActiveRaidMessageWithInfoDict(
                            sentMessage.Id,
                            sentMessage.Channel.Id,
                            code,
                            raidInfoDict);
                    }
                    catch (Exception ex)
                    {
                        Log($"Error tracking raid message: {ex.Message}");
                    }
                }
                await SharedRaidCodeHandler.UpdateReactionsOnAllMessages(true, token);
            }
        }

        /// <summary>
        /// Updates RaidEmbedInfoHelpers with accurate raid data using RaidInfoCommand
        /// </summary>
        private Task UpdateRaidEmbedInfo(CancellationToken token)
        {
            if (NoActiveRaids || _currentRaidIndex >= _settings.ActiveRaids.Count)
                return Task.CompletedTask;
            var currentRaid = _settings.ActiveRaids[_currentRaidIndex];
            string seedValue = currentRaid.Seed;

            // Map TeraCrystalType to RaidInfoCommand's contentType parameter
            int contentType = currentRaid.CrystalType switch
            {
                TeraCrystalType.Base => 0,    // Base crystal = regular raid
                TeraCrystalType.Black => 1,   // Black crystal = 1
                TeraCrystalType.Distribution => 2, // Distribution = 2
                TeraCrystalType.Might => 3,   // Might = 3
                _ => 0,
            };

            // Determine the proper map based on current region
            TeraRaidMapParent map = IsKitakami
                ? TeraRaidMapParent.Kitakami
                : IsBlueberry
                    ? TeraRaidMapParent.Blueberry
                    : TeraRaidMapParent.Paldea;

            // Convert GameProgressEnum to the numeric value expected by RaidInfoCommand
            int storyProgressLevel = (int)currentRaid.StoryProgress + 1;

            // Get the group ID for event raids
            int raidDeliveryGroupID = currentRaid.GroupID ?? 0;

            // Distribution and Might are true events
            bool isEvent = currentRaid.CrystalType == TeraCrystalType.Distribution ||
                          currentRaid.CrystalType == TeraCrystalType.Might;

            // Get the selected language ID
            int languageId = (int)_settings.EmbedToggles.EmbedLanguage;

            Log($"Generating raid info with parameters: Seed={seedValue}, ContentType={contentType}, Map={map}, " +
                $"StoryProgress={storyProgressLevel}, GroupID={raidDeliveryGroupID}, IsEvent={isEvent}, CrystalType={currentRaid.CrystalType}, Language={languageId}");

            // Generate accurate raid data using RaidInfoCommand, passing the language ID
            (PK9 pk, Embed embed) = RaidInfoCommand(
                seedValue, contentType, map, storyProgressLevel, raidDeliveryGroupID,
                _settings.EmbedToggles.RewardsToShow, _settings.EmbedToggles.MoveTypeEmojis,
                _settings.EmbedToggles.CustomTypeEmojis, 0, isEvent, languageId
            );

            // Populate RaidEmbedInfoHelpers with data from the generated embed and PK9
            RaidEmbedDataPopulator.PopulateFromRaidInfo(pk, embed, _settings.EmbedToggles);

            // Update the current raid's Species and Form if they're not already set
            if (currentRaid.Species == Species.None)
            {
                currentRaid.Species = (Species)pk.Species;
                currentRaid.SpeciesForm = pk.Form;
                currentRaid.IsShiny = pk.IsShiny;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets the mechanics for a 7-star raid boss
        /// </summary>
        private string GetRaidBossMechanics()
        {
            if (_settings.ActiveRaids[_currentRaidIndex].DifficultyLevel != 7)
                return string.Empty;

            StringBuilder mechanics = new();

            try
            {
                var species = (ushort)_settings.ActiveRaids[_currentRaidIndex].Species;
                var form = (byte)_settings.ActiveRaids[_currentRaidIndex].SpeciesForm;
                if (_raidBossMechanicsData.TryGetValue((species, form), out var mechanicsInfo))
                {
                    // Shield activation
                    if (mechanicsInfo.ShieldHpTrigger > 0 && mechanicsInfo.ShieldTimeTrigger > 0)
                    {
                        mechanics.AppendLine("**Shield Activation:**");
                        mechanics.AppendLine($"• {mechanicsInfo.ShieldHpTrigger}% HP Remaining");
                        mechanics.AppendLine($"• {mechanicsInfo.ShieldTimeTrigger}% Time Remaining");
                    }

                    if (mechanicsInfo.ExtraActions.Count > 0)
                    {
                        mechanics.AppendLine("**Other Actions:**");
                        var moveNames = GameInfo.GetStrings("en").Move;

                        foreach (var (action, timing, value, moveId) in mechanicsInfo.ExtraActions)
                        {
                            var type = timing == 0 ? "Time" : "HP";

                            switch (action)
                            {
                                case 1: // BOSS_STATUS_RESET
                                    mechanics.AppendLine($"• Resets Raid Boss' Stat Changes at {value}% {type} Remaining");
                                    break;

                                case 2: // PLAYER_STATUS_RESET
                                    mechanics.AppendLine($"• Resets Player's Stat Changes at {value}% {type} Remaining");
                                    break;

                                case 3: // WAZA (Move)
                                    var moveName = moveId < moveNames.Count ? moveNames[moveId] : $"Move #{moveId}";
                                    mechanics.AppendLine($"• Uses {moveName} at {value}% {type} Remaining");
                                    break;

                                case 4: // GEM_COUNT
                                    mechanics.AppendLine($"• Reduces Tera Orb Charge at {value}% {type} Remaining");
                                    break;

                                default:
                                    mechanics.AppendLine($"• Unknown action ({action}) at {value}% {type} Remaining");
                                    break;
                            }
                        }
                    }

                    return mechanics.ToString();
                }
                else
                {
                    return string.Empty;
                }
            }
            catch (Exception ex)
            {
                Log($"Error retrieving raid boss mechanics: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Loads raid boss mechanics data from game files
        /// </summary>
        private async Task LoadRaidBossMechanics(CancellationToken token)
        {
            try
            {
                // Clear existing data
                _raidBossMechanicsData.Clear();
                var baseBlockKeyPointer = await SwitchConnection.PointerAll(Offsets.BlockKeyPointer, token).ConfigureAwait(false);
                byte[] deliveryRaidFlatbuffer = await ReadBlockDefault(
                    baseBlockKeyPointer,
                    RaidCrawler.Core.Structures.Offsets.BCATRaidBinaryLocation,
                    "raid_enemy_array",
                    false,
                    token
                ).ConfigureAwait(false);

                var tableEncounters = FlatBufferConverter.DeserializeFrom<pkNX.Structures.FlatBuffers.Gen9.DeliveryRaidEnemyTableArray>(deliveryRaidFlatbuffer);

                if (tableEncounters?.Table != null)
                {
                    foreach (var entry in tableEncounters.Table)
                    {
                        if (entry.Info?.Difficulty == 7 && entry.Info.BossPokePara != null && entry.Info.BossDesc != null)
                        {
                            // Create mechanics info object
                            var mechanicsInfo = new RaidBossMechanicsInfo
                            {
                                ShieldHpTrigger = (byte)entry.Info.BossDesc.PowerChargeTrigerHp,
                                ShieldTimeTrigger = (byte)entry.Info.BossDesc.PowerChargeTrigerTime,
                                ExtraActions = []
                            };

                            var actions = new[] {
                                entry.Info.BossDesc.ExtraAction1,
                                entry.Info.BossDesc.ExtraAction2,
                                entry.Info.BossDesc.ExtraAction3,
                                entry.Info.BossDesc.ExtraAction4,
                                entry.Info.BossDesc.ExtraAction5,
                                entry.Info.BossDesc.ExtraAction6
                            };

                            foreach (var action in actions)
                            {
                                if (action.Action != 0 && action.Value != 0)
                                {
                                    mechanicsInfo.ExtraActions.Add((action.Action, action.Timing, action.Value, action.Wazano));
                                }
                            }
                            _raidBossMechanicsData[(entry.Info.BossPokePara.DevId, entry.Info.BossPokePara.FormId)] = mechanicsInfo;
                        }
                    }
                }
                else
                {
                    Log("Failed to deserialize raid data for boss mechanics");
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading raid boss mechanics: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes emoji codes from strings
        /// </summary>
        private static string CleanEmojiStrings(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return Regex.Replace(input, @"<:[a-zA-Z0-9_]+:[0-9]+>", "").Trim();
        }

        /// <summary>
        /// RSA public key for encrypting raid codes
        /// </summary>
        private const string PUBLIC_KEY = @"-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEArFbz7xXyQtO0j5JfcVW4
lcIO3/+kL0GuNN4GgdZHNLWu6OX4Sv0BypvMOqdOTrGMMj+/v/1tRWamUh1qRSN+
lmRsNLxj5A6kdwZk+UIU2LC6X3Y192FyVAvV/nYFgvdoyUzF1agvaTP7C7g8F3vH
/zbGZdaH/4ZqKfBTU+NebCASaL+z+b7oIyl3j0RKdBAm5MJjYhSwj6j+1DpFbNgj
ALwkMx63fBR0pKs+jJ8DcFrcJR50aVv1jfIAQpPIK5G6Dk/4hmV12Hdu5sSGLl40
5AlAy18QKMi3y3vyvJ4wZnuY+gpsaTsuTlSau6FxpVzxosvv4kh9x1HVaoX2iGSh
7QIDAQAB
-----END PUBLIC KEY-----";

        /// <summary>
        /// Encrypts a raid code using RSA
        /// </summary>
        private static string? EncryptRaidCode(string code)
        {
            try
            {
                using RSA rsa = RSA.Create();
                rsa.ImportFromPem(PUBLIC_KEY);
                byte[] dataToEncrypt = Encoding.UTF8.GetBytes(code);
                byte[] encryptedData = rsa.Encrypt(dataToEncrypt, RSAEncryptionPadding.Pkcs1);
                return Convert.ToBase64String(encryptedData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Encryption error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Sends raid information to a central server if enabled
        /// </summary>
        private async Task CurrentRaidInfo(List<string>? names, string code, bool hatTrick, bool disband, bool upnext, bool raidstart, string? imageUrl, bool lobbyFull, CancellationToken token)
        {
            if (!_settings.RaidSettings.JoinSharedRaidsProgram || CurrentWebRequest != null)
                return;

            string? encryptedCode = null;
            if (!string.IsNullOrEmpty(code) && code != "FREE FOR ALL" && code != "IJ0LTU")
            {
                encryptedCode = EncryptRaidCode(code);
            }

            var raidInfo = new
            {
                RaidEmbedTitle = CleanEmojiStrings(RaidEmbedEnglishHelpers.RaidEmbedTitle),
                RaidSpecies = RaidEmbedInfoHelpers.RaidSpecies.ToString(),
                RaidEmbedInfoHelpers.RaidSpeciesForm,
                RaidSpeciesGender = CleanEmojiStrings(RaidEmbedEnglishHelpers.RaidSpeciesGender),
                RaidEmbedInfoHelpers.RaidLevel,
                RaidEmbedInfoHelpers.RaidSpeciesIVs,
                RaidEmbedEnglishHelpers.RaidSpeciesAbility,
                RaidEmbedEnglishHelpers.RaidSpeciesNature,
                RaidEmbedEnglishHelpers.RaidSpeciesTeraType,
                Moves = CleanEmojiStrings(RaidEmbedEnglishHelpers.Moves),
                ExtraMoves = CleanEmojiStrings(RaidEmbedEnglishHelpers.ExtraMoves),
                RaidEmbedEnglishHelpers.ScaleText,
                SpecialRewards = CleanEmojiStrings(RaidEmbedEnglishHelpers.SpecialRewards),
                RaidEmbedInfoHelpers.ScaleNumber,
                Names = names,
                Code_encrypted = encryptedCode,
                HatTrick = hatTrick,
                Disband = disband,
                UpNext = upnext,
                RaidStart = raidstart,
                ImageUrl = imageUrl,
                LobbyFull = lobbyFull
            };

            try
            {
                var json = JsonConvert.SerializeObject(raidInfo, Formatting.Indented);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                string raidinfo = Encoding.UTF8.GetString(Convert.FromBase64String("aHR0cHM6Ly9nZW5wa20uY29tL3JhaWRzL3JhaWRfYXBpLnBocA=="));
                var response = await _httpClient.PostAsync(raidinfo, content, token);
            }
            catch
            {
                // Silently handle network errors
            }
        }

        /// <summary>
        /// Connects to online in the game
        /// </summary>
        private async Task<bool> ConnectToOnline(PokeRaidHubConfig config, CancellationToken token)
        {
            // First check if already connected - if so, return success immediately
            try
            {
                bool alreadyConnected = await IsConnectedOnline(_connectedOffset, token).ConfigureAwait(false);
                if (alreadyConnected)
                {
                    // Verify the connection with additional checks for stability
                    bool stable = true;
                    for (int i = 0; i < 3; i++)
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                        if (!await IsConnectedOnline(_connectedOffset, token).ConfigureAwait(false))
                        {
                            stable = false;
                            break;
                        }
                    }

                    if (stable)
                    {
                        Log("Already connected online, skipping connection process");
                        await Task.Delay(500, token).ConfigureAwait(false);
                        return true;
                    }
                    else
                    {
                        Log("Connection appears unstable, attempting to reconnect");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error checking online status: {ex.Message}");
            }

            // If we reach here, we need to connect (or reconnect)
            int attemptCount = 0;
            const int maxAttempts = 5;
            const int waitTimeMinutes = 10;

            while (attemptCount < maxAttempts && !token.IsCancellationRequested)
            {
                try
                {
                    // Check if already connected
                    if (await IsConnectedOnline(_connectedOffset, token).ConfigureAwait(false))
                    {
                        // Verify stability
                        bool stable = true;
                        for (int i = 0; i < 3; i++)
                        {
                            await Task.Delay(1000, token).ConfigureAwait(false);
                            if (!await IsConnectedOnline(_connectedOffset, token).ConfigureAwait(false))
                            {
                                stable = false;
                                break;
                            }
                        }

                        if (stable)
                            break;
                    }

                    if (attemptCount >= maxAttempts)
                    {
                        Log($"Failed to connect after {maxAttempts} attempts. Waiting {waitTimeMinutes} minutes before retrying.");

                        var embed = new EmbedBuilder
                        {
                            Title = "Experiencing Online Connection Issues",
                            Description = "The bot is experiencing issues connecting online. Please stand by as we try to resolve this issue.",
                            Color = Color.Red,
                            ThumbnailUrl = "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/x.png"
                        };
                        
                        //You should remove the await keyword since EchoUtil.RaidEmbed returns void, and replace null with an empty byte array to satisfy the non-nullable parameter.
                        EchoUtil.RaidEmbed([], "", embed);

                        await Click(B, 0_500, token).ConfigureAwait(false);
                        await Click(B, 0_500, token).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromMinutes(waitTimeMinutes), token).ConfigureAwait(false);
                        await ReOpenGame(_hub.Config, token).ConfigureAwait(false);
                        attemptCount = 0;
                        continue;
                    }

                    attemptCount++;
                    Log($"Connection attempt {attemptCount}/{maxAttempts}");

                    // Execute connection inputs
                    await Click(X, 3_000, token).ConfigureAwait(false);
                    await Click(L, 5_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
                    await Task.Delay(3000, token).ConfigureAwait(false);
                    await RecoverToOverworld(token).ConfigureAwait(false);

                    // Check connection with stability verification
                    bool connected = await IsConnectedOnline(_connectedOffset, token).ConfigureAwait(false);
                    if (connected)
                    {
                        bool stable = true;
                        for (int i = 0; i < 3; i++)
                        {
                            await Task.Delay(1000, token).ConfigureAwait(false);
                            if (!await IsConnectedOnline(_connectedOffset, token).ConfigureAwait(false))
                            {
                                stable = false;
                                break;
                            }
                        }

                        if (stable)
                            break;
                    }

                    // Back out for next attempt if needed
                    if (attemptCount < maxAttempts && !connected)
                        await Click(B, 0_500, token).ConfigureAwait(false);
                    
                    // If attemptCount 1 fails, press A to try to get past the splash screen. If this is set to 2 it will connect just in time on attempt 5 (Tested implementation, 1 works the best) 
                    if (attemptCount == 1)
                    {
                        Log("Pressing A in case our switch is at the splash screen, and not in overworld.");
                        await Click(A, 0_500, token).ConfigureAwait(false); // Press A to try to get past the splash screen
                    }
                }
                catch (Exception ex)
                {
                    Log($"Connection error: {ex.Message}");
                    attemptCount++;

                    if (attemptCount >= maxAttempts)
                    {
                        Log($"Connection failed after {maxAttempts} attempts due to errors.");
                        await Task.Delay(TimeSpan.FromMinutes(waitTimeMinutes), token).ConfigureAwait(false);
                        await ReOpenGame(_hub.Config, token).ConfigureAwait(false);
                        return false;
                    }
                }
            }

            // Final verification
            bool finalConnected = await IsConnectedOnline(_connectedOffset, token).ConfigureAwait(false);
            if (!finalConnected)
            {
                Log("Failed to establish a stable connection.");
                return false;
            }

            await Task.Delay(3_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Task.Delay(3_000, token).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Starts the game and prepares for a raid
        /// </summary>
        public async Task StartGameRaid(PokeRaidHubConfig config, CancellationToken token)
        {
            if (_isPaused)
            {
                Log("Bot is paused. Skipping game start.");
                return;
            }
            // First, check if the time rollback feature is enabled
            if (_settings.RaidSettings.EnableTimeRollBack && DateTime.Now - _timeForRollBackCheck >= TimeSpan.FromHours(5))
            {
                Log("Rolling Time back 5 hours.");
                await RollBackTime(token).ConfigureAwait(false);
                await Click(A, 1_500, token).ConfigureAwait(false);
                _timeForRollBackCheck = DateTime.Now;
            }

            var timing = config.Timings;
            var loadPro = timing.RestartGameSettings.ProfileSelectSettings.ProfileSelectionRequired ? timing.RestartGameSettings.ProfileSelectSettings.ExtraTimeLoadProfile : 0;

            await Click(A, 1_000 + loadPro, token).ConfigureAwait(false);

            if (timing.RestartGameSettings.AvoidSystemUpdate)
            {
                await Task.Delay(1_500, token).ConfigureAwait(false);
                await Click(DUP, 0_600, token).ConfigureAwait(false);
                await Click(A, 1_000 + loadPro, token).ConfigureAwait(false);
            }

            if (timing.RestartGameSettings.ProfileSelectSettings.ProfileSelectionRequired)
            {
                await Click(A, 1_000, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);
            }

            if (timing.RestartGameSettings.CheckGameDelay)
            {
                await Task.Delay(2_000 + timing.RestartGameSettings.ExtraTimeCheckGame, token).ConfigureAwait(false);
            }

            if (timing.RestartGameSettings.CheckForDLC)
            {
                await Click(DUP, 0_600, token).ConfigureAwait(false);
                await Click(A, 0_600, token).ConfigureAwait(false);
            }

            Log("Restarting the game!");

            await Task.Delay(19_000 + timing.RestartGameSettings.ExtraTimeLoadGame, token).ConfigureAwait(false);
            await InitializeRaidBlockPointers(token);

            if (_settings.ActiveRaids.Count > 1)
            {
                Log($"Rotation for {_settings.ActiveRaids[_currentRaidIndex].Species} has been found.");
                Log($"Checking Current Game Progress Level.");

                var desiredProgress = _settings.ActiveRaids[_currentRaidIndex].StoryProgress;
                if (GameProgress != (GameProgress)desiredProgress)
                {
                    Log($"Updating game progress level to: {desiredProgress}");
                    int raidCrawlerProgress = GameProgressMapper.ToRaidCrawlerProgress(desiredProgress);
                    // Verify expected star range for this progress level
                    var (minStar, maxStar) = GameProgressMapper.GetExpectedStarRange(desiredProgress);
                    Log($"Expected star range for this progress level: {minStar}-{maxStar}★");

                    await WriteProgressLive((GameProgress)desiredProgress).ConfigureAwait(false);
                    GameProgress = (GameProgress)desiredProgress;

                    Log($"Game progress updated successfully.");
                }
                else
                {
                    Log($"Game progress level is already {GameProgress}. No update needed.");
                }

                RaidDataBlocks.AdjustKWildSpawnsEnabledType(_settings.RaidSettings.DisableOverworldSpawns);

                if (_settings.RaidSettings.DisableOverworldSpawns)
                {
                    Log("Checking current state of Overworld Spawns.");
                    if (CurrentSpawnsEnabled.HasValue)
                    {
                        Log($"Current Overworld Spawns state: {CurrentSpawnsEnabled.Value}");

                        if (CurrentSpawnsEnabled.Value)
                        {
                            Log("Overworld Spawns are enabled, attempting to disable.");
                            await WriteBlock(false, RaidDataBlocks.KWildSpawnsEnabled, token, CurrentSpawnsEnabled);
                            CurrentSpawnsEnabled = false;
                            Log("Overworld Spawns successfully disabled.");
                        }
                        else
                        {
                            Log("Overworld Spawns are already disabled, no action taken.");
                        }
                    }
                }
                else
                {
                    Log("Settings indicate Overworld Spawns should be enabled. Checking current state.");
                    Log($"Current Overworld Spawns state: {CurrentSpawnsEnabled.Value}");

                    if (!CurrentSpawnsEnabled.Value)
                    {
                        Log("Overworld Spawns are disabled, attempting to enable.");
                        await WriteBlock(true, RaidDataBlocks.KWildSpawnsEnabled, token, CurrentSpawnsEnabled);
                        CurrentSpawnsEnabled = true;
                        Log("Overworld Spawns successfully enabled.");
                    }
                    else
                    {
                        Log("Overworld Spawns are already enabled, no action needed.");
                    }
                }

                Log($"Attempting to override seed for {_settings.ActiveRaids[_currentRaidIndex].Species}.");
                await OverrideSeedIndex(_seedIndexToReplace, token).ConfigureAwait(false);
                Log("Seed override completed.");
            }

            for (int i = 0; i < 8; i++)
                await Click(A, 1_000, token).ConfigureAwait(false);

            var timeout = TimeSpan.FromMinutes(1);
            var delayTask = Task.Delay(timeout, token);

            while (true)
            {
                var isOnOverworldTitleTask = IsOnOverworldTitle(token);

                var completedTask = await Task.WhenAny(isOnOverworldTitleTask, delayTask).ConfigureAwait(false);

                if (completedTask == isOnOverworldTitleTask)
                {
                    if (await isOnOverworldTitleTask.ConfigureAwait(false))
                    {
                        break;
                    }
                }
                else
                {
                    Log("Still not in the game, initiating reboot protocol!");
                    await PerformRebootAndReset(token);
                    return;
                }

                await Task.Delay(1_000, token).ConfigureAwait(false);
            }

            await Task.Delay(5_000 + timing.ExtraTimeLoadOverworld, token).ConfigureAwait(false);
            Log("Back in the overworld!");
            _lostRaid = 0;
            await Task.Delay(2_000, token).ConfigureAwait(false);
            await LogPlayerLocation(token);
        }

        /// <summary>
        /// Loads den locations from JSON resources
        /// </summary>
        private static Dictionary<string, float[]> LoadDenLocations(string resourceName)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new InvalidOperationException($"Could not find embedded resource: {resourceName}");
            }

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            var denLocations = JsonConvert.DeserializeObject<Dictionary<string, float[]>>(json) ?? [];

            // Validate all coordinate arrays have exactly 3 elements (X, Y, Z)
            foreach (var kvp in denLocations)
            {
                if (kvp.Value.Length != 3)
                {
                    throw new InvalidOperationException(
                        $"Invalid den location data in {resourceName}: Den '{kvp.Key}' has {kvp.Value.Length} coordinates but expected 3 (X, Y, Z)");
                }
            }

            return denLocations;
        }

        /// <summary>
        /// Gets cached den locations for the specified map type
        /// </summary>
        private static Dictionary<string, float[]> GetCachedDenLocations(TeraRaidMapParent mapType)
        {
            return mapType switch
            {
                TeraRaidMapParent.Paldea => CachedPaldeaDenLocations.Value,
                TeraRaidMapParent.Kitakami => CachedKitakamiDenLocations.Value,
                TeraRaidMapParent.Blueberry => CachedBlueberryDenLocations.Value,
                _ => throw new ArgumentException("Invalid map type", nameof(mapType))
            };
        }

        /// <summary>
        /// Finds the nearest den location to the player
        /// </summary>
        private static string FindNearestLocation((float, float, float) playerLocation, Dictionary<string, float[]> denLocations)
        {
            string nearestDen = string.Empty;
            float minDistance = float.MaxValue;

            foreach (var den in denLocations)
            {
                var denLocation = den.Value;
                float distance = CalculateDistance(playerLocation, (denLocation[0], denLocation[1], denLocation[2]));

                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestDen = den.Key;
                }
            }

            return nearestDen;
        }

        /// <summary>
        /// Calculates the distance between two 3D coordinates
        /// </summary>
        private static float CalculateDistance((float, float, float) loc1, (float, float, float) loc2)
        {
            return MathF.Sqrt(
                MathF.Pow(loc1.Item1 - loc2.Item1, 2) +
                MathF.Pow(loc1.Item2 - loc2.Item2, 2) +
                MathF.Pow(loc1.Item3 - loc2.Item3, 2));
        }

        /// <summary>
        /// Gets the player's current location in the game world
        /// </summary>
        private async Task<(float, float, float)> GetPlayersLocation(CancellationToken token)
        {
            var data = await ReadBlock(RaidDataBlocks.KCoordinates, token) as byte[];

            if (data == null)
            {
                throw new InvalidOperationException("Failed to read player coordinates from memory");
            }

            float x = BitConverter.ToSingle(data, 0);
            float y = BitConverter.ToSingle(data, 4);
            float z = BitConverter.ToSingle(data, 8);

            return (x, y, z);
        }

        /// <summary>
        /// Teleports the player to a specific den location
        /// </summary>
        public async Task TeleportToDen(float x, float y, float z, CancellationToken token)
        {
            const float offset = 1.8f;
            x += offset;

            // Convert coordinates to byte array
            byte[] xBytes = BitConverter.GetBytes(x);
            byte[] yBytes = BitConverter.GetBytes(y);
            byte[] zBytes = BitConverter.GetBytes(z);
            byte[] coordinatesData = new byte[xBytes.Length + yBytes.Length + zBytes.Length];

            Buffer.BlockCopy(xBytes, 0, coordinatesData, 0, xBytes.Length);
            Buffer.BlockCopy(yBytes, 0, coordinatesData, xBytes.Length, yBytes.Length);
            Buffer.BlockCopy(zBytes, 0, coordinatesData, xBytes.Length + yBytes.Length, zBytes.Length);

            // Write the coordinates
            var teleportBlock = RaidDataBlocks.KCoordinates;
            teleportBlock.Size = coordinatesData.Length;
            var currentCoordinateData = await ReadBlock(teleportBlock, token) as byte[];
            _ = await WriteEncryptedBlockSafe(teleportBlock, currentCoordinateData, coordinatesData, token);

            // Set rotation to face North
            float northRX = 0.0f;
            float northRY = -0.63828725f;
            float northRZ = 0.0f;
            float northRW = 0.7697983f;

            // Convert rotation to byte array
            byte[] rotationData = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(northRX), 0, rotationData, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(northRY), 0, rotationData, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(northRZ), 0, rotationData, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(northRW), 0, rotationData, 12, 4);

            // Write the rotation
            var rotationBlock = RaidDataBlocks.KPlayerRotation;
            rotationBlock.Size = rotationData.Length;
            var currentRotationData = await ReadBlock(rotationBlock, token) as byte[];
            _ = await WriteEncryptedBlockSafe(rotationBlock, currentRotationData, rotationData, token);
        }

        /// <summary>
        /// Extracts raid information from the specified map
        /// </summary>
        private async Task<List<(uint Area, uint LotteryGroup, uint Den, uint Seed, uint Flags, bool IsEvent)>> ExtractRaidInfo(TeraRaidMapParent mapType, CancellationToken token)
        {
            byte[] raidData = await ReadRaidsForRegion(mapType, token);

            var raids = new List<(uint Area, uint LotteryGroup, uint Den, uint Seed, uint Flags, bool IsEvent)>();
            for (int i = 0; i < raidData.Length; i += Raid.SIZE)
            {
                var raid = new Raid(raidData.AsSpan()[i..(i + Raid.SIZE)]);
                if (raid.IsValid)
                {
                    raids.Add((raid.Area, raid.LotteryGroup, raid.Den, raid.Seed, raid.Flags, raid.IsEvent));
                }
            }

            return raids;
        }

        /// <summary>
        /// Logs the player's location and den
        /// </summary>
        private async Task LogPlayerLocation(CancellationToken token)
        {
            var playerLocation = await GetPlayersLocation(token);

            // Use cached den locations for all regions
            var blueberryLocations = GetCachedDenLocations(TeraRaidMapParent.Blueberry);
            var kitakamiLocations = GetCachedDenLocations(TeraRaidMapParent.Kitakami);
            var baseLocations = GetCachedDenLocations(TeraRaidMapParent.Paldea);

            // Find the nearest location for each set and keep track of the overall nearest
            var nearestDen = new Dictionary<string, string>
            {
                { "Blueberry", FindNearestLocation(playerLocation, blueberryLocations) },
                { "Kitakami", FindNearestLocation(playerLocation, kitakamiLocations) },
                { "Paldea", FindNearestLocation(playerLocation, baseLocations) }
            };

            var overallNearest = nearestDen
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv =>
            {
                var denLocations = GetCachedDenLocations(kv.Key switch
                {
                    "Blueberry" => TeraRaidMapParent.Blueberry,
                    "Kitakami" => TeraRaidMapParent.Kitakami,
                    "Paldea" => TeraRaidMapParent.Paldea,
                    _ => TeraRaidMapParent.Paldea
                });

                if (!denLocations.TryGetValue(kv.Value, out var denLocationArray))
                    return null;

                var denLocationTuple = (denLocationArray[0], denLocationArray[1], denLocationArray[2]);
                return new { Region = kv.Key, DenIdentifier = kv.Value, Distance = CalculateDistance(playerLocation, denLocationTuple) };
            })
            .Where(x => x != null)
            .OrderBy(d => d!.Distance)
            .FirstOrDefault();

            if (overallNearest == null)
            {
                Log("Unable to determine nearest den location - den location data may be corrupted or missing.");
                return;
            }

            TeraRaidMapParent mapType = overallNearest.Region switch
            {
                "Blueberry" => TeraRaidMapParent.Blueberry,
                "Kitakami" => TeraRaidMapParent.Kitakami,
                "Paldea" => TeraRaidMapParent.Paldea,
                _ => throw new InvalidOperationException("Invalid region")
            };

            var activeRaids = await GetActiveRaidLocations(mapType, token);

            // Find the nearest active raid, if any
            var nearestActiveRaid = activeRaids
                .Select(raid => new { Raid = raid, Distance = CalculateDistance(playerLocation, (raid.Coordinates[0], raid.Coordinates[1], raid.Coordinates[2])) })
                .OrderBy(raid => raid.Distance)
                .FirstOrDefault();

            if (nearestActiveRaid != null)
            {
                // Check if the player is already at the nearest active den
                float distanceToNearestActiveDen = CalculateDistance(playerLocation, (nearestActiveRaid.Raid.Coordinates[0], nearestActiveRaid.Raid.Coordinates[1], nearestActiveRaid.Raid.Coordinates[2]));

                // Define a threshold for how close the player needs to be to be considered "at" the den
                const float threshold = 2.0f;

                uint denSeed = nearestActiveRaid.Raid.Seed;
                string hexDenSeed = denSeed.ToString("X8");
                _denHexSeed = hexDenSeed;

                // Log the nearest active den information
                Log($"Player location: ({playerLocation.Item1:F2}, {playerLocation.Item2:F2}, {playerLocation.Item3:F2})");
                Log($"Nearest active den: {nearestActiveRaid.Raid.DenIdentifier} (Index: {nearestActiveRaid.Raid.Index})");
                Log($"Distance to nearest den: {distanceToNearestActiveDen:F2}");
                Log($"Seed at nearest den: {hexDenSeed}");

                if (distanceToNearestActiveDen > threshold)
                {
                    Log($"Player is {distanceToNearestActiveDen:F2} units away from the nearest active den (threshold: {threshold})");
                }
                else
                {
                    Log($"Player is at the nearest active den");
                }
            }
            else
            {
                Log($"No active dens found in {overallNearest.Region}");
            }

            // Update region flags based on the detected region
            IsKitakami = overallNearest.Region == "Kitakami";
            IsBlueberry = overallNearest.Region == "Blueberry";
        }

        /// <summary>
        /// Gets locations of all active raids in the specified map
        /// </summary>
        private async Task<List<(string DenIdentifier, float[] Coordinates, int Index, uint Seed, uint Flags, bool IsEvent)>> GetActiveRaidLocations(TeraRaidMapParent mapType, CancellationToken token)
        {
            // Read the raw raid data for the region
            byte[] raidData = await ReadRaidsForRegion(mapType, token);

            // Use cached den locations
            var denLocations = GetCachedDenLocations(mapType);

            var activeRaids = new List<(string DenIdentifier, float[] Coordinates, int Index, uint Seed, uint Flags, bool IsEvent)>();

            // Calculate the starting index offset based on the map type
            int startingIndex = mapType switch
            {
                TeraRaidMapParent.Paldea => 0,
                TeraRaidMapParent.Kitakami => KitakamiStartIndex,
                TeraRaidMapParent.Blueberry => BlueberryStartIndex,
                _ => 0
            };

            // Process each raid in the data
            for (int i = 0; i < raidData.Length; i += Raid.SIZE)
            {
                var raid = new Raid(raidData.AsSpan()[i..(i + Raid.SIZE)], mapType);

                // Check if the raid is actually active (visible in game)
                if (raid.IsActive)
                {
                    string raidIdentifier = $"{raid.Area}-{raid.LotteryGroup}-{raid.Den}";
                    if (denLocations.TryGetValue(raidIdentifier, out var coordinates))
                    {
                        int globalIndex = startingIndex + (i / Raid.SIZE);

                        if (mapType == TeraRaidMapParent.Blueberry)
                        {
                            globalIndex -= 1;
                        }

                        activeRaids.Add((raidIdentifier, coordinates, globalIndex, raid.Seed, raid.Flags, raid.IsEvent));
                    }
                }
            }

            Log($"Found {activeRaids.Count} active raids in {mapType}");
            return activeRaids;
        }

        /// <summary>
        /// Updates the game progress level
        /// </summary>
        private async Task WriteProgressLive(GameProgress progress)
        {
            if (Connection is null)
                return;

            var difficultyBlocks = new[]
            {
                (GameProgress.Unlocked3Stars, RaidDataBlocks.KUnlockedRaidDifficulty3),
                (GameProgress.Unlocked4Stars, RaidDataBlocks.KUnlockedRaidDifficulty4),
                (GameProgress.Unlocked5Stars, RaidDataBlocks.KUnlockedRaidDifficulty5),
                (GameProgress.Unlocked6Stars, RaidDataBlocks.KUnlockedRaidDifficulty6),
            };

            foreach (var (requiredProgress, block) in difficultyBlocks)
            {
                bool shouldUnlock = progress >= requiredProgress;
                var toexpect = (bool?)await ReadBlock(block, CancellationToken.None);
                await WriteBlock(shouldUnlock, block, CancellationToken.None, toexpect);
            }
        }

        /// <summary>
        /// Skips to the next raid when too many empty lobbies occur
        /// </summary>
        private async Task SkipRaidOnLosses(CancellationToken token)
        {
            Log($"We had {_settings.LobbyOptions.SkipRaidLimit} lost/empty raids.. Moving on!");

            // Remove skipped temporary raids BEFORE advancing rotation
            RemoveTemporaryRaidIfNeeded("skipped");

            // Create replacement Mystery Raid BEFORE advancing rotation (if needed)
            if (_settings.RaidSettings.MysteryRaids)
            {
                int mysteryRaidCount = _settings.ActiveRaids.Count(raid => raid.Title.Contains(MysteryRaidTitle));
                if (mysteryRaidCount <= 1)
                {
                    try
                    {
                        CreateMysteryRaid();
                    }
                    catch (Exception ex)
                    {
                        Log($"Error in CreateMysteryRaid: {ex.Message}");
                    }
                }
            }

            await SanitizeRotationCount(token).ConfigureAwait(false);
            await EnqueueEmbed(null, "", false, false, true, false, token).ConfigureAwait(false);
            await CloseGame(_hub.Config, token).ConfigureAwait(false);
            await StartGameRaid(_hub.Config, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the URL for an alternate Pokemon sprite image
        /// </summary>
        private static string AltPokeImg(PKM pkm)
        {
            string pkmform = string.Empty;
            if (pkm.Form != 0)
                pkmform = $"-{pkm.Form}";

            return $"https://raw.githubusercontent.com/zyro670/PokeTextures/main/Placeholder_Sprites/scaled_up_sprites/Shiny/AlternateArt/{pkm.Species}{pkmform}.png";
        }

        /// <summary>
        /// Reads raid data from the game
        /// </summary>
        private async Task ReadRaids(CancellationToken token)
        {
            Log("Getting Raid data...");
            await InitializeRaidBlockPointers(token);

            if (_firstRun)
            {
                await LogPlayerLocation(token);
            }
            string game = await DetermineGame(token);
            Container = new(game);
            Container.SetGame(game);

            await SetStoryAndEventProgress(token);
            TeraRaidMapParent currentRegion = await DetectCurrentRegion(token);

            var allRaids = new List<Raid>();
            var allEncounters = new List<ITeraRaid>();
            var allRewards = new List<List<(int, int, int)>>();

            Log($"Reading {currentRegion} Raids...");
            var regionData = await _raidMemoryManager.ReadRaidData(currentRegion, token);
            var (regionRaids, regionEncounters, regionRewards) = await ProcessRaids(regionData, currentRegion, token);

            allRaids.AddRange(regionRaids);
            allEncounters.AddRange(regionEncounters);
            allRewards.AddRange(regionRewards);

            // Set combined data to container and process all raids
            Container.SetRaids(allRaids);
            Container.SetEncounters(allEncounters);
            Container.SetRewards(allRewards);
            await ProcessAllRaids(token);

            Log($"Successfully processed {allRaids.Count} raids from {currentRegion}");
        }

        /// <summary>
        /// Processes raids for a specific map
        /// </summary>
        private async Task<(List<Raid>, List<ITeraRaid>, List<List<(int, int, int)>>)> ProcessRaids(byte[] data, TeraRaidMapParent mapType, CancellationToken token)
        {
            int delivery, enc;
            var tempContainer = new RaidContainer(Container!.Game);
            tempContainer.SetGame(Container.Game);

            Log("Reading event raid status...");
            // Read event raids into tempContainer
            var baseBlockKeyPointer = await SwitchConnection.PointerAll(Offsets.BlockKeyPointer, token).ConfigureAwait(false);
            await ReadEventRaids(baseBlockKeyPointer, tempContainer, token).ConfigureAwait(false);
            await ReadEventRaids(baseBlockKeyPointer, Container, token).ConfigureAwait(false);

            (delivery, enc) = tempContainer.ReadAllRaids(data, _storyProgress, _eventProgress, 0, mapType);

            var raidsList = tempContainer.Raids.ToList();
            var encountersList = tempContainer.Encounters.ToList();
            var rewardsList = tempContainer.Rewards.Select(r => r.ToList()).ToList();

            return (raidsList, encountersList, rewardsList);
        }

        /// <summary>
        /// Initializes pointers to raid data blocks
        /// </summary>
        private async Task InitializeRaidBlockPointers(CancellationToken token)
        {
            _raidBlockPointerP = await SwitchConnection.PointerAll(RaidCrawler.Core.Structures.Offsets.RaidBlockPointerBase.ToArray(), token).ConfigureAwait(false);
            _raidBlockPointerK = await SwitchConnection.PointerAll(RaidCrawler.Core.Structures.Offsets.RaidBlockPointerKitakami.ToArray(), token).ConfigureAwait(false);
            _raidBlockPointerB = await SwitchConnection.PointerAll(RaidCrawler.Core.Structures.Offsets.RaidBlockPointerBlueberry.ToArray(), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Determines the game version (Scarlet or Violet)
        /// </summary>
        private async Task<string> DetermineGame(CancellationToken token)
        {
            string id = await SwitchConnection.GetTitleID(token).ConfigureAwait(false);
            return id switch
            {
                RaidCrawler.Core.Structures.Offsets.ScarletID => "Scarlet",
                RaidCrawler.Core.Structures.Offsets.VioletID => "Violet",
                _ => "",
            };
        }

        /// <summary>
        /// Sets the story and event progress levels
        /// </summary>
        private async Task SetStoryAndEventProgress(CancellationToken token)
        {
            var baseBlockKeyPointer = await SwitchConnection.PointerAll(Offsets.BlockKeyPointer, token).ConfigureAwait(false);
            _storyProgress = await GetStoryProgress(baseBlockKeyPointer, token).ConfigureAwait(false);
            _eventProgress = Math.Min(_storyProgress, 3);
        }

        /// <summary>
        /// Reads raid data for a specific region
        /// </summary>
        private async Task<byte[]> ReadRaidsForRegion(TeraRaidMapParent region, CancellationToken token)
        {
            return region switch
            {
                TeraRaidMapParent.Paldea => await SwitchConnection.ReadBytesAbsoluteAsync(_raidBlockPointerP + RaidBlock.HEADER_SIZE, (int)RaidBlock.SIZE_BASE, token).ConfigureAwait(false),
                TeraRaidMapParent.Kitakami => await SwitchConnection.ReadBytesAbsoluteAsync(_raidBlockPointerK, (int)RaidBlock.SIZE_KITAKAMI, token).ConfigureAwait(false),
                TeraRaidMapParent.Blueberry => await SwitchConnection.ReadBytesAbsoluteAsync(_raidBlockPointerB, (int)RaidBlock.SIZE_BLUEBERRY, token).ConfigureAwait(false),
                _ => throw new ArgumentException("Invalid region", nameof(region))
            };
        }

        /// <summary>
        /// Gets possible group IDs for distribution and might raids
        /// </summary>
        private static (List<int> distGroupIDs, List<int> mightGroupIDs) GetPossibleGroups(RaidContainer container)
        {
            List<int> distGroupIDs = [];
            List<int> mightGroupIDs = [];

            if (container.DistTeraRaids != null)
            {
                foreach (TeraDistribution e in container.DistTeraRaids)
                {
                    if (TeraDistribution.AvailableInGame(e.Entity, container.Game) && !distGroupIDs.Contains(e.DeliveryGroupID))
                        distGroupIDs.Add(e.DeliveryGroupID);
                }
            }

            if (container.MightTeraRaids != null)
            {
                foreach (TeraMight e in container.MightTeraRaids)
                {
                    if (TeraMight.AvailableInGame(e.Entity, container.Game) && !mightGroupIDs.Contains(e.DeliveryGroupID))
                        mightGroupIDs.Add(e.DeliveryGroupID);
                }
            }

            return (distGroupIDs, mightGroupIDs);
        }

        /// <summary>
        /// Processes all raids and updates information
        /// </summary>
        private async Task ProcessAllRaids(CancellationToken token)
        {
            var allRaids = Container.Raids;
            var allEncounters = Container.Encounters;
            var allRewards = Container.Rewards;

            if (string.IsNullOrEmpty(_denHexSeed))
                return;

            uint denHexSeedUInt;
            denHexSeedUInt = uint.Parse(_denHexSeed, NumberStyles.AllowHexSpecifier);
            await FindSeedIndexInRaids(denHexSeedUInt, token);
            var currentRegion = await DetectCurrentRegion(token);
            var raidInfoList = await ExtractRaidInfo(currentRegion, token);
            bool newEventSpeciesFound = false;
            var (distGroupIDs, mightGroupIDs) = GetPossibleGroups(Container);

            int raidsToCheck = Math.Min(5, Math.Min(allRaids.Count, allEncounters.Count));

            if (!IsKitakami && !IsBlueberry)
            {
                // check if new event species is found
                for (int i = 0; i < raidsToCheck; i++)
                {
                    var raid = allRaids[i];
                    var encounter = allEncounters[i];
                    bool isEventRaid = raid.Flags == 2 || raid.Flags == 3;

                    if (isEventRaid)
                    {
                        string speciesName = SpeciesName.GetSpeciesName(encounter.Species, 2);
                        if (!SpeciesToGroupIDMap.ContainsKey(speciesName))
                        {
                            newEventSpeciesFound = true;
                            SpeciesToGroupIDMap.Clear(); // Clear the map as we've found a new event species
                            break; // No need to check further
                        }
                    }
                }
            }

            int maxRaidIndex = Math.Min(allRaids.Count, allEncounters.Count);
            for (int i = 0; i < maxRaidIndex; i++)
            {
                if (newEventSpeciesFound)
                {
                    // stuff for paldea events
                    var raid = allRaids[i];
                    var encounter1 = allEncounters[i];
                    bool isDistributionRaid = raid.Flags == 2;
                    bool isMightRaid = raid.Flags == 3;
                    var (Area, LotteryGroup, Den, Seed, Flags, IsEvent) = raidInfoList.FirstOrDefault(r =>
                    r.Seed == raid.Seed &&
                    r.Flags == raid.Flags &&
                    r.Area == raid.Area &&
                    r.LotteryGroup == raid.LotteryGroup &&
                    r.Den == raid.Den);

                    string denIdentifier = $"{Area}-{LotteryGroup}-{Den}";

                    if (isDistributionRaid || isMightRaid)
                    {
                        string speciesName = SpeciesName.GetSpeciesName(encounter1.Species, 2);
                        string speciesKey = string.Join("", speciesName.Split(' '));
                        int groupID = -1;

                        if (isDistributionRaid)
                        {
                            var distRaid = Container.DistTeraRaids.FirstOrDefault(d => d.Species == encounter1.Species && d.Form == encounter1.Form);
                            if (distRaid != null)
                            {
                                groupID = distRaid.DeliveryGroupID;
                            }
                        }
                        else if (isMightRaid)
                        {
                            var mightRaid = Container.MightTeraRaids.FirstOrDefault(m => m.Species == encounter1.Species && m.Form == encounter1.Form);
                            if (mightRaid != null)
                            {
                                groupID = mightRaid.DeliveryGroupID;
                            }
                        }

                        if (groupID != -1)
                        {
                            if (!SpeciesToGroupIDMap.TryGetValue(speciesKey, out List<(int GroupID, int Index, string DenIdentifier)>? value))
                            {
                                SpeciesToGroupIDMap[speciesKey] = [(groupID, i, denIdentifier)];
                            }
                            else
                            {
                                value.Add((groupID, i, denIdentifier));
                            }
                        }
                    }
                }

                var (pk, seed) = IsSeedReturned(allEncounters[i], allRaids[i]);

                for (int a = 0; a < _settings.ActiveRaids.Count; a++)
                {
                    uint set;
                    try
                    {
                        set = uint.Parse(_settings.ActiveRaids[a].Seed, NumberStyles.AllowHexSpecifier);
                    }
                    catch (FormatException)
                    {
                        Log($"Invalid seed format detected. Removing {_settings.ActiveRaids[a].Seed} from list.");
                        _settings.ActiveRaids.RemoveAt(a);
                        a--;  // Decrement the index so that it does not skip the next element.
                        continue;  // Skip to the next iteration.
                    }
                    if (seed == set)
                    {
                        // Update Species and Form in ActiveRaids
                        _settings.ActiveRaids[a].Species = (Species)allEncounters[i].Species;
                        _settings.ActiveRaids[a].SpeciesForm = allEncounters[i].Form;

                        // Area Text
                        var areaText = $"{Areas.GetArea((int)(allRaids[i].Area - 1), allRaids[i].MapParent)} - Den {allRaids[i].Den}";
                        Log($"Seed {seed:X8} found for {(Species)allEncounters[i].Species} in {areaText}");
                    }
                }
            }
        }

        private static (PK9, uint) IsSeedReturned(ITeraRaid encounter, Raid raid)
        {
            var shiny = raid.IsShiny ? Shiny.Always : Shiny.Never;
            var gender = PersonalTable.SV.GetFormEntry(encounter.Species, encounter.Form).Gender;
            var param = new GenerateParam9(encounter.Species, gender, encounter.FlawlessIVCount, 1, 0, 0,
                SizeType9.RANDOM, 0, encounter.Ability, shiny);

            var pk = new PK9
            {
                Species = encounter.Species,
                Form = encounter.Form,
                Move1 = encounter.Move1,
                Move2 = encounter.Move2,
                Move3 = encounter.Move3,
                Move4 = encounter.Move4,
            };
            Encounter9RNG.GenerateData(pk, param, EncounterCriteria.Unrestricted, raid.Seed);
            return (pk, raid.Seed);
        }

        /// <summary>
        /// Finds the index of a seed in raid data
        /// </summary>
        private async Task FindSeedIndexInRaids(uint denHexSeedUInt, CancellationToken token)
        {
            // Search in Paldea region
            var dataP = await SwitchConnection.ReadBytesAbsoluteAsync(_raidBlockPointerP, 2304, token).ConfigureAwait(false);
            for (int i = 0; i < PaldeaRaidCount; i++)
            {
                var seed = BitConverter.ToUInt32(dataP.AsSpan(Raid.SIZE + i * Raid.SIZE, 4));
                if (seed == denHexSeedUInt)
                {
                    _seedIndexToReplace = i;
                    return;
                }
            }

            // Search in Kitakami region
            var dataK = await SwitchConnection.ReadBytesAbsoluteAsync(_raidBlockPointerK + 0x10, 0xC80, token).ConfigureAwait(false);
            for (int i = 0; i < BlueberryStartIndex; i++)
            {
                var seed = BitConverter.ToUInt32(dataK.AsSpan(i * Raid.SIZE, 4));
                if (seed == denHexSeedUInt)
                {
                    _seedIndexToReplace = i + KitakamiStartIndex;
                    return;
                }
            }

            // Search in Blueberry region
            var dataB = await SwitchConnection.ReadBytesAbsoluteAsync(_raidBlockPointerB + 0x10, 0xA00, token).ConfigureAwait(false);
            for (int i = BlueberryStartIndex; i < 118; i++)
            {
                var seed = BitConverter.ToUInt32(dataB.AsSpan((i - BlueberryStartIndex) * Raid.SIZE, 4));
                if (seed == denHexSeedUInt)
                {
                    _seedIndexToReplace = i - 1;
                    return;
                }
            }

            Log($"Seed {denHexSeedUInt:X8} not found in any region.");
        }

        /// <summary>
        /// Creates raid info for API command
        /// </summary>
        public static (PK9, Embed) RaidInfoCommand(string seedValue, int contentType, TeraRaidMapParent map, int storyProgressLevel,
            int raidDeliveryGroupID, List<string> rewardsToShow, bool moveTypeEmojis, List<MoveTypeEmojiInfo> customTypeEmojis,
            int queuePosition = 0, bool isEvent = false, int languageId = 1)
        {
            if (Container == null)
            {
                var errorEmbed = new EmbedBuilder
                {
                    Title = "Error: Raid Data Not Available",
                    Description = "The raid data hasn't been loaded. Please try again in a moment.",
                    Color = Color.Red
                };

                return (new PK9(), errorEmbed.Build());
            }

            // Process and parse raid data
            byte[] enabled = StringToByteArray("00000001");
            byte[] area = StringToByteArray("00000001");
            byte[] displaytype = StringToByteArray("00000001");
            byte[] spawnpoint = StringToByteArray("00000001");
            byte[] thisseed = StringToByteArray(seedValue);
            byte[] unused = StringToByteArray("00000000");
            byte[] content = StringToByteArray($"0000000{contentType}");
            byte[] leaguepoints = StringToByteArray("00000000");
            byte[] raidbyte = enabled.Concat(area).ToArray().Concat(displaytype).ToArray().Concat(spawnpoint).ToArray().Concat(thisseed).ToArray().Concat(unused).ToArray().Concat(content).ToArray().Concat(leaguepoints).ToArray();

            storyProgressLevel = storyProgressLevel switch
            {
                3 => 1,
                4 => 2,
                5 => 3,
                6 => 4,
                0 => 0,
                _ => 4 // default 6Unlocked
            };

            // Create raid and encounter objects
            var raid = new Raid(raidbyte, map);
            var progress = storyProgressLevel;
            var raid_delivery_group_id = raidDeliveryGroupID;
            var encounter = raid.GetTeraEncounter(Container, raid.IsEvent ? 3 : progress, contentType == 3 ? 1 : raid_delivery_group_id);
            var reward = encounter.GetRewards(Container, raid, 0);
            var stars = raid.IsEvent ? encounter.Stars : raid.GetStarCount(raid.Difficulty, storyProgressLevel, raid.IsBlack);
            var teraType = raid.GetTeraType(encounter);
            var level = encounter.Level;

            // Create GenerateParam9 with explicit shiny state to bypass early-return check in GenerateData
            var shiny = raid.IsShiny ? Shiny.Always : Shiny.Never;
            var gender = PersonalTable.SV.GetFormEntry(encounter.Species, encounter.Form).Gender;
            var param = new GenerateParam9(encounter.Species, gender, encounter.FlawlessIVCount, 1, 0, 0,
                SizeType9.RANDOM, 0, encounter.Ability, shiny);

            var pk = new PK9
            {
                Species = encounter.Species,
                Form = encounter.Form,
                Move1 = encounter.Move1,
                Move2 = encounter.Move2,
                Move3 = encounter.Move3,
                Move4 = encounter.Move4,
                TeraTypeOriginal = (MoveType)teraType,
                CurrentLevel = (byte)level
            };
            Encounter9RNG.GenerateData(pk, param, EncounterCriteria.Unrestricted, raid.Seed);

            // Get strings in the selected language
            var strings = GameInfo.GetStrings(((LanguageID)languageId).GetLanguageCode());
            var useTypeEmojis = moveTypeEmojis;
            var typeEmojis = customTypeEmojis
                .Where(e => !string.IsNullOrEmpty(e.EmojiCode))
                .ToDictionary(
                    e => e.MoveType,
                    e => $"{e.EmojiCode}"
                );

            // Build moves list
            var movesList = new StringBuilder();
            bool hasMoves = false;

            // Process regular moves
            for (int i = 0; i < 4; i++)
            {
                int moveId = i switch { 0 => pk.Move1, 1 => pk.Move2, 2 => pk.Move3, 3 => pk.Move4, _ => 0 };
                if (moveId != 0)
                {
                    string moveName = strings.Move[moveId];
                    byte moveTypeId = MoveInfo.GetType((ushort)moveId, pk.Context);
                    MoveType moveType = (MoveType)moveTypeId;

                    if (useTypeEmojis && typeEmojis.TryGetValue(moveType, out var moveEmoji))
                        movesList.AppendLine($"{moveEmoji} {moveName}");
                    else
                        movesList.AppendLine($"\\- {moveName}");

                    hasMoves = true;
                }
            }

            // Process extra moves
            var extraMovesList = new StringBuilder();
            bool hasExtraMoves = false;

            if (encounter.ExtraMoves.Length > 0)
            {
                foreach (var moveId in encounter.ExtraMoves)
                {
                    if (moveId != 0)
                    {
                        string moveName = strings.Move[moveId];
                        byte moveTypeId = MoveInfo.GetType(moveId, pk.Context);
                        MoveType moveType = (MoveType)moveTypeId;

                        if (useTypeEmojis && typeEmojis.TryGetValue(moveType, out var moveEmoji))
                            extraMovesList.AppendLine($"{moveEmoji} {moveName}");
                        else
                            extraMovesList.AppendLine($"\\- {moveName}");

                        hasExtraMoves = true;
                    }
                }
            }

            // Build final moves string
            string finalMoves = movesList.ToString();
            if (hasExtraMoves)
            {
                finalMoves += $"**Extra Moves:**\n{extraMovesList}";
            }

            // Process rewards
            string specialRewards = GetSpecialRewards(reward, rewardsToShow, languageId);

            // Build the embed
            var teraTypeLower = strings.Types[teraType].ToLower();
            var teraIconUrl = $"https://raw.githubusercontent.com/hexbyt3/sprites/main/teraicons/icon1/{teraTypeLower}.png";
            var disclaimer = $"Current Position: {queuePosition}";
            var titlePrefix = raid.IsShiny ? "Shiny " : "";
            var formName = ShowdownParsing.GetStringFromForm(pk.Form, strings, pk.Species, pk.Context);
            var authorName = $"{stars} ★ {titlePrefix}{strings.Species[encounter.Species]}{(pk.Form != 0 ? $"-{formName}" : "")}{(isEvent ? " (Event Raid)" : "")}";

            (int R, int G, int B) = RaidExtensions<PK9>.GetDominantColorAsync(RaidExtensions<PK9>.PokeImg(pk, false, false)).ConfigureAwait(false).GetAwaiter().GetResult();
            var embedColor = new Color(R, G, B);

            var embed = new EmbedBuilder
            {
                Color = embedColor,
                ThumbnailUrl = RaidExtensions<PK9>.PokeImg(pk, false, false),
            };
            embed.AddField(x =>
            {
                x.Name = "**__Stats__**";
                x.Value = $"{Format.Bold($"TeraType:")} {strings.Types[teraType]} \n" +
                          $"{Format.Bold($"Level:")} {level}\n" +
                          $"{Format.Bold($"Ability:")} {strings.Ability[pk.Ability]}\n" +
                          $"{Format.Bold("Nature:")} {strings.Natures[(int)pk.Nature]}\n" +
                          $"{Format.Bold("IVs:")} {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}\n" +
                          $"{Format.Bold($"Scale:")} {ScaleLabel(pk.Scale)}";
                x.IsInline = true;
            });

            if (hasMoves)
            {
                embed.AddField("**__Moves__**", movesList.ToString(), true);
            }
            else
            {
                embed.AddField("**__Moves__**", "No moves available", true);
            }

            if (hasExtraMoves)
            {
                embed.AddField("**__Extra Moves__**", extraMovesList.ToString(), true);
            }

            if (!string.IsNullOrEmpty(specialRewards))
            {
                embed.AddField("**__Special Rewards__**", specialRewards, true);
            }
            else
            {
                embed.AddField("**__Special Rewards__**", "No special rewards available", true);
            }

            var programIconUrl = "https://raw.githubusercontent.com/hexbyt3/sprites/main/imgs/icon4.png";
            embed.WithFooter(new EmbedFooterBuilder()
            {
                Text = disclaimer,
                IconUrl = programIconUrl
            });

            embed.WithAuthor(auth =>
            {
                auth.Name = authorName;
                auth.IconUrl = teraIconUrl;
            });
            var englishStrings = GameInfo.GetStrings("en");
            var englishMovesList = new StringBuilder();
            var englishExtraMovesList = new StringBuilder();
            for (int i = 0; i < 4; i++)
            {
                int moveId = i switch { 0 => pk.Move1, 1 => pk.Move2, 2 => pk.Move3, 3 => pk.Move4, _ => 0 };
                if (moveId != 0)
                {
                    string englishMoveName = englishStrings.Move[moveId];
                    englishMovesList.AppendLine($"- {englishMoveName}");
                }
            }
            if (encounter.ExtraMoves.Length > 0)
            {
                foreach (var moveId in encounter.ExtraMoves)
                {
                    if (moveId != 0)
                    {
                        string englishMoveName = englishStrings.Move[moveId];
                        englishExtraMovesList.AppendLine($"- {englishMoveName}");
                    }
                }
            }
            string englishFinalMoves = englishMovesList.ToString();
            if (englishExtraMovesList.Length > 0)
            {
                englishFinalMoves += $"Extra Moves:\n{englishExtraMovesList}";
            }
            string englishSpecialRewards = GetSpecialRewards(reward, rewardsToShow, 2);
            string englishFormName = ShowdownParsing.GetStringFromForm(pk.Form, englishStrings, pk.Species, pk.Context);
            string englishAuthorName = $"{stars} ★ {titlePrefix}{englishStrings.Species[encounter.Species]}{(pk.Form != 0 ? $"-{englishFormName}" : "")}{(isEvent ? " (Event Raid)" : "")}";
            RaidEmbedEnglishHelpers.RaidEmbedTitle = englishAuthorName;
            RaidEmbedEnglishHelpers.RaidSpeciesGender = pk.Gender == 0 ? "Male" : (pk.Gender == 1 ? "Female" : "Genderless");
            RaidEmbedEnglishHelpers.RaidSpeciesAbility = englishStrings.Ability[pk.Ability];
            RaidEmbedEnglishHelpers.RaidSpeciesNature = englishStrings.Natures[(int)pk.Nature];
            RaidEmbedEnglishHelpers.RaidSpeciesTeraType = englishStrings.Types[teraType];
            RaidEmbedEnglishHelpers.Moves = englishMovesList.ToString().TrimEnd();
            RaidEmbedEnglishHelpers.ExtraMoves = englishExtraMovesList.ToString().TrimEnd();
            RaidEmbedEnglishHelpers.ScaleText = ScaleLabel(pk.Scale);
            RaidEmbedEnglishHelpers.SpecialRewards = englishSpecialRewards;

            return (pk, embed.Build());
        }

        /// <summary>
        /// PKHeX 26.x renamed the middle size from AV to M; raid embeds keep AV.
        /// </summary>
        private static string ScaleLabel(byte scale)
        {
            var rating = PokeSizeDetailedUtil.GetSizeRating(scale);
            return rating == PokeSizeDetailed.M ? "AV" : rating.ToString();
        }

        public static byte[] StringToByteArray(string hex)
        {
            int NumberChars = hex.Length;
            byte[] bytes = new byte[NumberChars / 2];
            for (int i = 0; i < NumberChars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            Array.Reverse(bytes);
            return bytes;
        }

        /// </summary>
        /// <param name="config">Bot configuration</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>True if save was successful</returns>
        private async Task<bool> SaveGame(PokeRaidHubConfig config, CancellationToken token)
        {
            Log("Saving the Game...");

            // First ensure we're on the overworld
            if (!await RecoverToOverworld(token).ConfigureAwait(false))
            {
                Log("Failed to reach overworld before saving. Attempting save anyway.");
            }

            // Check if we're currently connected online
            bool isOnline = await IsConnectedOnline(_connectedOffset, token).ConfigureAwait(false);

            if (isOnline)
            {
                // If online, we need to disconnect first
                Log("Currently connected online. Disconnecting first...");
                await Task.Delay(2_000, token).ConfigureAwait(false);
                await Click(X, 2_000, token).ConfigureAwait(false);
                await Click(L, 5_000, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);

                // Verify we're disconnected
                bool disconnected = !await IsConnectedOnline(_connectedOffset, token).ConfigureAwait(false);
                if (!disconnected)
                {
                    Log("Failed to disconnect from online. Trying again...");
                    await RecoverToOverworld(token).ConfigureAwait(false);
                    await Click(X, 2_000, token).ConfigureAwait(false);
                    await Click(L, 5_000, token).ConfigureAwait(false);
                }

                // Return to overworld
                await RecoverToOverworld(token).ConfigureAwait(false);
            }

            // Now do the actual save by opening X menu and pressing L
            await Click(X, 2_000, token).ConfigureAwait(false);
            await Click(L, 5_000 + config.Timings.ExtraTimeConnectOnline, token).ConfigureAwait(false);

            // Wait a bit to ensure save completes, then return to overworld
            await Task.Delay(2_000, token).ConfigureAwait(false);
            await RecoverToOverworld(token).ConfigureAwait(false);

            Log("Game saved successfully.");
            return true;
        }
    }
}
