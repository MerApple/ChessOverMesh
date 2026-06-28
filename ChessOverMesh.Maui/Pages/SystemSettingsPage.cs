namespace ChessOverMesh.Maui;

/// <summary>System settings: low-level app behaviour that isn't tied to a single device. Currently a single
/// "Cached messages" toggle — when turned off, chat history is no longer persisted and the existing cache is
/// deleted (after a confirmation). Changes persist immediately to <see cref="AppSettings"/>.</summary>
public sealed class SystemSettingsPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");

    bool _suppress;   // guards the revert when the user cancels the delete confirmation

    public SystemSettingsPage()
    {
        Title = "System settings";
        BackgroundColor = Bg;

        var cacheSwitch = new Switch { IsToggled = AppSettings.CacheMessages, VerticalOptions = LayoutOptions.Center };
        cacheSwitch.Toggled += async (_, e) =>
        {
            if (_suppress) return;
            if (e.Value)
            {
                AppSettings.CacheMessages = true;
                return;
            }
            bool ok = await DisplayAlert("Delete cached messages?",
                "Turning off cached messages deletes all chat history currently cached on this device for every connection. " +
                "This cannot be undone. Continue?",
                "Delete", "Cancel");
            if (!ok)
            {
                _suppress = true;
                cacheSwitch.IsToggled = true;   // revert — keep caching on
                _suppress = false;
                return;
            }
            AppSettings.CacheMessages = false;
            DeviceCache.ClearAllChat();
        };

        var root = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        root.Add(new Label { Text = "System settings", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold });
        root.Add(new Label { Text = "Low-level app behaviour.", TextColor = Dim, FontSize = 12 });

        root.Add(Row(cacheSwitch, "Cached messages"));
        root.Add(new Label
        {
            Text = "Keep a local copy of chat messages per device so the conversation reloads after a reconnect. " +
                   "Turning this off deletes the existing cache and stops storing new messages.",
            TextColor = Dim, FontSize = 11,
        });

        var closeBtn = new Button { Text = "Close", HeightRequest = 44, Margin = new Thickness(0, 14, 0, 0) };
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
