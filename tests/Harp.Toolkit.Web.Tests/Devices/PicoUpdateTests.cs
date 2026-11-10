using System.Buffers.Binary;
using System.Text;
using Bonsai.Harp;
using Harp.Toolkit.Firmware;
using Harp.Toolkit.Firmware.Pico;
using Harp.Toolkit.Web.Devices;
using FirmwareMetadata = Harp.Toolkit.Firmware.FirmwareMetadata;
using PicoImage = Harp.Toolkit.Firmware.Pico.FirmwareImage;

namespace Harp.Toolkit.Web.Tests.Devices;

[TestClass]
public sealed class PicoUpdateTests
{
    const uint FlashStart = 0x10000000;
    const string FirmwareName = "Pico-fw0.2-harp1.13-hw1.0-ass0";

    // A unique ID whose board ID, in its last eight bytes, the boot ROM reports as its serial number.
    static readonly byte[] Uid = [0, 0, 0, 0, 0, 0, 0, 0, 0xE6, 0x61, 0x38, 0x52, 0x83, 0x2B, 0x6D, 0x2C];
    const string UidSerialNumber = "E6613852832B6D2C";

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    static EmulatedDevice CreateDevice(bool ignoreReset = false) =>
        new(name: "Pico", whoAmI: 1300, hardwareMajor: 1, hardwareMinor: 0) { Uid = Uid, IgnoreReset = ignoreReset };

    [TestMethod]
    public async Task Update_SupportedDevice_WritesVerifiesAndReboots()
    {
        var device = CreateDevice();
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 13)).ToArray();
        var firmware = CreateFirmware(FirmwareName, PicoImage.FamilyRP2040, data);
        var bootRom = new EmulatedBootRom(PicoModel.RP2040, UidSerialNumber);
        var connector = new EmulatedBootselConnector(bootRom);
        var stages = new List<UpdateStage>();

        await PicoBootRom.UpdateFirmwareAsync(device, firmware, forceUpdate: false, connector, new DeviceProtocolTests.ImmediateProgress(stages), Token);

        Assert.AreEqual((byte)0x20, device.ResetWrite);
        Assert.IsTrue(connector.Prepared);
        Assert.AreEqual(UidSerialNumber, connector.RequestedSerialNumber);
        Assert.IsTrue(connector.ExcludedWaiting);
        Assert.IsTrue(bootRom.ExclusiveAccess);
        Assert.AreEqual(2, bootRom.SectorsErased);
        for (int i = 0; i < data.Length; i++)
            Assert.AreEqual(data[i], bootRom.ReadFlash(FlashStart + (uint)i), $"Byte {i}");
        Assert.IsTrue(bootRom.Rebooted);
        Assert.IsTrue(bootRom.Closed);
        CollectionAssert.IsSubsetOf(
            new[] { UpdateStage.Check, UpdateStage.Reset, UpdateStage.Bootloader, UpdateStage.Write },
            stages);
        Assert.AreEqual(UpdateStage.Restart, stages[^1]);
    }

    [TestMethod]
    public async Task Update_RP2350Image_UsesRP2350Reboot()
    {
        var firmware = CreateFirmware(FirmwareName, PicoImage.FamilyRP2350ArmSecure, new byte[300]);
        var bootRom = new EmulatedBootRom(PicoModel.RP2350);

        await PicoBootRom.UpdateFirmwareAsync(CreateDevice(), firmware, forceUpdate: false, new EmulatedBootselConnector(bootRom), cancellationToken: Token);

        Assert.IsTrue(bootRom.Rebooted);
        Assert.Contains(PicobootClient.Reboot2, bootRom.Commands);
    }

    [TestMethod]
    public async Task Update_ImageForOtherMicrocontroller_RebootsAndRefuses()
    {
        var firmware = CreateFirmware(FirmwareName, PicoImage.FamilyRP2350ArmSecure, new byte[300]);
        var bootRom = new EmulatedBootRom(PicoModel.RP2040);

        await Assert.ThrowsExactlyAsync<UnsupportedImageException>(() =>
            PicoBootRom.UpdateFirmwareAsync(CreateDevice(), firmware, forceUpdate: false, new EmulatedBootselConnector(bootRom), cancellationToken: Token));

        Assert.IsTrue(bootRom.Rebooted);
        Assert.AreEqual(0, bootRom.SectorsErased);
        Assert.IsTrue(bootRom.Closed);
    }

    [TestMethod]
    public async Task Update_WrongHardware_RefusesBeforeReset()
    {
        var firmware = CreateFirmware("Pico-fw0.2-harp1.13-hw2.0-ass0", PicoImage.FamilyRP2040, new byte[300]);
        var device = CreateDevice();
        var connector = new EmulatedBootselConnector(new EmulatedBootRom(PicoModel.RP2040));

        await Assert.ThrowsExactlyAsync<HarpException>(() =>
            PicoBootRom.UpdateFirmwareAsync(device, firmware, forceUpdate: false, connector, cancellationToken: Token));

        Assert.IsNull(device.ResetWrite);
        Assert.IsNull(connector.ExcludedWaiting);
    }

    [TestMethod]
    public async Task Update_InBootselModeWithoutPort_WritesToWaitingDevice()
    {
        var data = new byte[1000];
        Array.Fill(data, (byte)0x5A);
        var firmware = CreateFirmware(FirmwareName, PicoImage.FamilyRP2040, data);
        var bootRom = new EmulatedBootRom(PicoModel.RP2040);
        var connector = new EmulatedBootselConnector(bootRom);

        await PicoBootRom.UpdateFirmwareAsync(null, firmware, forceUpdate: true, connector, cancellationToken: Token);

        Assert.IsFalse(connector.ExcludedWaiting);
        Assert.IsNull(connector.RequestedSerialNumber);
        Assert.AreEqual((byte)0x5A, bootRom.ReadFlash(FlashStart + 999));
        Assert.IsTrue(bootRom.Rebooted);
    }

    [TestMethod]
    public async Task Update_ForcedOnSilentPort_LooksForWaitingDevice()
    {
        // A device in BOOTSEL mode has no serial port, so the selected port does not answer.
        var device = new EmulatedDevice { SilentProtocol = true };
        var firmware = CreateFirmware(FirmwareName, PicoImage.FamilyRP2040, new byte[300]);
        var bootRom = new EmulatedBootRom(PicoModel.RP2040);
        var connector = new EmulatedBootselConnector(bootRom);

        await PicoBootRom.UpdateFirmwareAsync(device, firmware, forceUpdate: true, connector, cancellationToken: Token);

        Assert.IsFalse(connector.ExcludedWaiting);
        Assert.IsTrue(bootRom.Rebooted);
    }

    [TestMethod]
    public async Task Update_DeviceIgnoresReset_ReportsMissingUpdateFirmware()
    {
        var device = CreateDevice(ignoreReset: true);
        var firmware = CreateFirmware(FirmwareName, PicoImage.FamilyRP2040, new byte[300]);
        var connector = new EmulatedBootselConnector(new EmulatedBootRom(PicoModel.RP2040));

        await Assert.ThrowsExactlyAsync<BootselRequestIgnoredException>(() =>
            PicoBootRom.UpdateFirmwareAsync(device, firmware, forceUpdate: false, connector, cancellationToken: Token));

        Assert.IsNull(connector.ExcludedWaiting);
    }

    [TestMethod]
    public async Task Update_BootRomRejectsCommand_ReportsStatus()
    {
        var firmware = CreateFirmware(FirmwareName, PicoImage.FamilyRP2040, new byte[300]);
        var bootRom = new EmulatedBootRom(PicoModel.RP2040) { RejectCommand = PicobootClient.FlashErase };

        var error = await Assert.ThrowsExactlyAsync<HarpException>(() =>
            PicoBootRom.UpdateFirmwareAsync(null, firmware, forceUpdate: true, new EmulatedBootselConnector(bootRom), cancellationToken: Token));

        StringAssert.Contains(error.Message, "0x03");
        Assert.IsFalse(bootRom.Rebooted);
        Assert.IsTrue(bootRom.Closed);
    }

    [TestMethod]
    public void Read_Uf2File_ReadsPicoImageAndTargets()
    {
        var uf2 = CreateUf2(PicoImage.FamilyRP2040, new byte[600]);

        var file = FirmwareFile.Read(FirmwareName + ".uf2", new MemoryStream(uf2));

        var pico = Assert.IsInstanceOfType<PicoFirmwareFile>(file);
        Assert.AreEqual(FirmwareCore.Pico, pico.Core);
        Assert.AreEqual("Pico", pico.Metadata!.DeviceName);
        Assert.IsTrue(pico.TargetsRP2040);
        Assert.IsFalse(pico.TargetsRP2350);
        Assert.AreEqual("RP2040", pico.Targets);
    }

    [TestMethod]
    public void Read_UnsupportedFiles_Throw()
    {
        var empty = new MemoryStream();
        Assert.ThrowsExactly<ArgumentException>(() => FirmwareFile.Read(FirmwareName + ".bin", empty));
        Assert.ThrowsExactly<ArgumentException>(() => FirmwareFile.Read("firmware.uf2", empty));
        var coprocessor = Assert.ThrowsExactly<ArgumentException>(() =>
            FirmwareFile.Read("SoundCard.PIC32-fw2.2-harp1.4-hw2.2-ass0.hex", empty));
        StringAssert.Contains(coprocessor.Message, "coprocessor");
    }

    [TestMethod]
    public async Task Read_UnconventionalName_NeedsExplicitConsentAndSkipsDeviceCheck()
    {
        const string name = "white_rabbit-9d26b3be6ccf156a24286eeb83b7f1653a2d831c.uf2";
        var uf2 = CreateUf2(PicoImage.FamilyRP2040, new byte[300]);

        Assert.ThrowsExactly<FirmwareNameException>(() => FirmwareFile.Read(name, new MemoryStream(uf2)));
        var file = Assert.IsInstanceOfType<PicoFirmwareFile>(FirmwareFile.Read(name, new MemoryStream(uf2), allowUnknownName: true));
        Assert.IsNull(file.Metadata);

        // The device hardware is not checked, but the image must still be for its microcontroller.
        var bootRom = new EmulatedBootRom(PicoModel.RP2040);
        await PicoBootRom.UpdateFirmwareAsync(CreateDevice(), file, forceUpdate: false, new EmulatedBootselConnector(bootRom), cancellationToken: Token);
        Assert.IsTrue(bootRom.Rebooted);
    }

    [TestMethod]
    public void GetSerialNumber_BoardId_MatchesBootRomSerial()
    {
        Assert.AreEqual(UidSerialNumber, PicoBootRom.GetSerialNumber(Uid));
        Assert.IsNull(PicoBootRom.GetSerialNumber(new byte[16]));
        Assert.IsNull(PicoBootRom.GetSerialNumber(new byte[8]));
    }

    [TestMethod]
    public void CreateCommand_LaysOutPicobootPacket()
    {
        var command = PicobootClient.CreateCommand(7, PicobootClient.Read, 256, [1, 2, 3, 4]);

        Assert.HasCount(32, command);
        Assert.AreEqual(PicobootClient.Magic, BinaryPrimitives.ReadUInt32LittleEndian(command));
        Assert.AreEqual(7u, BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(4)));
        Assert.AreEqual(PicobootClient.Read, command[8]);
        Assert.AreEqual((byte)4, command[9]);
        Assert.AreEqual(256u, BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(12)));
        Assert.AreEqual((byte)3, command[18]);
    }

    static PicoFirmwareFile CreateFirmware(string name, uint family, byte[] data)
    {
        var image = PicoImage.FromStream(new MemoryStream(CreateUf2(family, data)));
        return new PicoFirmwareFile(name + ".uf2", FirmwareMetadata.Parse(name), image);
    }

    /// <summary>
    /// Lays out data as UF2 blocks of 256 bytes from the start of flash, as the Pico SDK does.
    /// </summary>
    static byte[] CreateUf2(uint family, byte[] data)
    {
        const int PayloadSize = 256;
        var blockCount = (data.Length + PayloadSize - 1) / PayloadSize;
        var uf2 = new byte[blockCount * 512];
        for (int i = 0; i < blockCount; i++)
        {
            var block = uf2.AsSpan(i * 512, 512);
            BinaryPrimitives.WriteUInt32LittleEndian(block, 0x0A324655);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..], 0x9E5D5157);
            BinaryPrimitives.WriteUInt32LittleEndian(block[8..], 0x00002000);
            BinaryPrimitives.WriteUInt32LittleEndian(block[12..], FlashStart + (uint)(i * PayloadSize));
            BinaryPrimitives.WriteUInt32LittleEndian(block[16..], PayloadSize);
            BinaryPrimitives.WriteUInt32LittleEndian(block[20..], (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(block[24..], (uint)blockCount);
            BinaryPrimitives.WriteUInt32LittleEndian(block[28..], family);
            var count = Math.Min(PayloadSize, data.Length - i * PayloadSize);
            data.AsSpan(i * PayloadSize, count).CopyTo(block[32..]);
            BinaryPrimitives.WriteUInt32LittleEndian(block[508..], 0x0AB16F30);
        }
        return uf2;
    }
}
