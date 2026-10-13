using FluentAssertions;
using SysBot.Base;
using SysBot.Pokemon;
using System.Linq;
using Xunit;

namespace SysBot.Tests
{
    public class CommandTests
    {
        [Fact]
        public void ButtonTestA()
        {
            var poke = SwitchCommand.Click(SwitchButton.A);
            var expect = new byte[] { 0x63, 0x6C, 0x69, 0x63, 0x6B, 0x20, 0x41, 0x0D, 0x0A }; // "click A\r\n"
            poke.SequenceEqual(expect).Should().BeTrue();
        }

        [Fact]
        public void ButtonTestX()
        {
            var poke = SwitchCommand.Click(SwitchButton.X);
            var expect = new byte[] { 0x63, 0x6C, 0x69, 0x63, 0x6B, 0x20, 0x58, 0x0D, 0x0A }; // "click X\r\n"
            poke.SequenceEqual(expect).Should().BeTrue();
        }
    }

    public class DecodeTests
    {
        [Fact]
        public void DecodeTest()
        {
            byte[] raw = { 0x30, 0x31, 0x30, 0x32, 0x30, 0x33, 0x30, 0x34, 0x30, 0x41, 0x30, 0x46 };
            byte[] expect = { 1, 2, 3, 4, 10, 15 };

            var convert = Decoder.ConvertHexByteStringToBytes(raw);
            convert.SequenceEqual(expect).Should().BeTrue();
        }

        // A screenshot that lost a digit used to decode with every byte after the gap shifted.
        [Fact]
        public void OddLengthHexIsRejected()
        {
            byte[] raw = { 0x30, 0x31, 0x30 };
            var convert = () => Decoder.ConvertHexByteStringToBytes(raw);
            convert.Should().Throw<System.ArgumentOutOfRangeException>();
        }
    }

    public class BotbaseVersionTests
    {
        // What getVersion answers in each release since 2.0, oldest first, read from each
        // project's main.c at its release tags.
        [Theory]
        [InlineData("2.0", "2.1", "2.11", "2.2", "2.3", "2.31", "2.4", "2.41", "2.5")] // sys-botbase
        [InlineData("2.2", "2.3", "2.4", "2.4.1", "2.42", "2.5")]                      // usb-botbase
        public void RealVersionsSortInReleaseOrder(params string[] released)
        {
            var parsed = released.Select(PokeRoutineExecutorBase.ParseBotbaseVersion).ToArray();
            parsed.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        }

        [Theory]
        [InlineData("2.31\n", false)]
        [InlineData("2.3\n\0", false)]
        [InlineData("2.4\n", true)]
        [InlineData("2.4.1\0", true)]
        [InlineData("2.41\n", true)]
        [InlineData("2.5\0\0\0\0\0\0", true)]
        [InlineData("", false)]
        public void MinimumVersionIsEnforced(string reply, bool supported) =>
            (PokeRoutineExecutorBase.ParseBotbaseVersion(reply) >= PokeRoutineExecutorBase.BotbaseVersion).Should().Be(supported);
    }

    public class UsbReplyTests
    {
        // usb-botbase sends a reply's length before the reply. A garbled length must not
        // become a 2 GB allocation or a negative one.
        [Theory]
        [InlineData(0, true)]
        [InlineData(8, true)]
        [InlineData(400_000, true)]
        [InlineData(-1, false)]
        [InlineData(int.MaxValue, false)]
        public void GarbledReplyLengthIsRejected(int size, bool plausible) =>
            SwitchUSB.IsPlausibleReplySize(size).Should().Be(plausible);
    }
}
