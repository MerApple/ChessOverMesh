using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>Connection options: the opt-in TCP keep-alive heartbeat and auto-reconnect.</summary>
internal sealed class ConnectionSettingsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));

    private readonly CheckBox _autoReconnect;
    private readonly TextBox _heartbeat;
    private readonly int _initialHeartbeat;

    public bool AutoReconnect => _autoReconnect.IsChecked == true;

    /// <summary>The keep-alive heartbeat period in seconds (0 = off). Falls back to the value passed in if the field
    /// isn't a valid non-negative integer.</summary>
    public int HeartbeatSeconds => int.TryParse(_heartbeat.Text.Trim(), out var v) && v >= 0 ? v : _initialHeartbeat;

    public ConnectionSettingsWindow(Window owner, bool autoReconnect, int heartbeatSeconds, Action onClearRecentHosts)
    {
        Title = "Connection settings";
        Owner = owner;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = Bg;
        _initialHeartbeat = Math.Max(0, heartbeatSeconds);

        var root = new StackPanel { Margin = new Thickness(14) };

        // TCP keep-alive: send an empty heartbeat every N seconds so the device doesn't drop an idle connection
        // (it closes a client that's silent for ~15 min). 0 turns it off.
        var hbRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        hbRow.Children.Add(new TextBlock
        {
            Text = "Keep-alive heartbeat (seconds, 0 = off):", Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        _heartbeat = new TextBox { Text = _initialHeartbeat.ToString(), Width = 60, VerticalAlignment = VerticalAlignment.Center };
        hbRow.Children.Add(_heartbeat);
        root.Children.Add(hbRow);
        root.Children.Add(new TextBlock
        {
            Text = "Keeps a WiFi/TCP link alive when idle. Each heartbeat is logged under the Heartbeat category in " +
                   "system messages. 300 (5 min) is a safe default; 0 disables it.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14),
        });

        _autoReconnect = new CheckBox { Content = "Auto-reconnect", Foreground = Fg, IsChecked = autoReconnect };
        root.Children.Add(_autoReconnect);
        root.Children.Add(new TextBlock
        {
            Text = "If the device goes offline, automatically retry the connection once a minute until it " +
                   "reconnects — or until you click Cancel.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14),
        });

        // Recent hosts: a button to forget the addresses remembered in the Host dropdown. Clears immediately
        // (the owner confirms), independent of this dialog's OK/Cancel.
        var clearHosts = new Button { Content = "Clear recent hosts", MinHeight = 26, HorizontalAlignment = HorizontalAlignment.Left };
        clearHosts.Click += (_, _) => onClearRecentHosts();
        root.Children.Add(clearHosts);
        root.Children.Add(new TextBlock
        {
            Text = "Forget the recently connected addresses listed in the Host dropdown.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", MinWidth = 80, MinHeight = 26, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, MinHeight = 26, IsCancel = true };
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
    }
}
