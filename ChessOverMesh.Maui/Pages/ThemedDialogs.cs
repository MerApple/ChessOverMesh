using Microsoft.Maui.Controls.Shapes;

namespace ChessOverMesh.Maui;

/// <summary>Themed modal dialogs that follow the app's UI text size — unlike the native
/// <c>DisplayAlert</c>/<c>DisplayPromptAsync</c>/<c>DisplayActionSheet</c>, which are OS-rendered and ignore it.
/// The method signatures mirror the native ones (with a leading <c>host</c> page), so a call site converts by a
/// uniform text replace: <c>DisplayAlert(</c> → <c>ThemedDialogs.Alert(this, </c> etc. Overload resolution then
/// picks the 1-button (Task) vs 2-button (Task&lt;bool&gt;) form by argument count, exactly like the native API.
/// The dialog's buttons/labels use the implicit styles, so they scale with the "App text size" setting.</summary>
public static class ThemedDialogs
{
    /// <summary>One-button notice — mirrors <c>DisplayAlert(title, message, cancel)</c>.</summary>
    public static async Task Alert(Page host, string title, string message, string cancel)
        => await ShowAsync(host, new ThemedDialogPage(title, message, new[] { (cancel, "ok", false) }));

    /// <summary>Two-button confirm — mirrors <c>DisplayAlert(title, message, accept, cancel)</c>. Returns true for accept.</summary>
    public static async Task<bool> Alert(Page host, string title, string message, string accept, string cancel)
        => await ShowAsync(host, new ThemedDialogPage(title, message, new[] { (accept, "yes", false), (cancel, "no", false) }, cancelResult: "no")) == "yes";

    /// <summary>Text prompt — mirrors <c>DisplayPromptAsync(...)</c>. Returns the entered text, or null if cancelled.</summary>
    public static async Task<string?> Prompt(Page host, string title, string message, string accept = "OK",
                                             string cancel = "Cancel", string? placeholder = null, int maxLength = -1,
                                             Keyboard? keyboard = null, string initialValue = "")
    {
        var page = new ThemedDialogPage(title, message, new[] { (accept, "accept", false), (cancel, "cancel", false) },
                                        withEntry: true, entryInitial: initialValue, keyboard: keyboard,
                                        placeholder: placeholder, maxLength: maxLength, cancelResult: "cancel");
        await host.Navigation.PushModalAsync(page);
        var r = await page.Result;
        string? text = page.EntryText;
        if (host.Navigation.ModalStack.Contains(page)) await host.Navigation.PopModalAsync();
        return r == "accept" ? (text ?? "") : null;
    }

    /// <summary>Action-sheet menu — mirrors <c>DisplayActionSheet(title, cancel, destruction, buttons)</c>. Returns the
    /// chosen label, the cancel label if cancelled, or null if dismissed.</summary>
    public static async Task<string?> ActionSheet(Page host, string title, string? cancel, string? destruction, params string[] buttons)
    {
        var items = new List<(string, string?, bool)>();
        if (!string.IsNullOrEmpty(destruction)) items.Add((destruction!, destruction, true));
        foreach (var b in buttons) items.Add((b, b, false));
        if (!string.IsNullOrEmpty(cancel)) items.Add((cancel!, cancel, false));
        return await ShowAsync(host, new ThemedDialogPage(title, null, items.ToArray(), cancelResult: null));
    }

    static async Task<string?> ShowAsync(Page host, ThemedDialogPage page)
    {
        await host.Navigation.PushModalAsync(page);
        var r = await page.Result;
        if (host.Navigation.ModalStack.Contains(page)) await host.Navigation.PopModalAsync();
        return r;
    }
}

/// <summary>The modal page behind <see cref="ThemedDialogs"/>: a centered dark card with a title, optional message,
/// optional text entry, and a vertical stack of buttons. Resolves its <see cref="Result"/> task on a button tap or
/// on back-navigation (returning <c>cancelResult</c>).</summary>
public sealed class ThemedDialogPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#2D2D30");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Red = Color.FromArgb("#FF6B6B");

    readonly TaskCompletionSource<string?> _tcs = new();
    public Task<string?> Result => _tcs.Task;
    readonly Entry? _entry;
    public string? EntryText => _entry?.Text;
    readonly string? _cancelResult;

    public ThemedDialogPage(string? title, string? message, (string label, string? result, bool destructive)[] buttons,
                            bool withEntry = false, string? entryInitial = null, Keyboard? keyboard = null,
                            string? placeholder = null, int maxLength = -1, string? cancelResult = null)
    {
        _cancelResult = cancelResult;
        BackgroundColor = Color.FromArgb("#66000000");   // dim the app behind the card

        var stack = new VerticalStackLayout { Spacing = 12 };
        if (!string.IsNullOrEmpty(title))
            stack.Add(new Label { Text = title, TextColor = Fg, FontAttributes = FontAttributes.Bold });
        if (!string.IsNullOrEmpty(message))
            stack.Add(new Label { Text = message, TextColor = Fg });
        if (withEntry)
        {
            _entry = new Entry { Text = entryInitial ?? "", TextColor = Fg, Keyboard = keyboard ?? Keyboard.Default };
            if (!string.IsNullOrEmpty(placeholder)) _entry.Placeholder = placeholder;
            if (maxLength > 0) _entry.MaxLength = maxLength;
            stack.Add(_entry);
        }
        foreach (var (label, result, destructive) in buttons)
        {
            var b = new Button { Text = label };
            if (destructive) b.TextColor = Red;
            b.Clicked += (_, _) => _tcs.TrySetResult(result);
            stack.Add(b);
        }

        Content = new Border
        {
            BackgroundColor = Bg,
            Stroke = Color.FromArgb("#3F3F46"),
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Padding = 18,
            Margin = 24,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = new ScrollView { Content = stack },
        };
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _tcs.TrySetResult(_cancelResult);   // no-op if a button already resolved it
    }
}
