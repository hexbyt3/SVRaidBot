using FluentAssertions;
using SysBot.Base;
using SysBot.Pokemon;
using SysBot.Pokemon.SV;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SysBot.Tests
{
    public class GameStartTests
    {
        // A fake sys-botbase that records every command. getTitleID answers a bare newline
        // (no game running, as on 10/07) until the game is started by pressing A on the
        // first software tile, which takes 15 presses of DLEFT to reach.
        private sealed class FakeSwitch
        {
            public readonly ConcurrentQueue<string> Commands = new();
            public readonly TcpListener Listener = new(IPAddress.Loopback, 0);
            public bool GameRunning;
            public bool StartsFromFirstTile = true;
            public int TitleReplyDelay;
            private int _leftPresses;

            public FakeSwitch()
            {
                Listener.Start();
                _ = Task.Run(AcceptLoop);
            }

            public int Port => ((IPEndPoint)Listener.LocalEndpoint).Port;

            private async Task AcceptLoop()
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await Listener.AcceptTcpClientAsync(); }
                    catch { return; }
                    _ = Task.Run(() => Serve(client));
                }
            }

            private async Task Serve(TcpClient client)
            {
                using var c = client;
                var stream = c.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                while (await reader.ReadLineAsync() is { } line)
                {
                    line = line.Trim();
                    Commands.Enqueue(line);
                    if (line == "click DLEFT")
                        _leftPresses++;
                    else if (line == "click A" && StartsFromFirstTile && _leftPresses >= 15)
                        GameRunning = true;

                    if (line != "getTitleID")
                        continue;
                    if (TitleReplyDelay > 0)
                        await Task.Delay(TitleReplyDelay);
                    var reply = GameRunning ? PokeDataOffsetsSV.ScarletID + "\n" : "\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
                }
            }
        }

        private static RemoteControlBotSV Connect(FakeSwitch fake)
        {
            var cfg = new PokeBotState();
            cfg.Connection.Protocol = SwitchProtocol.WiFi;
            cfg.Connection.IP = "127.0.0.1";
            cfg.Connection.Port = fake.Port;
            var bot = new RemoteControlBotSV(cfg);
            bot.Connection.Connect();
            return bot;
        }

        private static PokeRaidHubConfig QuickLoadConfig()
        {
            var config = new PokeRaidHubConfig();
            // The game "loads" in a second instead of 15.
            config.Timings.RestartGameSettings.ExtraTimeLoadGame = -14_000;
            return config;
        }

        [Fact]
        public async Task NoGameRunningReadsAsNotRunning()
        {
            var fake = new FakeSwitch();
            var bot = Connect(fake);
            (await bot.IsGameRunning(CancellationToken.None)).Should().BeFalse();
            fake.GameRunning = true;
            (await bot.IsGameRunning(CancellationToken.None)).Should().BeTrue();
            bot.Connection.Disconnect();
            fake.Listener.Stop();
        }

        // Each press waits for the console's answer before the next goes out, so a stall
        // can delay the macro but can't bunch its presses together.
        [Fact]
        public async Task ConfirmedPressesWaitForTheConsole()
        {
            var fake = new FakeSwitch();
            var bot = Connect(fake);
            await bot.ClickConfirmed(SwitchButton.B, 0, CancellationToken.None);
            await bot.PressAndHoldConfirmed(SwitchButton.DDOWN, 50, 0, CancellationToken.None);
            fake.Commands.Should().Equal("click B", "getTitleID", "press DDOWN", "getTitleID", "release DDOWN", "getTitleID");
            bot.Connection.Disconnect();
            fake.Listener.Stop();
        }

        [Fact]
        public async Task SlowConfirmationStopsTheMacro()
        {
            var fake = new FakeSwitch { TitleReplyDelay = 2_000 };
            var bot = Connect(fake);
            var press = () => bot.ClickConfirmed(SwitchButton.A, 0, CancellationToken.None);
            await press.Should().ThrowAsync<PressNotConfirmedException>();
            bot.Connection.Disconnect();
            fake.Listener.Stop();
        }

        // 10/07: after the clock rollback the start presses opened something other than the
        // game, and the bot read empty memory for an hour before crash-looping for three more.
        [Fact]
        public async Task GameThatDidNotStartIsStartedFromTheFirstTile()
        {
            var fake = new FakeSwitch();
            var bot = Connect(fake);
            await bot.EnsureGameStarted(QuickLoadConfig(), CancellationToken.None);

            fake.GameRunning.Should().BeTrue();
            var presses = fake.Commands.Where(c => c.StartsWith("click ")).Select(c => c[6..]).ToList();
            presses.Take(5).Should().Equal("B", "HOME", "DDOWN", "DDOWN", "DUP");
            presses.Skip(5).Take(15).Should().AllBe("DLEFT");
            presses[20].Should().Be("A");
            bot.Connection.Disconnect();
            fake.Listener.Stop();
        }

        [Fact]
        public async Task RunningGameIsLeftAlone()
        {
            var fake = new FakeSwitch { GameRunning = true };
            var bot = Connect(fake);
            await bot.EnsureGameStarted(QuickLoadConfig(), CancellationToken.None);
            fake.Commands.Should().Equal("getTitleID");
            bot.Connection.Disconnect();
            fake.Listener.Stop();
        }

        [Fact]
        public async Task GameThatWillNotStartIsReported()
        {
            var fake = new FakeSwitch { StartsFromFirstTile = false };
            var bot = Connect(fake);
            var start = () => bot.EnsureGameStarted(QuickLoadConfig(), CancellationToken.None);
            await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*would not start*");
            bot.Connection.Disconnect();
            fake.Listener.Stop();
        }
    }
}
