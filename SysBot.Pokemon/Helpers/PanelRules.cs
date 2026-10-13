using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SysBot.Pokemon.SV.BotRaid.Helpers
{
    /// <summary>
    /// Request checks for the web control panel.
    /// </summary>
    public static class PanelRules
    {
        // Every request the panel sends is a few dozen bytes of JSON.
        public const int MaxBodyBytes = 4096;

        /// <summary>
        /// A page on some other site can point its own host name at this PC (DNS
        /// rebinding) and then read the panel as if it were that site. Its requests still
        /// carry that foreign name, so only IP addresses, localhost and this PC's own
        /// names are accepted. Anyone who can reach the panel by IP still can.
        /// </summary>
        public static bool IsAllowedHost(string? hostHeader, IEnumerable<string> machineNames)
        {
            if (string.IsNullOrWhiteSpace(hostHeader))
                return true; // HTTP/1.0 clients; a browser always sends Host

            if (!Uri.TryCreate($"http://{hostHeader.Trim()}/", UriKind.Absolute, out var uri))
                return false;
            if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
                return true;
            if (uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.Basic))
                return false;

            var name = uri.IdnHost.TrimEnd('.');
            return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || machineNames.Any(m => !string.IsNullOrWhiteSpace(m) && name.Equals(m.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Reads a request body of at most <paramref name="limit"/> bytes. Returns null when it is
        /// larger, whether the client said so up front or sent it without a length.
        /// </summary>
        public static async Task<string?> ReadBodyAsync(Stream body, long declaredLength, int limit = MaxBodyBytes)
        {
            if (declaredLength > limit)
                return null;

            var buffer = new byte[limit + 1];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await body.ReadAsync(buffer.AsMemory(total)).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
            }
            return total > limit ? null : Encoding.UTF8.GetString(buffer, 0, total);
        }

        public static bool IsIdle(string? status) =>
            string.Equals(status, "IDLE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "STOPPED", StringComparison.OrdinalIgnoreCase);
    }
}
