using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>Chess board options: whether the board is shown, and the rainbow move effect (off by default).</summary>
internal sealed class ChessSettingsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));

    private readonly CheckBox _rainbow;

    /// <summary>Whether the rainbow move effect is enabled (read after the dialog is accepted).</summary>
    public bool RainbowEffect => _rainbow.IsChecked == true;

    public ChessSettingsWindow(Window owner, bool rainbowEffect)
    {
        Title = "Chess settings";
        Owner = owner;
        Width = 330;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        var root = new StackPanel { Margin = new Thickness(14) };

        // Show chessboard applies immediately (unlike the rainbow effect, which is read on OK) so the board
        // shows/hides live behind the dialog.
        var board = new CheckBox
        {
            Content = "Show chessboard",
            Foreground = Fg,
            IsChecked = AppSettings.ShowChessboard,
            Margin = new Thickness(0, 0, 0, 6),
        };
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
            Text = "Show the chessboard and moves, and the “Chess” button. When off, the board is hidden " +
                   "and only system messages and channel chat are shown.",
            Foreground = Dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        _rainbow = new CheckBox
        {
            Content = "Rainbow move effect",
            Foreground = Fg,
            IsChecked = rainbowEffect,
            Margin = new Thickness(0, 0, 0, 6),
        };
        root.Children.Add(_rainbow);

        root.Children.Add(new TextBlock
        {
            Text = "When on, a rainbow wave ripples outward from each moved piece across the green squares, then " +
                   "fades back to green. When off, the board looks as it always has.",
            Foreground = Dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
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
