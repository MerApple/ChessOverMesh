using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>
/// Noise settings: a per-hardware-type calibration offset (dBm) that is added to the noise floor reported by
/// nodes of that hardware. Different radios read their ambient RF noise differently, so this lets the user
/// nudge each hardware type up or down. Each row is one hardware model with a signed integer offset; the
/// default is 0 (no adjustment) and only non-zero offsets are kept. A filter box narrows the (long) list.
/// The edited offsets are exposed via <see cref="Calibrations"/> when the dialog is accepted.
/// </summary>
internal sealed class NoiseSettingsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    // Accepts an optional leading sign and digits only (validated as-you-type; empty is allowed = 0).
    private static readonly Regex SignedInt = new(@"^-?\d*$", RegexOptions.Compiled);

    private readonly List<(string Hardware, FrameworkElement Row, TextBox Box)> _rows = new();

    /// <summary>The edited offsets, keyed by hardware model display name. Only non-zero offsets are included.
    /// Valid once the dialog closes with OK (DialogResult == true).</summary>
    public IReadOnlyDictionary<string, int> Calibrations { get; private set; } = new Dictionary<string, int>();

    /// <param name="hardwareTypes">Hardware model display names to list (e.g. every known model, plus any node
    /// currently seen and any already-configured type). Deduplicated and sorted here.</param>
    /// <param name="current">The offsets already saved, keyed by hardware type.</param>
    public NoiseSettingsWindow(Window owner, IEnumerable<string> hardwareTypes, IReadOnlyDictionary<string, int> current)
    {
        Title = "Noise settings";
        Owner = owner;
        Width = 400;
        Height = 560;
        MinWidth = 320;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Bg;

        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // intro
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // filter
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // list
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons

        var intro = new TextBlock
        {
            Text = "Calibration added to the reported noise floor (dBm) per hardware type. Positive or negative; " +
                   "default 0 = no adjustment.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetRow(intro, 0);
        grid.Children.Add(intro);

        var filter = new TextBox
        {
            Background = FieldBg, Foreground = Fg, BorderBrush = Dim, Padding = new Thickness(4, 3, 4, 3),
            Margin = new Thickness(0, 0, 0, 8), ToolTip = "Type to filter the hardware list",
        };
        Grid.SetRow(filter, 1);
        grid.Children.Add(filter);

        // One editable row per hardware type, inside a scroll viewer (the list is long).
        var list = new StackPanel();
        foreach (var hw in hardwareTypes.Where(h => !string.IsNullOrWhiteSpace(h))
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = hw, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            int val = current.TryGetValue(hw, out var v) ? v : 0;
            var box = new TextBox
            {
                Width = 64,
                Text = val.ToString(CultureInfo.InvariantCulture),
                Background = FieldBg, Foreground = Fg, BorderBrush = Dim,
                HorizontalContentAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(4, 2, 4, 2),
                ToolTip = "Offset in dBm (whole number, may be negative)",
            };
            box.PreviewTextInput += (s, e) =>
            {
                var tb = (TextBox)s;
                string proposed = tb.Text.Remove(tb.SelectionStart, tb.SelectionLength)
                                         .Insert(tb.SelectionStart, e.Text);
                e.Handled = !SignedInt.IsMatch(proposed);
            };
            DataObject.AddPastingHandler(box, (s, e) =>
            {
                var text = e.DataObject.GetData(DataFormats.Text) as string;
                if (text == null || !SignedInt.IsMatch(text.Trim())) e.CancelCommand();
            });
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            list.Children.Add(row);
            _rows.Add((hw, row, box));
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);

        filter.TextChanged += (_, _) =>
        {
            string q = filter.Text.Trim();
            foreach (var (hw, rowEl, _) in _rows)
                rowEl.Visibility = q.Length == 0 || hw.Contains(q, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var ok = new Button { Content = "OK", MinWidth = 80, MinHeight = 26, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, MinHeight = 26, IsCancel = true };
        ok.Click += (_, _) =>
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (hw, _, box) in _rows)
                if (int.TryParse(box.Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var val) && val != 0)
                    map[hw] = val;
            Calibrations = map;
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);

        Content = grid;
    }
}
