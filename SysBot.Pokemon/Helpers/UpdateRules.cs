using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

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

        /// <summary>
        /// Only a strictly newer release is an update; a plain inequality would offer to
        /// "update" a newer build back down to whatever releases/latest points at.
        /// </summary>
        public static bool IsNewer(string? remoteTag, string currentVersion)
        {
            if (TryParseVersion(remoteTag, out var remote) && TryParseVersion(currentVersion, out var current))
                return remote > current;
            return !string.IsNullOrWhiteSpace(remoteTag) && !string.Equals(remoteTag.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryParseVersion(string? tag, out Version version)
        {
            version = new Version();
            if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var parsed))
                return false;
            version = parsed;
            return true;
        }

        public const string DefaultAssetName = "SVRaidBot.exe";
        private const string Sha256Prefix = "sha256:";

        /// <summary>
        /// Checks a download against the digest GitHub publishes for each release asset
        /// ("sha256:&lt;hex&gt;"). Releases from before GitHub added digests have none, and
        /// those still install as they always did.
        /// </summary>
        public static bool MatchesDigest(string filePath, string? digest)
        {
            if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
                return true;

            var expected = digest[Sha256Prefix.Length..].Trim();
            if (!IsSha256Hex(expected))
                return false;

            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSha256Hex(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);

        private static readonly Regex AssetName = new(@"^[A-Za-z0-9._-]+\.exe$", RegexOptions.IgnoreCase);

        /// <summary>
        /// The master copy already knows the release, so it hands the tag, checksum and file
        /// name to the other copies and they skip their own GitHub lookups, which all count
        /// against one hourly limit per PC. Older copies only read the word UPDATE and check
        /// for themselves.
        /// </summary>
        public static string UpdateCommand(string tag, string? digest, string? assetName)
        {
            var hex = digest != null && digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase) ? digest[Sha256Prefix.Length..] : "";
            return $"UPDATE:{tag}:{hex}:{assetName ?? DefaultAssetName}";
        }

        public static bool TryReadUpdateCommand(string command, out string tag, out string? digest, out string assetName)
        {
            tag = "";
            digest = null;
            assetName = DefaultAssetName;

            var parts = command.Split(':');
            if (parts.Length < 2 || !parts[0].Equals("UPDATE", StringComparison.OrdinalIgnoreCase) || !TryParseVersion(parts[1], out _))
                return false;
            if (parts.Length > 2 && parts[2].Length > 0)
            {
                if (!IsSha256Hex(parts[2]))
                    return false;
                digest = Sha256Prefix + parts[2];
            }
            if (parts.Length > 3 && parts[3].Length > 0)
            {
                if (!AssetName.IsMatch(parts[3]))
                    return false;
                assetName = parts[3];
            }
            tag = parts[1];
            return true;
        }
    }
}
