using FluentAssertions;
using SysBot.Base;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SysBot.Tests
{
    public class SwitchSocketTests
    {
        // A fake sys-botbase that answers each command with the given reply chunks,
        // pausing between chunks so they arrive as separate TCP reads.
        private static async Task<(SwitchSocketAsync Socket, Task Server)> Start(params string[][] replies)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[256];
                foreach (var chunks in replies)
                {
                    await stream.ReadAsync(buffer);
                    foreach (var chunk in chunks)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(chunk));
                        await stream.FlushAsync();
                        await Task.Delay(150);
                    }
                }
                listener.Stop();
            });
            var socket = SwitchSocketAsync.CreateInstance(new SwitchConnectionConfig { IP = "127.0.0.1", Port = port });
            socket.Connect();
            return (socket, server);
        }

        [Fact]
        public async Task ReplySplitAcrossPacketsIsReadWhole()
        {
            var (socket, server) = await Start(["0102", "0304\n"], ["AABBCCDD\n"]);
            (await socket.ReadBytesAbsoluteAsync(0x1000, 4, CancellationToken.None)).Should().Equal(1, 2, 3, 4);
            // The next reply must not pick up leftovers from the split one.
            (await socket.ReadBytesAbsoluteAsync(0x2000, 4, CancellationToken.None)).Should().Equal(0xAA, 0xBB, 0xCC, 0xDD);
            await server;
        }

        [Fact]
        public async Task ConcurrentReadsEachGetTheirOwnReply()
        {
            // Answers every peek with the low 4 bytes of the address it asked for,
            // split in two packets so a reply can be interleaved with another's.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var reader = new System.IO.StreamReader(client.GetStream(), Encoding.ASCII);
                var stream = client.GetStream();
                for (int n = 0; n < 8; n++)
                {
                    var line = await reader.ReadLineAsync();
                    var address = Convert.ToUInt32(line!.Split(' ')[1][^8..], 16);
                    var hex = BitConverter.ToString(BitConverter.GetBytes(address)).Replace("-", "");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(hex[..4]));
                    await Task.Delay(20);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(hex[4..] + "\n"));
                }
                listener.Stop();
            });
            var socket = SwitchSocketAsync.CreateInstance(new SwitchConnectionConfig { IP = "127.0.0.1", Port = port });
            socket.Connect();

            var reads = new Task<byte[]>[8];
            for (int i = 0; i < reads.Length; i++)
                reads[i] = socket.ReadBytesAbsoluteAsync(0x1000 + (ulong)i, 4, CancellationToken.None);
            var results = await Task.WhenAll(reads);
            for (int i = 0; i < results.Length; i++)
                BitConverter.ToUInt32(results[i]).Should().Be(0x1000u + (uint)i);
            await server;
        }

        [Fact]
        public async Task ShortErrorReplyFailsWithoutDesyncing()
        {
            var (socket, server) = await Start(["\n"], ["01020304\n"]);
            var read = () => socket.ReadBytesAbsoluteAsync(0, 4, CancellationToken.None);
            await read.Should().ThrowAsync<SwitchReadFailedException>();
            (await socket.ReadBytesAbsoluteAsync(0x1000, 4, CancellationToken.None)).Should().Equal(1, 2, 3, 4);
            await server;
        }
    }
}
