using System.IO;
using System.Text.Json;

namespace ChessOverMesh.Gui;

/// <summary>Small global app settings persisted to disk (e.g. the last host connected to).</summary>
internal static class AppSettings
{
    private sealed class Data
    {
        public string? LastHost { get; set; }
        public bool ShowSignal { get; set; } = true;
        public bool RainbowEffect { get; set; }   // rainbow wave on each move (default off)
        public bool HeartbeatEnabled { get; set; } = false;   // opt-in TCP keep-alive (off by default)
        public bool AutoReconnect { get; set; } = true;       // retry once a minute when the device drops (on by default)
        public bool ShowPositionUpdates { get; set; } = true;   // log "Position received from X" in system messages
        public bool ShowNewNodeInfo { get; set; } = true;       // log new-node / node-info in system messages
        public bool CacheMessages { get; set; } = true;         // persist chat history per device (off = never cache)
        public int ChatMessageLimit { get; set; } = 100;        // max chat messages kept per channel (cache + on-screen)
        public int SystemMessageLimit { get; set; } = 200;      // max system messages kept on screen
        public bool ShowChessboard { get; set; } = true;        // show the chessboard; off = system-messages + chat only
        public string? SystemFilterHidden { get; set; }         // CSV of SysCategory names hidden in the system-messages filter
        // Notification sounds: file path per category ("" = off, null = not chosen yet → a default is used),
        // and 0–100 volume.
        public string? ChessSoundPath { get; set; }
        public string? ChatSoundPath { get; set; }
        public int ChessVolume { get; set; } = 80;
        public int ChatVolume { get; set; } = 80;
        // Per-message-type colours as "#RRGGBB" (null = use the built-in default).
        public string? NormalColor { get; set; }
        public string? PendingColor { get; set; }
        public string? AckedColor { get; set; }
        public string? RelayedColor { get; set; }
        public string? CachedColor { get; set; }
        public string? WarningColor { get; set; }
        // Per-system-message-category colours ("#RRGGBB"; null = built-in default).
        public string? SysGameColor { get; set; }
        public string? SysConnectionColor { get; set; }
        public string? SysNodesColor { get; set; }
        public string? SysPositionColor { get; set; }
        public string? SysTelemetryColor { get; set; }
        public string? SysTracerouteColor { get; set; }
        public string? SysAdminColor { get; set; }
        public string? SysRequestsColor { get; set; }
        public string? SysWarningsColor { get; set; }

        // Per-list text font (null family = built-in default) and size, for the moves / system / chat lists.
        public string? MovesFont { get; set; }
        public double MovesSize { get; set; } = 12;
        public string? SystemFont { get; set; }
        public double SystemSize { get; set; } = 12;
        public string? ChatFont { get; set; }
        public double ChatSize { get; set; } = 14;
        public string? NodesFont { get; set; }
        public double NodesSize { get; set; } = 12;

        // App-wide text size for buttons and settings/labels (the window chrome). The four content lists above
        // keep their own sizes. Default 12 = the WPF default, so it's a no-op until changed.
        public double UiTextSize { get; set; } = 12;

        // Remembered proxy sign-in credentials, keyed by proxy host. The password is DPAPI-protected.
        public Dictionary<string, ProxyCred> ProxyCreds { get; set; } = new();

        // Remembered size of resizable pop-up windows, keyed by a stable window name (see WindowSizeMemory).
        public Dictionary<string, WinSize> WindowSizes { get; set; } = new();
    }

    /// <summary>A remembered proxy login: username plus the DPAPI-protected password.</summary>
    public sealed class ProxyCred
    {
        public string User { get; set; } = "";
        public string Pass { get; set; } = "";   // DPAPI-protected ciphertext
    }

    /// <summary>A remembered window size, in device-independent pixels.</summary>
    public sealed class WinSize
    {
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChessOverMesh", "settings.json");

    private static Data Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
        }
        catch { /* ignore */ }
        return new Data();
    }

    private static void Mutate(Action<Data> change)
    {
        try
        {
            var d = Load();
            change(d);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    public static string? LastHost
    {
        get => Load().LastHost;
        set => Mutate(d => d.LastHost = value);
    }

    /// <summary>Show RSSI/SNR/hop info on received chat messages.</summary>
    public static bool ShowSignal
    {
        get => Load().ShowSignal;
        set => Mutate(d => d.ShowSignal = value);
    }

    /// <summary>Play the rainbow wave ripple on each move (default off).</summary>
    public static bool RainbowEffect
    {
        get => Load().RainbowEffect;
        set => Mutate(d => d.RainbowEffect = value);
    }

    /// <summary>Send a periodic TCP keep-alive heartbeat (opt-in, off by default).</summary>
    public static bool HeartbeatEnabled
    {
        get => Load().HeartbeatEnabled;
        set => Mutate(d => d.HeartbeatEnabled = value);
    }

    /// <summary>When a connected device drops, automatically retry the connection once a minute (opt-in).</summary>
    public static bool AutoReconnect
    {
        get => Load().AutoReconnect;
        set => Mutate(d => d.AutoReconnect = value);
    }

    /// <summary>Whether received position broadcasts are logged in the system messages pane (default on).</summary>
    public static bool ShowPositionUpdates
    {
        get => Load().ShowPositionUpdates;
        set => Mutate(d => d.ShowPositionUpdates = value);
    }

    /// <summary>Whether new-node / node-info events are logged in the system messages pane (default on).</summary>
    public static bool ShowNewNodeInfo
    {
        get => Load().ShowNewNodeInfo;
        set => Mutate(d => d.ShowNewNodeInfo = value);
    }

    /// <summary>Whether chat messages are cached to disk per device for reload on reconnect (default on).
    /// When off, nothing new is cached and the existing cache should be cleared.</summary>
    public static bool CacheMessages
    {
        get => Load().CacheMessages;
        set => Mutate(d => d.CacheMessages = value);
    }

    /// <summary>Show the chessboard (and moves) in the main window. When off, the board is hidden and only
    /// system messages and channel chat are shown (default on).</summary>
    public static bool ShowChessboard
    {
        get => Load().ShowChessboard;
        set => Mutate(d => d.ShowChessboard = value);
    }

    /// <summary>Max chat messages kept per channel — applies to both the disk cache and the on-screen list.</summary>
    public static int ChatMessageLimit
    {
        get => Load().ChatMessageLimit;
        set => Mutate(d => d.ChatMessageLimit = value);
    }

    /// <summary>Max system messages kept on screen (oldest trimmed past this).</summary>
    public static int SystemMessageLimit
    {
        get => Load().SystemMessageLimit;
        set => Mutate(d => d.SystemMessageLimit = value);
    }

    /// <summary>CSV of system-message category names the user has hidden in the System-messages filter.</summary>
    public static string? SystemFilterHidden
    {
        get => Load().SystemFilterHidden;
        set => Mutate(d => d.SystemFilterHidden = value);
    }

    // Notification sound per category: null until the user has chosen (caller substitutes a default).
    public static string? ChessSoundPath { get => Load().ChessSoundPath; set => Mutate(d => d.ChessSoundPath = value); }
    public static string? ChatSoundPath { get => Load().ChatSoundPath; set => Mutate(d => d.ChatSoundPath = value); }
    public static int ChessVolume { get => Load().ChessVolume; set => Mutate(d => d.ChessVolume = value); }
    public static int ChatVolume { get => Load().ChatVolume; set => Mutate(d => d.ChatVolume = value); }

    // Per-message-type colours ("#RRGGBB"); null until the user picks one.
    public static string? NormalColor { get => Load().NormalColor; set => Mutate(d => d.NormalColor = value); }
    public static string? PendingColor { get => Load().PendingColor; set => Mutate(d => d.PendingColor = value); }
    public static string? AckedColor { get => Load().AckedColor; set => Mutate(d => d.AckedColor = value); }
    public static string? RelayedColor { get => Load().RelayedColor; set => Mutate(d => d.RelayedColor = value); }
    public static string? CachedColor { get => Load().CachedColor; set => Mutate(d => d.CachedColor = value); }
    public static string? WarningColor { get => Load().WarningColor; set => Mutate(d => d.WarningColor = value); }

    // Per-system-message-category colours.
    public static string? SysGameColor { get => Load().SysGameColor; set => Mutate(d => d.SysGameColor = value); }
    public static string? SysConnectionColor { get => Load().SysConnectionColor; set => Mutate(d => d.SysConnectionColor = value); }
    public static string? SysNodesColor { get => Load().SysNodesColor; set => Mutate(d => d.SysNodesColor = value); }
    public static string? SysPositionColor { get => Load().SysPositionColor; set => Mutate(d => d.SysPositionColor = value); }
    public static string? SysTelemetryColor { get => Load().SysTelemetryColor; set => Mutate(d => d.SysTelemetryColor = value); }
    public static string? SysTracerouteColor { get => Load().SysTracerouteColor; set => Mutate(d => d.SysTracerouteColor = value); }
    public static string? SysAdminColor { get => Load().SysAdminColor; set => Mutate(d => d.SysAdminColor = value); }
    public static string? SysRequestsColor { get => Load().SysRequestsColor; set => Mutate(d => d.SysRequestsColor = value); }
    public static string? SysWarningsColor { get => Load().SysWarningsColor; set => Mutate(d => d.SysWarningsColor = value); }

    // Per-list text font family (null = default) and size.
    public static string? MovesFont { get => Load().MovesFont; set => Mutate(d => d.MovesFont = value); }
    public static double MovesSize { get => Load().MovesSize; set => Mutate(d => d.MovesSize = value); }
    public static string? SystemFont { get => Load().SystemFont; set => Mutate(d => d.SystemFont = value); }
    public static double SystemSize { get => Load().SystemSize; set => Mutate(d => d.SystemSize = value); }
    public static string? ChatFont { get => Load().ChatFont; set => Mutate(d => d.ChatFont = value); }
    public static double ChatSize { get => Load().ChatSize; set => Mutate(d => d.ChatSize = value); }
    public static string? NodesFont { get => Load().NodesFont; set => Mutate(d => d.NodesFont = value); }
    public static double NodesSize { get => Load().NodesSize; set => Mutate(d => d.NodesSize = value); }

    // App-wide text size for buttons and settings/labels (window chrome). Content lists keep their own sizes.
    public static double UiTextSize { get => Load().UiTextSize; set => Mutate(d => d.UiTextSize = value); }

    /// <summary>The remembered proxy login for <paramref name="host"/> (password decrypted), or null if none saved.</summary>
    public static (string User, string Pass)? GetProxyCred(string host)
    {
        var c = Load().ProxyCreds.GetValueOrDefault(host);
        if (c == null) return null;
        return (c.User, SecretProtector.Unprotect(c.Pass));
    }

    /// <summary>Remembers a proxy login for <paramref name="host"/> (password DPAPI-protected).</summary>
    public static void SetProxyCred(string host, string user, string pass) =>
        Mutate(d => d.ProxyCreds[host] = new ProxyCred { User = user, Pass = SecretProtector.Protect(pass) });

    /// <summary>Forgets the remembered proxy login for <paramref name="host"/>.</summary>
    public static void ClearProxyCred(string host) => Mutate(d => d.ProxyCreds.Remove(host));

    /// <summary>The last size the user left the window named <paramref name="key"/> at, or null if never resized.</summary>
    public static (double Width, double Height)? GetWindowSize(string key)
    {
        var s = Load().WindowSizes.GetValueOrDefault(key);
        return s == null ? null : (s.Width, s.Height);
    }

    /// <summary>Remembers the size of the window named <paramref name="key"/> for next time it opens.</summary>
    public static void SetWindowSize(string key, double width, double height) =>
        Mutate(d => d.WindowSizes[key] = new WinSize { Width = width, Height = height });

    /// <summary>Restores every app setting to its built-in default by overwriting the settings file with a fresh
    /// default set — colours, fonts, text sizes, sounds, message limits and display toggles, and also the last
    /// host, saved proxy logins and remembered window sizes. Does NOT touch the per-device cache (devices.json):
    /// cached chat, node and telemetry data and channel settings are kept.</summary>
    public static void ResetToDefaults()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Data(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
