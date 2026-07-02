using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>Chat message settings: how many chat messages to keep per channel, plus a per-channel auto-delete
/// age. Both apply to the on-screen list and the disk cache. Changes persist immediately.</summary>
public sealed class ChatSettingsPage : ContentPage
{
    static readonly Color Bg = Color.FromArgb("#1E1E1E");
    static readonly Color Fg = Color.FromArgb("#E0E0E0");
    static readonly Color Dim = Color.FromArgb("#B0B0B0");

    readonly MainPage _main;

    public ChatSettingsPage(MainPage main)
    {
        _main = main;
        Title = "Chat messages";
        BackgroundColor = Bg;

        var root = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        root.Add(new Label { Text = "Chat messages", TextColor = Fg, FontSize = 20, FontAttributes = FontAttributes.Bold });

        // ---- Count limit (global) ----
        root.Add(new Label { Text = "How many chat messages to keep, per channel.", TextColor = Dim, FontSize = 12 });
        var limitEntry = new Entry { Text = AppSettings.ChatMessageLimit.ToString(), Keyboard = Keyboard.Numeric,
            TextColor = Fg, WidthRequest = 90, HorizontalOptions = LayoutOptions.Start };
        void ApplyLimit()
        {
            if (int.TryParse(limitEntry.Text, out var n) && n > 0)
            {
                AppSettings.ChatMessageLimit = n;
                _main.ApplyChatMessageLimit();
            }
            limitEntry.Text = AppSettings.ChatMessageLimit.ToString();
        }
        limitEntry.Completed += (_, _) => ApplyLimit();
        limitEntry.Unfocused += (_, _) => ApplyLimit();
        var limitRow = new HorizontalStackLayout { Spacing = 10 };
        limitRow.Add(new Label { Text = "Messages per channel", TextColor = Fg, VerticalOptions = LayoutOptions.Center });
        limitRow.Add(limitEntry);
        root.Add(limitRow);
        root.Add(new Label
        {
            Text = "The most recent messages on each channel are kept — on screen and in the saved history. " +
                   "Older messages are dropped past this.",
            TextColor = Dim, FontSize = 11,
        });

        // ---- Per-channel auto-delete age (per device) ----
        root.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#3F3F46"), Margin = new Thickness(0, 8, 0, 4) });
        root.Add(new Label { Text = "Auto-delete old messages", TextColor = Fg, FontAttributes = FontAttributes.Bold });
        root.Add(new Label
        {
            Text = "Automatically delete messages older than this, per channel (on screen and in the cache). " +
                   "Blank or 0 = keep forever.",
            TextColor = Dim, FontSize = 11,
        });

        string host = _main.CurrentHost;
        var channels = _main.GetAvailableChannels();
        var retention = host.Length > 0 ? DeviceCache.GetChannelRetention(host) : new Dictionary<uint, int>();

        if (channels.Count == 0 || host.Length == 0)
        {
            root.Add(new Label { Text = "Connect to a device to set per-channel auto-delete.", TextColor = Dim, FontSize = 12 });
        }
        else
        {
            foreach (var ch in channels)
            {
                var channel = ch;   // capture for the closures
                var valEntry = new Entry { Keyboard = Keyboard.Numeric, TextColor = Fg, WidthRequest = 64 };
                var unit = new Picker { TextColor = Fg, BackgroundColor = Bg, WidthRequest = 100 };
                unit.Items.Add("Days");
                unit.Items.Add("Hours");
                int hrs = retention.TryGetValue(channel.Index, out var h) ? h : 0;
                if (hrs > 0 && hrs % 24 == 0) { valEntry.Text = (hrs / 24).ToString(); unit.SelectedIndex = 0; }
                else if (hrs > 0) { valEntry.Text = hrs.ToString(); unit.SelectedIndex = 1; }
                else { valEntry.Text = ""; unit.SelectedIndex = 0; }

                void Apply()
                {
                    int hours = 0;
                    if (int.TryParse(valEntry.Text, out var v) && v > 0) hours = v * (unit.SelectedIndex == 1 ? 1 : 24);
                    DeviceCache.SetChannelRetention(host, channel.Index, hours);
                    _main.ApplyChatRetention();
                }
                valEntry.Completed += (_, _) => Apply();
                valEntry.Unfocused += (_, _) => Apply();
                unit.SelectedIndexChanged += (_, _) => Apply();

                var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2),
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) } };
                row.Add(new Label { Text = channel.DisplayName, TextColor = Fg, VerticalOptions = LayoutOptions.Center }, 0, 0);
                row.Add(valEntry, 1, 0);
                row.Add(unit, 2, 0);
                root.Add(row);
            }
        }

        var closeBtn = new Button { Text = "Close", HeightRequest = 44, Margin = new Thickness(0, 14, 0, 0) };
        closeBtn.Clicked += async (_, _) => await Navigation.PopModalAsync();
        root.Add(closeBtn);

        Content = new ScrollView { Content = root };
    }
}
