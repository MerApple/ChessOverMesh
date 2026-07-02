using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>
/// Traceroute to a node — the MAUI port of the desktop traceroute window. Sends the request and shows the route
/// (towards the node and back) when the reply arrives via MainPage's poll loop, or a timeout message after 30s.
/// </summary>
public sealed class TraceroutePage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");

    readonly MainPage _main;
    readonly MeshNode _target;
    readonly Label _status, _hops, _header;
    IDispatcherTimer? _timeout;
    bool _gotReply;

    public TraceroutePage(MainPage main, MeshNode target)
    {
        _main = main;
        _target = target;
        Title = "Traceroute";
        BackgroundColor = Bg;

        _header = new Label { Text = $"Traceroute to {target.Display}", TextColor = Fg, FontAttributes = FontAttributes.Bold, FontSize = 16 };
        _status = new Label { TextColor = Dim, FontSize = 12 };
        _hops = new Label { TextColor = Fg, FontFamily = "monospace", FontSize = 13 };

        var copy = new Button { Text = "Copy", MinimumHeightRequest = 40, Padding = new Thickness(12, 0) };
        copy.Clicked += async (_, _) => await Clipboard.SetTextAsync($"{_header.Text}\n{_status.Text}\n{_hops.Text}");
        var close = new Button { Text = "Close", MinimumHeightRequest = 40, Padding = new Thickness(12, 0) };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();
        var btns = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) } };
        btns.Add(new BoxView { Color = Colors.Transparent }, 0, 0);
        btns.Add(copy, 1, 0);
        btns.Add(close, 2, 0);

        var root = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        root.Add(_header);
        root.Add(_status);
        root.Add(_hops);
        root.Add(btns);
        Content = new ScrollView { Content = root };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        uint channel = _main.ChannelForNode(_target.Num);
        _status.Text = $"Sending request on channel {channel}… waiting for a reply (up to 30s).";

        _main.RegisterTracerouteWaiter(_target.Num, OnReply);

        _timeout = Dispatcher.CreateTimer();
        _timeout.Interval = TimeSpan.FromSeconds(30);
        _timeout.IsRepeating = false;
        _timeout.Tick += (_, _) =>
        {
            if (_gotReply) return;
            _status.Text = $"No response (timed out) on channel {channel}. The node may not be on that channel, " +
                           "or it's offline / out of range.";
        };
        _timeout.Start();

        try { await _main.SendTracerouteAsync(_target.Num); }
        catch (Exception ex) { _status.Text = $"Failed to send request: {ex.Message}"; }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timeout?.Stop();
        _main.UnregisterTracerouteWaiter(_target.Num);
    }

    void OnReply(MeshTraceroute t) => MainThread.BeginInvokeOnMainThread(() =>
    {
        _gotReply = true;
        _timeout?.Stop();
        string me = _main.DescribeNode(_main.MyNodeNum);
        string dest = _main.DescribeNode(t.Node);

        // Node labels for a path: from → intermediate hops → to. An unknown hop (node 0) shows as "Unknown".
        // No endpoint filtering, so the per-link SNR arrays line up with the links of this list.
        List<string> PathLabels(string fromLabel, IEnumerable<uint> mids, string toLabel)
        {
            var path = new List<string> { fromLabel };
            foreach (var h in mids) path.Add(h == 0 ? "Unknown" : _main.DescribeNode(h));
            path.Add(toLabel);
            return path;
        }

        var sb = new System.Text.StringBuilder();

        // Print each node, with the SNR of the link to the next node between them (matching the native app);
        // snr[i] is the link between nodes[i] and nodes[i+1].
        void RenderPath(string title, List<string> nodes, List<int> snr)
        {
            int hops = nodes.Count - 1;
            sb.AppendLine($"{title}  ({hops} hop{(hops == 1 ? "" : "s")}):");
            for (int i = 0; i < nodes.Count; i++)
            {
                sb.AppendLine($"   {i}.  {nodes[i]}");
                if (i < nodes.Count - 1)
                    sb.AppendLine($"        ↓ {(i < snr.Count ? MeshTraceroute.SnrLabel(snr[i]) : "SNR ?")}");
            }
        }

        RenderPath($"Towards {dest}", PathLabels($"{me}  (you)", t.Route, dest), t.SnrTowards);
        sb.AppendLine();
        RenderPath("Back to you", PathLabels(dest, t.RouteBack, $"{me}  (you)"), t.SnrBack);

        _status.Text = "Route received:";
        _hops.Text = sb.ToString().TrimEnd();
    });
}
