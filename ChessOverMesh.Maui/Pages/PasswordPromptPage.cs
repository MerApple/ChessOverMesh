namespace ChessOverMesh.Maui;

/// <summary>Prompts for a device's cache password on connect. Result: a password to try (Unlock), a request to
/// wipe the cache (Delete), or cancel (both null/false — e.g. back-navigation). The page dismisses itself.</summary>
public sealed class PasswordPromptPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");
    static readonly Color Red = Color.FromArgb("#FF6B6B");

    readonly TaskCompletionSource<(string? password, bool delete)> _tcs = new();
    public Task<(string? password, bool delete)> Result => _tcs.Task;

    public PasswordPromptPage(string? error)
    {
        Title = "Cache password";
        BackgroundColor = Bg;

        var entry = new Entry { IsPassword = true, TextColor = Fg, Placeholder = "Cache password" };
        var root = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        root.Add(new Label { Text = "Cache password", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold });
        root.Add(new Label
        {
            Text = "This device's cache is encrypted. Enter its password to decrypt, or delete the cache to start fresh.",
            TextColor = Dim, FontSize = 12,
        });
        if (!string.IsNullOrEmpty(error)) root.Add(new Label { Text = error, TextColor = Red, FontSize = 12 });
        root.Add(entry);

        // Buttons only set the result; the caller dismisses the page (avoids a pop race with the next dialog).
        var unlock = new Button { Text = "Unlock", MinimumHeightRequest = 44 };
        unlock.Clicked += (_, _) => _tcs.TrySetResult((entry.Text ?? "", false));
        var del = new Button { Text = "Delete cache", MinimumHeightRequest = 44 };
        del.Clicked += (_, _) => _tcs.TrySetResult((null, true));
        root.Add(unlock);
        root.Add(del);

        Content = new ScrollView { Content = root };
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _tcs.TrySetResult((null, false));   // back-navigation = cancel (no-op if a button already set the result)
    }
}
