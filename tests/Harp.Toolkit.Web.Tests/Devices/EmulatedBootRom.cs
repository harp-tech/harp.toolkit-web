using System.Buffers.Binary;
using Harp.Toolkit.Web.Devices;
using Harp.WebUsb;

namespace Harp.Toolkit.Web.Tests.Devices;

/// <summary>
/// Emulates the PICOBOOT interface of an RP2040 or RP2350 boot ROM in BOOTSEL mode, with a
/// flash memory that must be erased before it is written.
/// </summary>
sealed class EmulatedBootRom(PicoModel model, string? serialNumber = null) : IPicobootInterface
{
    const int SectorSize = 4096;

    readonly Dictionary<uint, byte> flash = [];
    byte[]? pendingCommand;
    byte[]? readData;
    bool expectingAck;
    bool ackIn;
    uint lastStatus;
    byte lastCommand;

    public PicoModel Model { get; } = model;

    public string? SerialNumber { get; } = serialNumber;

    /// <summary>Gets or sets a command the boot ROM rejects by stalling.</summary>
    public byte? RejectCommand { get; init; }

    public bool Rebooted { get; private set; }

    public bool Closed { get; private set; }

    public bool ExclusiveAccess { get; private set; }

    public int SectorsErased { get; private set; }

    public List<byte> Commands { get; } = [];

    public byte ReadFlash(uint address) => flash.TryGetValue(address, out var value) ? value : (byte)0xFF;

    public Task ResetInterfaceAsync()
    {
        pendingCommand = null;
        readData = null;
        expectingAck = false;
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadCommandStatusAsync()
    {
        var status = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(status.AsSpan(4), lastStatus);
        status[8] = lastCommand;
        return Task.FromResult(status);
    }

    public Task<int> BulkOutAsync(ReadOnlyMemory<byte> data)
    {
        if (expectingAck && !ackIn)
        {
            expectingAck = false;
            return Task.FromResult(data.Length);
        }

        if (pendingCommand is { } command)
        {
            // The data phase of a write command.
            pendingCommand = null;
            var address = BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(16));
            for (int i = 0; i < data.Length; i++)
            {
                if (ReadFlash(address + (uint)i) != 0xFF)
                    throw Stall(command[8], 5);
                flash[address + (uint)i] = data.Span[i];
            }
            Acknowledge(input: true);
            return Task.FromResult(data.Length);
        }

        var packet = data.ToArray();
        if (packet.Length != PicobootClient.CommandSize || BinaryPrimitives.ReadUInt32LittleEndian(packet) != PicobootClient.Magic)
            throw Stall(0, 1);

        var commandId = packet[8];
        Commands.Add(commandId);
        if (commandId == RejectCommand)
            throw Stall(commandId, 4);

        var transferLength = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12));
        var argument0 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16));
        var argument1 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20));
        switch (commandId)
        {
            case PicobootClient.ExclusiveAccess:
                ExclusiveAccess = packet[16] != 0;
                Acknowledge(input: true);
                break;
            case PicobootClient.ExitXip:
            case PicobootClient.EnterCommandXip:
                Acknowledge(input: true);
                break;
            case PicobootClient.FlashErase:
                if (argument0 % SectorSize != 0 || argument1 % SectorSize != 0)
                    throw Stall(commandId, 2);
                for (uint address = argument0; address < argument0 + argument1; address++)
                    flash.Remove(address);
                SectorsErased += (int)(argument1 / SectorSize);
                Acknowledge(input: true);
                break;
            case PicobootClient.Write:
                pendingCommand = packet;
                break;
            case PicobootClient.Read:
                readData = new byte[transferLength];
                for (int i = 0; i < readData.Length; i++)
                    readData[i] = ReadFlash(argument0 + (uint)i);
                break;
            case PicobootClient.Reboot when Model == PicoModel.RP2040:
            case PicobootClient.Reboot2 when Model == PicoModel.RP2350:
                Rebooted = true;
                Acknowledge(input: true);
                break;
            default:
                throw Stall(commandId, 1);
        }

        return Task.FromResult(packet.Length);
    }

    public Task<byte[]> BulkInAsync(int length)
    {
        if (readData is { } data)
        {
            readData = null;
            Acknowledge(input: false);
            return Task.FromResult(data.Take(length).ToArray());
        }

        if (expectingAck && ackIn)
        {
            expectingAck = false;
            return Task.FromResult(Array.Empty<byte>());
        }

        throw new UsbStallException("Nothing to read.");
    }

    public Task CloseAsync()
    {
        Closed = true;
        return Task.CompletedTask;
    }

    void Acknowledge(bool input)
    {
        expectingAck = true;
        ackIn = input;
    }

    UsbStallException Stall(byte commandId, uint status)
    {
        lastCommand = commandId;
        lastStatus = status;
        return new UsbStallException($"Command 0x{commandId:X2} stalled.");
    }
}

/// <summary>
/// Hands an emulated boot ROM to the update, recording how it was asked for.
/// </summary>
sealed class EmulatedBootselConnector(EmulatedBootRom bootRom) : IBootselConnector
{
    public bool Prepared { get; private set; }

    public string? RequestedSerialNumber { get; private set; }

    public bool? ExcludedWaiting { get; private set; }

    public Task PrepareAsync()
    {
        Prepared = true;
        return Task.CompletedTask;
    }

    public Task<IPicobootInterface> ConnectAsync(string? serialNumber, bool excludeWaiting)
    {
        RequestedSerialNumber = serialNumber;
        ExcludedWaiting = excludeWaiting;
        return Task.FromResult<IPicobootInterface>(bootRom);
    }
}
