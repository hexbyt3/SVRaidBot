using FluentAssertions;
using PKHeX.Core;
using pkNX.Structures.FlatBuffers;
using RaidCrawler.Core.Structures;
using SysBot.Pokemon;
using SysBot.Pokemon.SV.BotRaid;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace SysBot.Tests
{
    /// <summary>
    /// Builds raids from seeds the way the bot does, with no Switch attached.
    /// RaidCrawler is a prebuilt DLL compiled against one PKHeX; if a PKHeX
    /// update removes something it calls, these fail here instead of on every
    /// host's bot the moment it starts.
    /// </summary>
    [Collection("RaidContainer")]
    public class RaidDataTests
    {
        private static void UseGame(string game)
        {
            var container = new RaidContainer(game);
            container.SetGame(game);
            typeof(RotatingRaidBotSV).GetProperty(nameof(RotatingRaidBotSV.Container), BindingFlags.Public | BindingFlags.Static)!
                .SetValue(null, container);
        }

        private static PK9 Build(string game, string seed, int content, TeraRaidMapParent map)
        {
            UseGame(game);
            var (pk, _) = RotatingRaidBotSV.RaidInfoCommand(seed, content, map, 6, -1, [], false, new List<RotatingRaidSettingsSV.MoveTypeEmojiInfo>(), 0, false, 2);
            return pk;
        }

        [Theory]
        [InlineData("Scarlet", "3739A70B", 0, TeraRaidMapParent.Paldea, Species.Polteageist, true)]
        [InlineData("Violet", "3739A70B", 0, TeraRaidMapParent.Paldea, Species.Drakloak, true)]
        [InlineData("Scarlet", "3739A70B", 1, TeraRaidMapParent.Paldea, Species.Farigiraf, true)]
        [InlineData("Scarlet", "DEADBEEF", 0, TeraRaidMapParent.Kitakami, Species.Ludicolo, false)]
        [InlineData("Violet", "DEADBEEF", 1, TeraRaidMapParent.Paldea, Species.Avalugg, false)]
        public void SeedsMakeTheSameRaidsAsTheLastRelease(string game, string seed, int content, TeraRaidMapParent map, Species expected, bool shiny)
        {
            var pk = Build(game, seed, content, map);
            ((Species)pk.Species).Should().Be(expected);
            pk.IsShiny.Should().Be(shiny);
        }

        [Fact]
        public void TheSameSeedIsADifferentPokemonInEachGame()
        {
            var scarlet = (Species)Build("Scarlet", "3739A70B", 0, TeraRaidMapParent.Paldea).Species;
            var violet = (Species)Build("Violet", "3739A70B", 0, TeraRaidMapParent.Paldea).Species;

            WebRaidRules.SameSpecies("Polteageist", scarlet.ToString()).Should().BeTrue();
            WebRaidRules.SameSpecies("Polteageist", violet.ToString()).Should().BeFalse();
        }
    }
}
