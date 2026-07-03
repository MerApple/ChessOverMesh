using System.Windows;

namespace ChessOverMesh.Gui;

/// <summary>
/// Makes a resizable pop-up window remember the size the user left it at, so it reopens at that size
/// instead of a fixed default. Sizes are keyed by a stable name and persisted via <see cref="AppSettings"/>.
/// </summary>
internal static class WindowSizeMemory
{
    /// <summary>
    /// Restores the previously-remembered size for <paramref name="key"/> (falling back to
    /// <paramref name="defaultWidth"/>/<paramref name="defaultHeight"/> the first time), and saves the
    /// window's size again when it closes. Call before <c>Show()</c>/<c>ShowDialog()</c>.
    /// </summary>
    /// <param name="key">A stable identifier for this window (e.g. "NodeInfo"), independent of its title.</param>
    public static void RememberSize(this Window win, string key, double defaultWidth, double defaultHeight)
    {
        var saved = AppSettings.GetWindowSize(key);
        win.Width = saved?.Width ?? defaultWidth;
        win.Height = saved?.Height ?? defaultHeight;

        // Don't let a remembered size shrink the window below something usable, or grow it past the
        // work area if the user has since moved to a smaller screen.
        win.MinWidth = System.Math.Min(defaultWidth, 320);
        win.MinHeight = System.Math.Min(defaultHeight, 240);
        var wa = SystemParameters.WorkArea;
        if (win.Width > wa.Width) win.Width = wa.Width;
        if (win.Height > wa.Height) win.Height = wa.Height;

        win.Closing += (_, _) =>
        {
            // RestoreBounds is the "normal" (non-maximized) size even if the window is currently maximized.
            var b = win.RestoreBounds;
            var w = b.Width; var h = b.Height;
            if (double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0)
                AppSettings.SetWindowSize(key, w, h);
        };
    }
}
