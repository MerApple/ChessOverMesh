using ChessOverMesh.Map;

namespace ChessOverMesh.Maui;

/// <summary>
/// App-wide owner of the offline map tile cache and the loopback <see cref="MapTileServer"/> that feeds Leaflet
/// and cached tiles to the in-app map WebView. Both are created lazily on first use and live for the app's
/// lifetime (the server binds a loopback port; Android permits cleartext to 127.0.0.1 — see AndroidManifest).
/// </summary>
internal static class MapCacheService
{
    private static MapTileCache? _cache;
    private static MapTileServer? _server;
    private static readonly object Gate = new();

    /// <summary>The on-disk tile cache, stored under the app's private data directory.</summary>
    public static MapTileCache Cache
    {
        get
        {
            lock (Gate)
                return _cache ??= new MapTileCache(Path.Combine(FileSystem.AppDataDirectory, "mapcache"));
        }
    }

    /// <summary>Starts (once) the loopback tile server and returns its base URL (e.g. http://127.0.0.1:PORT) for
    /// <see cref="ChessOverMesh.Mesh.NodeMap.Html"/>, or null if the socket couldn't be bound (the map then loads
    /// Leaflet + tiles online instead).</summary>
    public static string? EnsureServerBaseUrl()
    {
        lock (Gate)
        {
            if (_server == null)
            {
                // Cache-only: the server never fetches from the network. The map's online layer goes straight to
                // OpenStreetMap; the offline layer is served from the cache here.
                var server = new MapTileServer(Cache, allowOnlineFallback: false);
                if (!server.Start()) { server.Dispose(); return null; }
                _server = server;
            }
            return _server.BaseUrl;
        }
    }
}
