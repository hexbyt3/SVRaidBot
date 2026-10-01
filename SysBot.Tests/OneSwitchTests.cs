using FluentAssertions;
using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon;
using System;
using Xunit;

namespace SysBot.Tests
{
    public class OneSwitchTests
    {
        private sealed class Runner(PokeRaidHubConfig config) : PokeBotRunner<PK9>(config, new BotFactory9SV());

        private static PokeBotState Switch(string ip, PokeRoutineType routine = PokeRoutineType.RotatingRaidBot)
        {
            var cfg = new PokeBotState { Connection = BotConfigUtil.GetConfig<SwitchConnectionConfig>(ip, 6000) };
            cfg.Initialize(routine);
            return cfg;
        }

        [Fact]
        public void AProgramHostsOneSwitch()
        {
            var runner = new Runner(new PokeRaidHubConfig());
            runner.Add(runner.CreateBotFromConfig(Switch("192.168.1.20")));

            var second = () => runner.Add(runner.CreateBotFromConfig(Switch("192.168.1.21")));
            second.Should().Throw<ArgumentException>().WithMessage("*one Switch per program*");

            var remote = () => runner.Add(runner.CreateBotFromConfig(Switch("192.168.1.22", PokeRoutineType.RemoteControl)));
            remote.Should().Throw<ArgumentException>();

            runner.Bots.Should().HaveCount(1);
            runner.Hub.Bots.Count.Should().Be(1);
        }

        [Fact]
        public void RemovingTheSwitchFreesTheSlot()
        {
            var runner = new Runner(new PokeRaidHubConfig());
            var first = Switch("192.168.1.20");
            runner.Add(runner.CreateBotFromConfig(first));
            runner.Remove(first, false).Should().BeTrue();

            runner.Add(runner.CreateBotFromConfig(Switch("192.168.1.21")));
            runner.Bots.Should().HaveCount(1);
        }
    }
}
