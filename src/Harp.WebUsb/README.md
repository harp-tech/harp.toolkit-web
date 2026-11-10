# Harp.WebUsb

USB device access for Blazor WebAssembly apps through the browser [WebUSB API](https://developer.mozilla.org/docs/Web/API/WebUSB_API), written to update the firmware of [Harp](https://harp-tech.org) devices built on the Pico core through the PICOBOOT interface of the RP2040 and RP2350 boot ROM.

libusb does not run in WebAssembly. This package gives you a `WebUsbDevice` with asynchronous control and bulk transfers, so a protocol written over libusb transfers can run in the browser.

## Browser support

WebUSB is available only in Chromium-based desktop browsers (Chrome, Edge, Opera), and only on pages served over HTTPS or from `localhost`. Check `UsbDevices.IsSupported` before using it.

The browser opens a device only when the operating system lets it:

- **Windows:** the interface needs the WinUSB driver. Devices that ask for it in their descriptors get it automatically; others need it installed, for example with [Zadig](https://zadig.akeo.ie/). `UsbDevices.IsWindows` tells you when to say so.
- **Linux:** the user needs permission on the device node, usually through a udev rule with `TAG+="uaccess"`.
- **macOS:** no setup, as long as no kernel driver claims the interface.

Opening or claiming fails with `UsbAccessException` when access is missing.

## Usage

Load the JavaScript module once at startup, in `Program.cs`:

```csharp
var host = builder.Build();
await UsbDevices.InitializeAsync();
await host.RunAsync();
```

Ask the user for a device in response to a click. The device keeps its permission, so later visits find it with `GetDevicesAsync` without asking:

```csharp
var device = await UsbDevices.RequestDeviceAsync(new WebUsbDeviceFilter(0x2E8A, 0x0003));
if (device is null) return; // the user picked nothing

await device.OpenAsync();
var picoboot = device.FindBulkInterface(interfaceClass: 0xFF)
    ?? throw new InvalidOperationException("No vendor interface.");
await device.ClaimInterfaceAsync(picoboot.InterfaceNumber);

await device.TransferOutAsync(picoboot.OutEndpoint, command);
var reply = await device.TransferInAsync(picoboot.InEndpoint, 64);

await device.ReleaseInterfaceAsync(picoboot.InterfaceNumber);
await device.CloseAsync();
```

A transfer the device rejects throws `UsbStallException`. WebUSB transfers have no timeout, so wait on the returned task with one (`WaitAsync`) and close the device to abandon a transfer that never completes.
