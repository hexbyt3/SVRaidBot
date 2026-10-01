using FluentAssertions;
using SysBot.Pokemon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static SysBot.Pokemon.RotatingRaidSettingsSV;

namespace SysBot.Tests
{
    public class RaidListTests
    {
        private static List<RotatingRaidParameters> Raids(params string[] titles) =>
            titles.Select(t => new RotatingRaidParameters { Title = t }).ToList();

        private static string Next(List<RotatingRaidParameters> list, int current) =>
            list[(current + 1) % list.Count].Title;

        [Fact]
        public void RemovingARaidBeforeTheCurrentOneKeepsTheRotation()
        {
            var list = Raids("A", "B", "C", "D");
            int current = 2; // C
            list[0].PendingRemoval = true;

            RaidListSync.RemovePending(list, ref current).Should().Be(1);

            list[current].Title.Should().Be("C");
            Next(list, current).Should().Be("D");
        }

        [Fact]
        public void RemovingTheCurrentRaidMovesOnToTheOneAfterIt()
        {
            var list = Raids("A", "B", "C", "D");
            int current = 1; // B
            list[1].PendingRemoval = true;

            RaidListSync.RemovePending(list, ref current);

            Next(list, current).Should().Be("C");
        }

        [Fact]
        public void RemovingTheFirstRaidWhileOnItWrapsToTheEnd()
        {
            var list = Raids("A", "B", "C");
            int current = 0;
            list[0].PendingRemoval = true;

            RaidListSync.RemovePending(list, ref current);

            current.Should().Be(1);
            Next(list, current).Should().Be("B");
        }

        [Fact]
        public void RemovingLaterRaidsLeavesTheIndexAlone()
        {
            var list = Raids("A", "B", "C", "D");
            int current = 1;
            list[2].PendingRemoval = true;
            list[3].PendingRemoval = true;

            RaidListSync.RemovePending(list, ref current).Should().Be(2);

            current.Should().Be(1);
            Next(list, current).Should().Be("A");
        }

        [Fact]
        public void RemovingEverythingLeavesIndexZero()
        {
            var list = Raids("A", "B");
            int current = 1;
            list.ForEach(r => r.PendingRemoval = true);

            RaidListSync.RemovePending(list, ref current);

            list.Should().BeEmpty();
            current.Should().Be(0);
        }

        [Fact]
        public async Task SnapshotsSurviveConcurrentInserts()
        {
            var list = Raids(Enumerable.Range(0, 50).Select(i => i.ToString()).ToArray());
            var writer = Task.Run(() =>
            {
                for (int i = 0; i < 20000; i++)
                    lock (RaidListSync.Gate) list.Insert(list.Count / 2, new RotatingRaidParameters { Title = "x" });
            });

            var reads = 0;
            while (!writer.IsCompleted)
                reads += RaidListSync.Snapshot(list).Count(r => r.Title == "x") >= 0 ? 1 : 0;

            await writer;
            reads.Should().BeGreaterThan(0);
            list.Should().HaveCount(20050);
        }

        [Fact]
        public void CheckingTheLimitDoesNotUseUpARequest()
        {
            var manager = new UserRequestManager();
            ulong user = (ulong)Random.Shared.NextInt64(1, long.MaxValue);

            for (int i = 0; i < 5; i++)
                manager.CanRequest(user, 2, 60, out _).Should().BeTrue();

            manager.RecordRequest(user, 2, 60);
            manager.CanRequest(user, 2, 60, out _).Should().BeTrue();
            manager.RecordRequest(user, 2, 60);
            manager.CanRequest(user, 2, 60, out var wait).Should().BeFalse();
            wait.Should().BeGreaterThan(TimeSpan.Zero);
        }

        [Fact]
        public void RequestCountsSurviveAnotherManagerWritingTheFile()
        {
            ulong first = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
            ulong second = first ^ 1;
            var a = new UserRequestManager();
            var b = new UserRequestManager();

            a.RecordRequest(first, 1, 60);
            b.RecordRequest(second, 1, 60);

            new UserRequestManager().CanRequest(first, 1, 60, out _).Should().BeFalse("b must not overwrite a's count");
        }
    }
}
