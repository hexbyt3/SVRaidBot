using FluentAssertions;
using SysBot.Pokemon.Discord.Helpers;
using System;
using Xunit;

namespace SysBot.Tests;

public class RaidCodeCooldownTests
{
    private static readonly DateTime Start = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SecondTapWithinTheWindowIsIgnored()
    {
        var cooldown = new RaidCodeCooldown();

        cooldown.TryStart(1, 100, Start).Should().BeTrue();
        cooldown.TryStart(1, 100, Start.AddSeconds(10)).Should().BeFalse();
        cooldown.TryStart(1, 100, Start + RaidCodeCooldown.Window).Should().BeTrue();
    }

    [Fact]
    public void OtherUsersAndOtherRaidsAreSeparate()
    {
        var cooldown = new RaidCodeCooldown();

        cooldown.TryStart(1, 100, Start).Should().BeTrue();
        cooldown.TryStart(1, 200, Start).Should().BeTrue();
        cooldown.TryStart(2, 100, Start).Should().BeTrue();
    }

    [Fact]
    public void PruningKeepsActiveCooldowns()
    {
        var cooldown = new RaidCodeCooldown();
        cooldown.TryStart(1, 1, Start).Should().BeTrue();
        for (ulong user = 2; user < 1100; user++)
            cooldown.TryStart(1, user, Start.AddMinutes(-5));

        cooldown.TryStart(1, 5000, Start.AddSeconds(1)).Should().BeTrue();
        cooldown.TryStart(1, 1, Start.AddSeconds(2)).Should().BeFalse();
    }
}
