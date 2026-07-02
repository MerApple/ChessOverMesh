using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>Prompts for a device's cache password on connect. The user can enter the password (Unlock), wipe the
/// cache to start fresh (Delete cache), or Cancel. The caller verifies the password and re-shows this with an
/// error on a wrong attempt.</summary>
internal sealed class PasswordPromptWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

    private readonly PasswordBox _pw = new() { MinWidth = 260 };
    public string Password => _pw.Password;
    public bool DeleteRequested { get; private set; }

    public PasswordPromptWindow(Window owner, string? error)
    {
        Title = "Cache password";
        Owner = owner;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock
        {
            Text = "This device's cache is encrypted. Enter its password to decrypt, or delete the cache to start fresh.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        if (!string.IsNullOrEmpty(error))
            root.Children.Add(new TextBlock { Text = error, Foreground = Red, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(_pw);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var unlock = new Button { Content = "Unlock", MinWidth = 80, MinHeight = 26, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        unlock.Click += (_, _) => DialogResult = true;
        var del = new Button { Content = "Delete cache", MinWidth = 100, MinHeight = 26, Margin = new Thickness(0, 0, 6, 0) };
        del.Click += (_, _) => { DeleteRequested = true; DialogResult = true; };
        var cancel = new Button { Content = "Cancel", MinWidth = 70, MinHeight = 26, IsCancel = true };
        btns.Children.Add(unlock);
        btns.Children.Add(del);
        btns.Children.Add(cancel);
        root.Children.Add(btns);

        Content = root;
        Loaded += (_, _) => _pw.Focus();
    }
}
