using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChessOverMesh.Game;
using ChessOverMesh.Mesh;

namespace ChessOverMesh.Gui;

/// <summary>Chat message settings: how many chat messages to keep per channel, a per-channel receiver auto-delete
/// age, and a per-channel sender self-destruct that stamps outgoing messages to auto-delete on every recipient.
/// All apply immediately; the window only has Close.</summary>
internal sealed class ChatSettingsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));

    public ChatSettingsWindow(Window owner)
    {
        Title = "Chat messages";
        Owner = owner;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;
        var main = owner as MainWindow;

        var root = new StackPanel { Margin = new Thickness(14) };

        // ---- Count limit (global, all channels) ----
        root.Children.Add(new TextBlock
        {
            Text = "How many chat messages to keep, per channel.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        var limitRow = new StackPanel { Orientation = Orientation.Horizontal };
        limitRow.Children.Add(new TextBlock { Text = "Messages per channel:", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center });
        var limitBox = new TextBox { MinWidth = 70, Margin = new Thickness(8, 0, 0, 0), Text = AppSettings.ChatMessageLimit.ToString() };
        limitBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(limitBox.Text, out var n) && n > 0)
            {
                AppSettings.ChatMessageLimit = n;
                main?.ApplyChatMessageLimit();
            }
            limitBox.Text = AppSettings.ChatMessageLimit.ToString();
        };
        limitRow.Children.Add(limitBox);
        root.Children.Add(limitRow);
        root.Children.Add(new TextBlock
        {
            Text = "The most recent messages on each channel are kept — on screen and in the saved history. " +
                   "Older messages are dropped past this.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 14),
        });

        // ---- Per-channel auto-delete age (per device) ----
        root.Children.Add(new TextBlock
        {
            Text = "Auto-delete old messages", Foreground = Fg, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2),
        });
        root.Children.Add(new TextBlock
        {
            Text = "Automatically delete messages older than this, per channel (on screen and in the cache). " +
                   "Blank or 0 = keep forever.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        string host = main?.CurrentHost ?? "";
        var channels = main?.GetAvailableChannels() ?? (IReadOnlyList<MeshChannel>)Array.Empty<MeshChannel>();
        var retention = host.Length > 0 ? DeviceCache.GetChannelRetention(host) : new Dictionary<uint, int>();

        if (channels.Count == 0 || host.Length == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Connect to a device to set per-channel auto-delete.",
                Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            });
        }
        else
        {
            foreach (var ch in channels)
            {
                var channel = ch;   // capture for the closures
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                row.Children.Add(new TextBlock { Text = channel.DisplayName, Foreground = Fg, MinWidth = 150, VerticalAlignment = VerticalAlignment.Center });
                var valBox = new TextBox { MinWidth = 50, VerticalAlignment = VerticalAlignment.Center };
                var unit = new ComboBox { MinWidth = 88, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                unit.Items.Add("Minutes");
                unit.Items.Add("Hours");
                unit.Items.Add("Days");
                int mins = retention.TryGetValue(channel.Index, out var mv) ? mv : 0;
                if (mins > 0 && mins % 1440 == 0) { valBox.Text = (mins / 1440).ToString(); unit.SelectedIndex = 2; }
                else if (mins > 0 && mins % 60 == 0) { valBox.Text = (mins / 60).ToString(); unit.SelectedIndex = 1; }
                else if (mins > 0) { valBox.Text = mins.ToString(); unit.SelectedIndex = 0; }
                else { valBox.Text = ""; unit.SelectedIndex = 1; }   // default unit: Hours

                void Apply()
                {
                    int minutes = 0;
                    if (int.TryParse(valBox.Text, out var v) && v > 0)
                        minutes = v * (unit.SelectedIndex == 2 ? 1440 : unit.SelectedIndex == 1 ? 60 : 1);
                    DeviceCache.SetChannelRetention(host, channel.Index, minutes);
                    main?.ApplyChatRetention();
                }
                valBox.LostFocus += (_, _) => Apply();
                unit.SelectionChanged += (_, _) => Apply();
                row.Children.Add(valBox);
                row.Children.Add(unit);
                root.Children.Add(row);
            }
        }

        // ---- Per-channel sender self-destruct (rides in the message; every receiver honours it) ----
        root.Children.Add(new TextBlock
        {
            Text = "Self-destruct sent messages", Foreground = Fg, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 2),
        });
        root.Children.Add(new TextBlock
        {
            Text = "When you send on a channel, stamp your message to auto-delete after this — on every recipient " +
                   "and here — with a live countdown shown under it. Cooperative: a recipient not running this app " +
                   "keeps the message. Blank or 0 = off.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        if (channels.Count == 0 || host.Length == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Connect to a device to set per-channel self-destruct.",
                Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            });
        }
        else
        {
            var sendTtl = DeviceCache.GetChannelSendTtl(host);
            foreach (var ch in channels)
            {
                var channel = ch;   // capture for the closures
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                row.Children.Add(new TextBlock { Text = channel.DisplayName, Foreground = Fg, MinWidth = 150, VerticalAlignment = VerticalAlignment.Center });
                var valBox = new TextBox { MinWidth = 50, VerticalAlignment = VerticalAlignment.Center };
                var unit = new ComboBox { MinWidth = 88, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                unit.Items.Add("Minutes");
                unit.Items.Add("Hours");
                unit.Items.Add("Days");
                int mins = sendTtl.TryGetValue(channel.Index, out var mm) ? mm : 0;
                if (mins > 0 && mins % 1440 == 0) { valBox.Text = (mins / 1440).ToString(); unit.SelectedIndex = 2; }
                else if (mins > 0 && mins % 60 == 0) { valBox.Text = (mins / 60).ToString(); unit.SelectedIndex = 1; }
                else if (mins > 0) { valBox.Text = mins.ToString(); unit.SelectedIndex = 0; }
                else { valBox.Text = ""; unit.SelectedIndex = 0; }   // default unit: Minutes

                void Apply()
                {
                    int minutes = 0;
                    if (int.TryParse(valBox.Text, out var v) && v > 0)
                        minutes = v * (unit.SelectedIndex == 2 ? 1440 : unit.SelectedIndex == 1 ? 60 : 1);
                    DeviceCache.SetChannelSendTtl(host, channel.Index, minutes);
                }
                valBox.LostFocus += (_, _) => Apply();
                unit.SelectionChanged += (_, _) => Apply();
                row.Children.Add(valBox);
                row.Children.Add(unit);
                root.Children.Add(row);
            }
        }

        // ---- Per-channel split long messages ----
        root.Children.Add(new TextBlock
        {
            Text = "Split long messages", Foreground = Fg, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 2),
        });
        root.Children.Add(new TextBlock
        {
            Text = $"When on, a message too long for one packet is split into a sequence (up to {ProtocolMessage.MaxChatChunks} parts) " +
                   "and reassembled by the receiver. The sequence markers travel unencrypted; the message text stays encrypted " +
                   "if the channel has a key. Cooperative: a recipient not running this app sees the separate parts. Off = long " +
                   "messages are refused instead.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        if (channels.Count == 0 || host.Length == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Connect to a device to set per-channel message splitting.",
                Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            });
        }
        else
        {
            var splitOn = DeviceCache.GetChannelSplitOn(host);
            foreach (var ch in channels)
            {
                var channel = ch;   // capture for the closure
                var cb = new CheckBox
                {
                    Content = channel.DisplayName,
                    Foreground = Fg,
                    IsChecked = splitOn.Contains(channel.Index),
                    Margin = new Thickness(0, 0, 0, 4),
                };
                cb.Checked += (_, _) => DeviceCache.SetChannelSplit(host, channel.Index, true);
                cb.Unchecked += (_, _) => DeviceCache.SetChannelSplit(host, channel.Index, false);
                root.Children.Add(cb);
            }
        }

        // ---- Per-channel: add sequence headers when splitting ----
        root.Children.Add(new TextBlock
        {
            Text = "Add sequence headers", Foreground = Fg, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 2),
        });
        root.Children.Add(new TextBlock
        {
            Text = "On (default): the parts carry sequence markers so the receiver reassembles them into one message, " +
                   "and each part is sent one-at-a-time waiting for its acknowledgement. Off: the message is split into " +
                   "separate independent messages (no reassembly) — only possible on a channel with no app key, since an " +
                   "encrypted split always needs headers (those channels stay ticked and locked).",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        if (channels.Count == 0 || host.Length == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Connect to a device to set per-channel headers.",
                Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            });
        }
        else
        {
            foreach (var ch in channels)
            {
                var channel = ch;   // capture for the closure
                bool hasKey = DeviceCache.GetChannelKey(host, channel.Index).Length > 0;
                var cb = new CheckBox
                {
                    Content = hasKey ? $"{channel.DisplayName}  (required — encrypted)" : channel.DisplayName,
                    Foreground = Fg,
                    // Headers on unless the user turned them off; a keyed channel forces (and locks) them on.
                    IsChecked = hasKey || !DeviceCache.IsSplitHeadersOff(host, channel.Index),
                    IsEnabled = !hasKey,
                    Margin = new Thickness(0, 0, 0, 4),
                };
                cb.Checked += (_, _) => DeviceCache.SetSplitHeaders(host, channel.Index, true);
                cb.Unchecked += (_, _) => DeviceCache.SetSplitHeaders(host, channel.Index, false);
                root.Children.Add(cb);
            }
        }

        var closeBtn = new Button
        {
            Content = "Close", MinWidth = 80, MinHeight = 26, IsDefault = true, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
        };
        closeBtn.Click += (_, _) => Close();
        root.Children.Add(closeBtn);

        Content = root;
    }
}
