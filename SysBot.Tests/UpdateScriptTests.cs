using FluentAssertions;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            var script = UpdateScript.Build(1234);
            script.Should().Contain(@"""%SystemRoot%\System32\tasklist.exe"" /FI ""PID eq 1234""");
            script.Should().NotContain("timeout /t");
            script.Should().Contain(@"start """" /D ""%SVRAIDBOT_FOLDER%"" ""%SVRAIDBOT_EXE%""");
            script.Should().Contain(@"move /y ""%SVRAIDBOT_DOWNLOAD%"" ""%SVRAIDBOT_EXE%""");
            // Gives up without starting anything if the old copy never exits.
            script.IndexOf("goto done").Should().BeLessThan(script.IndexOf("start \"\""));
            script.Should().Contain("\r\n");
        }

        // cmd reads a batch file in the console code page, so any path written into the
        // file broke on a folder like "Pokémon"; the script itself must stay plain ASCII.
        [Fact]
        public void ScriptHoldsNoPathsAndOnlyAscii()
        {
            var script = UpdateScript.Build(1234);
            script.All(c => c < 128).Should().BeTrue();
            script.Should().NotContain(@":\");
            script.Should().Contain("DisableDelayedExpansion", "a ! in a folder name must not be expanded either");
        }

        [Fact]
        public void PathsTravelInTheEnvironmentAndNotOnTheCommandLine()
        {
            var info = UpdateScript.StartInfo(@"C:\Users\Zoë\AppData\Local\Temp\UpdateSVRaidBot_1234.bat",
                @"C:\Bots\Pokémon 100%\SVRaidBot.exe", @"C:\Users\Zoë\AppData\Local\Temp\new.exe");

            info.UseShellExecute.Should().BeFalse("the environment is only passed when the shell is not used");
            info.CreateNoWindow.Should().BeTrue();
            info.FileName.Should().EndWith(@"\System32\cmd.exe");
            info.Arguments.Should().Be(@"/d /c "".\UpdateSVRaidBot_1234.bat""");
            info.WorkingDirectory.Should().Be(@"C:\Users\Zoë\AppData\Local\Temp");
            info.Environment[UpdateScript.ExeVariable].Should().Be(@"C:\Bots\Pokémon 100%\SVRaidBot.exe");
            info.Environment[UpdateScript.DownloadVariable].Should().Be(@"C:\Users\Zoë\AppData\Local\Temp\new.exe");
            info.Environment[UpdateScript.FolderVariable].Should().Be(@"C:\Bots\Pokémon 100%");
        }

        // Runs the real script against stand-in programs in a folder whose name has the
        // characters that broke the old one.
        [Fact]
        public void SwapsTheProgramInAFolderWithAccentsAndPercentSigns()
        {
            if (!OperatingSystem.IsWindows())
                return;

            var root = Path.Combine(Path.GetTempPath(), $"SVRaidBot test é 100%path% {Guid.NewGuid():N}");
            var botFolder = Path.Combine(root, "Pokémon %USERNAME%");
            Directory.CreateDirectory(botFolder);
            try
            {
                var exePath = Path.Combine(botFolder, "SVRaidBot.exe");
                var downloadedPath = Path.Combine(root, "downloaded é.exe");
                var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                File.Copy(Path.Combine(system32, "PING.EXE"), exePath);
                // rundll32 with no arguments exits at once without a window.
                File.Copy(Path.Combine(system32, "rundll32.exe"), downloadedPath);
                var oldBytes = File.ReadAllBytes(exePath);
                var newBytes = File.ReadAllBytes(downloadedPath);

                // Stands in for the copy that is still closing; the script must wait for it.
                using var closing = Process.Start(new ProcessStartInfo(Path.Combine(system32, "PING.EXE"), "-n 3 127.0.0.1")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                })!;

                var scriptPath = Path.Combine(root, $"UpdateSVRaidBot_{closing.Id}.bat");
                File.WriteAllText(scriptPath, UpdateScript.Build(closing.Id));
                var info = UpdateScript.StartInfo(scriptPath, exePath, downloadedPath);
                info.RedirectStandardOutput = true;
                using var script = Process.Start(info)!;
                script.WaitForExit(60_000).Should().BeTrue();

                closing.HasExited.Should().BeTrue("the swap may only happen after the old copy exits");
                File.ReadAllBytes(exePath).Should().Equal(newBytes);
                File.ReadAllBytes(exePath + ".backup").Should().Equal(oldBytes);
                File.Exists(downloadedPath).Should().BeFalse();
                File.Exists(scriptPath).Should().BeFalse("the script deletes itself");
            }
            finally
            {
                for (int attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
                {
                    try { Directory.Delete(root, true); }
                    catch (IOException) { System.Threading.Thread.Sleep(500); }
                    catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(500); }
                }
            }
        }
    }
}
