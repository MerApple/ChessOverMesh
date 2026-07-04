namespace ChessOverMesh.Maui;

/// <summary>
/// Message-type colour picker (MAUI port of the desktop ColorSettingsWindow). MAUI has no native colour
/// dialog, so each type offers preset swatches plus a #RRGGBB field. Changes persist immediately and apply
/// to subsequently-logged lines (existing lines keep the colour they were logged with).
/// </summary>
public sealed class ColorSettingsPage : ContentPage
{
    sealed record Choice(string Name, Func<Color> Get, Action<Color> Set, Color Default);

    /// <summary>One editable list font: current family/size and a callback that applies + persists a change.</summary>
    public sealed record FontChoice(string Name, string Family, double Size, Action<string, double> Apply);

    // Android exposes only the system-generic font families (display label → MAUI/Android family value).
    static readonly (string Label, string Value)[] Families =
        { ("Default", ""), ("Serif", "serif"), ("Monospace", "monospace") };
    static readonly int[] FontSizes = { 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28 };

    static readonly string[] Presets =
    {
        "#E0E0E0", "#FFFFFF", "#FFC107", "#FFA000", "#77DD77", "#4CAF50",
        "#FF6B6B", "#E53935", "#64B5F6", "#BA68C8", "#B0B0B0", "#80CBC4",
    };

    public ColorSettingsPage(IReadOnlyList<FontChoice>? fonts = null)
    {
        Title = "Colours & fonts";
        BackgroundColor = Color.FromArgb("#2D2D30");

        var choices = new[]
        {
            new Choice("Received / normal", () => Palette.Normal, v => { Palette.Normal = v; AppSettings.NormalColor = Hex(v); }, Color.FromRgb(0xE0,0xE0,0xE0)),
            new Choice("Received direct message", () => Palette.Dm, v => { Palette.Dm = v; AppSettings.DmColor = Hex(v); }, Color.FromRgb(0xC7,0x9E,0xFF)),
            new Choice("Sending — awaiting ack", () => Palette.Pending, v => { Palette.Pending = v; AppSettings.PendingColor = Hex(v); }, Color.FromRgb(0xFF,0xC1,0x07)),
            new Choice("Delivered / acknowledged", () => Palette.Acked, v => { Palette.Acked = v; AppSettings.AckedColor = Hex(v); }, Color.FromRgb(0x77,0xDD,0x77)),
            new Choice("Relayed (rebroadcast heard)", () => Palette.Relayed, v => { Palette.Relayed = v; AppSettings.RelayedColor = Hex(v); }, Color.FromRgb(0x80,0xCB,0xC4)),
            new Choice("Old cached messages", () => Palette.Cached, v => { Palette.Cached = v; AppSettings.CachedColor = Hex(v); }, Color.FromRgb(0x9E,0x9E,0x9E)),
            new Choice("Failed / warning", () => Palette.Warning, v => { Palette.Warning = v; AppSettings.WarningColor = Hex(v); }, Color.FromRgb(0xFF,0x6B,0x6B)),
            // System-message categories (System messages only — these don't affect chat).
            new Choice("System · Game", () => Palette.SysGame, v => { Palette.SysGame = v; AppSettings.SysGameColor = Hex(v); }, Color.FromRgb(0xE0,0xE0,0xE0)),
            new Choice("System · Connection", () => Palette.SysConnection, v => { Palette.SysConnection = v; AppSettings.SysConnectionColor = Hex(v); }, Color.FromRgb(0x80,0xCB,0xC4)),
            new Choice("System · Nodes", () => Palette.SysNodes, v => { Palette.SysNodes = v; AppSettings.SysNodesColor = Hex(v); }, Color.FromRgb(0x7F,0xC8,0xE8)),
            new Choice("System · Position", () => Palette.SysPosition, v => { Palette.SysPosition = v; AppSettings.SysPositionColor = Hex(v); }, Color.FromRgb(0xA5,0xD6,0xA7)),
            new Choice("System · Telemetry", () => Palette.SysTelemetry, v => { Palette.SysTelemetry = v; AppSettings.SysTelemetryColor = Hex(v); }, Color.FromRgb(0xC5,0xA3,0xFF)),
            new Choice("System · Traceroute", () => Palette.SysTraceroute, v => { Palette.SysTraceroute = v; AppSettings.SysTracerouteColor = Hex(v); }, Color.FromRgb(0xFF,0xCC,0x80)),
            new Choice("System · Admin", () => Palette.SysAdmin, v => { Palette.SysAdmin = v; AppSettings.SysAdminColor = Hex(v); }, Color.FromRgb(0xFF,0xD5,0x4F)),
            new Choice("System · Requests", () => Palette.SysRequests, v => { Palette.SysRequests = v; AppSettings.SysRequestsColor = Hex(v); }, Color.FromRgb(0xF4,0x8F,0xB1)),
            new Choice("System · Warnings", () => Palette.SysWarnings, v => { Palette.SysWarnings = v; AppSettings.SysWarningsColor = Hex(v); }, Color.FromRgb(0xFF,0x6B,0x6B)),
        };

        var root = new VerticalStackLayout { Padding = 14, Spacing = 10 };
        root.Add(new Label { Text = "Pick a colour for each message type. Changes apply to new lines.", TextColor = Color.FromArgb("#E0E0E0") });
        foreach (var ch in choices) root.Add(BuildRow(ch));

        if (fonts is { Count: > 0 })
        {
            root.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#3F3F46"), Margin = new Thickness(0, 10, 0, 4) });
            root.Add(new Label { Text = "Text font & size", TextColor = Color.FromArgb("#E0E0E0"), FontAttributes = FontAttributes.Bold });
            root.Add(new Label { Text = "Font for each list (Android offers the system-generic families).", TextColor = Color.FromArgb("#B0B0B0"), FontSize = 12 });
            foreach (var f in fonts) root.Add(BuildFontRow(f));
        }

        root.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#3F3F46"), Margin = new Thickness(0, 10, 0, 4) });
        root.Add(new Label { Text = "App text size", TextColor = Color.FromArgb("#E0E0E0"), FontAttributes = FontAttributes.Bold });
        root.Add(new Label { Text = "Size of buttons and labels across the app (settings screens). The lists above keep their own sizes.",
            TextColor = Color.FromArgb("#B0B0B0"), FontSize = 12 });
        root.Add(BuildUiTextRow());

        var done = new Button { Text = "Done", MinimumHeightRequest = 40, Margin = new Thickness(0, 12, 0, 0) };
        done.Clicked += async (_, _) => await Navigation.PopModalAsync();
        root.Add(done);

        Content = new ScrollView { Content = root };
    }

    View BuildRow(Choice ch)
    {
        var swatch = new BoxView { WidthRequest = 28, HeightRequest = 20, Color = ch.Get(), CornerRadius = 3, VerticalOptions = LayoutOptions.Center };
        var sample = new Label { Text = ch.Name, TextColor = ch.Get(), VerticalOptions = LayoutOptions.Center };
        var hex = new Entry { Text = Hex(ch.Get()), MinimumWidthRequest = 110, TextColor = Color.FromArgb("#E0E0E0"), Placeholder = "#RRGGBB" };

        void Apply(Color c)
        {
            ch.Set(c);
            swatch.Color = c;
            sample.TextColor = c;
            hex.Text = Hex(c);
        }

        var presets = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var p in Presets)
        {
            var c = Color.FromArgb(p);
            var b = new Button { BackgroundColor = c, MinimumWidthRequest = 34, MinimumHeightRequest = 28, Margin = 3, CornerRadius = 4 };
            b.Clicked += (_, _) => Apply(c);
            presets.Add(b);
        }

        var applyBtn = new Button { Text = "Apply", MinimumHeightRequest = 36, Padding = new Thickness(10, 0) };
        applyBtn.Clicked += (_, _) => { var c = Parse(hex.Text); if (c != null) Apply(c); };
        var resetBtn = new Button { Text = "Reset", MinimumHeightRequest = 36, Padding = new Thickness(10, 0) };
        resetBtn.Clicked += (_, _) => Apply(ch.Default);

        var header = new HorizontalStackLayout { Spacing = 8 };
        header.Add(swatch);
        header.Add(sample);

        var hexRow = new HorizontalStackLayout { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        hexRow.Add(hex);
        hexRow.Add(applyBtn);
        hexRow.Add(resetBtn);

        var box = new VerticalStackLayout { Spacing = 2, Margin = new Thickness(0, 6, 0, 6) };
        box.Add(header);
        box.Add(presets);
        box.Add(hexRow);
        return new Border { Stroke = Color.FromArgb("#3F3F46"), StrokeThickness = 1, Padding = 8, Content = box };
    }

    View BuildFontRow(FontChoice f)
    {
        var familyPicker = new Picker { TextColor = Color.FromArgb("#E0E0E0"), BackgroundColor = Color.FromArgb("#1E1E1E"), MinimumWidthRequest = 150 };
        foreach (var fam in Families) familyPicker.Items.Add(fam.Label);
        int famIdx = Array.FindIndex(Families, x => string.Equals(x.Value, f.Family, StringComparison.OrdinalIgnoreCase));
        familyPicker.SelectedIndex = famIdx >= 0 ? famIdx : 0;

        var sizePicker = new Picker { TextColor = Color.FromArgb("#E0E0E0"), BackgroundColor = Color.FromArgb("#1E1E1E"), MinimumWidthRequest = 70 };
        foreach (var s in FontSizes) sizePicker.Items.Add(s.ToString());
        int nearest = FontSizes.OrderBy(s => Math.Abs(s - (int)Math.Round(f.Size))).First();
        sizePicker.SelectedIndex = Array.IndexOf(FontSizes, nearest);

        void ApplyNow()
        {
            string fam = familyPicker.SelectedIndex >= 0 ? Families[familyPicker.SelectedIndex].Value : f.Family;
            double size = sizePicker.SelectedIndex >= 0 ? FontSizes[sizePicker.SelectedIndex] : f.Size;
            f.Apply(fam, size);
        }
        familyPicker.SelectedIndexChanged += (_, _) => ApplyNow();
        sizePicker.SelectedIndexChanged += (_, _) => ApplyNow();

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
            Margin = new Thickness(0, 4, 0, 4),
        };
        var name = new Label { Text = f.Name, TextColor = Color.FromArgb("#E0E0E0"), VerticalOptions = LayoutOptions.Center };
        row.Add(name); Grid.SetColumn(name, 0);
        row.Add(familyPicker); Grid.SetColumn(familyPicker, 1);
        row.Add(sizePicker); Grid.SetColumn(sizePicker, 2);
        return row;
    }

    View BuildUiTextRow()
    {
        var sizePicker = new Picker { TextColor = Color.FromArgb("#E0E0E0"), BackgroundColor = Color.FromArgb("#1E1E1E"), MinimumWidthRequest = 70 };
        foreach (var s in FontSizes) sizePicker.Items.Add(s.ToString());
        int nearest = FontSizes.OrderBy(s => Math.Abs(s - (int)Math.Round(AppSettings.UiTextSize))).First();
        sizePicker.SelectedIndex = Array.IndexOf(FontSizes, nearest);
        sizePicker.SelectedIndexChanged += (_, _) =>
        {
            if (sizePicker.SelectedIndex < 0) return;
            AppSettings.UiTextSize = FontSizes[sizePicker.SelectedIndex];
            App.ApplyUiTextSize();   // resize buttons/labels app-wide, live
        };

        var row = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 4),
        };
        var name = new Label { Text = "Buttons & labels", TextColor = Color.FromArgb("#E0E0E0"), VerticalOptions = LayoutOptions.Center };
        row.Add(name); Grid.SetColumn(name, 0);
        row.Add(sizePicker); Grid.SetColumn(sizePicker, 1);
        return row;
    }

    static string Hex(Color c) => $"#{(int)(c.Red * 255):X2}{(int)(c.Green * 255):X2}{(int)(c.Blue * 255):X2}";

    static Color? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return Color.FromArgb(hex.StartsWith("#") ? hex : "#" + hex); } catch { return null; }
    }
}
