using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChessOverMesh.Gui;

/// <summary>
/// Small hub dialog that groups the app's settings sections behind one "Settings…" toolbar button.
/// Each row opens the corresponding section (Device / Color / Sound) via a callback into the main window,
/// shown modally on top of this dialog. Device configuration needs a connected radio, so its row is
/// enabled only when <paramref name="deviceEnabled"/> is true (computed by the caller at open time).
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));

    public SettingsWindow(Window owner, bool deviceEnabled,
                          Action openDevice, Action openColor, Action openSound, Action openChess, Action openConnection,
                          Action openSystemSettings, Action openChatSettings)
    {
        Title = "Settings";
        Owner = owner;
        Width = 280;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        var root = new StackPanel { Margin = new Thickness(14) };

        root.Children.Add(new TextBlock
        {
            Text = "Choose a settings section:",
            Foreground = Dim,
            Margin = new Thickness(0, 0, 0, 10),
        });

        // A full-width section button that runs its action when clicked (the section opens modally over this).
        Button Section(string content, string tip, Action action, bool enabled)
        {
            var b = new Button
            {
                Content = content,
                MinHeight = 30,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 0, 0, 0),
                IsEnabled = enabled,
                ToolTip = tip,
            };
            b.Click += (_, _) => action();
            return b;
        }

        root.Children.Add(Section("Device settings",
            deviceEnabled
                ? "Configure the device: owner/role, LoRa radio, position, telemetry, and reboot/reset"
                : "Connect to a device first to configure it.",
            openDevice, deviceEnabled));
        root.Children.Add(Section("Color/Fonts",
            "Choose colors and fonts for message types (received, awaiting ack, delivered, failed)",
            openColor, true));
        root.Children.Add(Section("Sound…",
            "Choose the notification sound and volume for moves and chat",
            openSound, true));
        root.Children.Add(Section("Chess settings",
            "Chess board options: whether the chessboard is shown, and the rainbow move effect",
            openChess, true));
        root.Children.Add(Section("Connection settings",
            "Keep-alive heartbeat and auto-reconnect for the device connection",
            openConnection, true));
        root.Children.Add(Section("Chat messages",
            "How many chat messages to keep per channel (on screen and in the saved history)",
            openChatSettings, true));
        root.Children.Add(Section("System settings",
            "Low-level app behaviour, such as whether chat messages are cached on this computer",
            openSystemSettings, true));

        var closeBtn = new Button
        {
            Content = "Close",
            MinWidth = 80,
            MinHeight = 26,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        closeBtn.Click += (_, _) => Close();
        root.Children.Add(closeBtn);

        Content = root;
    }
}
