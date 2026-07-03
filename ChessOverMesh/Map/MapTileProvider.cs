namespace ChessOverMesh.Map;

/// <summary>
/// Resolves the map tile source. OpenStreetMap's public tile servers forbid bulk/offline downloading and answer a
/// "blocked" placeholder tile if you try (see osm.wiki/blocked), so offline caching must use a provider whose
/// terms permit it — those need a free API key. This maps a chosen provider id + key to the concrete
/// <c>{z}/{x}/{y}</c> tile URL used both to download tiles into the <see cref="MapTileCache"/> and to draw the
/// online layer on the <see cref="ChessOverMesh.Mesh.NodeMap"/> page. Kept in one place so the two stay in sync.
/// </summary>
public static class MapTileProvider
{
    public const string OpenStreetMap = "osm";
    public const string MapTiler = "maptiler";
    public const string Thunderforest = "thunderforest";
    public const string Stadia = "stadia";

    /// <summary>The default provider when nothing is configured: plain online OpenStreetMap (no offline caching).</summary>
    public const string Default = OpenStreetMap;

    /// <summary>The provider ids in display order (OSM first as the online-only default).</summary>
    public static readonly string[] All = { OpenStreetMap, MapTiler, Thunderforest, Stadia };

    /// <summary>The plain online OpenStreetMap tile URL — the fallback for the online layer and for any provider
    /// that has no usable key yet. Bulk downloading from here is against OSM policy (see the class summary).</summary>
    public const string OsmOnlineUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    /// <summary>A human-readable name for a provider id (for settings UIs).</summary>
    public static string DisplayName(string? id) => Normalize(id) switch
    {
        MapTiler => "MapTiler",
        Thunderforest => "Thunderforest",
        Stadia => "Stadia Maps",
        _ => "OpenStreetMap (online only)",
    };

    /// <summary>True when the provider needs an API key (every provider except plain OSM).</summary>
    public static bool NeedsKey(string? id) => Normalize(id) != OpenStreetMap;

    /// <summary>True when the provider's terms allow downloading tiles for offline use. Plain OSM does not, so the
    /// "Cache map area" download is only offered for the keyed providers.</summary>
    public static bool AllowsOfflineCaching(string? id) => Normalize(id) != OpenStreetMap;

    /// <summary>Where to get a free API key for the provider (shown in the settings UI); empty for OSM.</summary>
    public static string SignupUrl(string? id) => Normalize(id) switch
    {
        MapTiler => "https://www.maptiler.com/cloud/",
        Thunderforest => "https://www.thunderforest.com/pricing/",
        Stadia => "https://stadiamaps.com/stamen/",
        _ => "",
    };

    /// <summary>
    /// The concrete tile URL template (with the <c>{z}/{x}/{y}</c> placeholders left intact for Leaflet /
    /// <see cref="MapTileCache"/> to fill) for a provider + key. Falls back to <see cref="OsmOnlineUrl"/> when the
    /// provider is OSM or a keyed provider has no key yet — so the map always renders online, it just can't cache.
    /// </summary>
    public static string TileUrl(string? id, string? apiKey)
    {
        string key = (apiKey ?? "").Trim();
        string provider = Normalize(id);
        if (provider != OpenStreetMap && key.Length == 0) provider = OpenStreetMap;   // no key → online OSM
        return provider switch
        {
            MapTiler => $"https://api.maptiler.com/maps/streets-v2/256/{{z}}/{{x}}/{{y}}.png?key={key}",
            Thunderforest => $"https://tile.thunderforest.com/atlas/{{z}}/{{x}}/{{y}}.png?apikey={key}",
            Stadia => $"https://tiles.stadiamaps.com/tiles/alidade_smooth/{{z}}/{{x}}/{{y}}.png?api_key={key}",
            _ => OsmOnlineUrl,
        };
    }

    /// <summary>True when a keyed provider is selected and a non-empty key is present — i.e. offline caching is
    /// actually usable right now (the "Cache map area" button should be enabled).</summary>
    public static bool CanCacheOffline(string? id, string? apiKey) =>
        AllowsOfflineCaching(id) && !string.IsNullOrWhiteSpace(apiKey);

    private static string Normalize(string? id) => (id ?? "").Trim().ToLowerInvariant() switch
    {
        MapTiler => MapTiler,
        Thunderforest => Thunderforest,
        Stadia => Stadia,
        _ => OpenStreetMap,
    };
}
