using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Base
{
    public abstract class SwitchRoutineExecutor<T> : RoutineExecutor<T> where T : class, IConsoleBotConfig
    {
        public readonly bool UseCRLF;
        protected readonly ISwitchConnectionAsync SwitchConnection;

        protected SwitchRoutineExecutor(IConsoleBotManaged<IConsoleConnection, IConsoleConnectionAsync> cfg) : base(cfg)
        {
            UseCRLF = cfg.GetInnerConfig() is ISwitchConnectionConfig { UseCRLF: true };
            if (Connection is not ISwitchConnectionAsync connect)
                throw new System.Exception("Not a valid switch connection");
            SwitchConnection = connect;
        }

        public override async Task InitialStartup(CancellationToken token) => await EchoCommands(false, token).ConfigureAwait(false);

        public async Task Click(SwitchButton b, int delay, CancellationToken token)
        {
            await Connection.SendAsync(SwitchCommand.Click(b, UseCRLF), token).ConfigureAwait(false);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        public async Task PressAndHold(SwitchButton b, int hold, int delay, CancellationToken token)
        {
            await Connection.SendAsync(SwitchCommand.Hold(b, UseCRLF), token).ConfigureAwait(false);
            await Task.Delay(hold, token).ConfigureAwait(false);
            await Connection.SendAsync(SwitchCommand.Release(b, UseCRLF), token).ConfigureAwait(false);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        // sys-botbase finishes a press before it answers the next command, so an answer
        // proves the press landed. Blind presses sent through a Wi-Fi stall arrive late and
        // bunched together, and a menu macro built from them walks somewhere else; on
        // 10/07 the clock rollback left a host's Switch with no game running for hours.
        public async Task ClickConfirmed(SwitchButton b, int delay, CancellationToken token)
        {
            await Connection.SendAsync(SwitchCommand.Click(b, UseCRLF), token).ConfigureAwait(false);
            await ConfirmPress(token).ConfigureAwait(false);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        public async Task PressAndHoldConfirmed(SwitchButton b, int hold, int delay, CancellationToken token)
        {
            await Connection.SendAsync(SwitchCommand.Hold(b, UseCRLF), token).ConfigureAwait(false);
            await ConfirmPress(token).ConfigureAwait(false);
            await Task.Delay(hold, token).ConfigureAwait(false);
            await Connection.SendAsync(SwitchCommand.Release(b, UseCRLF), token).ConfigureAwait(false);
            await ConfirmPress(token).ConfigureAwait(false);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        // A healthy answer takes well under a second. One that needed a retry means the
        // connection was replaced, and a press still queued on the old one was lost.
        public static TimeSpan PressConfirmLimit { get; set; } = TimeSpan.FromSeconds(1.5);

        private async Task ConfirmPress(CancellationToken token)
        {
            var waited = Stopwatch.StartNew();
            await SwitchConnection.GetTitleID(token).ConfigureAwait(false);
            if (waited.Elapsed > PressConfirmLimit)
                throw new PressNotConfirmedException($"The console took {waited.Elapsed.TotalSeconds:0.0} seconds to confirm a button press.");
        }

        public async Task DaisyChainCommands(int delay, IEnumerable<SwitchButton> buttons, CancellationToken token)
        {
            SwitchCommand.Configure(SwitchConfigureParameter.mainLoopSleepTime, delay, UseCRLF);
            var commands = buttons.Select(z => SwitchCommand.Click(z, UseCRLF)).ToArray();
            var chain = commands.SelectMany(x => x).ToArray();
            await Connection.SendAsync(chain, token).ConfigureAwait(false);
            SwitchCommand.Configure(SwitchConfigureParameter.mainLoopSleepTime, 0, UseCRLF);
        }

        public async Task SetStick(SwitchStick stick, short x, short y, int delay, CancellationToken token)
        {
            var cmd = SwitchCommand.SetStick(stick, x, y, UseCRLF);
            await Connection.SendAsync(cmd, token).ConfigureAwait(false);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        public async Task DetachController(CancellationToken token)
        {
            await Connection.SendAsync(SwitchCommand.DetachController(UseCRLF), token).ConfigureAwait(false);
        }

        public async Task SetScreen(ScreenState state, CancellationToken token)
        {
            await Connection.SendAsync(SwitchCommand.SetScreen(state, UseCRLF), token).ConfigureAwait(false);
        }

        public async Task EchoCommands(bool value, CancellationToken token)
        {
            var cmd = SwitchCommand.Configure(SwitchConfigureParameter.echoCommands, value ? 1 : 0, UseCRLF);
            await Connection.SendAsync(cmd, token).ConfigureAwait(false);
        }

        /// <inheritdoc cref="ReadUntilChanged(ulong,byte[],int,int,bool,bool,CancellationToken)"/>
        public async Task<bool> ReadUntilChanged(uint offset, byte[] comparison, int waitms, int waitInterval, bool match, CancellationToken token) =>
            await ReadUntilChanged(offset, comparison, waitms, waitInterval, match, false, token).ConfigureAwait(false);

        /// <summary>
        /// Reads an offset until it changes to either match or differ from the comparison value.
        /// </summary>
        /// <returns>If <see cref="match"/> is set to true, then the function returns true when the offset matches the given value.<br>Otherwise, it returns true when the offset no longer matches the given value.</br></returns>
        public async Task<bool> ReadUntilChanged(ulong offset, byte[] comparison, int waitms, int waitInterval, bool match, bool absolute, CancellationToken token)
        {
            var sw = new Stopwatch();
            sw.Start();
            do
            {
                var task = absolute
                    ? SwitchConnection.ReadBytesAbsoluteAsync(offset, comparison.Length, token)
                    : SwitchConnection.ReadBytesAsync((uint)offset, comparison.Length, token);
                var result = await task.ConfigureAwait(false);
                if (match == result.SequenceEqual(comparison))
                    return true;

                await Task.Delay(waitInterval, token).ConfigureAwait(false);
            } while (sw.ElapsedMilliseconds < waitms);
            return false;
        }

        public async Task DaySkip(CancellationToken token) => await Connection.SendAsync(SwitchCommand.DaySkip(UseCRLF), token).ConfigureAwait(false);

        public async Task ResetTime(CancellationToken token) => await Connection.SendAsync(SwitchCommand.ResetTime(UseCRLF), token).ConfigureAwait(false);
    }
}