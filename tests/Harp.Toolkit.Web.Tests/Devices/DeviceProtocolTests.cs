using System.Text;
using Bonsai.Harp;
using Harp.Toolkit.Firmware;
using Harp.Toolkit.Web.Devices;
using AtxmegaImage = Harp.Toolkit.Firmware.ATxmega.FirmwareImage;
using FirmwareMetadata = Harp.Toolkit.Firmware.FirmwareMetadata;

namespace Harp.Toolkit.Web.Tests.Devices;

[TestClass]
public sealed class DeviceProtocolTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    /// <summary>
    /// A stream that returns its data in chunks of the specified size, like a serial port does.
    /// </summary>
    sealed class ChunkedStream(byte[] data, int chunkSize) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }

    static async Task<List<HarpMessage>> ReadAllAsync(HarpMessageReader reader, CancellationToken cancellationToken)
    {
        var messages = new List<HarpMessage>();
        while (await reader.ReadAsync(cancellationToken) is { } message)
            messages.Add(message);
        return messages;
    }

    [TestMethod]
    public async Task Reader_SplitAcrossReads_ReadsEveryMessage()
    {
        var first = HarpMessage.FromPayload(32, 1.25, MessageType.Event, PayloadType.U16, [1, 2]);
        var second = HarpCommand.WriteByte(33, 7);
        var data = first.MessageBytes.Concat(second.MessageBytes).ToArray();

        var reader = new HarpMessageReader(new ChunkedStream(data, chunkSize: 3));
        var messages = await ReadAllAsync(reader, Token);

        Assert.HasCount(2, messages);
        CollectionAssert.AreEqual(first.MessageBytes, messages[0].MessageBytes);
        CollectionAssert.AreEqual(second.MessageBytes, messages[1].MessageBytes);
        // Harp timestamps have a resolution of 32 microseconds.
        Assert.AreEqual(1.25, messages[0].GetTimestamp(), 32e-6);
        Assert.AreEqual(0, reader.DiscardedBytes);
    }

    [TestMethod]
    public async Task Reader_GarbageAndCorruptMessage_ResynchronizesOnNextValidMessage()
    {
        var corrupt = HarpCommand.WriteUInt16(40, 1000).MessageBytes.ToArray();
        corrupt[^1] ^= 0xFF;
        var valid = HarpMessage.FromPayload(41, 2.5, MessageType.Event, PayloadType.U8, [9]);
        byte[] garbage = [0xFF, 0x00, 0x42, 0x13];
        var data = garbage.Concat(corrupt).Concat(valid.MessageBytes).ToArray();

        var reader = new HarpMessageReader(new MemoryStream(data));
        var messages = await ReadAllAsync(reader, Token);

        Assert.HasCount(1, messages);
        CollectionAssert.AreEqual(valid.MessageBytes, messages[0].MessageBytes);
        Assert.IsGreaterThan(0L, reader.DiscardedBytes);
    }

    [TestMethod]
    public async Task Reader_ManyMessages_SurvivesBufferWraparound()
    {
        var message = HarpMessage.FromPayload(50, 3.0, MessageType.Event, PayloadType.U32, new byte[16]);
        var data = Enumerable.Repeat(message.MessageBytes, 2000).SelectMany(bytes => bytes).ToArray();

        var reader = new HarpMessageReader(new ChunkedStream(data, chunkSize: 1000), bufferSize: 512);
        var messages = await ReadAllAsync(reader, Token);

        Assert.HasCount(2000, messages);
        Assert.AreEqual(0, reader.DiscardedBytes);
    }

    [TestMethod]
    public async Task Connection_ReadIdentity_ReturnsDeviceRegisters()
    {
        var device = new EmulatedDevice(name: "Behavior", whoAmI: 1216);
        await using var connection = new HarpConnection(await device.OpenAsync(PortMode.Protocol));

        var identity = await connection.ReadIdentityAsync(Token);

        Assert.AreEqual(1216, identity.WhoAmI);
        Assert.AreEqual("Behavior", identity.DeviceName);
        Assert.AreEqual((ushort)0x1234, identity.SerialNumber);
        Assert.AreEqual(new HarpVersion(2, 0), identity.HardwareVersion);
        Assert.AreEqual(new HarpVersion(1, 13), identity.CoreVersion);
        Assert.AreEqual(42u, identity.TimestampSeconds);
    }

    [TestMethod]
    public async Task Connection_ErrorReply_ThrowsHarpException()
    {
        var device = new EmulatedDevice();
        await using var connection = new HarpConnection(await device.OpenAsync(PortMode.Protocol));

        await Assert.ThrowsExactlyAsync<HarpException>(() => connection.CommandAsync(HarpCommand.ReadByte(200), Token));
    }

    [TestMethod]
    public async Task Connection_NoReply_ThrowsTimeout()
    {
        var device = new EmulatedDevice { SilentProtocol = true };
        await using var connection = new HarpConnection(await device.OpenAsync(PortMode.Protocol))
        {
            ReplyTimeout = TimeSpan.FromMilliseconds(50)
        };

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => connection.CommandAsync(HarpCommand.ReadUInt16(0), Token));
    }

    [TestMethod]
    public async Task Connection_StreamEnds_FailsPendingCommand()
    {
        var device = new EmulatedDevice { SilentProtocol = true };
        await using var connection = new HarpConnection(await device.OpenAsync(PortMode.Protocol))
        {
            ReplyTimeout = TimeSpan.FromSeconds(10)
        };

        var command = connection.CommandAsync(HarpCommand.ReadUInt16(0), Token);
        await device.CloseAsync();

        await Assert.ThrowsExactlyAsync<IOException>(() => command);
    }

    [TestMethod]
    public async Task Bootloader_SupportedDevice_WritesEveryPageAndExits()
    {
        var device = new EmulatedDevice(name: "Behavior", hardwareMajor: 2, hardwareMinor: 0);
        var firmware = CreateFirmware("Behavior-fw1.2-harp1.13-hw2.0-ass0", pages: 3);
        var stages = new List<UpdateStage>();

        await AtxmegaBootloader.UpdateFirmwareAsync(device, firmware, forceUpdate: false, new ImmediateProgress(stages), Token);

        Assert.IsTrue(device.Exited);
        Assert.AreEqual(3, device.PagesWritten);
        CollectionAssert.AreEqual(firmware.Image.Data, device.Flash.Take(firmware.Image.Data.Length).ToArray());
        CollectionAssert.AreEqual(new[] { PortMode.Protocol, PortMode.Bootloader }, device.Opens);
        Assert.AreEqual(UpdateStage.Restart, stages[^1]);
    }

    [TestMethod]
    public async Task Bootloader_WrongHardware_RefusesWithoutForce()
    {
        var device = new EmulatedDevice(name: "Behavior", hardwareMajor: 1, hardwareMinor: 0);
        var firmware = CreateFirmware("Behavior-fw1.2-harp1.13-hw2.0-ass0", pages: 1);

        await Assert.ThrowsExactlyAsync<HarpException>(() =>
            AtxmegaBootloader.UpdateFirmwareAsync(device, firmware, forceUpdate: false, cancellationToken: Token));
        Assert.IsFalse(device.InBootloader);
        Assert.AreEqual(0, device.PagesWritten);
    }

    [TestMethod]
    public async Task Bootloader_ForceOnSilentDevice_StillWritesFirmware()
    {
        // A device already in its bootloader does not answer the Harp protocol.
        var device = new EmulatedDevice { StartInBootloader = true };
        var firmware = CreateFirmware("Behavior-fw1.2-harp1.13-hw2.0-ass0", pages: 2);

        await AtxmegaBootloader.UpdateFirmwareAsync(device, firmware, forceUpdate: true, cancellationToken: Token);

        Assert.IsTrue(device.Exited);
        Assert.AreEqual(2, device.PagesWritten);
    }

    [TestMethod]
    public void ReadFirmware_IntelHex_ParsesMetadataFromFileName()
    {
        var hex = ToIntelHex(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());

        var file = FirmwareFile.Read("Behavior-fw1.2-harp1.13-hw2.0-ass0.hex", new MemoryStream(Encoding.ASCII.GetBytes(hex)));

        var firmware = Assert.IsInstanceOfType<AtxmegaFirmwareFile>(file);
        Assert.AreEqual(FirmwareCore.ATxmega, firmware.Core);
        Assert.AreEqual("Behavior", firmware.Metadata!.DeviceName);
        Assert.AreEqual(new HarpVersion(1, 2), firmware.Metadata.FirmwareVersion);
        Assert.HasCount(AtxmegaBootloader.ImagePageSize, firmware.Image.Data);
        Assert.AreEqual((byte)99, firmware.Image.Data[99]);
    }

    [TestMethod]
    public void PayloadFormat_WriteAndFormat_RoundTrips()
    {
        var command = PayloadFormat.CreateWrite(40, PayloadType.S16, "-3, 0x10, 200");

        Assert.AreEqual("-3, 16, 200", PayloadFormat.Format(command));
        Assert.AreEqual(PayloadType.S16, PayloadFormat.GetBaseType(command.PayloadType));
        Assert.ThrowsExactly<FormatException>(() => PayloadFormat.CreateWrite(40, PayloadType.U8, "300"));
    }

    static AtxmegaFirmwareFile CreateFirmware(string name, int pages)
    {
        var data = Enumerable.Range(0, pages * AtxmegaBootloader.ImagePageSize).Select(i => (byte)(i * 7)).ToArray();
        var hex = ToIntelHex(data);
        var image = AtxmegaImage.FromStream(new MemoryStream(Encoding.ASCII.GetBytes(hex)), AtxmegaBootloader.ImagePageSize);
        return new AtxmegaFirmwareFile(name + ".hex", FirmwareMetadata.Parse(name), image);
    }

    internal static string ToIntelHex(byte[] data)
    {
        var text = new StringBuilder();
        for (int address = 0; address < data.Length; address += 16)
        {
            var count = Math.Min(16, data.Length - address);
            var sum = count + (address >> 8) + (address & 0xFF);
            text.Append($":{count:X2}{address:X4}00");
            for (int i = 0; i < count; i++)
            {
                text.Append($"{data[address + i]:X2}");
                sum += data[address + i];
            }
            text.Append($"{(byte)-sum:X2}\n");
        }
        return text.Append(":00000001FF\n").ToString();
    }

    internal sealed class ImmediateProgress(List<UpdateStage> stages) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => stages.Add(value.Stage);
    }
}
