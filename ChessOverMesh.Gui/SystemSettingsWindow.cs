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
        cache.Unchecked += (_, _) =>
        {
            if (_suppress) return;
            var answer = MessageBox.Show(this,
                "Turning off cached messages deletes all chat history currently cached on this computer for every device. " +
                "This cannot be undone. Continue?",
                "Delete cached messages?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                _suppress = true;
                cache.IsChecked = true;   // revert — keep caching on
                _suppress = false;
                return;
            }
            AppSettings.CacheMessages = false;
            DeviceCache.ClearAllChat();
        };
        root.Children.Add(cache);
        root.Children.Add(new TextBlock
        {
            Text = "Keep a local copy of chat messages per device so the conversation reloads after a reconnect. " +
                   "Turning this off deletes the existing cache and stops storing new messages.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14),
        });

        var board = new CheckBox { Content = "Show chessboard", Foreground = Fg, IsChecked = AppSettings.ShowChessboard };
        board.Checked += (_, _) =>
        {
            AppSettings.ShowChessboard = true;
            (Owner as MainWindow)?.ApplyChessboardVisibility();
        };
        board.Unchecked += (_, _) =>
        {
            AppSettings.ShowChessboard = false;
            (Owner as MainWindow)?.ApplyChessboardVisibility();
        };
        root.Children.Add(board);
        root.Children.Add(new TextBlock
        {
            Text = "Show the chessboard and moves. When off, the board is hidden and only system messages " +
                   "and channel chat are shown.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14),
        });

        var closeBtn = new Button
        {
            Content = "Close", Width = 80, Height = 26, IsDefault = true, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeBtn.Click += (_, _) => Close();
        root.Children.Add(closeBtn);

        Content = root;
    }
}
