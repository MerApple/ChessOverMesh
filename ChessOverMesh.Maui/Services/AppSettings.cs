using System.Text.Json;

namespace ChessOverMesh.Maui;

/// <summary>Small global app settings persisted as JSON in app-private storage (ported from the
/// desktop app; uses MAUI's AppDataDirectory instead of the Windows LocalApplicationData folder).</summary>
internal static class AppSettings
{
    private sealed class Data
    {
        public string? LastHost { get; set; }
        public bool ShowSignal { get; set; } = true;
        public bool HeartbeatEnabled { get; set; } = false;   // opt-in TCP keep-alive (off by default)
        public bool AutoReconnect { get; set; } = true;       // retry once a minute when the device drops (on by default)
        public bool BackgroundPoll { get; set; } = true;      // periodic ~15-min poll for messages while asleep (on by default)
        public bool ShowPositionUpdates { get; set; } = true;   // log "Position received from X" in system messages
        public bool ShowNewNodeInfo { get; set; } = true;       // log new-node / node-info in system messages
        public bool CacheMessages { get; set; } = true;         // persist chat history per device (off = never cache)
        public bool ShowChessboard { get; set; } = true;        // show the chessboard; off = system-messages only
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
    public static bool ShowSignal { get => Load().ShowSignal; set => Mutate(d => d.ShowSignal = value); }
    public static bool HeartbeatEnabled { get => Load().HeartbeatEnabled; set => Mutate(d => d.HeartbeatEnabled = value); }
    public static bool AutoReconnect { get => Load().AutoReconnect; set => Mutate(d => d.AutoReconnect = value); }
    public static bool BackgroundPoll { get => Load().BackgroundPoll; set => Mutate(d => d.BackgroundPoll = value); }
    public static bool ShowPositionUpdates { get => Load().ShowPositionUpdates; set => Mutate(d => d.ShowPositionUpdates = value); }
    public static bool ShowNewNodeInfo { get => Load().ShowNewNodeInfo; set => Mutate(d => d.ShowNewNodeInfo = value); }
    public static bool CacheMessages { get => Load().CacheMessages; set => Mutate(d => d.CacheMessages = value); }
    public static bool ShowChessboard { get => Load().ShowChessboard; set => Mutate(d => d.ShowChessboard = value); }
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
}
