using System.Windows;
using System.Windows.Controls;

namespace ChessOverMesh.Gui;

public partial class App : Application
{
    public App()
    {
        // Apply the user's app-wide UI text size to every window as it loads. FontSize is inherited, so buttons,
        // checkboxes, text boxes and settings labels that don't set their own size pick this up — while the four
        // content lists (moves/system/chat/nodes) set their own FontSize, so they keep their independent sizes.
        // One class handler covers every window, including the settings dialogs and any added later.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w) w.FontSize = AppSettings.UiTextSize; }));
    }

    /// <summary>Re-applies the current UI text size to every open window — call after the setting changes so it
    /// takes effect live without reopening.</summary>
    public static void ApplyUiTextSize()
    {
        double size = AppSettings.UiTextSize;
        foreach (Window w in Current.Windows) w.FontSize = size;
    }
}
