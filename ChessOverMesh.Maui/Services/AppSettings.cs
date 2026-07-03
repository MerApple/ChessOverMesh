using System.Text.Json;

namespace ChessOverMesh.Maui;

/// <summary>Small global app settings persisted as JSON in app-private storage (ported from the
/// desktop app; uses MAUI's AppDataDirectory instead of the Windows LocalApplicationData folder).</summary>
internal static class AppSettings
{
    private sealed class Data
    {
        public string? LastHost { get; set; }
        public List<string> RecentHosts { get; set; } = new();   // recently connected hosts, newest first
        public bool ShowSignal { get; set; } = true;
        public bool HeartbeatEnabled { get; set; } = false;   // opt-in TCP keep-alive (off by default)
        public bool AutoReconnect { get; set; } = true;       // retry once a minute when the device drops (on by default)
        public bool BackgroundPoll { get; set; } = true;      // periodic ~15-min poll for messages while asleep (on by default)
        public bool CacheMessages { get; set; } = true;         // persist chat history per device (off = never cache)
        public bool ShowChessboard { get; set; } = true;        // show the chessboard; off = system-messages only
        public int ChatMessageLimit { get; set; } = 100;        // max chat messages kept per channel (cache + on-screen)
        public int SystemMessageLimit { get; set; } = 200;      // max system messages kept on screen
        public string? SystemFilterHidden { get; set; }         // CSV of SysCategory names hidden in the system-messages filter
        public string? ChessSoundPath { get; set; }
        public string? ChatSoundPath { get; set; }
        public int ChessVolume { get; set; } = 80;
        public int ChatVolume { get; set; } = 80;
        public string? NormalColor { get; set; }
        public string? PendingColor { get; set; }
        public string? AckedColor { get; set; }
        public string? RelayedColor { get; set; }
        public string? CachedColor { get; set; }
        public string? WarningColor { get; set; }
        public string? SysGameColor { get; set; }
        public string? SysConnectionColor { get; set; }
        public string? SysNodesColor { get; set; }
        public string? SysPositionColor { get; set; }
        public string? SysTelemetryColor { get; set; }
        public string? SysTracerouteColor { get; set; }
        public string? SysAdminColor { get; set; }
        public string? SysRequestsColor { get; set; }
        public string? SysWarningsColor { get; set; }

        // Per-list text font (family "" = platform default) and size, for the moves / system / chat lists.
        public string? MovesFont { get; set; }
        public double MovesSize { get; set; } = 13;
        public string? SystemFont { get; set; }
        public double SystemSize { get; set; } = 13;
        public string? ChatFont { get; set; }
        public double ChatSize { get; set; } = 15;
        public string? NodesFont { get; set; }
        public double NodesSize { get; set; } = 13;

        // App-wide text size for buttons and labels (window chrome). Content lists keep their own sizes.
        // Default 14 matches the built-in style, so it's a no-op until changed.
        public double UiTextSize { get; set; } = 14;

        // Offline-map tile provider id (see MapTileProvider; null = OpenStreetMap online-only) and its API key.
        // OSM forbids bulk downloading, so caching an area requires a keyed provider + key.
        public string? MapProvider { get; set; }
        public string? MapApiKey { get; set; }

        // Remembered proxy sign-in credentials, keyed by proxy host. The password is protected via SecretProtector.
        public Dictionary<string, ProxyCred> ProxyCreds { get; set; } = new();
    }

    /// <summary>A remembered proxy login: username plus the protected password.</summary>
    public sealed class ProxyCred
    {
        public string User { get; set; } = "";
        public string Pass { get; set; } = "";   // protected ciphertext
    }

    private static readonly string FilePath =
        Path.Combine(FileSystem.AppDataDirectory, "settings.json");

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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    public static string? LastHost { get => Load().LastHost; set => Mutate(d => d.LastHost = value); }

    private const int RecentHostsMax = 10;
    /// <summary>Recently connected hosts, newest first (capped at 10) — for the Host dropdown on the Device tab.</summary>
    public static IReadOnlyList<string> RecentHosts => Load().RecentHosts;
    /// <summary>Records a successful connection's host at the top of the recent list (deduped, case-insensitive, capped).</summary>
    public static void AddRecentHost(string host)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) return;
        Mutate(d =>
        {
            d.RecentHosts.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
            d.RecentHosts.Insert(0, host);
            if (d.RecentHosts.Count > RecentHostsMax) d.RecentHosts.RemoveRange(RecentHostsMax, d.RecentHosts.Count - RecentHostsMax);
        });
    }
    /// <summary>Forgets all remembered recent hosts.</summary>
    public static void ClearRecentHosts() => Mutate(d => d.RecentHosts.Clear());
    public static bool ShowSignal { get => Load().ShowSignal; set => Mutate(d => d.ShowSignal = value); }
    public static bool HeartbeatEnabled { get => Load().HeartbeatEnabled; set => Mutate(d => d.HeartbeatEnabled = value); }
    public static bool AutoReconnect { get => Load().AutoReconnect; set => Mutate(d => d.AutoReconnect = value); }
    public static bool BackgroundPoll { get => Load().BackgroundPoll; set => Mutate(d => d.BackgroundPoll = value); }
    public static bool CacheMessages { get => Load().CacheMessages; set => Mutate(d => d.CacheMessages = value); }
    public static bool ShowChessboard { get => Load().ShowChessboard; set => Mutate(d => d.ShowChessboard = value); }
    public static int ChatMessageLimit { get => Load().ChatMessageLimit; set => Mutate(d => d.ChatMessageLimit = value); }
    public static int SystemMessageLimit { get => Load().SystemMessageLimit; set => Mutate(d => d.SystemMessageLimit = value); }
    public static string? SystemFilterHidden { get => Load().SystemFilterHidden; set => Mutate(d => d.SystemFilterHidden = value); }
    public static string? ChessSoundPath { get => Load().ChessSoundPath; set => Mutate(d => d.ChessSoundPath = value); }
    public static string? ChatSoundPath { get => Load().ChatSoundPath; set => Mutate(d => d.ChatSoundPath = value); }
    public static int ChessVolume { get => Load().ChessVolume; set => Mutate(d => d.ChessVolume = value); }
    public static int ChatVolume { get => Load().ChatVolume; set => Mutate(d => d.ChatVolume = value); }
    public static string? NormalColor { get => Load().NormalColor; set => Mutate(d => d.NormalColor = value); }
    public static string? PendingColor { get => Load().PendingColor; set => Mutate(d => d.PendingColor = value); }
    public static string? AckedColor { get => Load().AckedColor; set => Mutate(d => d.AckedColor = value); }
    public static string? RelayedColor { get => Load().RelayedColor; set => Mutate(d => d.RelayedColor = value); }
    public static string? CachedColor { get => Load().CachedColor; set => Mutate(d => d.CachedColor = value); }
    public static string? WarningColor { get => Load().WarningColor; set => Mutate(d => d.WarningColor = value); }
    public static string? SysGameColor { get => Load().SysGameColor; set => Mutate(d => d.SysGameColor = value); }
    public static string? SysConnectionColor { get => Load().SysConnectionColor; set => Mutate(d => d.SysConnectionColor = value); }
    public static string? SysNodesColor { get => Load().SysNodesColor; set => Mutate(d => d.SysNodesColor = value); }
    public static string? SysPositionColor { get => Load().SysPositionColor; set => Mutate(d => d.SysPositionColor = value); }
    public static string? SysTelemetryColor { get => Load().SysTelemetryColor; set => Mutate(d => d.SysTelemetryColor = value); }
    public static string? SysTracerouteColor { get => Load().SysTracerouteColor; set => Mutate(d => d.SysTracerouteColor = value); }
    public static string? SysAdminColor { get => Load().SysAdminColor; set => Mutate(d => d.SysAdminColor = value); }
    public static string? SysRequestsColor { get => Load().SysRequestsColor; set => Mutate(d => d.SysRequestsColor = value); }
    public static string? SysWarningsColor { get => Load().SysWarningsColor; set => Mutate(d => d.SysWarningsColor = value); }

    public static string? MovesFont { get => Load().MovesFont; set => Mutate(d => d.MovesFont = value); }
    public static double MovesSize { get => Load().MovesSize; set => Mutate(d => d.MovesSize = value); }
    public static string? SystemFont { get => Load().SystemFont; set => Mutate(d => d.SystemFont = value); }
    public static double SystemSize { get => Load().SystemSize; set => Mutate(d => d.SystemSize = value); }
    public static string? ChatFont { get => Load().ChatFont; set => Mutate(d => d.ChatFont = value); }
    public static double ChatSize { get => Load().ChatSize; set => Mutate(d => d.ChatSize = value); }
    public static string? NodesFont { get => Load().NodesFont; set => Mutate(d => d.NodesFont = value); }
    public static double NodesSize { get => Load().NodesSize; set => Mutate(d => d.NodesSize = value); }

    // App-wide text size for buttons and labels (window chrome). Content lists keep their own sizes.
    public static double UiTextSize { get => Load().UiTextSize; set => Mutate(d => d.UiTextSize = value); }

    /// <summary>The offline-map tile provider id (see <see cref="ChessOverMesh.Map.MapTileProvider"/>); null = OSM online-only.</summary>
    public static string? MapProvider { get => Load().MapProvider; set => Mutate(d => d.MapProvider = value); }
    /// <summary>The API key for the chosen keyed tile provider (empty for OSM).</summary>
    public static string? MapApiKey { get => Load().MapApiKey; set => Mutate(d => d.MapApiKey = value); }

    /// <summary>The remembered proxy login for <paramref name="host"/> (password decrypted), or null if none saved.</summary>
    public static (string User, string Pass)? GetProxyCred(string host)
    {
        var c = Load().ProxyCreds.GetValueOrDefault(host);
        if (c == null) return null;
        return (c.User, SecretProtector.Unprotect(c.Pass));
    }

    /// <summary>Remembers a proxy login for <paramref name="host"/> (password protected).</summary>
    public static void SetProxyCred(string host, string user, string pass) =>
        Mutate(d => d.ProxyCreds[host] = new ProxyCred { User = user, Pass = SecretProtector.Protect(pass) });

    /// <summary>Forgets the remembered proxy login for <paramref name="host"/>.</summary>
    public static void ClearProxyCred(string host) => Mutate(d => d.ProxyCreds.Remove(host));
}
