using FluentAssertions;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using Xunit;

namespace SysBot.Tests
{
    public class UpdateScriptTests
    {
        // 10/07: several copies took a required update at once, all through one shared
        // UpdateSVRaidBot.bat, and one folder's program opened several times.
        [Fact]
        public void EachCopyGetsItsOwnScript()
        {
            UpdateScript.PathFor(1234).Should().NotBe(UpdateScript.PathFor(5678));
            UpdateScript.PathFor(1234).Should().EndWith("UpdateSVRaidBot_1234.bat");
        }

        [Fact]
        public void ScriptWaitsForThatCopyToExitAndStartsItInItsOwnFolder()
        {
            var script = UpdateScript.Build(1234, @"C:\Bots\Switch 2\SVRaidBot.exe", @"C:\Temp\new.exe");
            script.Should().Contain(@"""%SystemRoot%\System32\tasklist.exe"" /FI ""PID eq 1234""");
            script.Should().NotContain("timeout /t");
            script.Should().Contain(@"start """" /D ""C:\Bots\Switch 2"" ""C:\Bots\Switch 2\SVRaidBot.exe""");
            script.Should().Contain(@"move /y ""C:\Temp\new.exe"" ""C:\Bots\Switch 2\SVRaidBot.exe""");
            // Gives up without starting anything if the old copy never exits.
            script.IndexOf("goto done").Should().BeLessThan(script.IndexOf("start \"\""));
            script.Should().Contain("\r\n");
        }
    }
}
