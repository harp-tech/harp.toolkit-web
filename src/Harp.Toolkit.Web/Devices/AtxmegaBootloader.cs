using Bonsai.Harp;
using Harp.Toolkit.Firmware;

namespace Harp.Toolkit.Web.Devices;

/// <summary>
/// Updates the firmware of an ATxmega Harp device using its bootloader protocol.
/// </summary>
/// <remarks>
/// This follows <c>Harp.Toolkit.Firmware.ATxmega.Bootloader</c>, which opens its serial port by
/// name and so cannot run in the browser, step for step.
/// </remarks>
public static class AtxmegaBootloader
{
    /// <summary>
    /// The size, in bytes, of the pages in which Harp.Toolkit lays out firmware images.
    /// </summary>
    public const int ImagePageSize = 512;

    const int HeaderSize = 15;
    const int FlushDelayMilliseconds = 500;
    const int BootloaderTimeoutMilliseconds = 500;
    const int MaxAttempts = 3;

    const int WritePage = 0x0;
    const int ReadPageSize = 0x66;
    const int ExitBootloader = 0x77;

    const int MinPageSize = 64;
    const int MaxPageSize = 4096;

    const int NoError = 0;
    const int UndefinedError = 1;
    const int InvalidAddress = 2;
    const int InvalidDataLength = 3;

    /// <summary>
    /// Asynchronously updates the firmware of the device on the specified port.
    /// </summary>
    /// <param name="port">The port of the device. The port must be closed.</param>
    /// <param name="firmware">The firmware image to upload.</param>
    /// <param name="forceUpdate">
    /// <see langword="true"/> to upload the firmware even if the device reports unsupported
    /// hardware, or is already in bootloader mode; otherwise, <see langword="false"/>.
    /// </param>
    /// <param name="progress">The optional object that receives update progress reports.</param>
    /// <param name="cancellationToken">
    /// The token that cancels the update. Cancellation is accepted only before the device is reset,
    /// since a device in the bootloader stays there until an update completes.
    /// </param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public static async Task UpdateFirmwareAsync(
        IDevicePort port,
        AtxmegaFirmwareFile firmware,
        bool forceUpdate,
        IProgress<UpdateProgress>? progress = default,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(UpdateStage.Connect, 0));
        try
        {
            var stream = await port.OpenAsync(PortMode.Protocol);
            await using (var device = new HarpConnection(stream))
            {
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

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(UpdateStage.Reset, 20));
                var reset = await device.ReadResetDeviceAsync();
                if ((reset & ResetFlags.BootFromEeprom) != 0)
                {
                    await device.WriteResetDeviceAsync(ResetFlags.RestoreEeprom);
                }
                else if ((reset & ResetFlags.BootFromDefault) != 0)
                {
                    await device.WriteResetDeviceAsync(ResetFlags.RestoreDefault);
                }
                else throw new HarpException("The device is in an unexpected boot mode.");
            }
        }
        catch (Exception ex) when (ex is TimeoutException || ex is IOException)
        {
            if (!forceUpdate)
                throw;
        }
        finally
        {
            await port.CloseAsync();
        }

        await Task.Delay(FlushDelayMilliseconds);

        try
        {
            for (int i = 1; i <= MaxAttempts; i++)
            {
                try
                {
                    progress?.Report(new(UpdateStage.Bootloader, 30));
                    var bootloader = await port.OpenAsync(PortMode.Bootloader);
                    await Task.Delay(FlushDelayMilliseconds);
                    var pageSize = await ReadPageSizeAsync(bootloader);
                    progress?.Report(new(UpdateStage.Write, 40));

                    var bytesWritten = 0;
                    var reportSize = pageSize * 8;
                    var dataMessage = new byte[pageSize + HeaderSize];
                    while (bytesWritten < firmware.Image.Data.Length)
                    {
                        CreateBootloaderMessage(dataMessage, WritePage, bytesWritten, firmware.Image.Data, bytesWritten, pageSize);
                        await BootloaderCommandAsync(bootloader, dataMessage);
                        bytesWritten += pageSize;
                        if (bytesWritten % reportSize == 0)
                        {
                            var percent = 40 + bytesWritten * 50 / firmware.Image.Data.Length;
                            progress?.Report(new(UpdateStage.Write, percent));
                        }
                    }

                    progress?.Report(new(UpdateStage.Restart, 90));
                    CreateBootloaderMessage(dataMessage, ExitBootloader, 0, firmware.Image.Data, 0, pageSize);
                    await BootloaderCommandAsync(bootloader, dataMessage);
                    progress?.Report(new(UpdateStage.Restart, 100));
                    break;
                }
                catch (Exception ex) when (
                    i < MaxAttempts &&
                    (ex is IOException || ex is TimeoutException || ex is InvalidOperationException))
                {
                    await port.CloseAsync();
                    await Task.Delay(FlushDelayMilliseconds);
                }
            }
        }
        finally
        {
            await port.CloseAsync();
        }
    }

    static ushort GetMessageChecksum(byte[] messageBytes)
    {
        var checksum = (ushort)0;
        unchecked
        {
            for (int i = 0; i < messageBytes.Length - 2; i++)
            {
                checksum += messageBytes[i];
            }
        }
        return checksum;
    }

    static bool IsValidChecksum(byte[] messageBytes)
    {
        var checksum = GetMessageChecksum(messageBytes);
        var messageChecksum = (messageBytes[messageBytes.Length - 1] << 8) + messageBytes[messageBytes.Length - 2];
        return checksum == messageChecksum;
    }

    internal static void CreateBootloaderMessage(byte[] messageBytes, int opcode, int address, byte[] data, int offset, int count)
    {
        messageBytes[0] = 1;
        messageBytes[1] = 2;
        messageBytes[2] = 3;
        messageBytes[3] = (byte)opcode;
        messageBytes[4] = 0; // error
        messageBytes[5] = (byte)address;
        messageBytes[6] = (byte)(address >> 8);
        messageBytes[7] = (byte)(address >> 16);
        messageBytes[8] = (byte)(address >> 24);
        messageBytes[9] = (byte)count;
        messageBytes[10] = (byte)(count >> 8);
        messageBytes[11] = (byte)(count >> 16);
        messageBytes[12] = (byte)(count >> 24);
        Array.Copy(data, offset, messageBytes, 13, count);
        var checksum = GetMessageChecksum(messageBytes);
        messageBytes[messageBytes.Length - 2] = (byte)checksum;
        messageBytes[messageBytes.Length - 1] = (byte)(checksum >> 8);
    }

    static async Task<int> ReadPageSizeAsync(Stream stream)
    {
        var message = new byte[HeaderSize];
        CreateBootloaderMessage(message, ReadPageSize, 0, [], 0, 0);
        await BootloaderCommandAsync(stream, message);
        var pageSize = BitConverter.ToInt32(message, startIndex: 9);
        if (pageSize < MinPageSize || pageSize > MaxPageSize || (pageSize & (pageSize - 1)) != 0)
            throw new HarpException("The device reported an invalid bootloader page size.");

        return pageSize;
    }

    static async Task BootloaderCommandAsync(Stream stream, byte[] message)
    {
        var bytesRead = 0;
        var opcode = message[3];
        await stream.WriteAsync(message);
        using var timeout = new CancellationTokenSource(BootloaderTimeoutMilliseconds);
        try
        {
            while (bytesRead < message.Length)
            {
                var count = await stream.ReadAsync(message.AsMemory(bytesRead), timeout.Token);
                if (count == 0)
                    throw new IOException("The port closed while waiting for the bootloader.");
                bytesRead += count;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("There was a timeout while awaiting the device response.");
        }

        if (message[0] != 1 || message[1] != 2 || message[2] != 3 || message[3] != opcode)
            throw new HarpException("The device did not respond with a bootloader message.");

        if (!IsValidChecksum(message))
            throw new HarpException("The device responded with an invalid response checksum.");

        switch (message[4])
        {
            case NoError: break;
            case UndefinedError: throw new HarpException("The device reported an undefined error while updating the bootloader logic.");
            case InvalidAddress: throw new HarpException("The device reported an invalid address while updating the bootloader logic.");
            case InvalidDataLength: throw new HarpException("The device reported an invalid data length while writing the bootloader page.");
            default: throw new HarpException("The device reported an unknown error while updating the bootloader logic.");
        }
    }
}
