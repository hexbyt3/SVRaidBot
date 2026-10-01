using FluentAssertions;
using TeraRaidMapParent = PKHeX.Core.TeraRaidMapParent;
using SysBot.Pokemon.Helpers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace SysBot.Tests
{
    public class RaidSlotTests
    {
        private static long SeedPointerOffset(int index)
        {
            var method = typeof(RaidMemoryManager).GetMethod("DeterminePointer", BindingFlags.NonPublic | BindingFlags.Static)!;
            return ((List<long>)method.Invoke(null, [index])!)[3];
        }

        [Fact]
        public void EverySlotHasItsOwnNumber()
        {
            var seen = new HashSet<int>();
            foreach (var (map, slots) in new[] { (TeraRaidMapParent.Paldea, 72), (TeraRaidMapParent.Kitakami, 100), (TeraRaidMapParent.Blueberry, 80) })
            {
                for (int slot = 0; slot < slots; slot++)
                {
                    int index = RaidMemoryManager.ToGlobalIndex(map, slot);
                    seen.Add(index).Should().BeTrue($"{map} slot {slot} must not share a number");
                    RaidMemoryManager.FromGlobalIndex(index).Should().Be((map, slot));
                }
            }
            seen.Should().HaveCount(RaidMemoryManager.TotalSlots);
        }

        // The addresses the bot wrote to before the numbering fix, for every slot
        // that was not caught in the Kitakami/Blueberry overlap.
        [Fact]
        public void SeedAddressesMatchTheGameLayout()
        {
            for (int slot = 0; slot < 69; slot++)
                SeedPointerOffset(RaidMemoryManager.ToGlobalIndex(TeraRaidMapParent.Paldea, slot)).Should().Be(0x60 + slot * 0x20);
            for (int slot = 0; slot < 25; slot++)
                SeedPointerOffset(RaidMemoryManager.ToGlobalIndex(TeraRaidMapParent.Kitakami, slot)).Should().Be(0xCE8 + slot * 0x20);
            for (int slot = 0; slot < 24; slot++)
                SeedPointerOffset(RaidMemoryManager.ToGlobalIndex(TeraRaidMapParent.Blueberry, slot)).Should().Be(0x1968 + slot * 0x20);
        }

        [Fact]
        public void BlueberrySlotZeroNoLongerLandsInKitakami()
        {
            int kitakamiLast = RaidMemoryManager.ToGlobalIndex(TeraRaidMapParent.Kitakami, 24);
            int blueberryFirst = RaidMemoryManager.ToGlobalIndex(TeraRaidMapParent.Blueberry, 0);
            kitakamiLast.Should().NotBe(blueberryFirst);
            SeedPointerOffset(blueberryFirst).Should().Be(0x1968);
            SeedPointerOffset(kitakamiLast).Should().Be(0xCE8 + 24 * 0x20);
        }

        [Fact]
        public void SeedOffsetPointsAtTheSeedInsideEachRaidEntry()
        {
            Enumerable.Range(0, 5).Select(RaidMemoryManager.SeedOffset).Should().Equal(0x10, 0x30, 0x50, 0x70, 0x90);
        }
    }
}
