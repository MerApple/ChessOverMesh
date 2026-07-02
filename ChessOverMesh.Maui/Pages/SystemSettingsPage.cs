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

    readonly MainPage _main;   // live page so the "Show chessboard" toggle applies immediately

    public SystemSettingsPage(MainPage main)
    {
        _main = main;
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

        var limitEntry = new Entry { Text = AppSettings.SystemMessageLimit.ToString(), Keyboard = Keyboard.Numeric,
            TextColor = Fg, WidthRequest = 90, HorizontalOptions = LayoutOptions.Start };
        void ApplyLimit()
        {
            if (int.TryParse(limitEntry.Text, out var n) && n > 0)
            {
                AppSettings.SystemMessageLimit = n;
                _main.TrimSystemMessages();
            }
            limitEntry.Text = AppSettings.SystemMessageLimit.ToString();   // normalise (revert bad input)
        }
        limitEntry.Completed += (_, _) => ApplyLimit();
        limitEntry.Unfocused += (_, _) => ApplyLimit();
        var limitRow = new HorizontalStackLayout { Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        limitRow.Add(new Label { Text = "Max system messages", TextColor = Fg, VerticalOptions = LayoutOptions.Center });
        limitRow.Add(limitEntry);
        root.Add(limitRow);
        root.Add(new Label
        {
            Text = "How many system-message lines to keep on screen. Oldest lines are dropped past this.",
            TextColor = Dim, FontSize = 11,
        });

        // ---- Cache password (per device) ----
        root.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#3F3F46"), Margin = new Thickness(0, 8, 0, 4) });
        root.Add(new Label { Text = "Cache encryption", TextColor = Fg, FontAttributes = FontAttributes.Bold });
        string host = _main.CurrentHost;
        var cacheStatus = new Label { TextColor = Dim, FontSize = 11 };
        var setPwBtn = new Button { Text = "Set / change password", MinimumHeightRequest = 44 };
        var removePwBtn = new Button { Text = "Remove password", MinimumHeightRequest = 44 };

        void RefreshCacheState()
        {
            if (host.Length == 0)
            {
                cacheStatus.Text = "Connect to a device to set a cache password.";
                setPwBtn.IsEnabled = removePwBtn.IsEnabled = false;
                return;
            }
            bool enc = DeviceCache.IsEncrypted(host);
            cacheStatus.Text = enc
                ? "This device's cache is ENCRYPTED. You'll be asked for the password when connecting."
                : "This device's cache is not encrypted. Set a password to encrypt it.";
            setPwBtn.IsEnabled = true;
            removePwBtn.IsEnabled = enc;
        }
        setPwBtn.Clicked += async (_, _) =>
        {
            // Changing an existing password requires the current one — verified via Unlock, which also loads the
            // session key so the change re-encrypts the existing cache instead of losing it.
            if (DeviceCache.IsEncrypted(host))
            {
                string? cur = await DisplayPromptAsync("Change cache password", "Current password:", "OK", "Cancel");
                if (string.IsNullOrEmpty(cur)) return;
                if (!DeviceCache.Unlock(host, cur)) { await DisplayAlert("Incorrect password", "The current password is incorrect.", "OK"); return; }
            }
            string? p1 = await DisplayPromptAsync("Set cache password", "New password:", "OK", "Cancel");
            if (string.IsNullOrEmpty(p1)) return;
            string? p2 = await DisplayPromptAsync("Set cache password", "Confirm password:", "OK", "Cancel");
            if (p1 != p2) { await DisplayAlert("Passwords don't match", "Please try again.", "OK"); return; }
            DeviceCache.SetPassword(host, p1);
            RefreshCacheState();
        };
        removePwBtn.Clicked += async (_, _) =>
        {
            bool ok = await DisplayAlert("Remove cache password",
                "Remove the cache password for this device? Its cache will be stored unencrypted.", "Remove", "Cancel");
            if (!ok) return;
            DeviceCache.SetPassword(host, "");
            RefreshCacheState();
        };
        RefreshCacheState();
        root.Add(cacheStatus);
        root.Add(setPwBtn);
        root.Add(removePwBtn);
        root.Add(new Label
        {
            Text = "Encrypts this device's cached chat, node and telemetry data (AES) with a password you enter on " +
                   "connect. The password isn't stored. If you forget it, you can delete the cache when connecting.",
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
