using Android.Content;
using Android.Net.Wifi;
using Android.OS;

namespace ChessOverMesh.Maui;

/// <summary>
/// Holds the OS locks needed to keep an HTTP/WiFi poll alive while the phone sleeps: a WiFi lock so the radio
/// isn't powered down, and a partial wake lock so the CPU keeps running the poll timer (TCP has no hardware
/// wakeup, unlike BLE). Held only while connected over WiFi; released on disconnect. This costs battery —
/// it deliberately keeps the CPU and WiFi awake.
/// </summary>
static class NetworkKeepAlive
{
    static WifiManager.WifiLock? _wifiLock;
    static PowerManager.WakeLock? _wakeLock;

    public static void Acquire()
    {
        var ctx = Android.App.Application.Context;

        if (_wifiLock == null && ctx.GetSystemService(Context.WifiService) is WifiManager wifi)
        {
#pragma warning disable CA1422 // FullHighPerf is deprecated but is the background-honored mode (FullLowLatency only applies in foreground).
            _wifiLock = wifi.CreateWifiLock(Android.Net.WifiMode.FullHighPerf, "ChessOverMesh:wifi");
#pragma warning restore CA1422
            _wifiLock?.SetReferenceCounted(false);
            _wifiLock?.Acquire();
        }

        if (_wakeLock == null && ctx.GetSystemService(Context.PowerService) is PowerManager power)
        {
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "ChessOverMesh:poll");
            _wakeLock?.SetReferenceCounted(false);
            _wakeLock?.Acquire();
        }
    }

    public static void Release()
    {
        try { if (_wifiLock is { IsHeld: true }) _wifiLock.Release(); } catch { /* ignore */ }
        try { if (_wakeLock is { IsHeld: true }) _wakeLock.Release(); } catch { /* ignore */ }
        _wifiLock = null;
        _wakeLock = null;
    }
}
