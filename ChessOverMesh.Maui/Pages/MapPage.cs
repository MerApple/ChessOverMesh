using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>
/// Shows known mesh node positions on an OpenStreetMap/Leaflet map in an in-app WebView. Reuses the shared
/// <see cref="NodeMap"/> HTML (identical to the desktop app's browser map). The map needs an internet
/// connection to load Leaflet and the tiles.
/// </summary>
public sealed class MapPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");

    public MapPage(IReadOnlyList<MeshNodePosition> positions,
        IReadOnlyDictionary<uint, List<(double Lat, double Lon, long LastHeard, long PosTime)>>? history = null)
    {
        Title = "Node map";
        BackgroundColor = Bg;

        var web = new WebView { Source = new HtmlWebViewSource { Html = NodeMap.Html(positions, history) } };

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
}
