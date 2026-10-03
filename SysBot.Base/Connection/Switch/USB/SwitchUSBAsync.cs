using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchOffsetType;

namespace SysBot.Base
{
    /// <summary>
    /// Connection to a Nintendo Switch hosting the sys-module via USB.
    /// </summary>
    /// <remarks>
    /// Interactions are performed asynchronously.
    /// </remarks>
    public sealed class SwitchUSBAsync : SwitchUSB, ISwitchConnectionAsync
    {
        public SwitchUSBAsync(int port) : base(port)
        {
        }

        public Task<int> SendAsync(byte[] data, CancellationToken token)
        {
            Debug.Assert(data.Length < MaximumTransferSize);
            return Task.Run(() => Send(data), token);
        }

        public Task<byte[]> ReadBytesAsync(uint offset, int length, CancellationToken token) => Task.Run(() => Read(offset, length, Heap.GetReadMethod(false)), token);

        public Task<byte[]> ReadBytesMainAsync(ulong offset, int length, CancellationToken token) => Task.Run(() => Read(offset, length, Main.GetReadMethod(false)), token);

        public Task<byte[]> ReadBytesAbsoluteAsync(ulong offset, int length, CancellationToken token) => Task.Run(() => Read(offset, length, Absolute.GetReadMethod(false)), token);

        public Task<byte[]> ReadBytesMultiAsync(IReadOnlyDictionary<ulong, int> offsetSizes, CancellationToken token) => Task.Run(() => ReadMulti(offsetSizes, Heap.GetReadMultiMethod(false)), token);

        public Task<byte[]> ReadBytesMainMultiAsync(IReadOnlyDictionary<ulong, int> offsetSizes, CancellationToken token) => Task.Run(() => ReadMulti(offsetSizes, Main.GetReadMultiMethod(false)), token);

        public Task<byte[]> ReadBytesAbsoluteMultiAsync(IReadOnlyDictionary<ulong, int> offsetSizes, CancellationToken token) => Task.Run(() => ReadMulti(offsetSizes, Absolute.GetReadMultiMethod(false)), token);

        public Task WriteBytesAsync(byte[] data, uint offset, CancellationToken token) => Task.Run(() => Write(data, offset, Heap.GetWriteMethod(false)), token);

        public Task WriteBytesMainAsync(byte[] data, ulong offset, CancellationToken token) => Task.Run(() => Write(data, offset, Main.GetWriteMethod(false)), token);

        public Task WriteBytesAbsoluteAsync(byte[] data, ulong offset, CancellationToken token) => Task.Run(() => Write(data, offset, Absolute.GetWriteMethod(false)), token);

        public Task<ulong> GetMainNsoBaseAsync(CancellationToken token)
        {
            return Task.Run(() =>
            {
                byte[] baseBytes = Exchange(SwitchCommand.GetMainNsoBase(false));
                return BitConverter.ToUInt64(baseBytes, 0);
            }, token);
        }

        public Task<ulong> GetHeapBaseAsync(CancellationToken token)
        {
            return Task.Run(() =>
            {
                byte[] baseBytes = Exchange(SwitchCommand.GetHeapBase(false));
                return BitConverter.ToUInt64(baseBytes, 0);
            }, token);
        }

        public Task<string> GetTitleID(CancellationToken token)
        {
            return Task.Run(() =>
            {
                byte[] baseBytes = Exchange(SwitchCommand.GetTitleID(false));
                return BitConverter.ToUInt64(baseBytes, 0).ToString("X16").Trim();
            }, token);
        }

        public Task<string> GetBotbaseVersion(CancellationToken token)
        {
            return Task.Run(() =>
            {
                byte[] baseBytes = Exchange(SwitchCommand.GetBotbaseVersion(false));
                return Encoding.UTF8.GetString(baseBytes).Trim('\0');
            }, token);
        }

        public Task<string> GetGameInfo(string info, CancellationToken token)
        {
            return Task.Run(() =>
            {
                byte[] baseBytes = Exchange(SwitchCommand.GetGameInfo(info, false));
                return Encoding.UTF8.GetString(baseBytes).Trim('\0');
            }, token);
        }

        public Task<bool> IsProgramRunning(ulong pid, CancellationToken token)
        {
            return Task.Run(() =>
            {
                byte[] baseBytes = Exchange(SwitchCommand.IsProgramRunning(pid, false));
                return baseBytes.Length == 1 && BitConverter.ToBoolean(baseBytes, 0);
            }, token);
        }

        public Task<byte[]> ReadRaw(byte[] command, int length, CancellationToken token)
        {
            return Task.Run(() =>
            {
                return Exchange(command);
            }, token);
        }

        public Task SendRaw(byte[] command, CancellationToken token)
        {
            return Task.Run(() => Send(command), token);
        }

        public Task<byte[]> PointerPeek(int size, IEnumerable<long> jumps, CancellationToken token)
        {
            return Task.Run(() =>
            {
                return Exchange(SwitchCommand.PointerPeek(jumps, size, false));
            }, token);
        }

        public Task PointerPoke(byte[] data, IEnumerable<long> jumps, CancellationToken token)
        {
            return Task.Run(() =>
            {
                Send(SwitchCommand.PointerPoke(jumps, data, false));
            }, token);
        }

        public Task<ulong> PointerAll(IEnumerable<long> jumps, CancellationToken token)
        {
            return Task.Run(() =>
            {
                byte[] baseBytes = Exchange(SwitchCommand.PointerAll(jumps, false));
                return BitConverter.ToUInt64(baseBytes, 0);
            }, token);
        }

        public Task<ulong> PointerRelative(IEnumerable<long> jumps, CancellationToken token)
        {
            return Task.Run(() =>
            {
                byte[] baseBytes = Exchange(SwitchCommand.PointerRelative(jumps, false));
                return BitConverter.ToUInt64(baseBytes, 0);
            }, token);
        }

        public Task<byte[]> PixelPeek(CancellationToken token)
        {
            return Task.Run(() =>
            {
                return ExchangeScreenshot(SwitchCommand.PixelPeek(false));
            }, token);
        }

        public Task<long> GetUnixTime(CancellationToken token)
        {
            return Task.Run(() =>
            {
                byte[] baseBytes = Exchange(SwitchCommand.GetUnixTime(false));
                return BitConverter.ToInt64(baseBytes, 0);
            }, token);
        }
    }
}