namespace ChessOverMesh.Maui;

/// <summary>
/// The System-messages filter: a switch per message category (Game, Connection, Nodes, …). Turning one off
/// hides that category's rows from the System list (live) and remembers the choice. Mirrors the desktop
/// "Filter ▾" dropdown.
/// </summary>
public sealed class SystemFilterPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");

    readonly MainPage _main;
    readonly VerticalStackLayout _list = new() { Spacing = 2 };

    static readonly SysCategory[] Categories =
        { SysCategory.Game, SysCategory.Connection, SysCategory.Nodes, SysCategory.Position,
          SysCategory.Telemetry, SysCategory.Traceroute, SysCategory.Admin, SysCategory.Requests,
          SysCategory.Outgoing, SysCategory.Warnings };

    public SystemFilterPage(MainPage main)
    {
        _main = main;
        Title = "Filter system messages";
        BackgroundColor = Bg;

        var allBtn = new Button { Text = "All", MinimumHeightRequest = 38, Padding = new Thickness(14, 0) };
        allBtn.Clicked += (_, _) => { foreach (var c in Categories) _main.SetSystemCategoryHidden(c, false); Build(); };
        var noneBtn = new Button { Text = "None", MinimumHeightRequest = 38, Padding = new Thickness(14, 0) };
        noneBtn.Clicked += (_, _) => { foreach (var c in Categories) _main.SetSystemCategoryHidden(c, true); Build(); };
        var btns = new HorizontalStackLayout { Spacing = 8 };
        btns.Add(allBtn); btns.Add(noneBtn);

        var close = new Button { Text = "Close", MinimumHeightRequest = 44, Margin = new Thickness(0, 12, 0, 0) };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        var root = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        root.Add(new Label { Text = "Filter system messages", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold });
        root.Add(new Label { Text = "Choose which types of system messages are shown.", TextColor = Dim, FontSize = 12 });
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
        var hidden = _main.HiddenSysCats;
        foreach (var cat in Categories)
        {
            var c = cat;   // capture for the closure
            var sw = new Switch { IsToggled = !hidden.Contains(cat), VerticalOptions = LayoutOptions.Center };
            sw.Toggled += (_, e) => _main.SetSystemCategoryHidden(c, hidden: !e.Value);
            var label = new Label { Text = Label(cat), TextColor = Fg, VerticalOptions = LayoutOptions.Center };
            var row = new HorizontalStackLayout { Spacing = 10, Margin = new Thickness(0, 2) };
            row.Add(sw);
            row.Add(label);
            _list.Children.Add(row);
        }
    }

    static string Label(SysCategory c) => c switch
    {
        SysCategory.Nodes => "Nodes (node info)",
        SysCategory.Requests => "Requests from others",
        SysCategory.Outgoing => "Outgoing (our device)",
        SysCategory.Warnings => "Warnings & notices",
        _ => c.ToString(),
    };
}
