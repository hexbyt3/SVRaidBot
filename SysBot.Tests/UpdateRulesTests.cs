using FluentAssertions;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace SysBot.Tests
{
    public class UpdateRulesTests
    {
        private static (string?, string?, bool) Release(string tag, bool required, bool prerelease = false) =>
            (tag, required ? "## Recent Changes\n- Fix things\n\nRequired = Yes" : "## Recent Changes\n- Fix things", prerelease);

        [Fact]
        public void ARequiredReleaseStaysRequiredAfterALaterOneShips()
        {
            var releases = new[] { Release("v8.8.5", false), Release("v8.8.4", true), Release("v8.8.3", false) };

            UpdateRules.IsRequired(releases, "v8.8.3").Should().BeTrue();
            UpdateRules.IsRequired(releases, "v8.8.4").Should().BeFalse("the host already has the required release");
        }

        [Fact]
        public void OlderRequiredReleasesDoNotForceANewerHost()
        {
            UpdateRules.IsRequired([Release("v8.7.0", true), Release("v8.8.5", false)], "v8.8.3").Should().BeFalse();
        }

        [Fact]
        public void PrereleasesNeverForceAnUpdate()
        {
            UpdateRules.IsRequired([Release("v8.9.0", true, prerelease: true)], "v8.8.3").Should().BeFalse();
        }

        [Fact]
        public void OnlyANewerReleaseIsAnUpdate()
        {
            UpdateRules.IsNewer("v8.10.0", "v8.9.6").Should().BeTrue();
            UpdateRules.IsNewer("v8.9.6", "v8.10.0").Should().BeFalse("a newer build must never be offered the older release");
            UpdateRules.IsNewer("v8.10.0", "v8.10.0").Should().BeFalse();
            UpdateRules.IsNewer(null, "v8.10.0").Should().BeFalse();
        }

        // "Update all" picks the copies to update with IsNewer; a copy on a dev build ahead
        // of the release, or already on it, must be left alone.
        [Fact]
        public void UpdateAllOnlyTouchesCopiesThatAreBehind()
        {
            var copies = new[] { "v8.9.6", "v8.10.0", "v8.10.1", "Unknown" };
            Array.FindAll(copies, v => UpdateRules.IsNewer("v8.10.0", v)).Should().Equal("v8.9.6", "Unknown");
            UpdateRules.IsNewer("", "v8.9.6").Should().BeFalse("no release found means nothing to update");
        }

        [Fact]
        public void DownloadsMustMatchThePublishedDigest()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "SVRaidBot build");
                var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("SVRaidBot build"))).ToLowerInvariant();

                UpdateRules.MatchesDigest(path, digest).Should().BeTrue();
                UpdateRules.MatchesDigest(path, digest.ToUpperInvariant().Replace("SHA256", "sha256")).Should().BeTrue();
                UpdateRules.MatchesDigest(path, "sha256:" + new string('0', 64)).Should().BeFalse();
                UpdateRules.MatchesDigest(path, "sha256:not-hex").Should().BeFalse();
                UpdateRules.MatchesDigest(path, null).Should().BeTrue("releases from before GitHub added digests have none");
                UpdateRules.MatchesDigest(path, "").Should().BeTrue();
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TheUpdateCommandCarriesTheReleaseToOtherCopies()
        {
            var hex = new string('a', 64);
            var command = UpdateRules.UpdateCommand("v8.10.1", "sha256:" + hex, "SVRaidBot.exe");
            command.Should().Be($"UPDATE:v8.10.1:{hex}:SVRaidBot.exe");
            command.Split(':')[0].Should().Be("UPDATE", "older copies only read the first word");

            UpdateRules.TryReadUpdateCommand(command, out var tag, out var digest, out var asset).Should().BeTrue();
            tag.Should().Be("v8.10.1");
            digest.Should().Be("sha256:" + hex);
            asset.Should().Be("SVRaidBot.exe");

            UpdateRules.TryReadUpdateCommand("UPDATE:v8.10.1::SVRaidBot.exe", out _, out digest, out _).Should().BeTrue();
            digest.Should().BeNull();
            UpdateRules.TryReadUpdateCommand("UPDATE", out _, out _, out _).Should().BeFalse("a bare UPDATE means check for yourself");
            UpdateRules.TryReadUpdateCommand("UPDATE:latest", out _, out _, out _).Should().BeFalse();
            UpdateRules.TryReadUpdateCommand($"UPDATE:v8.10.1:{hex}:..\\evil.exe", out _, out _, out _).Should().BeFalse();
            UpdateRules.TryReadUpdateCommand("UPDATE:v8.10.1:abc:SVRaidBot.exe", out _, out _, out _).Should().BeFalse();
        }

        [Fact]
        public void VersionsCompareAsNumbers()
        {
            UpdateRules.IsRequired([Release("v8.10.0", true)], "v8.9.9").Should().BeTrue();
            UpdateRules.TryParseVersion("v8.8.4", out var v).Should().BeTrue();
            v.ToString().Should().Be("8.8.4");
            UpdateRules.TryParseVersion("latest", out _).Should().BeFalse();
        }
    }
}
