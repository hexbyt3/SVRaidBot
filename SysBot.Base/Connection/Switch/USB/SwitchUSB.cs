using LibUsbDotNet;
using LibUsbDotNet.Main;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Base
{
    /// <summary>
    /// Abstract class representing the communication over USB.
    /// </summary>
    public abstract class SwitchUSB : IConsoleConnection
    {
        public string Name { get; }
        public string Label { get; set; }
        public bool Connected { get; protected set; }
        private readonly int Port;

        protected SwitchUSB(int port)
        {
            Port = port;
            Name = Label = $"USB-{port}";
        }

        public void Log(string message) => LogInfo(message);

        public void LogInfo(string message) => LogUtil.LogInfo(message, Label);

        public void LogError(string message) => LogUtil.LogError(message, Label);

        private UsbDevice? SwDevice;
        private UsbEndpointReader? reader;
        private UsbEndpointWriter? writer;

        public int MaximumTransferSize { get; set; } = 0x1C0;
        public int BaseDelay { get; set; } = 1;
        public int DelayFactor { get; set; } = 1000;

        private readonly object _sync = new();
        private static readonly object _registry = new();

        public void Reset()
        {
            Disconnect();
            Connect();
        }

        public Task ConnectAsync(CancellationToken token) => Task.Run(Connect, token);

        private void Connect()
        {
            SwDevice = TryFindUSB();
            if (SwDevice == null)
                throw new Exception("USB device not found.");
            if (SwDevice is not IUsbDevice usb)
                throw new Exception("Device is using a WinUSB driver. Use libusbK and create a filter.");

            lock (_sync)
            {
                if (!usb.UsbRegistryInfo.IsAlive)
                    usb.ResetDevice();

                if (usb.IsOpen)
                    usb.Close();
                usb.Open();

                usb.SetConfiguration(1);
                bool resagain = usb.ClaimInterface(0);
                if (!resagain)
                {
                    usb.ReleaseInterface(0);
                    usb.ClaimInterface(0);
                }

                reader = SwDevice.OpenEndpointReader(ReadEndpointID.Ep01);
                writer = SwDevice.OpenEndpointWriter(WriteEndpointID.Ep01);
                Connected = true;
            }
        }

        private UsbDevice? TryFindUSB()
        {
            lock (_registry)
            {
                foreach (UsbRegistry ur in UsbDevice.AllLibUsbDevices.Cast<UsbRegistry>())
                {
                    if (ur.Vid != 0x057E)
                        continue;
                    if (ur.Pid != 0x3000)
                        continue;

                    ur.DeviceProperties.TryGetValue("Address", out object? addr);
                    if (Port.ToString() != addr?.ToString())
                        continue;

                    return ur.Device;
                }
            }
            return null;
        }

        public void Disconnect()
        {
            lock (_sync)
            {
                if (SwDevice is { IsOpen: true } x)
                {
                    if (x is IUsbDevice wholeUsbDevice)
                    {
                        if (!wholeUsbDevice.UsbRegistryInfo.IsAlive)
                            wholeUsbDevice.ResetDevice();
                        wholeUsbDevice.ReleaseInterface(0);
                    }
                    x.Close();
                }

                reader?.Dispose();
                writer?.Dispose();
                reader = null;
                writer = null;
                SwDevice = null;
                Connected = false;
            }
        }

        public int Send(byte[] buffer)
        {
            lock (_sync)
                return SendInternal(buffer);
        }

        // One command and its reply at a time: two callers that both send before
        // either reads would each take the other's reply.
        protected byte[] Exchange(byte[] command)
        {
            lock (_sync)
            {
                SendInternal(command);
                return ReadBulkUSB();
            }
        }

        protected TimeSpan TimeExchange(byte[] command)
        {
            lock (_sync)
            {
                var answered = Stopwatch.StartNew();
                Exchange(command);
                return answered.Elapsed;
            }
        }

        protected byte[] ExchangeScreenshot(byte[] command)
        {
            lock (_sync)
            {
                SendInternal(command);
                return PixelPeekUSB();
            }
        }

        protected byte[] Read(ulong offset, int length, Func<ulong, int, byte[]> method) => Exchange(method(offset, length));

        // The biggest real replies are screenshots and event data, a few hundred KB at most.
        // A length past this is a garbled header, not something to allocate.
        public const int MaxReplySize = 16 * 1024 * 1024;

        public static bool IsPlausibleReplySize(int size) => size is >= 0 and <= MaxReplySize;

        private int ReadReplySize()
        {
            byte[] sizeOfReturn = new byte[4];
            var ec = reader!.Read(sizeOfReturn, 5000, out int got);
            if (ec != ErrorCode.None || got != 4)
            {
                Disconnect();
                throw new IOException($"The console did not say how long its reply is ({ec}).");
            }
            int size = BitConverter.ToInt32(sizeOfReturn, 0);
            if (!IsPlausibleReplySize(size))
            {
                Disconnect();
                throw new IOException($"The console announced a {size}-byte reply, which can't be right.");
            }
            return size;
        }

        protected byte[] ReadBulkUSB()
        {
            // Give it time to push back.
            Thread.Sleep(1);

            lock (_sync)
            {
                if (reader == null)
                    throw new Exception("USB device not found or not connected.");

                // usb-botbase sends the reply's length first, then the reply.
                int size = ReadReplySize();
                byte[] buffer = new byte[size];
                int read = 0;
                while (read < size)
                {
                    Thread.Sleep(1);
                    var ec = reader.Read(buffer, read, Math.Min(reader.ReadBufferSize, size - read), 5000, out int got);
                    // A read that reports no error but no bytes either would otherwise spin here forever.
                    if (ec != ErrorCode.None || got <= 0)
                    {
                        Disconnect();
                        throw new IOException($"The console stopped partway through its reply ({read} of {size} bytes, {ec}).");
                    }
                    read += got;
                }
                return buffer;
            }
        }

        protected void Write(byte[] data, ulong offset, Func<ulong, byte[], byte[]> method)
        {
            if (data.Length <= MaximumTransferSize)
            {
                WriteSmall(data, offset, method);
                return;
            }
            foreach (var chunk in data.Chunk(MaximumTransferSize))
            {
                WriteSmall(chunk, offset, method);
                offset += (ulong)chunk.Length;
                Thread.Sleep((MaximumTransferSize / DelayFactor) + BaseDelay);
            }
        }

        private void WriteSmall(byte[] data, ulong offset, Func<ulong, byte[], byte[]> method)
        {
            lock (_sync)
            {
                var cmd = method(offset, data);
                SendInternal(cmd);
                Thread.Sleep(1);
            }
        }

        private int SendInternal(byte[] buffer)
        {
            if (writer == null)
                throw new Exception("USB device not found or not connected.");

            uint pack = (uint)buffer.Length + 2;
            var ec = writer.Write(BitConverter.GetBytes(pack), 2000, out _);
            if (ec != ErrorCode.None)
            {
                Disconnect();
                throw new Exception(UsbDevice.LastErrorString);
            }
            ec = writer.Write(buffer, 2000, out var l);
            if (ec != ErrorCode.None)
            {
                Disconnect();
                throw new Exception(UsbDevice.LastErrorString);
            }
            return l;
        }

        // A missed screenshot only costs the embed its picture; a half-read one would show as a broken image.
        protected byte[] PixelPeekUSB()
        {
            try
            {
                return ReadBulkUSB();
            }
            catch (IOException ex)
            {
                LogError($"Could not read the screenshot: {ex.Message}");
                return [];
            }
        }
    }
}