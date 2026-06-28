using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>One editable message-type colour: the live brush it recolours, its built-in default, and how
/// to persist a chosen colour.</summary>
internal sealed record ColorChoice(string Name, SolidColorBrush Brush, Color Default, Action<Color> Persist);

/// <summary>One editable list font: the current family/size and a callback that applies + persists a change.</summary>
internal sealed record FontChoice(string Name, string Family, double Size, Action<string, double> Apply);

/// <summary>
/// Modal colour + font picker. Editing a colour updates the shared brush in place, so existing list lines
/// using it recolour immediately; editing a font calls back to apply it to the matching list live.
/// </summary>
internal sealed class ColorSettingsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Edge = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5A));

    private static readonly int[] Sizes = { 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28 };
    private readonly List<string> _families =
        Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    public ColorSettingsWindow(Window owner, IReadOnlyList<ColorChoice> choices, IReadOnlyList<FontChoice> fonts)
    {
        Title = "Colors & fonts";
        Owner = owner;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(Header("Message colors"));
        root.Children.Add(new TextBlock
        {
            Text = "Pick a color for each message type. Changes apply immediately.",
            Foreground = Fg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (var ch in choices) root.Children.Add(ColorRow(ch));

        root.Children.Add(new Border { Height = 1, Background = Edge, Margin = new Thickness(0, 12, 0, 0) });
        root.Children.Add(Header("Text font & size"));
        root.Children.Add(new TextBlock
        {
            Text = "Font for each list. Changes apply immediately.",
            Foreground = Fg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (var f in fonts) root.Children.Add(FontRow(f));

        var done = new Button { Content = "Done", Width = 80, Height = 26, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        done.Click += (_, _) => { DialogResult = true; };
        root.Children.Add(done);

        Content = root;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Fg, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 4),
    };

    private UIElement ColorRow(ColorChoice ch)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

        var swatch = new Border { Width = 30, Height = 20, Background = ch.Brush, BorderBrush = Edge, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(swatch, Dock.Left);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var choose = new Button { Content = "Choose…", Width = 72, Height = 24 };
        var reset = new Button { Content = "Reset", Width = 60, Height = 24, Margin = new Thickness(6, 0, 0, 0) };
        choose.Click += (_, _) => Pick(ch);
        reset.Click += (_, _) => { ch.Brush.Color = ch.Default; ch.Persist(ch.Default); };
        buttons.Children.Add(choose);
        buttons.Children.Add(reset);
        DockPanel.SetDock(buttons, Dock.Right);

        var name = new TextBlock { Text = ch.Name, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(swatch);
        row.Children.Add(buttons);
        row.Children.Add(name);
        return row;
    }

    private UIElement FontRow(FontChoice f)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

        var name = new TextBlock { Text = f.Name, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Width = 120 };
        DockPanel.SetDock(name, Dock.Left);

        // Make sure the current family is selectable even if it isn't in the enumerated system list.
        if (!_families.Contains(f.Family, StringComparer.OrdinalIgnoreCase)) _families.Insert(0, f.Family);
        string currentFamily = _families.First(x => string.Equals(x, f.Family, StringComparison.OrdinalIgnoreCase));

        var familyCombo = new ComboBox { Width = 170, Height = 24, ItemsSource = _families, SelectedItem = currentFamily };
        var sizeCombo = new ComboBox { Width = 56, Height = 24, Margin = new Thickness(8, 0, 0, 0), ItemsSource = Sizes, SelectedItem = NearestSize(f.Size) };

        void ApplyNow()
        {
            string fam = familyCombo.SelectedItem as string ?? f.Family;
            double size = sizeCombo.SelectedItem is int s ? s : f.Size;
            f.Apply(fam, size);
        }
        familyCombo.SelectionChanged += (_, _) => ApplyNow();
        sizeCombo.SelectionChanged += (_, _) => ApplyNow();

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        controls.Children.Add(familyCombo);
        controls.Children.Add(sizeCombo);
        DockPanel.SetDock(controls, Dock.Right);

        row.Children.Add(name);
        row.Children.Add(controls);
        return row;
    }

    private static int NearestSize(double size)
    {
        int target = (int)Math.Round(size);
        return Sizes.OrderBy(s => Math.Abs(s - target)).First();
    }

    private static void Pick(ColorChoice ch)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(ch.Brush.Color.R, ch.Brush.Color.G, ch.Brush.Color.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var c = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
        ch.Brush.Color = c;
        ch.Persist(c);
    }
}
