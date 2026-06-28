namespace ChessOverMesh.Maui;

/// <summary>
/// Custom MAUI permission bundling the Android Bluetooth-LE runtime permissions. Android 12+ (API 31) uses the
/// new BLUETOOTH_SCAN / BLUETOOTH_CONNECT runtime grants; older devices need ACCESS_FINE_LOCATION for BLE scans.
/// </summary>
public sealed class BlePermissions : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        OperatingSystem.IsAndroidVersionAtLeast(31)
            ? new[]
            {
                ("android.permission.BLUETOOTH_SCAN", true),
                ("android.permission.BLUETOOTH_CONNECT", true),
            }
            : new[]
            {
                ("android.permission.BLUETOOTH", true),
                ("android.permission.BLUETOOTH_ADMIN", true),
                ("android.permission.ACCESS_FINE_LOCATION", true),
            };
}
