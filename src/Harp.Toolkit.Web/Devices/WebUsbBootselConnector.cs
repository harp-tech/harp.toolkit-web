using System.Diagnostics;
using System.Runtime.Versioning;
using Bonsai.Harp;
using Harp.Toolkit.Firmware.Pico;
using Harp.WebUsb;

namespace Harp.Toolkit.Web.Devices;

/// <summary>
/// Finds a device in BOOTSEL mode through WebUSB.
/// </summary>
/// <remarks>
/// The page can open without asking only the devices the user granted it earlier. When none of
/// them appears in BOOTSEL mode, the connector calls <c>requestDevice</c>, which must ask the user
/// to pick the device in response to a click, since browsers allow the device chooser only then.
/// Once granted, a device keeps its permission, so later updates of the same device need no click.
/// </remarks>
/// <param name="requestDevice">
/// Asks the user to pick the device in BOOTSEL mode, and returns it, or <see langword="null"/> if
/// the user picked none.
/// </param>
[SupportedOSPlatform("browser")]
public sealed class WebUsbBootselConnector(Func<Task<WebUsbDevice?>> requestDevice) : IBootselConnector
{
    const int VendorSpecificClass = 0xFF;

    static readonly TimeSpan GrantedDeviceTimeout = TimeSpan.FromSeconds(4);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets the filters that match the RP2040 and RP2350 boot ROMs.
    /// </summary>
    public static WebUsbDeviceFilter[] Filters { get; } =
    [
        new(PicobootClient.VendorId, PicobootClient.ProductIdRP2040),
        new(PicobootClient.VendorId, PicobootClient.ProductIdRP2350)
    ];

    static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    WebUsbDevice[] waiting = [];

    /// <inheritdoc/>
    public async Task PrepareAsync()
    {
        waiting = await GetBootselDevicesAsync();
    }

    /// <inheritdoc/>
    public async Task<IPicobootInterface> ConnectAsync(string? serialNumber, bool excludeWaiting)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            var candidates = (await GetBootselDevicesAsync())
                .Where(device => !excludeWaiting || !waiting.Any(device.IsSameAs))
                .ToList();
            if (Select(candidates, serialNumber) is { } match)
                return await OpenAsync(match);
            await Task.Delay(PollInterval);
        }
        while (stopwatch.Elapsed < GrantedDeviceTimeout);

        var picked = await requestDevice() ?? throw new BootselNotFoundException(
            "No device in BOOTSEL mode was selected.");
        return await OpenAsync(picked);
    }

    /// <summary>
    /// Asynchronously returns the granted devices that are in BOOTSEL mode.
    /// </summary>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains the devices.
    /// </returns>
    public static async Task<WebUsbDevice[]> GetBootselDevicesAsync()
    {
        var devices = await UsbDevices.GetDevicesAsync();
        return devices.Where(device => PicobootClient.GetModel(device.VendorId, device.ProductId) is not null).ToArray();
    }

    static WebUsbDevice? Select(List<WebUsbDevice> candidates, string? serialNumber)
    {
        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];
        if (serialNumber is not null &&
            candidates.FirstOrDefault(device => string.Equals(device.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase)) is { } match)
            return match;

        throw new AmbiguousBootselDeviceException(
            "Several devices are in BOOTSEL mode and none could be matched to this device, so the update " +
            "stopped without writing to any of them. Disconnect the other devices and update again.");
    }

    /// <summary>
    /// Asynchronously runs one WebUSB call with a timeout, since WebUSB calls have none, logging
    /// it to the browser console so a stuck step can be told apart.
    /// </summary>
    /// <param name="step">What the call does, for the log and the error message.</param>
    /// <param name="operation">The call.</param>
    /// <param name="timeout">The time to wait for the call to complete.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    /// <exception cref="TimeoutException">The call did not complete in time.</exception>
    internal static async Task StepAsync(string step, Func<Task> operation, TimeSpan timeout)
    {
        Console.WriteLine($"[picoboot] {step}…");
        try
        {
            await operation().WaitAsync(timeout);
            Console.WriteLine($"[picoboot] {step}: done");
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"[picoboot] {step}: no answer after {timeout.TotalSeconds:0} s");
            throw new TimeoutException($"The device did not answer when asked to {step}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[picoboot] {step}: failed: {ex.Message}");
            throw;
        }
    }

    static async Task<IPicobootInterface> OpenAsync(WebUsbDevice device)
    {
        var model = PicobootClient.GetModel(device.VendorId, device.ProductId)
            ?? throw new HarpException("The selected device is not an RP2040 or RP2350 in BOOTSEL mode.");
        Console.WriteLine($"[picoboot] {model} {device.ProductName} (serial {device.SerialNumber ?? "none"})");
        try
        {
            await StepAsync("open the device", device.OpenAsync, OpenTimeout);
            var picoboot = device.FindBulkInterface(VendorSpecificClass)
                ?? throw new HarpException("The device in BOOTSEL mode has no PICOBOOT interface.");
            await StepAsync($"claim interface {picoboot.InterfaceNumber}", () => device.ClaimInterfaceAsync(picoboot.InterfaceNumber), OpenTimeout);
            return new WebUsbPicobootInterface(device, picoboot, model);
        }
        catch (UsbAccessException ex)
        {
            await device.CloseAsync();
            throw new BootselAccessException(
                $"The browser cannot open the {model} boot interface of the device in BOOTSEL mode. {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            // A device the operating system gives the browser no access to can also leave the
            // request unanswered, so report it as an access failure.
            await device.CloseAsync();
            throw new BootselAccessException(
                $"The browser did not manage to open the {model} boot interface of the device in BOOTSEL mode. {ex.Message}", ex);
        }
        catch
        {
            await device.CloseAsync();
            throw;
        }
    }
}

/// <summary>
/// Sends PICOBOOT transfers to a boot ROM through WebUSB.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WebUsbPicobootInterface : IPicobootInterface
{
    const byte InterfaceReset = 0x41;
    const byte InterfaceCommandStatus = 0x42;
    const int StatusSize = 16;

    readonly WebUsbDevice device;
    readonly UsbBulkInterface endpoints;

    internal WebUsbPicobootInterface(WebUsbDevice device, UsbBulkInterface endpoints, PicoModel model)
    {
        this.device = device;
        this.endpoints = endpoints;
        Model = model;
    }

    /// <inheritdoc/>
    public PicoModel Model { get; }

    /// <inheritdoc/>
    public string? SerialNumber => device.SerialNumber;

    /// <inheritdoc/>
    public async Task ResetInterfaceAsync()
    {
        // Clearing a halt is best effort, as in picotool: a boot ROM that does not answer it can
        // still accept the interface reset.
        try { await WebUsbBootselConnector.StepAsync("clear the IN endpoint", () => device.ClearHaltAsync(UsbDirection.In, endpoints.InEndpoint), StepTimeout); }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        try { await WebUsbBootselConnector.StepAsync("clear the OUT endpoint", () => device.ClearHaltAsync(UsbDirection.Out, endpoints.OutEndpoint), StepTimeout); }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        await WebUsbBootselConnector.StepAsync("reset the PICOBOOT interface", () => device.ControlTransferOutAsync(
            UsbRequestType.Vendor, UsbRecipient.Interface, InterfaceReset, 0, (ushort)endpoints.InterfaceNumber), StepTimeout);
    }

    static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(2);

    /// <inheritdoc/>
    public Task<byte[]> ReadCommandStatusAsync() => device.ControlTransferInAsync(
        UsbRequestType.Vendor, UsbRecipient.Interface, InterfaceCommandStatus, 0, (ushort)endpoints.InterfaceNumber, StatusSize);

    /// <inheritdoc/>
    public Task<int> BulkOutAsync(ReadOnlyMemory<byte> data) => device.TransferOutAsync(endpoints.OutEndpoint, data);

    /// <inheritdoc/>
    public Task<byte[]> BulkInAsync(int length) => device.TransferInAsync(endpoints.InEndpoint, length);

    /// <inheritdoc/>
    public async Task CloseAsync()
    {
        try { await device.ReleaseInterfaceAsync(endpoints.InterfaceNumber).WaitAsync(StepTimeout); }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        try { await device.CloseAsync().WaitAsync(StepTimeout); } catch (TimeoutException) { }
    }
}
