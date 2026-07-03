namespace ChessOverMesh.Maui;

/// <summary>
/// The "Settings" bottom tab. Groups the Nodes / Channels / Colours / Sound sections, opening each via
/// <see cref="MainPage"/>'s existing handlers (they push their modal pages over the shell). Nodes and Channels
/// need a device connection; Colours and Sound work any time.
/// </summary>
public sealed class SettingsTabPage : ContentPage
{
    readonly MainPage _main;

    public SettingsTabPage(MainPage main)
    {
        _main = main;
        Title = "Settings";
        BackgroundColor = Color.FromArgb("#1E1E1E");

        var stack = new VerticalStackLayout { Padding = 16, Spacing = 12 };
        stack.Add(new Label { Text = "Settings", TextColor = Color.FromArgb("#E0E0E0"), FontSize = 20, FontAttributes = FontAttributes.Bold });
        stack.Add(Section("Nodes", needsConnection: true, _main.OpenNodes));
        stack.Add(Section("Channels", needsConnection: true, _main.OpenChannels));
        stack.Add(Section("Colours", needsConnection: false, _main.OpenColours));
        stack.Add(Section("Sound", needsConnection: false, _main.OpenSound));
        stack.Add(Section("Chess settings", needsConnection: false, _main.OpenChessSettings));
        stack.Add(Section("Chat messages", needsConnection: false, _main.OpenChatSettings));
        stack.Add(Section("System settings", needsConnection: false, _main.OpenSystemSettings));

        Content = new ScrollView { Content = stack };
    }

    // A full-width section button. If it needs a connection and there isn't one, it prompts instead of opening.
    Button Section(string text, bool needsConnection, Action open)
    {
        var btn = new Button { Text = text, MinimumHeightRequest = 48, HorizontalOptions = LayoutOptions.Fill };
        btn.Clicked += async (_, _) =>
        {
            if (needsConnection && !_main.IsConnected)
            {
                await ThemedDialogs.Alert(this, "Not connected", "Connect to a device first, then open " + text + ".", "OK");
                return;
            }
            open();
        };
        return btn;
    }
}
