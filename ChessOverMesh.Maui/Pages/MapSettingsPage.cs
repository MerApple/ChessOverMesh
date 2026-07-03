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

        _summary = new Label { TextColor = Fg, FontSize = 14 };
        stack.Add(_summary);

        _regions = new VerticalStackLayout { Spacing = 2 };
        stack.Add(_regions);

        var cache = new Button { Text = "Cache map area…", MinimumHeightRequest = 48, HorizontalOptions = LayoutOptions.Fill };
        cache.Clicked += OnCacheArea;
        stack.Add(cache);

        _delete = new Button { Text = "Delete map cache", MinimumHeightRequest = 48, HorizontalOptions = LayoutOptions.Fill };
        _delete.Clicked += OnDelete;
        stack.Add(_delete);

        var close = new Button { Text = "Close", MinimumHeightRequest = 48, HorizontalOptions = LayoutOptions.Fill };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();
        stack.Add(close);

        Content = new ScrollView { Content = stack };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();   // pick up any tiles just downloaded via the cache page
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
