using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>Themed, blocking replacements for the native WPF <see cref="MessageBox"/> (which renders with the OS
/// font and ignores the app's "UI text size" setting). These are ordinary code-created <see cref="Window"/>s, so
/// the App-level class handler applies the current UI text size to them — they scale with the rest of the app.
/// Blocking (ShowDialog), so they're a drop-in for the MessageBox call sites that use the result inline.</summary>
internal static class ThemedDialog
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));

    private static (Window dlg, StackPanel panel) Build(Window? owner, string title, string message, double maxWidth)
    {
        var dlg = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = Bg,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = message, Foreground = Fg, TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth, Margin = new Thickness(0, 0, 0, 16),
        });
        dlg.Content = panel;
        return (dlg, panel);
    }

    private static Button Btn(string text, bool isDefault = false, bool isCancel = false) => new()
    {
        Content = text, MinWidth = 80, Padding = new Thickness(12, 3, 12, 3), IsDefault = isDefault, IsCancel = isCancel,
    };

    /// <summary>Blocking OK notice — replaces an informational/warning/error MessageBox.</summary>
    public static void Info(Window? owner, string message, string title)
    {
        var (dlg, panel) = Build(owner, title, message, 400);
        var ok = Btn("OK", isDefault: true, isCancel: true);
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.Click += (_, _) => dlg.Close();
        panel.Children.Add(ok);
        dlg.ShowDialog();
    }

    /// <summary>Blocking Yes/No confirm — replaces a YesNo MessageBox. Returns true for Yes; the window-close (X)
    /// counts as No. <paramref name="defaultYes"/> sets which button is the Enter default.</summary>
    public static bool Confirm(Window? owner, string message, string title, bool defaultYes = false, string yes = "Yes", string no = "No")
    {
        var (dlg, panel) = Build(owner, title, message, 400);
        bool result = false;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var yesBtn = Btn(yes, isDefault: defaultYes);
        yesBtn.Margin = new Thickness(0, 0, 8, 0);
        var noBtn = Btn(no, isDefault: !defaultYes, isCancel: true);
        yesBtn.Click += (_, _) => { result = true; dlg.Close(); };
        noBtn.Click += (_, _) => { result = false; dlg.Close(); };
        row.Children.Add(yesBtn);
        row.Children.Add(noBtn);
        panel.Children.Add(row);
        dlg.ShowDialog();
        return result;
    }
}
