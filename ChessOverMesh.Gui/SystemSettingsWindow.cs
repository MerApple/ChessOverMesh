using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>System settings: low-level app behaviour that isn't tied to a single device. Currently a single
/// "Cached messages" toggle — when turned off, the chat history is no longer persisted and the existing cache
/// is deleted (after a confirmation). Settings are applied immediately; the window only has a Close button.</summary>
internal sealed class SystemSettingsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

    private bool _suppress;   // guards the revert when the user cancels the delete confirmation

    public SystemSettingsWindow(Window owner)
    {
        Title = "System settings";
        Owner = owner;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        var root = new StackPanel { Margin = new Thickness(14) };

        root.Children.Add(new TextBlock
        {
            Text = "Low-level app behaviour.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        var cache = new CheckBox { Content = "Cached messages", Foreground = Fg, IsChecked = AppSettings.CacheMessages };
        cache.Checked += (_, _) =>
        {
            if (_suppress) return;
            AppSettings.CacheMessages = true;
        };
        // cache.Unchecked is wired up further down, after the cache-password section's controls exist — turning
        // caching off wipes every device and its password, and we refresh that section to reflect it.
        root.Children.Add(cache);
        root.Children.Add(new TextBlock
        {
            Text = "Keep a local copy of chat messages per device so the conversation reloads after a reconnect. " +
                   "Turning this off deletes all cached data for every device (including any cache passwords) and stops storing new messages.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14),
        });

        // Max system messages kept on screen.
        var limitRow = new StackPanel { Orientation = Orientation.Horizontal };
        limitRow.Children.Add(new TextBlock { Text = "Max system messages:", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center });
        var limitBox = new TextBox { MinWidth = 70, Margin = new Thickness(8, 0, 0, 0), Text = AppSettings.SystemMessageLimit.ToString() };
        limitBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(limitBox.Text, out var n) && n > 0)
            {
                AppSettings.SystemMessageLimit = n;
                (Owner as MainWindow)?.TrimSystemMessages();
            }
            limitBox.Text = AppSettings.SystemMessageLimit.ToString();   // normalise (revert bad input)
        };
        limitRow.Children.Add(limitBox);
        root.Children.Add(limitRow);
        root.Children.Add(new TextBlock
        {
            Text = "How many system-message lines to keep on screen. Oldest lines are dropped past this.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14),
        });

        // ---- Cache password (per device) ----
        root.Children.Add(new TextBlock { Text = "Cache encryption", Foreground = Fg, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
        string host = (Owner as MainWindow)?.CurrentHost ?? "";
        var statusText = new TextBlock { Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        var setBtn = new Button { Content = "Set / change password…", MinHeight = 26, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        var removeBtn = new Button { Content = "Remove password", MinHeight = 26, Padding = new Thickness(10, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Left };

        void RefreshCacheState()
        {
            if (host.Length == 0)
            {
                statusText.Text = "Connect to a device to set a cache password.";
                setBtn.IsEnabled = removeBtn.IsEnabled = false;
                return;
            }
            bool enc = DeviceCache.IsEncrypted(host);
            statusText.Text = enc
                ? "This device's cache is ENCRYPTED. You'll be asked for the password when connecting."
                : "This device's cache is not encrypted. Set a password to encrypt it.";
            setBtn.IsEnabled = true;
            removeBtn.IsEnabled = enc;
        }
        setBtn.Click += (_, _) =>
        {
            // Changing an existing password requires the current one — verified via Unlock, which also loads the
            // session key so the change re-encrypts the existing cache instead of losing it.
            Func<string, bool>? verifyCurrent = DeviceCache.IsEncrypted(host) ? (c => DeviceCache.Unlock(host, c)) : null;
            var pw = PromptNewPassword(this, verifyCurrent);
            if (pw == null) return;
            DeviceCache.SetPassword(host, pw);
            RefreshCacheState();
        };
        removeBtn.Click += (_, _) =>
        {
            // Removing the password keeps the cached data but stores it in plaintext, so require the current
            // password first (unlike deleting the cache, which destroys the data and needs no password). Unlock
            // also loads the session key so SetPassword("") decrypts the existing cache before re-writing it.
            if (!PromptCurrentPassword(this, c => DeviceCache.Unlock(host, c))) return;
            DeviceCache.SetPassword(host, "");
            RefreshCacheState();
        };
        RefreshCacheState();
        cache.Unchecked += (_, _) =>
        {
            if (_suppress) return;
            bool answer = ThemedDialog.Confirm(this,
                "Turning off cached messages deletes ALL cached data for every device on this computer — chat history, " +
                "node and telemetry data, saved channel settings and app keys — and removes any cache passwords. " +
                "This cannot be undone. Continue?",
                "Delete cached messages?");
            if (!answer)
            {
                _suppress = true;
                cache.IsChecked = true;   // revert — keep caching on
                _suppress = false;
                return;
            }
            AppSettings.CacheMessages = false;
            DeviceCache.ClearAllDevices();
            RefreshCacheState();   // the wiped device is no longer encrypted — reflect that in the password section
        };
        root.Children.Add(statusText);
        root.Children.Add(setBtn);
        root.Children.Add(removeBtn);
        root.Children.Add(new TextBlock
        {
            Text = "Encrypts this device's cached chat, node and telemetry data (AES) with a password you enter on " +
                   "connect. The password is not stored. If you forget it, you can delete the cache when connecting.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14),
        });

        // ---- Reset all app settings ----
        root.Children.Add(new TextBlock { Text = "Reset", Foreground = Fg, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
        var resetBtn = new Button { Content = "Reset app settings to defaults…", MinHeight = 26, Padding = new Thickness(10, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Left };
        resetBtn.Click += (_, _) =>
        {
            if (!ThemedDialog.Confirm(this,
                "Reset ALL app settings to their defaults? This restores colours, fonts, text sizes, sounds, message " +
                "limits and display toggles, and also forgets the last connected device, saved proxy logins and " +
                "remembered window sizes.\n\n" +
                "Your cached chat, node and telemetry data is NOT affected. Restart the app for all changes to take effect.",
                "Reset app settings?")) return;
            AppSettings.ResetToDefaults();
            ThemedDialog.Info(this, "App settings have been reset to defaults. Restart the app for all changes to take effect.", "Settings reset");
        };
        root.Children.Add(resetBtn);
        root.Children.Add(new TextBlock
        {
            Text = "Restores every app setting (colours, fonts, sizes, sounds, message limits, display toggles) to default " +
                   "and forgets the last device, saved proxy logins and remembered window sizes. Cached chat, node and " +
                   "telemetry data is kept. Some changes apply only after a restart.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14),
        });

        var closeBtn = new Button
        {
            Content = "Close", MinWidth = 80, MinHeight = 26, IsDefault = true, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeBtn.Click += (_, _) => Close();
        root.Children.Add(closeBtn);

        Content = root;
    }

    /// <summary>Modal that asks for a new cache password twice (must match, non-empty). Returns it, or null on cancel.
    /// When <paramref name="verifyCurrent"/> is supplied (changing an existing password), it also asks for the current
    /// password first and won't proceed until that verifies.</summary>
    private static string? PromptNewPassword(Window owner, Func<string, bool>? verifyCurrent = null)
    {
        var w = new Window
        {
            Title = "Set cache password", Owner = owner, Width = 360, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = Bg,
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        PasswordBox? cur = null;
        if (verifyCurrent != null)
        {
            panel.Children.Add(new TextBlock { Text = "Current password:", Foreground = Fg });
            cur = new PasswordBox { MinWidth = 260, Margin = new Thickness(0, 2, 0, 8) };
            panel.Children.Add(cur);
        }
        panel.Children.Add(new TextBlock { Text = "New password:", Foreground = Fg });
        var p1 = new PasswordBox { MinWidth = 260, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(p1);
        panel.Children.Add(new TextBlock { Text = "Confirm password:", Foreground = Fg });
        var p2 = new PasswordBox { MinWidth = 260, Margin = new Thickness(0, 2, 0, 0) };
        panel.Children.Add(p2);
        var err = new TextBlock { Foreground = Red, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(err);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", MinWidth = 80, MinHeight = 26, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) =>
        {
            if (cur != null && !verifyCurrent!(cur.Password)) { err.Text = "Current password is incorrect."; err.Visibility = Visibility.Visible; return; }
            if (p1.Password.Length == 0) { err.Text = "Password can't be empty."; err.Visibility = Visibility.Visible; return; }
            if (p1.Password != p2.Password) { err.Text = "Passwords don't match."; err.Visibility = Visibility.Visible; return; }
            w.Tag = p1.Password;
            w.DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 70, MinHeight = 26, IsCancel = true };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);
        w.Content = panel;
        w.Loaded += (_, _) => (cur ?? p1).Focus();
        return w.ShowDialog() == true ? (string?)w.Tag : null;
    }

    /// <summary>Modal that asks for the current cache password before an operation that would expose the cache
    /// (removing the password → storing it unencrypted). Won't return true until <paramref name="verify"/> accepts
    /// the entered password. Returns false on cancel.</summary>
    private static bool PromptCurrentPassword(Window owner, Func<string, bool> verify)
    {
        var w = new Window
        {
            Title = "Remove cache password", Owner = owner, Width = 360, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = Bg,
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "Enter the current password to store this device's cache unencrypted.",
            Foreground = Fg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock { Text = "Current password:", Foreground = Fg });
        var cur = new PasswordBox { MinWidth = 260, Margin = new Thickness(0, 2, 0, 0) };
        panel.Children.Add(cur);
        var err = new TextBlock { Foreground = Red, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(err);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Remove", MinWidth = 80, MinHeight = 26, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) =>
        {
            if (!verify(cur.Password)) { err.Text = "Current password is incorrect."; err.Visibility = Visibility.Visible; return; }
            w.DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 70, MinHeight = 26, IsCancel = true };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);
        w.Content = panel;
        w.Loaded += (_, _) => cur.Focus();
        return w.ShowDialog() == true;
    }
}
