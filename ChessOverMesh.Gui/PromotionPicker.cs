using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChessOverMesh.Chess;

namespace ChessOverMesh.Gui;

/// <summary>A tiny modal dialog asking which piece a promoting pawn becomes.</summary>
internal static class PromotionPicker
{
    public static PieceType Ask(Window owner, ChessOverMesh.Chess.Color color)
    {
        PieceType result = PieceType.Queen; // default if the dialog is dismissed

        var dialog = new Window
        {
            Title = "Promote to",
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x30))
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
        var fg = color == ChessOverMesh.Chess.Color.White
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFA, 0xFA, 0xFA))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20));

        foreach (var (type, glyph) in new[]
                 {
                     (PieceType.Queen, "♛"),
                     (PieceType.Rook, "♜"),
                     (PieceType.Bishop, "♝"),
                     (PieceType.Knight, "♞"),
                 })
        {
            var btn = new Button
            {
                Content = new TextBlock { Text = glyph, FontSize = 34, Foreground = fg },
                MinWidth = 56,
                MinHeight = 56,
                Margin = new Thickness(4),
                Tag = type
            };
            btn.Click += (_, _) => { result = (PieceType)btn.Tag; dialog.DialogResult = true; };
            panel.Children.Add(btn);
        }

        dialog.Content = panel;
        dialog.ShowDialog();
        return result;
    }
}
