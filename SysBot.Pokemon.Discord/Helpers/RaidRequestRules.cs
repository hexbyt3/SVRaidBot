using SysBot.Pokemon.SV.BotRaid;
using System;
using System.Collections.Generic;
using System.Text;

namespace SysBot.Pokemon.Discord.Helpers;

/// <summary>
/// Checks and formatting shared by the raid commands, kept free of Discord state so they can be tested.
/// </summary>
public static class RaidRequestRules
{
    public const int FieldLimit = 1024;
    public const int EmbedLimit = 6000;
    public const int FieldCountLimit = 25;

    /// <summary>The reply for a level and story progress that can't be raided, or null when they fit.</summary>
    public static string? CheckLevel(int level, int storyProgressLevel)
    {
        if (level is < 1 or > 7)
            return "Invalid raid level. Please enter a level between 1 and 7.";
        if (storyProgressLevel is < 1 or > 6)
            return "Invalid story progress level. Please enter a value between 1 and 6.";
        if (level < 3)
            return $"{level}★ raids can't be hosted. Please pick a raid from 3★ to 7★.";
        if (!WebRaidRules.StarsFitProgress(level, storyProgressLevel))
        {
            return $"The selected raid difficulty level ({level}★) is not compatible with your current story progress. " +
                   $"To access {level}★ raids, you need to have at least {RequiredProgress(level)} in the game's story.";
        }
        return null;
    }

    public static string RequiredProgress(int level) => level switch
    {
        >= 6 => "6☆ Unlocked Progress",
        5 => "5☆ Unlocked Progress",
        4 => "4☆ Unlocked Progress",
        _ => "3☆ Unlocked Progress",
    };

    public static GameProgress ToGameProgress(int storyProgressLevel) => storyProgressLevel switch
    {
        5 => GameProgress.Unlocked5Stars,
        4 => GameProgress.Unlocked4Stars,
        3 => GameProgress.Unlocked3Stars,
        2 or 1 => GameProgress.UnlockedTeraRaids,
        _ => GameProgress.Unlocked6Stars,
    };

    /// <summary>
    /// Packs lines into embed field values of at most <see cref="FieldLimit"/> characters.
    /// A line longer than a field is cut short.
    /// </summary>
    public static List<string> PackIntoFields(IEnumerable<string> lines)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Length > FieldLimit - 1 ? raw[..(FieldLimit - 2)] + "…" : raw;
            if (current.Length + line.Length + 1 > FieldLimit)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            current.Append(line).Append('\n');
        }
        if (current.Length > 0)
            fields.Add(current.ToString());
        return fields;
    }

    /// <summary>Whether the fields plus their names and the embed title stay inside Discord's embed limits.</summary>
    public static bool FitsInOneEmbed(IReadOnlyList<string> fields, Func<int, string> fieldName, string title)
    {
        if (fields.Count > FieldCountLimit)
            return false;
        int total = title.Length;
        for (int i = 0; i < fields.Count; i++)
            total += fields[i].Length + fieldName(i).Length;
        return total <= EmbedLimit;
    }
}
