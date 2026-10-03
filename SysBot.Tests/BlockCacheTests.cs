using FluentAssertions;
using SysBot.Base;
using SysBot.Pokemon;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SysBot.Tests
{
    public class BlockCacheTests
    {
        // A fake sys-botbase whose memory is a dictionary: peekAbsolute returns the
        // bytes stored at that address, or a bare newline (unreadable) like the real one.
        private static (TcpListener Listener, ConcurrentDictionary<ulong, byte[]> Memory) StartFakeSwitch()
        {
            var memory = new ConcurrentDictionary<ulong, byte[]>();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using var c = client;
                        var stream = c.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII);
                        while (await reader.ReadLineAsync() is { } line)
                        {
                            var parts = line.Split(' ');
                            if (parts[0] != "peekAbsolute")
                                continue;
                            var address = ulong.Parse(parts[1][2..], NumberStyles.HexNumber);
                            int count = int.Parse(parts[2]);
                            string reply = memory.TryGetValue(address, out var bytes) && bytes.Length >= count
                                ? Convert.ToHexString(bytes, 0, count) + "\n"
                                : "\n";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
                        }
                    });
                }
            });
            return (listener, memory);
        }

        // One save block key table: the range (start, end) at baseBlock + 8, one entry holding the key.
        private static void LayOutTable(ConcurrentDictionary<ulong, byte[]> memory, ulong baseBlock, ulong entry, uint key)
        {
            var range = new byte[16];
            BitConverter.TryWriteBytes(range.AsSpan(0, 8), entry);
            BitConverter.TryWriteBytes(range.AsSpan(8, 8), entry + 48);
            memory[baseBlock + 8] = range;
            memory[entry] = BitConverter.GetBytes(key);
        }

        // 10/03: the host restarted the game while the bot was stopped, then pressed
        // Start. The cached block address now pointed at unreadable memory, and the
        // check threw "could not read that memory" instead of searching again.
        [Fact]
        public async Task MovedBlockIsFoundAgainAfterTheGameRestarts()
        {
            var (listener, memory) = StartFakeSwitch();
            var cfg = new PokeBotState();
            cfg.Connection.Protocol = SwitchProtocol.WiFi;
            cfg.Connection.IP = "127.0.0.1";
            cfg.Connection.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var bot = new RemoteControlBotSV(cfg);
            bot.Connection.Connect();

            const uint key = 0xCAFEBABE;
            const ulong baseBlock = 0x1000;
            LayOutTable(memory, baseBlock, 0x5000, key);
            (await bot.SearchSaveKey(baseBlock, key, CancellationToken.None)).Should().Be(0x5000UL);

            // The game restarts: the old entry is gone and the table lives somewhere else.
            memory.Clear();
            LayOutTable(memory, baseBlock, 0x9000, key);
            (await bot.SearchSaveKey(baseBlock, key, CancellationToken.None)).Should().Be(0x9000UL);

            bot.Connection.Disconnect();
            listener.Stop();
        }
    }
}
