using System.Threading.Channels;
using Bonsai.Harp;
using Harp.Toolkit.Web.Devices;

namespace Harp.Toolkit.Web.Tests.Devices;

/// <summary>
/// Emulates the serial port of an ATxmega Harp device: it answers core register commands in
/// protocol mode, and the page protocol in bootloader mode.
/// </summary>
sealed class EmulatedDevice : IDevicePort
{
    const int HeaderSize = 15;

    readonly Dictionary<int, HarpMessage> registers = new();
    DeviceStream? stream;

    public EmulatedDevice(string name = "Behavior", int whoAmI = 1216, int hardwareMajor = 2, int hardwareMinor = 0)
    {
        SetRegister(HarpCommand.WriteUInt16(WhoAmI.Address, (ushort)whoAmI));
        SetRegister(HarpCommand.WriteByte(HardwareVersionHigh.Address, (byte)hardwareMajor));
        SetRegister(HarpCommand.WriteByte(HardwareVersionLow.Address, (byte)hardwareMinor));
        SetRegister(HarpCommand.WriteByte(FirmwareVersionHigh.Address, 1));
        SetRegister(HarpCommand.WriteByte(FirmwareVersionLow.Address, 0));
        SetRegister(HarpCommand.WriteByte(CoreVersionHigh.Address, 1));
        SetRegister(HarpCommand.WriteByte(CoreVersionLow.Address, 13));
        SetRegister(HarpCommand.WriteByte(AssemblyVersion.Address, 0));
        SetRegister(HarpCommand.WriteUInt32(TimestampSeconds.Address, 42));
        SetRegister(HarpCommand.WriteUInt16(SerialNumber.Address, 0x1234));
        SetRegister(HarpCommand.WriteByte(ResetDevice.Address, (byte)ResetFlags.BootFromEeprom));
        SetRegister(HarpCommand.WriteByte(OperationControl.Address, 0x60));
        var nameBytes = new byte[25];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(nameBytes, 0);
        SetRegister(HarpCommand.WriteByte(DeviceName.Address, nameBytes));
    }

    public int PageSize { get; init; } = 512;

    public bool InBootloader { get; private set; }

    public bool StartInBootloader
    {
        get => InBootloader;
        init => InBootloader = value;
    }

    public bool SilentProtocol { get; init; }

    /// <summary>
    /// Gets a value indicating whether the device replies to reset requests but keeps running,
    /// like Pico firmware that does not implement <c>UPDATE_FIRMWARE</c>.
    /// </summary>
    public bool IgnoreReset { get; init; }

    /// <summary>
    /// Sets the contents of the unique ID register, which Pico devices implement.
    /// </summary>
    public byte[] Uid
    {
        init => SetRegister(HarpCommand.WriteByte(UidAddress, value));
    }

    /// <summary>
    /// Gets the value last written to the reset register.
    /// </summary>
    public byte? ResetWrite { get; private set; }

    const int UidAddress = 16;

    public List<PortMode> Opens { get; } = [];

    public byte[] Flash { get; } = new byte[64 * 1024];

    public int PagesWritten { get; private set; }

    public bool Exited { get; private set; }

    public Task<Stream> OpenAsync(PortMode mode)
    {
        Opens.Add(mode);
        stream = new DeviceStream(this, mode);
        return Task.FromResult<Stream>(stream);
    }

    public Task CloseAsync()
    {
        stream?.Complete();
        stream = null;
        return Task.CompletedTask;
    }

    void SetRegister(HarpMessage write)
    {
        registers[write.Address] = write;
    }

    IEnumerable<byte[]> HandleProtocol(HarpMessage command)
    {
        if (SilentProtocol || InBootloader)
            yield break;

        if (!registers.TryGetValue(command.Address, out var value))
        {
            var error = HarpMessage.FromPayload(command.Address, command.MessageType, command.PayloadType, new byte[1]);
            var bytes = error.MessageBytes.ToArray();
            bytes[0] |= 0x08;
            yield return new HarpMessage(updateChecksum: true, bytes).MessageBytes;
            yield break;
        }

        if (command.MessageType == MessageType.Write)
        {
            if (command.Address == ResetDevice.Address)
            {
                ResetWrite = command.GetPayloadByte();
                InBootloader = !IgnoreReset;
            }
            else
                registers[command.Address] = command;
        }

        var payload = registers[command.Address].GetPayload();
        yield return HarpMessage.FromPayload(command.Address, 1.5, command.MessageType, value.PayloadType, payload).MessageBytes;
    }

    byte[]? HandleBootloader(byte[] message)
    {
        if (!InBootloader)
            return null;

        var reply = (byte[])message.Clone();
        var opcode = message[3];
        var address = BitConverter.ToInt32(message, 5);
        var count = BitConverter.ToInt32(message, 9);
        switch (opcode)
        {
            case 0x66:
                Array.Resize(ref reply, HeaderSize);
                BitConverter.GetBytes(PageSize).CopyTo(reply, 9);
                break;
            case 0x00:
                Array.Copy(message, 13, Flash, address, count);
                PagesWritten++;
                break;
            case 0x77:
                Exited = true;
                InBootloader = false;
                break;
        }

        ushort checksum = 0;
        for (int i = 0; i < reply.Length - 2; i++)
            checksum += reply[i];
        reply[^2] = (byte)checksum;
        reply[^1] = (byte)(checksum >> 8);
        return reply;
    }

    sealed class DeviceStream(EmulatedDevice device, PortMode mode) : Stream
    {
        readonly Channel<byte[]> output = Channel.CreateUnbounded<byte[]>();
        readonly List<byte> input = [];
        byte[]? current;
        int offset;

        public void Complete() => output.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (current is null || offset >= current.Length)
            {
                if (!await output.Reader.WaitToReadAsync(cancellationToken))
                    return 0;
                current = await output.Reader.ReadAsync(cancellationToken);
                offset = 0;
            }

            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            input.AddRange(buffer.ToArray());
            if (mode == PortMode.Protocol)
            {
                while (input.Count >= 2 && input.Count >= input[1] + 2)
                {
                    var length = input[1] + 2;
                    var command = new HarpMessage(input.Take(length).ToArray());
                    input.RemoveRange(0, length);
                    foreach (var reply in device.HandleProtocol(command))
                        output.Writer.TryWrite(reply);
                }
            }
            else if (input.Count >= HeaderSize)
            {
                var count = BitConverter.ToInt32(input.ToArray(), 9);
                if (input.Count >= HeaderSize + count)
                {
                    var message = input.Take(HeaderSize + count).ToArray();
                    input.RemoveRange(0, HeaderSize + count);
                    if (device.HandleBootloader(message) is { } reply)
                        output.Writer.TryWrite(reply);
                }
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
