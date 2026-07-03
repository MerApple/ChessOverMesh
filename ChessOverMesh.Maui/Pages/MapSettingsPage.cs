using ChessOverMesh.Map;

namespace ChessOverMesh.Maui;

/// <summary>
/// The "Map settings" section: manage the offline map tile cache. Shows how much is cached (tiles / size / the
/// downloaded areas), lets the user download a new area for offline use (opens <see cref="MapCachePage"/>), and
/// delete all cached tiles. Refreshes whenever it reappears (e.g. after a download).
/// </summary>
public sealed class MapSettingsPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");

    readonly MainPage _main;
    readonly Label _summary;
    readonly VerticalStackLayout _regions;
    readonly Button _delete;
    readonly Button _cache;
    readonly Picker _provider;
    readonly Entry _key;
    readonly Label _providerHelp;
    bool _loading;   // suppress change handlers while populating the controls

    public MapSettingsPage(MainPage main)
    {
        _main = main;
        Title = "Map settings";
        BackgroundColor = Bg;

        var stack = new VerticalStackLayout { Padding = 16, Spacing = 12 };
        stack.Add(new Label { Text = "Map settings", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold });
        stack.Add(new Label
        {
            Text = "Offline map: download the tiles for an area so the node map works with no internet. " +
                   "When something is cached, the map lets you switch between the online and cached layers.",
            TextColor = Dim, FontSize = 13,
        });

        // Tile provider: OSM forbids bulk downloading (it returns an "Access blocked" tile), so caching an area
        // needs a keyed provider whose terms permit it.
        stack.Add(new Label { Text = "Tile provider", TextColor = Fg, FontSize = 14 });
        _provider = new Picker { TextColor = Fg, TitleColor = Dim, BackgroundColor = Bg };
        foreach (var id in MapTileProvider.All) _provider.Items.Add(MapTileProvider.DisplayName(id));
        _provider.SelectedIndexChanged += (_, _) => OnProviderOrKeyChanged();
        stack.Add(_provider);

        stack.Add(new Label { Text = "API key", TextColor = Fg, FontSize = 14 });
        _key = new Entry { TextColor = Fg, PlaceholderColor = Dim, BackgroundColor = Bg, Placeholder = "provider API key" };
        _key.Unfocused += (_, _) => OnProviderOrKeyChanged();
        stack.Add(_key);

        _providerHelp = new Label { TextColor = Dim, FontSize = 11 };
        stack.Add(_providerHelp);

        _summary = new Label { TextColor = Fg, FontSize = 14 };
        stack.Add(_summary);

        _regions = new VerticalStackLayout { Spacing = 2 };
        stack.Add(_regions);

        _cache = new Button { Text = "Cache map area…", MinimumHeightRequest = 48, HorizontalOptions = LayoutOptions.Fill };
        _cache.Clicked += OnCacheArea;
        stack.Add(_cache);

        _delete = new Button { Text = "Delete map cache", MinimumHeightRequest = 48, HorizontalOptions = LayoutOptions.Fill };
        _delete.Clicked += OnDelete;
        stack.Add(_delete);

        var close = new Button { Text = "Close", MinimumHeightRequest = 48, HorizontalOptions = LayoutOptions.Fill };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();
        stack.Add(close);

        Content = new ScrollView { Content = stack };
        LoadProvider();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();   // pick up any tiles just downloaded via the cache page
    }

    // Sets the provider picker + key field from saved settings (without firing the change handlers).
    void LoadProvider()
    {
        _loading = true;
        string current = string.IsNullOrWhiteSpace(AppSettings.MapProvider) ? MapTileProvider.Default : AppSettings.MapProvider!;
        int idx = Array.IndexOf(MapTileProvider.All, current);
        _provider.SelectedIndex = idx >= 0 ? idx : 0;
        _key.Text = AppSettings.MapApiKey ?? "";
        _loading = false;
        UpdateProviderHelp();
    }

    // Persists the chosen provider + key, rebuilds the cache/server against the new source, and updates the UI.
    void OnProviderOrKeyChanged()
    {
        if (_loading) return;
        int i = _provider.SelectedIndex;
        string provider = i >= 0 && i < MapTileProvider.All.Length ? MapTileProvider.All[i] : MapTileProvider.Default;
        string key = (_key.Text ?? "").Trim();
        bool changed = !string.Equals(provider, AppSettings.MapProvider ?? MapTileProvider.Default, StringComparison.Ordinal)
                       || !string.Equals(key, AppSettings.MapApiKey ?? "", StringComparison.Ordinal);
        AppSettings.MapProvider = provider;
        AppSettings.MapApiKey = key;
        if (changed) MapCacheService.InvalidateTileSource();
        UpdateProviderHelp();
        Refresh();   // enable/disable the Cache button for the new provider/key
    }

    // Shows provider-specific guidance: where to get a key, or that OSM can't cache offline.
    void UpdateProviderHelp()
    {
        int i = _provider.SelectedIndex;
        string provider = i >= 0 && i < MapTileProvider.All.Length ? MapTileProvider.All[i] : MapTileProvider.Default;
        _key.IsEnabled = MapTileProvider.NeedsKey(provider);
        if (!MapTileProvider.AllowsOfflineCaching(provider))
            _providerHelp.Text = "OpenStreetMap is online-only: its tile policy forbids downloading areas for " +
                                 "offline use. Pick a keyed provider to enable \"Cache map area\".";
        else if (string.IsNullOrWhiteSpace(_key.Text))
            _providerHelp.Text = $"Enter your free {MapTileProvider.DisplayName(provider)} API key to enable offline " +
                                 $"caching. Get one at {MapTileProvider.SignupUrl(provider)}";
        else
            _providerHelp.Text = $"Using {MapTileProvider.DisplayName(provider)} for the map and offline caching.";
    }

    async void OnCacheArea(object? sender, EventArgs e)
    {
        // Suggest a centre: the mean of known node positions, else the map's default (Stockholm).
        double cLat = 59.3293, cLon = 18.0686;
        var pts = _main.GetNodePositions().Where(p => p.Latitude != 0 || p.Longitude != 0).ToList();
        if (pts.Count > 0) { cLat = pts.Average(p => p.Latitude); cLon = pts.Average(p => p.Longitude); }
        try { await Navigation.PushModalAsync(new MapCachePage(cLat, cLon)); }
        catch { /* ignore navigation races */ }
    }

    async void OnDelete(object? sender, EventArgs e)
    {
        var (tiles, bytes) = await Task.Run(() => MapCacheService.Cache.CacheStats());
        if (tiles == 0) { await DisplayAlert("Map cache", "There are no cached map tiles to delete.", "OK"); return; }
        bool ok = await DisplayAlert("Delete map cache",
            $"Delete all cached map tiles ({tiles:N0} tiles, {FormatBytes(bytes)})?\n\n" +
            "The map will work online only, as before, until you cache an area again.",
            "Delete", "Cancel");
        if (!ok) return;
        await Task.Run(() => MapCacheService.Cache.Clear());
        Refresh();
    }

    async void Refresh()
    {
        var (tiles, bytes) = await Task.Run(() => MapCacheService.Cache.CacheStats());
        var regions = MapCacheService.Cache.GetRegions();
        _summary.Text = tiles == 0
            ? "No offline map tiles are cached yet."
            : $"Cached: {tiles:N0} tiles, {FormatBytes(bytes)}" +
              (regions.Count > 0 ? $" across {regions.Count} area(s):" : ".");
        _delete.IsEnabled = tiles > 0;

        // Caching an area is only possible with a keyed provider that has a key (OSM forbids bulk downloading).
        _cache.IsEnabled = MapTileProvider.CanCacheOffline(AppSettings.MapProvider, AppSettings.MapApiKey);

        _regions.Clear();
        foreach (var r in regions)
            _regions.Add(new Label
            {
                TextColor = Dim, FontSize = 12,
                Text = $"• {r.Name} — zoom {r.MinZoom}–{r.MaxZoom}, {r.Tiles:N0} tiles  ({r.When:yyyy-MM-dd HH:mm})",
            });
    }

    static string FormatBytes(long bytes)
    {
        double b = bytes;
        if (b < 1024) return $"{b:0} B";
        if (b < 1024 * 1024) return $"{b / 1024:0.#} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):0.#} MB";
        return $"{b / (1024.0 * 1024 * 1024):0.##} GB";
    }
}
