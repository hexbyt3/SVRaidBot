using FluentAssertions;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace SysBot.Tests
{
    public class PanelRulesTests
    {
        private static readonly string[] MachineNames = ["RAIDPC", "raidpc.home.lan", "RAIDPC.local"];

        [Theory]
        [InlineData("localhost:9090")]
        [InlineData("LOCALHOST")]
        [InlineData("127.0.0.1:9090")]
        [InlineData("192.168.1.44:9090")]
        [InlineData("[::1]:9090")]
        [InlineData("[fe80::1]")]
        [InlineData("raidpc:9090")]
        [InlineData("RaidPC.home.lan.:9090")]
        [InlineData("raidpc.local")]
        [InlineData(null)]
        public void LocalNamesAndAnyIpAreAllowed(string? host)
        {
            PanelRules.IsAllowedHost(host, MachineNames).Should().BeTrue();
        }

        // A rebinding attack arrives under the attacker's own name.
        [Theory]
        [InlineData("evil.example.com:9090")]
        [InlineData("raidpc.evil.example.com")]
        [InlineData("localhost.evil.example.com")]
        [InlineData("not a host")]
        public void ForeignNamesAreRejected(string host)
        {
            PanelRules.IsAllowedHost(host, MachineNames).Should().BeFalse();
        }

        [Fact]
        public async Task SmallBodiesAreRead()
        {
            var json = """{"command":"start"}""";
            var body = await PanelRules.ReadBodyAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), json.Length);
            body.Should().Be(json);
        }

        [Fact]
        public async Task OversizedBodiesAreRefusedWithOrWithoutALength()
        {
            var big = new byte[PanelRules.MaxBodyBytes + 1];
            (await PanelRules.ReadBodyAsync(new MemoryStream(big), big.Length)).Should().BeNull();
            (await PanelRules.ReadBodyAsync(new MemoryStream(big), -1)).Should().BeNull("a chunked body has no declared length");
            (await PanelRules.ReadBodyAsync(new MemoryStream(new byte[10]), 1_000_000_000)).Should().BeNull("the declared length is refused before reading");
            (await PanelRules.ReadBodyAsync(new MemoryStream(new byte[PanelRules.MaxBodyBytes]), -1)).Should().NotBeNull();
        }

        [Theory]
        [InlineData("IDLE", true)]
        [InlineData("stopped", true)]
        [InlineData("RotatingRaidBot", false)]
        [InlineData("REBOOTING", false)]
        [InlineData(null, false)]
        public void OnlyIdleAndStoppedCountAsIdle(string? status, bool idle)
        {
            PanelRules.IsIdle(status).Should().Be(idle);
        }
    }
}
