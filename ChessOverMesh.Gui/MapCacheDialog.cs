using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChessOverMesh.Map;

namespace ChessOverMesh.Gui;

/// <summary>
/// "Cache map area for offline use" dialog: the user picks a centre (prefilled), a radius, and a zoom range, sees
/// a live tile-count/size estimate, then downloads those OpenStreetMap tiles into the <see cref="MapTileCache"/>
/// so the map works with no internet. Cancellable; reports progress on a bar. Built in code to match the app's
/// dark theme (the GUI has no XAML dialogs for this).
/// </summary>
internal sealed class MapCacheDialog : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
    private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush Border = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

    private readonly MapTileCache _cache;
    private readonly TextBox _lat, _lon, _radius, _name;
    private readonly ComboBox _minZoom, _maxZoom;
    private readonly TextBlock _estimate, _status;
    private readonly ProgressBar _progress;
    private readonly Button _download, _stop, _close;
    private CancellationTokenSource? _cts;

    /// <summary>A one-line summary for the caller's status bar once the dialog closes.</summary>
    public string? ResultText { get; private set; }

    public MapCacheDialog(MapTileCache cache, double centerLat, double centerLon)
    {
        _cache = cache;
        Title = "Cache map area for offline use";
        Background = Bg;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        _lat = MakeField(centerLat.ToString("0.#####", CultureInfo.InvariantCulture));
        _lon = MakeField(centerLon.ToString("0.#####", CultureInfo.InvariantCulture));
        _radius = MakeField("5");
        _name = MakeField("");

        _minZoom = MakeZoomCombo(1, 19, 8);
        _maxZoom = MakeZoomCombo(1, 19, 15);

        _estimate = new TextBlock { Foreground = Fg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        _status = new TextBlock { Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        _progress = new ProgressBar { Height = 16, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

        _download = new Button { Content = "Download", MinWidth = 100, MinHeight = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        _stop = new Button { Content = "Stop", MinWidth = 80, MinHeight = 28, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        _close = new Button { Content = "Close", MinWidth = 80, MinHeight = 28, IsCancel = true };

        _download.Click += async (_, _) => await DownloadAsync();
        _stop.Click += (_, _) => _cts?.Cancel();
        _close.Click += (_, _) => Close();

        foreach (var tb in new[] { _lat, _lon, _radius }) tb.TextChanged += (_, _) => UpdateEstimate();
        _minZoom.SelectionChanged += (_, _) => UpdateEstimate();
        _maxZoom.SelectionChanged += (_, _) => UpdateEstimate();

        Content = BuildLayout();
        UpdateEstimate();
    }

    private FrameworkElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text = "Downloads the map tiles covering this area so the node map works offline. " +
                   "Larger areas and higher zoom levels take longer and use more storage.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddRow(int row, string label, FrameworkElement field)
        {
            var lbl = new TextBlock { Text = label, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            Grid.SetRow(field, row); Grid.SetColumn(field, 1);
            field.Margin = new Thickness(0, 4, 0, 4);
            grid.Children.Add(lbl); grid.Children.Add(field);
        }

        AddRow(0, "Centre latitude", _lat);
        AddRow(1, "Centre longitude", _lon);
        AddRow(2, "Radius (km)", _radius);

        var zoomPanel = new StackPanel { Orientation = Orientation.Horizontal };
        zoomPanel.Children.Add(_minZoom);
        zoomPanel.Children.Add(new TextBlock { Text = "to", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) });
        zoomPanel.Children.Add(_maxZoom);
        zoomPanel.Children.Add(new TextBlock { Text = "(higher = more detail)", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        AddRow(3, "Zoom levels", zoomPanel);

        AddRow(4, "Name (optional)", _name);

        root.Children.Add(grid);
        root.Children.Add(_estimate);
        root.Children.Add(_progress);
        root.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_download);
        buttons.Children.Add(_stop);
        buttons.Children.Add(_close);
        root.Children.Add(buttons);

        return root;
    }

    private static TextBox MakeField(string text) => new()
    {
        Text = text, MinHeight = 24, Padding = new Thickness(4, 2, 4, 2),
        Background = Field, Foreground = Fg, CaretBrush = Fg, BorderBrush = Border,
    };

    private ComboBox MakeZoomCombo(int min, int max, int selected)
    {
        var combo = new ComboBox { MinWidth = 60, MinHeight = 24, Background = Field, Foreground = Fg, BorderBrush = Border };
        for (int z = min; z <= max; z++) combo.Items.Add(z);
        combo.SelectedItem = selected;
        return combo;
    }

    // Reads the current inputs into (bounds, minZoom, maxZoom); returns false (and sets _estimate to the reason)
    // when they don't parse or are out of range.
    private bool TryReadInputs(out GeoBounds bounds, out int minZoom, out int maxZoom)
    {
        bounds = default; minZoom = 0; maxZoom = 0;
        if (!double.TryParse(_lat.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) || lat < -85 || lat > 85)
        { _estimate.Text = "Enter a latitude between -85 and 85."; return false; }
        if (!double.TryParse(_lon.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) || lon < -180 || lon > 180)
        { _estimate.Text = "Enter a longitude between -180 and 180."; return false; }
        if (!double.TryParse(_radius.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) || radius <= 0 || radius > 500)
        { _estimate.Text = "Enter a radius between 0 and 500 km."; return false; }
        minZoom = (int)(_minZoom.SelectedItem ?? 8);
        maxZoom = (int)(_maxZoom.SelectedItem ?? 15);
        if (maxZoom < minZoom) { _estimate.Text = "\"To\" zoom must be at least the \"from\" zoom."; return false; }
        bounds = GeoBounds.AroundCenter(lat, lon, radius);
        return true;
    }

    private void UpdateEstimate()
    {
        if (!TryReadInputs(out var bounds, out int minZoom, out int maxZoom)) { _download.IsEnabled = false; return; }
        long tiles = MapTileCache.EstimateTiles(bounds, minZoom, maxZoom);
        long bytes = MapTileCache.EstimateBytes(tiles);
        if (tiles > MapTileCache.MaxTilesPerRequest)
        {
            _estimate.Text = $"~{tiles:N0} tiles — too many (limit {MapTileCache.MaxTilesPerRequest:N0}). " +
                             "Reduce the radius or the maximum zoom.";
            _estimate.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));
            _download.IsEnabled = false;
            return;
        }
        _estimate.Foreground = Fg;
        _estimate.Text = $"Estimated download: ~{tiles:N0} tiles, ~{FormatBytes(bytes)} (tiles already cached are skipped).";
        _download.IsEnabled = true;
    }

    private async System.Threading.Tasks.Task DownloadAsync()
    {
        if (!TryReadInputs(out var bounds, out int minZoom, out int maxZoom)) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var progress = new Progress<TileDownloadProgress>(p =>
        {
            _progress.Value = p.Fraction;
            _status.Text = $"Downloading… {p.Done:N0}/{p.Total:N0} tiles  ({p.Downloaded:N0} new, {p.Skipped:N0} cached, {p.Failed:N0} failed)";
        });

        try
        {
            string name = _name.Text.Trim();
            if (name.Length == 0) name = $"{bounds.CenterLat:0.###},{bounds.CenterLon:0.###}";
            var result = await _cache.CacheAreaAsync(name, bounds, minZoom, maxZoom, progress, _cts.Token);
            ResultText = result.Cancelled
                ? $"Offline map: stopped after {result.Downloaded:N0} of {result.Total:N0} tiles ({result.Skipped:N0} already cached)."
                : $"Offline map: cached {result.Downloaded:N0} new tiles ({result.Skipped:N0} already had, {result.Failed:N0} failed) for \"{name}\".";
            _status.Text = ResultText;
            _status.Foreground = result.Failed > 0 && result.Downloaded == 0
                ? new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)) : Dim;
        }
        catch (Exception ex)
        {
            ResultText = $"Offline map download failed: {ex.Message}";
            _status.Text = ResultText;
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void SetBusy(bool busy)
    {
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) _progress.Value = 0;
        _download.IsEnabled = !busy;
        _stop.IsEnabled = busy;
        _lat.IsEnabled = _lon.IsEnabled = _radius.IsEnabled = _name.IsEnabled = _minZoom.IsEnabled = _maxZoom.IsEnabled = !busy;
        _close.Content = busy ? "Close" : "Close";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();   // stop an in-flight download if the user closes mid-run
        base.OnClosing(e);
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
