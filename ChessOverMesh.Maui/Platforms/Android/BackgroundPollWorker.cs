using Android.Content;
using AndroidX.Work;
using ChessOverMesh.Game;
using ChessOverMesh.Mesh;
using Java.Util.Concurrent;

namespace ChessOverMesh.Maui;

/// <summary>
/// Periodic background job (~every 15 min, in Doze maintenance windows). When enabled, it connects to the device
/// over WiFi, drains any chat messages the device buffered while we were asleep, posts a notification for each,
/// and disconnects. It's a coarse fallback — Android won't run this more often than ~15 min — for real-time
/// background reception, BLE is the right transport.
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
                {
                    if (!chatChannels.Contains(msg.Channel)) continue;
                    string body = msg.Text;
                    if (body.StartsWith(ProtocolMessage.ChatResendPrefix, StringComparison.Ordinal))
                        body = body.Substring(ProtocolMessage.ChatResendPrefix.Length);
                    if (ProtocolMessage.TryParse(body, out _)) continue;   // chess/control message, not chat
                    string who = mesh.DescribeNode(msg.FromNode);
                    MeshForegroundService.PostMessage(who, msg.DecryptFailed ? "⚠ message (decryption failed)" : body);
                }
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
}
