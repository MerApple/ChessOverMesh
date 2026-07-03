using ChessOverMesh.Game;
using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>Chat message settings: how many chat messages to keep per channel, a per-channel receiver auto-delete
/// age, and a per-channel sender self-destruct that stamps outgoing messages to auto-delete on every recipient.
/// Changes persist immediately.</summary>
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
                var valEntry = new Entry { Keyboard = Keyboard.Numeric, TextColor = Fg, MinimumWidthRequest = 64 };
                var unit = new Picker { TextColor = Fg, BackgroundColor = Bg, MinimumWidthRequest = 110 };
                unit.Items.Add("Minutes");
                unit.Items.Add("Hours");
                unit.Items.Add("Days");
                int mins = retention.TryGetValue(channel.Index, out var mv) ? mv : 0;
                if (mins > 0 && mins % 1440 == 0) { valEntry.Text = (mins / 1440).ToString(); unit.SelectedIndex = 2; }
                else if (mins > 0 && mins % 60 == 0) { valEntry.Text = (mins / 60).ToString(); unit.SelectedIndex = 1; }
                else if (mins > 0) { valEntry.Text = mins.ToString(); unit.SelectedIndex = 0; }
                else { valEntry.Text = ""; unit.SelectedIndex = 1; }   // default unit: Hours

                void Apply()
                {
                    int minutes = 0;
                    if (int.TryParse(valEntry.Text, out var v) && v > 0)
                        minutes = v * (unit.SelectedIndex == 2 ? 1440 : unit.SelectedIndex == 1 ? 60 : 1);
                    DeviceCache.SetChannelRetention(host, channel.Index, minutes);
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

        // ---- Per-channel sender self-destruct (rides in the message; every receiver honours it) ----
        root.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#3F3F46"), Margin = new Thickness(0, 8, 0, 4) });
        root.Add(new Label { Text = "Self-destruct sent messages", TextColor = Fg, FontAttributes = FontAttributes.Bold });
        root.Add(new Label
        {
            Text = "When you send on a channel, stamp your message to auto-delete after this — on every recipient " +
                   "and here — with a live countdown shown under it. Cooperative: a recipient not running this app " +
                   "keeps the message. Blank or 0 = off.",
            TextColor = Dim, FontSize = 11,
        });

        if (channels.Count == 0 || host.Length == 0)
        {
            root.Add(new Label { Text = "Connect to a device to set per-channel self-destruct.", TextColor = Dim, FontSize = 12 });
        }
        else
        {
            var sendTtl = DeviceCache.GetChannelSendTtl(host);
            foreach (var ch in channels)
            {
                var channel = ch;   // capture for the closures
                var valEntry = new Entry { Keyboard = Keyboard.Numeric, TextColor = Fg, MinimumWidthRequest = 64 };
                var unit = new Picker { TextColor = Fg, BackgroundColor = Bg, MinimumWidthRequest = 110 };
                unit.Items.Add("Minutes");
                unit.Items.Add("Hours");
                unit.Items.Add("Days");
                int mins = sendTtl.TryGetValue(channel.Index, out var mm) ? mm : 0;
                if (mins > 0 && mins % 1440 == 0) { valEntry.Text = (mins / 1440).ToString(); unit.SelectedIndex = 2; }
                else if (mins > 0 && mins % 60 == 0) { valEntry.Text = (mins / 60).ToString(); unit.SelectedIndex = 1; }
                else if (mins > 0) { valEntry.Text = mins.ToString(); unit.SelectedIndex = 0; }
                else { valEntry.Text = ""; unit.SelectedIndex = 0; }   // default unit: Minutes

                void Apply()
                {
                    int minutes = 0;
                    if (int.TryParse(valEntry.Text, out var v) && v > 0)
                        minutes = v * (unit.SelectedIndex == 2 ? 1440 : unit.SelectedIndex == 1 ? 60 : 1);
                    DeviceCache.SetChannelSendTtl(host, channel.Index, minutes);
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

        // ---- Per-channel split long messages ----
        root.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#3F3F46"), Margin = new Thickness(0, 8, 0, 4) });
        root.Add(new Label { Text = "Split long messages", TextColor = Fg, FontAttributes = FontAttributes.Bold });
        root.Add(new Label
        {
            Text = $"When on, a message too long for one packet is split into a sequence (up to {ProtocolMessage.MaxChatChunks} parts) " +
                   "and reassembled by the receiver. The sequence markers travel unencrypted; the message text stays encrypted " +
                   "if the channel has a key. Cooperative: a recipient not running this app sees the separate parts. Off = long " +
                   "messages are refused instead.",
            TextColor = Dim, FontSize = 11,
        });

        if (channels.Count == 0 || host.Length == 0)
        {
            root.Add(new Label { Text = "Connect to a device to set per-channel message splitting.", TextColor = Dim, FontSize = 12 });
        }
        else
        {
            var splitOn = DeviceCache.GetChannelSplitOn(host);
            foreach (var ch in channels)
            {
                var channel = ch;   // capture for the closure
                var sw = new Switch { IsToggled = splitOn.Contains(channel.Index), VerticalOptions = LayoutOptions.Center };
                sw.Toggled += (_, e) => DeviceCache.SetChannelSplit(host, channel.Index, e.Value);
                var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2),
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
                row.Add(new Label { Text = channel.DisplayName, TextColor = Fg, VerticalOptions = LayoutOptions.Center }, 0, 0);
                row.Add(sw, 1, 0);
                root.Add(row);
            }
        }

        // ---- Per-channel: add sequence headers when splitting ----
        root.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#3F3F46"), Margin = new Thickness(0, 8, 0, 4) });
        root.Add(new Label { Text = "Add sequence headers", TextColor = Fg, FontAttributes = FontAttributes.Bold });
        root.Add(new Label
        {
            Text = "On (default): parts carry sequence markers so the receiver reassembles them into one message, sent " +
                   "one-at-a-time waiting for each acknowledgement. Off: the message is split into separate independent " +
                   "messages (no reassembly) — only possible on a channel with no app key (encrypted splits always need " +
                   "headers, so those channels stay on and locked).",
            TextColor = Dim, FontSize = 11,
        });

        if (channels.Count == 0 || host.Length == 0)
        {
            root.Add(new Label { Text = "Connect to a device to set per-channel headers.", TextColor = Dim, FontSize = 12 });
        }
        else
        {
            foreach (var ch in channels)
            {
                var channel = ch;   // capture for the closure
                bool hasKey = DeviceCache.GetChannelKey(host, channel.Index).Length > 0;
                var sw = new Switch
                {
                    IsToggled = hasKey || !DeviceCache.IsSplitHeadersOff(host, channel.Index),
                    IsEnabled = !hasKey,   // a keyed channel forces (and locks) headers on
                    VerticalOptions = LayoutOptions.Center,
                };
                sw.Toggled += (_, e) => DeviceCache.SetSplitHeaders(host, channel.Index, e.Value);
                var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2),
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
                row.Add(new Label { Text = hasKey ? $"{channel.DisplayName}  (required — encrypted)" : channel.DisplayName,
                    TextColor = Fg, VerticalOptions = LayoutOptions.Center }, 0, 0);
                row.Add(sw, 1, 0);
                root.Add(row);
            }
        }

        var closeBtn = new Button { Text = "Close", MinimumHeightRequest = 44, Margin = new Thickness(0, 14, 0, 0) };
        closeBtn.Clicked += async (_, _) => await Navigation.PopModalAsync();
        root.Add(closeBtn);

        Content = new ScrollView { Content = root };
    }
}
