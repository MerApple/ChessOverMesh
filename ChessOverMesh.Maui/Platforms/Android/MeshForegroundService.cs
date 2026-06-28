using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace ChessOverMesh.Maui;

/// <summary>
/// A connectedDevice foreground service. While running it keeps the app process at foreground priority and shows
/// an ongoing notification, so the receive loop keeps running (and the BLE link stays up) after the screen turns
/// off — the same approach the native Meshtastic app uses. It also exposes a static helper to post per-message
/// notifications.
///
/// SPIKE: compiles and integrates; unverified on a physical device. Aggressive OEM battery managers can still
/// kill background work — the user may need to exclude the app from battery optimization.
/// </summary>
// connectedDevice covers the BLE radio link; dataSync covers the HTTP/WiFi poll. Declaring both lets the one
// dataSync is the correct type for keeping a network (WiFi/TCP/HTTP) link alive. (connectedDevice is only for a
// physically attached device — BLE/USB/companion — and Android 14+ rejects it for a WiFi connection, which would
// crash the app at StartForeground.)
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class MeshForegroundService : Service
{
    const string OngoingChannelId = "mesh_connection";
    const string MessageChannelId = "mesh_messages";
    const int OngoingNotificationId = 1001;
    const string ExtraLabel = "label";
    static int _messageId = 2000;
    // Ids of the per-message notifications we've posted, so they can be dismissed when the user reads the chat.
    static readonly object _msgLock = new();
    static readonly List<int> _postedMessageIds = new();

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var label = intent?.GetStringExtra(ExtraLabel) ?? "device";
        EnsureChannels(this);

        var notification = new NotificationCompat.Builder(this, OngoingChannelId)
            .SetContentTitle("Chess over Meshtastic")
            .SetContentText($"Connected to {label} — receiving in the background")
            .SetSmallIcon(Android.Resource.Drawable.StatNotifyChat)
            .SetOngoing(true)
            .SetPriority((int)NotificationPriority.Low)
            .SetContentIntent(LaunchIntent(this))
            .Build();

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                StartForeground(OngoingNotificationId, notification, ForegroundService.TypeDataSync);
            else
                StartForeground(OngoingNotificationId, notification);
        }
        catch (System.Exception)
        {
            // Some OEMs/Android versions can refuse a typed foreground start. Fall back to a typeless start
            // (which still satisfies Android's "must call startForeground" requirement); if even that is
            // refused, stop the optional service rather than crash — the connection works without it.
            try
            {
                StartForeground(OngoingNotificationId, notification);
            }
            catch (System.Exception)
            {
                try { StopSelf(); } catch { }
                return StartCommandResult.NotSticky;
            }
        }

        return StartCommandResult.Sticky;   // restart if the OS reclaims us
    }

    public static void Start(string label)
    {
        var ctx = Android.App.Application.Context;
        var intent = new Intent(ctx, typeof(MeshForegroundService));
        intent.PutExtra(ExtraLabel, label);
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) ctx.StartForegroundService(intent);
        else ctx.StartService(intent);
    }

    public static void Stop()
    {
        var ctx = Android.App.Application.Context;
        ctx.StopService(new Intent(ctx, typeof(MeshForegroundService)));
    }

    public static void PostMessage(string title, string body)
    {
        var ctx = Android.App.Application.Context;
        EnsureChannels(ctx);
        var notification = new NotificationCompat.Builder(ctx, MessageChannelId)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
            .SetSmallIcon(Android.Resource.Drawable.StatNotifyChat)
            .SetPriority((int)NotificationPriority.High)
            .SetAutoCancel(true)
            .SetContentIntent(LaunchIntent(ctx))
            .Build();
        int id = System.Threading.Interlocked.Increment(ref _messageId);
        NotificationManagerCompat.From(ctx).Notify(id, notification);
        lock (_msgLock) _postedMessageIds.Add(id);
    }

    /// <summary>Dismisses every per-message notification we've posted (the ongoing "connected" one is left alone).
    /// Called when the user reads the chat in-app so stale message alerts don't linger in the status bar.</summary>
    public static void CancelMessages()
    {
        int[] ids;
        lock (_msgLock) { ids = _postedMessageIds.ToArray(); _postedMessageIds.Clear(); }
        if (ids.Length == 0) return;
        var mgr = NotificationManagerCompat.From(Android.App.Application.Context);
        foreach (var id in ids) mgr.Cancel(id);
    }

    // Tapping a notification brings the app's single activity back to the front.
    static PendingIntent? LaunchIntent(Context ctx)
    {
        var launch = ctx.PackageManager?.GetLaunchIntentForPackage(ctx.PackageName!);
        if (launch == null) return null;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        return PendingIntent.GetActivity(ctx, 0, launch,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
    }

    static void EnsureChannels(Context ctx)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var mgr = (NotificationManager?)ctx.GetSystemService(NotificationService);
        if (mgr == null) return;

        if (mgr.GetNotificationChannel(OngoingChannelId) == null)
            mgr.CreateNotificationChannel(new NotificationChannel(
                OngoingChannelId, "Device connection", NotificationImportance.Low)
            { Description = "Ongoing notification while connected to a Meshtastic device." });

        if (mgr.GetNotificationChannel(MessageChannelId) == null)
            mgr.CreateNotificationChannel(new NotificationChannel(
                MessageChannelId, "Messages", NotificationImportance.High)
            { Description = "New chat messages and game events." });
    }
}
