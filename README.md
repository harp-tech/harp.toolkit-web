# Harp.Toolkit Web

[Harp.Toolkit](https://github.com/harp-tech/toolkit) in the browser: generate device interfaces, talk to a connected Harp device, and update its firmware, with nothing to install.

| Page | What it does | Browsers |
|---|---|---|
| **Generate** | Generates the C# and Python interfaces and the firmware of a device from its `device.yml` | All |
| **Device** | Connects to a device over USB serial and builds a control panel for it from its `device.yml`: every register gets controls for its payload (a switch per flag for bit masks, choices for group masks, switches for booleans, text and number fields with their limits), filled from a live read. Also shows the device identity, switches it between Standby and Active, and logs every message it sends, decoded, with either its bytes or its frame header fields (length, port, payload type, checksum) | Chrome, Edge |
| **Firmware** | Updates the firmware of an ATxmega device from a `.hex` release file, or of a Pico (RP2040 or RP2350) device from a `.uf2` release file, as four steps: connect the device, choose the file, confirm (installed and new version side by side, with hardware and downgrade checks), and update. Pico images are read back to verify them before the device restarts. Also recovers a device left in its bootloader, or in BOOTSEL mode | Chrome, Edge |

The device pages use the [Web Serial API](https://developer.mozilla.org/docs/Web/API/Web_Serial_API), and Pico updates also the [WebUSB API](https://developer.mozilla.org/docs/Web/API/WebUSB_API). Only Chromium-based desktop browsers implement them, and only on pages served over HTTPS or from `localhost`.

### Updating Pico devices

The update restarts the device in BOOTSEL mode, where its serial port disappears and the boot ROM appears as a USB device. The first time, the browser asks the user to pick it (**RP2 Boot** or **RP2350 Boot**); afterwards it is found on its own. Its serial port can come back as a new port, which the page looks for among the ports granted earlier.

Some systems need a one-time setup before the browser can open the boot interface, the same as for `harp.toolkit update` ([platform setup](https://harp-tech.org/toolkit/articles/update.html#platform-setup)):

- **Windows:** the RP2040 boot interface needs the WinUSB driver. Install it with [Zadig](https://zadig.akeo.ie/): with the device in BOOTSEL mode, choose *Options › List All Devices*, select **RP2 Boot (Interface 1)**, choose **WinUSB** and install it. The RP2350 needs no driver. The Firmware page warns about this before updating an RP2040, and shows these steps when the device cannot be found or opened.
- **Linux:** the browser needs a udev rule to open the boot interface, which the page shows when the device cannot be opened.
- **macOS:** no setup.

A device left in BOOTSEL mode, for example by an interrupted update, shows up as a USB drive. Choose **Update a device in BOOTSEL mode** in the first step to write a `.uf2` file to it.

The Generate page started as a copy of [harp-tech/generators-web](https://github.com/harp-tech/generators-web).

## Run locally

```sh
dotnet run --project src/Harp.Toolkit.Web
dotnet test
```

The repository also holds two libraries meant to become their own packages: [Harp.WebSerial](src/Harp.WebSerial), which gives Blazor WebAssembly apps serial ports through Web Serial, and [Harp.WebUsb](src/Harp.WebUsb), which gives them USB devices through WebUSB.

## How it works

Everything runs as .NET in WebAssembly. The parts that cannot run in the browser are replaced:

- **Serial ports.** `System.IO.Ports` does not work in WebAssembly. [Harp.WebSerial](src/Harp.WebSerial) wraps Web Serial in a `Stream`.
- **Harp messages.** Bonsai.Harp reads its serial port synchronously on a dedicated thread, which the browser cannot do. [`HarpMessageReader`](src/Harp.Toolkit.Web/Devices/HarpMessageReader.cs) is an asynchronous port of its parser. [`HarpConnection`](src/Harp.Toolkit.Web/Devices/HarpConnection.cs) matches each command to its reply. Bonsai.Harp itself is still used for message types and core register definitions.
- **Firmware updates.** Harp.Toolkit.Core opens ports by name, so [`AtxmegaBootloader`](src/Harp.Toolkit.Web/Devices/AtxmegaBootloader.cs) follows its bootloader protocol step for step over an [`IDevicePort`](src/Harp.Toolkit.Web/Devices/IDevicePort.cs). For Pico devices, Harp.Toolkit.Core also reaches the boot ROM through libusb, so [`PicoBootRom`](src/Harp.Toolkit.Web/Devices/PicoBootRom.cs) follows its update step for step, and [`PicobootClient`](src/Harp.Toolkit.Web/Devices/PicobootClient.cs) sends the PICOBOOT commands through [Harp.WebUsb](src/Harp.WebUsb). Firmware files (Intel HEX and UF2) are still parsed by Harp.Toolkit.Core.
- **Register controls.** The generated C# interface cannot be compiled in the browser without shipping Roslyn, so [`DeviceSchema`](src/Harp.Toolkit.Web/Devices/DeviceSchema.cs) reads the same metadata Harp.Generators reads and lays out each payload the way the generated code does: member offsets, masks and shifts, bool and string members, and primitive types spanning several elements. [`PayloadCodec`](src/Harp.Toolkit.Web/Devices/PayloadCodec.cs) then decodes and encodes payloads with it. Members with a custom converter are shown as raw elements.
- **Device metadata.** The `device.yml` comes from the [whoami registry](https://github.com/harp-tech/whoami), looked up by WhoAmI when the device connects, from a file, or from the Generate tab for a device in development. Core registers come from the `core.yml` embedded in Harp.Generators. Metadata is loaded without Harp.Generators' validation, which rejects any register declaring a `minValue` above zero without a `defaultValue`, such as several Behavior registers (#4).

The protocol code depends only on `Stream` and, for PICOBOOT, on an interface over the USB transfers, so the tests run it against an emulated device and boot ROM on desktop .NET, including full firmware updates.

### Upstream changes that would remove the copies

Tracked in the [`upstream` issues](https://github.com/bruno-f-cruz/harp.toolkit-web/issues?q=label%3Aupstream):

- **Bonsai.Harp:** accept a `Stream` (or a transport interface) next to a port name, with an asynchronous receive loop. `HarpMessageReader` and `HarpConnection` could then be dropped (#1).
- **Harp.Toolkit.Core:** let the bootloaders take a port abstraction instead of a port name, and the Pico boot ROM a USB transport instead of libusb. `AtxmegaBootloader`, `PicoBootRom` and `PicobootClient` could then be dropped (#2). The firmware types (`ATxmegaFirmware`, `PicoFirmware`) are also created only from a file path, so the app pairs the metadata and image itself in [`FirmwareFile`](src/Harp.Toolkit.Web/Devices/FirmwareFile.cs); a `FromStream` would remove that.
- **Harp.Generators:** expose the payload layout at run time. `DeviceSchema` and `PayloadCodec` could then be dropped (#4).

## Not yet supported

- Device verification (`harp.toolkit verify`). Its checks live inside the command-line tool, and porting them meant copying them all; it will come back once they move into a library (#3).
- Updating the SoundCard PIC32 coprocessor. The Firmware page updates only the main processor of a SoundCard, and warns that the coprocessor image of the same release must be installed with `harp.toolkit update`.
- Spreadsheet to `device.yml` conversion.

## Automation

The GitHub workflows come from generators-web:

- Every hour, the device list is rebuilt from the whoami registry and the site is redeployed if it changed.
- Every day, a pull request is opened if a new Harp.Generators release is on NuGet.

They are disabled while the site is deployed by hand (#5).
