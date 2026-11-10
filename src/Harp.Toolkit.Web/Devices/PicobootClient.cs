using System.Buffers.Binary;
using Bonsai.Harp;
using Harp.WebUsb;

namespace Harp.Toolkit.Web.Devices;

/// <summary>
/// Specifies the microcontroller of a device in BOOTSEL mode.
/// </summary>
public enum PicoModel
{
    /// <summary>The RP2040.</summary>
    RP2040,
    /// <summary>The RP2350.</summary>
    RP2350
}

/// <summary>
/// Represents the USB transfers on the PICOBOOT interface of a boot ROM.
/// </summary>
/// <remarks>
/// Transfers that the device rejects throw <see cref="UsbStallException"/>. The browser
/// implementation is <see cref="WebUsbPicobootInterface"/>; tests use an emulated boot ROM.
/// </remarks>
public interface IPicobootInterface
{
    /// <summary>
    /// Gets the microcontroller of the device.
    /// </summary>
    PicoModel Model { get; }

    /// <summary>
    /// Gets the USB serial number of the device, or <see langword="null"/> if it reports none.
    /// </summary>
    string? SerialNumber { get; }

    /// <summary>
    /// Asynchronously clears both endpoints and sends the PICOBOOT interface reset request.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    Task ResetInterfaceAsync();

    /// <summary>
    /// Asynchronously reads the 16-byte status of the last command.
    /// </summary>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains the status.
    /// </returns>
    Task<byte[]> ReadCommandStatusAsync();

    /// <summary>
    /// Asynchronously sends data to the bulk OUT endpoint.
    /// </summary>
    /// <param name="data">The data to send.</param>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains the number of bytes sent.
    /// </returns>
    Task<int> BulkOutAsync(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Asynchronously receives data from the bulk IN endpoint.
    /// </summary>
    /// <param name="length">The number of bytes to receive.</param>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains the bytes received.
    /// </returns>
    Task<byte[]> BulkInAsync(int length);

    /// <summary>
    /// Asynchronously releases the interface and closes the device.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    Task CloseAsync();
}

/// <summary>
/// Sends PICOBOOT commands to the RP2040 or RP2350 boot ROM.
/// </summary>
/// <remarks>
/// This follows the internal <c>Harp.Toolkit.Firmware.Pico.PicobootDevice</c>, which talks to the
/// device through libusb and so cannot run in the browser, command for command. Every command is a
/// 32-byte packet on the bulk OUT endpoint, followed by an optional data phase and an
/// acknowledgement in the opposite direction to the data.
/// </remarks>
public sealed class PicobootClient(IPicobootInterface usb)
{
    /// <summary>The USB vendor ID of the boot ROM.</summary>
    public const ushort VendorId = 0x2E8A;

    /// <summary>The USB product ID of the RP2040 boot ROM.</summary>
    public const ushort ProductIdRP2040 = 0x0003;

    /// <summary>The USB product ID of the RP2350 boot ROM.</summary>
    public const ushort ProductIdRP2350 = 0x000F;

    /// <summary>The PICOBOOT magic number that starts every command.</summary>
    public const uint Magic = 0x431FD10B;

    /// <summary>The size of a command packet.</summary>
    public const int CommandSize = 32;

    /// <summary>The command that takes exclusive access to flash.</summary>
    public const byte ExclusiveAccess = 0x01;
    /// <summary>The RP2040 reboot command.</summary>
    public const byte Reboot = 0x02;
    /// <summary>The command that erases a range of flash.</summary>
    public const byte FlashErase = 0x03;
    /// <summary>The command that reads memory.</summary>
    public const byte Read = 0x84;
    /// <summary>The command that writes memory.</summary>
    public const byte Write = 0x05;
    /// <summary>The command that leaves XIP mode.</summary>
    public const byte ExitXip = 0x06;
    /// <summary>The command that enters XIP mode.</summary>
    public const byte EnterCommandXip = 0x07;
    /// <summary>The RP2350 reboot command.</summary>
    public const byte Reboot2 = 0x0A;

    const byte Exclusive = 1;
    const uint RP2040StackPointer = 0x20042000;
    const uint RebootDelayMilliseconds = 500;

    static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan DataTimeout = TimeSpan.FromSeconds(10);

    uint token = 1;

    /// <summary>
    /// Gets the microcontroller of the device.
    /// </summary>
    public PicoModel Model => usb.Model;

    /// <summary>
    /// Gets the USB serial number of the device, or <see langword="null"/> if it reports none.
    /// </summary>
    public string? SerialNumber => usb.SerialNumber;

    /// <summary>
    /// Returns the microcontroller of a boot ROM with the specified USB IDs.
    /// </summary>
    /// <param name="vendorId">The USB vendor ID.</param>
    /// <param name="productId">The USB product ID.</param>
    /// <returns>The microcontroller, or <see langword="null"/> if the IDs are not those of a boot ROM.</returns>
    public static PicoModel? GetModel(ushort vendorId, ushort productId) =>
        vendorId != VendorId ? null :
        productId == ProductIdRP2040 ? PicoModel.RP2040 :
        productId == ProductIdRP2350 ? PicoModel.RP2350 : null;

    /// <summary>
    /// Creates the 32-byte packet of a PICOBOOT command.
    /// </summary>
    /// <param name="token">The token that identifies the command.</param>
    /// <param name="commandId">The command.</param>
    /// <param name="transferLength">The length of the data phase.</param>
    /// <param name="arguments">The arguments of the command, at most 16 bytes.</param>
    /// <returns>The command packet.</returns>
    public static byte[] CreateCommand(uint token, byte commandId, uint transferLength, ReadOnlySpan<byte> arguments)
    {
        if (arguments.Length > 16) throw new ArgumentOutOfRangeException(nameof(arguments));
        var command = new byte[CommandSize];
        var span = command.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], token);
        command[8] = commandId;
        command[9] = (byte)arguments.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], transferLength);
        arguments.CopyTo(span[16..]);
        return command;
    }

    /// <summary>
    /// Asynchronously resets the PICOBOOT interface, discarding any command left over from an
    /// earlier connection.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public Task ResetAsync() => usb.ResetInterfaceAsync();

    /// <summary>
    /// Asynchronously takes exclusive access to flash, which stops the mass storage drive from writing it.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public Task TakeExclusiveAccessAsync() => ExecuteAsync(ExclusiveAccess, [Exclusive]);

    /// <summary>
    /// Asynchronously prepares flash for erase and write commands.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public Task PrepareFlashWriteAsync() => ExecuteAsync(ExitXip, []);

    /// <summary>
    /// Asynchronously prepares flash for read commands after a write.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    /// <remarks>
    /// The RP2040 reads flash back through XIP. The RP2350 reads flash back while XIP is off, and
    /// reads only zeros through XIP.
    /// </remarks>
    public Task PrepareFlashReadAsync() => ExecuteAsync(Model == PicoModel.RP2040 ? EnterCommandXip : ExitXip, []);

    /// <summary>
    /// Asynchronously erases a range of flash.
    /// </summary>
    /// <param name="address">The address of the first sector.</param>
    /// <param name="size">The size of the range, a multiple of the sector size.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public Task EraseFlashAsync(uint address, uint size) => ExecuteAsync(FlashErase, RangeArguments(address, size));

    /// <summary>
    /// Asynchronously writes memory.
    /// </summary>
    /// <param name="address">The address of the first byte.</param>
    /// <param name="data">The data to write.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public Task WriteMemoryAsync(uint address, byte[] data) =>
        ExecuteAsync(Write, RangeArguments(address, (uint)data.Length), data);

    /// <summary>
    /// Asynchronously reads memory.
    /// </summary>
    /// <param name="address">The address of the first byte.</param>
    /// <param name="size">The number of bytes to read.</param>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains the bytes read.
    /// </returns>
    public async Task<byte[]> ReadMemoryAsync(uint address, int size)
    {
        var data = new byte[size];
        await ExecuteAsync(Read, RangeArguments(address, (uint)size), data);
        return data;
    }

    /// <summary>
    /// Asynchronously reboots the device into the application in flash, after a short delay that
    /// lets the command be acknowledged.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public Task RebootToApplicationAsync()
    {
        var arguments = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(arguments, 0);
        if (Model == PicoModel.RP2040)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(4), RP2040StackPointer);
            BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(8), RebootDelayMilliseconds);
            return ExecuteAsync(Reboot, arguments.AsSpan(0, 12).ToArray());
        }

        BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(4), RebootDelayMilliseconds);
        return ExecuteAsync(Reboot2, arguments);
    }

    static byte[] RangeArguments(uint address, uint size)
    {
        var arguments = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(arguments, address);
        BinaryPrimitives.WriteUInt32LittleEndian(arguments.AsSpan(4), size);
        return arguments;
    }

    async Task ExecuteAsync(byte commandId, byte[] arguments, byte[]? data = null)
    {
        var transferLength = data?.Length ?? 0;
        var command = CreateCommand(token++, commandId, (uint)transferLength, arguments);
        var isRead = (commandId & 0x80) != 0;
        try
        {
            await usb.BulkOutAsync(command).WaitAsync(CommandTimeout);
            if (data is not null && data.Length > 0)
            {
                if (isRead)
                {
                    var received = await usb.BulkInAsync(data.Length).WaitAsync(DataTimeout);
                    if (received.Length != data.Length)
                        throw new IOException("The PICOBOOT data transfer was incomplete.");
                    received.CopyTo(data, 0);
                }
                else
                {
                    var sent = await usb.BulkOutAsync(data).WaitAsync(DataTimeout);
                    if (sent != data.Length)
                        throw new IOException("The PICOBOOT data transfer was incomplete.");
                }
            }

            // The acknowledgement goes in the direction opposite to the data, as one byte like
            // picotool and Harp.Toolkit send it.
            var ackTimeout = transferLength == 0 ? DataTimeout : CommandTimeout;
            if (isRead)
                await usb.BulkOutAsync(new byte[1]).WaitAsync(ackTimeout);
            else
                await usb.BulkInAsync(1).WaitAsync(ackTimeout);
        }
        catch (UsbStallException)
        {
            var status = await usb.ReadCommandStatusAsync();
            var statusCode = BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(4));
            throw new HarpException(
                $"The boot ROM rejected PICOBOOT command 0x{status[8]:X2} with status {statusCode}.");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The boot ROM did not answer PICOBOOT command 0x{commandId:X2} in time.");
        }
    }
}
