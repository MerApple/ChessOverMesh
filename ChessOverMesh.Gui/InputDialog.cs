using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>A small modal text-input dialog (WPF has no built-in InputBox).</summary>
internal static class InputDialog
{
    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        string? result = null;

        var dialog = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.Height,
            Width = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
        };

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock
        {
            Text = prompt,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var box = new TextBox { Text = initial, MinHeight = 26 };
        root.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var ok = new Button { Content = "OK", MinWidth = 75, MinHeight = 26, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 75, MinHeight = 26, IsCancel = true };
        ok.Click += (_, _) => { result = box.Text; dialog.DialogResult = true; };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        dialog.Content = root;
        box.Focus();
        box.SelectAll();
        dialog.ShowDialog();
        return result;
    }
}
