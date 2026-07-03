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

    private readonly MapTileCache _cache;
    private readonly Action<Window> _openCacheArea;
    private readonly TextBlock _summary;
    private readonly ItemsControl _regions;
    private readonly Button _deleteBtn;

    /// <param name="openCacheArea">Opens the "Cache map area" download dialog owned by the given window (the main
    /// window supplies its node-position-centred version).</param>
    public MapSettingsWindow(Window owner, MapTileCache cache, Action<Window> openCacheArea)
    {
        _cache = cache;
        _openCacheArea = openCacheArea;

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

        var cacheBtn = new Button { Content = "Cache map area…", MinWidth = 130, MinHeight = 28, Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Download a chosen area's map tiles for offline use." };
        cacheBtn.Click += (_, _) => { _openCacheArea(this); Refresh(); };

        _deleteBtn = new Button { Content = "Delete map cache", MinWidth = 130, MinHeight = 28, Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Delete all cached map tiles. The map then works online only, as before." };
        _deleteBtn.Click += (_, _) => DeleteCache();

        var closeBtn = new Button { Content = "Close", MinWidth = 80, MinHeight = 28, IsDefault = true, IsCancel = true };
        closeBtn.Click += (_, _) => Close();

        buttons.Children.Add(cacheBtn);
        buttons.Children.Add(_deleteBtn);
        buttons.Children.Add(closeBtn);
        root.Children.Add(buttons);

        Content = root;
        Refresh();
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
