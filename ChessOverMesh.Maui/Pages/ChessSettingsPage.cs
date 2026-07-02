namespace ChessOverMesh.Maui;

/// <summary>Chess board options. Currently just the "Show chessboard" toggle — when off, the board, game
/// buttons and the Moves/System toggle are hidden and the chess tab is renamed "System messages". Changes
/// persist immediately to <see cref="AppSettings"/> and apply live.</summary>
public sealed class ChessSettingsPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");

    readonly MainPage _main;   // live page so the "Show chessboard" toggle applies immediately

    public ChessSettingsPage(MainPage main)
    {
        _main = main;
        Title = "Chess settings";
        BackgroundColor = Bg;

        var root = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        root.Add(new Label { Text = "Chess settings", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold });

        var boardSwitch = new Switch { IsToggled = AppSettings.ShowChessboard, VerticalOptions = LayoutOptions.Center };
        boardSwitch.Toggled += (_, e) =>
        {
            AppSettings.ShowChessboard = e.Value;
            _main.ApplyChessboardVisibility();
            (Shell.Current as AppShell)?.RefreshChessTabTitle();
        };
        root.Add(Row(boardSwitch, "Show chessboard"));
        root.Add(new Label
        {
            Text = "Show the chessboard. When off, the chess tab is renamed “System messages” and shows only " +
                   "system messages.",
            TextColor = Dim, FontSize = 11,
        });

        var closeBtn = new Button { Text = "Close", MinimumHeightRequest = 44, Margin = new Thickness(0, 14, 0, 0) };
        closeBtn.Clicked += async (_, _) => await Navigation.PopModalAsync();
        root.Add(closeBtn);

        Content = new ScrollView { Content = root };
    }

    static View Row(Switch sw, string label)
    {
        var row = new HorizontalStackLayout { Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        row.Add(sw);
        row.Add(new Label { Text = label, TextColor = Fg, VerticalOptions = LayoutOptions.Center });
        return row;
    }
}
