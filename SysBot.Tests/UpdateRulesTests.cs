using FluentAssertions;
using SysBot.Pokemon.SV.BotRaid.Helpers;
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
        public void VersionsCompareAsNumbers()
        {
            UpdateRules.IsRequired([Release("v8.10.0", true)], "v8.9.9").Should().BeTrue();
            UpdateRules.TryParseVersion("v8.8.4", out var v).Should().BeTrue();
            v.ToString().Should().Be("8.8.4");
            UpdateRules.TryParseVersion("latest", out _).Should().BeFalse();
        }
    }
}
