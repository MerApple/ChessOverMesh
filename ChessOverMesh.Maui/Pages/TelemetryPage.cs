using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>
/// Environment-telemetry history for a node — the MAUI port of the desktop telemetry window. Lists every reading
/// heard this session (newest first), with buttons to request a fresh reading, copy, or clear. It refreshes live
/// while open as new telemetry arrives via MainPage's poll loop.
/// </summary>
public sealed class TelemetryPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");

    readonly MainPage _main;
    readonly MeshNode _target;
    readonly Label _header, _info, _sub, _status;
    readonly VerticalStackLayout _list = new() { Spacing = 2 };

    public TelemetryPage(MainPage main, MeshNode target)
    {
        _main = main;
        _target = target;
        Title = "Node info";
        BackgroundColor = Bg;

        _header = new Label { Text = $"All information for {target.Display}", TextColor = Fg, FontAttributes = FontAttributes.Bold, FontSize = 16 };
        // Everything we know about the node (refreshed live as signal/telemetry/position arrive).
        _info = new Label { TextColor = Fg, FontFamily = "monospace", FontSize = 12 };
        _sub = new Label { TextColor = Dim, FontSize = 12 };
        _status = new Label { TextColor = Dim, FontSize = 12 };

        var requestBtn = new Button { Text = "Request telemetry", HeightRequest = 40, Padding = new Thickness(10, 0), Margin = new Thickness(0, 0, 8, 8) };
        requestBtn.Clicked += OnRequest;
        var metricsBtn = new Button { Text = "Request battery/metrics", HeightRequest = 40, Padding = new Thickness(10, 0), Margin = new Thickness(0, 0, 8, 8) };
        metricsBtn.Clicked += async (_, _) =>
        {
            _status.Text = $"Requesting device metrics from {_target.Display}…";
            try { await _main.RequestDeviceMetricsForAsync(_target.Num); _status.Text = $"Requested device metrics from {_target.Display} — reply refreshes the info above."; }
            catch (Exception ex) { _status.Text = $"Request failed: {ex.Message}"; }
        };
        var deleteBtn = new Button { Text = "Delete", HeightRequest = 40, Padding = new Thickness(10, 0) };
        deleteBtn.Clicked += OnDelete;
        var copyBtn = new Button { Text = "Copy", HeightRequest = 40, Padding = new Thickness(10, 0) };
        copyBtn.Clicked += OnCopy;
        var closeBtn = new Button { Text = "Close", HeightRequest = 40, Padding = new Thickness(10, 0) };
        closeBtn.Clicked += async (_, _) => await Navigation.PopModalAsync();
        var btns = new HorizontalStackLayout { Spacing = 8 };
        btns.Add(deleteBtn); btns.Add(copyBtn); btns.Add(closeBtn);

        // Node actions (moved here from the Nodes list long-press menu). Wrap so they fit on narrow screens.
        var infoBtn = new Button { Text = "Request info", HeightRequest = 40, Padding = new Thickness(10, 0), Margin = new Thickness(0, 0, 8, 8) };
        infoBtn.Clicked += async (_, _) => { _status.Text = $"Requesting info from {_target.Display}…"; _status.Text = await _main.RequestNodeInfoForAsync(_target.Num); };
        var posBtn = new Button { Text = "Request position", HeightRequest = 40, Padding = new Thickness(10, 0), Margin = new Thickness(0, 0, 8, 8) };
        posBtn.Clicked += async (_, _) =>
        {
            _status.Text = $"Requesting position from {_target.Display}…";
            try { await _main.RequestNodePositionAsync(_target.Num); _status.Text = $"Requested position from {_target.Display}."; }
            catch (Exception ex) { _status.Text = $"Position request failed: {ex.Message}"; }
        };
        var traceBtn = new Button { Text = "Traceroute", HeightRequest = 40, Padding = new Thickness(10, 0), Margin = new Thickness(0, 0, 8, 8) };
        traceBtn.Clicked += async (_, _) => await Navigation.PushModalAsync(new TraceroutePage(_main, _target));
        var noiseBtn = new Button { Text = "Request noise floor", HeightRequest = 40, Padding = new Thickness(10, 0), Margin = new Thickness(0, 0, 8, 8) };
        noiseBtn.Clicked += async (_, _) =>
        {
            _status.Text = $"Requesting noise floor from {_target.Display}…";
            try { await _main.RequestNoiseFloorAsync(_target.Num); }
            catch (Exception ex) { _status.Text = $"Request failed: {ex.Message}"; }
        };
        var actions = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Direction = Microsoft.Maui.Layouts.FlexDirection.Row };
        actions.Add(infoBtn); actions.Add(posBtn); actions.Add(requestBtn); actions.Add(metricsBtn); actions.Add(traceBtn); actions.Add(noiseBtn);

        var root = new VerticalStackLayout { Padding = 16, Spacing = 8 };
        root.Add(_header);
        root.Add(_info);
        root.Add(actions);
        root.Add(new Label { Text = "Telemetry history:", TextColor = Fg, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 4, 0, 0) });
        root.Add(_sub);
        root.Add(_status);
        root.Add(btns);
        root.Add(_list);
        Content = new ScrollView { Content = root };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _main.SetTelemetryRefresh(() => MainThread.BeginInvokeOnMainThread(Load));
        Load();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _main.SetTelemetryRefresh(null);
    }

    void Load()
    {
        _info.Text = _main.NodeInfoText(_target.Num);
        var history = _main.NodeEnvironmentHistory(_target.Num);
        _list.Children.Clear();
        foreach (var e in history.Reverse())   // newest first
            _list.Children.Add(new Label { Text = $"{e.Timestamp:yyyy-MM-dd HH:mm:ss}   {EnvSummary(e)}", TextColor = Fg, FontFamily = "monospace", FontSize = 12 });
        _sub.Text = history.Count == 0
            ? "No telemetry cached for this node. Use \"Request telemetry\", or wait for a broadcast."
            : $"{history.Count} reading{(history.Count == 1 ? "" : "s")} this session (newest first).";
    }

    async void OnRequest(object? sender, EventArgs e)
    {
        _status.Text = "Requesting telemetry… (a node without an environment sensor won't reply)";
        try { await _main.RequestTelemetryForAsync(_target.Num); }
        catch (Exception ex) { _status.Text = $"Request failed: {ex.Message}"; }
    }

    async void OnDelete(object? sender, EventArgs e)
    {
        if (!await DisplayAlert("Delete telemetry", $"Delete all cached telemetry for {_target.Display}?", "Yes", "No")) return;
        _main.ClearNodeEnvironment(_target.Num);
        Load();
        _status.Text = "Telemetry deleted.";
    }

    async void OnCopy(object? sender, EventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(_header.Text);
        sb.AppendLine(_info.Text);
        sb.AppendLine("Telemetry history:");
        foreach (var e2 in _main.NodeEnvironmentHistory(_target.Num).Reverse())
            sb.AppendLine($"{e2.Timestamp:yyyy-MM-dd HH:mm:ss}   {EnvSummary(e2)}");
        await Clipboard.SetTextAsync(sb.ToString());
        _status.Text = "Copied to clipboard.";
    }

    static string EnvSummary(MeshEnvironment e)
    {
        var parts = new List<string> { $"{e.TemperatureC:0.#}°C" };
        if (e.RelativeHumidity > 0) parts.Add($"{e.RelativeHumidity:0}%RH");
        if (e.DewPointC is double dp) parts.Add($"dp {dp:0.#}°C");
        if (e.BarometricPressure > 0) parts.Add($"{e.BarometricPressure:0} hPa");
        return string.Join(" · ", parts);
    }
}
