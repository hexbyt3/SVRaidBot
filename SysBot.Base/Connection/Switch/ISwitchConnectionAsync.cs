using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Base;

/// <summary>
/// Exposes the available interactions for asynchronous communications with a Nintendo Switch.
/// </summary>
public interface ISwitchConnectionAsync : IConsoleConnectionAsync
{
    Task<ulong> GetMainNsoBaseAsync(CancellationToken token);

    Task<ulong> GetHeapBaseAsync(CancellationToken token);

    Task<string> GetTitleID(CancellationToken token);

    /// <summary>
    /// Asks the console for its title ID and returns how long the answer took, not counting
    /// time spent waiting for other commands on the connection to finish first.
    /// </summary>
    Task<TimeSpan> PingAsync(CancellationToken token);

    Task<string> GetBotbaseVersion(CancellationToken token);

    Task<string> GetGameInfo(string info, CancellationToken token);

    Task<bool> IsProgramRunning(ulong pid, CancellationToken token);

    Task<byte[]> ReadBytesMainAsync(ulong offset, int length, CancellationToken token);

    Task<byte[]> ReadBytesAbsoluteAsync(ulong offset, int length, CancellationToken token);

    Task WriteBytesMainAsync(byte[] data, ulong offset, CancellationToken token);

    Task WriteBytesAbsoluteAsync(byte[] data, ulong offset, CancellationToken token);

    Task<byte[]> ReadRaw(byte[] command, int length, CancellationToken token);

    Task<byte[]> PointerPeek(int size, IEnumerable<long> jumps, CancellationToken token);

    Task PointerPoke(byte[] data, IEnumerable<long> jumps, CancellationToken token);

    Task<ulong> PointerAll(IEnumerable<long> jumps, CancellationToken token);

}