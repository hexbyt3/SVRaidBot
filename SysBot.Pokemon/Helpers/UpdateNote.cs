using System;
using System.Globalization;
using System.IO;

namespace SysBot.Pokemon.SV.BotRaid.Helpers
{
    /// <summary>
    /// What a copy leaves for itself when it updates on its own: which release it tried,
    /// when, and whether it was hosting and should start again afterward.
    /// </summary>
    public sealed record UpdateNote(string Tag, DateTime TriedUtc, bool Resume)
    {
        // A swap that fails starts the old program again, which would download the same
        // release over and over without a pause between tries.
        public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

        // Long enough for the swap and a slow start; a program opened by hand the next
        // day must not start hosting by itself.
        public static readonly TimeSpan ResumeWithin = TimeSpan.FromMinutes(30);

        public bool BlocksRetry(string tag, DateTime utcNow) =>
            string.Equals(Tag, tag, StringComparison.OrdinalIgnoreCase) && Age(utcNow) < RetryAfter;

        public bool ShouldResume(DateTime utcNow) => Resume && Age(utcNow) < ResumeWithin;

        // A clock set back makes the age negative; treat that note as old.
        private TimeSpan Age(DateTime utcNow) => utcNow < TriedUtc ? TimeSpan.MaxValue : utcNow - TriedUtc;

        public override string ToString() => $"{Tag}|{TriedUtc.ToString("O", CultureInfo.InvariantCulture)}|{(Resume ? 1 : 0)}";

        public static UpdateNote? Parse(string? text)
        {
            var parts = text?.Trim().Split('|');
            if (parts is not { Length: 3 } || parts[0].Length == 0 || parts[2] is not ("0" or "1")
                || !DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var tried))
                return null;
            return new UpdateNote(parts[0], tried.ToUniversalTime(), parts[2] == "1");
        }

        public static UpdateNote? Read(string path)
        {
            try
            {
                return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Write(string path) => File.WriteAllText(path, ToString());
    }
}
