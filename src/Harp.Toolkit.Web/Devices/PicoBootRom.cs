using Bonsai.Harp;
using Harp.Toolkit.Firmware;
using Harp.Toolkit.Firmware.Pico;
using PicoImage = Harp.Toolkit.Firmware.Pico.FirmwareImage;

namespace Harp.Toolkit.Web.Devices;

/// <summary>
/// Finds and opens the PICOBOOT interface of a device that restarted in BOOTSEL mode.
/// </summary>
public interface IBootselConnector
{
    /// <summary>
    /// Asynchronously remembers the devices already in BOOTSEL mode before the update restarts
    /// the device, so they are not mistaken for it.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    Task PrepareAsync();

    /// <summary>
    /// Asynchronously waits for a device in BOOTSEL mode and opens its PICOBOOT interface.
    /// </summary>
    /// <param name="serialNumber">
    /// The USB serial number expected of the device, or <see langword="null"/> if unknown.
    /// </param>
    /// <param name="excludeWaiting">
    /// <see langword="true"/> to ignore the devices that were already in BOOTSEL mode when
    /// <see cref="PrepareAsync"/> was called, because the update restarted the device.
    /// </param>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains the open interface.
    /// </returns>
    /// <exception cref="BootselNotFoundException">No device in BOOTSEL mode was found or selected.</exception>
    /// <exception cref="BootselAccessException">The device was found but the browser cannot open it.</exception>
    /// <exception cref="AmbiguousBootselDeviceException">Several devices are in BOOTSEL mode and none matches.</exception>
    Task<IPicobootInterface> ConnectAsync(string? serialNumber, bool excludeWaiting);
}

/// <summary>
/// The exception that is thrown when no device in BOOTSEL mode was found or selected.
/// </summary>
/// <param name="message">The message that describes the error.</param>
public sealed class BootselNotFoundException(string message) : TimeoutException(message);

/// <summary>
/// Updates the firmware of a device built on the Pico core through the PICOBOOT interface of the
/// RP2040 and RP2350 boot ROM.
/// </summary>
/// <remarks>
/// This follows <c>Harp.Toolkit.Firmware.Pico.BootRom</c>, which opens its serial port by name and
/// its USB device through libusb, so cannot run in the browser, step for step. Writing the
/// <c>UPDATE_FIRMWARE</c> bit of <c>R_RESET_DEV</c> restarts the device in BOOTSEL mode: its serial
/// port disappears and the boot ROM appears as a USB device with the PICOBOOT interface, which
/// writes the image and reads it back.
/// </remarks>
public static class PicoBootRom
{
    const int UidAddress = 16;
    const int UidLength = 16;
    const int BoardIdOffset = 8;
    const byte UpdateFirmware = 0x20;

    static readonly TimeSpan ResetReplyTimeout = TimeSpan.FromMilliseconds(200);
    static readonly TimeSpan DisconnectDelay = TimeSpan.FromMilliseconds(1500);
    static readonly TimeSpan StillRunningTimeout = TimeSpan.FromMilliseconds(500);
    const int VerifyChunkSize = PicoImage.SectorSize;

    /// <summary>
    /// Asynchronously updates the firmware of a Pico device.
    /// </summary>
    /// <param name="port">
    /// The serial port of the device, which must be closed, or <see langword="null"/> for a device
    /// already in BOOTSEL mode, which has no serial port.
    /// </param>
    /// <param name="firmware">The firmware image to write.</param>
    /// <param name="forceUpdate">
    /// <see langword="true"/> to write the image even if the device reports unsupported hardware or
    /// does not answer, as when it is already in BOOTSEL mode; otherwise, <see langword="false"/>.
    /// </param>
    /// <param name="connector">Finds and opens the device once it is in BOOTSEL mode.</param>
    /// <param name="progress">The optional object that receives update progress reports.</param>
    /// <param name="cancellationToken">
    /// The token that cancels the update. Cancellation is accepted only before the device is reset.
    /// </param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    /// <exception cref="UnsupportedImageException">
    /// The image does not target the microcontroller of the device. The device was restarted with
    /// its previous firmware.
    /// </exception>
    /// <exception cref="BootselRequestIgnoredException">
    /// The device kept running its firmware after the request to restart in BOOTSEL mode.
    /// </exception>
    public static async Task UpdateFirmwareAsync(
        IDevicePort? port,
        PicoFirmwareFile firmware,
        bool forceUpdate,
        IBootselConnector connector,
        IProgress<UpdateProgress>? progress = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(firmware);
        ArgumentNullException.ThrowIfNull(connector);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(UpdateStage.Connect, 0));
        await connector.PrepareAsync();

        string? serialNumber = null;
        var answered = false;
        var reset = false;
        var replied = false;
        if (port is not null)
        {
            try
            {
                var stream = await port.OpenAsync(PortMode.Protocol);
                await using var device = new HarpConnection(stream);
                progress?.Report(new(UpdateStage.Check, 10));
                // A file without metadata does not say which device it is for, so there is nothing to check.
                if (!forceUpdate && firmware.Metadata is { } metadata)
                {
                    var identity = await device.ReadIdentityAsync(cancellationToken);
                    if (!metadata.Supports(identity.DeviceName ?? string.Empty, identity.HardwareVersion))
                    {
                        throw new HarpException(
                            $"The firmware file is for {metadata.DeviceName} with hardware version " +
                            $"{metadata.HardwareVersion}, but the device on this port is {identity.DisplayName} " +
                            $"with hardware version {identity.HardwareVersion}.");
                    }
                }

                // A forced update skips the identity check, so the device may not have answered yet.
                (answered, serialNumber) = await TryReadSerialNumberAsync(device, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(UpdateStage.Reset, 20));
                reset = true;
                device.ReplyTimeout = ResetReplyTimeout;
                try
                {
                    await device.SendAsync(HarpCommand.WriteByte(ResetDevice.Address, UpdateFirmware));
                    replied = true;
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    // The device can restart before its reply goes out.
                }
            }
            catch (Exception ex) when (!reset && forceUpdate && ex is TimeoutException or IOException or InvalidOperationException)
            {
                // A device in BOOTSEL mode does not answer on its serial port. A forced update looks
                // for it among the devices in BOOTSEL mode instead.
            }
            finally
            {
                // The serial port disappears when the device restarts in BOOTSEL mode.
                try { await port.CloseAsync(); } catch (IOException) { }
            }

            if (replied)
            {
                await Task.Delay(DisconnectDelay);
                if (await IsStillRunningAsync(port))
                {
                    throw new BootselRequestIgnoredException(
                        "The device kept running after the request to restart in BOOTSEL mode, so its firmware " +
                        "does not implement UPDATE_FIRMWARE. Hold its BOOTSEL button while plugging it in, then update it in BOOTSEL mode.");
                }
            }
        }

        progress?.Report(new(UpdateStage.Bootloader, 30));
        // Devices already in BOOTSEL mode are ignored only when this device answered and was reset,
        // since a device that never answered may be one of them.
        var usb = await connector.ConnectAsync(serialNumber, excludeWaiting: reset && answered);
        try
        {
            var picoboot = new PicobootClient(usb);
            await picoboot.ResetAsync();
            var family = SelectFamily(firmware.Image, picoboot.Model);
            if (family is null)
            {
                try { await picoboot.RebootToApplicationAsync(); }
                catch (Exception ex) when (ex is IOException or HarpException or TimeoutException) { }
                throw new UnsupportedImageException(
                    $"The firmware image is for the {firmware.Targets}, but the device is an {picoboot.Model}. " +
                    "The device was restarted with its previous firmware.");
            }

            var sectors = firmware.Image.GetSectors(family.GetValueOrDefault());
            progress?.Report(new(UpdateStage.Write, 40));
            await WriteAndVerifyAsync(picoboot, sectors, progress);

            progress?.Report(new(UpdateStage.Restart, 90));
            await picoboot.RebootToApplicationAsync();
            progress?.Report(new(UpdateStage.Restart, 100));
        }
        finally
        {
            await usb.CloseAsync();
        }
    }

    /// <summary>
    /// Returns the USB serial number the boot ROM reports for a device with the specified unique ID.
    /// </summary>
    /// <param name="uid">The contents of the unique ID register of the device.</param>
    /// <returns>The serial number, or <see langword="null"/> if the unique ID carries no board ID.</returns>
    public static string? GetSerialNumber(byte[] uid)
    {
        if (uid.Length != UidLength)
            return null;

        var boardId = uid.AsSpan(BoardIdOffset);
        return boardId.IndexOfAnyExcept((byte)0) < 0 ? null : Convert.ToHexString(boardId);
    }

    static async Task<(bool Answered, string? SerialNumber)> TryReadSerialNumberAsync(HarpConnection device, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await device.SendAsync(HarpCommand.ReadByte(UidAddress), cancellationToken);
            return (true, reply.Error ? null : GetSerialNumber(reply.GetPayload().ToArray()));
        }
        catch (TimeoutException)
        {
            return (false, null);
        }
    }

    static async Task<bool> IsStillRunningAsync(IDevicePort port)
    {
        try
        {
            var stream = await port.OpenAsync(PortMode.Protocol).WaitAsync(StillRunningTimeout);
            await using var device = new HarpConnection(stream) { ReplyTimeout = StillRunningTimeout };
            await device.CommandAsync(HarpCommand.ReadUInt16(WhoAmI.Address));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or HarpException)
        {
            return false;
        }
        finally
        {
            try { await port.CloseAsync(); } catch (IOException) { }
        }
    }

    static uint? SelectFamily(PicoImage image, PicoModel model)
    {
        var supported = model == PicoModel.RP2040
            ? new[] { PicoImage.FamilyRP2040 }
            : new[] { PicoImage.FamilyRP2350ArmSecure, PicoImage.FamilyRP2350RiscV, PicoImage.FamilyRP2350ArmNonSecure };
        foreach (var family in supported)
        {
            if (image.Families.Contains(family))
                return family;
        }

        return null;
    }

    static async Task WriteAndVerifyAsync(PicobootClient picoboot, IReadOnlyList<FlashSector> sectors, IProgress<UpdateProgress>? progress)
    {
        await picoboot.TakeExclusiveAccessAsync();
        await picoboot.PrepareFlashWriteAsync();
        for (int i = 0; i < sectors.Count; i++)
        {
            var sector = sectors[i];
            await picoboot.EraseFlashAsync(sector.Address, (uint)sector.Data.Length);
            await picoboot.WriteMemoryAsync(sector.Address, sector.Data);
            progress?.Report(new(UpdateStage.Write, 40 + (i + 1) * 40 / sectors.Count));
        }

        await picoboot.PrepareFlashReadAsync();
        for (int i = 0; i < sectors.Count; i++)
        {
            var sector = sectors[i];
            for (int offset = 0; offset < sector.Data.Length; offset += VerifyChunkSize)
            {
                var size = Math.Min(VerifyChunkSize, sector.Data.Length - offset);
                var readBack = await picoboot.ReadMemoryAsync(sector.Address + (uint)offset, size);
                if (!readBack.AsSpan().SequenceEqual(sector.Data.AsSpan(offset, size)))
                {
                    throw new HarpException(
                        $"The device memory at 0x{sector.Address + (uint)offset:X8} does not match the firmware image after writing.");
                }
            }

            progress?.Report(new(UpdateStage.Write, 80 + (i + 1) * 10 / sectors.Count));
        }
    }
}
