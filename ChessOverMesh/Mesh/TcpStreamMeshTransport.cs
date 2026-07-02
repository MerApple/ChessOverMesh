using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace ChessOverMesh.Mesh;

/// <summary>Thrown when a <see cref="Meshtastic.Proxy"/> requires a username/password the client didn't supply or
/// rejected the ones it did. <see cref="Rejected"/> distinguishes "credentials refused" (retry with a correction)
/// from "auth required, none given" (prompt for the first time).</summary>
public sealed class ProxyAuthException : Exception
{
    public bool Rejected { get; }
    public ProxyAuthException(string message, bool rejected) : base(message) => Rejected = rejected;
}

/// <summary>
/// <see cref="IMeshTransport"/> over the Meshtastic TCP stream API (default port 4403) — the same link the
/// native app uses, and far faster than the HTTP REST API for the initial sync. A single persistent socket
/// streams framed protobufs (<c>[0x94 0xc3][len-hi][len-lo][payload]</c>) back-to-back, so draining the whole
/// node DB is one burst over one connection instead of a slow HTTP round-trip per packet.
///
/// A background reader continuously parses incoming frames into a queue; <see cref="ReadAsync"/> dequeues
/// (returning null when the queue is momentarily empty), and <see cref="WriteAsync"/> frames + sends on the
/// same full-duplex socket — so sends never wait on reads.
/// </summary>
public sealed class TcpStreamMeshTransport : IMeshTransport
{
    public const int DefaultPort = 4403;
    private static readonly TimeSpan DefaultReadWait = TimeSpan.FromSeconds(2);
    // After we write (e.g. want_config), the device replies with a burst of frames; wait longer for the next
    // frame during that window so a brief gap mid-dump doesn't look like "queue drained" and truncate the read.
    private static readonly TimeSpan PostWriteReadWait = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PostWriteWindow = TimeSpan.FromSeconds(8);

    private readonly TcpClient _client;
    private readonly Stream _stream;   // a NetworkStream for a plain device link, or an SslStream for the TLS proxy
    private readonly Channel<byte[]> _inbox =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _readerCts = new();
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private volatile bool _faulted;   // set when a read/write fails or the peer closes the socket
    private bool _disposed;

    /// <summary>False once the socket has dropped (a write/read failed or the device closed it).</summary>
    public bool IsConnected => !_faulted && !_disposed;

    /// <summary>The TCP link is held open, so the device expects periodic heartbeats to keep it alive.</summary>
    public bool NeedsKeepAlive => true;

    /// <summary>The persistent socket reports its own liveness (faults on drop + OS keep-alive), so the app must not
    /// open a competing connection to probe this single-client port — see <see cref="IMeshTransport.SelfReportsLiveness"/>.</summary>
    public bool SelfReportsLiveness => true;

    private TcpStreamMeshTransport(TcpClient client, Stream stream)
    {
        _client = client;
        _stream = stream;
        _ = Task.Run(() => ReadLoopAsync(_readerCts.Token));
    }

    /// <summary>Opens a TCP stream connection to <paramref name="host"/>:<paramref name="port"/>. Throws if the
    /// port isn't reachable (so the caller can fall back to HTTP).</summary>
    public static async Task<TcpStreamMeshTransport> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false); }
        catch { client.Dispose(); throw; }
        EnableKeepAlive(client.Client);
        return new TcpStreamMeshTransport(client, client.GetStream());
    }

    // Proxy control frames ("MPXY" + cmd + data), out-of-band from the ToRadio/FromRadio stream.
    private static readonly byte[] Mpxy = { (byte)'M', (byte)'P', (byte)'X', (byte)'Y' };
    private const byte MpxyAuth = 0x02;        // client → proxy: username/password
    private const byte MpxyAuthResult = 0x03;  // proxy → client: 0x01 ok / 0x00 fail
    private const byte MpxyHello = 0x04;        // proxy → client (first frame): 0x01 = auth required
    private static readonly TimeSpan HelloWait = TimeSpan.FromSeconds(3);   // how long to wait for the proxy's hello

    /// <summary>Opens a TLS connection to a <see cref="Meshtastic.Proxy"/> that speaks the same framed stream
    /// protocol. The proxy uses a self-signed certificate (transport encryption, not identity), so any cert is
    /// accepted — exactly as the HTTP transport accepts the device's self-signed HTTPS. If the proxy requires a
    /// username/password it announces that in its opening frame; supply <paramref name="user"/>/<paramref name="pass"/>
    /// or a <see cref="ProxyAuthException"/> is thrown so the caller can prompt.</summary>
    public static async Task<TcpStreamMeshTransport> ConnectTlsAsync(string host, int port, TimeSpan timeout,
                                                                     string? user = null, string? pass = null, CancellationToken ct = default)
    {
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false); }
        catch { client.Dispose(); throw; }
        EnableKeepAlive(client.Client);
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, _, _, _) => true);
        try { await ssl.AuthenticateAsClientAsync(host).ConfigureAwait(false); }
        catch { ssl.Dispose(); client.Dispose(); throw; }

        try
        {
            // The proxy greets us with a hello frame stating whether it requires auth (an older proxy sends nothing,
            // so a timeout here means "no auth" and we just proceed). Consumed before the read loop starts.
            var hello = await ReadFrameAsync(ssl, HelloWait, cts.Token).ConfigureAwait(false);
            bool authRequired = IsControl(hello, MpxyHello) && hello!.Length >= 6 && hello[5] == 0x01;
            if (authRequired)
            {
                if (string.IsNullOrEmpty(user))
                    throw new ProxyAuthException("This proxy requires a username and password.", rejected: false);
                await WriteFrameAsync(ssl, BuildAuthPayload(user!, pass ?? ""), cts.Token).ConfigureAwait(false);
                var result = await ReadFrameAsync(ssl, timeout, cts.Token).ConfigureAwait(false);
                bool ok = IsControl(result, MpxyAuthResult) && result!.Length >= 6 && result[5] == 0x01;
                if (!ok) throw new ProxyAuthException("The proxy rejected the username or password.", rejected: true);
            }
        }
        catch (ProxyAuthException) { ssl.Dispose(); client.Dispose(); throw; }
        catch { ssl.Dispose(); client.Dispose(); throw; }

        return new TcpStreamMeshTransport(client, ssl);
    }

    private static bool IsControl(byte[]? frame, byte cmd) =>
        frame is { Length: >= 5 } && frame[0] == Mpxy[0] && frame[1] == Mpxy[1] && frame[2] == Mpxy[2] && frame[3] == Mpxy[3] && frame[4] == cmd;

    // "MPXY" + 0x02 + [userLen:1][user utf8][pass utf8]. Username capped at 255 bytes; password fills the rest.
    private static byte[] BuildAuthPayload(string user, string pass)
    {
        var u = Encoding.UTF8.GetBytes(user);
        var p = Encoding.UTF8.GetBytes(pass);
        if (u.Length > 255) throw new ArgumentException("Username too long.");
        var payload = new byte[6 + u.Length + p.Length];
        Buffer.BlockCopy(Mpxy, 0, payload, 0, 4);
        payload[4] = MpxyAuth;
        payload[5] = (byte)u.Length;
        Buffer.BlockCopy(u, 0, payload, 6, u.Length);
        Buffer.BlockCopy(p, 0, payload, 6 + u.Length, p.Length);
        return payload;
    }

    // Frames a payload with the [0x94 0xc3][len-hi][len-lo] header and writes it (used for the pre-read-loop handshake).
    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        var framed = new byte[payload.Length + 4];
        framed[0] = 0x94; framed[1] = 0xc3;
        framed[2] = (byte)((payload.Length >> 8) & 0xFF); framed[3] = (byte)(payload.Length & 0xFF);
        Buffer.BlockCopy(payload, 0, framed, 4, payload.Length);
        await stream.WriteAsync(framed, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // Reads one complete framed payload (waiting up to <paramref name="wait"/>), or null on timeout / EOF. Only used
    // for the handshake, before the background read loop takes over the stream.
    private static async Task<byte[]?> ReadFrameAsync(Stream stream, TimeSpan wait, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        var acc = new List<byte>(256);
        var buf = new byte[256];
        try
        {
            while (true)
            {
                if (TryExtractFrame(acc, out var frame)) return frame;
                int n = await stream.ReadAsync(buf.AsMemory(), cts.Token).ConfigureAwait(false);
                if (n <= 0) return null;
                for (int i = 0; i < n; i++) acc.Add(buf[i]);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static bool TryExtractFrame(List<byte> acc, out byte[] frame)
    {
        frame = Array.Empty<byte>();
        int start = -1;
        for (int i = 0; i + 1 < acc.Count; i++)
            if (acc[i] == 0x94 && acc[i + 1] == 0xc3) { start = i; break; }
        if (start < 0) return false;
        if (start > 0) acc.RemoveRange(0, start);
        if (acc.Count < 4) return false;
        int len = (acc[2] << 8) | acc[3];
        if (acc.Count < 4 + len) return false;
        frame = acc.GetRange(4, len).ToArray();
        acc.RemoveRange(0, 4 + len);
        return true;
    }

    // Enable OS-level TCP keep-alive: the kernel sends periodic probes so a half-open connection (WiFi dropped,
    // router/NAT evicted it) is detected within ~35s even when we're not sending — instead of only being noticed
    // when the next write (a chat send/resend) fails. The probe traffic can also keep an idle connection from
    // being dropped by the router in the first place.
    private static void EnableKeepAlive(System.Net.Sockets.Socket socket)
    {
        try { socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
        // Fine-grained timing (not supported on every platform — best effort): probe after 20s idle, every 5s, give up after 3.
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 20); } catch { }
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5); } catch { }
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3); } catch { }
    }

    public async Task WriteAsync(byte[] toRadio, CancellationToken ct)
    {
        var framed = new byte[toRadio.Length + 4];
        framed[0] = 0x94;
        framed[1] = 0xc3;
        framed[2] = (byte)((toRadio.Length >> 8) & 0xFF);
        framed[3] = (byte)(toRadio.Length & 0xFF);
        Buffer.BlockCopy(toRadio, 0, framed, 4, toRadio.Length);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);   // don't interleave frames from concurrent sends
        try
        {
            await _stream.WriteAsync(framed, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            _lastWriteUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { throw; }
        catch { _faulted = true; throw; }   // socket write failed (net_io_writefailure) — the link is dead
        finally { _writeGate.Release(); }
    }

    public async Task<byte[]?> ReadAsync(bool all, CancellationToken ct, TimeSpan? requestTimeout = null)
    {
        if (_inbox.Reader.TryRead(out var ready)) return ready;   // already-parsed frame — instant

        // Within the burst window after a write (e.g. the want_config dump), wait longer so a brief inter-frame
        // gap isn't mistaken for an empty queue — that truncation would drop later channels/nodes.
        var wait = requestTimeout
            ?? (DateTime.UtcNow - _lastWriteUtc < PostWriteWindow ? PostWriteReadWait : DefaultReadWait);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try
        {
            if (await _inbox.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false) &&
                _inbox.Reader.TryRead(out var item))
                return item;
            return null;   // channel completed (socket closed) with nothing queued
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;   // our wait timed out — nothing queued right now
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var acc = new List<byte>(8192);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) break;   // socket closed by peer
                for (int i = 0; i < n; i++) acc.Add(buffer[i]);
                DrainFrames(acc);
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
        catch (Exception) { /* socket error */ }
        finally
        {
            // If the loop ended for any reason other than us disposing, the link is dead — flag it so the app
            // detects the drop (IsConnected) and reconnects, instead of silently appearing connected.
            if (!_disposed) _faulted = true;
            _inbox.Writer.TryComplete();
        }
    }

    // Parse every complete frame currently in the accumulator into the inbox, leaving any partial tail behind.
    private void DrainFrames(List<byte> acc)
    {
        while (true)
        {
            if (acc.Count < 2) return;
            // Locate the next frame-start marker, discarding any junk before it.
            int start = -1;
            for (int i = 0; i + 1 < acc.Count; i++)
                if (acc[i] == 0x94 && acc[i + 1] == 0xc3) { start = i; break; }
            if (start < 0)
            {
                byte last = acc[acc.Count - 1];        // keep a lone trailing 0x94 (possible split marker)
                acc.Clear();
                if (last == 0x94) acc.Add(last);
                return;
            }
            if (start > 0) acc.RemoveRange(0, start);   // drop bytes before the marker
            if (acc.Count < 4) return;                   // need the 4-byte header
            int len = (acc[2] << 8) | acc[3];
            if (acc.Count < 4 + len) return;             // need the full payload
            _inbox.Writer.TryWrite(acc.GetRange(4, len).ToArray());
            acc.RemoveRange(0, 4 + len);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _readerCts.Cancel(); } catch { /* ignore */ }
        try { _client.Dispose(); } catch { /* ignore */ }
        _readerCts.Dispose();
        _writeGate.Dispose();
    }
}
