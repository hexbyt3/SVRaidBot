using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchOffsetType;

namespace SysBot.Base
{
    /// <summary>
    /// Connection to a Nintendo Switch hosting the sys-module via a socket (WiFi).
    /// </summary>
    /// <remarks>
    /// Interactions are performed asynchronously.
    /// </remarks>
    public sealed class SwitchSocketAsync : SwitchSocket, ISwitchConnectionAsync
    {
        private SwitchSocketAsync(IWirelessConnectionConfig cfg) : base(cfg)
        {
        }

        public static SwitchSocketAsync CreateInstance(IWirelessConnectionConfig cfg)
        {
            return new SwitchSocketAsync(cfg);
        }

        public override void Connect()
        {
            if (Connected)
            {
                Log("Already connected prior, skipping initial connection.");
                return;
            }

            Log("Connecting to device...");
            int retryCount = 0;
            const int maxRetries = 10;

            while (retryCount < maxRetries)
            {
                try
                {
                    IAsyncResult result = Connection.BeginConnect(Info.IP, Info.Port, null, null);
                    bool success = result.AsyncWaitHandle.WaitOne(5000, true);
                    if (!success || !Connection.Connected)
                    {
                        throw new Exception("Failed to connect to device.");
                    }
                    Connection.EndConnect(result);
                    Log("Connected!");
                    Label = Name;
                    return;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    Log($"Connection attempt {retryCount} failed: {ex.Message}");
                    if (retryCount >= maxRetries)
                    {
                        throw;
                    }
                    Task.Delay(1000 * retryCount).Wait(); // Wait before retrying
                }
            }
        }

        public override void Reset()
        {
            if (Connected)
                Disconnect();
            else
                InitializeSocket();
            Connect();
        }

        public override void Disconnect()
        {
            Log("Disconnecting from device...");
            IAsyncResult result = Connection.BeginDisconnect(false, null, null);
            bool success = result.AsyncWaitHandle.WaitOne(5000, true);
            if (!success || Connection.Connected)
            {
                InitializeSocket();
                throw new Exception("Failed to disconnect from device.");
            }
            Connection.EndDisconnect(result);
            Log("Disconnected! Resetting Socket.");
            InitializeSocket();
        }

        // One command and its reply at a time. The bot loop and the web panel share
        // this socket; two requests in flight at once can each take the other's reply.
        private readonly SemaphoreSlim _io = new(1, 1);

        /// <summary> Only call this if you are sending small commands. </summary>
        public async Task<int> SendAsync(byte[] buffer, CancellationToken token)
        {
            return await RetryOperation(async (ct) =>
            {
                await _io.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await Connection.SendAsync(buffer, ct).ConfigureAwait(false);
                }
                finally
                {
                    _io.Release();
                }
            }, token);
        }

        public async Task EnsureConnectedAsync(CancellationToken token)
        {
            if (!Connected)
            {
                Log("Connection lost. Attempting to reconnect...");
                await RetryOperation(async (ct) =>
                {
                    Reset();
                    Connect();
                    return true;
                }, token);
            }
        }

        private async Task<byte[]> ReadBytesFromCmdAsync(byte[] cmd, int length, CancellationToken token)
        {
            return await RetryOperation(async (ct) =>
            {
                await EnsureConnectedAsync(ct);
                var size = (length * 2) + 1;
                var buffer = ArrayPool<byte>.Shared.Rent(size);
                await _io.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await Connection.SendAsync(cmd, ct).ConfigureAwait(false);
                    var mem = buffer.AsMemory()[..size];
                    var received = await ReceiveResponseAsync(mem, ct).ConfigureAwait(false);
                    if (received < size)
                        throw new SwitchReadFailedException($"The console could not read that memory ({received} of {size} bytes came back).");
                    if (mem.Span[size - 1] != (byte)'\n')
                        throw Resync($"The console sent a longer reply than {size} bytes.");
                    try
                    {
                        return DecodeResult(mem, length);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        throw Resync("The console's reply was not hex.");
                    }
                }
                finally
                {
                    _io.Release();
                    ArrayPool<byte>.Shared.Return(buffer, true);
                }
            }, token);
        }

        private static byte[] DecodeResult(ReadOnlyMemory<byte> buffer, int length)
        {
            var result = new byte[length];
            var span = buffer.Span[..^1]; // Last byte is always a terminator
            Decoder.LoadHexBytesTo(span, result, 2);
            return result;
        }

        public async Task<byte[]> ReadBytesAsync(uint offset, int length, CancellationToken token) => await Read(offset, length, Heap, token).ConfigureAwait(false);

        public async Task<byte[]> ReadBytesMainAsync(ulong offset, int length, CancellationToken token) => await Read(offset, length, Main, token).ConfigureAwait(false);

        public async Task<byte[]> ReadBytesAbsoluteAsync(ulong offset, int length, CancellationToken token) => await Read(offset, length, Absolute, token).ConfigureAwait(false);

        public async Task<byte[]> ReadBytesMultiAsync(IReadOnlyDictionary<ulong, int> offsetSizes, CancellationToken token) => await ReadMulti(offsetSizes, Heap, token).ConfigureAwait(false);

        public async Task<byte[]> ReadBytesMainMultiAsync(IReadOnlyDictionary<ulong, int> offsetSizes, CancellationToken token) => await ReadMulti(offsetSizes, Main, token).ConfigureAwait(false);

        public async Task<byte[]> ReadBytesAbsoluteMultiAsync(IReadOnlyDictionary<ulong, int> offsetSizes, CancellationToken token) => await ReadMulti(offsetSizes, Absolute, token).ConfigureAwait(false);

        public async Task WriteBytesAsync(byte[] data, uint offset, CancellationToken token) => await Write(data, offset, Heap, token).ConfigureAwait(false);

        public async Task WriteBytesMainAsync(byte[] data, ulong offset, CancellationToken token) => await Write(data, offset, Main, token).ConfigureAwait(false);

        public async Task WriteBytesAbsoluteAsync(byte[] data, ulong offset, CancellationToken token) => await Write(data, offset, Absolute, token).ConfigureAwait(false);

        public async Task<ulong> GetMainNsoBaseAsync(CancellationToken token)
        {
            byte[] baseBytes = await ReadBytesFromCmdAsync(SwitchCommand.GetMainNsoBase(), sizeof(ulong), token).ConfigureAwait(false);
            Array.Reverse(baseBytes, 0, 8);
            return BitConverter.ToUInt64(baseBytes, 0);
        }

        public async Task<ulong> GetHeapBaseAsync(CancellationToken token)
        {
            var baseBytes = await ReadBytesFromCmdAsync(SwitchCommand.GetHeapBase(), sizeof(ulong), token).ConfigureAwait(false);
            Array.Reverse(baseBytes, 0, 8);
            return BitConverter.ToUInt64(baseBytes, 0);
        }

        public async Task<string> GetTitleID(CancellationToken token)
        {
            var bytes = await ReadRaw(SwitchCommand.GetTitleID(), 17, token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(bytes).Trim();
        }

        public async Task<string> GetBotbaseVersion(CancellationToken token)
        {
            // Allows up to 9 characters for version, and trims extra '\0' if unused.
            var bytes = await ReadRaw(SwitchCommand.GetBotbaseVersion(), 10, token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(bytes).Trim('\0');
        }

        public async Task<string> GetGameInfo(string info, CancellationToken token)
        {
            var bytes = await ReadRaw(SwitchCommand.GetGameInfo(info), 17, token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(bytes).Trim(new char[] { '\0', '\n' });
        }

        public async Task<bool> IsProgramRunning(ulong pid, CancellationToken token)
        {
            var bytes = await ReadRaw(SwitchCommand.IsProgramRunning(pid), 17, token).ConfigureAwait(false);
            return ulong.TryParse(Encoding.ASCII.GetString(bytes).Trim(), out var value) && value == 1;
        }

        private async Task<byte[]> Read(ulong offset, int length, SwitchOffsetType type, CancellationToken token)
        {
            await EnsureConnectedAsync(token);
            var method = type.GetReadMethod();
            if (length <= MaximumTransferSize)
            {
                var cmd = method(offset, length);
                return await ReadBytesFromCmdAsync(cmd, length, token).ConfigureAwait(false);
            }

            byte[] result = new byte[length];
            for (int i = 0; i < length; i += MaximumTransferSize)
            {
                int len = MaximumTransferSize;
                int delta = length - i;
                if (delta < MaximumTransferSize)
                    len = delta;

                var cmd = method(offset + (uint)i, len);
                var bytes = await ReadBytesFromCmdAsync(cmd, len, token).ConfigureAwait(false);
                bytes.CopyTo(result, i);
                await Task.Delay((MaximumTransferSize / DelayFactor) + BaseDelay, token).ConfigureAwait(false);
            }
            return result;
        }

        private async Task<byte[]> ReadMulti(IReadOnlyDictionary<ulong, int> offsetSizes, SwitchOffsetType type, CancellationToken token)
        {
            var method = type.GetReadMultiMethod();
            var cmd = method(offsetSizes);
            var totalSize = offsetSizes.Values.Sum();
            return await ReadBytesFromCmdAsync(cmd, totalSize, token).ConfigureAwait(false);
        }

        private async Task Write(byte[] data, ulong offset, SwitchOffsetType type, CancellationToken token)
        {
            var method = type.GetWriteMethod();
            if (data.Length <= MaximumTransferSize)
            {
                var cmd = method(offset, data);
                await SendAsync(cmd, token).ConfigureAwait(false);
                return;
            }
            int byteCount = data.Length;
            for (int i = 0; i < byteCount; i += MaximumTransferSize)
            {
                var slice = data.SliceSafe(i, MaximumTransferSize);
                var cmd = method(offset + (uint)i, slice);
                await SendAsync(cmd, token).ConfigureAwait(false);
                await Task.Delay((MaximumTransferSize / DelayFactor) + BaseDelay, token).ConfigureAwait(false);
            }
        }

        public async Task<byte[]> ReadRaw(byte[] command, int length, CancellationToken token)
        {
            var buffer = new byte[length];
            await _io.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Connection.SendAsync(command, token).ConfigureAwait(false);
                await ReceiveResponseAsync(buffer, token).ConfigureAwait(false);
            }
            finally
            {
                _io.Release();
            }
            return buffer;
        }

        /// <summary>
        /// ReceiveAsync returns as soon as any bytes arrive, not when the buffer is
        /// full. A reply split across TCP packets used to decode its unfilled tail
        /// as zeros ("Parameter '_0'"), and the rest of it was then read as the start
        /// of the next reply, so every read after it failed too. Replies are hex text
        /// ending in '\n', which the payload never contains, so a chunk ending in it
        /// is the whole reply; that also ends the short replies sys-botbase sends
        /// for an unreadable address instead of waiting on them forever.
        /// </summary>
        private async ValueTask<int> ReceiveResponseAsync(Memory<byte> buffer, CancellationToken token)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int count = await Connection.ReceiveAsync(buffer[read..], token).ConfigureAwait(false);
                if (count == 0)
                    throw new SocketException((int)SocketError.ConnectionReset);
                read += count;
                if (buffer.Span[read - 1] == (byte)'\n')
                    break;
            }
            return read;
        }

        public async Task SendRaw(byte[] command, CancellationToken token)
        {
            await SendAsync(command, token).ConfigureAwait(false);
        }

        public async Task<byte[]> PointerPeek(int size, IEnumerable<long> jumps, CancellationToken token)
        {
            return await ReadBytesFromCmdAsync(SwitchCommand.PointerPeek(jumps, size), size, token).ConfigureAwait(false);
        }

        public async Task PointerPoke(byte[] data, IEnumerable<long> jumps, CancellationToken token)
        {
            await SendAsync(SwitchCommand.PointerPoke(jumps, data), token).ConfigureAwait(false);
        }

        public async Task<ulong> PointerAll(IEnumerable<long> jumps, CancellationToken token)
        {
            var offsetBytes = await ReadBytesFromCmdAsync(SwitchCommand.PointerAll(jumps), sizeof(ulong), token).ConfigureAwait(false);
            Array.Reverse(offsetBytes, 0, 8);
            return BitConverter.ToUInt64(offsetBytes, 0);
        }

        public async Task<ulong> PointerRelative(IEnumerable<long> jumps, CancellationToken token)
        {
            var offsetBytes = await ReadBytesFromCmdAsync(SwitchCommand.PointerRelative(jumps), sizeof(ulong), token).ConfigureAwait(false);
            Array.Reverse(offsetBytes, 0, 8);
            return BitConverter.ToUInt64(offsetBytes, 0);
        }

        public async Task<byte[]> PixelPeek(CancellationToken token)
        {
            byte[] data;
            await _io.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Connection.SendAsync(SwitchCommand.PixelPeek(), token).ConfigureAwait(false);
                data = await FlexRead(token).ConfigureAwait(false);
            }
            finally
            {
                _io.Release();
            }
            var result = Array.Empty<byte>();
            try
            {
                result = Decoder.ConvertHexByteStringToBytes(data);
            }
            catch (Exception e)
            {
                LogError($"Malformed screenshot data received:\n{e.Message}");
            }

            return result;
        }

        private async Task<byte[]> FlexRead(CancellationToken token)
        {
            List<byte> flexBuffer = new();
            int available = Connection.Available;
            Connection.ReceiveTimeout = 5_000;

            do
            {
                byte[] buffer = new byte[available];
                try
                {
                    Connection.Receive(buffer, available, SocketFlags.None);
                    flexBuffer.AddRange(buffer);
                }
                catch (Exception ex)
                {
                    LogError($"Socket exception thrown while receiving data:\n{ex.Message}");
                    Resync("The screenshot stopped arriving partway.");
                    return Array.Empty<byte>();
                }

                await Task.Delay(MaximumTransferSize / DelayFactor + BaseDelay, token).ConfigureAwait(false);
                available = Connection.Available;
            } while (flexBuffer.Count == 0 || flexBuffer.Last() != (byte)'\n');

            Connection.ReceiveTimeout = 0;
            return flexBuffer.ToArray();
        }

        public async Task<long> GetUnixTime(CancellationToken token)
        {
            var result = await ReadBytesFromCmdAsync(SwitchCommand.GetUnixTime(), 8, token).ConfigureAwait(false);
            Array.Reverse(result);
            return BitConverter.ToInt64(result, 0);
        }

        /// <summary>
        /// Replies carry no request id, so once one reply is left half-read every
        /// later read takes the leftover of the one before it. A Wi-Fi stall during
        /// a screenshot did that and the bot read garbage for eight hours. A fresh
        /// socket is the only way back in step. Call it while holding <see cref="_io"/>;
        /// the IOException it returns makes <see cref="RetryOperation"/> try again.
        /// </summary>
        private IOException Resync(string reason)
        {
            LogError($"{reason} Replies are out of step; reconnecting to start clean.");
            try { Connection.Close(); } catch { }
            InitializeSocket();
            Connect();
            return new IOException(reason);
        }

        private void HandleDisconnect()
        {
            Log("Unexpected disconnection detected. Attempting to reconnect...");
            try
            {
                Reset();
                Connect();
            }
            catch (Exception ex)
            {
                LogError($"Failed to reconnect: {ex.Message}");
            }
        }

        private async Task<T> RetryOperation<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token, int maxRetries = 3)
        {
            int retryCount = 0;
            while (true)
            {
                try
                {
                    return await operation(token);
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    if (++retryCount > maxRetries)
                        throw;

                    int delay = (int)Math.Pow(2, retryCount) * 1000; // Exponential backoff
                    Log($"Connection error. Retrying in {delay}ms. Attempt {retryCount} of {maxRetries}");
                    await Task.Delay(delay, token);
                }
            }
        }
    }
}