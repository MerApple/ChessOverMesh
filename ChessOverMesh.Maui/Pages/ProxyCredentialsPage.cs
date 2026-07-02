namespace ChessOverMesh.Maui;

/// <summary>Prompts for a proxy's username/password on connect, with an option to remember them for this proxy.
/// Result: (ok, user, pass, remember); ok=false on cancel/back. The caller dismisses the page.</summary>
public sealed class ProxyCredentialsPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");
    static readonly Color Red = Color.FromArgb("#FF6B6B");

    readonly TaskCompletionSource<(bool ok, string user, string pass, bool remember)> _tcs = new();
    public Task<(bool ok, string user, string pass, bool remember)> Result => _tcs.Task;

    public ProxyCredentialsPage(string proxyHost, string? error, string? prefillUser)
    {
        Title = "Proxy sign-in";
        BackgroundColor = Bg;

        var user = new Entry { Text = prefillUser ?? "", TextColor = Fg, Placeholder = "Username" };
        var pass = new Entry { IsPassword = true, TextColor = Fg, Placeholder = "Password" };
        var remember = new Switch { IsToggled = true };

        var root = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        root.Add(new Label { Text = "Proxy sign-in", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold });
        root.Add(new Label { Text = $"The proxy at {proxyHost} requires a username and password.", TextColor = Dim, FontSize = 12 });
        if (!string.IsNullOrEmpty(error)) root.Add(new Label { Text = error, TextColor = Red, FontSize = 12 });
        root.Add(user);
        root.Add(pass);

        var rememberRow = new HorizontalStackLayout { Spacing = 8 };
        rememberRow.Add(remember);
        rememberRow.Add(new Label { Text = "Remember for this proxy", TextColor = Fg, VerticalOptions = LayoutOptions.Center });
        root.Add(rememberRow);

        var ok = new Button { Text = "Sign in", MinimumHeightRequest = 44 };
        ok.Clicked += (_, _) => _tcs.TrySetResult((true, user.Text?.Trim() ?? "", pass.Text ?? "", remember.IsToggled));
        var cancel = new Button { Text = "Cancel", MinimumHeightRequest = 44 };
        cancel.Clicked += (_, _) => _tcs.TrySetResult((false, "", "", false));
        root.Add(ok);
        root.Add(cancel);

        Content = new ScrollView { Content = root };
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _tcs.TrySetResult((false, "", "", false));   // back-navigation = cancel (no-op if a button already set the result)
    }
}
