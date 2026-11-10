using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Harp.WebUsb;

/// <summary>
/// Specifies the type of a USB control request.
/// </summary>
public enum UsbRequestType
{
    /// <summary>A request defined by the USB specification.</summary>
    Standard,
    /// <summary>A request defined by a USB device class.</summary>
    Class,
    /// <summary>A request defined by the device vendor.</summary>
    Vendor
}

/// <summary>
/// Specifies the target of a USB control request.
/// </summary>
public enum UsbRecipient
{
    /// <summary>The device.</summary>
    Device,
    /// <summary>An interface of the device.</summary>
    Interface,
    /// <summary>An endpoint of the device.</summary>
    Endpoint,
    /// <summary>Another target.</summary>
    Other
}

/// <summary>
/// Specifies the direction of a USB endpoint.
/// </summary>
public enum UsbDirection
{
    /// <summary>From the device to the host.</summary>
    In,
    /// <summary>From the host to the device.</summary>
    Out
}

/// <summary>
/// Describes an interface with a bulk endpoint in each direction.
/// </summary>
/// <param name="InterfaceNumber">The number of the interface.</param>
/// <param name="OutEndpoint">The number of the bulk OUT endpoint, without the direction bit.</param>
/// <param name="InEndpoint">The number of the bulk IN endpoint, without the direction bit.</param>
public readonly record struct UsbBulkInterface(int InterfaceNumber, int OutEndpoint, int InEndpoint);

/// <summary>
/// The exception that is thrown when an endpoint stalls a transfer, which is how a device rejects a request.
/// </summary>
public sealed class UsbStallException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UsbStallException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public UsbStallException(string message) : base(message) { }
}

/// <summary>
/// The exception that is thrown when the browser cannot open a device or claim its interface,
/// usually because the operating system gives the browser no access to it.
/// </summary>
public sealed class UsbAccessException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UsbAccessException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The error reported by the browser.</param>
    public UsbAccessException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>
/// Represents a USB device the user has granted the page access to.
/// </summary>
/// <remarks>
/// WebUSB transfers have no timeout. Callers that need one wait on the returned task with a
/// timeout, and close the device to abandon a transfer that never completes.
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class WebUsbDevice
{
    readonly JSObject device;

    internal WebUsbDevice(JSObject device)
    {
        this.device = device;
        VendorId = (ushort)Interop.GetVendorId(device);
        ProductId = (ushort)Interop.GetProductId(device);
        SerialNumber = Interop.GetSerialNumber(device);
        ProductName = Interop.GetProductName(device);
    }

    /// <summary>
    /// Gets the USB vendor ID of the device.
    /// </summary>
    public ushort VendorId { get; }

    /// <summary>
    /// Gets the USB product ID of the device.
    /// </summary>
    public ushort ProductId { get; }

    /// <summary>
    /// Gets the serial number string of the device, or <see langword="null"/> if it reports none.
    /// </summary>
    public string? SerialNumber { get; }

    /// <summary>
    /// Gets the product name string of the device, or <see langword="null"/> if it reports none.
    /// </summary>
    public string? ProductName { get; }

    /// <summary>
    /// Gets a value indicating whether the device is open.
    /// </summary>
    public bool IsOpen => Interop.IsOpened(device);

    /// <summary>
    /// Returns a value indicating whether another object represents the same browser USB device.
    /// </summary>
    /// <param name="other">The device to compare with.</param>
    /// <returns><see langword="true"/> if both objects wrap the same device.</returns>
    public bool IsSameAs(WebUsbDevice other) => Interop.IsSameDevice(device, other.device);

    /// <summary>
    /// Asynchronously opens the device, and selects its first configuration if none is selected.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    /// <exception cref="UsbAccessException">The browser cannot open the device.</exception>
    public async Task OpenAsync()
    {
        try
        {
            await Interop.OpenAsync(device);
        }
        catch (JSException ex)
        {
            throw new UsbAccessException($"The device could not be opened. {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Asynchronously closes the device. Closing a device that is not open has no effect.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public async Task CloseAsync()
    {
        try
        {
            await Interop.CloseAsync(device);
        }
        catch (JSException)
        {
            // The device may already be gone, which closes it.
        }
    }

    /// <summary>
    /// Asynchronously resets the device.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public Task ResetAsync() => WrapAsync(() => Interop.ResetAsync(device));

    /// <summary>
    /// Returns the first interface of the specified class that has a bulk endpoint in each direction.
    /// </summary>
    /// <param name="interfaceClass">The USB class code of the interface, such as 0xFF for vendor-specific.</param>
    /// <returns>The interface, or <see langword="null"/> if the open device has none.</returns>
    public UsbBulkInterface? FindBulkInterface(int interfaceClass)
    {
        var result = Interop.FindInterface(device, interfaceClass);
        return result.Length == 3 ? new UsbBulkInterface(result[0], result[1], result[2]) : null;
    }

    /// <summary>
    /// Asynchronously claims an interface for exclusive use by the page.
    /// </summary>
    /// <param name="interfaceNumber">The number of the interface.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    /// <exception cref="UsbAccessException">The browser cannot claim the interface.</exception>
    public async Task ClaimInterfaceAsync(int interfaceNumber)
    {
        try
        {
            await Interop.ClaimInterfaceAsync(device, interfaceNumber);
        }
        catch (JSException ex)
        {
            throw new UsbAccessException($"Interface {interfaceNumber} of the device could not be claimed. {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Asynchronously releases a claimed interface.
    /// </summary>
    /// <param name="interfaceNumber">The number of the interface.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public Task ReleaseInterfaceAsync(int interfaceNumber) => WrapAsync(() => Interop.ReleaseInterfaceAsync(device, interfaceNumber));

    /// <summary>
    /// Asynchronously clears the halt condition of an endpoint.
    /// </summary>
    /// <param name="direction">The direction of the endpoint.</param>
    /// <param name="endpointNumber">The number of the endpoint, without the direction bit.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public Task ClearHaltAsync(UsbDirection direction, int endpointNumber) =>
        WrapAsync(() => Interop.ClearHaltAsync(device, direction == UsbDirection.In ? "in" : "out", endpointNumber));

    /// <summary>
    /// Asynchronously sends a control request with an optional data stage from the host.
    /// </summary>
    /// <param name="requestType">The type of the request.</param>
    /// <param name="recipient">The target of the request.</param>
    /// <param name="request">The request code.</param>
    /// <param name="value">The value field of the request.</param>
    /// <param name="index">The index field of the request.</param>
    /// <param name="data">The data to send, if any.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    /// <exception cref="UsbStallException">The device rejected the request.</exception>
    public async Task ControlTransferOutAsync(
        UsbRequestType requestType, UsbRecipient recipient, byte request, ushort value, ushort index, ReadOnlyMemory<byte> data = default)
    {
        using var buffer = data.IsEmpty ? null : Copy(data);
        using var result = await WrapAsync(() => Interop.ControlTransferOutAsync(
            device, ToString(requestType), ToString(recipient), request, value, index, buffer));
        CheckStatus(result, "control request");
    }

    /// <summary>
    /// Asynchronously sends a control request with a data stage from the device.
    /// </summary>
    /// <param name="requestType">The type of the request.</param>
    /// <param name="recipient">The target of the request.</param>
    /// <param name="request">The request code.</param>
    /// <param name="value">The value field of the request.</param>
    /// <param name="index">The index field of the request.</param>
    /// <param name="length">The maximum number of bytes to receive.</param>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains the bytes received.
    /// </returns>
    /// <exception cref="UsbStallException">The device rejected the request.</exception>
    public async Task<byte[]> ControlTransferInAsync(
        UsbRequestType requestType, UsbRecipient recipient, byte request, ushort value, ushort index, int length)
    {
        using var result = await WrapAsync(() => Interop.ControlTransferInAsync(
            device, ToString(requestType), ToString(recipient), request, value, index, length));
        CheckStatus(result, "control request");
        return ReadBytes(result);
    }

    /// <summary>
    /// Asynchronously sends data to a bulk or interrupt OUT endpoint.
    /// </summary>
    /// <param name="endpointNumber">The number of the endpoint, without the direction bit.</param>
    /// <param name="data">The data to send.</param>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains the number of bytes sent.
    /// </returns>
    /// <exception cref="UsbStallException">The endpoint stalled.</exception>
    public async Task<int> TransferOutAsync(int endpointNumber, ReadOnlyMemory<byte> data)
    {
        using var buffer = Copy(data);
        using var result = await WrapAsync(() => Interop.TransferOutAsync(device, endpointNumber, buffer));
        CheckStatus(result, $"transfer to endpoint {endpointNumber}");
        return Interop.GetLength(result);
    }

    /// <summary>
    /// Asynchronously receives data from a bulk or interrupt IN endpoint.
    /// </summary>
    /// <param name="endpointNumber">The number of the endpoint, without the direction bit.</param>
    /// <param name="length">The maximum number of bytes to receive.</param>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains the bytes received.
    /// </returns>
    /// <exception cref="UsbStallException">The endpoint stalled.</exception>
    public async Task<byte[]> TransferInAsync(int endpointNumber, int length)
    {
        using var result = await WrapAsync(() => Interop.TransferInAsync(device, endpointNumber, length));
        CheckStatus(result, $"transfer from endpoint {endpointNumber}");
        return ReadBytes(result);
    }

    static JSObject Copy(ReadOnlyMemory<byte> data)
    {
        // The memory view must be writable, so copy read-only memory into an array first.
        var bytes = data.ToArray();
        return Interop.CopyFrom(bytes);
    }

    static byte[] ReadBytes(JSObject result)
    {
        var bytes = new byte[Interop.GetLength(result)];
        Interop.CopyBytes(result, bytes);
        return bytes;
    }

    static void CheckStatus(JSObject result, string operation)
    {
        switch (Interop.GetStatus(result))
        {
            case "ok":
                return;
            case "stall":
                throw new UsbStallException($"The device stalled the {operation}.");
            default:
                throw new IOException($"The device sent more data than requested in the {operation}.");
        }
    }

    static async Task WrapAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (JSException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    static async Task<T> WrapAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (JSException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    static string ToString(UsbRequestType requestType) => requestType switch
    {
        UsbRequestType.Standard => "standard",
        UsbRequestType.Class => "class",
        _ => "vendor"
    };

    static string ToString(UsbRecipient recipient) => recipient switch
    {
        UsbRecipient.Device => "device",
        UsbRecipient.Interface => "interface",
        UsbRecipient.Endpoint => "endpoint",
        _ => "other"
    };
}
