using System;
using System.Collections.Generic;
using System.Linq;

namespace SysBot.Pokemon.SV.BotRaid.Helpers
{
    public static class UpdateRules
    {
        public const string RequiredMarker = "Required = Yes";

        /// <summary>
        /// Required when any published release newer than the running version is
        /// marked with <see cref="RequiredMarker"/>, so a later unmarked release
        /// cannot let a host skip past a required one.
        /// </summary>
        public static bool IsRequired(IEnumerable<(string? Tag, string? Body, bool Prerelease)> releases, string currentVersion)
        {
            if (!TryParseVersion(currentVersion, out var current))
                return false;
            return releases.Any(r => !r.Prerelease
                && TryParseVersion(r.Tag, out var version)
                && version > current
                && r.Body?.Contains(RequiredMarker, StringComparison.OrdinalIgnoreCase) == true);
        }

        public static bool TryParseVersion(string? tag, out Version version)
        {
            version = new Version();
            if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var parsed))
                return false;
            version = parsed;
            return true;
        }
    }
}
