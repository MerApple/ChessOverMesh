using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>
/// Shows known mesh node positions on an OpenStreetMap/Leaflet map in an in-app WebView. Reuses the shared
/// <see cref="NodeMap"/> HTML (identical to the desktop app's browser map). Leaflet and the tiles are served by a
/// local loopback <see cref="ChessOverMesh.Map.MapTileServer"/> backed by the on-disk tile cache, so areas cached
/// with "Cache area" work offline (and browsing online fills the cache). Falls back to loading them online if the
/// local server can't start.
/// </summary>
public sealed class MapPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");

    public MapPage(IReadOnlyList<MeshNodePosition> positions,
        IReadOnlyDictionary<uint, List<(double Lat, double Lon, long LastHeard, long PosTime)>>? history = null,
        uint? focusNum = null)
    {
        Title = "Node map";
        BackgroundColor = Bg;

        // Serve Leaflet + tiles from the local cache server when it starts; null → load them online (as before).
        string? assetBase = MapCacheService.EnsureServerBaseUrl();
        // focusNum opens the map straight into that node's recent-position track (the "Show on map" button).
        var web = new WebView { Source = new HtmlWebViewSource { Html = NodeMap.Html(positions, history, focusNum, assetBase) } };

        var close = new Button { Text = "Close", Padding = new Thickness(14, 0), MinimumHeightRequest = 40 };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        var cache = new Button { Text = "Cache area", Padding = new Thickness(14, 0), MinimumHeightRequest = 40 };
        cache.Clicked += async (_, _) =>
        {
            // Suggest a centre: the mean of known node positions, else the map's default (Stockholm).
            double cLat = 59.3293, cLon = 18.0686;
            var pts = positions.Where(p => p.Latitude != 0 || p.Longitude != 0).ToList();
            if (pts.Count > 0) { cLat = pts.Average(p => p.Latitude); cLon = pts.Average(p => p.Longitude); }
            try { await Navigation.PushModalAsync(new MapCachePage(cLat, cLon)); }
            catch { /* ignore navigation races */ }
        };

        string count = positions.Count == 0
            ? "No node positions yet — open Nodes and request a position, or wait for nodes to broadcast one."
            : $"{positions.Count} node position(s).";
        var bar = new Grid { Padding = new Thickness(10, 6), ColumnSpacing = 8, BackgroundColor = Color.FromArgb("#252526"),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) } };
        bar.Add(new Label { Text = count, TextColor = Fg, FontSize = 12, VerticalOptions = LayoutOptions.Center }, 0, 0);
        bar.Add(cache, 1, 0);
        bar.Add(close, 2, 0);

        var root = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        root.Add(bar, 0, 0);
        root.Add(web, 0, 1);
        Content = root;
    }
}
