using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChessOverMesh.Mesh;

namespace ChessOverMesh.Gui;

/// <summary>Chat message settings: how many chat messages to keep per channel, plus a per-channel auto-delete
/// age. Both apply to the on-screen list and the disk cache. Applied immediately; the window only has Close.</summary>
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
        var limitBox = new TextBox { Width = 70, Margin = new Thickness(8, 0, 0, 0), Text = AppSettings.ChatMessageLimit.ToString() };
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
                row.Children.Add(new TextBlock { Text = channel.DisplayName, Foreground = Fg, Width = 150, VerticalAlignment = VerticalAlignment.Center });
                var valBox = new TextBox { Width = 50, VerticalAlignment = VerticalAlignment.Center };
                var unit = new ComboBox { Width = 74, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                unit.Items.Add("Days");
                unit.Items.Add("Hours");
                int hrs = retention.TryGetValue(channel.Index, out var h) ? h : 0;
                if (hrs > 0 && hrs % 24 == 0) { valBox.Text = (hrs / 24).ToString(); unit.SelectedIndex = 0; }
                else if (hrs > 0) { valBox.Text = hrs.ToString(); unit.SelectedIndex = 1; }
                else { valBox.Text = ""; unit.SelectedIndex = 0; }

                void Apply()
                {
                    int hours = 0;
                    if (int.TryParse(valBox.Text, out var v) && v > 0) hours = v * (unit.SelectedIndex == 1 ? 1 : 24);
                    DeviceCache.SetChannelRetention(host, channel.Index, hours);
                    main?.ApplyChatRetention();
                }
                valBox.LostFocus += (_, _) => Apply();
                unit.SelectionChanged += (_, _) => Apply();
                row.Children.Add(valBox);
                row.Children.Add(unit);
                root.Children.Add(row);
            }
        }

        var closeBtn = new Button
        {
            Content = "Close", Width = 80, Height = 26, IsDefault = true, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
        };
        closeBtn.Click += (_, _) => Close();
        root.Children.Add(closeBtn);

        Content = root;
    }
}
