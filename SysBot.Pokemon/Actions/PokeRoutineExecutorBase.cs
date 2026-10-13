using PKHeX.Core;
using SysBot.Base;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Pokemon
{
    public abstract class PokeRoutineExecutorBase : SwitchRoutineExecutor<PokeBotState>
    {
        public const decimal BotbaseVersion = 2.4m;

        // sys-botbase releases went 2.1, 2.11, 2.2, 2.3, 2.31, 2.4, 2.41, 2.5, so the digits after
        // the dot are a decimal fraction, not a minor number. usb-botbase's FW21 build answers
        // "2.4.1", which sits between its 2.4 and 2.42.
        public static decimal ParseBotbaseVersion(string reply)
        {
            var parts = reply.Trim('\0', '\r', '\n', ' ').Split('.');
            var text = parts.Length > 1 ? parts[0] + "." + string.Concat(parts[1..]) : parts[0];
            return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var version) ? version : 0;
        }

        protected PokeRoutineExecutorBase(IConsoleBotManaged<IConsoleConnectionAsync> cfg) : base(cfg)
        {
        }

        public LanguageID GameLang { get; private set; }
        public GameVersion Version { get; private set; }
        public string InGameName { get; private set; } = "SysBot.NET";

        public override string GetSummary()
        {
            var current = Config.CurrentRoutineType;
            var initial = Config.InitialRoutine;
            if (current == initial)
                return $"{Connection.Label} - {initial}";
            return $"{Connection.Label} - {initial} ({current})";
        }

        protected void InitSaveData(SaveFile sav)
        {
            GameLang = (LanguageID)sav.Language;
            Version = sav.Version;
            InGameName = sav.OT;
            Connection.Label = $"{InGameName}-{sav.DisplayTID:000000}";
            Log($"{Connection.Name} identified as {Connection.Label}, using {GameLang}.");
        }

        protected bool IsValidTrainerData() => GameLang is (> 0 and <= LanguageID.ChineseT) && InGameName.Length > 0 && Version > 0;

        public override void SoftStop() => Config.Pause();

        public async Task Click(SwitchButton b, int delayMin, int delayMax, CancellationToken token) =>
            await Click(b, Util.Rand.Next(delayMin, delayMax), token).ConfigureAwait(false);

        public async Task SetStick(SwitchStick stick, short x, short y, int delayMin, int delayMax, CancellationToken token) =>
            await SetStick(stick, x, y, Util.Rand.Next(delayMin, delayMax), token).ConfigureAwait(false);
    }
}