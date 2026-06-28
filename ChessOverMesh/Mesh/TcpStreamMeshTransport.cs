using System.Net.Sockets;
using System.Threading.Channels;

namespace ChessOverMesh.Mesh;

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
    private readonly NetworkStream _stream;
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

    private TcpStreamMeshTransport(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
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
        return new TcpStreamMeshTransport(client);
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
