using Discord;
using PKHeX.Core;
using System.Threading.Tasks;
using Color = Discord.Color;

namespace SysBot.Pokemon.Discord.Helpers;

public static class RPEmbed
{
    public static async Task<Embed> PokeEmbedAsync(PKM pk, string username)
    {
        var strings = GameInfo.GetStrings(GameLanguage.DefaultLanguage);
        var english = GameInfo.GetStrings("en");
        var items = strings.GetItemStrings(pk.Context, (GameVersion)pk.Version);
        var formName = ShowdownParsing.GetStringFromForm(pk.Form, strings, pk.Species, pk.Context);
        var itemName = items[pk.HeldItem];

        var imageUrl = RaidExtensions<PK9>.PokeImg(pk, false, false);
        (int R, int G, int B) = await RaidExtensions<PK9>.GetDominantColorAsync(imageUrl);

        var embed = new EmbedBuilder
        {
            Color = new Color(R, G, B),
            ThumbnailUrl = imageUrl,
        };

        embed.AddField(x =>
        {
            x.Name = $"{Format.Bold($"{english.Species[pk.Species]}{(pk.Form != 0 ? $"-{formName}" : "")} {(pk.HeldItem != 0 ? $"➜ {itemName}" : "")}")}";
            x.Value = $"{Format.Bold($"Ability:")} {english.Ability[pk.Ability]}\n{Format.Bold("Level:")} {pk.CurrentLevel}\n{Format.Bold("Nature:")} {(Nature)pk.Nature}\n{Format.Bold("IVs:")} {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}\n{Format.Bold("Move:")} {english.Move[pk.Move1]}";
            x.IsInline = true;
        });

        embed.WithFooter(footer =>
        {
            footer.Text = $"{username}'s Raid Battler";
        });

        embed.WithAuthor(auth =>
        {
            auth.Name = "Pokémon Updated!";
            auth.Url = "https://notpaldea.net";
        });

        return embed.Build();
    }
}