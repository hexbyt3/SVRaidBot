using Discord;
using SysBot.Base;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord.Helpers;

/// <summary>
/// Collects log lines for one Discord channel and posts them every couple of seconds as a few
/// messages under Discord's 2000-character cap, instead of one message per line.
/// </summary>
public sealed class LogBatcher : IDisposable
{
    public const int MaxMessageLength = 1900;
    public const int MaxMessagesPerFlush = 5;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly IMessageChannel _channel;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly Timer _timer;
    private int _flushing;

    public LogBatcher(IMessageChannel channel)
    {
        _channel = channel;
        _timer = new Timer(_ => _ = FlushAsync(), null, Interval, Interval);
    }

    public void Add(string line) => _pending.Enqueue(line);

    private async Task FlushAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1)
            return;
        try
        {
            var lines = new List<string>();
            while (_pending.TryDequeue(out var line))
                lines.Add(line);

            foreach (var message in Pack(lines, MaxMessageLength, MaxMessagesPerFlush))
                await _channel.SendMessageAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // LogSafe writes to the file only, so a broken log channel can't feed itself.
            LogUtil.LogSafe(ex, nameof(LogBatcher));
        }
        finally
        {
            Volatile.Write(ref _flushing, 0);
        }
    }

    /// <summary>
    /// Joins lines into messages of at most <paramref name="maxLength"/> characters. Long lines are cut,
    /// and past <paramref name="maxMessages"/> the rest is summarized so a log flood can't queue forever.
    /// </summary>
    public static List<string> Pack(IReadOnlyList<string> lines, int maxLength, int maxMessages)
    {
        var messages = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Length > maxLength ? lines[i][..(maxLength - 1)] + "…" : lines[i];
            if (current.Length > 0 && current.Length + 1 + line.Length > maxLength)
            {
                messages.Add(current.ToString());
                current.Clear();
                if (messages.Count == maxMessages - 1)
                {
                    int skipped = lines.Count - i;
                    var note = $"> ...{skipped} more log line{(skipped == 1 ? "" : "s")} skipped; see the log file.";
                    if (skipped == 1 && line.Length <= maxLength)
                        note = line;
                    messages.Add(note);
                    return messages;
                }
            }
            if (current.Length > 0)
                current.Append('\n');
            current.Append(line);
        }
        if (current.Length > 0)
            messages.Add(current.ToString());
        return messages;
    }

    public void Dispose() => _timer.Dispose();
}
