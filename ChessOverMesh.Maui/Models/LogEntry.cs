using System.ComponentModel;
using ChessOverMesh.Mesh;
using Microsoft.Maui.Graphics;

namespace ChessOverMesh.Maui;

/// <summary>Category of a System-messages line, used by the per-type filter.</summary>
public enum SysCategory { Game, Connection, Nodes, Position, Telemetry, Traceroute, Admin, Requests, Warnings }

/// <summary>A move/system/chat list row whose text and colour update live (e.g. when an ack arrives).
/// MAUI port of the desktop LogEntry — bound by a CollectionView DataTemplate.</summary>
public sealed class LogEntry : INotifyPropertyChanged
{
    private string _text = "";
    public string Text
    {
        get => _text;
        set { _text = value; PropertyChanged?.Invoke(this, TextArgs); }
    }

    private Color _textColor = Palette.Normal;
    public Color TextColor
    {
        get => _textColor;
        set { _textColor = value; PropertyChanged?.Invoke(this, ColorArgs); }
    }

    // Optional dim metadata line shown under the message in the chat list (timestamp/channel/signal/marks).
    private string _detail = "";
    public string Detail
    {
        get => _detail;
        set { _detail = value; PropertyChanged?.Invoke(this, DetailArgs); }
    }

    // Per-row font, so the colours/fonts settings can restyle a list live ("" family = platform default).
    private string _fontFamily = "";
    public string FontFamily
    {
        get => _fontFamily;
        set { _fontFamily = value; PropertyChanged?.Invoke(this, FontFamilyArgs); }
    }

    private double _fontSize = 13;
    public double FontSize
    {
        get => _fontSize;
        set { _fontSize = value; PropertyChanged?.Invoke(this, FontSizeArgs); }
    }

    // For received chat rows: the raw mesh message, so the long-press menu can request the sender's node info
    // and show the signal/relay breakdown. Null for sent/system rows.
    public MeshTextMessage? Rx;

    // The mesh packet id of this chat message (sent or received), 0 if none — the target for replies/reactions.
    public uint PacketId;

    // The channel this chat row belongs to (for per-channel caching/removal). uint.MaxValue = divider/none.
    public uint Channel = uint.MaxValue;

    // Wall-clock time this chat row represents (for age-based auto-delete). default = unknown (never pruned).
    public DateTime Time;

    // Sender-set self-destruct time for this row (local wall-clock); null = no expiry. When set, the row shows a
    // live "deletes in …" countdown and is removed (screen + cache) once reached.
    public DateTime? ExpiresAt;

    // The live "🕓 deletes in …" countdown line, shown dim under the message on its own line (kept separate from
    // Detail so the sending/delivered marks that mutate Detail don't fight the per-second countdown). Empty = none.
    private string _expiry = "";
    public string Expiry
    {
        get => _expiry;
        set { _expiry = value; PropertyChanged?.Invoke(this, ExpiryArgs); }
    }
    private static readonly PropertyChangedEventArgs ExpiryArgs = new(nameof(Expiry));

    // Stable id of this row's cached copy, so "Remove message" can delete it from the cache too. Null if uncached.
    public string? CacheId;

    // For a DM row, the other node (conversation peer); 0 for channel/system rows. Used by the RX view filter.
    public uint DmPeer;

    // For system rows: the message category, so the System-messages filter can show/hide by type.
    public SysCategory Category = SysCategory.Game;

    // For a received chat row: the message text shown after the "<name>: " prefix. Kept so the row can be
    // re-rendered with the sender's real name once that node's info arrives (it first shows as "!hex"). Null otherwise.
    public string? ChatNameBody;

    // Whether this row is shown (the RX filter can hide a channel/DM's rows). Notifies so the list updates live.
    private bool _visible = true;
    public bool Visible
    {
        get => _visible;
        set { _visible = value; PropertyChanged?.Invoke(this, VisibleArgs); }
    }
    private static readonly PropertyChangedEventArgs VisibleArgs = new(nameof(Visible));

    // Emoji reactions on this chat message ("👍 ❤️ 2"), shown on its own line. Empty when none.
    private string _reactions = "";
    public string Reactions
    {
        get => _reactions;
        set { _reactions = value; PropertyChanged?.Invoke(this, ReactionsArgs); }
    }
    private static readonly PropertyChangedEventArgs ReactionsArgs = new(nameof(Reactions));

    private static readonly PropertyChangedEventArgs TextArgs = new(nameof(Text));
    private static readonly PropertyChangedEventArgs DetailArgs = new(nameof(Detail));
    private static readonly PropertyChangedEventArgs FontFamilyArgs = new(nameof(FontFamily));
    private static readonly PropertyChangedEventArgs FontSizeArgs = new(nameof(FontSize));
    private static readonly PropertyChangedEventArgs ColorArgs = new(nameof(TextColor));
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => _text;
}

/// <summary>Per-message-type colours (received/normal, awaiting-ack amber, acked green, failed red).
/// Mutable so a future colour-settings screen can recolour live, matching the desktop behaviour.</summary>
public static class Palette
{
    public static Color Normal  { get; set; } = Color.FromRgb(0xE0, 0xE0, 0xE0);
    public static Color Pending { get; set; } = Color.FromRgb(0xFF, 0xC1, 0x07);   // amber — awaiting ack
    public static Color Acked   { get; set; } = Color.FromRgb(0x77, 0xDD, 0x77);   // green — acknowledged
    public static Color Relayed { get; set; } = Color.FromRgb(0x80, 0xCB, 0xC4);   // teal — rebroadcast heard
    public static Color Cached  { get; set; } = Color.FromRgb(0x9E, 0x9E, 0x9E);   // grey — old cached history
    public static Color Warning { get; set; } = Color.FromRgb(0xFF, 0x6B, 0x6B);   // red — failed/warning

    // Per-system-message-category colours (System messages only — chat is never coloured by these).
    public static Color SysGame       { get; set; } = Color.FromRgb(0xE0, 0xE0, 0xE0);   // white/grey
    public static Color SysConnection { get; set; } = Color.FromRgb(0x80, 0xCB, 0xC4);   // teal
    public static Color SysNodes      { get; set; } = Color.FromRgb(0x7F, 0xC8, 0xE8);   // light blue
    public static Color SysPosition   { get; set; } = Color.FromRgb(0xA5, 0xD6, 0xA7);   // green
    public static Color SysTelemetry  { get; set; } = Color.FromRgb(0xC5, 0xA3, 0xFF);   // lavender
    public static Color SysTraceroute { get; set; } = Color.FromRgb(0xFF, 0xCC, 0x80);   // orange
    public static Color SysAdmin      { get; set; } = Color.FromRgb(0xFF, 0xD5, 0x4F);   // gold
    public static Color SysRequests   { get; set; } = Color.FromRgb(0xF4, 0x8F, 0xB1);   // pink
    public static Color SysWarnings   { get; set; } = Color.FromRgb(0xFF, 0x6B, 0x6B);   // red
}
