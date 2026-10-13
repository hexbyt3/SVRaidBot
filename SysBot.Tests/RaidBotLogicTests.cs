using FluentAssertions;
using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon;
using SysBot.Pokemon.SV.BotRaid;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using static SysBot.Pokemon.RotatingRaidSettingsSV;

namespace SysBot.Tests
{
    public class RaidBotLogicTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private static List<(GameProgress Progress, List<int> Stars)> Choices(MysteryRaidsSettings settings)
        {
            var method = typeof(RotatingRaidBotSV).GetMethod("MysteryRaidChoices", BindingFlags.NonPublic | BindingFlags.Static)!;
            return ((IEnumerable)method.Invoke(null, [settings])!).Cast<ValueTuple<GameProgress, List<int>>>()
                .Select(c => (c.Item1, c.Item2)).ToList();
        }

        [Fact]
        public void ALevelWithNoStarsLeftIsSkippedNotFatal()
        {
            var settings = new MysteryRaidsSettings();
            var s3 = settings.Unlocked3StarSettings;
            s3.Allow1StarRaids = s3.Allow2StarRaids = s3.Allow3StarRaids = false;

            var choices = Choices(settings);

            choices.Select(c => c.Progress).Should().Equal(GameProgress.Unlocked4Stars, GameProgress.Unlocked5Stars, GameProgress.Unlocked6Stars);
            choices.Single(c => c.Progress == GameProgress.Unlocked6Stars).Stars.Should().Equal(3, 4, 5, 6);
        }

        [Fact]
        public void MysteryRaidsHaveNothingToPickOnlyWhenEveryLevelIsEmpty()
        {
            var settings = new MysteryRaidsSettings();
            settings.Unlocked3StarSettings.Enabled = false;
            settings.Unlocked4StarSettings.Enabled = false;
            var s5 = settings.Unlocked5StarSettings;
            s5.Allow3StarRaids = s5.Allow4StarRaids = s5.Allow5StarRaids = false;
            var s6 = settings.Unlocked6StarSettings;
            s6.Allow3StarRaids = s6.Allow4StarRaids = s6.Allow5StarRaids = false;

            Choices(settings).Should().ContainSingle().Which.Stars.Should().Equal(6);

            s6.Allow6StarRaids = false;
            Choices(settings).Should().BeEmpty();
        }

        [Fact]
        public void UnreadableSeedsAreMarkedAndLeaveBetweenRaids()
        {
            var list = new[] { "0000717F", "NOTAHEX!", "3739A70B", "", "000091EC" }
                .Select(s => new RotatingRaidParameters { Seed = s, Title = s }).ToList();
            int current = 2;

            RaidListSync.MarkUnreadableSeeds(list).Select(r => r.Title).Should().Equal("NOTAHEX!", "");
            list.Should().HaveCount(5, "nothing leaves the list while a raid may be in progress");
            RaidListSync.MarkUnreadableSeeds(list).Should().BeEmpty("a raid is only reported once");

            RaidListSync.RemovePending(list, ref current).Should().Be(2);
            list.Select(r => r.Seed).Should().Equal("0000717F", "3739A70B", "000091EC");
            list[current].Seed.Should().Be("3739A70B");
        }

        private static RotatingRaidBotSV Bot(params string[] titles)
        {
            var config = new PokeRaidHubConfig();
            foreach (var title in titles)
                config.RotatingRaidSV.ActiveRaids.Add(new RotatingRaidParameters { Title = title, Seed = "0000717F" });
            var state = new PokeBotState { Connection = BotConfigUtil.GetConfig<SwitchConnectionConfig>("192.168.1.20", 6000) };
            state.Initialize(PokeRoutineType.RotatingRaidBot);
            return new RotatingRaidBotSV(state, new PokeRaidHub<PK9>(config));
        }

        [Fact]
        public void TheFirstRaidIsNotHostedTwiceInARow()
        {
            var bot = Bot("A", "B", "C");
            typeof(RotatingRaidBotSV).GetMethod("PickFirstRaid", Private)!.Invoke(bot, null);
            var current = (int)typeof(RotatingRaidBotSV).GetField("_currentRaidIndex", Private)!.GetValue(bot)!;
            var next = (int)typeof(RotatingRaidBotSV).GetMethod("SelectNextRaidIndex", Private)!.Invoke(bot, null)!;

            current.Should().Be(0);
            next.Should().Be(1);
        }

        [Fact]
        public void RaidBlockPointersEndInTheBlockOffset()
        {
            // RaidMemoryManager adjusts the last entry with [^1] and older code used [3]; both
            // only agree while each chain is four entries long.
            RaidCrawler.Core.Structures.Offsets.RaidBlockPointerBase.ToArray().Should().HaveCount(4);
            RaidCrawler.Core.Structures.Offsets.RaidBlockPointerKitakami.ToArray().Should().HaveCount(4);
            RaidCrawler.Core.Structures.Offsets.RaidBlockPointerBlueberry.ToArray().Should().HaveCount(4);
        }

        [Fact]
        public void ALimitOfZeroNeverBlocksOrCounts()
        {
            var manager = new UserRequestManager();
            ulong user = (ulong)Random.Shared.NextInt64(1, long.MaxValue);

            manager.RecordRequest(user, 1, 60);
            manager.CanRequest(user, 1, 60, out _).Should().BeFalse();
            manager.CanRequest(user, 0, 60, out _).Should().BeTrue("0 turns the limit off");

            ulong other = user ^ 2;
            manager.RecordRequest(other, 0, 60);
            manager.CanRequest(other, 1, 60, out _).Should().BeTrue("nothing was counted while the limit was off");
        }
    }
}
