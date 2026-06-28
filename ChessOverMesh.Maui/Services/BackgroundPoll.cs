namespace ChessOverMesh.Maui;

/// <summary>
/// Coordinates the opt-in periodic background poll. While the app is asleep, Android won't keep a live connection,
/// but it does run a WorkManager job roughly every 15 minutes (in Doze maintenance windows). That job connects,
/// drains any messages the device buffered, posts notifications, and disconnects. See BackgroundPollWorker.
/// </summary>
public static class BackgroundPoll
{
    /// <summary>Set by the foreground poll loop on each healthy poll, so the background job can skip when the
    /// foreground app is already connected and receiving (the device's /fromradio queue has one consumer).</summary>
    public static DateTime LastForegroundPollUtc = DateTime.MinValue;

    /// <summary>Schedule or cancel the periodic job to match the setting. Call on startup and when the toggle changes.</summary>
    public static void Apply()
    {
#if ANDROID
        if (AppSettings.BackgroundPoll) BackgroundPollWorker.Schedule();
        else BackgroundPollWorker.Cancel();
#endif
    }
}
