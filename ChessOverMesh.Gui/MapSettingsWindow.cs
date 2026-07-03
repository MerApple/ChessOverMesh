using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChessOverMesh.Map;

namespace ChessOverMesh.Gui;

/// <summary>
/// The "Map settings" section: manage the offline map tile cache. Shows how much is cached (tiles / size / the
/// downloaded areas), lets the user download a new area for offline use ("Cache map area…", which opens the
/// download dialog), and delete all cached tiles. Built in code to match the app's dark settings dialogs.
/// </summary>
internal sealed class MapSettingsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
    private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush Border = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

    private readonly MapTileCache _cache;
    private readonly Action<Window> _openCacheArea;
    private readonly Action _onTileSourceChanged;
    private readonly TextBlock _summary;
    private readonly ItemsControl _regions;
    private readonly Button _deleteBtn;
    private readonly Button _cacheBtn;
    private readonly ComboBox _providerBox;
    private readonly TextBox _keyBox;
    private readonly TextBlock _providerHelp;
    private bool _loading;   // suppress change handlers while populating the controls

    /// <param name="openCacheArea">Opens the "Cache map area" download dialog owned by the given window (the main
    /// window supplies its node-position-centred version).</param>
    /// <param name="onTileSourceChanged">Called after the provider/key changes so the caller can rebuild its tile
    /// cache + server against the new source.</param>
    public MapSettingsWindow(Window owner, MapTileCache cache, Action<Window> openCacheArea, Action onTileSourceChanged)
    {
        _cache = cache;
        _openCacheArea = openCacheArea;
        _onTileSourceChanged = onTileSourceChanged;

        Title = "Map settings";
        Owner = owner;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        var root = new StackPanel { Margin = new Thickness(14) };

        root.Children.Add(new TextBlock
        {
            Text = "Offline map: download the tiles for an area so the node map works with no internet. " +
                   "When something is cached, the map lets you switch between the online and cached layers.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // --- Tile provider ---------------------------------------------------------------------------------
        // OpenStreetMap's servers forbid bulk downloading (they return an "Access blocked" tile), so caching an
        // area needs a provider whose terms allow it, with a free API key.
        root.Children.Add(new TextBlock { Text = "Tile provider", Foreground = Fg, Margin = new Thickness(0, 0, 0, 4) });
        _providerBox = new ComboBox { Background = Field, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(0, 0, 0, 6) };
        var itemStyle = new Style(typeof(ComboBoxItem));   // dark dropdown items (else light-on-white = invisible)
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Field));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        _providerBox.ItemContainerStyle = itemStyle;
        foreach (var id in MapTileProvider.All)
            _providerBox.Items.Add(new ComboBoxItem { Content = MapTileProvider.DisplayName(id), Tag = id, Foreground = Fg, Background = Field });
        _providerBox.SelectionChanged += (_, _) => OnProviderOrKeyChanged();
        root.Children.Add(_providerBox);

        root.Children.Add(new TextBlock { Text = "API key", Foreground = Fg, Margin = new Thickness(0, 0, 0, 4) });
        _keyBox = new TextBox { Background = Field, Foreground = Fg, CaretBrush = Fg, BorderBrush = Border,
            MinHeight = 24, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 0, 0, 4) };
        _keyBox.LostFocus += (_, _) => OnProviderOrKeyChanged();
        root.Children.Add(_keyBox);

        _providerHelp = new TextBlock { Foreground = Dim, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(_providerHelp);

        _summary = new TextBlock { Foreground = Fg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(_summary);

        _regions = new ItemsControl { Margin = new Thickness(0, 0, 0, 8) };
        var scroll = new ScrollViewer
        {
            Content = _regions,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 160,
            Margin = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(scroll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        _cacheBtn = new Button { Content = "Cache map area…", MinWidth = 130, MinHeight = 28, Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Download a chosen area's map tiles for offline use." };
        _cacheBtn.Click += (_, _) => { _openCacheArea(this); Refresh(); };

        _deleteBtn = new Button { Content = "Delete map cache", MinWidth = 130, MinHeight = 28, Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Delete all cached map tiles. The map then works online only, as before." };
        _deleteBtn.Click += (_, _) => DeleteCache();

        var closeBtn = new Button { Content = "Close", MinWidth = 80, MinHeight = 28, IsDefault = true, IsCancel = true };
        closeBtn.Click += (_, _) => Close();

        buttons.Children.Add(_cacheBtn);
        buttons.Children.Add(_deleteBtn);
        buttons.Children.Add(closeBtn);
        root.Children.Add(buttons);

        Content = root;
        LoadProvider();
        Refresh();
    }

    // Sets the provider dropdown + key field from saved settings (without firing the change handlers).
    private void LoadProvider()
    {
        _loading = true;
        string current = string.IsNullOrWhiteSpace(AppSettings.MapProvider) ? MapTileProvider.Default : AppSettings.MapProvider!;
        int idx = Array.IndexOf(MapTileProvider.All, current);
        _providerBox.SelectedIndex = idx >= 0 ? idx : 0;
        _keyBox.Text = AppSettings.MapApiKey ?? "";
        _loading = false;
        UpdateProviderHelp();
    }

    // Persists the chosen provider + key, tells the owner to rebuild its cache/server, and updates the UI state.
    private void OnProviderOrKeyChanged()
    {
        if (_loading) return;
        string provider = (_providerBox.SelectedItem as ComboBoxItem)?.Tag as string ?? MapTileProvider.Default;
        string key = _keyBox.Text.Trim();
        bool changed = !string.Equals(provider, AppSettings.MapProvider ?? MapTileProvider.Default, StringComparison.Ordinal)
                       || !string.Equals(key, AppSettings.MapApiKey ?? "", StringComparison.Ordinal);
        AppSettings.MapProvider = provider;
        AppSettings.MapApiKey = key;
        if (changed) _onTileSourceChanged();
        UpdateProviderHelp();
        Refresh();   // enable/disable the Cache button for the new provider/key
    }

    // Shows provider-specific guidance: where to get a key, or that OSM can't cache offline.
    private void UpdateProviderHelp()
    {
        string provider = (_providerBox.SelectedItem as ComboBoxItem)?.Tag as string ?? MapTileProvider.Default;
        _keyBox.IsEnabled = MapTileProvider.NeedsKey(provider);
        if (!MapTileProvider.AllowsOfflineCaching(provider))
            _providerHelp.Text = "OpenStreetMap is online-only: its tile policy forbids downloading areas for " +
                                 "offline use. Pick a keyed provider below to enable \"Cache map area\".";
        else if (string.IsNullOrWhiteSpace(_keyBox.Text))
            _providerHelp.Text = $"Enter your free {MapTileProvider.DisplayName(provider)} API key to enable offline " +
                                 $"caching. Get one at {MapTileProvider.SignupUrl(provider)}";
        else
            _providerHelp.Text = $"Using {MapTileProvider.DisplayName(provider)} for the map and offline caching.";
    }

    // Recomputes the cache summary + region list and enables/disables Delete accordingly.
    private void Refresh()
    {
        var (tiles, bytes) = _cache.CacheStats();
        var regions = _cache.GetRegions();
        _summary.Text = tiles == 0
            ? "No offline map tiles are cached yet."
            : $"Cached: {tiles:N0} tiles, {FormatBytes(bytes)}" +
              (regions.Count > 0 ? $" across {regions.Count} area(s):" : ".");
        _deleteBtn.IsEnabled = tiles > 0;

        // Caching an area is only possible with a keyed provider that has a key (OSM forbids bulk downloading).
        bool canCache = MapTileProvider.CanCacheOffline(AppSettings.MapProvider, AppSettings.MapApiKey);
        _cacheBtn.IsEnabled = canCache;
        _cacheBtn.ToolTip = canCache
            ? "Download a chosen area's map tiles for offline use."
            : "Choose a tile provider and enter its API key above to enable offline caching.";

        _regions.Items.Clear();
        foreach (var r in regions)
            _regions.Items.Add(new TextBlock
            {
                Foreground = Dim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
                Text = $"• {r.Name} — zoom {r.MinZoom}–{r.MaxZoom}, {r.Tiles:N0} tiles  ({r.When:yyyy-MM-dd HH:mm})",
            });
    }

    private void DeleteCache()
    {
        var (tiles, bytes) = _cache.CacheStats();
        if (tiles == 0) { ThemedDialog.Info(this, "There are no cached map tiles to delete.", "Map cache"); return; }
        if (!ThemedDialog.Confirm(this,
                $"Delete all cached map tiles ({tiles:N0} tiles, {FormatBytes(bytes)})?\n\n" +
                "The map will work online only, as before, until you cache an area again.",
                "Delete map cache"))
            return;
        _cache.Clear();
        Refresh();
    }

    private static string FormatBytes(long bytes)
    {
        double b = bytes;
        if (b < 1024) return $"{b:0} B";
        if (b < 1024 * 1024) return $"{b / 1024:0.#} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):0.#} MB";
        return $"{b / (1024.0 * 1024 * 1024):0.##} GB";
    }
}
