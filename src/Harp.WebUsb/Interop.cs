using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Harp.WebUsb;

[SupportedOSPlatform("browser")]
static partial class Interop
{
    internal const string ModuleName = "harp-webusb";

    [JSImport("isSupported", ModuleName)]
    internal static partial bool IsSupported();

    [JSImport("getPlatform", ModuleName)]
    internal static partial string GetPlatform();

    [JSImport("requestDevice", ModuleName)]
    internal static partial Task<JSObject?> RequestDeviceAsync(string? filtersJson);

    [JSImport("getDevices", ModuleName)]
    internal static partial Task<JSObject> GetDevicesAsync();

    [JSImport("getDeviceCount", ModuleName)]
    internal static partial int GetDeviceCount(JSObject devices);

    [JSImport("getDeviceAt", ModuleName)]
    internal static partial JSObject GetDeviceAt(JSObject devices, int index);

    [JSImport("getVendorId", ModuleName)]
    internal static partial int GetVendorId(JSObject device);

    [JSImport("getProductId", ModuleName)]
    internal static partial int GetProductId(JSObject device);

    [JSImport("getSerialNumber", ModuleName)]
    internal static partial string? GetSerialNumber(JSObject device);

    [JSImport("getProductName", ModuleName)]
    internal static partial string? GetProductName(JSObject device);

    [JSImport("isOpened", ModuleName)]
    internal static partial bool IsOpened(JSObject device);

    [JSImport("isSameDevice", ModuleName)]
    internal static partial bool IsSameDevice(JSObject a, JSObject b);

    [JSImport("open", ModuleName)]
    internal static partial Task OpenAsync(JSObject device);

    [JSImport("close", ModuleName)]
    internal static partial Task CloseAsync(JSObject device);

    [JSImport("reset", ModuleName)]
    internal static partial Task ResetAsync(JSObject device);

    [JSImport("findInterface", ModuleName)]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    internal static partial int[] FindInterface(JSObject device, int interfaceClass);

    [JSImport("claimInterface", ModuleName)]
    internal static partial Task ClaimInterfaceAsync(JSObject device, int interfaceNumber);

    [JSImport("releaseInterface", ModuleName)]
    internal static partial Task ReleaseInterfaceAsync(JSObject device, int interfaceNumber);

    [JSImport("clearHalt", ModuleName)]
    internal static partial Task ClearHaltAsync(JSObject device, string direction, int endpointNumber);

    [JSImport("controlTransferIn", ModuleName)]
    internal static partial Task<JSObject> ControlTransferInAsync(
        JSObject device, string requestType, string recipient, int request, int value, int index, int length);

    [JSImport("controlTransferOut", ModuleName)]
    internal static partial Task<JSObject> ControlTransferOutAsync(
        JSObject device, string requestType, string recipient, int request, int value, int index, JSObject? data);

    [JSImport("transferIn", ModuleName)]
    internal static partial Task<JSObject> TransferInAsync(JSObject device, int endpointNumber, int length);

    [JSImport("transferOut", ModuleName)]
    internal static partial Task<JSObject> TransferOutAsync(JSObject device, int endpointNumber, JSObject data);

    [JSImport("copyFrom", ModuleName)]
    internal static partial JSObject CopyFrom([JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);

    [JSImport("getStatus", ModuleName)]
    internal static partial string GetStatus(JSObject result);

    [JSImport("getLength", ModuleName)]
    internal static partial int GetLength(JSObject result);

    [JSImport("copyBytes", ModuleName)]
    internal static partial int CopyBytes(JSObject result, [JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);
}
