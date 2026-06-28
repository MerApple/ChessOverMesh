using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using ChessOverMesh.Mesh;
using Google.Protobuf;
using Meshtastic.Protobufs;

namespace Meshtastic.Proxy;

/// <summary>
/// Holds the single link to a Meshtastic device and multiplexes it to many TLS clients (GUI / MAUI), so several
/// apps can share one radio — which the device itself doesn't allow (TCP 4403 is single-client; HTTP /fromradio is
/// a destructive shared queue).
///
/// Robustness: the TLS listener is ALWAYS on, and the device runs in a connect/reconnect loop, so:
///   * Apps can connect to the proxy before the device is up (their want_config waits briefly for the device).
///   * If the device drops, the proxy stays up, drops its clients (so the apps clearly show "disconnected" and
///     auto-reconnect), and keeps trying to reconnect to the device. When it's back, clients re-sync on reconnect.
///
/// Wire model (both sides use the Meshtastic stream framing [0x94 0xc3][len-hi][len-lo][payload]):
///   * Device -> clients: every FromRadio.packet is broadcast to all clients (this includes the device's echo of
///     anything a client sent, which is how every client ends up seeing every message — "shared view").
///   * Client -> device: a ToRadio.want_config is answered locally from a cached device dump; anything else
///     (a packet/heartbeat) is written to the device.
/// </summary>
internal sealed class ProxyHub
{
    private const uint ProxyWantConfigId = 0xC0FFEE01;   // the proxy's own want_config id (distinct from clients')

    private readonly Func<CancellationToken, Task<IMeshTransport>> _connectDevice;
    private readonly X509Certificate2 _cert;
    private readonly int _port;
    private readonly Action<string> _log;
    private readonly bool _verbose;

    private readonly object _sync = new();
    private readonly List<Client> _clients = new();

    private volatile IMeshTransport? _device;   // the live device link, or null while (re)connecting
    private volatile bool _deviceSynced;        // true once the current device link's config dump is cached
    private volatile uint _myNodeNum;           // the shared device's node number (from MyInfo), for the send mirror

    // Packet ids a client sent: the radio's own loopback/relay of these (from == our node) is delivered ONLY back
    // to that sender, because the OTHER clients already got the message via the mirror — so they don't see it twice.
    private readonly Dictionary<uint, Client> _echoSuppress = new();
    private readonly Queue<uint> _echoOrder = new();

    // RAM-only ring buffer of the latest received text messages (raw frame + the message's rx_time), so a client
    // (re)connecting can catch up: it asks for everything newer than the newest it already has. Not persisted.
    private const int RecentCap = 100;
    private readonly List<(long RxTime, byte[] Frame)> _recent = new();

    // Cached device-config dump (raw FromRadio bytes), replayed to each new client on its want_config.
    private byte[]? _myInfo, _metadata;
    private readonly Dictionary<int, byte[]> _config = new();         // Config.PayloadVariantCase -> frame
    private readonly Dictionary<int, byte[]> _moduleConfig = new();   // ModuleConfig.PayloadVariantCase -> frame
    private readonly SortedDictionary<int, byte[]> _channels = new(); // channel index -> frame
    private readonly Dictionary<uint, byte[]> _nodes = new();         // node num -> frame

    public ProxyHub(Func<CancellationToken, Task<IMeshTransport>> connectDevice, X509Certificate2 cert, int port, Action<string> log, bool verbose = false)
    {
        _connectDevice = connectDevice; _cert = cert; _port = port; _log = log; _verbose = verbose;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var accept = AcceptLoopAsync(ct);     // clients can connect any time, even before the device is up
        var device = DeviceConnectLoopAsync(ct);
        await Task.WhenAny(accept, device);
    }

    // ---- Device side: connect, prime, pump, and reconnect forever ----

    private async Task DeviceConnectLoopAsync(CancellationToken ct)
    {
        int backoff = 2;
        while (!ct.IsCancellationRequested)
        {
            IMeshTransport dev;
            try { dev = await _connectDevice(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log($"Device unreachable ({ex.Message}); retrying in {backoff}s.");
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); } catch { break; }
                backoff = Math.Min(backoff * 2, 15);
                continue;
            }

            backoff = 2;
            _device = dev;
            try
            {
                await PrimeDeviceAsync(dev, ct);
                _deviceSynced = true;
                _log($"Device connected and primed (channels:{_channels.Count}, nodes:{_nodes.Count}).");
                DropStaleClients();   // clients that connected before the device was up: reconnect for a full sync
                await DeviceLoopAsync(dev, ct);   // returns when the device link drops
            }
            catch (OperationCanceledException) { dev.Dispose(); break; }
            catch (Exception ex) { _log($"Device link error: {ex.Message}"); }

            // Device dropped: tear down so the apps clearly see a disconnect, then loop to reconnect.
            _device = null;
            _deviceSynced = false;
            try { dev.Dispose(); } catch { }
            DropAllClients("device link lost");
            _log("Device disconnected — dropped clients, will reconnect.");
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); } catch { break; }
        }
    }

    // Pull the full config/channel/node dump (a few want_config rounds, like the app does) and cache it.
    private async Task PrimeDeviceAsync(IMeshTransport dev, CancellationToken ct)
    {
        for (int round = 0; round < 3 && !ct.IsCancellationRequested; round++)
        {
            await dev.WriteAsync(new ToRadio { WantConfigId = ProxyWantConfigId }.ToByteArray(), ct);
            while (!ct.IsCancellationRequested)
            {
                var bytes = await dev.ReadAsync(all: true, ct, TimeSpan.FromSeconds(6));
                if (bytes == null) break;   // round drained
                FromRadio fr;
                try { fr = FromRadio.Parser.ParseFrom(bytes); } catch { continue; }
                if (fr.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.ConfigCompleteId) break;
                CacheFrame(fr, bytes);
            }
        }
    }

    private async Task DeviceLoopAsync(IMeshTransport dev, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && dev.IsConnected)
        {
            var bytes = await dev.ReadAsync(all: false, ct, null);
            if (bytes == null) continue;

            FromRadio fr;
            try { fr = FromRadio.Parser.ParseFrom(bytes); } catch { continue; }

            // The proxy's own want_config completion isn't meant for clients.
            if (fr.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.ConfigCompleteId) continue;

            // A packet from our own node is the radio looping back / relaying something a client sent. The other
            // clients already got it via the mirror, so deliver this only to the original sender (for its ack/relay
            // tracking) instead of broadcasting it to everyone again.
            if (fr.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.Packet
                && _myNodeNum != 0 && fr.Packet.From == _myNodeNum)
            {
                Client? sender;
                lock (_sync) _echoSuppress.TryGetValue(fr.Packet.Id, out sender);
                if (sender != null)
                {
                    if (_verbose) _log($"Device -> sender only: {fr.Packet.Decoded?.Portnum} id 0x{fr.Packet.Id:x8}");
                    try { await SendFrameAsync(sender, bytes, CancellationToken.None); } catch { Remove(sender); }
                    continue;
                }
            }

            // Keep the replay cache current (new nodes, config changes), then forward live to every client.
            CacheFrame(fr, bytes);
            // Remember received text messages (RAM ring) so a (re)connecting client can catch up on what it missed.
            if (fr.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.Packet
                && fr.Packet.Decoded?.Portnum == PortNum.TextMessageApp)
                lock (_sync)
                {
                    _recent.Add((fr.Packet.RxTime, bytes));
                    if (_recent.Count > RecentCap) _recent.RemoveAt(0);
                }
            if (_verbose)
            {
                if (fr.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.Packet && fr.Packet.Decoded != null)
                    _log($"Device -> clients: {fr.Packet.Decoded.Portnum} from !{fr.Packet.From:x8}");
                else if (fr.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.NodeInfo)
                    _log($"Device -> clients: NodeInfo !{fr.NodeInfo.Num:x8}");
            }
            await BroadcastAsync(bytes);
        }
    }

    private void CacheFrame(FromRadio fr, byte[] bytes)
    {
        lock (_sync)
        {
            switch (fr.PayloadVariantCase)
            {
                case FromRadio.PayloadVariantOneofCase.MyInfo: _myInfo = bytes; _myNodeNum = fr.MyInfo.MyNodeNum; break;
                case FromRadio.PayloadVariantOneofCase.Metadata: _metadata = bytes; break;
                case FromRadio.PayloadVariantOneofCase.Config: _config[(int)fr.Config.PayloadVariantCase] = bytes; break;
                case FromRadio.PayloadVariantOneofCase.ModuleConfig: _moduleConfig[(int)fr.ModuleConfig.PayloadVariantCase] = bytes; break;
                case FromRadio.PayloadVariantOneofCase.Channel: _channels[fr.Channel.Index] = bytes; break;
                case FromRadio.PayloadVariantOneofCase.NodeInfo: _nodes[fr.NodeInfo.Num] = bytes; break;
                default: break;   // packets and other live frames aren't cached
            }
        }
    }

    // ---- Client side ----

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _log($"Listening for clients on port {_port} (TLS).");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                _ = HandleClientAsync(tcp, ct);
            }
        }
        finally { listener.Stop(); }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        tcp.NoDelay = true;
        var remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        SslStream? ssl = null;
        Client? client = null;
        try
        {
            ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(_cert, clientCertificateRequired: false, checkCertificateRevocation: false);
            client = new Client(ssl);
            lock (_sync) _clients.Add(client);
            _log($"Client connected: {remote}  (total {_clients.Count})");

            while (!ct.IsCancellationRequested)
            {
                var frame = await client.ReadFrameAsync(ct);
                if (frame == null) break;   // client disconnected
                await HandleClientFrameAsync(client, frame, ct);
            }
        }
        catch (Exception ex) { _log($"Client {remote} error: {ex.Message}"); }
        finally
        {
            if (client != null) Remove(client);
            try { ssl?.Dispose(); } catch { }
            try { tcp.Dispose(); } catch { }
            _log($"Client disconnected: {remote}");
        }
    }

    private async Task HandleClientFrameAsync(Client client, byte[] toRadioBytes, CancellationToken ct)
    {
        // Proxy control frame (not a real ToRadio): "MPXY" + cmd + payload. cmd 0x01 = backfill request, followed by
        // an int64 (little-endian) "since" rx_time — replay every cached text message newer than that to this client.
        if (toRadioBytes.Length >= 13 && toRadioBytes[0] == (byte)'M' && toRadioBytes[1] == (byte)'P'
            && toRadioBytes[2] == (byte)'X' && toRadioBytes[3] == (byte)'Y')
        {
            if (toRadioBytes[4] == 0x01)
            {
                long since = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(toRadioBytes.AsSpan(5));
                await ReplayRecentMessagesAsync(client, since, ct);
            }
            return;
        }

        ToRadio tr;
        try { tr = ToRadio.Parser.ParseFrom(toRadioBytes); }
        catch { return; }

        if (tr.PayloadVariantCase == ToRadio.PayloadVariantOneofCase.WantConfigId)
        {
            await ReplayConfigAsync(client, tr.WantConfigId, ct);   // answer locally; don't disturb the device
            return;
        }
        // A packet / heartbeat / anything else: send it to the device (if we have one).
        var dev = _device;
        if (dev == null) { _log("Dropping client packet: device offline."); return; }

        if (tr.PayloadVariantCase == ToRadio.PayloadVariantOneofCase.Packet && tr.Packet.Decoded != null)
        {
            _log($"Client -> device: {tr.Packet.Decoded.Portnum} to !{tr.Packet.To:x8} (want_response={tr.Packet.Decoded.WantResponse})");
            // Mirror the message straight to the OTHER clients so they see it even if the radio doesn't loop a
            // broadcast back, and remember the id so the radio's own loopback/relay goes only to this sender.
            if (_myNodeNum != 0 && tr.Packet.Id != 0)
            {
                RememberEchoSender(tr.Packet.Id, client);
                var synth = tr.Packet.Clone();
                synth.From = _myNodeNum;                       // present it as coming from the shared device node
                var synthBytes = new FromRadio { Packet = synth }.ToByteArray();
                await MirrorToOthersAsync(client, synthBytes);
            }
        }
        try { await dev.WriteAsync(toRadioBytes, ct); }
        catch (Exception ex) { _log($"Device write failed: {ex.Message}"); }
    }

    // Replays the cached device dump to one client, ending with config_complete carrying that client's id, so the
    // app's handshake completes as if it had talked to the device directly. Waits briefly for the device if a
    // client connected before it came up.
    private async Task ReplayConfigAsync(Client client, uint wantId, CancellationToken ct)
    {
        for (int i = 0; i < 80 && !_deviceSynced && !ct.IsCancellationRequested; i++)
            await Task.Delay(100, ct);   // up to ~8s for the device to come up / finish priming
        if (!_deviceSynced) client.ServedEmpty = true;   // serving without a device — drop on device-up to re-sync

        List<byte[]> frames = new();
        lock (_sync)
        {
            if (_myInfo != null) frames.Add(_myInfo);
            if (_metadata != null) frames.Add(_metadata);
            frames.AddRange(_config.Values);
            frames.AddRange(_moduleConfig.Values);
            frames.AddRange(_channels.Values);
            frames.AddRange(_nodes.Values);
        }
        foreach (var f in frames) await SendFrameAsync(client, f, ct);
        await SendFrameAsync(client, new FromRadio { ConfigCompleteId = wantId }.ToByteArray(), ct);
    }

    // Records which client sent a packet id, so the radio's loopback/relay of it is routed back to that sender only.
    // Bounded so it can't grow without limit (older entries just stop being suppressed — at worst a rare duplicate).
    private void RememberEchoSender(uint id, Client sender)
    {
        lock (_sync)
        {
            if (!_echoSuppress.ContainsKey(id)) _echoOrder.Enqueue(id);
            _echoSuppress[id] = sender;
            while (_echoOrder.Count > 512) _echoSuppress.Remove(_echoOrder.Dequeue());
        }
    }

    // Sends a frame to every connected client except the one that originated it.
    private async Task MirrorToOthersAsync(Client origin, byte[] payload)
    {
        Client[] others;
        lock (_sync) others = _clients.Where(c => c != origin).ToArray();
        foreach (var c in others)
        {
            try { await SendFrameAsync(c, payload, CancellationToken.None); }
            catch { Remove(c); }
        }
    }

    // Replays the cached received text messages newer than the client's "since" rx_time, so it catches up on what it
    // missed while disconnected (RAM only; bounded to the last 100 messages).
    private async Task ReplayRecentMessagesAsync(Client client, long since, CancellationToken ct)
    {
        byte[][] frames;
        lock (_sync) frames = _recent.Where(m => m.RxTime > since).Select(m => m.Frame).ToArray();
        if (frames.Length > 0) _log($"Backfill: replaying {frames.Length} message(s) newer than {since} to a client.");
        foreach (var f in frames) await SendFrameAsync(client, f, ct);
    }

    private async Task BroadcastAsync(byte[] payload)
    {
        Client[] snapshot;
        lock (_sync) snapshot = _clients.ToArray();
        foreach (var c in snapshot)
        {
            try { await SendFrameAsync(c, payload, CancellationToken.None); }
            catch { Remove(c); }
        }
    }

    private void DropAllClients(string reason)
    {
        Client[] snapshot;
        lock (_sync) { snapshot = _clients.ToArray(); _clients.Clear(); }
        foreach (var c in snapshot)
            try { c.Stream.Dispose(); } catch { }
        if (snapshot.Length > 0) _log($"Dropped {snapshot.Length} client(s): {reason}.");
    }

    // Drops only clients that synced while the device was down (empty config), so they reconnect for a full sync.
    private void DropStaleClients()
    {
        Client[] stale;
        lock (_sync) { stale = _clients.Where(c => c.ServedEmpty).ToArray(); foreach (var c in stale) _clients.Remove(c); }
        foreach (var c in stale)
            try { c.Stream.Dispose(); } catch { }
        if (stale.Length > 0) _log($"Dropped {stale.Length} early client(s) so they reconnect with a full sync.");
    }

    private static async Task SendFrameAsync(Client c, byte[] payload, CancellationToken ct)
    {
        var framed = Frame(payload);
        await c.WriteLock.WaitAsync(ct);
        try { await c.Stream.WriteAsync(framed, ct); await c.Stream.FlushAsync(ct); }
        finally { c.WriteLock.Release(); }
    }

    private void Remove(Client c)
    {
        lock (_sync) _clients.Remove(c);
        try { c.Stream.Dispose(); } catch { }
    }

    private static byte[] Frame(byte[] p)
    {
        var f = new byte[p.Length + 4];
        f[0] = 0x94; f[1] = 0xc3;
        f[2] = (byte)((p.Length >> 8) & 0xFF);
        f[3] = (byte)(p.Length & 0xFF);
        Buffer.BlockCopy(p, 0, f, 4, p.Length);
        return f;
    }

    // One connected app: its TLS stream plus a write lock and a frame accumulator for its inbound bytes.
    private sealed class Client
    {
        public readonly SslStream Stream;
        public readonly SemaphoreSlim WriteLock = new(1, 1);
        public volatile bool ServedEmpty;   // completed want_config while the device was down (needs a re-sync)
        private readonly List<byte> _acc = new(8192);
        private readonly byte[] _buf = new byte[4096];

        public Client(SslStream stream) => Stream = stream;

        // Reads one complete framed payload, accumulating socket reads until a full frame is available; null on EOF.
        public async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
        {
            while (true)
            {
                if (TryExtract(out var frame)) return frame;
                int n = await Stream.ReadAsync(_buf, ct);
                if (n <= 0) return null;
                for (int i = 0; i < n; i++) _acc.Add(_buf[i]);
            }
        }

        private bool TryExtract(out byte[] frame)
        {
            frame = Array.Empty<byte>();
            while (true)
            {
                int start = -1;
                for (int i = 0; i + 1 < _acc.Count; i++)
                    if (_acc[i] == 0x94 && _acc[i + 1] == 0xc3) { start = i; break; }
                if (start < 0)
                {
                    byte last = _acc.Count > 0 ? _acc[^1] : (byte)0;
                    _acc.Clear();
                    if (last == 0x94) _acc.Add(last);   // keep a possible split marker
                    return false;
                }
                if (start > 0) _acc.RemoveRange(0, start);
                if (_acc.Count < 4) return false;
                int len = (_acc[2] << 8) | _acc[3];
                if (_acc.Count < 4 + len) return false;
                frame = _acc.GetRange(4, len).ToArray();
                _acc.RemoveRange(0, 4 + len);
                return true;
            }
        }
    }
}
