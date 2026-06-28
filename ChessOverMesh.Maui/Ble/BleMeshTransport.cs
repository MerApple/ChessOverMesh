using ChessOverMesh.Mesh;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ChessOverMesh.Maui;

/// <summary>
/// <see cref="IMeshTransport"/> over Bluetooth LE (experimental). Meshtastic exposes one GATT service with three
/// characteristics: write a ToRadio protobuf to <c>toRadio</c>, then read <c>fromRadio</c> repeatedly until it
/// returns empty to drain the queue (the same one-packet-per-read model the HTTP API uses, which is why the
/// existing drain loops in <see cref="MeshtasticHttpClient"/> work unchanged). <c>fromNum</c> notifies when new
/// packets are queued.
///
/// SPIKE: compiles and integrates, but is unverified on a physical radio. Real devices typically require BLE
/// bonding with a PIN (default 123456) — that surfaces as the OS pairing dialog on first connect.
/// </summary>
public sealed class BleMeshTransport : IMeshTransport, IAsyncDisposable
{
    // Meshtastic BLE GATT UUIDs (firmware: src/mesh/api/.. / the documented BLE service).
    public static readonly Guid ServiceUuid   = Guid.Parse("6ba1b218-15a8-461f-9fa8-5dcae273eafd");
    static readonly Guid ToRadioUuid   = Guid.Parse("f75c76d2-129e-4dad-a1dd-7866124401e7");
    static readonly Guid FromRadioUuid = Guid.Parse("2c55e69e-4993-11ed-b878-0242ac120002");
    static readonly Guid FromNumUuid   = Guid.Parse("ed9da18c-a800-4f66-a670-aa7547e34453");

    readonly IAdapter _adapter;
    readonly IDevice _device;
    ICharacteristic? _toRadio, _fromRadio, _fromNum;
    readonly SemaphoreSlim _gate = new(1, 1);   // GATT permits one operation at a time
    volatile bool _faulted;   // set when the radio disconnects or a GATT op fails — the link is down
    bool _disposed;

    /// <summary>False once the BLE link has dropped — the device disconnected (out of range / powered off /
    /// dropped the bond) or a GATT read/write failed. The poll/probe loop uses this to tear down and reconnect,
    /// instead of the app showing "connected" while sends fail with "Gatt write characteristic FAILED".</summary>
    public bool IsConnected => !_disposed && !_faulted && _device.State == DeviceState.Connected;

    /// <summary>The BLE link reports its own liveness (device state + GATT-error faulting), so the app must not run
    /// the external reachability probe against it — see <see cref="IMeshTransport.SelfReportsLiveness"/>.</summary>
    public bool SelfReportsLiveness => true;

    BleMeshTransport(IAdapter adapter, IDevice device)
    {
        _adapter = adapter;
        _device = device;
        // The OS notifies us when the radio drops the link (unexpected disconnect or connection lost).
        _adapter.DeviceDisconnected += OnDeviceDropped;
        _adapter.DeviceConnectionLost += OnDeviceConnectionLost;
    }

    void OnDeviceDropped(object? sender, DeviceEventArgs e) { if (e.Device?.Id == _device.Id) _faulted = true; }
    void OnDeviceConnectionLost(object? sender, DeviceErrorEventArgs e) { if (e.Device?.Id == _device.Id) _faulted = true; }

    /// <summary>Connects to <paramref name="device"/>, negotiates a larger MTU, and resolves the Meshtastic
    /// characteristics. Throws if the device doesn't expose the Meshtastic service.</summary>
    public static async Task<BleMeshTransport> ConnectAsync(IDevice device, CancellationToken ct = default)
    {
        var adapter = CrossBluetoothLE.Current.Adapter;
        var transport = new BleMeshTransport(adapter, device);

        await adapter.ConnectToDeviceAsync(device,
            new ConnectParameters(autoConnect: false, forceBleTransport: true), ct).ConfigureAwait(false);

        // ToRadio packets can exceed the 23-byte default ATT MTU; ask for the max (best effort).
        try { await device.RequestMtuAsync(512).ConfigureAwait(false); } catch { /* not fatal */ }

        var service = await device.GetServiceAsync(ServiceUuid, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This device doesn't expose the Meshtastic BLE service.");

        transport._toRadio   = await service.GetCharacteristicAsync(ToRadioUuid).ConfigureAwait(false);
        transport._fromRadio = await service.GetCharacteristicAsync(FromRadioUuid).ConfigureAwait(false);
        transport._fromNum   = await service.GetCharacteristicAsync(FromNumUuid).ConfigureAwait(false);
        if (transport._toRadio == null || transport._fromRadio == null)
            throw new InvalidOperationException("Meshtastic BLE characteristics not found on this device.");

        // Enabling fromNum notifications keeps the link active and lets the device wake us on new packets;
        // we still drain via reads, so a missed notification is harmless.
        if (transport._fromNum != null)
        {
            try { await transport._fromNum.StartUpdatesAsync(ct).ConfigureAwait(false); } catch { /* optional */ }
        }
        return transport;
    }

    public async Task WriteAsync(byte[] toRadio, CancellationToken ct)
    {
        if (_toRadio == null) throw new InvalidOperationException("BLE transport not connected.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await _toRadio.WriteAsync(toRadio, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { _faulted = true; throw; }   // GATT write failed — the link is down
        finally { _gate.Release(); }
    }

    public async Task<byte[]?> ReadAsync(bool all, CancellationToken ct, TimeSpan? requestTimeout = null)
    {
        if (_fromRadio == null) throw new InvalidOperationException("BLE transport not connected.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (requestTimeout is { } t) cts.CancelAfter(t);
        await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
        try
        {
            var (data, _) = await _fromRadio.ReadAsync(cts.Token).ConfigureAwait(false);
            return data is { Length: > 0 } ? data : null;   // empty read == queue drained
        }
        catch (OperationCanceledException) { throw; }   // our read-timeout / external cancel — not a fault
        catch { _faulted = true; throw; }                // GATT read failed — the link is down
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adapter.DeviceDisconnected -= OnDeviceDropped;
        _adapter.DeviceConnectionLost -= OnDeviceConnectionLost;
        try { _ = _adapter.DisconnectDeviceAsync(_device); } catch { /* ignore */ }
        _gate.Dispose();
    }

    /// <summary>Closes the link and AWAITS the GATT disconnect — used when the app is closing (the user swiped it
    /// away). Unlike <see cref="Dispose"/> (fire-and-forget, fine while the app keeps running), this makes sure the
    /// native GATT is actually disconnected/closed before the process goes away; otherwise the Android Bluetooth
    /// stack keeps the connection and the device can't be found again until the phone is rebooted.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) { _gate.Dispose(); return; }
        _disposed = true;
        _adapter.DeviceDisconnected -= OnDeviceDropped;
        _adapter.DeviceConnectionLost -= OnDeviceConnectionLost;
        try { await _adapter.DisconnectDeviceAsync(_device).ConfigureAwait(false); } catch { /* ignore */ }
        _gate.Dispose();
    }
}
