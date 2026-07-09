using ChessOverMesh.Map;
using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>
/// Shows known mesh node positions on an OpenStreetMap/Leaflet map in an in-app WebView. Reuses the shared
/// <see cref="NodeMap"/> HTML (identical to the desktop app's browser map). With no offline cache the map is
/// online-only, exactly as before. Once an area is cached with "Cache area", Leaflet is served locally by a
/// loopback <see cref="ChessOverMesh.Map.MapTileServer"/> and the map offers an online/offline base-layer choice.
/// </summary>
public sealed class MapPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");

    // Provides a fresh node-positions snapshot (NodeMap.SerializeNodes output) on demand; pushed into the loopback
    // server on a timer so the WebView's /positions.json poll shows new fixes live. Null → the map is a snapshot.
    readonly Func<string>? _liveSnapshot;
    readonly IDispatcher _dispatcher;
    IDispatcherTimer? _liveTimer;

    /// <param name="liveSnapshot">Returns the current positions as a NodeMap.SerializeNodes JSON string. When
    /// supplied, the map updates live while open; called on the UI thread (safe to read the mesh there).</param>
    public MapPage(IReadOnlyList<MeshNodePosition> positions,
        IReadOnlyDictionary<uint, List<(double Lat, double Lon, long LastHeard, long PosTime)>>? history = null,
        uint? focusNum = null, Func<string>? liveSnapshot = null)
    {
        Title = "Node map";
        BackgroundColor = Bg;
        _liveSnapshot = liveSnapshot;
        _dispatcher = Dispatcher;

        // Stand up the loopback server whenever the map opens so the page can poll /positions.json and update live.
        // The offline/online base-layer choice is still only offered once an area is cached; with no cache the asset
        // base stays null and Leaflet + tiles load online, as before. If the socket couldn't bind, liveUrl is null
        // and the map is a one-shot snapshot (as before).
        string? liveBase = MapCacheService.EnsureServerBaseUrl();
        string? assetBase = (liveBase != null && MapCacheService.Cache.HasAnyTiles()) ? liveBase : null;
        string? liveUrl = liveBase != null ? liveBase + "/positions.json" : null;
        string onlineTileUrl = MapTileProvider.TileUrl(AppSettings.MapProvider, AppSettings.MapApiKey);
        MapCacheService.SetMapPositions(NodeMap.SerializeNodes(positions, history));   // seed the first poll
        // focusNum opens the map straight into that node's recent-position track (the "Show on map" button).
        var web = new WebView { Source = new HtmlWebViewSource { Html = NodeMap.Html(positions, history, focusNum, assetBase, onlineTileUrl, liveUrl) } };

        var close = new Button { Text = "Close", Padding = new Thickness(14, 0), MinimumHeightRequest = 40 };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        string count = positions.Count == 0
            ? "No node positions yet — open Nodes and request a position, or wait for nodes to broadcast one."
            : $"{positions.Count} node position(s).";
        var bar = new Grid { Padding = new Thickness(10, 6), ColumnSpacing = 8, BackgroundColor = Color.FromArgb("#252526"),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        bar.Add(new Label { Text = count, TextColor = Fg, FontSize = 12, VerticalOptions = LayoutOptions.Center }, 0, 0);
        bar.Add(close, 1, 0);

        var root = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        root.Add(bar, 0, 0);
        root.Add(web, 0, 1);
        Content = root;
    }

    // While the map is on screen, re-serialize the live positions into the server every few seconds so the WebView's
    // poll shows new fixes. Ticks on the UI thread (safe to read the mesh); stopped when the page goes away.
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_liveSnapshot == null || _liveTimer != null) return;
        _liveTimer = _dispatcher.CreateTimer();
        _liveTimer.Interval = TimeSpan.FromSeconds(3);
        _liveTimer.Tick += (_, _) =>
        {
            try { MapCacheService.SetMapPositions(_liveSnapshot()); }
            catch { /* transient read race — the next tick retries */ }
        };
        _liveTimer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _liveTimer?.Stop();
        _liveTimer = null;
    }
}
