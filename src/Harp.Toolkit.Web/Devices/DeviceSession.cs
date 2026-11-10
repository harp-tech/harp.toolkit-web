using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Bonsai.Harp;
using Harp.Toolkit.Web.Generation;
using Harp.WebSerial;
using Harp.WebUsb;

namespace Harp.Toolkit.Web.Devices;

/// <summary>
/// Specifies the state of the connection to the selected device.
/// </summary>
public enum SessionState
{
    /// <summary>No port is selected.</summary>
    NoPort,
    /// <summary>A port is selected but not open.</summary>
    Disconnected,
    /// <summary>The port is opening and the device identity is being read.</summary>
    Connecting,
    /// <summary>The device is connected and answering commands.</summary>
    Connected,
    /// <summary>An operation, such as a firmware update, has exclusive use of the port.</summary>
    Busy
}

/// <summary>
/// Describes a serial port the user granted access to, and the device found on it.
/// </summary>
/// <param name="Port">The serial port.</param>
/// <param name="Identity">The identity of the device on the port, or <see langword="null"/> if none answered.</param>
/// <param name="IsCurrent">Whether this is the port the session has selected.</param>
public sealed record PortProbe(WebSerialPort Port, DeviceIdentity? Identity, bool IsCurrent);

/// <summary>
/// Holds the selected serial port, the connection to its device and the register schema,
/// shared by every page so the device stays connected while moving between them.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class DeviceSession(HttpClient http)
{
    readonly Dictionary<int, HarpMessage> latest = [];
    WebSerialDevicePort? port;
    IReadOnlyList<DeviceEntry>? catalog;
    bool schemaFromUser;

    /// <summary>
    /// Occurs when the state, identity or schema of the session changes.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Occurs when the connected device sends a message.
    /// </summary>
    public event Action<HarpMessage>? MessageReceived;

    /// <summary>
    /// Gets a value indicating whether the browser supports Web Serial.
    /// </summary>
    public bool IsSupported { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the browser supports WebUSB, which updates Pico devices.
    /// </summary>
    public bool IsUsbSupported { get; private set; }

    /// <summary>
    /// Gets the state of the session.
    /// </summary>
    public SessionState State { get; private set; } = SessionState.NoPort;

    /// <summary>
    /// Gets a value indicating whether the device is connected.
    /// </summary>
    public bool IsConnected => State == SessionState.Connected && Connection is not null;

    /// <summary>
    /// Gets the selected port, if any.
    /// </summary>
    public WebSerialPort? Port => port?.Port;

    /// <summary>
    /// Gets the connection to the device, while connected.
    /// </summary>
    public HarpConnection? Connection { get; private set; }

    /// <summary>
    /// Gets the identity of the device, read when it last connected.
    /// </summary>
    public DeviceIdentity? Identity { get; private set; }

    /// <summary>
    /// Gets the operation mode of the device, if known.
    /// </summary>
    public OperationMode? OperationMode { get; private set; }

    /// <summary>
    /// Gets the catalog entry of the device, if its WhoAmI is registered.
    /// </summary>
    public DeviceEntry? CatalogEntry { get; private set; }

    /// <summary>
    /// Gets the register schema of the device. Until device metadata loads, only the core registers are known.
    /// </summary>
    public DeviceSchema Schema { get; private set; } = DeviceSchema.Core;

    /// <summary>
    /// Gets the message of the last error, if any.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Gets a short description of the selected port.
    /// </summary>
    public string PortLabel => Port is { } selected
        ? selected.UsbVendorId is { } vendor ? $"USB {vendor:x4}:{selected.UsbProductId:x4}" : "Serial port"
        : "No port";

    /// <summary>
    /// Returns the last message received from the specified register, if any.
    /// </summary>
    /// <param name="address">The address of the register.</param>
    /// <returns>The last message from the register, or <see langword="null"/>.</returns>
    public HarpMessage? GetLatest(int address) => latest.GetValueOrDefault(address);

    /// <summary>
    /// Asynchronously loads Web Serial and selects the first port the user granted earlier, if any.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public async Task InitializeAsync()
    {
        await Task.WhenAll(SerialPorts.InitializeAsync(), UsbDevices.InitializeAsync());
        IsSupported = SerialPorts.IsSupported;
        IsUsbSupported = UsbDevices.IsSupported;
        if (!IsSupported)
            return;

        var granted = await SerialPorts.GetPortsAsync();
        if (granted.Length > 0)
            SetPort(granted[0]);
    }

    /// <summary>
    /// Asynchronously asks the user to select a port, and connects to it.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public async Task SelectPortAsync()
    {
        var selected = await SerialPorts.RequestPortAsync();
        if (selected is null)
            return;

        await DisconnectAsync();
        SetPort(selected);
        await ConnectAsync();
    }

    /// <summary>
    /// Asynchronously asks each port the user granted earlier for the identity of its device.
    /// </summary>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property contains one entry per granted port. Ports that are busy or silent have no identity.
    /// </returns>
    /// <remarks>The selected port is not opened again: its identity is the one it already reported.</remarks>
    public async Task<IReadOnlyList<PortProbe>> ProbePortsAsync()
    {
        if (!IsSupported)
            return [];

        var granted = await SerialPorts.GetPortsAsync();
        var idle = State is not (SessionState.Connecting or SessionState.Busy);
        return await Task.WhenAll(granted.Select(async candidate =>
        {
            if (port is not null && candidate.IsSameAs(port.Port))
                return new PortProbe(port.Port, IsConnected ? Identity : null, IsCurrent: true);
            return new PortProbe(candidate, idle ? await ProbeAsync(candidate) : null, IsCurrent: false);
        }));
    }

    /// <summary>
    /// Asynchronously switches to a port from <see cref="ProbePortsAsync"/>, and connects to it.
    /// </summary>
    /// <param name="selected">The port to use.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public async Task UsePortAsync(WebSerialPort selected)
    {
        if (port is not null && selected.IsSameAs(port.Port))
            return;

        await DisconnectAsync();
        SetPort(selected);
        await ConnectAsync();
    }

    static async Task<DeviceIdentity?> ProbeAsync(WebSerialPort candidate)
    {
        var devicePort = new WebSerialDevicePort(candidate);
        HarpConnection? connection = null;
        try
        {
            connection = new HarpConnection(await devicePort.OpenAsync(PortMode.Protocol))
            {
                ReplyTimeout = TimeSpan.FromMilliseconds(500)
            };
            return await connection.ReadIdentityAsync();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or InvalidDataException)
        {
            // The port is in use, or nothing on it speaks Harp.
            return null;
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync();
            try { await devicePort.CloseAsync(); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Asynchronously opens the selected port and reads the device identity.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public async Task ConnectAsync()
    {
        if (port is null || State is SessionState.Connecting or SessionState.Connected or SessionState.Busy)
            return;

        SetState(SessionState.Connecting, error: null);
        HarpConnection? connection = null;
        try
        {
            var stream = await port.OpenAsync(PortMode.Protocol);
            connection = new HarpConnection(stream);
            connection.MessageReceived += OnMessageReceived;
            connection.Closed += OnConnectionClosed;
            Identity = await connection.ReadIdentityAsync();
            OperationMode = OperationControl.GetPayload(
                await connection.CommandAsync(HarpCommand.ReadByte(OperationControl.Address))).OperationMode;
            Connection = connection;
            SetState(SessionState.Connected, error: null);
            if (!schemaFromUser || Schema.WhoAmI != Identity.WhoAmI)
                await LoadRegistrySchemaAsync(Identity.WhoAmI);
        }
        catch (Exception ex)
        {
            if (connection is not null)
            {
                connection.Closed -= OnConnectionClosed;
                await connection.DisposeAsync();
            }
            await port.CloseAsync();
            Connection = null;
            SetState(SessionState.Disconnected, Describe(ex));
        }
    }

    /// <summary>
    /// Asynchronously closes the connection to the device.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public async Task DisconnectAsync()
    {
        var connection = Connection;
        Connection = null;
        if (connection is not null)
        {
            connection.Closed -= OnConnectionClosed;
            connection.MessageReceived -= OnMessageReceived;
            await connection.DisposeAsync();
        }

        if (port is not null)
        {
            await port.CloseAsync();
            SetState(SessionState.Disconnected, Error);
        }
    }

    /// <summary>
    /// Asynchronously runs an operation that needs exclusive use of the port, such as a firmware update,
    /// and reconnects to the device afterwards.
    /// </summary>
    /// <param name="operation">The operation, which receives the closed port.</param>
    /// <param name="reconnectDelay">The time to wait for the device to restart before reconnecting.</param>
    /// <param name="portMayMove">
    /// <see langword="true"/> if the device leaves and reappears on USB, as a Pico device does when it
    /// restarts in BOOTSEL mode, so its serial port can come back as a different port.
    /// </param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public async Task RunExclusiveAsync(Func<IDevicePort, Task> operation, TimeSpan reconnectDelay, bool portMayMove = false)
    {
        var current = port ?? throw new InvalidOperationException("Select a port first.");
        var previous = Identity;
        await DisconnectAsync();
        SetState(SessionState.Busy, error: null);
        try
        {
            await operation(current);
        }
        finally
        {
            // The device may have left USB during the operation, which can fail the close.
            try { await current.CloseAsync(); } catch (IOException) { }
            SetState(SessionState.Disconnected, Error);
        }

        await Task.Delay(reconnectDelay);
        if (portMayMove && previous is not null)
            await FindDeviceAsync(previous.WhoAmI, previous.SerialNumber, MovedPortTimeout);
        else
            await ConnectAsync();
    }

    static readonly TimeSpan MovedPortTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Asynchronously connects to a device that may have come back on a different serial port,
    /// trying the selected port first and then the other ports the user granted earlier.
    /// </summary>
    /// <param name="whoAmI">The identity class of the device.</param>
    /// <param name="serialNumber">The serial number of the device, if known.</param>
    /// <param name="timeout">The time to keep looking for the device.</param>
    /// <returns>
    /// The task object representing the asynchronous operation. The <see cref="Task{TResult}.Result"/>
    /// property is <see langword="true"/> if the device connected.
    /// </returns>
    /// <remarks>
    /// A device that reappears on USB keeps its permission only if the browser can recognize it,
    /// so the user may have to select its port again.
    /// </remarks>
    public async Task<bool> FindDeviceAsync(int whoAmI, ushort? serialNumber, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (port is not null)
            {
                await ConnectAsync();
                if (IsConnected)
                    return true;
            }

            foreach (var candidate in await SerialPorts.GetPortsAsync())
            {
                if (port is not null && candidate.IsSameAs(port.Port))
                    continue;
                var identity = await ProbeAsync(candidate);
                if (identity is not null && identity.WhoAmI == whoAmI &&
                    (serialNumber is null || identity.SerialNumber == serialNumber))
                {
                    SetPort(candidate);
                    await ConnectAsync();
                    if (IsConnected)
                        return true;
                }
            }

            if (stopwatch.Elapsed > timeout)
                return false;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Asynchronously sets the operation mode of the device, leaving its other operation settings unchanged.
    /// </summary>
    /// <param name="mode">The operation mode. Devices only send events in Active mode.</param>
    /// <param name="dumpRegisters"><see langword="true"/> to also ask the device to send the value of every register.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public async Task SetOperationModeAsync(OperationMode mode, bool dumpRegisters = false)
    {
        var connection = Connection ?? throw new InvalidOperationException("Connect the device first.");
        var current = OperationControl.GetPayload(await connection.CommandAsync(HarpCommand.ReadByte(OperationControl.Address)));
        await connection.CommandAsync(OperationControl.FromPayload(MessageType.Write, new OperationControlPayload(
            mode, dumpRegisters, current.MuteReplies, current.VisualIndicators, current.OperationLed, current.Heartbeat)));
        OperationMode = mode;
        Changed?.Invoke();
    }

    /// <summary>
    /// Asynchronously reads the operation mode of the device again, after it may have changed.
    /// </summary>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public async Task RefreshOperationModeAsync()
    {
        if (Connection is not { } connection)
            return;
        OperationMode = OperationControl.GetPayload(
            await connection.CommandAsync(HarpCommand.ReadByte(OperationControl.Address))).OperationMode;
        Changed?.Invoke();
    }

    /// <summary>
    /// Uses the registers declared in a device.yml the user provided.
    /// </summary>
    /// <param name="yaml">The contents of the device metadata file.</param>
    /// <param name="source">Where the file came from, for display.</param>
    /// <exception cref="YamlDotNet.Core.YamlException">The metadata is not valid.</exception>
    public void LoadSchema(string yaml, string source)
    {
        Schema = DeviceSchema.Parse(yaml, source);
        schemaFromUser = true;
        Changed?.Invoke();
    }

    async Task LoadRegistrySchemaAsync(int whoAmI)
    {
        CatalogEntry = null;
        schemaFromUser = false;
        Schema = DeviceSchema.Core;
        try
        {
            catalog ??= await http.GetFromJsonAsync<List<DeviceEntry>>("devices.json") ?? [];
            CatalogEntry = catalog.FirstOrDefault(entry => entry.WhoAmI == whoAmI);
            if (CatalogEntry is not null)
            {
                var yaml = await http.GetStringAsync(CatalogEntry.DeviceUrl);
                Schema = DeviceSchema.Parse(yaml, "Harp device registry");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or YamlDotNet.Core.YamlException or NotSupportedException)
        {
            // The device metadata is optional: without it, only the core registers are described.
        }
        Changed?.Invoke();
    }

    void SetPort(WebSerialPort selected)
    {
        port = new WebSerialDevicePort(selected);
        Identity = null;
        OperationMode = null;
        CatalogEntry = null;
        latest.Clear();
        SetState(SessionState.Disconnected, error: null);
    }

    void SetState(SessionState state, string? error)
    {
        State = state;
        Error = error;
        Changed?.Invoke();
    }

    void OnMessageReceived(HarpMessage message)
    {
        if (!message.Error)
            latest[message.Address] = message;
        MessageReceived?.Invoke(message);
    }

    async void OnConnectionClosed(Exception? error)
    {
        // The read loop stopped on its own, for example because the device was unplugged.
        Connection = null;
        if (port is not null)
        {
            try { await port.CloseAsync(); } catch (IOException) { }
        }
        SetState(SessionState.Disconnected, error is null ? "The device closed the connection." : Describe(error));
    }

    static string Describe(Exception ex) => ex switch
    {
        TimeoutException => "The device did not answer. Check that it is powered and that the right port is selected.",
        _ => ex.Message
    };
}
