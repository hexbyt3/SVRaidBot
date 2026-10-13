using FluentAssertions;
using SysBot.Pokemon;
using SysBot.Pokemon.Discord;
using SysBot.Pokemon.Discord.Helpers;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace SysBot.Tests;

public class RaidRequestRulesTests
{
    [Theory]
    [InlineData(0, 6)]
    [InlineData(8, 6)]
    [InlineData(-1, 6)]
    [InlineData(1, 6)]
    [InlineData(2, 6)]
    [InlineData(5, 0)]
    [InlineData(5, 9)]
    [InlineData(6, 5)]
    [InlineData(4, 3)]
    public void RejectsLevelsThatCantBeRaided(int level, int story)
    {
        RaidRequestRules.CheckLevel(level, story).Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(7, 6)]
    public void AcceptsLevelsTheStoryUnlocks(int level, int story)
    {
        RaidRequestRules.CheckLevel(level, story).Should().BeNull();
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void RequiredProgressNeverThrows(int level)
    {
        RaidRequestRules.RequiredProgress(level).Should().EndWith("Unlocked Progress");
    }

    [Fact]
    public void LongRaidListsSplitIntoFieldsDiscordAccepts()
    {
        var lines = Enumerable.Range(0, 200).Select(i => $"{i}.) Somebody's Requested Raid (Chien-Pao Event Raid) - 0A1B2C3D - Status: Active").ToList();

        var fields = RaidRequestRules.PackIntoFields(lines);

        fields.Should().OnlyContain(f => f.Length <= RaidRequestRules.FieldLimit);
        string.Concat(fields).Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Should().Equal(lines);
        RaidRequestRules.FitsInOneEmbed(fields, i => $"Raid List - Part {i + 1}", "Raid List").Should().BeFalse();
    }

    [Fact]
    public void ShortRaidListsStayInOneEmbed()
    {
        var lines = Enumerable.Range(0, 20).Select(i => $"{i}.) Pikachu - 0000ABCD - Status: Active");

        var fields = RaidRequestRules.PackIntoFields(lines);

        fields.Should().ContainSingle();
        RaidRequestRules.FitsInOneEmbed(fields, i => $"Raid List - Part {i + 1}", "Raid List").Should().BeTrue();
    }

    [Fact]
    public void AnOverlongTitleIsCutToOneField()
    {
        var fields = RaidRequestRules.PackIntoFields([new string('x', 3000), "next"]);

        fields.Should().HaveCount(2);
        fields[0].Length.Should().BeLessThanOrEqualTo(RaidRequestRules.FieldLimit);
    }

    [Fact]
    public void EmptyRaidRoleListLetsEveryoneRequestEvenFromAnOldConfig()
    {
        // Configs written before the role was enforced saved it with AllowIfEmpty off.
        const string json = """{ "RoleRaidRequest": { "List": [], "AllowIfEmpty": false } }""";
        var settings = JsonSerializer.Deserialize<DiscordSettings>(json)!;
        var manager = new DiscordManager(settings);

        manager.AllowsEveryone(nameof(DiscordManager.RolesRaid)).Should().BeTrue();
        manager.GetHasRoleAccess(nameof(DiscordManager.RolesRaid), []).Should().BeTrue();
    }

    [Fact]
    public void RaidRolesLimitRequestsOnceSet()
    {
        var settings = new DiscordSettings();
        settings.RoleRaidRequest.AddIfNew([new RemoteControlAccess { Name = "Raiders" }]);
        var manager = new DiscordManager(settings);

        manager.AllowsEveryone(nameof(DiscordManager.RolesRaid)).Should().BeFalse();
        manager.GetHasRoleAccess(nameof(DiscordManager.RolesRaid), ["Members"]).Should().BeFalse();
        manager.GetHasRoleAccess(nameof(DiscordManager.RolesRaid), ["Raiders"]).Should().BeTrue();
    }

    [Fact]
    public void EmptyRemoteControlRolesStillMeanNobody()
    {
        var manager = new DiscordManager(new DiscordSettings());

        manager.AllowsEveryone(nameof(DiscordManager.RolesRemoteControl)).Should().BeFalse();
    }
}
