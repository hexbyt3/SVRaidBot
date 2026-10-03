using FluentAssertions;
using SysBot.Base;
using SysBot.Pokemon;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SysBot.Tests
{
    public class BotSourceTests
    {
        // Crashes on its first run, then runs until stopped.
        private sealed class CrashOnceBot(PokeBotState cfg) : RoutineExecutor<PokeBotState>(cfg)
        {
            public int Runs;
            public int Running;
            public int MostAtOnce;

            public override async Task MainLoop(CancellationToken token)
            {
                if (Interlocked.Increment(ref Runs) == 1)
                    throw new InvalidOperationException("boom");
                int now = Interlocked.Increment(ref Running);
                if (now > MostAtOnce)
                    MostAtOnce = now;
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    // Shut down slowly, like a routine stuck in a blocking connect.
                    await Task.Delay(1500, CancellationToken.None);
                    Interlocked.Decrement(ref Running);
                }
            }

            public override string GetSummary() => "test";
            public override Task InitialStartup(CancellationToken token) => Task.CompletedTask;
            public override void SoftStop() { }
            public override Task HardStop() => Task.CompletedTask;
            public override Task RebootAndStop(CancellationToken token) => Task.CompletedTask;
            public override Task RefreshMap(CancellationToken token) => Task.CompletedTask;
        }

        private static (BotSource<PokeBotState> Source, CrashOnceBot Bot, TcpListener Switch) Create()
        {
            // Accepts any number of connections and never answers; the fake bot sends nothing.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var cfg = new PokeBotState();
            cfg.Connection.Protocol = SwitchProtocol.WiFi;
            cfg.Connection.IP = "127.0.0.1";
            cfg.Connection.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var bot = new CrashOnceBot(cfg);
            return (new BotSource<PokeBotState>(bot), bot, listener);
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.Now.AddSeconds(10);
            while (!condition() && DateTime.Now < deadline)
                await Task.Delay(20);
        }

        // On 10/02 the bot crashed at 08:34 and sat dead until it was started by hand:
        // the restart ran while IsRunning was still true and did nothing.
        [Fact]
        public async Task CrashedBotRestartsItself()
        {
            var (source, bot, listener) = Create();
            source.Start();

            await WaitFor(() => bot.Running == 1);
            bot.Runs.Should().Be(2);
            source.IsRunning.Should().BeTrue();

            source.Stop();
            await WaitFor(() => !source.IsRunning);
            listener.Stop();
        }

        [Fact]
        public async Task StopThenStartNeverRunsTwoLoops()
        {
            var (source, bot, listener) = Create();
            source.Start();
            await WaitFor(() => bot.Running == 1);

            source.Stop();
            source.Start();
            await WaitFor(() => bot.Runs == 3);
            await WaitFor(() => bot.Running == 1);

            bot.Runs.Should().Be(3, "a Start sent during a Stop must still happen");
            bot.MostAtOnce.Should().Be(1);
            source.Stop();
            await WaitFor(() => !source.IsRunning);
            listener.Stop();
        }
    }
}
