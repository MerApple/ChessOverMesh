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

        // Context menus live in their own popup and default to the Windows system-menu font instead of inheriting
        // the window's size, so the UiTextSize applied above never reaches them. Setting FontSize on the ContextMenu
        // alone is not enough: its MenuItems sit in the menu's own popup and keep the system menu font rather than
        // inheriting from the parent, so we set the size on each MenuItem explicitly (recursing into submenus like
        // "React"). Loaded fires each time a menu opens, so this covers every ContextMenu in the app (chat / system /
        // moves / nodes, XAML or code-built) and picks up a changed setting the next time the menu is opened.
        EventManager.RegisterClassHandler(typeof(ContextMenu), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) =>
            {
                if (s is not ContextMenu cm) return;
                double size = AppSettings.UiTextSize;
                cm.FontSize = size;
                ApplyMenuFontSize(cm.Items, size);
            }));
    }

    /// <summary>Sets FontSize on every MenuItem in the collection, recursing into submenus, so items pick up the
    /// app UI text size instead of the default Windows system-menu font.</summary>
    private static void ApplyMenuFontSize(ItemCollection items, double size)
    {
        foreach (object item in items)
        {
            if (item is MenuItem mi)
            {
                mi.FontSize = size;
                ApplyMenuFontSize(mi.Items, size);
            }
        }
    }

    /// <summary>Re-applies the current UI text size to every open window — call after the setting changes so it
    /// takes effect live without reopening.</summary>
    public static void ApplyUiTextSize()
    {
        double size = AppSettings.UiTextSize;
        foreach (Window w in Current.Windows) w.FontSize = size;
    }
}
