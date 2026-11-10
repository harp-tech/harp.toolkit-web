using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;

namespace Harp.WebUsb;

/// <summary>
/// Provides access to the USB devices of the browser through the WebUSB API.
/// </summary>
/// <remarks>
/// WebUSB is available only in Chromium-based desktop browsers, such as Chrome and Edge, and only
/// on pages served over HTTPS or from localhost. Call <see cref="InitializeAsync"/> once before
/// using any other member.
/// </remarks>
[SupportedOSPlatform("browser")]
public static class UsbDevices
{
    /// <summary>
    /// The default URL of the JavaScript module, relative to the <c>_framework</c> folder of the app.
    /// </summary>
    public const string DefaultModuleUrl = "../_content/Harp.WebUsb/harp-webusb.js";

    static Task? initialization;

    /// <summary>
    /// Asynchronously loads the JavaScript module used to access the WebUSB API.
    /// </summary>
    /// <param name="moduleUrl">
    /// The URL of the JavaScript module. The default URL suits apps that reference this package.
    /// </param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    /// <remarks>Calling this method more than once loads the module only once.</remarks>
    public static Task InitializeAsync(string moduleUrl = DefaultModuleUrl)
    {
        return initialization ??= JSHost.ImportAsync(Interop.ModuleName, moduleUrl);
    }

    /// <summary>
    /// Gets a value indicating whether the browser supports the WebUSB API.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            EnsureInitialized();
            return Interop.IsSupported();
        }
    }

    /// <summary>
    /// Gets the operating system the browser reports, such as <c>Windows</c>, <c>macOS</c> or <c>Linux</c>.
    /// </summary>
    /// <remarks>
    /// Whether a device can be opened depends on the operating system: Windows opens only devices
    /// with a WinUSB driver, and Linux only devices the user has permission to access.
    /// </remarks>
    public static string Platform
    {
        get
        {
            EnsureInitialized();
            return Interop.GetPlatform();
        }
    }

    /// <summary>
    /// Gets a value indicating whether the browser runs on Windows.
    /// </summary>
    public static bool IsWindows => Platform.StartsWith("Win", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a value indicating whether the browser runs on Linux.
    /// </summary>
    public static bool IsLinux => Platform.Contains("Linux", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asynchronously asks the user to select a USB device, and grants the page access to it.
    /// </summary>
    /// <param name="filters">The filters that limit which devices the browser offers to the user.</param>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains the selected device, or <see langword="null"/> if the user selected none.
    /// </returns>
    /// <remarks>
    /// Browsers allow this call only in response to a user action, such as a button click.
    /// </remarks>
    public static async Task<WebUsbDevice?> RequestDeviceAsync(params WebUsbDeviceFilter[] filters)
    {
        EnsureInitialized();
        var device = await Interop.RequestDeviceAsync(filters.Length > 0 ? ToJson(filters) : null);
        return device is null ? null : new WebUsbDevice(device);
    }

    /// <summary>
    /// Asynchronously returns the attached USB devices the user has already granted the page access to.
    /// </summary>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains the devices the page can open without asking the user.
    /// </returns>
    public static async Task<WebUsbDevice[]> GetDevicesAsync()
    {
        EnsureInitialized();
        using var devices = await Interop.GetDevicesAsync();
        var result = new WebUsbDevice[Interop.GetDeviceCount(devices)];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new WebUsbDevice(Interop.GetDeviceAt(devices, i));
        }
        return result;
    }

    internal static void EnsureInitialized()
    {
        if (initialization is not { IsCompletedSuccessfully: true })
        {
            throw new InvalidOperationException(
                $"The WebUSB module is not loaded. Await {nameof(UsbDevices)}.{nameof(InitializeAsync)} first.");
        }
    }

    static string ToJson(WebUsbDeviceFilter[] filters)
    {
        var json = new StringBuilder("[");
        for (int i = 0; i < filters.Length; i++)
        {
            if (i > 0) json.Append(',');
            json.Append("{\"vendorId\":").Append(filters[i].VendorId);
            if (filters[i].ProductId is ushort productId)
            {
                json.Append(",\"productId\":").Append(productId);
            }
            json.Append('}');
        }
        return json.Append(']').ToString();
    }
}

/// <summary>
/// Represents a USB filter that limits which devices the browser offers when requesting a device.
/// </summary>
/// <param name="VendorId">The USB vendor ID of the device.</param>
/// <param name="ProductId">The optional USB product ID of the device.</param>
public readonly record struct WebUsbDeviceFilter(ushort VendorId, ushort? ProductId = null);
