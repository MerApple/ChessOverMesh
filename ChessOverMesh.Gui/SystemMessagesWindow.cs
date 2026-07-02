using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>System-messages options: which background events are written to the system log pane
/// (received position broadcasts, and new-node / node-info events).</summary>
internal sealed class SystemMessagesWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));

    private readonly CheckBox _showPositions;
    private readonly CheckBox _showNewNodes;

    public bool ShowPositions => _showPositions.IsChecked == true;
    public bool ShowNewNodes => _showNewNodes.IsChecked == true;

    public SystemMessagesWindow(Window owner, bool showPositions, bool showNewNodes)
    {
        Title = "System messages";
        Owner = owner;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        var root = new StackPanel { Margin = new Thickness(14) };

        root.Children.Add(new TextBlock
        {
            Text = "Choose which background events appear in the system messages list.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        _showPositions = new CheckBox { Content = "Show position updates", Foreground = Fg, IsChecked = showPositions };
        root.Children.Add(_showPositions);
        root.Children.Add(new TextBlock
        {
            Text = "Log a line when another node's position is received (its broadcast, or a reply to your request).",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12),
        });

        _showNewNodes = new CheckBox { Content = "Show new node information", Foreground = Fg, IsChecked = showNewNodes };
        root.Children.Add(_showNewNodes);
        root.Children.Add(new TextBlock
        {
            Text = "Log a line when a node is heard for the first time, or when its node info (name/hardware) is received.",
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
