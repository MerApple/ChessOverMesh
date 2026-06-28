using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ChessOverMesh.Maui;

/// <summary>A Meshtastic device discovered over BLE: the live <see cref="IDevice"/> handle plus a display name.</summary>
public sealed record BleDeviceInfo(IDevice Device, string Name);

/// <summary>Scans for Meshtastic radios advertising the BLE service UUID. Experimental — see <see cref="BleMeshTransport"/>.</summary>
public static class BleScanner
{
    public static bool IsAvailable => CrossBluetoothLE.Current.IsAvailable;
    public static bool IsOn => CrossBluetoothLE.Current.State == BluetoothState.On;

    /// <summary>Requests the Android BLE runtime permissions. Returns true if granted.</summary>
    public static async Task<bool> EnsurePermissionsAsync()
    {
        var status = await Permissions.CheckStatusAsync<BlePermissions>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<BlePermissions>();
        return status == PermissionStatus.Granted;
    }

    /// <summary>Scans for devices advertising the Meshtastic service for <paramref name="timeout"/>, returning the
    /// unique set found (de-duplicated by device id).</summary>
    public static async Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var adapter = CrossBluetoothLE.Current.Adapter;
        var found = new Dictionary<Guid, BleDeviceInfo>();

        void OnDiscovered(object? sender, DeviceEventArgs e)
        {
            var name = string.IsNullOrWhiteSpace(e.Device.Name) ? "(unnamed Meshtastic)" : e.Device.Name;
            found[e.Device.Id] = new BleDeviceInfo(e.Device, name);
        }

        adapter.DeviceDiscovered += OnDiscovered;
        adapter.ScanTimeout = (int)timeout.TotalMilliseconds;
        adapter.ScanMode = ScanMode.LowLatency;
        try
        {
            await adapter.StartScanningForDevicesAsync(
                new ScanFilterOptions { ServiceUuids = new[] { BleMeshTransport.ServiceUuid } },
                cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            adapter.DeviceDiscovered -= OnDiscovered;
        }
        return found.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
