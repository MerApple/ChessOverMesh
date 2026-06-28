namespace ChessOverMesh.Maui;

/// <summary>
/// The RX view filter: choose which channels and DMs are shown in chat, with All/None and an unread badge per
/// item. Hiding a target stops its messages from showing (and from notifying); they accrue as "unread" and are
/// revealed when you show it again. Mirrors the desktop RX dropdown.
/// </summary>
public sealed class RxFilterPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");
    static readonly Color Badge = Color.FromArgb("#7FC8E8");

    readonly MainPage _main;
    readonly VerticalStackLayout _list = new() { Spacing = 2 };

    public RxFilterPage(MainPage main)
    {
        _main = main;
        Title = "Show channels & DMs";
        BackgroundColor = Bg;

        var allBtn = new Button { Text = "All", HeightRequest = 38, Padding = new Thickness(14, 0) };
        allBtn.Clicked += (_, _) => { _main.ShowAllRx(); Build(); };
        var noneBtn = new Button { Text = "None", HeightRequest = 38, Padding = new Thickness(14, 0) };
        noneBtn.Clicked += (_, _) => { _main.HideAllRx(); Build(); };
        var btns = new HorizontalStackLayout { Spacing = 8 };
        btns.Add(allBtn); btns.Add(noneBtn);

        var close = new Button { Text = "Close", HeightRequest = 44, Margin = new Thickness(0, 12, 0, 0) };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        var root = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        root.Add(new Label { Text = "Show channels & DMs", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold });
        root.Add(new Label { Text = "Choose what's shown in chat. ● marks unread messages on hidden ones. 🗑 deletes all messages on that channel/DM.", TextColor = Dim, FontSize = 12 });
        root.Add(btns);
        root.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#3F3F46") });
        root.Add(_list);
        root.Add(close);
        Content = new ScrollView { Content = root };
        Build();
    }

    void Build()
    {
        _list.Children.Clear();
        foreach (var t in _main.RxTargets())
        {
            var target = t;   // capture for the closures
            var sw = new Switch { IsToggled = !_main.IsRxHidden(target.IsDm, target.Id), VerticalOptions = LayoutOptions.Center };
            sw.Toggled += (_, e) =>
            {
                _main.SetRxHidden(target.IsDm, target.Id, hidden: !e.Value);
                MainThread.BeginInvokeOnMainThread(Build);   // refresh the unread badges
            };
            var label = new Label { Text = target.Label, TextColor = Fg, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Start };
            int unread = _main.RxUnread(target.IsDm, target.Id);
            var badge = new Label { Text = unread > 0 ? $"●{unread}" : "", TextColor = Badge, VerticalOptions = LayoutOptions.Center };
            var del = new Button { Text = "🗑", FontSize = 15, Padding = new Thickness(8, 0), HeightRequest = 34, BackgroundColor = Colors.Transparent, TextColor = Fg };
            del.Clicked += async (_, _) => await DeletePromptAsync(target);

            // [switch] [label …grows…] [badge] [delete]
            var row = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 2),
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) } };
            row.Add(sw, 0, 0);
            row.Add(label, 1, 0);
            row.Add(badge, 2, 0);
            row.Add(del, 3, 0);
            _list.Children.Add(row);
        }
    }

    async Task DeletePromptAsync(MainPage.ChatTxTarget t)
    {
        bool ok = await DisplayAlert("Delete messages",
            $"Delete all messages on {t.Label}?\n\nThis removes them from chat and the saved history on this phone.", "Delete", "Cancel");
        if (!ok) return;
        _main.DeleteChatTarget(t.IsDm, t.Id);
        Build();   // refresh badges/state
    }
}
