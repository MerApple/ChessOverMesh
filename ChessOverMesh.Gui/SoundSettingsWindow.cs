using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>
/// Modal sound settings: pick the notification sound and volume for received chess moves and for received
/// chat messages independently, with a Test button to preview each. "(none)" turns that sound off.
/// </summary>
internal sealed class SoundSettingsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));

    private readonly ComboBox _chessCombo = new() { MinWidth = 200, MinHeight = 24 };
    private readonly ComboBox _chatCombo = new() { MinWidth = 200, MinHeight = 24 };
    private readonly Slider _chessVol = new() { MinWidth = 160, Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _chatVol = new() { MinWidth = 160, Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly MediaPlayer _preview = new();

    public string ChessSoundPath { get; private set; }
    public string ChatSoundPath { get; private set; }
    public int ChessVolume { get; private set; }
    public int ChatVolume { get; private set; }

    public SoundSettingsWindow(Window owner, string chessPath, int chessVolume, string chatPath, int chatVolume)
    {
        ChessSoundPath = chessPath; ChessVolume = chessVolume;
        ChatSoundPath = chatPath; ChatVolume = chatVolume;

        Title = "Sound settings";
        Owner = owner;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        var sounds = SoundLibrary.Available();
        foreach (var combo in new[] { _chessCombo, _chatCombo })
        {
            combo.ItemsSource = sounds;
            combo.DisplayMemberPath = nameof(SoundLibrary.Sound.Name);
        }
        _chessCombo.SelectedItem = sounds.FirstOrDefault(s => s.Path == chessPath) ?? SoundLibrary.None;
        _chatCombo.SelectedItem = sounds.FirstOrDefault(s => s.Path == chatPath) ?? SoundLibrary.None;
        _chessVol.Value = Math.Clamp(chessVolume, 0, 100);
        _chatVol.Value = Math.Clamp(chatVolume, 0, 100);

        Content = BuildLayout();
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(12) };

        root.Children.Add(Section("Chess move sound", _chessCombo, _chessVol, () => Preview(_chessCombo, _chessVol)));
        root.Children.Add(Section("Chat message sound", _chatCombo, _chatVol, () => Preview(_chatCombo, _chatVol)));

        var done = new Button { Content = "Done", MinWidth = 80, MinHeight = 26, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        done.Click += Done_Click;
        root.Children.Add(done);
        return root;
    }

    private UIElement Section(string title, ComboBox combo, Slider volume, Action test)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(new TextBlock { Text = title, Foreground = Fg, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 4) });

        var row = new WrapPanel();
        row.Children.Add(new TextBlock { Text = "Sound:", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        row.Children.Add(combo);
        var testBtn = new Button { Content = "Test", MinWidth = 56, MinHeight = 24, Margin = new Thickness(8, 0, 0, 0) };
        testBtn.Click += (_, _) => test();
        row.Children.Add(testBtn);
        panel.Children.Add(row);

        var volRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        volRow.Children.Add(new TextBlock { Text = "Volume:", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        volRow.Children.Add(volume);
        var pct = new TextBlock { Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        pct.Text = $"{(int)volume.Value}%";
        volume.ValueChanged += (_, _) => pct.Text = $"{(int)volume.Value}%";
        volRow.Children.Add(pct);
        panel.Children.Add(volRow);

        return panel;
    }

    private void Preview(ComboBox combo, Slider volume)
    {
        if (combo.SelectedItem is not SoundLibrary.Sound s || s.Path.Length == 0) return;
        try
        {
            _preview.Open(new Uri(s.Path));
            _preview.Volume = Math.Clamp(volume.Value, 0, 100) / 100.0;
            _preview.Position = TimeSpan.Zero;
            _preview.Play();
        }
        catch { /* missing/unplayable file — ignore */ }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        ChessSoundPath = (_chessCombo.SelectedItem as SoundLibrary.Sound)?.Path ?? "";
        ChatSoundPath = (_chatCombo.SelectedItem as SoundLibrary.Sound)?.Path ?? "";
        ChessVolume = (int)_chessVol.Value;
        ChatVolume = (int)_chatVol.Value;
        DialogResult = true;
    }
}
