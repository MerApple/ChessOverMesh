using System.Globalization;
using ChessOverMesh.Map;

namespace ChessOverMesh.Maui;

/// <summary>
/// "Cache map area for offline use" page: the user picks a centre (prefilled), a radius, and a zoom range, sees a
/// live tile-count/size estimate, then downloads those OpenStreetMap tiles into the shared
/// <see cref="MapCacheService.Cache"/> so the node map works with no internet. Cancellable; shows progress.
/// </summary>
public sealed class MapCachePage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Bar = Color.FromArgb("#252526");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");
    static readonly Color Warn = Color.FromArgb("#E57373");

    readonly Entry _lat, _lon, _radius, _name;
    readonly Picker _minZoom, _maxZoom;
    readonly Label _estimate, _status;
    readonly ProgressBar _progress;
    readonly Button _download, _stop, _close;
    CancellationTokenSource? _cts;

    public MapCachePage(double centerLat, double centerLon)
    {
        Title = "Cache map area";
        BackgroundColor = Bg;

        _lat = MakeEntry(centerLat.ToString("0.#####", CultureInfo.InvariantCulture), Keyboard.Numeric);
        _lon = MakeEntry(centerLon.ToString("0.#####", CultureInfo.InvariantCulture), Keyboard.Numeric);
        _radius = MakeEntry("5", Keyboard.Numeric);
        _name = MakeEntry("", Keyboard.Default);

        _minZoom = MakeZoomPicker(8);
        _maxZoom = MakeZoomPicker(15);

        _estimate = new Label { TextColor = Fg, FontSize = 13 };
        _status = new Label { TextColor = Dim, FontSize = 13 };
        _progress = new ProgressBar { Progress = 0, IsVisible = false };

        _download = new Button { Text = "Download", MinimumHeightRequest = 44 };
        _stop = new Button { Text = "Stop", MinimumHeightRequest = 44, IsEnabled = false };
        _close = new Button { Text = "Close", MinimumHeightRequest = 44 };

        _download.Clicked += async (_, _) => await DownloadAsync();
        _stop.Clicked += (_, _) => _cts?.Cancel();
        _close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        _lat.TextChanged += (_, _) => UpdateEstimate();
        _lon.TextChanged += (_, _) => UpdateEstimate();
        _radius.TextChanged += (_, _) => UpdateEstimate();
        _minZoom.SelectedIndexChanged += (_, _) => UpdateEstimate();
        _maxZoom.SelectedIndexChanged += (_, _) => UpdateEstimate();

        Content = BuildLayout();
        UpdateEstimate();
    }

    View BuildLayout()
    {
        var stack = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        stack.Add(new Label
        {
            Text = "Downloads the map tiles covering this area so the node map works offline. " +
                   "Larger areas and higher zoom levels take longer and use more storage.",
            TextColor = Dim, FontSize = 13,
        });

        stack.Add(FieldRow("Centre latitude", _lat));
        stack.Add(FieldRow("Centre longitude", _lon));
        stack.Add(FieldRow("Radius (km)", _radius));

        var zoomRow = new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center };
        zoomRow.Add(_minZoom);
        zoomRow.Add(new Label { Text = "to", TextColor = Dim, VerticalOptions = LayoutOptions.Center });
        zoomRow.Add(_maxZoom);
        zoomRow.Add(new Label { Text = "(higher = more detail)", TextColor = Dim, FontSize = 12, VerticalOptions = LayoutOptions.Center });
        stack.Add(FieldRow("Zoom levels", zoomRow));

        stack.Add(FieldRow("Name (optional)", _name));

        stack.Add(_estimate);
        stack.Add(_progress);
        stack.Add(_status);

        var buttons = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        _download.HorizontalOptions = LayoutOptions.Fill;
        buttons.Add(_download);
        buttons.Add(_stop);
        buttons.Add(_close);
        stack.Add(buttons);

        return new ScrollView { Content = stack };
    }

    static Grid FieldRow(string label, View field)
    {
        var grid = new Grid { ColumnSpacing = 8, ColumnDefinitions =
            { new ColumnDefinition(new GridLength(130)), new ColumnDefinition(GridLength.Star) } };
        grid.Add(new Label { Text = label, TextColor = Fg, VerticalOptions = LayoutOptions.Center }, 0, 0);
        grid.Add(field, 1, 0);
        return grid;
    }

    static Entry MakeEntry(string text, Keyboard keyboard) =>
        new() { Text = text, Keyboard = keyboard, TextColor = Fg, BackgroundColor = Bar };

    static Picker MakeZoomPicker(int selected)
    {
        var picker = new Picker { TextColor = Fg, BackgroundColor = Bar, MinimumWidthRequest = 70 };
        for (int z = 1; z <= 19; z++) picker.Items.Add(z.ToString());
        picker.SelectedIndex = selected - 1;
        return picker;
    }

    bool TryReadInputs(out GeoBounds bounds, out int minZoom, out int maxZoom)
    {
        bounds = default; minZoom = 0; maxZoom = 0;
        if (!double.TryParse(_lat.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) || lat < -85 || lat > 85)
        { _estimate.Text = "Enter a latitude between -85 and 85."; return false; }
        if (!double.TryParse(_lon.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) || lon < -180 || lon > 180)
        { _estimate.Text = "Enter a longitude between -180 and 180."; return false; }
        if (!double.TryParse(_radius.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) || radius <= 0 || radius > 500)
        { _estimate.Text = "Enter a radius between 0 and 500 km."; return false; }
        minZoom = _minZoom.SelectedIndex + 1;
        maxZoom = _maxZoom.SelectedIndex + 1;
        if (maxZoom < minZoom) { _estimate.Text = "\"To\" zoom must be at least the \"from\" zoom."; return false; }
        bounds = GeoBounds.AroundCenter(lat, lon, radius);
        return true;
    }

    void UpdateEstimate()
    {
        if (!TryReadInputs(out var bounds, out int minZoom, out int maxZoom)) { _download.IsEnabled = false; return; }
        long tiles = MapTileCache.EstimateTiles(bounds, minZoom, maxZoom);
        long bytes = MapTileCache.EstimateBytes(tiles);
        if (tiles > MapTileCache.MaxTilesPerRequest)
        {
            _estimate.Text = $"~{tiles:N0} tiles — too many (limit {MapTileCache.MaxTilesPerRequest:N0}). Reduce the radius or the maximum zoom.";
            _estimate.TextColor = Warn;
            _download.IsEnabled = false;
            return;
        }
        _estimate.TextColor = Fg;
        _estimate.Text = $"Estimated download: ~{tiles:N0} tiles, ~{FormatBytes(bytes)} (tiles already cached are skipped).";
        _download.IsEnabled = true;
    }

    async Task DownloadAsync()
    {
        if (!TryReadInputs(out var bounds, out int minZoom, out int maxZoom)) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var progress = new Progress<TileDownloadProgress>(p =>
        {
            _progress.Progress = p.Fraction;
            _status.Text = $"Downloading… {p.Done:N0}/{p.Total:N0} tiles  ({p.Downloaded:N0} new, {p.Skipped:N0} cached, {p.Failed:N0} failed)";
        });

        try
        {
            string name = (_name.Text ?? "").Trim();
            if (name.Length == 0) name = $"{bounds.CenterLat:0.###},{bounds.CenterLon:0.###}";
            var result = await MapCacheService.Cache.CacheAreaAsync(name, bounds, minZoom, maxZoom, progress, _cts.Token);
            _status.Text = result.Cancelled
                ? $"Stopped after {result.Downloaded:N0} of {result.Total:N0} tiles ({result.Skipped:N0} already cached)."
                : $"Cached {result.Downloaded:N0} new tiles ({result.Skipped:N0} already had, {result.Failed:N0} failed) for \"{name}\".";
            _status.TextColor = result.Failed > 0 && result.Downloaded == 0 ? Warn : Dim;
        }
        catch (Exception ex)
        {
            _status.Text = $"Download failed: {ex.Message}";
            _status.TextColor = Warn;
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    void SetBusy(bool busy)
    {
        _progress.IsVisible = busy;
        if (busy) _progress.Progress = 0;
        _download.IsEnabled = !busy;
        _stop.IsEnabled = busy;
        _lat.IsEnabled = _lon.IsEnabled = _radius.IsEnabled = _name.IsEnabled = _minZoom.IsEnabled = _maxZoom.IsEnabled = !busy;
    }

    protected override bool OnBackButtonPressed()
    {
        _cts?.Cancel();   // stop an in-flight download if the user backs out
        return base.OnBackButtonPressed();
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
