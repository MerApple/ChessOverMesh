using Android.Content;
using AndroidX.Work;
using ChessOverMesh.Game;
using ChessOverMesh.Mesh;
using Java.Util.Concurrent;

namespace ChessOverMesh.Maui;

/// <summary>
/// Periodic background job (~every 15 min, in Doze maintenance windows). When enabled, it connects to the device
/// over WiFi, drains any chat messages the device buffered while we were asleep, posts a notification for each,
/// and — crucially — persists each one to the same per-device cache the UI loads on connect, so the message is
/// visible in the Chat window when the user opens the app. Then it disconnects. It's a coarse fallback — Android
/// won't run this more often than ~15 min — for real-time background reception, BLE is the right transport.
///
/// Why the persist matters: this job opens its OWN connection, entirely separate from MainPage's, and the device's
/// /fromradio queue is a single-consumer DESTRUCTIVE queue. Draining it here consumes those packets, so a later
/// foreground connect can never re-fetch them. Without caching, a background message was only ever a notification
/// that led to an empty Chat window (and a "device not connected" Device tab).
///
/// SPIKE: compiles and is wired up, but unverified on a device.
/// </summary>
public class BackgroundPollWorker : Worker
{
    const string WorkName = "mesh-bg-poll";

    public BackgroundPollWorker(Context context, WorkerParameters workerParams) : base(context, workerParams) { }

    public static void Schedule()
    {
        var constraints = new Constraints.Builder()
            .SetRequiredNetworkType(NetworkType.Connected!)   // only run when there's a network
            .Build();
        var request = new PeriodicWorkRequest.Builder(
                Java.Lang.Class.FromType(typeof(BackgroundPollWorker)), 15, TimeUnit.Minutes!)
            .SetConstraints(constraints!)
            .Build();
        WorkManager.GetInstance(Android.App.Application.Context)
            .EnqueueUniquePeriodicWork(WorkName, ExistingPeriodicWorkPolicy.Update!, request);
    }

    public static void Cancel() =>
        WorkManager.GetInstance(Android.App.Application.Context).CancelUniqueWork(WorkName);

    public override Result DoWork()
    {
        try { PollOnceAsync().GetAwaiter().GetResult(); } catch { /* never fail the periodic chain */ }
        return Result.InvokeSuccess()!;
    }

    static async Task PollOnceAsync()
    {
        if (!AppSettings.BackgroundPoll) return;
        // The foreground app is connected and draining the (single-consumer) queue — don't fight it.
        if (DateTime.UtcNow - BackgroundPoll.LastForegroundPollUtc < TimeSpan.FromMinutes(2)) return;

        string host = AppSettings.LastHost ?? "";
        if (host.Length == 0) return;
        if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "http://" + host;
        string ip;
        try { ip = new Uri(host).Host; } catch { return; }

        TcpStreamMeshTransport? tcp = null;
        MeshtasticHttpClient? mesh = null;
        try
        {
            try { tcp = await TcpStreamMeshTransport.ConnectAsync(ip, TcpStreamMeshTransport.DefaultPort, TimeSpan.FromSeconds(5)); }
            catch { tcp = null; }
            mesh = tcp != null ? new MeshtasticHttpClient(tcp) : new MeshtasticHttpClient(host);

            // Apply cached app-level channel keys so encrypted chat decodes.
            foreach (var kv in DeviceCache.GetChannelKeys(host)) mesh.SetChannelKey(kv.Key, kv.Value);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await mesh.InitializeAsync(cts.Token);

            var prefs = DeviceCache.GetChannelPrefs(host);
            var chatChannels = prefs?.ChatListen is { } list ? new HashSet<uint>(list) : new HashSet<uint>();

            const int chunk = 50;
            while (true)
            {
                var result = await mesh.ReceiveAsync(chunk, cts.Token);
                foreach (var msg in result.Texts)
                    HandleText(mesh, host, chatChannels, msg);
                if (result.PacketCount < chunk) break;
            }
        }
        catch { /* unreachable / timed out — try again next window */ }
        finally
        {
            mesh?.Dispose();
            tcp?.Dispose();
        }
    }

    // Turn one received text into a notification AND persist it to the same per-device cache the UI loads on
    // connect (DeviceCache), mirroring the foreground receive path in MainPage. Persisting is the fix for a
    // background-received message showing up in the Chat window: the foreground wasn't connected when it arrived,
    // and the destructive /fromradio drain here means it can't be re-fetched later.
    static void HandleText(MeshtasticHttpClient mesh, string host, HashSet<uint> chatChannels, MeshTextMessage msg)
    {
        if (msg.IsReaction || msg.IsChunkPart) return;   // reactions attach to a row; split parts need reassembly we don't do here
        if (msg.FromNode == mesh.MyNodeNum) return;       // our own echoed message — not an incoming alert
        bool isDm = msg.IsDmTo(mesh.MyNodeNum);
        if (!isDm && !chatChannels.Contains(msg.Channel)) return;   // a channel we don't listen to (DMs to us always count)

        if (ProtocolMessage.TryParse(msg.Text, out _)) return;      // chess/control message, not chat (check the raw text, like the foreground)
        string body = msg.Text;
        // Peel off the sender's self-destruct TTL header (honour its lifetime), then a resend marker — matching MainPage.
        int ttlSeconds = 0;
        if (ProtocolMessage.TryDecodeChatTtl(body, out var ts, out var stripped)) { ttlSeconds = ts; body = stripped; }
        if (body.StartsWith(ProtocolMessage.ChatResendPrefix, StringComparison.Ordinal))
            body = body.Substring(ProtocolMessage.ChatResendPrefix.Length);

        string who = mesh.DescribeNode(msg.FromNode);

        // Persist to the shared cache (skip undecryptable text and honour the "cache messages" setting, exactly like
        // the foreground's CacheChat). Cache under the message's channel so LoadCachedChat renders it on next connect.
        if (AppSettings.CacheMessages && !msg.DecryptFailed)
        {
            string dmTag = isDm ? "DM ← " : "";
            string chan = isDm ? "" : $"{ChannelLabel(mesh, msg.Channel)} ";
            string detail = $"{FormatStamp(msg.RxTime)}{chan}".Trim();
            DateTime rxLocal = msg.RxTime != 0 ? DateTimeOffset.FromUnixTimeSeconds(msg.RxTime).LocalDateTime : DateTime.Now;
            DateTime expiresAt = ttlSeconds > 0 ? rxLocal.AddSeconds(ttlSeconds) : default;
            DeviceCache.AppendChat(host, msg.Channel, new DeviceCache.ChatMessage
            {
                Text = $"{dmTag}{who}: {body}",
                Detail = detail,
                Time = DateTime.Now,
                Id = Guid.NewGuid().ToString("N"),
                RxTime = msg.RxTime,
                ExpiresAt = expiresAt,
            });
        }

        // Notify (tapping it routes to the Chat tab — see MeshForegroundService).
        string title = isDm ? $"DM from {who}" : who;
        MeshForegroundService.PostMessage(title, msg.DecryptFailed ? "⚠ message (decryption failed)" : body);
    }

    // "[MM-dd HH:mm:ss] " from the device rx_time (local), mirroring MainPage.Stamp so cached rows read the same.
    static string FormatStamp(uint rxTime)
    {
        DateTime when = rxTime != 0 ? DateTimeOffset.FromUnixTimeSeconds(rxTime).LocalDateTime : DateTime.Now;
        return $"[{when:MM-dd HH:mm:ss}] ";
    }

    // "Robotnic [1]" (or just "[1]" when unnamed) — mirrors MainPage.ChannelLabel.
    static string ChannelLabel(MeshtasticHttpClient mesh, uint index)
    {
        foreach (var c in mesh.GetAvailableChannels())
            if (c.Index == index && !string.IsNullOrWhiteSpace(c.Name))
                return $"{c.Name} [{index}]";
        return $"[{index}]";
    }
}
