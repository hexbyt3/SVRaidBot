using System;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Base
{
    public class BotSource<T> where T : class, IConsoleBotConfig
    {
        public readonly RoutineExecutor<T> Bot;
        private CancellationTokenSource Source = new();

        public BotSource(RoutineExecutor<T> bot) => Bot = bot;

        public bool IsRunning { get; private set; }
        public bool IsPaused { get; private set; }

        public bool IsStopping { get; set; }

        // The routine that is running now. Stop waits for it to really end, so a
        // quick Stop then Start can't leave two loops sharing one Switch. Each launch
        // gets a generation; a routine that outlives its Stop must not touch the
        // state of the one launched after it.
        private Task _routine = Task.CompletedTask;
        private int _generation;

        // Connecting alone can take ~95 s when the Switch is off the network.
        private static readonly TimeSpan StopWait = TimeSpan.FromMinutes(2);

        // A crashed bot restarts after a growing pause instead of giving up, so an
        // overnight Wi-Fi outage costs the outage and not the rest of the night.
        private static readonly TimeSpan[] RestartDelays =
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
        private int _failuresInARow;
        private DateTime _lastFailure;
        private const int AlertAfterCrashes = 3;
        private const int AlertEveryCrashes = 12;

        // The third crash in a row, then about once an hour while the five-minute restarts go on.
        public static bool ShouldAlertOwner(int crashesInARow) =>
            crashesInARow >= AlertAfterCrashes && (crashesInARow - AlertAfterCrashes) % AlertEveryCrashes == 0;

        public void Stop()
        {
            if (!IsRunning || IsStopping)
                return;

            IsStopping = true;
            Source.Cancel();
            Source = new CancellationTokenSource();
            Interlocked.Increment(ref _generation);
            var routine = _routine;

            Task.Run(async () =>
            {
                try
                {
                    // The routine runs HardStop itself on the way out; only do it here
                    // when it is stuck and never got there.
                    if (await Task.WhenAny(routine, Task.Delay(StopWait)).ConfigureAwait(false) != routine)
                    {
                        LogUtil.LogError("The bot did not stop within 2 minutes.", Bot.Connection.Name);
                        await Bot.HardStop().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Stopping the bot failed: {ex.Message}", Bot.Connection.Name);
                }
                finally
                {
                    IsPaused = IsRunning = IsStopping = false;
                }
            });
        }

        public void Pause()
        {
            if (!IsRunning || IsStopping)
                return;

            IsPaused = true;
            Bot.SoftStop();
        }

        public void Start() => LaunchWhenStopped(Bot.RunAsync);

        public void RebootAndStop() => LaunchWhenStopped(Bot.RebootAndStopAsync);

        // A start asked for while a stop is still finishing waits its turn instead of being dropped.
        private void LaunchWhenStopped(Func<CancellationToken, Task> routine)
        {
            if (IsPaused)
                Stop(); // can't soft-resume; just re-launch

            if (!IsStopping)
            {
                if (!IsRunning)
                    Launch(routine);
                return;
            }

            Task.Run(async () =>
            {
                var deadline = DateTime.Now + StopWait + TimeSpan.FromSeconds(30);
                while (IsStopping && DateTime.Now < deadline)
                    await Task.Delay(100).ConfigureAwait(false);
                if (!IsRunning && !IsStopping)
                    Launch(routine);
            });
        }

        private void Launch(Func<CancellationToken, Task> routine)
        {
            var token = Source.Token;
            int generation = Interlocked.Increment(ref _generation);
            IsRunning = true;
            _routine = Task.Run(async () =>
            {
                while (true)
                {
                    Exception? failure = null;
                    try
                    {
                        await routine(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }

                    if (failure == null || token.IsCancellationRequested || generation != _generation)
                        break;

                    // IsRunning stays true while waiting to restart, so Stop can cancel the restart.
                    var wait = ReportFailure(failure);
                    try
                    {
                        await Task.Delay(wait, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    if (generation != _generation)
                        break;

                    LogUtil.LogInfo("Restarting the bot.", Bot.Connection.Name);
                    IsPaused = false;
                    routine = Bot.RunAsync;
                }

                if (generation == _generation && !IsStopping)
                    IsPaused = IsRunning = false;
            });
        }

        public void RefreshMap()
        {
            if (IsStopping)
                return;
            var token = Source.Token;
            Task.Run(async () =>
            {
                try
                {
                    await Bot.RefreshMapAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    LogUtil.LogError($"Map refresh failed: {ex.Message}", Bot.Connection.Name);
                }
            });
        }

        /// <returns>How long to wait before restarting</returns>
        private TimeSpan ReportFailure(Exception failure)
        {
            var ident = Bot.Connection.Name;
            var ae = failure as AggregateException ?? new AggregateException(failure);

            LogUtil.LogError("Bot has crashed!", ident);

            if (!string.IsNullOrEmpty(ae.Message))
                LogUtil.LogError("Aggregate message: " + ae.Message, ident);

            var st = ae.StackTrace;
            if (!string.IsNullOrEmpty(st))
                LogUtil.LogError("Aggregate stacktrace: " + st, ident);

            foreach (var e in ae.InnerExceptions)
            {
                if (!string.IsNullOrEmpty(e.Message))
                    LogUtil.LogError("Inner message: " + e.Message, ident);
                LogUtil.LogError("Inner stacktrace: " + e.StackTrace, ident);
            }

            // Half an hour without a crash means the next one starts the pauses over.
            if (DateTime.Now - _lastFailure > TimeSpan.FromMinutes(30))
                _failuresInARow = 0;
            _lastFailure = DateTime.Now;

            var wait = RestartDelays[Math.Min(_failuresInARow, RestartDelays.Length - 1)];
            _failuresInARow++;
            LogUtil.LogInfo($"Restarting in {wait.TotalSeconds:0} seconds (crash {_failuresInARow} in a row).", ident);

            // Restarting fixes a dropped connection, but not a Switch that is asleep or sitting
            // on the HOME menu. On 10/07 one crashed 30 times over three hours before anyone looked.
            if (ShouldAlertOwner(_failuresInARow))
            {
                var reason = ae.InnerExceptions.Count > 0 ? ae.InnerExceptions[0].Message.Trim() : ae.Message;
                EchoUtil.AlertOwner($"The raid bot for the Switch at {ident} has crashed {_failuresInARow} times in a row ({reason}). " +
                    "It keeps retrying on its own, but if this continues, check the Switch: is it asleep, on the HOME menu, or showing an error?");
            }
            return wait;
        }

        /// <summary>
        /// Stops the bot, waits for it to end, then starts it on a fresh connection.
        /// </summary>
        public void Restart()
        {
            Stop();
            Start();
        }

        public void Resume() => Start();
    }
}
