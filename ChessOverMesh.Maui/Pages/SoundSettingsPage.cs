namespace ChessOverMesh.Maui;

/// <summary>Sound settings (MAUI port of the desktop SoundSettingsWindow): pick the notification sound and
/// volume for received chess moves and chat messages independently, with a Test button. "(none)" = off.</summary>
public sealed class SoundSettingsPage : ContentPage
{
    public sealed record Result(string ChessSound, int ChessVolume, string ChatSound, int ChatVolume);

    readonly TaskCompletionSource<Result> _tcs = new();
    public Task<Result> Completion => _tcs.Task;

    readonly Picker _chessPicker = new() { TextColor = Color.FromArgb("#E0E0E0"), BackgroundColor = Color.FromArgb("#1E1E1E") };
    readonly Picker _chatPicker = new() { TextColor = Color.FromArgb("#E0E0E0"), BackgroundColor = Color.FromArgb("#1E1E1E") };
    readonly Slider _chessVol = new() { Minimum = 0, Maximum = 100 };
    readonly Slider _chatVol = new() { Minimum = 0, Maximum = 100 };
    readonly IReadOnlyList<SoundLibrary.Sound> _sounds = SoundLibrary.Available();

    public SoundSettingsPage(string chessSound, int chessVolume, string chatSound, int chatVolume)
    {
        Title = "Sound settings";
        BackgroundColor = Color.FromArgb("#2D2D30");

        foreach (var picker in new[] { _chessPicker, _chatPicker })
        {
            picker.ItemsSource = _sounds.ToList();
            picker.ItemDisplayBinding = new Binding(nameof(SoundLibrary.Sound.Name));
        }
        _chessPicker.SelectedItem = _sounds.FirstOrDefault(s => s.Asset == chessSound) ?? SoundLibrary.None;
        _chatPicker.SelectedItem = _sounds.FirstOrDefault(s => s.Asset == chatSound) ?? SoundLibrary.None;
        _chessVol.Value = Math.Clamp(chessVolume, 0, 100);
        _chatVol.Value = Math.Clamp(chatVolume, 0, 100);

        var root = new VerticalStackLayout { Padding = 14, Spacing = 6 };
        root.Add(Section("Chess move sound", _chessPicker, _chessVol));
        root.Add(Section("Chat message sound", _chatPicker, _chatVol));

        var done = new Button { Text = "Done", HeightRequest = 40, Margin = new Thickness(0, 14, 0, 0) };
        done.Clicked += OnDone;
        root.Add(done);

        Content = new ScrollView { Content = root };
    }

    View Section(string title, Picker picker, Slider volume)
    {
        var box = new VerticalStackLayout { Spacing = 4, Margin = new Thickness(0, 6, 0, 6) };
        box.Add(new Label { Text = title, TextColor = Color.FromArgb("#E0E0E0"), FontAttributes = FontAttributes.Bold });

        var test = new Button { Text = "Test", HeightRequest = 36, Padding = new Thickness(10, 0), FontSize = 13 };
        test.Clicked += (_, _) =>
        {
            if (picker.SelectedItem is SoundLibrary.Sound s) SoundService.Play(s.Asset, (int)volume.Value);
        };
        var row = new Grid { ColumnDefinitions = Columns("*,Auto"), ColumnSpacing = 8 };
        row.Add(picker); Grid.SetColumn(picker, 0);
        row.Add(test); Grid.SetColumn(test, 1);
        box.Add(row);

        var pct = new Label { Text = $"{(int)volume.Value}%", TextColor = Color.FromArgb("#B0B0B0"), VerticalOptions = LayoutOptions.Center, WidthRequest = 44 };
        volume.ValueChanged += (_, e) => pct.Text = $"{(int)e.NewValue}%";
        var volRow = new Grid { ColumnDefinitions = Columns("Auto,*,Auto"), ColumnSpacing = 8 };
        var lbl = new Label { Text = "Volume", TextColor = Color.FromArgb("#E0E0E0"), VerticalOptions = LayoutOptions.Center };
        volRow.Add(lbl); Grid.SetColumn(lbl, 0);
        volRow.Add(volume); Grid.SetColumn(volume, 1);
        volRow.Add(pct); Grid.SetColumn(pct, 2);
        box.Add(volRow);
        return box;
    }

    static ColumnDefinitionCollection Columns(string spec) => new ColumnDefinitionCollection(
        spec.Split(',').Select(s => new ColumnDefinition(
            s == "*" ? GridLength.Star : s == "Auto" ? GridLength.Auto : new GridLength(double.Parse(s)))).ToArray());

    async void OnDone(object? sender, EventArgs e)
    {
        string chess = (_chessPicker.SelectedItem as SoundLibrary.Sound)?.Asset ?? "";
        string chat = (_chatPicker.SelectedItem as SoundLibrary.Sound)?.Asset ?? "";
        _tcs.TrySetResult(new Result(chess, (int)_chessVol.Value, chat, (int)_chatVol.Value));
        await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        string chess = (_chessPicker.SelectedItem as SoundLibrary.Sound)?.Asset ?? "";
        string chat = (_chatPicker.SelectedItem as SoundLibrary.Sound)?.Asset ?? "";
        _tcs.TrySetResult(new Result(chess, (int)_chessVol.Value, chat, (int)_chatVol.Value));
        return base.OnBackButtonPressed();
    }
}
