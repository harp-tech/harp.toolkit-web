// WebUSB bindings for Harp.WebUsb. Errors are rethrown with their DOMException name in the
// message, so the .NET side can tell an access failure from a disconnection.

function rethrow(e) {
    if (e && e.name && e.message && !e.message.startsWith(e.name)) {
        throw new Error(`${e.name}: ${e.message}`);
    }
    throw e;
}

export function isSupported() {
    return typeof navigator !== "undefined" && "usb" in navigator;
}

export function getPlatform() {
    if (typeof navigator === "undefined") return "";
    return navigator.userAgentData?.platform || navigator.platform || "";
}

export async function requestDevice(filtersJson) {
    const filters = filtersJson ? JSON.parse(filtersJson) : [];
    try {
        return await navigator.usb.requestDevice({ filters });
    } catch (e) {
        // The user dismissed the device chooser, or no device matched the filters.
        if (e.name === "NotFoundError") return null;
        rethrow(e);
    }
}

export async function getDevices() {
    return await navigator.usb.getDevices();
}

export function getDeviceCount(devices) {
    return devices.length;
}

export function getDeviceAt(devices, index) {
    return devices[index];
}

export function getVendorId(device) {
    return device.vendorId;
}

export function getProductId(device) {
    return device.productId;
}

export function getSerialNumber(device) {
    return device.serialNumber ?? null;
}

export function getProductName(device) {
    return device.productName ?? null;
}

export function isOpened(device) {
    return device.opened;
}

export function isSameDevice(a, b) {
    return a === b;
}

export async function open(device) {
    try {
        await device.open();
        if (device.configuration === null) {
            await device.selectConfiguration(1);
        }
    } catch (e) {
        rethrow(e);
    }
}

export async function close(device) {
    try {
        if (device.opened) await device.close();
    } catch (e) {
        rethrow(e);
    }
}

export async function reset(device) {
    try {
        await device.reset();
    } catch (e) {
        rethrow(e);
    }
}

// Returns [interfaceNumber, bulkOutEndpoint, bulkInEndpoint] for the first interface of the
// specified class with a bulk endpoint in each direction, or an empty array.
export function findInterface(device, interfaceClass) {
    const configuration = device.configuration;
    if (!configuration) return [];
    for (const usbInterface of configuration.interfaces) {
        const alternate = usbInterface.alternate ?? usbInterface.alternates[0];
        if (!alternate || alternate.interfaceClass !== interfaceClass) continue;
        const bulkOut = alternate.endpoints.find(e => e.type === "bulk" && e.direction === "out");
        const bulkIn = alternate.endpoints.find(e => e.type === "bulk" && e.direction === "in");
        if (bulkOut && bulkIn) {
            return [usbInterface.interfaceNumber, bulkOut.endpointNumber, bulkIn.endpointNumber];
        }
    }
    return [];
}

export async function claimInterface(device, interfaceNumber) {
    try {
        await device.claimInterface(interfaceNumber);
    } catch (e) {
        rethrow(e);
    }
}

export async function releaseInterface(device, interfaceNumber) {
    try {
        await device.releaseInterface(interfaceNumber);
    } catch (e) {
        rethrow(e);
    }
}

export async function clearHalt(device, direction, endpointNumber) {
    try {
        await device.clearHalt(direction, endpointNumber);
    } catch (e) {
        rethrow(e);
    }
}

function setup(requestType, recipient, request, value, index) {
    return { requestType, recipient, request, value, index };
}

// Transfers return a result object read by the functions below: { status, bytes }.

export async function controlTransferIn(device, requestType, recipient, request, value, index, length) {
    try {
        const result = await device.controlTransferIn(setup(requestType, recipient, request, value, index), length);
        return { status: result.status, bytes: toBytes(result.data) };
    } catch (e) {
        rethrow(e);
    }
}

export async function controlTransferOut(device, requestType, recipient, request, value, index, data) {
    try {
        const result = data && data.length > 0
            ? await device.controlTransferOut(setup(requestType, recipient, request, value, index), data)
            : await device.controlTransferOut(setup(requestType, recipient, request, value, index));
        return { status: result.status, bytes: null, written: result.bytesWritten };
    } catch (e) {
        rethrow(e);
    }
}

export async function transferIn(device, endpointNumber, length) {
    try {
        const result = await device.transferIn(endpointNumber, length);
        return { status: result.status, bytes: toBytes(result.data) };
    } catch (e) {
        rethrow(e);
    }
}

export async function transferOut(device, endpointNumber, data) {
    try {
        const result = await device.transferOut(endpointNumber, data);
        return { status: result.status, bytes: null, written: result.bytesWritten };
    } catch (e) {
        rethrow(e);
    }
}

// Copies .NET memory into a JavaScript buffer, since a memory view is valid only during the call
// that receives it and transfers complete later.
export function copyFrom(view) {
    return view.slice();
}

function toBytes(dataView) {
    return dataView ? new Uint8Array(dataView.buffer, dataView.byteOffset, dataView.byteLength) : null;
}

export function getStatus(result) {
    return result.status;
}

export function getLength(result) {
    return result.bytes ? result.bytes.length : (result.written ?? 0);
}

export function copyBytes(result, buffer) {
    // buffer is a MemoryView over .NET memory, valid only during this call.
    if (!result.bytes) return 0;
    const count = Math.min(buffer.byteLength, result.bytes.length);
    buffer.set(result.bytes.subarray(0, count));
    return count;
}
