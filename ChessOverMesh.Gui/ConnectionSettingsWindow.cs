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

    public bool AutoReconnect => _autoReconnect.IsChecked == true;

    public ConnectionSettingsWindow(Window owner, bool autoReconnect)
    {
        Title = "Connection settings";
        Owner = owner;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        var root = new StackPanel { Margin = new Thickness(14) };

        _autoReconnect = new CheckBox { Content = "Auto-reconnect", Foreground = Fg, IsChecked = autoReconnect };
        root.Children.Add(_autoReconnect);
        root.Children.Add(new TextBlock
        {
            Text = "If the device goes offline, automatically retry the connection once a minute until it " +
                   "reconnects — or until you click Cancel.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 80, Height = 26, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, Height = 26, IsCancel = true };
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
    }
}
