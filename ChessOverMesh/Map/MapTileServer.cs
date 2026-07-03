using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace ChessOverMesh.Map;

/// <summary>
/// A tiny loopback HTTP server (127.0.0.1, ephemeral port) that serves the offline map's static assets and tiles
/// to the in-app WebView (MAUI) or the system browser (desktop), so the same <see cref="NodeMap"/> page renders
/// with no internet:
///   <c>GET /leaflet/leaflet.js|leaflet.css|images/*</c> — Leaflet, bundled as embedded resources.
///   <c>GET /tiles/{z}/{x}/{y}.png</c>                    — a map tile from <see cref="MapTileCache"/> (with an
///                                                          optional transparent-when-online-fallback fetch).
/// Binding to the loopback address means no firewall prompt and nothing is exposed off the machine. The port is
/// chosen by the OS; read <see cref="BaseUrl"/> after <see cref="Start"/> and hand it to
/// <see cref="NodeMap.Html"/>.
/// </summary>
public sealed class MapTileServer : IDisposable
{
    private readonly MapTileCache _cache;
    private readonly bool _allowOnlineFallback;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    // A 1×1 transparent PNG returned for a tile that isn't cached (and can't be fetched) — the map shows a blank
    // square there instead of a broken-image icon.
    private static readonly byte[] BlankTile = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <param name="allowOnlineFallback">When true, a tile that isn't cached is fetched from the tile server on
    /// demand (and saved), so browsing online transparently fills the cache. When false, only cached tiles are
    /// served (a strict-offline mode).</param>
    public MapTileServer(MapTileCache cache, bool allowOnlineFallback = true)
    {
        _cache = cache;
        _allowOnlineFallback = allowOnlineFallback;
    }

    /// <summary>The port the server is listening on (0 until <see cref="Start"/> succeeds).</summary>
    public int Port { get; private set; }

    /// <summary>The base URL to reference from the map page (e.g. <c>http://127.0.0.1:49152</c>), or null before
    /// <see cref="Start"/>.</summary>
    public string? BaseUrl => Port > 0 ? $"http://127.0.0.1:{Port}" : null;

    /// <summary>Starts listening on a loopback ephemeral port. Idempotent. Returns true on success; false if the
    /// socket couldn't be bound (the caller then falls back to the online CDN/tile URLs).</summary>
    public bool Start()
    {
        if (_listener != null) return true;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            return true;
        }
        catch { _listener = null; Port = 0; return false; }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                // Serve requests on this connection until the client closes it or goes idle (keep-alive).
                while (!ct.IsCancellationRequested)
                {
                    var path = await ReadRequestPathAsync(stream, ct).ConfigureAwait(false);
                    if (path == null) break;   // EOF / malformed / non-GET
                    bool keepAlive = await RouteAsync(stream, path, ct).ConfigureAwait(false);
                    if (!keepAlive) break;
                }
            }
        }
        catch { /* connection-level errors are non-fatal */ }
    }

    // Reads one HTTP request's headers and returns the request-target of a GET (e.g. "/tiles/5/16/10.png"), or
    // null on EOF, timeout, a non-GET method, or a malformed/oversized request.
    private static async Task<string?> ReadRequestPathAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[2048];
        var sb = new StringBuilder();
        int total = 0;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));   // close idle keep-alive connections
        try
        {
            while (total < 16 * 1024)
            {
                int n;
                try { n = await stream.ReadAsync(buf, timeoutCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
                if (n <= 0) return null;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                total += n;
                int end = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0) continue;

                string firstLine = sb.ToString(0, end).Split("\r\n")[0];
                var parts = firstLine.Split(' ');
                if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase)) return null;
                return parts[1];
            }
        }
        catch { /* fall through */ }
        return null;
    }

    // Routes a request, writes the response, and returns whether to keep the connection alive.
    private async Task<bool> RouteAsync(NetworkStream stream, string target, CancellationToken ct)
    {
        // Strip any query string and normalise.
        string path = target.Split('?')[0];

        if (path.StartsWith("/tiles/", StringComparison.Ordinal))
        {
            var bytes = await ServeTileAsync(path, ct).ConfigureAwait(false);
            await WriteResponseAsync(stream, 200, "image/png", bytes, ct).ConfigureAwait(false);
            return true;
        }

        if (path.StartsWith("/leaflet/", StringComparison.Ordinal))
        {
            string rel = path["/leaflet/".Length..];
            var (bytes, contentType) = LoadAsset(rel);
            if (bytes == null) { await WriteResponseAsync(stream, 404, "text/plain", Encoding.ASCII.GetBytes("not found"), ct).ConfigureAwait(false); return true; }
            await WriteResponseAsync(stream, 200, contentType, bytes, ct).ConfigureAwait(false);
            return true;
        }

        await WriteResponseAsync(stream, 404, "text/plain", Encoding.ASCII.GetBytes("not found"), ct).ConfigureAwait(false);
        return true;
    }

    // Parses "/tiles/{z}/{x}/{y}.png" and returns the tile bytes, or a blank tile when it isn't available.
    private async Task<byte[]> ServeTileAsync(string path, CancellationToken ct)
    {
        try
        {
            string rel = path["/tiles/".Length..];
            if (rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) rel = rel[..^4];
            var seg = rel.Split('/');
            if (seg.Length == 3 &&
                int.TryParse(seg[0], out int z) && int.TryParse(seg[1], out int x) && int.TryParse(seg[2], out int y))
            {
                var bytes = await _cache.GetTileAsync(z, x, y, _allowOnlineFallback, ct).ConfigureAwait(false);
                if (bytes != null) return bytes;
            }
        }
        catch { /* fall through to the blank tile */ }
        return BlankTile;
    }

    // ---- Embedded Leaflet assets -------------------------------------------------------------------------

    private static readonly Assembly Asm = typeof(MapTileServer).Assembly;

    // Maps a request path under /leaflet/ (e.g. "leaflet.js", "images/marker-icon.png") to an embedded resource.
    // Embedded resource names replace path separators with '.', so we match by suffix.
    private static (byte[]? Bytes, string ContentType) LoadAsset(string rel)
    {
        string suffix = "." + rel.Replace('/', '.');
        string? name = Asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name == null) return (null, "text/plain");
        try
        {
            using var s = Asm.GetManifestResourceStream(name);
            if (s == null) return (null, "text/plain");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return (ms.ToArray(), ContentTypeFor(rel));
        }
        catch { return (null, "text/plain"); }
    }

    private static string ContentTypeFor(string path) =>
        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "application/javascript" :
        path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css" :
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" :
        "application/octet-stream";

    // ---- Response writing --------------------------------------------------------------------------------

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string contentType, byte[] body, CancellationToken ct)
    {
        string reason = status switch { 200 => "OK", 404 => "Not Found", _ => "OK" };
        var header = new StringBuilder();
        header.Append($"HTTP/1.1 {status} {reason}\r\n");
        header.Append($"Content-Type: {contentType}\r\n");
        header.Append($"Content-Length: {body.Length}\r\n");
        header.Append("Cache-Control: max-age=86400\r\n");
        header.Append("Connection: keep-alive\r\n");
        header.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        Port = 0;
    }
}
