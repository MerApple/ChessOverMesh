namespace ChessOverMesh.Maui;

/// <summary>
/// Keeps the app process alive while connected so the receive loop survives the screen turning off, and posts
/// status-bar notifications for new messages. On Android this is backed by a foreground service (the same
/// mechanism the native Meshtastic app uses); other platforms are no-ops for now.
/// </summary>
public static class BackgroundConnection
{
    /// <summary>Start the keep-alive foreground service (shows the ongoing "connected" notification). When
    /// <paramref name="keepWifiAwake"/> (an HTTP/WiFi connection), also hold a WiFi + CPU wake lock so the poll
    /// keeps running through sleep — BLE doesn't need this (the radio wakes us on packets). Best-effort: this is
    /// an optional convenience, so a failure here must never break (or crash) the connection.</summary>
    public static void Start(string deviceLabel, bool keepWifiAwake)
    {
#if ANDROID
        try
        {
            MeshForegroundService.Start(deviceLabel);
            if (keepWifiAwake) NetworkKeepAlive.Acquire();
        }
        catch (System.Exception) { /* keep-alive is optional; the connection works without it */ }
#endif
    }

    /// <summary>Stop the foreground service (removes the ongoing notification) and release any WiFi/CPU locks.</summary>
    public static void Stop()
    {
#if ANDROID
        try { NetworkKeepAlive.Release(); } catch (System.Exception) { }
        try { MeshForegroundService.Stop(); } catch (System.Exception) { }
#endif
    }

    /// <summary>Requests notification permission (Android 13+). Safe to call repeatedly.</summary>
    public static async Task EnsureNotificationPermissionAsync()
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var status = await Permissions.CheckStatusAsync<NotificationPermission>();
            if (status != PermissionStatus.Granted)
                await Permissions.RequestAsync<NotificationPermission>();
        }
#endif
        await Task.CompletedTask;
    }

    /// <summary>Post a status-bar notification for a new message (chat / game event).</summary>
    public static void NotifyMessage(string title, string body)
    {
#if ANDROID
        MeshForegroundService.PostMessage(title, body);
#endif
    }

    /// <summary>Dismiss any outstanding new-message notifications — called when the user reads the chat in-app, so
    /// alerts for messages they've now seen don't linger in the status bar. Leaves the ongoing "connected" one.</summary>
    public static void ClearMessageNotifications()
    {
#if ANDROID
        try { MeshForegroundService.CancelMessages(); } catch (System.Exception) { /* best-effort */ }
#endif
    }
}
