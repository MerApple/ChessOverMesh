using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>Prompts for a proxy's username/password on connect, with an option to remember them for this proxy.
/// The caller re-shows this with an error if the proxy rejects the credentials.</summary>
internal sealed class ProxyCredentialsPrompt : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

    private readonly TextBox _user = new() { MinWidth = 260 };
    private readonly PasswordBox _pw = new() { MinWidth = 260 };
    private readonly CheckBox _remember = new() { Content = "Remember for this proxy", Foreground = Fg, IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };

    public string User => _user.Text.Trim();
    public string Password => _pw.Password;
    public bool Remember => _remember.IsChecked == true;

    public ProxyCredentialsPrompt(Window owner, string proxyHost, string? error, string? prefillUser)
    {
        Title = "Proxy sign-in";
        Owner = owner;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock
        {
            Text = $"The proxy at {proxyHost} requires a username and password.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        if (!string.IsNullOrEmpty(error))
            root.Children.Add(new TextBlock { Text = error, Foreground = Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });

        root.Children.Add(new TextBlock { Text = "Username", Foreground = Fg, Margin = new Thickness(0, 0, 0, 2) });
        _user.Text = prefillUser ?? "";
        root.Children.Add(_user);
        root.Children.Add(new TextBlock { Text = "Password", Foreground = Fg, Margin = new Thickness(0, 8, 0, 2) });
        root.Children.Add(_pw);
        root.Children.Add(_remember);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Sign in", MinWidth = 80, MinHeight = 26, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", MinWidth = 70, MinHeight = 26, IsCancel = true };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        root.Children.Add(btns);

        Content = root;
        Loaded += (_, _) => { if (_user.Text.Length == 0) _user.Focus(); else _pw.Focus(); };
    }

    /// <summary>Shows the prompt; returns (user, password, remember) or null if the user cancelled.</summary>
    public static (string User, string Pass, bool Remember)? Show(Window owner, string proxyHost, string? error, string? prefillUser)
    {
        var dlg = new ProxyCredentialsPrompt(owner, proxyHost, error, prefillUser);
        return dlg.ShowDialog() == true ? (dlg.User, dlg.Password, dlg.Remember) : null;
    }
}
