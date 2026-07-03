namespace ChessOverMesh.Map;

/// <summary>A geographic bounding box in decimal degrees (WGS84). South/West are the min corner, North/East the
/// max. Longitudes are clamped to [-180, 180] and latitudes to the Web-Mercator limit (±85.0511°) by
/// <see cref="TileMath"/> when enumerating tiles.</summary>
public readonly record struct GeoBounds(double South, double West, double North, double East)
{
    /// <summary>A box of <paramref name="radiusKm"/> around a centre point. The longitude span widens with
    /// latitude (degrees of longitude shrink toward the poles) so the box stays roughly square on the ground.</summary>
    public static GeoBounds AroundCenter(double lat, double lon, double radiusKm)
    {
        double dLat = radiusKm / 111.0;                                            // ~111 km per degree of latitude
        double cos = System.Math.Cos(lat * System.Math.PI / 180.0);
        double dLon = radiusKm / (111.0 * System.Math.Max(cos, 0.01));             // guard against the poles
        return new GeoBounds(lat - dLat, lon - dLon, lat + dLat, lon + dLon);
    }

    public double CenterLat => (South + North) / 2.0;
    public double CenterLon => (West + East) / 2.0;
}

/// <summary>Slippy-map ("XYZ") tile arithmetic: converts between lon/lat and OpenStreetMap tile coordinates and
/// enumerates the tiles covering a <see cref="GeoBounds"/> across a zoom range. Standard Web-Mercator formulas
/// (EPSG:3857), matching what Leaflet requests from the tile server.</summary>
public static class TileMath
{
    /// <summary>Web-Mercator can't represent latitudes beyond this; clamp to it.</summary>
    public const double MaxLatitude = 85.05112878;

    public static int LonToTileX(double lon, int zoom)
    {
        lon = System.Math.Clamp(lon, -180.0, 180.0);
        int n = 1 << zoom;
        int x = (int)System.Math.Floor((lon + 180.0) / 360.0 * n);
        return System.Math.Clamp(x, 0, n - 1);
    }

    public static int LatToTileY(double lat, int zoom)
    {
        lat = System.Math.Clamp(lat, -MaxLatitude, MaxLatitude);
        double latRad = lat * System.Math.PI / 180.0;
        int n = 1 << zoom;
        int y = (int)System.Math.Floor((1.0 - System.Math.Asinh(System.Math.Tan(latRad)) / System.Math.PI) / 2.0 * n);
        return System.Math.Clamp(y, 0, n - 1);
    }

    /// <summary>The number of tiles covering <paramref name="bounds"/> at a single zoom level.</summary>
    public static long TileCountAtZoom(GeoBounds bounds, int zoom)
    {
        int x0 = LonToTileX(bounds.West, zoom), x1 = LonToTileX(bounds.East, zoom);
        int y0 = LatToTileY(bounds.North, zoom), y1 = LatToTileY(bounds.South, zoom);   // y grows southward
        return (long)(System.Math.Abs(x1 - x0) + 1) * (System.Math.Abs(y1 - y0) + 1);
    }

    /// <summary>The total number of tiles covering <paramref name="bounds"/> across the inclusive zoom range.</summary>
    public static long TileCount(GeoBounds bounds, int minZoom, int maxZoom)
    {
        long total = 0;
        for (int z = minZoom; z <= maxZoom; z++) total += TileCountAtZoom(bounds, z);
        return total;
    }

    /// <summary>Enumerates every tile (zoom, x, y) covering <paramref name="bounds"/> across the inclusive zoom
    /// range, coarsest zoom first so a cancelled download still leaves usable low-detail coverage.</summary>
    public static IEnumerable<(int Z, int X, int Y)> Tiles(GeoBounds bounds, int minZoom, int maxZoom)
    {
        for (int z = minZoom; z <= maxZoom; z++)
        {
            int x0 = LonToTileX(bounds.West, z), x1 = LonToTileX(bounds.East, z);
            int y0 = LatToTileY(bounds.North, z), y1 = LatToTileY(bounds.South, z);
            for (int x = System.Math.Min(x0, x1); x <= System.Math.Max(x0, x1); x++)
                for (int y = System.Math.Min(y0, y1); y <= System.Math.Max(y0, y1); y++)
                    yield return (z, x, y);
        }
    }
}
