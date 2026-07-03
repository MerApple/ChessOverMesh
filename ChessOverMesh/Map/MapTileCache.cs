using System.Text.Json;

namespace ChessOverMesh.Map;

/// <summary>Progress of an in-flight <see cref="MapTileCache.CacheAreaAsync"/> download.</summary>
public readonly record struct TileDownloadProgress(int Done, int Total, int Downloaded, int Skipped, int Failed)
{
    public double Fraction => Total > 0 ? (double)Done / Total : 0.0;
}

/// <summary>Outcome of caching an area: how many tiles were fetched, already present, or failed.</summary>
public readonly record struct TileDownloadResult(int Total, int Downloaded, int Skipped, int Failed, bool Cancelled);

/// <summary>One cached region as the user asked for it, kept in a small manifest so the UI can list and manage
/// what's been downloaded for offline use.</summary>
public sealed class CachedRegion
{
    public string Name { get; set; } = "";
    public double South { get; set; }
    public double West { get; set; }
    public double North { get; set; }
    public double East { get; set; }
    public int MinZoom { get; set; }
    public int MaxZoom { get; set; }
    public long Tiles { get; set; }
    public DateTime When { get; set; }
}

/// <summary>
/// An on-disk cache of OpenStreetMap map tiles for offline use, stored as plain PNG files at
/// <c>{root}/{z}/{x}/{y}.png</c> (the standard slippy-map layout). Downloads the tiles covering a user-chosen
/// area/zoom range up front (<see cref="CacheAreaAsync"/>) and serves them back to the map — via
/// <see cref="MapTileServer"/> — with no network needed. Browsing the map online also fills the cache
/// transparently (<see cref="GetTileAsync"/> saves what it fetches), so revisited areas work offline afterward.
/// </summary>
/// <remarks>
/// Tiles come from OpenStreetMap's public server. Its tile usage policy forbids heavy bulk downloading, so this
/// caps a single area request (<see cref="MaxTilesPerRequest"/>), keeps concurrency low, sends a descriptive
/// User-Agent, and skips tiles already on disk. For a public/large deployment, point <see cref="TileUrlTemplate"/>
/// at a provider that permits offline caching (or your own tiles) instead.
/// </remarks>
public sealed class MapTileCache
{
    /// <summary>The tile source. <c>{z}/{x}/{y}</c> are substituted per tile. Kept as one place to swap providers.</summary>
    public const string TileUrlTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    /// <summary>Refuse to bulk-download more than this many tiles in one area request (be a good tile-server
    /// citizen and stop the user accidentally queuing millions of tiles). The UI shows the estimate first.</summary>
    public const int MaxTilesPerRequest = 100_000;

    private const int MaxConcurrentDownloads = 4;   // keep well within the OSM tile policy

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _root;
    private readonly string _manifestPath;

    public MapTileCache(string rootDir)
    {
        _root = rootDir;
        _manifestPath = System.IO.Path.Combine(_root, "regions.json");
        try { System.IO.Directory.CreateDirectory(_root); } catch { /* created lazily on write too */ }
    }

    /// <summary>The cache's root folder (where the <c>{z}/{x}/{y}.png</c> tree lives).</summary>
    public string RootDir => _root;

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // OSM requires a valid, identifying User-Agent; a generic/library one gets blocked.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChessOverMesh-OfflineMap/1.0 (+https://github.com/MerApple/ChessOverMesh)");
        return http;
    }

    private string TilePath(int z, int x, int y) =>
        System.IO.Path.Combine(_root, z.ToString(), x.ToString(), y + ".png");

    /// <summary>True when this tile is already stored (a non-empty file on disk).</summary>
    public bool Has(int z, int x, int y)
    {
        try { var fi = new System.IO.FileInfo(TilePath(z, x, y)); return fi.Exists && fi.Length > 0; }
        catch { return false; }
    }

    /// <summary>Returns a tile's PNG bytes: from the cache if present, else — when <paramref name="allowOnline"/>
    /// and a network is available — fetched from the tile server and saved for next time. Null when it isn't
    /// cached and can't be fetched (offline, or the fetch failed). Never throws.</summary>
    public async Task<byte[]?> GetTileAsync(int z, int x, int y, bool allowOnline, CancellationToken ct = default)
    {
        string path = TilePath(z, x, y);
        try
        {
            if (Has(z, x, y)) return await System.IO.File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch { /* fall through to a re-fetch */ }

        if (!allowOnline) return null;
        try
        {
            var bytes = await DownloadTileAsync(z, x, y, ct).ConfigureAwait(false);
            if (bytes != null) await SaveTileAsync(path, bytes, ct).ConfigureAwait(false);
            return bytes;
        }
        catch { return null; }
    }

    private static async Task<byte[]?> DownloadTileAsync(int z, int x, int y, CancellationToken ct)
    {
        string url = TileUrlTemplate.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return bytes.Length > 0 ? bytes : null;
    }

    private static async Task SaveTileAsync(string path, byte[] bytes, CancellationToken ct)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // Write to a temp file then move, so a cancelled/failed write never leaves a truncated tile.
            string tmp = path + ".tmp";
            await System.IO.File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            System.IO.File.Move(tmp, path, overwrite: true);
        }
        catch { /* caching is best-effort */ }
    }

    /// <summary>The number of tiles covering an area across a zoom range (for the pre-download estimate).</summary>
    public static long EstimateTiles(GeoBounds bounds, int minZoom, int maxZoom) =>
        TileMath.TileCount(bounds, minZoom, maxZoom);

    /// <summary>A rough on-disk size estimate for a tile count (~18 KB/tile average for OSM raster tiles).</summary>
    public static long EstimateBytes(long tileCount) => tileCount * 18_000;

    /// <summary>
    /// Downloads every tile covering <paramref name="bounds"/> across the inclusive zoom range into the cache,
    /// skipping tiles already stored. Coarse zooms first, so a cancelled run still leaves usable low-detail
    /// coverage. Reports progress and honours cancellation. On success (not cancelled) the area is recorded in
    /// the region manifest under <paramref name="name"/>. Never throws for per-tile failures — they're counted.
    /// </summary>
    public async Task<TileDownloadResult> CacheAreaAsync(string name, GeoBounds bounds, int minZoom, int maxZoom,
        IProgress<TileDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var tiles = TileMath.Tiles(bounds, minZoom, maxZoom).ToList();
        int total = tiles.Count;
        int done = 0, downloaded = 0, skipped = 0, failed = 0;

        void Report() => progress?.Report(new TileDownloadProgress(
            System.Threading.Volatile.Read(ref done), total,
            System.Threading.Volatile.Read(ref downloaded),
            System.Threading.Volatile.Read(ref skipped),
            System.Threading.Volatile.Read(ref failed)));

        using var gate = new SemaphoreSlim(MaxConcurrentDownloads);
        var tasks = new List<Task>(total);
        bool cancelled = false;

        foreach (var (z, x, y) in tiles)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (Has(z, x, y)) { System.Threading.Interlocked.Increment(ref skipped); return; }
                    var bytes = await DownloadTileAsync(z, x, y, ct).ConfigureAwait(false);
                    if (bytes != null)
                    {
                        await SaveTileAsync(TilePath(z, x, y), bytes, ct).ConfigureAwait(false);
                        System.Threading.Interlocked.Increment(ref downloaded);
                    }
                    else System.Threading.Interlocked.Increment(ref failed);
                }
                catch (OperationCanceledException) { /* whole run cancelled */ }
                catch { System.Threading.Interlocked.Increment(ref failed); }
                finally
                {
                    System.Threading.Interlocked.Increment(ref done);
                    gate.Release();
                    Report();
                }
            }, ct));
        }

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { cancelled = true; }
        Report();

        if (!cancelled && !ct.IsCancellationRequested && downloaded + skipped > 0)
            AddRegion(new CachedRegion
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Cached area" : name.Trim(),
                South = bounds.South, West = bounds.West, North = bounds.North, East = bounds.East,
                MinZoom = minZoom, MaxZoom = maxZoom, Tiles = total, When = DateTime.Now,
            });

        return new TileDownloadResult(total, downloaded, skipped, failed, cancelled || ct.IsCancellationRequested);
    }

    // ---- Cache stats + management ------------------------------------------------------------------------

    /// <summary>The number of tile files currently stored (walks the tree; may be slow for a huge cache).</summary>
    public long CachedTileCount()
    {
        try { return System.IO.Directory.Exists(_root) ? System.IO.Directory.EnumerateFiles(_root, "*.png", System.IO.SearchOption.AllDirectories).LongCount() : 0; }
        catch { return 0; }
    }

    /// <summary>Total bytes used by stored tiles.</summary>
    public long CacheSizeBytes()
    {
        try
        {
            if (!System.IO.Directory.Exists(_root)) return 0;
            long sum = 0;
            foreach (var f in System.IO.Directory.EnumerateFiles(_root, "*.png", System.IO.SearchOption.AllDirectories))
                try { sum += new System.IO.FileInfo(f).Length; } catch { }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>Deletes every cached tile and the region manifest (frees all offline map storage).</summary>
    public void Clear()
    {
        try { if (System.IO.Directory.Exists(_root)) System.IO.Directory.Delete(_root, recursive: true); } catch { }
        try { System.IO.Directory.CreateDirectory(_root); } catch { }
    }

    // ---- Region manifest ---------------------------------------------------------------------------------

    /// <summary>The regions the user has cached (most recent first). Best-effort; empty on any read error.</summary>
    public IReadOnlyList<CachedRegion> GetRegions()
    {
        try
        {
            if (!System.IO.File.Exists(_manifestPath)) return Array.Empty<CachedRegion>();
            var list = JsonSerializer.Deserialize<List<CachedRegion>>(System.IO.File.ReadAllText(_manifestPath));
            return list ?? new List<CachedRegion>();
        }
        catch { return Array.Empty<CachedRegion>(); }
    }

    private void AddRegion(CachedRegion region)
    {
        try
        {
            var list = GetRegions().ToList();
            list.Insert(0, region);
            System.IO.Directory.CreateDirectory(_root);
            System.IO.File.WriteAllText(_manifestPath,
                JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
