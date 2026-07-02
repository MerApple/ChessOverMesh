namespace ChessOverMesh.Maui;

/// <summary>System-messages options: which background events are written to the system messages list (received
/// position broadcasts, and new-node / node-info events). Toggles persist immediately to <see cref="AppSettings"/>.</summary>
public sealed class SystemMessagesPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");

    public SystemMessagesPage()
    {
        Title = "System messages";
        BackgroundColor = Bg;

        var positionSwitch = new Switch { IsToggled = AppSettings.ShowPositionUpdates, VerticalOptions = LayoutOptions.Center };
        positionSwitch.Toggled += (_, e) => AppSettings.ShowPositionUpdates = e.Value;

        var newNodeSwitch = new Switch { IsToggled = AppSettings.ShowNewNodeInfo, VerticalOptions = LayoutOptions.Center };
        newNodeSwitch.Toggled += (_, e) => AppSettings.ShowNewNodeInfo = e.Value;

        var root = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        root.Add(new Label { Text = "System messages", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold });
        root.Add(new Label { Text = "Choose which background events appear in the system messages list.", TextColor = Dim, FontSize = 12 });

        root.Add(Row(positionSwitch, "Show position updates"));
        root.Add(new Label { Text = "Log a line when another node's position is received (its broadcast, or a reply to your request).", TextColor = Dim, FontSize = 11 });

        root.Add(Row(newNodeSwitch, "Show new node information"));
        root.Add(new Label { Text = "Log a line when a node is heard for the first time, or when its node info (name/hardware) is received.", TextColor = Dim, FontSize = 11 });

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
