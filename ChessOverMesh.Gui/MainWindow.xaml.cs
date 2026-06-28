using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ChessOverMesh.Chess;
using ChessOverMesh.Game;
using ChessOverMesh.Mesh;
using MediaColor = System.Windows.Media.Color;
using GameColor = ChessOverMesh.Chess.Color;

namespace ChessOverMesh.Gui;

public partial class MainWindow : Window
{
    // ---- Palette ----
    private static readonly Brush LightSquare = new SolidColorBrush(MediaColor.FromRgb(0xEE, 0xEE, 0xD2));
    private static readonly Brush DarkSquare = new SolidColorBrush(MediaColor.FromRgb(0x76, 0x96, 0x56));
    private static readonly Brush SelectBrush = new SolidColorBrush(MediaColor.FromRgb(0xF6, 0xF6, 0x69));
    private static readonly Brush LastMoveBrush = new SolidColorBrush(MediaColor.FromRgb(0xBB, 0xCB, 0x44));
    private static readonly Brush CheckBrush = new SolidColorBrush(MediaColor.FromRgb(0xE5, 0x73, 0x73));
    // Pieces are drawn as a filled glyph plus an overlaid outline glyph so both
    // colors stay legible on light and dark squares.
    private static readonly Brush WhitePieceFill = new SolidColorBrush(MediaColor.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush WhitePieceOutline = new SolidColorBrush(MediaColor.FromRgb(0x18, 0x18, 0x18));
    private static readonly Brush BlackPieceFill = new SolidColorBrush(MediaColor.FromRgb(0x20, 0x20, 0x20));
    private static readonly Brush BlackPieceOutline = new SolidColorBrush(MediaColor.FromRgb(0xE8, 0xE8, 0xE8));
    private static readonly Brush MarkerBrush = new SolidColorBrush(MediaColor.FromArgb(0x66, 0x20, 0x20, 0x20));

    private const string RoleAuto = "Auto";
    private const string RoleWhite = "White";
    private const string RoleBlack = "Black";

    private readonly Button[] _cells = new Button[64];
    private readonly TextBlock[] _glyphFill = new TextBlock[64];
    private readonly TextBlock[] _glyphOutline = new TextBlock[64];
    private readonly Ellipse[] _markers = new Ellipse[64];

    // Move "wave" that ripples outward from a just-moved piece: rainbow across the green (dark) squares, and —
    // on a capture — red→dark-red across the light squares.
    private static readonly SolidColorBrush[] Rainbow = BuildRainbow(72);
    private static readonly SolidColorBrush[] RedRamp = BuildRedRamp(72);
    private const double WaveSpeed = 7.5;    // wavefront expansion speed, in board squares per second
    private const double WaveSpan = 3.5;     // width of the colour band, in squares
    private readonly List<(int Sq, double Dist, bool IsDark)> _waveSquares = new();   // squares being animated, distance from the move, and dark/light
    private double _waveMaxDist;             // distance to the farthest square — the wave runs until it passes this
    private System.Diagnostics.Stopwatch? _waveClock;
    private EventHandler? _waveHandler;
    private bool _rainbowEffect;             // when off (default), moves render the board as before (no wave)

    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _probeTimer;   // TCP reachability probe (connection-loss detection)
    private readonly DispatcherTimer _autoReconnectTimer;   // ticks every second to count down to the next retry
    private bool _autoReconnecting;
    private string _autoReconnectHost = "";
    private int _reconnectCountdown;                        // seconds until the next auto-reconnect attempt
    private DispatcherTimer? _chatHoldTimer;   // one-shot: re-enables Send when the post-receive hold ends
    private bool _polling;
    private bool _refreshing;

    private MeshtasticHttpClient? _mesh;
    private Board _board = Board.CreateStartingPosition();
    private GameColor _myColor = GameColor.White;
    private string _gameId = "";
    private int _ply;
    private bool _playing;
    private bool _gameOver;
    private bool _connected;
    private bool _connecting;
    private int _probeFailures;   // consecutive TCP reachability-probe failures; resets when the device answers
    private bool _probing;        // re-entrancy guard for the TCP probe
    private DateTime _lastPollOkUtc = DateTime.UtcNow;   // when a poll last succeeded — while data flows the device is clearly up
    private const int ConnectionLostThreshold = 3;   // consecutive probe failures before declaring the link lost
    private const int ReconnectIntervalSeconds = 15;  // wait between auto-reconnect attempts (the countdown resets to this)
    // A powered-off/unreachable device is detected with a TCP CONNECT probe rather than an HTTP-response timeout:
    // the ESP32's TCP stack (lwIP) accepts connections from a separate task, so a connect succeeds fast even when
    // the radio is busy and HTTP responses are slow — cleanly telling "alive but slow" from "dead" with no false
    // positives. The probe only runs when a poll hasn't recently succeeded (ProbeIdleGrace), so it's near-free in
    // normal use, and the data poll keeps the generous 30s default so a slow-but-working read is never cancelled.
    private static readonly TimeSpan TcpProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProbeIdleGrace = TimeSpan.FromSeconds(4);
    private bool _synced;    // true once the post-connect packet backlog has been drained
    private bool _syncing;   // true while the background backlog drain is running
    private string _currentHost = "";
    private string _probeHost = "";   // host:port the reachability probe dials (the API port actually in use)
    private int _probePort;

    // Reception watchdog: the device's live-packet subscription can drop (reboot, or another client stealing
    // the shared /fromradio queue), after which messages silently stop. Warn once — and on receive errors —
    // suggesting "Update nodes" (which re-sends want_config and re-subscribes).
    private DateTime _lastRxUtc = DateTime.UtcNow;
    private bool _rxStallWarned;          // true while a dead-air warning is active; cleared on any packet so the next spell warns at once
    private DateTime _lastStallWarnUtc;   // when the dead-air warning was last shown, to rate-limit the repeats
    // Warn when no packet of any kind has been received for ReceiveStallTimeout, then repeat the warning every
    // StallRepeatInterval for as long as the silence continues (it stops as soon as any packet arrives).
    private static readonly TimeSpan ReceiveStallTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StallRepeatInterval = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private int? _selected;
    private List<Move> _legalForSelected = new();
    private (int from, int to)? _lastMove;

    // ---- Channels & chat preferences ----
    // Chess uses the single _mesh.ChannelIndex; chat is independent: it listens on a set of channels and
    // transmits on one of them. Per-device prefs are persisted in DeviceCache; the toggles in AppSettings.
    private uint _chatTxChannel;
    // When set, the chat TX target is a direct message to this node (instead of a channel broadcast). The
    // DM is still stamped with _chatTxChannel (decision: reuse the current channel for the non-PKI fallback).
    private uint? _chatTxDest;
    // Per-node DM/Block flags for the connected device (node num → prefs), loaded from DeviceCache on connect.
    private readonly Dictionary<uint, DeviceCache.NodePrefs> _nodePrefs = new();
    // Set while the Nodes dialog is open, so traceroute / node-info replies can update its status line.
    private Action<string>? _nodeDiagHandler;
    // Set while the Nodes dialog is open: refreshes node rows in place (e.g. when fresh telemetry/signal arrives).
    private Action? _nodesRefresh;
    // Set while the Nodes dialog is open: rebuilds the whole list (used when new nodes appear and rows must be added).
    private Action? _nodesRepopulate;
    // Tracks the known-node count between poll cycles so we can detect newly heard nodes (rebuild) vs. just
    // refresh existing rows in place. -1 until the first poll establishes a baseline.
    private int _lastNodeViewCount = -1;
    // Set while the Nodes dialog is open: sets its status line without rebuilding the list (telemetry feedback).
    private Action<string>? _nodeStatusHandler;
    // Set while a telemetry-history window is open: reloads it when fresh telemetry arrives for its node.
    private Action? _telemetryRefresh;
    // Open traceroute result windows awaiting a reply, keyed by the node being traced (reply's sender).
    private readonly Dictionary<uint, Action<MeshTraceroute>> _tracerouteWaiters = new();
    // Optional observer of each poll cycle's result, used by the traceroute window for live diagnostics.
    private Action<MeshReceiveResult>? _pollObserver;
    private readonly HashSet<uint> _chatListen = new();
    // RX view filter: which targets are HIDDEN from the chat view (default empty = everything shown), and the
    // unread count per hidden target. A target is a channel (IsDm=false, Id=index) or a DM peer (IsDm=true, Id=node).
    private readonly HashSet<(bool IsDm, uint Id)> _rxHidden = new();
    private readonly Dictionary<(bool IsDm, uint Id), int> _unread = new();
    private bool _showSignal = true;             // show RSSI/SNR/hops on received chat lines
    private readonly HashSet<uint> _chatAckOn = new();   // channels where chat acks are on (default off; chess always acks)
    private readonly HashSet<uint> _chatAckSignalOn = new();   // channels whose chat ack also reports RSSI/SNR/hops
    // Per-channel auto-ack keywords: a received message whose (lowercased) text contains any of these triggers a
    // CHATACK-with-RSSI regardless of the channel's ack setting (handy for range-test pings).
    private readonly Dictionary<uint, List<string>> _ackTriggers = new();
    // Reply state: packet id of the message the next chat will reply to (0 = not replying), plus a cache of
    // chat text by packet id so a reply reference can quote the original message.
    private uint _replyToId;
    private readonly Dictionary<uint, string> _chatById = new();
    private readonly Dictionary<uint, LogEntry> _chatEntryById = new();   // packet id → its chat row (for attaching reactions)
    private readonly Dictionary<uint, List<(string Emoji, uint Node)>> _reactions = new();   // target packet id → reactions

    // Notification sounds (path "" = off) and 0–100 volume, per category, played via MediaPlayer.
    private string _chessSoundPath = "", _chatSoundPath = "";
    private int _chessVolume = 80, _chatVolume = 80;
    private readonly System.Windows.Media.MediaPlayer _chessPlayer = new();
    private readonly System.Windows.Media.MediaPlayer _chatPlayer = new();
    private string _chessOpened = "", _chatOpened = "";

    private readonly List<string> _moveHistory = new();   // UCI moves from the start (for saving)
    private GameSave? _loadedGame;                          // a game loaded from file, to resume on Create/Join
    private string _loadedName = "";

    // ---- Delivery acknowledgement / retry ----
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(15);
    private const int MaxSendAttempts = 2;

    // Before transmitting an application-level ack, wait a random 0–2s. Acks are responses to a
    // broadcast, so several receivers tend to ack at the same instant; jittering spreads those
    // transmissions out to avoid colliding on the shared channel.
    private static readonly TimeSpan MaxAckJitter = TimeSpan.FromSeconds(2);
    private static Task AckJitterDelay() =>
        Task.Delay(Random.Shared.Next((int)MaxAckJitter.TotalMilliseconds + 1));

    // Max length of a chat message as it goes on the wire (the encrypted base64 form when a key is set).
    private const int MaxChatChars = 200;

    // After receiving the opponent's move we send an ack; hold off sending our own move for this long
    // so that ack finishes transmitting first. _moveSendAllowedUtc is when we're allowed to move again.
    private static readonly TimeSpan MoveSendDelay = TimeSpan.FromSeconds(5);
    private DateTime _moveSendAllowedUtc = DateTime.MinValue;

    // Same idea for chat: after receiving a chat message we send a CHATACK; hold off sending our own
    // chat briefly so that ack transmits first.
    private static readonly TimeSpan ChatSendDelay = TimeSpan.FromSeconds(5);
    private DateTime _chatSendAllowedUtc = DateTime.MinValue;
    private readonly DispatcherTimer _ackTimer;
    private readonly Dictionary<uint, PendingSend> _pending = new();
    private bool _checkingAcks;

    // Tracks who has acknowledged each chat message we sent (keyed by its packet id).
    private readonly Dictionary<uint, ChatAckInfo> _chatAckers = new();

    // Open games announced on the channel that we could join (keyed by game id).
    private readonly Dictionary<string, PendingGame> _pendingGames = new();

    // SaveName is set when the game resumes a saved file; joiners must load that same file to join.
    private sealed record PendingGame(string GameId, uint CreatorNode, GameColor CreatorColor, uint Channel, string SaveName);

    // While waiting for the host to acknowledge our JOIN.
    private bool _joining;
    private PendingGame? _joinGame;
    private GameColor _joinColor;

    // While waiting for the opponent to acknowledge our RESIGN.
    private bool _resigning;

    // True for a creator who has announced a game but whose opponent hasn't joined yet.
    private bool _awaitingOpponent;

    // Tracks who acknowledged the game we just created (shown like chat ackers).
    private ChatAckInfo? _createInfo;

    private sealed class ChatAckInfo
    {
        public LogEntry Entry = null!;
        public string BaseText = "";
        public readonly HashSet<uint> Ackers = new();
        public readonly Dictionary<uint, string> AckerSignals = new();   // acker node → how THEY heard our message (RSSI/SNR/hops they reported)
        public readonly Dictionary<uint, string> MyReception = new();    // acker node → how OUR node heard their ack packet
    }

    // Inline delivery markers shown in the move/chat lists.
    private const string MarkSending = " …";
    private const string MarkDelivered = " ✓";
    private const string MarkFailed = " ✗";
    private const string MarkSent = " ↗";   // transmitted on an ack-off channel (no ack expected)

    /// <summary>A move- or chat-list row whose text updates live (e.g. when an ack arrives).</summary>
    private sealed class LogEntry : INotifyPropertyChanged
    {
        private string _text = "";
        public string Text
        {
            get => _text;
            set { _text = value; PropertyChanged?.Invoke(this, TextChangedArgs); }
        }
        // Optional dim metadata line shown under the message in the chat list (timestamp/channel/signal/marks).
        // Empty for move/system rows, which render as a single line.
        private string _detail = "";
        public string Detail
        {
            get => _detail;
            set { _detail = value; PropertyChanged?.Invoke(this, DetailChangedArgs); }
        }
        private static readonly PropertyChangedEventArgs DetailChangedArgs = new(nameof(Detail));

        // Per-row text colour; can change over a message's lifetime (e.g. yellow→green→red), so it notifies.
        private Brush _foreground = NormalText;
        public Brush Foreground
        {
            get => _foreground;
            set { _foreground = value; PropertyChanged?.Invoke(this, ForegroundChangedArgs); }
        }
        // For received chat rows: the raw mesh message, so right-click → "More information" can show the
        // full signal/relay breakdown. Null for sent/system/move rows.
        public MeshTextMessage? Rx;

        // The mesh packet id of this chat message (sent or received), 0 if none — used as the target for replies.
        public uint PacketId;

        // The channel this chat row belongs to (so it can be cached/cleared per channel). uint.MaxValue = divider/none.
        public uint Channel = uint.MaxValue;

        // For a DM row, the other node (the conversation peer); 0 for channel/system rows. Used by the RX filter.
        public uint DmPeer;

        // Whether this row is shown (the RX filter can hide a channel/DM's rows). Notifies so the list updates live.
        private bool _visible = true;
        public bool Visible
        {
            get => _visible;
            set { _visible = value; PropertyChanged?.Invoke(this, VisibleChangedArgs); }
        }
        private static readonly PropertyChangedEventArgs VisibleChangedArgs = new(nameof(Visible));

        // Stable id of this row's cached copy, so right-click → "Remove message" can delete it from the cache too.
        // Null for rows that were never cached (system/move/divider) or legacy cached rows.
        public string? CacheId;

        // For a sent broadcast confirmed by an implicit relay ack: the full "relayed by … [RSSI/SNR]" text,
        // kept so "More information" can always show the signal even when the inline view hides it. Null otherwise.
        public string? RelayAckInfo;

        // Emoji reactions on this chat message ("👍 ❤️ 2"), shown on its own line. Empty when none.
        private string _reactions = "";
        public string Reactions
        {
            get => _reactions;
            set { _reactions = value; PropertyChanged?.Invoke(this, ReactionsChangedArgs); }
        }
        private static readonly PropertyChangedEventArgs ReactionsChangedArgs = new(nameof(Reactions));

        private static readonly PropertyChangedEventArgs TextChangedArgs = new(nameof(Text));
        private static readonly PropertyChangedEventArgs ForegroundChangedArgs = new(nameof(Foreground));
        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => _text;
    }

    // Per-message-type colours. Kept mutable (not frozen) so the Color settings window can recolour them
    // live — every list line bound to one of these brushes updates instantly when its Color changes.
    private static readonly MediaColor DefNormalColor = MediaColor.FromRgb(0xE0, 0xE0, 0xE0);
    private static readonly MediaColor DefWarningColor = MediaColor.FromRgb(0xFF, 0x6B, 0x6B);   // red
    private static readonly MediaColor DefPendingColor = MediaColor.FromRgb(0xFF, 0xC1, 0x07);   // amber (awaiting ack)
    private static readonly MediaColor DefAckedColor = MediaColor.FromRgb(0x77, 0xDD, 0x77);     // green (acknowledged)
    private static readonly MediaColor DefRelayedColor = MediaColor.FromRgb(0x80, 0xCB, 0xC4);   // teal (rebroadcast heard)
    private static readonly MediaColor DefCachedColor = MediaColor.FromRgb(0x9E, 0x9E, 0x9E);    // grey (old cached history)
    internal static readonly SolidColorBrush NormalText = new(DefNormalColor);
    internal static readonly SolidColorBrush WarningText = new(DefWarningColor);
    internal static readonly SolidColorBrush PendingText = new(DefPendingColor);
    internal static readonly SolidColorBrush AckedText = new(DefAckedColor);
    internal static readonly SolidColorBrush RelayedText = new(DefRelayedColor);
    internal static readonly SolidColorBrush CachedText = new(DefCachedColor);
    private static readonly Brush CounterNormal = Frozen(MediaColor.FromRgb(0xB0, 0xB0, 0xB0));
    private static Brush Frozen(MediaColor c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>An outbound message awaiting a mesh acknowledgement.</summary>
    private sealed class PendingSend
    {
        public uint Id;
        public string Payload = "";
        public DateTime LastSentUtc;
        public int Attempts;
        public bool IsMove;
        public bool IsJoin;                // a JOIN handshake awaiting the host's JOINACK
        public bool IsResign;              // a RESIGN awaiting the opponent's RESIGNACK
        public bool IsCreate;              // a NEW announcement awaiting at least one CREATEACK
        public bool IsSave;                // a SAVE request awaiting the opponent's SAVEACK
        public bool IsChat;                // a chat message awaiting confirmation (only one in flight at a time)
        public bool IsRelayConfirm;        // ack-off chat: confirmed by overhearing a relay, not a CHATACK
        public bool IsDm;                  // a direct message — confirmed by the recipient's routing ack
        public uint ReplyId;               // when replying to a message, that message's packet id (carried on resend)
        public uint Channel;               // for chat: the channel it was sent on (any reply there confirms it)
        public string SaveFileName = "";   // for IsSave: the filename to save locally on ack
        public int Ply;                    // for moves: the half-move number being acknowledged
        public string Label = "";          // human description for status
        public LogEntry? Entry;            // the move/chat row to annotate with a delivery mark
        public string BaseText = "";       // row text without the delivery mark
        public bool NeedsResend;           // a move whose auto-retries are exhausted — awaiting a manual Resend
        public bool RelayHeard;            // for ack-on chat: we overheard a relay (intermediate "relayed" state)
        public DateTime SendDeadlineUtc;   // for chat: when the wait gives up if unconfirmed — drives the Send countdown
    }

    public MainWindow()
    {
        InitializeComponent();
        // Show the app version in the title bar (assembly version from the .csproj), e.g. "… v1.0.1".
        Title = $"Chess over Meshtastic  v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!.ToString(3)}";
        BrushFreeze();

        // Pre-fill the last host we connected to.
        var lastHost = AppSettings.LastHost;
        if (!string.IsNullOrWhiteSpace(lastHost)) HostBox.Text = lastHost;

        BuildBoard();
        Render();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _pollTimer.Tick += async (_, _) => await PollAsync();

        _ackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ackTimer.Tick += async (_, _) => { await CheckPendingAcksAsync(); UpdateChatSendButton(); };
        _ackTimer.Start();

        _probeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _probeTimer.Tick += async (_, _) => await ProbeAsync();
        _probeTimer.Start();

        _autoReconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoReconnectTimer.Tick += async (_, _) => await AutoReconnectTickAsync();

        _chatHoldTimer = new DispatcherTimer();
        _chatHoldTimer.Tick += (_, _) => { _chatHoldTimer!.Stop(); ApplyConnectionState(); };

        LoadSoundSettings();
        LoadColors();
        LoadFonts();
        _rainbowEffect = AppSettings.RainbowEffect;

        Closed += (_, _) => { StopMoveWave(); _pollTimer.Stop(); _ackTimer.Stop(); _probeTimer.Stop(); _autoReconnectTimer.Stop(); _chatHoldTimer.Stop(); _mesh?.Dispose(); };

        ApplyConnectionState();
    }

    private void LoadSoundSettings()
    {
        // Use a sensible default the first time (path not chosen yet); "" means the user turned it off.
        _chessSoundPath = AppSettings.ChessSoundPath ?? SoundLibrary.DefaultChess();
        _chatSoundPath = AppSettings.ChatSoundPath ?? SoundLibrary.DefaultChat();
        _chessVolume = AppSettings.ChessVolume;
        _chatVolume = AppSettings.ChatVolume;
        PreOpen(_chessPlayer, _chessSoundPath, ref _chessOpened);
        PreOpen(_chatPlayer, _chatSoundPath, ref _chatOpened);
    }

    private static void PreOpen(System.Windows.Media.MediaPlayer player, string path, ref string opened)
    {
        // Open the file ahead of time so the first Play isn't dropped while the media loads.
        try { if (path.Length > 0) { player.Open(new Uri(path)); opened = path; } }
        catch { opened = ""; }
    }

    private void PlayChessSound() => PlaySound(_chessPlayer, _chessSoundPath, _chessVolume, ref _chessOpened);
    private void PlayChatSound() => PlaySound(_chatPlayer, _chatSoundPath, _chatVolume, ref _chatOpened);

    private static void PlaySound(System.Windows.Media.MediaPlayer player, string path, int volume, ref string opened)
    {
        if (string.IsNullOrEmpty(path)) return;   // "(none)" — sound off for this category
        try
        {
            if (opened != path) { player.Open(new Uri(path)); opened = path; }
            player.Volume = Math.Clamp(volume, 0, 100) / 100.0;
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch { /* missing/unplayable file or no audio device — ignore */ }
    }

    // Opens the settings hub, which groups the Device / Color / Sound sections behind one button. Device
    // configuration is only available while connected, so its enabled state is computed here at open time.
    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        bool deviceEnabled = _connected && !_refreshing && !_joining && _mesh != null;
        new SettingsWindow(this, deviceEnabled, OpenDeviceSettings, OpenColorSettings, OpenSoundSettings, OpenChessSettings, OpenConnectionSettings, OpenSystemMessagesSettings, OpenSystemSettings).ShowDialog();
    }

    private void OpenSystemMessagesSettings()
    {
        var dlg = new SystemMessagesWindow(this, AppSettings.ShowPositionUpdates, AppSettings.ShowNewNodeInfo);
        if (dlg.ShowDialog() != true) return;
        AppSettings.ShowPositionUpdates = dlg.ShowPositions;
        AppSettings.ShowNewNodeInfo = dlg.ShowNewNodes;
    }

    // System settings (cached-messages toggle): the window applies its changes immediately.
    private void OpenSystemSettings() => new SystemSettingsWindow(this).ShowDialog();

    private void OpenConnectionSettings()
    {
        var dlg = new ConnectionSettingsWindow(this, AppSettings.AutoReconnect);
        if (dlg.ShowDialog() != true) return;
        AppSettings.AutoReconnect = dlg.AutoReconnect;
        if (!AppSettings.AutoReconnect) StopAutoReconnect(reconnected: false);   // turning it off cancels any retry loop
    }

    private void OpenChessSettings()
    {
        var dlg = new ChessSettingsWindow(this, _rainbowEffect);
        if (dlg.ShowDialog() != true) return;
        _rainbowEffect = dlg.RainbowEffect;
        AppSettings.RainbowEffect = _rainbowEffect;
        if (!_rainbowEffect) StopMoveWave();   // turning it off mid-ripple clears the wave immediately
    }

    private void OpenSoundSettings()
    {
        var dlg = new SoundSettingsWindow(this, _chessSoundPath, _chessVolume, _chatSoundPath, _chatVolume);
        if (dlg.ShowDialog() != true) return;

        _chessSoundPath = dlg.ChessSoundPath; _chessVolume = dlg.ChessVolume;
        _chatSoundPath = dlg.ChatSoundPath; _chatVolume = dlg.ChatVolume;
        AppSettings.ChessSoundPath = _chessSoundPath; AppSettings.ChessVolume = _chessVolume;
        AppSettings.ChatSoundPath = _chatSoundPath; AppSettings.ChatVolume = _chatVolume;
        PreOpen(_chessPlayer, _chessSoundPath, ref _chessOpened);
        PreOpen(_chatPlayer, _chatSoundPath, ref _chatOpened);
    }

    // ---- Message colours -------------------------------------------------------------

    private void LoadColors()
    {
        NormalText.Color = ParseHex(AppSettings.NormalColor) ?? DefNormalColor;
        PendingText.Color = ParseHex(AppSettings.PendingColor) ?? DefPendingColor;
        AckedText.Color = ParseHex(AppSettings.AckedColor) ?? DefAckedColor;
        RelayedText.Color = ParseHex(AppSettings.RelayedColor) ?? DefRelayedColor;
        CachedText.Color = ParseHex(AppSettings.CachedColor) ?? DefCachedColor;
        WarningText.Color = ParseHex(AppSettings.WarningColor) ?? DefWarningColor;
    }

    private void OpenColorSettings()
    {
        // Each choice points at the live brush so picking a colour recolours existing lines immediately.
        var choices = new List<ColorChoice>
        {
            new("Received / normal text", NormalText, DefNormalColor, c => AppSettings.NormalColor = ToHex(c)),
            new("Sending — awaiting ack",  PendingText, DefPendingColor, c => AppSettings.PendingColor = ToHex(c)),
            new("Delivered / acknowledged", AckedText, DefAckedColor, c => AppSettings.AckedColor = ToHex(c)),
            new("Relayed (rebroadcast heard)", RelayedText, DefRelayedColor, c => AppSettings.RelayedColor = ToHex(c)),
            new("Old cached messages",      CachedText, DefCachedColor, c => AppSettings.CachedColor = ToHex(c)),
            new("Failed / warning",         WarningText, DefWarningColor, c => AppSettings.WarningColor = ToHex(c)),
        };
        var fonts = new List<FontChoice>
        {
            new("Moves", MoveList.FontFamily.Source, MoveList.FontSize, (f, s) => ApplyMovesFont(f, s)),
            new("System messages", SystemList.FontFamily.Source, SystemList.FontSize, (f, s) => ApplySystemFont(f, s)),
            new("Chat text", ChatList.FontFamily.Source, ChatList.FontSize, (f, s) => ApplyChatFont(f, s)),
        };
        new ColorSettingsWindow(this, choices, fonts).ShowDialog();
    }

    // ---- List fonts -------------------------------------------------------------------

    private void LoadFonts()
    {
        // Moves/system default to Consolas (matching the original XAML); chat to the default UI font.
        ApplyMovesFont(AppSettings.MovesFont ?? "Consolas", AppSettings.MovesSize, persist: false);
        ApplySystemFont(AppSettings.SystemFont ?? "Consolas", AppSettings.SystemSize, persist: false);
        ApplyChatFont(AppSettings.ChatFont ?? "Segoe UI", AppSettings.ChatSize, persist: false);
    }

    private void ApplyMovesFont(string family, double size, bool persist = true)
    {
        MoveList.FontFamily = new FontFamily(family);
        MoveList.FontSize = size;
        if (persist) { AppSettings.MovesFont = family; AppSettings.MovesSize = size; }
    }

    private void ApplySystemFont(string family, double size, bool persist = true)
    {
        SystemList.FontFamily = new FontFamily(family);
        SystemList.FontSize = size;
        if (persist) { AppSettings.SystemFont = family; AppSettings.SystemSize = size; }
    }

    private void ApplyChatFont(string family, double size, bool persist = true)
    {
        // The chat message line inherits this; the dim metadata line keeps its small fixed size.
        ChatList.FontFamily = new FontFamily(family);
        ChatList.FontSize = size;
        if (persist) { AppSettings.ChatFont = family; AppSettings.ChatSize = size; }
    }

    private static string ToHex(MediaColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static MediaColor? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var s = hex.TrimStart('#');
            if (s.Length == 6)
                return MediaColor.FromRgb(Convert.ToByte(s.Substring(0, 2), 16),
                                          Convert.ToByte(s.Substring(2, 2), 16),
                                          Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch { /* malformed — use default */ }
        return null;
    }

    private static void BrushFreeze()
    {
        foreach (var b in new[] { LightSquare, DarkSquare, SelectBrush, LastMoveBrush, CheckBrush,
                                  WhitePieceFill, WhitePieceOutline, BlackPieceFill, BlackPieceOutline, MarkerBrush })
            if (b.CanFreeze) b.Freeze();
    }

    // ---- Connection ------------------------------------------------------------------

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting) return;
        if (_autoReconnecting) { StopAutoReconnect(reconnected: false); return; }   // button shows "Cancel"
        if (_connected) { Disconnect("Disconnected."); return; }
        await ConnectAsync();
    }

    /// <summary>Auto-find a Meshtastic device on the LAN by resolving its mDNS hostname (meshtastic.local) via
    /// the OS resolver. This opens no socket, so it triggers no Windows Firewall prompt — at the cost of only
    /// finding a single device (the generic hostname all WiFi devices share). Manual entry remains the fallback.</summary>
    private async void FindBtn_Click(object sender, RoutedEventArgs e)
    {
        FindBtn.IsEnabled = false;
        Status("Searching for a device (meshtastic.local)…");
        try
        {
            var ips = await ResolveHostnameAsync();
            if (ips.Count == 0)
            {
                Status("No device found automatically — enter the IP address manually.");
                MessageBox.Show(this,
                    "No Meshtastic device was found at meshtastic.local.\n\n" +
                    "Check that the device has WiFi enabled and is on the same network, then try Find again — " +
                    "or just type its IP address in the Host box.",
                    "No device found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            HostBox.Text = ips[0];
            Status(ips.Count == 1
                ? $"Found a device at {ips[0]} — click Connect."
                : $"Found {ips.Count} addresses: {string.Join(", ", ips)}. Using {ips[0]} — edit if needed, then Connect.");
        }
        catch (Exception ex) { Status($"Device search failed: {ex.Message}"); }
        finally { FindBtn.IsEnabled = !_connected && !_connecting; }
    }

    /// <summary>Resolves the generic Meshtastic mDNS hostname to its IPv4 address(es). Windows 10/11 resolve
    /// ".local" names via the OS mDNS resolver (no app socket → no firewall prompt). Empty if nothing answered.</summary>
    private static async Task<List<string>> ResolveHostnameAsync()
    {
        var found = new List<string>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var addrs = await System.Net.Dns.GetHostAddressesAsync("meshtastic.local", cts.Token);
            foreach (var a in addrs)
                if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !found.Contains(a.ToString()))
                    found.Add(a.ToString());
        }
        catch { /* not found / mDNS unavailable */ }
        return found;
    }

    private async Task ConnectAsync()
    {
        string host = HostBox.Text.Trim();
        if (host.Length == 0 || host == "http://")
        {
            MessageBox.Show(this, "Enter the device's address, e.g. http://192.168.1.50",
                "Host required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            host = "http://" + host;

        string ipHost;
        try { ipHost = new Uri(host).Host; }
        catch
        {
            MessageBox.Show(this, "That doesn't look like a valid address. Try e.g. http://192.168.1.50",
                "Host required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _connecting = true;
        _lastPollOkUtc = DateTime.UtcNow; _probeFailures = 0;   // give the first poll a chance before the TCP probe runs
        ApplyConnectionState();
        Status($"Connecting to {host} (timeout {ConnectTimeout.TotalSeconds:0}s)...");

        // Prefer the fast TCP stream API (port 4403, like the native app) — it streams the node DB in one burst.
        // Fall back to the HTTP REST API if 4403 is closed. Always sync with the device (no cache-only fast path).
        TcpStreamMeshTransport? tcp = null;
        try { tcp = await TcpStreamMeshTransport.ConnectAsync(ipHost, TcpStreamMeshTransport.DefaultPort, TimeSpan.FromSeconds(5)); }
        catch { tcp = null; }

        try
        {
            if (tcp != null)
            {
                try
                {
                    await FinishConnectAsync(new MeshtasticHttpClient(tcp), host, ipHost, TcpStreamMeshTransport.DefaultPort);
                    tcp = null;   // ownership handed off
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    tcp = null;   // FinishConnectAsync disposed it on failure
                    Status("Fast TCP connect failed — trying HTTP…");
                }
            }
            await FinishConnectAsync(new MeshtasticHttpClient(host), host, ipHost, SafePort(host, 80));
        }
        catch (OperationCanceledException)
        {
            Status($"Connection timed out after {ConnectTimeout.TotalSeconds:0}s. You can try again.");
            if (!_autoReconnecting)   // stay quiet during auto-reconnect retries — no pop-up every minute
                MessageBox.Show(this,
                    $"The device at {host} did not respond within {ConnectTimeout.TotalSeconds:0} seconds.\n\n" +
                    "Check it's powered on, on WiFi, and its API is enabled, then Connect again.",
                    "Connection timed out", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Status("Connection failed.");
            if (!_autoReconnecting)
                MessageBox.Show(this, $"Could not reach the device:\n{ex.Message}\n\n" +
                "Check it's on WiFi and its API is enabled, then Connect again.",
                "Connection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            tcp?.Dispose();         // dispose an unused TCP transport (only when both attempts were skipped/failed)
            _connecting = false;
            ApplyConnectionState();
        }
    }

    private static int SafePort(string url, int fallback)
    {
        try { var p = new Uri(url).Port; return p > 0 ? p : fallback; } catch { return fallback; }
    }

    // Runs the want-config handshake on a freshly-built client (over TCP or HTTP), then arms state + the
    // background backlog sync. Always syncs with the device. Disposes the client (and its transport) and
    // rethrows on failure so the caller can fall back or report it.
    private async Task FinishConnectAsync(MeshtasticHttpClient client, string host, string probeHost, int probePort)
    {
        try
        {
            using var cts = new CancellationTokenSource(ConnectTimeout);
            await client.InitializeAsync(cts.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _mesh?.Dispose();
        _mesh = client;
        _currentHost = host;
        _probeHost = probeHost;
        _probePort = probePort;
        _connected = true;
        _synced = false;
        AppSettings.LastHost = host;

        DeviceCache.Save(host, _mesh.GetAvailableChannels(), _mesh.MyNodeNum);
        LoadChannelPrefs(host, _mesh.GetAvailableChannels());
        ApplyConnectionState();
        _ = RunSyncAsync();
    }

    /// <summary>True when a valid chess channel is selected. Chess can't run on the primary channel (0), so if
    /// none is set this prompts the user to pick or create one in Channels and returns false.</summary>
    private bool EnsureChessChannelSelected()
    {
        if (_mesh != null && _mesh.ChannelIndex != 0) return true;
        MessageBox.Show(this,
            "No chess channel is set for this device.\n\n" +
            "Chess needs a dedicated secondary channel — it can't use the primary channel (0). " +
            "Open Channels… to pick an existing channel for chess, or create one, then start a game.",
            "Choose a chess channel", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void Disconnect(string statusMessage)
    {
        _pollTimer.Stop();
        _mesh?.Dispose();
        _mesh = null;
        _connected = false;
        _probeFailures = 0;
        _synced = false;
        _syncing = false;
        _playing = false;
        _gameOver = false;
        _selected = null;
        _legalForSelected.Clear();
        _lastMove = null;
        _pending.Clear();
        _chatAckers.Clear();
        _pendingGames.Clear();
        _joining = false;
        _joinGame = null;
        _resigning = false;
        _createInfo = null;
        _moveHistory.Clear();
        _loadedGame = null;
        _loadedName = "";
        _rxHidden.Clear();   // reset the RX view filter for the next device
        _unread.Clear();
        RefreshRxButton();

        // Reset the board to a clean starting position.
        _board = Board.CreateStartingPosition();
        _ply = 0;
        BuildBoard();
        Render();

        ApplyConnectionState();
        Status(statusMessage);
    }

    /// <summary>Single source of truth for which controls are enabled in each state.</summary>
    private void ApplyConnectionState()
    {
        ConnectBtn.Content = _connected ? "Disconnect" : _autoReconnecting ? "Cancel" : "Connect";
        ConnectBtn.IsEnabled = !_connecting;
        HostBox.IsEnabled = !_connected && !_connecting && !_autoReconnecting;
        FindBtn.IsEnabled = !_connected && !_connecting && !_autoReconnecting;

        // Chat, game setup and play are all available as soon as connected — you can start and
        // move during the background sync (the ack timer is paused while syncing, so nothing
        // false-fails). Channel refresh stays gated until "Ready" so it doesn't fight the drain.
        bool canConfigure = _connected && !_playing && !_refreshing && !_joining;
        // Channel config is allowed during a game too (the chess channel itself is locked while playing).
        ChannelsBtn.IsEnabled = _connected && !_refreshing && !_joining;
        // The Settings button is always available (Color/Sound need no connection); the Device section inside
        // it is gated on the connection state, computed when the hub opens.
        StartBtn.IsEnabled = canConfigure;
        ChatTxCombo.IsEnabled = _connected && _chatListen.Count > 0;
        ChatAckCheck.IsEnabled = _connected && _chatListen.Count > 0;
        RxButton.IsEnabled = _connected;

        ChatBox.IsEnabled = _connected;                       // you can compose the next message…
        EmojiBtn.IsEnabled = _connected;
        // …but only send once the last is acked/timed out, and after the brief post-receive ack hold.
        UpdateChatSendButton();
        NodesBtn.IsEnabled = _connected && !_refreshing;
        ResignBtn.IsEnabled = _connected && _playing && !_gameOver && !_resigning;
        SaveBtn.IsEnabled = _connected && _playing && !_gameOver;
        // Resend lights up only once a move's auto-retries are exhausted (awaiting manual resend).
        ResendBtn.IsEnabled = _connected && _pending.Values.Any(p => p.IsMove && p.NeedsResend);
        // Cancel is only offered while waiting for an opponent to join, or after
        // resigning while waiting for the opponent to acknowledge.
        CancelBtn.IsEnabled = _connected && _playing && !_gameOver && (_awaitingOpponent || _resigning);
    }

    /// <summary>Enables/labels the chat Send button. While a send is blocked — waiting for an ack/relay/reply, or
    /// during the brief post-receive ack hold — the button greys out and shows a live countdown of the seconds
    /// left. A CHATACK / relay / reply removes the pending early, which cancels the countdown and re-enables Send;
    /// if nothing is heard, the countdown reaches zero exactly as the wait gives up and Send re-enables anyway.
    /// Called from ApplyConnectionState and once a second from the ack timer so the number ticks down.</summary>
    private void UpdateChatSendButton()
    {
        var now = DateTime.UtcNow;
        var chat = _pending.Values.FirstOrDefault(p => p.IsChat);   // at most one chat in flight at a time
        bool inFlight = chat != null;

        // The latest of the two "blocked until" moments that currently applies.
        DateTime blockedUntil = _chatSendAllowedUtc;                 // post-receive ack hold
        if (inFlight && chat!.SendDeadlineUtc > blockedUntil) blockedUntil = chat.SendDeadlineUtc;

        ChatSendBtn.IsEnabled = _connected && !inFlight && now >= _chatSendAllowedUtc;
        int secsLeft = (int)Math.Ceiling((blockedUntil - now).TotalSeconds);
        ChatSendBtn.Content = _connected && !ChatSendBtn.IsEnabled && secsLeft > 0 ? $"{secsLeft}s" : "Send";
    }

    /// <summary>Opens the channel manager (create/delete channels, pick chess + chat-listen channels).
    /// Polling is paused while it's open so the dialog's admin writes can drain /fromradio for their acks
    /// without the poll loop stealing packets.</summary>
    private void ChannelsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null) return;

        bool wasPolling = _pollTimer.IsEnabled;
        _pollTimer.Stop();
        _refreshing = true;
        ApplyConnectionState();
        Status("Managing channels — polling paused.");
        try
        {
            var dlg = new ChannelsWindow(this, _mesh, _currentHost, _mesh.GetAvailableChannels(),
                                         _mesh.ChannelIndex, _chatListen, lockChessChannel: _playing,
                                         onClearChat: RemoveChatForChannel);
            dlg.ShowDialog();

            _mesh.ChannelIndex = dlg.ChessChannel;             // chess channel
            _chatListen.Clear();
            foreach (var i in dlg.ChatListen) _chatListen.Add(i);
            if (!_chatListen.Contains(_chatTxChannel))
                _chatTxChannel = _chatListen.Contains(dlg.ChessChannel) ? dlg.ChessChannel : _chatListen.FirstOrDefault();
            RebuildChatTxCombo();

            if (_currentHost.Length > 0)
            {
                // Refresh the cached channel list (enabled only) and persist the selections.
                DeviceCache.Save(_currentHost, dlg.Channels.Where(c => !c.IsDisabled), _mesh.MyNodeNum);
                DeviceCache.SaveChannelPrefs(_currentHost, _mesh.ChannelIndex, _chatListen, _chatTxChannel);
            }
            // The dialog may have changed per-channel ack settings — reload them.
            _chatAckOn.Clear();
            foreach (var i in DeviceCache.GetChatAckOn(_currentHost)) _chatAckOn.Add(i);
            _chatAckSignalOn.Clear();
            foreach (var i in DeviceCache.GetAckSignalOn(_currentHost)) _chatAckSignalOn.Add(i);
            LoadAckTriggers(_currentHost);
            UpdateChatAckCheck();   // reflect any ack change the dialog made to the TX channel
            Status($"Chess on channel [{_mesh.ChannelIndex}]; chat listening on {_chatListen.Count} channel(s).");
        }
        catch (Exception ex)
        {
            Status($"Channel manager error: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
            _lastRxUtc = DateTime.UtcNow; _rxStallWarned = false;   // polling was paused — re-arm the watchdog
            ApplyConnectionState();
            if (wasPolling) _pollTimer.Start();
        }
    }

    /// <summary>Opens the device settings editor (reads current config, writes changes via admin messages).
    /// Polling is paused while it's open so its fetch/admin writes can drain /fromradio for their acks.</summary>
    private void OpenDeviceSettings()
    {
        if (_mesh == null) return;

        bool wasPolling = _pollTimer.IsEnabled;
        _pollTimer.Stop();
        _refreshing = true;
        ApplyConnectionState();
        Status("Device settings — polling paused.");
        try
        {
            new DeviceSettingsWindow(this, _mesh).ShowDialog();
            Status("Closed device settings.");
        }
        catch (Exception ex) { Status($"Device settings error: {ex.Message}"); }
        finally
        {
            _refreshing = false;
            _lastRxUtc = DateTime.UtcNow; _rxStallWarned = false;   // polling was paused — re-arm the watchdog
            ApplyConnectionState();
            if (wasPolling) _pollTimer.Start();
        }
    }

    private void ChatTxCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ChatTxCombo.SelectedItem is not TxTarget t) return;
        if (t.IsDm)
        {
            // Send a direct message to this node; keep _chatTxChannel as the channel we stamp the DM with.
            _chatTxDest = t.Id;
        }
        else
        {
            _chatTxDest = null;
            _chatTxChannel = t.Id;
            if (_mesh != null && _currentHost.Length > 0)
                DeviceCache.SaveChannelPrefs(_currentHost, _mesh.ChannelIndex, _chatListen, _chatTxChannel);
        }
        UpdateChatAckCheck();   // reflect the new TX channel's ack setting
    }

    /// <summary>Toggles chat acks for the current TX channel. This is the same per-channel setting the
    /// channel manager exposes, so the two checkboxes always agree for a given channel.</summary>
    private void ChatAckCheck_Click(object sender, RoutedEventArgs e)
    {
        bool on = ChatAckCheck.IsChecked == true;
        if (on) _chatAckOn.Add(_chatTxChannel); else _chatAckOn.Remove(_chatTxChannel);
        if (_currentHost.Length > 0) DeviceCache.SetChatAck(_currentHost, _chatTxChannel, on);
        Status($"Chat acks {(on ? "on" : "off")} for channel [{_chatTxChannel}].");
    }

    /// <summary>Syncs the chat-area Ack checkbox to the current TX channel's ack setting.</summary>
    private void UpdateChatAckCheck() => ChatAckCheck.IsChecked = _chatAckOn.Contains(_chatTxChannel);

    /// <summary>Loads the per-channel auto-ack keyword lists for a device into <see cref="_ackTriggers"/>.</summary>
    private void LoadAckTriggers(string host)
    {
        _ackTriggers.Clear();
        foreach (var kv in DeviceCache.GetAckTriggers(host)) _ackTriggers[kv.Key] = new List<string>(kv.Value);
    }

    /// <summary>True when a received message's text (matched case-insensitively) matches any of the channel's
    /// auto-ack keywords — meaning we should reply with an RSSI ack even if acks are off for the channel. A keyword
    /// wrapped in double quotes ("ping") matches only when the whole message equals it; an unquoted keyword (ping)
    /// matches anywhere in the text (a substring), as before.</summary>
    private bool MatchesAckTrigger(uint channel, string text)
    {
        if (string.IsNullOrEmpty(text) || !_ackTriggers.TryGetValue(channel, out var triggers) || triggers.Count == 0)
            return false;
        string lower = text.ToLowerInvariant();
        string whole = lower.Trim();
        foreach (var t in triggers)
        {
            if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')   // "ping" → whole-message (exact) match
            {
                if (whole == t.Substring(1, t.Length - 2)) return true;
            }
            else if (lower.Contains(t)) return true;   // ping → substring match
        }
        return false;
    }

    private void ShowSignalCheck_Click(object sender, RoutedEventArgs e)
    {
        _showSignal = ShowSignalCheck.IsChecked == true;
        AppSettings.ShowSignal = _showSignal;
    }



    /// <summary>Loads saved channel/chat preferences for this device and applies them to the UI + client.</summary>
    private void LoadChannelPrefs(string host, IReadOnlyList<MeshChannel> channels)
    {
        _showSignal = AppSettings.ShowSignal;
        ShowSignalCheck.IsChecked = _showSignal;

        // Per-node DM/Block flags for this device.
        _nodePrefs.Clear();
        foreach (var kv in DeviceCache.GetNodePrefs(host))
            _nodePrefs[kv.Key] = new DeviceCache.NodePrefs { Dm = kv.Value.Dm, Block = kv.Value.Block };

        // Seed cached telemetry history so it's available immediately on (re)connect, before any live reading.
        if (_mesh != null)
        {
            var seed = new Dictionary<uint, List<MeshEnvironment>>();
            foreach (var kv in DeviceCache.GetTelemetry(host))
                seed[kv.Key] = kv.Value.Select(ToEnv).ToList();
            _mesh.SeedEnvironment(seed);

            // Seed cached node names/roles/hardware/positions so nodes the device has since forgotten still show
            // in the nodes list (and aren't dropped from the saved cache when we next persist the live set).
            var cached = DeviceCache.Get(host);
            if (cached != null)
                _mesh.SeedNodes(cached.NodeNames, cached.NodeRoles, cached.NodeHw,
                    cached.Positions.ToDictionary(kv => kv.Key, kv => (kv.Value.Lat, kv.Value.Lon, kv.Value.LastHeard, kv.Value.PosTime)));
        }

        // Per-channel chat-ack setting (default off; we store the on exceptions).
        _chatAckOn.Clear();
        foreach (var i in DeviceCache.GetChatAckOn(host)) _chatAckOn.Add(i);
        _chatAckSignalOn.Clear();
        foreach (var i in DeviceCache.GetAckSignalOn(host)) _chatAckSignalOn.Add(i);
        LoadAckTriggers(host);

        // Restore saved app-level AES keys for this device so chess/chat traffic is en/decrypted per channel.
        if (_mesh != null)
            foreach (var kv in DeviceCache.GetChannelKeys(host))
                _mesh.SetChannelKey(kv.Key, kv.Value);

        // Restore the utility/info channel (position/telemetry/node-info requests + manual broadcasts); null = auto.
        _mesh?.SetUtilityChannel(DeviceCache.GetUtilityChannel(host));

        var available = channels.Select(c => c.Index).ToHashSet();
        uint primary = channels.OrderBy(c => c.Index).Select(c => (uint?)c.Index).FirstOrDefault() ?? 0;
        var prefs = DeviceCache.GetChannelPrefs(host);

        uint chess = prefs?.ChessChannel ?? primary;
        if (!available.Contains(chess)) chess = primary;
        if (_mesh != null) _mesh.ChannelIndex = chess;

        // Keep all saved listen channels (the cached channel list can be incomplete on a cached connect,
        // so don't drop a valid selection just because it isn't in `available`).
        _chatListen.Clear();
        foreach (var i in prefs?.ChatListen ?? new List<uint>())
            _chatListen.Add(i);
        if (_chatListen.Count == 0) _chatListen.Add(chess);

        _chatTxChannel = prefs?.ChatTxChannel ?? chess;
        if (!_chatListen.Contains(_chatTxChannel)) _chatTxChannel = _chatListen.First();

        RebuildChatTxCombo();
        LoadCachedChat(host);
    }

    /// <summary>Renders the per-channel cached chat history (for the channels chat listens to) into the chat
    /// list on connect, oldest first, followed by a divider. No-op if chat already has rows (avoids dupes on
    /// reconnect).</summary>
    private void LoadCachedChat(string host)
    {
        if (host.Length == 0 || ChatList.Items.Count > 0) return;
        var chat = DeviceCache.GetChat(host);
        var msgs = new List<(DateTime Time, string Text, string Detail, uint Channel, string? Id)>();
        foreach (var ch in ReceiveChannels())   // all enabled channels (RX shows everything; the filter hides)
            if (chat.TryGetValue(ch, out var list))
                foreach (var m in list) msgs.Add((m.Time, m.Text, m.Detail, ch, m.Id));
        if (msgs.Count == 0) return;
        foreach (var m in msgs.OrderBy(m => m.Time))
        {
            var e = AddChatLine(m.Text, m.Detail, CachedText);   // grey — saved history from a previous session
            e.Channel = m.Channel;
            e.CacheId = m.Id;
        }
        var divider = AddChatLine("──────── saved history above · live messages below ────────", "",
            new SolidColorBrush(MediaColor.FromRgb(0x8A, 0x8A, 0x8A)));
        divider.Channel = uint.MaxValue;
    }

    /// <summary>Caches a chat line (latest 100 per channel) for the current device. Returns the stable id given
    /// to the cached copy (or null when not cached), so the caller can stamp it on the displayed row for later
    /// per-message removal.</summary>
    private string? CacheChat(uint channel, string text, string detail)
    {
        if (_currentHost.Length == 0 || !AppSettings.CacheMessages) return null;   // caching disabled in System settings
        string id = Guid.NewGuid().ToString("N");
        DeviceCache.AppendChat(_currentHost, channel, new DeviceCache.ChatMessage { Text = text, Detail = detail, Time = DateTime.Now, Id = id });
        return id;
    }

    /// <summary>Removes the displayed chat rows for one channel (used when its cache is deleted).</summary>
    private void RemoveChatForChannel(uint channel)
    {
        for (int i = ChatList.Items.Count - 1; i >= 0; i--)
            if (ChatList.Items[i] is LogEntry e && e.Channel == channel)
                ChatList.Items.RemoveAt(i);

        // If the only thing left is the "saved history above / live messages below" divider (Channel == MaxValue),
        // drop it too so the chat window isn't left with a lone divider and no messages.
        if (ChatList.Items.Count == 1 && ChatList.Items[0] is LogEntry { Channel: uint.MaxValue })
            ChatList.Items.Clear();
    }

    /// <summary>Repopulates the chat TX dropdown: the chat-listen channels (group messages) followed by any
    /// DM-enabled, non-blocked nodes (direct messages). Selecting a node sends a DM to it; selecting a
    /// channel broadcasts on it.</summary>
    /// <summary>The chat targets: the listened channels (group messages) followed by any DM-enabled, non-blocked
    /// nodes (direct messages). Shared by the TX dropdown and the RX (view-filter) dropdown.</summary>
    private List<TxTarget> GetTxTargets()
    {
        var names = _mesh?.GetAvailableChannels().ToDictionary(c => c.Index, c => c.DisplayName)
                    ?? new Dictionary<uint, string>();
        var items = _chatListen.OrderBy(i => i)
            .Select(i => new TxTarget(false, i, $"[{i}] {(names.TryGetValue(i, out var n) ? n : "")}".TrimEnd()))
            .ToList();

        // Append DM targets: nodes the user has flagged for DMs (and not blocked).
        if (_mesh != null)
        {
            var nodes = _mesh.GetNodes().Where(n => !n.IsSelf).ToDictionary(n => n.Num, n => n);
            foreach (var num in _nodePrefs.Where(kv => kv.Value.Dm && !kv.Value.Block)
                                          .Select(kv => kv.Key).OrderBy(n => n))
            {
                string label = nodes.TryGetValue(num, out var nd) ? NodeShortLabel(nd) : $"!{num:x8}";
                items.Add(new TxTarget(true, num, $"DM → {label}"));
            }
        }
        return items;
    }

    /// <summary>Every enabled device channel index (those configured in channel settings). Chat is received and
    /// shown for all of these; the RX filter then chooses which to display.</summary>
    private List<uint> ReceiveChannels() =>
        _mesh?.GetAvailableChannels().Where(c => !c.IsDisabled).Select(c => c.Index).OrderBy(i => i).ToList()
        ?? new List<uint>();

    private bool IsReceiveChannel(uint channel) =>
        _mesh?.GetAvailableChannels().Any(c => !c.IsDisabled && c.Index == channel) ?? false;

    /// <summary>The RX (view-filter) targets: ALL enabled device channels followed by the DM peers — independent
    /// of the TX channel set, so you can show/hide any channel the device has.</summary>
    private List<TxTarget> GetRxTargets()
    {
        var chans = _mesh?.GetAvailableChannels().Where(c => !c.IsDisabled).OrderBy(c => c.Index).ToList()
                    ?? new List<MeshChannel>();
        var items = chans.Select(c => new TxTarget(false, c.Index, $"[{c.Index}] {c.DisplayName}".TrimEnd())).ToList();
        if (_mesh != null)
        {
            var nodes = _mesh.GetNodes().Where(n => !n.IsSelf).ToDictionary(n => n.Num, n => n);
            foreach (var num in _nodePrefs.Where(kv => kv.Value.Dm && !kv.Value.Block).Select(kv => kv.Key).OrderBy(n => n))
            {
                string label = nodes.TryGetValue(num, out var nd) ? NodeShortLabel(nd) : $"!{num:x8}";
                items.Add(new TxTarget(true, num, $"DM → {label}"));
            }
        }
        return items;
    }

    private void RebuildChatTxCombo()
    {
        var items = GetTxTargets();
        ChatTxCombo.ItemsSource = items;
        ChatTxCombo.DisplayMemberPath = nameof(TxTarget.Label);

        // Drop a stale DM target whose node is no longer DM-enabled.
        if (_chatTxDest.HasValue && !items.Any(it => it.IsDm && it.Id == _chatTxDest.Value))
            _chatTxDest = null;

        TxTarget? sel = _chatTxDest.HasValue
            ? items.FirstOrDefault(it => it.IsDm && it.Id == _chatTxDest.Value)
            : items.FirstOrDefault(it => !it.IsDm && it.Id == _chatTxChannel) ?? items.FirstOrDefault(it => !it.IsDm);
        ChatTxCombo.SelectedItem = sel;
        if (sel is { IsDm: false }) _chatTxChannel = sel.Id;
        UpdateChatAckCheck();
        RefreshRxButton();
        ApplyConnectionState();
    }

    // ---- RX view filter (which channels/DMs are shown in chat) -----------------------

    private static readonly Brush UnreadBadge = new SolidColorBrush(MediaColor.FromRgb(0x7F, 0xC8, 0xE8));

    /// <summary>The RX target a chat row belongs to: a DM peer when it's a DM, else its channel.</summary>
    private static (bool IsDm, uint Id) RxKey(LogEntry e) => e.DmPeer != 0 ? (true, e.DmPeer) : (false, e.Channel);

    /// <summary>Stamps a chat row's RX target, sets whether it's shown (per the filter), and — for an incoming
    /// message on a hidden target — bumps that target's unread count. Returns whether the row is shown.</summary>
    private bool RouteRx(LogEntry e, bool isDm, uint peer, bool incoming)
    {
        e.DmPeer = isDm ? peer : 0;
        var key = isDm ? (true, peer) : (false, e.Channel);
        bool shown = !_rxHidden.Contains(key);
        e.Visible = shown;
        if (incoming && !shown)
        {
            _unread[key] = _unread.GetValueOrDefault(key) + 1;
            RefreshRxButton();
        }
        return shown;
    }

    private void RecomputeChatVisibility()
    {
        foreach (var item in ChatList.Items)
            if (item is LogEntry e && e.Channel != uint.MaxValue)   // dividers/system rows always show
                e.Visible = !_rxHidden.Contains(RxKey(e));
    }

    private void SetRxHidden(bool isDm, uint id, bool hidden)
    {
        var key = (isDm, id);
        if (hidden) _rxHidden.Add(key);
        else { _rxHidden.Remove(key); _unread.Remove(key); }   // showing a target clears its unread
        RecomputeChatVisibility();
        RefreshRxButton();
    }

    private void RefreshRxButton()
    {
        int total = _unread.Values.Sum();
        RxButton.Content = total > 0 ? $"Show ▾  ● {total}" : "Show ▾";
    }

    /// <summary>Deletes all chat messages for one RX target (a channel or a DM peer) — both the rows shown in the
    /// chat window and the cached history for this device.</summary>
    private void DeleteChatTarget(bool isDm, uint id)
    {
        // A channel's whole cached bucket can be wiped in one go; DM rows live mixed in their arrival channel's
        // bucket, so those are removed one at a time by their cached id.
        if (!isDm && _currentHost.Length > 0) DeviceCache.ClearChat(_currentHost, id);

        for (int i = ChatList.Items.Count - 1; i >= 0; i--)
        {
            if (ChatList.Items[i] is not LogEntry e || e.Channel == uint.MaxValue) continue;   // keep dividers/system
            bool match = isDm ? e.DmPeer == id : (e.DmPeer == 0 && e.Channel == id);
            if (!match) continue;
            if (isDm && _currentHost.Length > 0)
                DeviceCache.RemoveChat(_currentHost, e.Channel, e.CacheId, e.Text, e.Detail);
            if (e.PacketId != 0)
            {
                _chatEntryById.Remove(e.PacketId);
                _chatById.Remove(e.PacketId);
                _pending.Remove(e.PacketId);
                _chatAckers.Remove(e.PacketId);
            }
            ChatList.Items.RemoveAt(i);
        }
        _unread.Remove((isDm, id));
        RefreshRxButton();
        // Don't leave a lone "saved history above / live below" divider behind.
        if (ChatList.Items.Count == 1 && ChatList.Items[0] is LogEntry { Channel: uint.MaxValue }) ChatList.Items.Clear();
    }

    private void RxButton_Click(object sender, RoutedEventArgs e)
    {
        BuildRxList();
        RxPopup.IsOpen = true;
    }

    /// <summary>Builds the RX checklist popup: All/None, then a checkbox per channel/DM with its unread badge.</summary>
    private void BuildRxList()
    {
        RxList.Children.Clear();
        RxList.Children.Add(new TextBlock
        {
            Text = "Choose what's shown in chat. ● marks unread on hidden ones. 🗑 deletes all messages on that channel/DM.",
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xB0, 0xB0, 0xB0)),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 240, Margin = new Thickness(0, 0, 0, 6),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var allBtn = new Button { Content = "All", Width = 54, Height = 22, Margin = new Thickness(0, 0, 4, 0) };
        var noneBtn = new Button { Content = "None", Width = 54, Height = 22 };
        var targets = GetRxTargets();   // all device channels + DM peers
        allBtn.Click += (_, _) => { _rxHidden.Clear(); _unread.Clear(); RecomputeChatVisibility(); RefreshRxButton(); BuildRxList(); };
        noneBtn.Click += (_, _) => { foreach (var t in targets) _rxHidden.Add((t.IsDm, t.Id)); RecomputeChatVisibility(); RefreshRxButton(); BuildRxList(); };
        buttons.Children.Add(allBtn);
        buttons.Children.Add(noneBtn);
        RxList.Children.Add(buttons);

        foreach (var t in targets)
        {
            var key = (t.IsDm, t.Id);
            var target = t;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            // Right-docked: a delete (🗑) button, then the unread badge.
            var del = new Button { Content = "🗑", Width = 24, Height = 22, Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Delete all messages on this channel/DM (shown and cached)" };
            del.Click += (_, _) => DeleteChatTargetPrompt(target);
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            int unread = _unread.GetValueOrDefault(key);
            if (unread > 0)
            {
                var badge = new TextBlock { Text = $"● {unread}", Foreground = UnreadBadge, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                DockPanel.SetDock(badge, Dock.Right);
                row.Children.Add(badge);
            }

            var cb = new CheckBox { Content = t.Label, Foreground = NormalText, IsChecked = !_rxHidden.Contains(key), VerticalAlignment = VerticalAlignment.Center };
            cb.Checked += (_, _) => SetRxHidden(target.IsDm, target.Id, false);
            cb.Unchecked += (_, _) => SetRxHidden(target.IsDm, target.Id, true);
            row.Children.Add(cb);   // fills the remaining width
            RxList.Children.Add(row);
        }
    }

    private void DeleteChatTargetPrompt(TxTarget t)
    {
        if (MessageBox.Show(this, $"Delete all messages on {t.Label}?\n\nThis removes them from the chat window and the saved history on this PC.",
                "Delete messages", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DeleteChatTarget(t.IsDm, t.Id);
        BuildRxList();   // refresh the popup (badges/state)
        Status($"Deleted messages on {t.Label}.");
    }

    /// <summary>A compact name for a node in the TX dropdown: short name, else long name, else !hex.</summary>
    private static string NodeShortLabel(MeshNode n) =>
        !string.IsNullOrWhiteSpace(n.ShortName) ? n.ShortName
        : !string.IsNullOrWhiteSpace(n.LongName) ? n.LongName
        : $"!{n.Num:x8}";

    /// <summary>A chat TX target: either a channel (group broadcast) or a node (direct message).
    /// When <see cref="IsDm"/> is false, <see cref="Id"/> is a channel index; when true, it's a node number.</summary>
    private sealed record TxTarget(bool IsDm, uint Id, string Label);

    /// <summary>Turns on the DM flag for a node (so it appears as a TX target and we can reply), unless it's
    /// blocked or already enabled. Called when a DM arrives from a node we hadn't DM-enabled yet.</summary>
    private void EnsureDmEnabled(uint nodeNum)
    {
        var pref = _nodePrefs.GetValueOrDefault(nodeNum);
        if (pref is { Block: true } || pref is { Dm: true }) return;   // blocked, or already enabled
        _nodePrefs[nodeNum] = new DeviceCache.NodePrefs { Dm = true, Block = false };
        if (_currentHost.Length > 0) DeviceCache.SetNodePref(_currentHost, nodeNum, dm: true, block: false);
        RebuildChatTxCombo();   // list the node so the user can reply with a DM
    }

    // ---- Start / create / join game --------------------------------------------------

    private enum StartChoice { New, Load, Join, Cancel }

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null) return;
        if (!EnsureChessChannelSelected()) return;   // chess needs a non-primary channel to host or join

        // The Chess window offers: start fresh, host a save, or join an open game (asks game id + role for the first two).
        var (choice, gameId, role) = AskNewOrLoad();
        switch (choice)
        {
            case StartChoice.Cancel:
                return;
            case StartChoice.Join:
                ShowJoinDialog();                  // pick an open game to join — no game id/role needed
                return;
            case StartChoice.Load:
                if (!LoadGameFromFile()) return;   // user cancelled the picker or the file was unreadable
                break;
            default:                               // New: discard any previously-loaded preview
                _loadedGame = null;
                _loadedName = "";
                break;
        }

        StartHostedGame(gameId, role);
    }

    /// <summary>Hosts a game — a fresh one, or the currently-loaded save if there is one.</summary>
    private void StartHostedGame(string gameId, string role)
    {
        if (_mesh == null) return;
        // Chess uses _mesh.ChannelIndex (the chess channel), chosen in the channel manager.

        // A loaded game fixes your colour; otherwise White/Black give that colour and Auto picks at random.
        gameId = gameId.Trim();
        if (gameId.Length == 0) gameId = NewGameId();
        GameSave? load = _loadedGame;
        GameColor color = load != null
            ? (load.MyColor.Equals("black", StringComparison.OrdinalIgnoreCase) ? GameColor.Black : GameColor.White)
            : role == RoleWhite ? GameColor.White
            : role == RoleBlack ? GameColor.Black
            : (Random.Shared.Next(2) == 0 ? GameColor.White : GameColor.Black);

        string saveName = load != null ? _loadedName : "";
        BeginGame(color, gameId, load?.Fen, awaitingOpponent: true);
        if (load != null)
            AddSystem(Stamp() + $"— Hosting loaded game '{_loadedName}' as {color}. Opponents who join receive the position. —");
        _loadedGame = null;
        _loadedName = "";
        AnnounceCreate(gameId, color, saveName);
    }

    /// <summary>Asks for the game id, role, and whether to create a new game or load an existing one.</summary>
    private (StartChoice choice, string gameId, string role) AskNewOrLoad()
    {
        var result = StartChoice.Cancel;
        var dialog = new Window
        {
            Title = "Chess",
            Owner = this,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x25, 0x26)),
        };
        var light = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xE0, 0xE0));
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Start a new game, host a saved game, or join an open game. " +
                   "(Game id and role apply when creating or hosting.)",
            Foreground = light,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(new TextBlock { Text = "Game id:", Foreground = light, Margin = new Thickness(0, 0, 0, 2) });
        var idBox = new TextBox { Text = NewGameId(), Height = 24, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(idBox);

        panel.Children.Add(new TextBlock { Text = "Role:", Foreground = light, Margin = new Thickness(0, 0, 0, 2) });
        var roleBox = new ComboBox { Height = 24, Margin = new Thickness(0, 0, 0, 14), ItemsSource = new[] { RoleAuto, RoleWhite, RoleBlack }, SelectedIndex = 0 };
        panel.Children.Add(roleBox);

        var newBtn = new Button { Content = "Create new game", Height = 34, Margin = new Thickness(0, 0, 0, 8), IsDefault = true };
        var loadBtn = new Button { Content = "Load existing game", Height = 34, Margin = new Thickness(0, 0, 0, 8) };
        var joinBtn = new Button { Content = "Join an open game", Height = 34, Margin = new Thickness(0, 0, 0, 12) };
        var cancelBtn = new Button { Content = "Cancel", Height = 26, Width = 90, HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        newBtn.Click += (_, _) => { result = StartChoice.New; dialog.Close(); };
        loadBtn.Click += (_, _) => { result = StartChoice.Load; dialog.Close(); };
        joinBtn.Click += (_, _) => { result = StartChoice.Join; dialog.Close(); };
        cancelBtn.Click += (_, _) => { result = StartChoice.Cancel; dialog.Close(); };
        panel.Children.Add(newBtn);
        panel.Children.Add(loadBtn);
        panel.Children.Add(joinBtn);
        panel.Children.Add(cancelBtn);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { idBox.Focus(); idBox.SelectAll(); };   // id field ready to type on open
        dialog.ShowDialog();
        return (result, idBox.Text.Trim(), (roleBox.SelectedItem as string) ?? RoleAuto);
    }

    /// <summary>Broadcasts the new-game announcement and waits for it to be acknowledged.</summary>
    private async void AnnounceCreate(string gameId, GameColor color, string saveName = "")
    {
        if (_mesh == null) return;
        string resume = string.IsNullOrEmpty(saveName) ? "" : $" resuming '{saveName}'";
        string baseText = Stamp() + $"Created game '{gameId}' as {color}{resume} (channel {_mesh.ChannelIndex})";
        _createInfo = new ChatAckInfo { Entry = AddSystem(baseText + MarkSending), BaseText = baseText };
        _createInfo.Entry.Foreground = PendingText;   // yellow while awaiting acknowledgement

        string payload = ProtocolMessage.EncodeNew(gameId, color, saveName);
        try
        {
            uint id = await _mesh.SendTextAsync(payload);
            _pending[id] = new PendingSend
            {
                Id = id,
                Payload = payload,
                LastSentUtc = DateTime.UtcNow,
                Attempts = 1,
                IsCreate = true,
                Label = $"game '{gameId}'",
            };
            Status($"Game '{gameId}' created as {color} — waiting for acknowledgement…");
        }
        catch (Exception ex)
        {
            FailCreate($"Could not announce the game: {ex.Message}");
        }
    }

    /// <summary>A recipient acknowledged our new game — record the acker and confirm creation.</summary>
    private void HandleCreateAck(ProtocolMessage pm, MeshTextMessage msg)
    {
        if (_createInfo == null || pm.GameId != _gameId) return;
        if (!_createInfo.Ackers.Add(msg.FromNode)) return;

        // First acknowledgement confirms the game was heard — stop the retry/fail timer.
        var cp = _pending.Values.FirstOrDefault(p => p.IsCreate);
        if (cp != null) _pending.Remove(cp.Id);

        string names = string.Join(", ", _createInfo.Ackers.Select(a => _mesh?.DescribeNode(a) ?? a.ToString()));
        _createInfo.Entry.Text = $"{_createInfo.BaseText} {MarkDelivered.Trim()} acked by: {names}";
        _createInfo.Entry.Foreground = AckedText;   // green — acknowledged
        Status($"Game '{_gameId}' acknowledged. Waiting for an opponent to join…");
    }

    private void FailCreate(string reason)
    {
        var cp = _pending.Values.FirstOrDefault(p => p.IsCreate);
        if (cp != null) _pending.Remove(cp.Id);
        string gid = _gameId;
        if (_createInfo != null)
        {
            _createInfo.Entry.Text = $"{_createInfo.BaseText} {MarkFailed.Trim()} — not acknowledged; game not created";
            _createInfo.Entry.Foreground = WarningText;   // red — creation not acknowledged
        }
        _createInfo = null;

        // Leave the unconfirmed game and return to the lobby so the user can try again.
        _playing = false;
        _gameOver = false;
        _awaitingOpponent = false;
        _gameId = "";
        _board = Board.CreateStartingPosition();
        _ply = 0;
        _moveHistory.Clear();
        _selected = null;
        _legalForSelected.Clear();
        _lastMove = null;
        BuildBoard();
        Render();
        ApplyConnectionState();
        Status("Game creation not acknowledged — try creating again.");
        ShowNotice("No acknowledgement",
            $"No one acknowledged your new game '{gid}'.\n\n{reason}\n\nTry creating it again.");
    }

    private static string NewGameId() => Guid.NewGuid().ToString("N").Substring(0, 4);


    private async void JoinGame(PendingGame game)
    {
        if (_mesh == null) return;

        // No file needed — you take the opposite colour of the host and receive the board over the air.
        GameColor myColor = game.CreatorColor.Opposite();
        _mesh.ChannelIndex = game.Channel;              // join on the channel it was announced on
        _pendingGames.Remove(game.GameId);

        // Don't enter the game until the host replies with the board (BOARD doubles as the join ack).
        _joining = true;
        _joinGame = game;
        _joinColor = myColor;
        ApplyConnectionState();

        string payload = ProtocolMessage.EncodeJoin(game.GameId, myColor);
        try
        {
            uint id = await _mesh.SendTextAsync(payload);
            _pending[id] = new PendingSend
            {
                Id = id,
                Payload = payload,
                LastSentUtc = DateTime.UtcNow,
                Attempts = 1,
                IsJoin = true,
                Label = $"join '{game.GameId}'",
            };
            Status($"Joining {_mesh.DescribeNode(game.CreatorNode)}'s game '{game.GameId}' as {myColor} — " +
                   "waiting for the host to send the board…");
        }
        catch (Exception ex)
        {
            FailJoin($"Could not send join request: {ex.Message}");
        }
    }

    /// <summary>Host replied to our JOIN with the board — enter the game using the received position.</summary>
    private void HandleBoard(ProtocolMessage pm)
    {
        if (!_joining || _joinGame == null || pm.GameId != _joinGame.GameId) return;
        var joinEntry = _pending.Values.FirstOrDefault(p => p.IsJoin);
        if (joinEntry != null) _pending.Remove(joinEntry.Id);

        var game = _joinGame;
        var color = pm.AnnouncedColor ?? _joinColor;   // colour assigned by the host
        _joining = false;
        _joinGame = null;

        BeginGame(color, game.GameId, pm.Fen);
        AddSystem(Stamp() + $"— Joined {_mesh?.DescribeNode(game.CreatorNode)}'s game '{game.GameId}' as {color} " +
                "(board received from host). —");
        Status($"Joined game '{game.GameId}' — you are {color}.");
    }

    private void FailJoin(string reason)
    {
        var joinEntry = _pending.Values.FirstOrDefault(p => p.IsJoin);
        if (joinEntry != null) _pending.Remove(joinEntry.Id);
        string gameId = _joinGame?.GameId ?? "";
        _joining = false;
        _joinGame = null;
        ApplyConnectionState();
        Status($"Join failed — no acknowledgement. Try joining again.");
        ShowNotice("No acknowledgement",
            $"No acknowledgement from the host for game '{gameId}'.\n\n{reason}\n\nPlease try joining again.");
    }

    /// <summary>Sets up the board/state for a game. <paramref name="fen"/> is the starting position
    /// (from a loaded file or received from the host); null/empty means a fresh starting position.</summary>
    private void BeginGame(GameColor color, string gameId, string? fen = null,
                           bool awaitingOpponent = false)
    {
        _awaitingOpponent = awaitingOpponent;
        _myColor = color;
        _gameId = gameId;

        _moveHistory.Clear();   // position-based: no replayable history; the move list starts here
        MoveList.Items.Clear();
        if (!string.IsNullOrWhiteSpace(fen))
        {
            _board = Board.FromFen(fen);
            _ply = _board.HalfMovesPlayed();
        }
        else
        {
            _board = Board.CreateStartingPosition();
            _ply = 0;
        }
        _lastMove = null;
        _selected = null;
        _legalForSelected.Clear();
        _gameOver = false;
        _playing = true;
        _pending.Clear();
        _createInfo = null;
        _pendingGames.Remove(gameId);   // we're in this game now

        ApplyConnectionState();
        BuildBoard();
        Render();
        UpdateTurnStatus();
    }

    /// <summary>Fire-and-forget broadcast of a control message (game announcement / join). Acks pass
    /// <paramref name="encrypt"/> = false so they travel as plaintext even when a channel key is set.</summary>
    private async void SendControl(string text, bool encrypt = true, bool delay = false)
    {
        if (_mesh == null) return;
        if (delay) await AckJitterDelay();
        try { await _mesh.SendTextAsync(text, encrypt: encrypt); }
        catch { /* best effort */ }
    }

    // ---- Cancel game -----------------------------------------------------------------

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_playing || _gameOver || !(_awaitingOpponent || _resigning)) return;
        if (MessageBox.Show(this, "Cancel this game?", "Cancel game",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        CancelGame($"Game '{_gameId}' cancelled.");
    }

    /// <summary>Abandons the current game and returns to the lobby, telling the channel.</summary>
    private void CancelGame(string status)
    {
        if (_gameId.Length > 0) SendControl(ProtocolMessage.EncodeCancel(_gameId));
        LeaveGame(status);
    }

    /// <summary>Returns to the lobby (not in a game), keeping the connection and chat.</summary>
    private void LeaveGame(string status)
    {
        // Drop game-related pending sends (keep chat acks, which are channel-level).
        foreach (var key in _pending.Where(kv => kv.Value.IsMove || kv.Value.IsJoin || kv.Value.IsResign
                                                || kv.Value.IsCreate || kv.Value.IsSave)
                                    .Select(kv => kv.Key).ToList())
            _pending.Remove(key);

        _playing = false;
        _gameOver = false;
        _resigning = false;
        _awaitingOpponent = false;
        _createInfo = null;
        _gameId = "";
        _board = Board.CreateStartingPosition();
        _ply = 0;
        _moveHistory.Clear();
        _selected = null;
        _legalForSelected.Clear();
        _lastMove = null;
        BuildBoard();
        Render();
        ApplyConnectionState();
        Status(status);
    }

    /// <summary>An opponent cancelled a game we're in, or a lobby game we knew about.</summary>
    private void HandleCancel(ProtocolMessage pm)
    {
        if (pm.GameId == _gameId && _playing)
        {
            LeaveGame("Opponent cancelled the game.");
            AddSystem(Stamp() + "— Opponent cancelled the game. —");
            return;
        }
        if (_pendingGames.Remove(pm.GameId))
        {
            AddSystem(Stamp() + $"— Game '{pm.GameId}' was cancelled by its host. —");
            ApplyConnectionState();
        }
    }

    /// <summary>Opponent replied that the game we moved in is no longer running on their side
    /// (they ended it). End it here too, so we stop trying to resend the move.</summary>
    private void HandleEnded(ProtocolMessage pm)
    {
        if (pm.GameId != _gameId || !_playing || _gameOver) return;
        // Drop any unacknowledged move(s) — there's no opponent left to ack them.
        foreach (var key in _pending.Where(kv => kv.Value.IsMove).Select(kv => kv.Key).ToList())
            _pending.Remove(key);
        AddSystem(Stamp() + $"— Opponent reports game '{pm.GameId}' has already ended on their side. —");
        EndGame("Opponent has ended the game.");
    }

    // ---- Save / load game ------------------------------------------------------------

    private GameSave CurrentSave() => new()
    {
        Fen = _board.ToFen(),
        MyColor = _myColor == GameColor.White ? "white" : "black",
    };

    private static string SanitizeFileName(string name)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace("|", "_").Trim();
    }

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_playing || _gameOver || _mesh == null) return;
        if (_pending.Values.Any(p => p.IsSave)) return;   // a save is already pending

        string? name = InputDialog.Ask(this, "Save game",
            "Filename for the saved game (both players save it under this name):", _gameId);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = SanitizeFileName(name!);

        // Save our own copy right now — it's self-contained and must never depend on the opponent.
        bool savedOk = GameStorage.Save(GameStorage.PathFor(name), CurrentSave());

        // Invite the opponent to save a copy and end too. Log the request as a system message so it's
        // clear it was sent; that line is updated when the opponent acks (green) / declines / never
        // answers (red).
        string baseText = Stamp() + $"Save request '{name}' sent to opponent — awaiting acknowledgement";
        LogEntry reqEntry = AddSystem(baseText + MarkSending);
        reqEntry.Foreground = PendingText;   // yellow while awaiting
        string payload = ProtocolMessage.EncodeSave(_gameId, name);
        try
        {
            uint id = await _mesh.SendTextAsync(payload);
            _pending[id] = new PendingSend
            {
                Id = id,
                Payload = payload,
                LastSentUtc = DateTime.UtcNow,
                Attempts = 1,
                IsSave = true,
                SaveFileName = name,
                Label = $"save '{name}'",
                Entry = reqEntry,
                BaseText = baseText,
            };
        }
        catch (Exception ex)
        {
            reqEntry.Text = baseText + MarkFailed + $" — could not send ({ex.Message})";
            reqEntry.Foreground = WarningText;
        }

        // End OUR game immediately — whether or not it's our move, without waiting for the opponent.
        EndGame(savedOk ? $"Saved as '{name}' and ended the game — opponent invited to save a copy."
                        : $"Game ended, but writing your copy of '{name}' failed.");
    }

    /// <summary>Opponent asked to save the game — prompt, save our copy, and acknowledge.</summary>
    private void HandleSaveRequest(ProtocolMessage pm, MeshTextMessage msg)
    {
        if (pm.GameId != _gameId || !_playing || _gameOver) return;

        string who = _mesh?.DescribeNode(msg.FromNode) ?? "Opponent";
        string saveName = pm.SaveName;
        ShowConfirm("Game ended",
            $"{who} ended the game and saved it as '{saveName}'.\n\n" +
            "The game ends now either way. Do you also want to save a copy on your side?",
            "Save a copy", "End without saving",
            onYes: () =>
            {
                bool ok = GameStorage.Save(GameStorage.PathFor(saveName), CurrentSave());
                SendControl(ProtocolMessage.EncodeSaveAck(_gameId, ok), encrypt: false, delay: true);
                EndGame(ok ? $"Game saved as '{saveName}'."
                           : $"Game ended, but writing your copy of '{saveName}' failed.");
            },
            onNo: () =>
            {
                // Declined the file only — the game still ends on this side.
                SendControl(ProtocolMessage.EncodeSaveAck(_gameId, false), encrypt: false, delay: true);
                AddSystem(Stamp() + $"— Declined to save '{saveName}'. Game ended. —");
                EndGame($"Game ended without saving (declined '{saveName}').");
            });
    }

    /// <summary>Opponent answered our save request. Our game already ended when we requested the
    /// save (and our copy is already written), so this only reports their decision in the system log.</summary>
    private void HandleSaveAck(ProtocolMessage pm)
    {
        var sp = _pending.Values.FirstOrDefault(p => p.IsSave);
        if (sp == null || pm.GameId != _gameId) return;
        _pending.Remove(sp.Id);

        if (sp.Entry != null)
        {
            // The request WAS acknowledged either way; the flag only says whether they kept a copy.
            sp.Entry.Text = sp.BaseText + $" {MarkDelivered.Trim()} " +
                (pm.SaveOk ? "opponent saved their copy" : "opponent declined (your copy is saved)");
            sp.Entry.Foreground = pm.SaveOk ? AckedText : NormalText;   // green if they saved
        }
        Status(pm.SaveOk ? $"Opponent saved '{sp.SaveFileName}' too."
                         : $"Opponent declined to save '{sp.SaveFileName}' (your copy is saved).");
    }

    private void FailSave(string reason)
    {
        var sp = _pending.Values.FirstOrDefault(p => p.IsSave);
        if (sp == null) return;
        _pending.Remove(sp.Id);

        // Our game already ended (and our copy is saved) when we requested the save; just mark the
        // request line as unacknowledged.
        if (sp.Entry != null)
        {
            sp.Entry.Text = sp.BaseText + $" {MarkFailed.Trim()} not acknowledged by opponent";
            sp.Entry.Foreground = WarningText;   // red
        }
        Status($"Opponent didn't acknowledge the save of '{sp.SaveFileName}' (your copy is saved).");
    }

    /// <summary>Shows a file picker and loads a saved game into the loaded-game state (with a board
    /// preview). Returns true if a game was loaded; false if the user cancelled or the file was bad.</summary>
    private bool LoadGameFromFile()
    {
        Directory.CreateDirectory(GameStorage.DefaultFolder);
        var dlg = new OpenFileDialog
        {
            Title = "Load a saved game",
            InitialDirectory = GameStorage.DefaultFolder,
            Filter = "Chess saves (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return false;

        var game = GameStorage.Load(dlg.FileName);
        if (game == null)
        {
            MessageBox.Show(this, "Couldn't read that save file.", "Load failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _loadedGame = game;
        _loadedName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        bool black = game.MyColor.Equals("black", StringComparison.OrdinalIgnoreCase);
        _myColor = black ? GameColor.Black : GameColor.White;   // a loaded game's colour comes from the file, not the Role field

        // Preview the loaded position (not yet playing).
        _board = GameStorage.Rebuild(game, out int ply);
        _ply = ply;
        _lastMove = null;
        BuildBoard();
        Render();
        return true;
    }

    // ---- Lobby (open games) ----------------------------------------------------------

    private void HandleGameAnnounced(ProtocolMessage pm, MeshTextMessage msg)
    {
        // Ignore announcements for a game we're actively in. A *finished* game must not suppress this
        // (the next game may reuse the same id), or the creator would never get our acknowledgement.
        if (pm.GameId == _gameId && _playing) return;

        // Acknowledge the announcement so the creator knows it was heard (creator shows the ackers).
        SendControl(ProtocolMessage.EncodeCreateAck(pm.GameId), encrypt: false, delay: true);

        bool isNew = !_pendingGames.ContainsKey(pm.GameId);
        var creatorColor = pm.AnnouncedColor ?? GameColor.White;
        string saveName = pm.SaveName ?? "";
        _pendingGames[pm.GameId] = new PendingGame(pm.GameId, msg.FromNode, creatorColor, msg.Channel, saveName);
        if (isNew)   // avoid re-announcing in chat on the creator's retries
        {
            string resumes = string.IsNullOrEmpty(saveName) ? "" : $" (resuming '{saveName}')";
            AddSystem(Stamp() + $"{_mesh?.DescribeNode(msg.FromNode)} started game '{pm.GameId}'{resumes} (they're {creatorColor}). " +
                $"Click Join… to play as {creatorColor.Opposite()} — the board comes to you.");
        }
        ApplyConnectionState();
    }

    private void HandleGameJoined(ProtocolMessage pm, MeshTextMessage msg)
    {
        _pendingGames.Remove(pm.GameId);                  // no longer open
        if (pm.GameId == _gameId && _playing)
        {
            // We're the host — reply with the current board so the joiner can play on without a file.
            // BOARD doubles as the join acknowledgement; the joiner plays the opposite colour.
            SendControl(ProtocolMessage.EncodeBoard(_gameId, _myColor.Opposite(), _board.ToFen()), encrypt: false);
            _awaitingOpponent = false;   // opponent is here — the game is on, no longer cancellable
            AddSystem(Stamp() + $"{_mesh?.DescribeNode(msg.FromNode)} joined as {_myColor.Opposite()} — sent them the board. Game on!");
            UpdateTurnStatus();
        }
        ApplyConnectionState();
    }

    private void ShowJoinDialog()
    {
        if (_mesh == null) return;

        var dialog = new Window
        {
            Title = "Join a game",
            Owner = this,
            Width = 420,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x25, 0x26)),
        };
        var root = new DockPanel { Margin = new Thickness(10) };
        var header = new TextBlock
        {
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xE0, 0xE0)),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(header, Dock.Top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var joinBtn = new Button { Content = "Join selected", Width = 110, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
        var closeBtn = new Button { Content = "Close", Width = 80, Height = 28 };
        buttons.Children.Add(joinBtn);
        buttons.Children.Add(closeBtn);

        var list = new ListBox
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderThickness = new Thickness(0),
        };

        // The games currently shown in the list (kept in sync with the ItemsSource, so a selected
        // index always maps to the right game even if the lobby changes between open and click).
        var shown = new List<PendingGame>();
        void Populate()
        {
            shown = _pendingGames.Values.ToList();
            list.ItemsSource = null;
            list.ItemsSource = shown.Select(g =>
                $"'{g.GameId}' by {_mesh!.DescribeNode(g.CreatorNode)} — they're {g.CreatorColor}, you'd be {g.CreatorColor.Opposite()}")
                .ToList();
            header.Text = shown.Count == 0
                ? "No open games. Wait for someone to create one."
                : $"Open games: {shown.Count}  —  the board is sent to you, no file needed.";
        }

        Populate();

        joinBtn.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= shown.Count) { Populate(); return; }
            var chosen = shown[i];
            dialog.Close();
            JoinGame(chosen);   // no file required — the host transmits the board on join
        };
        closeBtn.Click += (_, _) => dialog.Close();

        root.Children.Add(header);
        root.Children.Add(buttons);
        root.Children.Add(list);
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private async void ResignBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_playing || _gameOver || _resigning || _mesh == null) return;
        if (MessageBox.Show(this, "Resign this game?", "Resign",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        // Send RESIGN and wait for the opponent's RESIGNACK before ending the game.
        _resigning = true;
        ApplyConnectionState();
        string payload = ProtocolMessage.EncodeResign(_gameId, _myColor);
        try
        {
            uint id = await _mesh.SendTextAsync(payload);
            _pending[id] = new PendingSend
            {
                Id = id,
                Payload = payload,
                LastSentUtc = DateTime.UtcNow,
                Attempts = 1,
                IsResign = true,
                Label = "resignation",
            };
            Status("Resigning — waiting for your opponent to acknowledge…");
        }
        catch (Exception ex)
        {
            FailResign($"Could not send resignation: {ex.Message}");
        }
    }

    /// <summary>Opponent acknowledged our resignation — end the game.</summary>
    private void HandleResignAck(ProtocolMessage pm)
    {
        if (!_resigning || pm.GameId != _gameId) return;
        var rp = _pending.Values.FirstOrDefault(p => p.IsResign);
        if (rp != null) _pending.Remove(rp.Id);
        _resigning = false;
        EndGame("You resigned. Opponent wins.");
    }

    private void FailResign(string reason)
    {
        var rp = _pending.Values.FirstOrDefault(p => p.IsResign);
        if (rp != null) _pending.Remove(rp.Id);
        _resigning = false;
        ApplyConnectionState();
        ShowConfirm("No acknowledgement",
            $"Your opponent didn't acknowledge your resignation.\n\n{reason}\n\nCancel the game anyway?",
            "Cancel game", "Keep waiting",
            onYes: () => CancelGame("Game cancelled (resignation not acknowledged)."),
            onNo:  () => Status("Resignation not acknowledged — resign or cancel again when ready."));
    }

    /// <summary>Handles an incoming RESIGN: acknowledge it (so the resigner can stop) and end the game.</summary>
    private void HandleResign(ProtocolMessage pm)
    {
        if (pm.GameId != _gameId) return;

        // Acknowledge as a player in this game — even if our game already ended, so the resigner's
        // retries resolve.
        SendControl(ProtocolMessage.EncodeResignAck(_gameId), encrypt: false, delay: true);

        if (_gameOver || !_playing) return;
        EndGame("Opponent resigned. You win!");
    }

    // ---- Board construction & rendering ----------------------------------------------

    private void BuildBoard()
    {
        StopMoveWave();   // a board rebuild replaces the cells — cancel any rainbow wave first
        BoardGrid.Children.Clear();
        BoardGrid.RowDefinitions.Clear();
        BoardGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < 8; i++)
        {
            BoardGrid.RowDefinitions.Add(new RowDefinition());
            BoardGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (int sq = 0; sq < 64; sq++)
        {
            var (row, col) = SquareToCell(sq);

            TextBlock MakeGlyph() => new()
            {
                FontSize = 40,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            var glyphFill = MakeGlyph();      // solid body of the piece
            var glyphOutline = MakeGlyph();   // contrasting edge drawn on top
            var marker = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = MarkerBrush,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            var coord = new TextBlock
            {
                FontSize = 9,
                Foreground = new SolidColorBrush(MediaColor.FromArgb(0x99, 0x33, 0x33, 0x33)),
                Margin = new Thickness(2, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Text = CoordLabel(sq)
            };

            var content = new Grid();
            content.Children.Add(coord);
            content.Children.Add(marker);
            content.Children.Add(glyphFill);
            content.Children.Add(glyphOutline);

            var cell = new Button
            {
                Style = (Style)FindResource("CellButton"),
                Content = content,
                Tag = sq,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(cell, $"sq{sq}");
            cell.Click += Cell_Click;

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            BoardGrid.Children.Add(cell);

            _cells[sq] = cell;
            _glyphFill[sq] = glyphFill;
            _glyphOutline[sq] = glyphOutline;
            _markers[sq] = marker;
        }
    }

    /// <summary>Maps a board square to a grid (row, col) honoring the viewing perspective.</summary>
    private (int row, int col) SquareToCell(int sq)
    {
        int rank = Move.RankOf(sq), file = Move.FileOf(sq);
        return _myColor == GameColor.White ? (7 - rank, file) : (rank, 7 - file);
    }

    private string CoordLabel(int sq)
    {
        var (row, col) = SquareToCell(sq);
        string s = "";
        if (col == 0) s += (char)('1' + Move.RankOf(sq));
        if (row == 7) s += (s.Length > 0 ? " " : "") + (char)('a' + Move.FileOf(sq));
        return s;
    }

    private void Render()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            _cells[sq].Background = BaseColor(sq);
            _markers[sq].Visibility = Visibility.Collapsed;
            SetGlyph(sq);
        }

        if (_lastMove is { } lm)
        {
            _cells[lm.from].Background = LastMoveBrush;
            _cells[lm.to].Background = LastMoveBrush;
        }

        if (_board.GetStatus() == GameStatus.Check)
        {
            int king = FindKing(_board.SideToMove);
            if (king >= 0) _cells[king].Background = CheckBrush;
        }

        if (_selected is { } sel)
        {
            _cells[sel].Background = SelectBrush;
            foreach (var m in _legalForSelected)
                _markers[m.To].Visibility = Visibility.Visible;
        }
    }

    private Brush BaseColor(int sq) =>
        (Move.FileOf(sq) + Move.RankOf(sq)) % 2 == 0 ? DarkSquare : LightSquare;

    // ---- Rainbow move wave -----------------------------------------------------------

    /// <summary>Starts a rainbow wave that pulses outward from the just-moved piece across the board's GREEN
    /// (dark) squares: each green square turns rainbow as the wavefront reaches it, then back to green once the
    /// band has passed. The wave keeps expanding until it has run past the farthest square (off the board edges),
    /// then every green square is restored. Only green squares are touched — light, last-move and check squares
    /// keep their colours.</summary>
    private void StartMoveWave(int originSq, bool capture)
    {
        if (!_rainbowEffect) return;   // effect disabled in Chess settings — board renders as normal
        StopMoveWave();   // cancel any wave still running (restores the board first)

        var (r0, c0) = SquareToCell(originSq);

        // Animate squares showing their plain board colour right now (not last-move / check highlights, which
        // are drawn in other colours and must be left alone). Dark (green) squares always ripple rainbow; on a
        // capture the light squares ripple red→dark-red as well.
        var skip = new HashSet<int>();
        if (_lastMove is { } lm) { skip.Add(lm.from); skip.Add(lm.to); }
        if (_board.GetStatus() == GameStatus.Check) { int k = FindKing(_board.SideToMove); if (k >= 0) skip.Add(k); }

        _waveSquares.Clear();
        _waveMaxDist = 0;
        for (int sq = 0; sq < 64; sq++)
        {
            var (r, c) = SquareToCell(sq);
            double d = Math.Sqrt((r - r0) * (r - r0) + (c - c0) * (c - c0));
            if (d > _waveMaxDist) _waveMaxDist = d;   // farthest square the wave must clear to leave the screen
            if (skip.Contains(sq)) continue;          // highlighted right now — leave it alone
            bool dark = (Move.FileOf(sq) + Move.RankOf(sq)) % 2 == 0;
            if (dark || capture) _waveSquares.Add((sq, d, dark));   // light squares only ripple on a capture
        }
        if (_waveSquares.Count == 0) return;

        _waveClock = System.Diagnostics.Stopwatch.StartNew();
        _waveHandler = (_, _) => OnWaveFrame();
        CompositionTarget.Rendering += _waveHandler;
    }

    private void OnWaveFrame()
    {
        if (_waveClock == null) return;
        double front = _waveClock.Elapsed.TotalSeconds * WaveSpeed;   // leading-edge radius, in squares

        if (front - WaveSpan > _waveMaxDist) { StopMoveWave(); return; }   // trailing edge has left the board

        int n = Rainbow.Length;
        foreach (var (sq, d, isDark) in _waveSquares)
        {
            double delta = front - d;   // how far the front has moved past this square
            if (delta >= 0 && delta <= WaveSpan)
            {
                int idx = (int)(delta / WaveSpan * (n - 1));
                if (idx < 0) idx = 0; else if (idx >= n) idx = n - 1;
                _cells[sq].Background = isDark ? Rainbow[idx] : RedRamp[idx];   // dark → rainbow, light → red
            }
            else
            {
                _cells[sq].Background = isDark ? DarkSquare : LightSquare;   // ahead of, or behind, the wave
            }
        }
    }

    private void StopMoveWave()
    {
        bool wasRunning = _waveHandler != null;
        if (_waveHandler != null) { CompositionTarget.Rendering -= _waveHandler; _waveHandler = null; }
        _waveClock = null;
        _waveSquares.Clear();
        if (wasRunning) Render();   // restore the proper square colours (green + any highlights)
    }

    private static SolidColorBrush[] BuildRainbow(int n)
    {
        var arr = new SolidColorBrush[n];
        for (int i = 0; i < n; i++)
        {
            var b = new SolidColorBrush(HsvToColor(i / (double)(n - 1) * 300.0, 1.0, 1.0));   // red → violet
            b.Freeze();
            arr[i] = b;
        }
        return arr;
    }

    // Bright red → dark red, for the capture wave on light squares.
    private static SolidColorBrush[] BuildRedRamp(int n)
    {
        var arr = new SolidColorBrush[n];
        for (int i = 0; i < n; i++)
        {
            byte r = (byte)Math.Round(255 - i / (double)(n - 1) * (255 - 80));   // 255 (bright) → 80 (dark)
            var b = new SolidColorBrush(MediaColor.FromRgb(r, 0, 0));
            b.Freeze();
            arr[i] = b;
        }
        return arr;
    }

    private static MediaColor HsvToColor(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h < 60 ? (c, x, 0d)
            : h < 120 ? (x, c, 0d)
            : h < 180 ? (0d, c, x)
            : h < 240 ? (0d, x, c)
            : h < 300 ? (x, 0d, c)
            : (c, 0d, x);
        return MediaColor.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private void SetGlyph(int sq)
    {
        var p = _board[sq];
        if (p.IsEmpty)
        {
            _glyphFill[sq].Text = "";
            _glyphOutline[sq].Text = "";
            return;
        }

        // Filled glyph for the body, hollow glyph (same shape, edge only) for the outline.
        (string filled, string hollow) = p.Type switch
        {
            PieceType.King => ("♚", "♔"),
            PieceType.Queen => ("♛", "♕"),
            PieceType.Rook => ("♜", "♖"),
            PieceType.Bishop => ("♝", "♗"),
            PieceType.Knight => ("♞", "♘"),
            PieceType.Pawn => ("♟", "♙"),
            _ => ("", "")
        };

        bool white = p.Color == GameColor.White;
        _glyphFill[sq].Text = filled;
        _glyphFill[sq].Foreground = white ? WhitePieceFill : BlackPieceFill;
        _glyphOutline[sq].Text = hollow;
        _glyphOutline[sq].Foreground = white ? WhitePieceOutline : BlackPieceOutline;
    }

    private int FindKing(GameColor color)
    {
        for (int sq = 0; sq < 64; sq++)
            if (_board[sq].Type == PieceType.King && _board[sq].Color == color)
                return sq;
        return -1;
    }

    // ---- Interaction (players only) --------------------------------------------------

    private async void Cell_Click(object sender, RoutedEventArgs e)
    {
        if (!_playing || _gameOver) return;
        if (_board.SideToMove != _myColor) { Status("Not your turn — waiting for opponent."); return; }
        if (sender is not Button cell || cell.Tag is not int sq) return;

        if (_selected == null) { SelectSquare(sq); return; }

        var candidates = _legalForSelected.Where(m => m.To == sq).ToList();
        if (candidates.Count > 0)
        {
            // The move is made immediately; CommitLocalMoveAsync just defers the network send if we're
            // still inside the post-receive hold (so our ack for the opponent's move transmits first).
            await CommitLocalMoveAsync(ResolvePromotion(candidates));
            return;
        }

        if (!_board[sq].IsEmpty && _board[sq].Color == _myColor)
            SelectSquare(sq);
        else
        {
            _selected = null;
            _legalForSelected.Clear();
            Render();
        }
    }

    private void SelectSquare(int sq)
    {
        var piece = _board[sq];
        if (piece.IsEmpty || piece.Color != _myColor)
        {
            _selected = null;
            _legalForSelected.Clear();
            Render();
            return;
        }
        _selected = sq;
        _legalForSelected = _board.GenerateLegalMoves().Where(m => m.From == sq).ToList();
        Render();
    }

    private Move ResolvePromotion(List<Move> candidates)
    {
        var promo = candidates.Where(m => m.Promotion != PieceType.None).ToList();
        if (promo.Count == 0) return candidates[0];
        PieceType chosen = PromotionPicker.Ask(this, _myColor);
        return promo.FirstOrDefault(m => m.Promotion == chosen,
                                    promo.First(m => m.Promotion == PieceType.Queen));
    }

    private async Task CommitLocalMoveAsync(Move move)
    {
        if (_mesh == null) return;

        // Snapshot the pre-move state so we can roll back only if the device can't even send it
        // (a missing ack never reverts a move — we just resend).
        var snapshot = _board.Clone();
        int prevPly = _ply;
        var prevLast = _lastMove;

        var mover = _board.SideToMove;
        bool isCapture = !_board[move.To].IsEmpty;   // a piece on the destination → this move captures it
        _board.MakeMove(move);
        _ply++;
        _moveHistory.Add(move.ToUci());
        _lastMove = (move.From, move.To);
        _selected = null;
        _legalForSelected.Clear();
        string baseText = Stamp() + MoveLine(_ply, mover, move);
        var entry = AddMoveEntry(baseText + MarkSending);
        entry.Foreground = PendingText;   // yellow while awaiting acknowledgement
        Render();
        StartMoveWave(move.To, isCapture);   // rainbow ripple out from the piece we just moved (+ red on light squares if a capture)

        string payload = ProtocolMessage.EncodeMove(_gameId, _ply, move);
        try
        {
            // The move is already on the board; only hold back its transmission until the post-receive
            // window passes, so our ack for the opponent's move finishes sending first.
            var wait = _moveSendAllowedUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                Status($"Move made — transmitting in {Math.Ceiling(wait.TotalSeconds):0}s (finishing acknowledgement)…");
                await Task.Delay(wait);
            }
            uint id = await _mesh.SendTextAsync(payload);
            _pending[id] = new PendingSend
            {
                Id = id,
                Payload = payload,
                LastSentUtc = DateTime.UtcNow,
                Attempts = 1,
                IsMove = true,
                Ply = _ply,
                Label = $"move {move.ToUci()}",
                Entry = entry,
                BaseText = baseText,
            };
            Status($"Sent {move.ToUci()} — awaiting acknowledgement...");
        }
        catch (Exception ex)
        {
            // Couldn't even hand it to the device: revert right away.
            _board = snapshot;
            _ply = prevPly;
            _lastMove = prevLast;
            if (_moveHistory.Count > 0) _moveHistory.RemoveAt(_moveHistory.Count - 1);
            MoveList.Items.Remove(entry);
            Render();
            MessageBox.Show(this, $"Move could not be sent:\n{ex.Message}\n\nReverted — please try again.",
                "Send failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (!CheckForEnd()) UpdateTurnStatus();
    }

    /// <summary>Manually resend the latest move whose auto-retries were exhausted (Resend button).</summary>
    private async void ResendBtn_Click(object sender, RoutedEventArgs e)
    {
        var p = _pending.Values.FirstOrDefault(m => m.IsMove && m.NeedsResend);
        if (p == null) return;
        p.NeedsResend = false;
        p.Attempts = 1;
        p.LastSentUtc = DateTime.UtcNow;
        if (p.Entry != null) p.Entry.Foreground = PendingText;   // yellow again while awaiting ack
        Status($"Resending {p.Label}…");
        ApplyConnectionState();                                  // grey the button until it fails again
        await ResendPendingAsync(p);
    }

    // ---- Receiving -------------------------------------------------------------------

    private async Task PollAsync()
    {
        if (_polling || _refreshing || _syncing || _mesh == null) return;
        if (!_mesh.TransportConnected) return;   // dead socket — ProbeAsync tears it down
        _polling = true;
        try
        {
            var result = await _mesh.ReceiveAsync();
            _lastPollOkUtc = DateTime.UtcNow; _probeFailures = 0;   // the device answered — definitely alive
            if (result.PacketCount > 0) { _lastRxUtc = DateTime.UtcNow; _rxStallWarned = false; }
            foreach (var ack in result.Acks) MarkAcked(ack);
            foreach (var msg in result.Texts) Dispatch(msg);
            HandleNodeDiagnostics(result);
            _pollObserver?.Invoke(result);
        }
        catch (Exception ex)
        {
            // A slow or failed poll is NOT treated as a lost link here — the device can be alive but busy (the
            // ESP32 serves HTTP slowly while the radio is active). The TCP reachability probe (ProbeAsync) is the
            // authority on whether the device is actually unreachable. A non-connectivity error is a data problem.
            if (!IsConnectivityError(ex))
                Status($"Mesh receive error: {ex.Message}");
        }
        finally
        {
            _polling = false;
        }
    }

    // A failed poll is a lost link (vs. a data error) when the HTTP call itself couldn't reach the device:
    // connection refused/reset, DNS failure, a socket error, or the request timing out — all of which happen
    // while the radio is rebooting or off the network.
    private static bool IsConnectivityError(Exception ex) =>
        ex is System.Net.Http.HttpRequestException or TaskCanceledException or OperationCanceledException or System.IO.IOException
        || ex.InnerException is System.Net.Sockets.SocketException;

    // Declares the device connection lost after repeated failed TCP probes (e.g. the radio was powered off). Tears
    // the connection down so the button flips back to "Connect", and posts a system message asking to reconnect.
    private void HandleConnectionLost()
    {
        if (!_connected) return;   // already torn down
        string host = _currentHost;
        AddSystemWarning(Stamp() + "— Connection to the device was lost (it may have been turned off or left the network). —");
        Disconnect("Connection lost.");
        if (AppSettings.AutoReconnect && host.Length > 0)
            StartAutoReconnect(host);
        else
            Status("Connection lost — click Connect to reconnect.");
    }

    // Auto-reconnect: after the device drops, retry every ReconnectIntervalSeconds (first attempt immediately)
    // until it reconnects or the user clicks Cancel. A 1-second timer counts down between attempts so the user
    // can see exactly when the next try happens — and that the loop is alive, not hung. Off unless enabled in
    // Connection settings.
    private void StartAutoReconnect(string host)
    {
        _autoReconnecting = true;
        _autoReconnectHost = host;
        AddSystemWarning(Stamp() + $"— Auto-reconnect on: retrying every {ReconnectIntervalSeconds}s. Click Cancel to stop. —");
        ApplyConnectionState();
        _autoReconnectTimer.Start();
        _ = TryAutoReconnectAsync();   // first attempt right away; the timer counts down to the next one
    }

    // Fires once a second while auto-reconnecting: counts the seconds down (showing them in the status bar and on
    // the Cancel button), then launches the next attempt when it reaches zero. While an attempt is actually in
    // flight we don't count down — we show "trying…" so a slow connect doesn't look like a frozen countdown.
    private async Task AutoReconnectTickAsync()
    {
        if (!_autoReconnecting) return;
        if (_connecting || _connected || _mesh != null)
        {
            if (_connecting) Status("Auto-reconnect: trying to reach the device…");
            return;   // an attempt is running (or we just reconnected) — hold the countdown
        }
        if (_reconnectCountdown > 0)
        {
            _reconnectCountdown--;
            ConnectBtn.Content = $"Cancel ({_reconnectCountdown}s)";
            Status($"Device offline — next reconnect attempt in {_reconnectCountdown}s. Click Cancel to stop.");
            return;
        }
        await TryAutoReconnectAsync();   // countdown elapsed — try now (resets the countdown for the next round)
    }

    private async Task TryAutoReconnectAsync()
    {
        if (!_autoReconnecting || _connecting || _connected || _mesh != null) return;
        _reconnectCountdown = ReconnectIntervalSeconds;   // arm the next countdown before the (slow) attempt
        HostBox.Text = _autoReconnectHost;
        Status("Auto-reconnect: trying to reach the device…");
        await ConnectAsync();   // errors are suppressed (no pop-ups) while auto-reconnecting
        if (_connected) StopAutoReconnect(reconnected: true);
    }

    private void StopAutoReconnect(bool reconnected)
    {
        if (!_autoReconnecting) return;
        _autoReconnecting = false;
        _autoReconnectTimer.Stop();
        _reconnectCountdown = 0;
        if (reconnected) AddSystem(Stamp() + "— Reconnected. —");
        else Status("Auto-reconnect cancelled — click Connect to reconnect.");
        ApplyConnectionState();
    }

    /// <summary>TCP reachability probe (runs on the 2.5s probe timer). Opens a bare TCP connection to the device's
    /// HTTP port: the ESP32's network stack accepts it from a task separate from the busy main loop, so a CONNECT
    /// succeeds quickly even when HTTP responses are slow — unlike a slow response, a failed connect reliably means
    /// the device is unreachable. To stay near-free, it only probes when a poll hasn't recently succeeded (data
    /// flowing already proves the device is up). N consecutive failed connects → connection lost.</summary>

    private async Task ProbeAsync()
    {
        if (_probing || _connecting || _refreshing || _syncing || !_connected || _mesh == null) return;
        // A persistent transport (TCP) can have its socket reset by the device while a fresh probe would still
        // succeed; check the live link directly and tear down cleanly so the UI doesn't show "connected" when it
        // can't send/receive.
        if (!_mesh.TransportConnected) { HandleConnectionLost(); return; }
        // A persistent TCP/BLE link already reports its own health (its socket faults on drop, backed by OS
        // keep-alive). We must NOT open a competing connection to "probe" it: the TCP stream API (port 4403) allows
        // only one client, so the probe connect is refused *because* our live connection holds the slot — a false
        // "unreachable" that triggered needless reconnects. The TransportConnected check above is the real test for
        // these links; only HTTP (always "connected") still needs the external probe below.
        if (_mesh.TransportSelfReportsLiveness) return;
        if (DateTime.UtcNow - _lastPollOkUtc < ProbeIdleGrace) return;   // data is flowing — device is clearly up
        _probing = true;
        try
        {
            bool reachable = await IsDeviceReachableAsync(_probeHost, _probePort, TcpProbeTimeout);
            if (!_connected) return;   // disconnected while probing
            if (reachable)
                _probeFailures = 0;
            else if (++_probeFailures >= ConnectionLostThreshold)
            {
                _probeFailures = 0;
                HandleConnectionLost();
            }
        }
        finally { _probing = false; }
    }

    // Returns true if a TCP connection to the device's API port can be established within <paramref name="timeout"/>.
    private static async Task<bool> IsDeviceReachableAsync(string host, int port, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(host) || port <= 0) return true;   // unknown target — don't declare it lost
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new System.Threading.CancellationTokenSource(timeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch { return false; }
    }

    /// <summary>Surfaces traceroute and node-info replies from a receive batch: logs each to System messages
    /// and (when the Nodes dialog is open) updates its status line. Node-info replies also refresh the cached
    /// names, since the device merged the reply into its node DB.</summary>
    private void HandleNodeDiagnostics(MeshReceiveResult r)
    {
        if (_mesh == null) return;

        foreach (var t in r.Traceroutes)
        {
            // Render the path: us → [reported hops] → traced node (avoid repeating the node if it's last).
            var parts = new List<string> { _mesh.DescribeNode(_mesh.MyNodeNum) };
            foreach (var hop in t.Route) parts.Add(_mesh.DescribeNode(hop));
            if (t.Route.Count == 0 || t.Route[^1] != t.Node) parts.Add(_mesh.DescribeNode(t.Node));
            int hops = Math.Max(0, parts.Count - 1);
            string line = $"Traceroute to {_mesh.DescribeNode(t.Node)}: {string.Join(" → ", parts)}  ({hops} hop{(hops == 1 ? "" : "s")})";
            AddSystem(Stamp() + line);
            // Deliver to the open traceroute window for this node; otherwise fall back to the Nodes status line.
            if (_tracerouteWaiters.TryGetValue(t.Node, out var waiter)) waiter(t);
            else _nodeDiagHandler?.Invoke(line);
        }

        foreach (var ni in r.NodeInfos)
        {
            string name = ni.Name.Length > 0 ? ni.Name : $"!{ni.Node:x8}";
            string line = $"Node info received from {name}.";
            if (AppSettings.ShowNewNodeInfo) AddSystem(Stamp() + line);
            _nodeDiagHandler?.Invoke(line);
        }

        // Announce nodes heard for the first time (their live broadcast was just merged into the DB).
        foreach (var nn in r.NewNodes)
        {
            string name = nn.Name.Length > 0 ? nn.Name : $"!{nn.Node:x8}";
            string line = $"New node heard: {name}.";
            if (AppSettings.ShowNewNodeInfo) AddSystem(Stamp() + line);
            _nodeDiagHandler?.Invoke(line);
        }

        // A node-info reply updates the device's node DB; persist the refreshed names so they survive reconnect.
        if (r.NodeInfos.Count > 0 && _currentHost.Length > 0)
            DeviceCache.Save(_currentHost, _mesh.GetAvailableChannels(), _mesh.MyNodeNum, _mesh.GetNodeNameMap(), _mesh.GetNodeRoleMap(), _mesh.GetNodeHwMap());

        // Position heard from another node (broadcast or a reply to our request): note it in the system log, and
        // refresh the Nodes dialog/map since the node DB position just changed.
        if (AppSettings.ShowPositionUpdates)
            foreach (var pos in r.Positions)
            {
                string name = pos.Name.Length > 0 ? pos.Name : $"!{pos.Node:x8}";
                AddSystem(Stamp() + $"Position received from {name}: {pos.Latitude.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}, {pos.Longitude.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}.");
            }

        // Noise floor replies (requested via the node info window): log them and refresh any open node window.
        foreach (var nf in r.NoiseFloors)
        {
            string name = nf.Name.Length > 0 ? nf.Name : $"!{nf.Node:x8}";
            string line = $"Noise floor for {name}: {nf.NoiseFloorDbm} dBm";
            AddSystem(Stamp() + line);
            _nodeDiagHandler?.Invoke(line);
        }
        if (r.NoiseFloors.Count > 0) { _nodesRefresh?.Invoke(); _telemetryRefresh?.Invoke(); }
        if (r.Positions.Count > 0)
        {
            _nodesRefresh?.Invoke();
            if (_currentHost.Length > 0) DeviceCache.SaveNodePositions(_currentHost, _mesh.GetNodePositionMap());
        }

        // Fresh telemetry: refresh the open Nodes dialog rows + any telemetry window, persist, and note nodes.
        if (r.Telemetry.Count > 0)
        {
            _nodesRefresh?.Invoke();
            _telemetryRefresh?.Invoke();
            var names = string.Join(", ", r.Telemetry.Distinct().Select(_mesh.DescribeNode));
            _nodeStatusHandler?.Invoke($"Environment updated: {names}");
            if (_currentHost.Length > 0)
                foreach (var num in r.Telemetry.Distinct())
                    DeviceCache.SaveTelemetry(_currentHost, num, _mesh.GetEnvironmentHistory(num).Select(ToReading));
        }

        // Nodes are merged into the device DB as their live broadcasts arrive (see ReceiveAsync), so the known
        // count grows on its own. When it changes, persist the refreshed node names/roles and — if the Nodes
        // dialog is open — rebuild it so the new rows appear. Otherwise tick the existing rows in place so each
        // node's RSSI/SNR/last-heard stays current without a scroll jump.
        int nodeCount = _mesh.GetNodes().Count;
        if (nodeCount != _lastNodeViewCount)
        {
            _lastNodeViewCount = nodeCount;
            if (_currentHost.Length > 0)
                DeviceCache.Save(_currentHost, _mesh.GetAvailableChannels(), _mesh.MyNodeNum, _mesh.GetNodeNameMap(), _mesh.GetNodeRoleMap(), _mesh.GetNodeHwMap());
            _nodesRepopulate?.Invoke();
        }
        else
            _nodesRefresh?.Invoke();
    }

    private static MeshEnvironment ToEnv(DeviceCache.TelemetryReading r) =>
        new MeshEnvironment(r.Temperature, r.Humidity, r.Pressure, r.RxTime, r.ReceivedAt);

    private static DeviceCache.TelemetryReading ToReading(MeshEnvironment e) =>
        new DeviceCache.TelemetryReading
        {
            Temperature = e.TemperatureC, Humidity = e.RelativeHumidity, Pressure = e.BarometricPressure,
            RxTime = e.RxTime, ReceivedAt = e.ReceivedAt,
        };

    /// <summary>Emits the dead-air warning (a red system message + status) pointing the user at Update nodes.
    /// The caller (<see cref="CheckReceptionStall"/>) rate-limits how often this is shown.</summary>
    private void WarnReceptionStall(string reason)
    {
        AddSystemWarning(Stamp() + $"— {reason} If the device was restarted, click Nodes… → Update nodes to resync with it. —");
        Status("No messages received recently — if the device was restarted, click Nodes… → Update nodes to resync.");
    }

    /// <summary>Watchdog (runs on the 1s ack timer): if no packet of any kind has arrived for
    /// <see cref="ReceiveStallTimeout"/> (10 min), warn — and keep repeating every
    /// <see cref="StallRepeatInterval"/> (10 min) while the silence continues. Any received packet resets
    /// <see cref="_lastRxUtc"/> (and clears <see cref="_rxStallWarned"/>), so a working link never trips it and
    /// the next dead-air spell warns immediately at the timeout.</summary>
    private void CheckReceptionStall()
    {
        if (!_connected || !_synced || _syncing || _refreshing) return;
        var now = DateTime.UtcNow;
        if (now - _lastRxUtc < ReceiveStallTimeout) return;                          // received within the window — fine
        if (_rxStallWarned && now - _lastStallWarnUtc < StallRepeatInterval) return; // already warned this interval
        _rxStallWarned = true;
        _lastStallWarnUtc = now;
        WarnReceptionStall($"No messages received from the device for {ReceiveStallTimeout.TotalMinutes:0} minutes.");
    }

    /// <summary>
    /// Drains the connect-time packet backlog in the background, in chunks, showing a progress
    /// counter and processing messages/acks as it goes. When done, enables play and the poll loop.
    /// </summary>
    private async Task RunSyncAsync()
    {
        if (_mesh == null) return;
        _syncing = true;
        int total = 0;
        const int chunk = 25;
        Status("Syncing with the mesh…");
        try
        {
            while (_mesh != null)
            {
                var result = await _mesh.ReceiveAsync(chunk);
                foreach (var ack in result.Acks) MarkAcked(ack);
                foreach (var msg in result.Texts) Dispatch(msg);
                total += result.PacketCount;
                Status($"Syncing with the mesh… {total} packet{(total == 1 ? "" : "s")} drained");
                if (result.PacketCount < chunk) break;   // queue emptied
            }
        }
        catch (Exception ex)
        {
            Status($"Sync error: {ex.Message}");
        }

        _syncing = false;
        _synced = true;
        // Messages sent during sync shouldn't be counted as "waiting" until now.
        var now = DateTime.UtcNow;
        foreach (var p in _pending.Values) p.LastSentUtc = now;
        _lastRxUtc = now; _rxStallWarned = false;   // reception is live now — arm the stall watchdog

        // The full drain populated the node list — persist names and show them on any acks so far.
        if (_mesh != null)
        {
            DeviceCache.Save(_currentHost, _mesh.GetAvailableChannels(), _mesh.MyNodeNum, _mesh.GetNodeNameMap(), _mesh.GetNodeRoleMap(), _mesh.GetNodeHwMap());
            RefreshChatAckerNames();
            SyncDeviceClockIfAhead();   // correct a radio whose clock is set in the future (bad "last heard" stamps)
        }

        ApplyConnectionState();
        if (_playing)
            UpdateTurnStatus();   // a game was started during sync; restore its turn status
        else
            Status($"Ready ({total} packets synced). Pick a channel/role and start, or chat.");
        _pollTimer.Start();
    }

    private void Dispatch(MeshTextMessage msg)
    {
        if (_mesh == null) return;
        // Chess traffic is confined to the chess channel; chat is shown for any channel chat listens to.
        bool onChess = msg.Channel == _mesh.ChannelIndex;
        bool onChat = IsReceiveChannel(msg.Channel);   // chat is received on ALL enabled channels; the RX filter chooses what shows
        // A direct message addressed to us: shown as chat regardless of channel — unless the sender is blocked.
        bool wasDm = msg.IsDmTo(_mesh.MyNodeNum);
        if (wasDm && _nodePrefs.GetValueOrDefault(msg.FromNode)?.Block == true)
            return;   // blocked node — ignore its DMs
        // An emoji reaction (tapback): attach it to the target message instead of showing it as its own chat line.
        if (msg.IsReaction)
        {
            if (msg.ReplyId != 0 && (onChat || wasDm)) AddReaction(msg.ReplyId, msg.Text, msg.FromNode);
            return;
        }
        if (!onChess && !onChat && !wasDm) return;   // a channel we don't route (chess or chat) and not a DM; ignore

        // Stamp anything logged while handling this message with the device's receive time (if known).
        _incomingRxTime = msg.RxTime;
        try
        {
        // Chess protocol messages drive the board; everything else is channel chat.
        if (ProtocolMessage.TryParse(msg.Text, out var pm))
        {
            // A CHATACK belongs to chat (it acks a chat message we sent); everything else is game traffic.
            if (pm.Kind == MessageKind.ChatAck)
            {
                // pm.AckSignal = how the acker heard our original message; AckSignalText(msg) = how OUR node
                // heard their ack packet just now. Record both directions of the link.
                if (onChat) RegisterChatAck(pm.ChatPacketId, msg.FromNode, pm.AckSignal, AckSignalText(msg));
            }
            else if (!onChess)
            {
                // Game/control message arriving off the chess channel — ignore it.
            }
            else if (pm.Kind == MessageKind.Create)
                HandleGameAnnounced(pm, msg);
            else if (pm.Kind == MessageKind.CreateAck)
                HandleCreateAck(pm, msg);
            else if (pm.Kind == MessageKind.Join)
                HandleGameJoined(pm, msg);
            else if (pm.Kind == MessageKind.Board)
                HandleBoard(pm);   // host's reply to our JOIN carrying the position
            else if (pm.Kind == MessageKind.ResignAck)
                HandleResignAck(pm);
            else if (pm.Kind == MessageKind.Resign)
                HandleResign(pm);   // handled even after game-over so retries get re-acked
            else if (pm.Kind == MessageKind.Save)
                HandleSaveRequest(pm, msg);
            else if (pm.Kind == MessageKind.SaveAck)
                HandleSaveAck(pm);
            else if (pm.Kind == MessageKind.Cancel)
                HandleCancel(pm);
            else if (pm.Kind == MessageKind.Ended)
                HandleEnded(pm);
            else if (pm.Kind == MessageKind.Ack)
                HandleMoveAck(pm);   // process even after game-over, e.g. the ack of a checkmating move
            else if (pm.GameId == _gameId && _playing && !_gameOver)
                ApplyIncoming(pm);
            else if (pm.Kind == MessageKind.Move && pm.GameId == _gameId)
                // A move for our game, but it's over/not running here — tell the sender it has ended.
                SendControl(ProtocolMessage.EncodeEnded(pm.GameId), encrypt: false);
        }
        else if (onChat || wasDm)
        {
            string who = _mesh?.DescribeNode(msg.FromNode) ?? msg.FromNode.ToString();
            string sig = SignalTag(msg, _showSignal);   // hops/relay always; RSSI/SNR only when the checkbox is on
            string chan = (!wasDm && ReceiveChannels().Count > 1) ? $"[{msg.Channel}] " : "";   // channel when several exist
            string dmTag = wasDm ? "DM ← " : "";   // make it clear in the metadata this was a direct message
            // A resent chat carries a marker prefix; strip it and note "resent" in the metadata instead.
            string body = msg.Text;
            bool resent = body.StartsWith(ProtocolMessage.ChatResendPrefix, StringComparison.Ordinal);
            if (resent) body = body.Substring(ProtocolMessage.ChatResendPrefix.Length);
            string detail = $"{Stamp()}{dmTag}{chan}{sig}".Trim();   // dim metadata line under the message
            if (resent) detail = detail.Length > 0 ? detail + "  · resent" : "resent";
            // Note when the device got this off MQTT rather than over the air (shown regardless of the signal toggle).
            if (msg.ViaMqtt) detail = detail.Length > 0 ? detail + "  · via MQTT" : "via MQTT";
            string replyRef = ReplyRef(msg.ReplyId);   // if this is a reply, quote what it answers
            if (replyRef.Length > 0) detail = detail.Length > 0 ? $"{replyRef}  ·  {detail}" : replyRef;
            LogEntry entry = msg.DecryptFailed
                // Channel has an app key set, but this didn't decrypt — show the raw payload in red.
                ? AddChatLine($"{who}: {msg.Text}  ⚠ decryption failed (wrong/missing key)", detail, WarningText, msg)
                : AddChatLine($"{who}: {body}", detail, NormalText, msg);
            entry.PacketId = msg.PacketId;                       // so it can be replied to
            entry.Channel = msg.Channel;
            _chatEntryById[msg.PacketId] = entry;                // so reactions can attach to this row
            if (_reactions.TryGetValue(msg.PacketId, out var early)) entry.Reactions = FormatReactions(early);
            if (!msg.DecryptFailed)
            {
                _chatById[msg.PacketId] = body;                  // remember its text for reply quoting
                entry.CacheId = CacheChat(msg.Channel, entry.Text, entry.Detail);   // persist (latest 100 per channel)
            }
            // Apply the RX filter: hide the row if its channel/DM is hidden, and count it as unread there instead
            // of notifying (so a hidden conversation just shows a badge in the RX list).
            bool shown = RouteRx(entry, wasDm, msg.FromNode, incoming: true);
            if (shown) { FlashNotify(); PlayChatSound(); }
            // A DM from a node we haven't DM-enabled flips its DM flag on (and lists it in TX) so we can reply.
            if (wasDm) EnsureDmEnabled(msg.FromNode);
            // A reply on the channel we just sent to confirms our in-flight chat (turns it green, frees Send).
            ConfirmChatByIncomingReply(msg.Channel);
            // Acks are per-channel (default off); a DM is routing-acked by the firmware. A keyword match forces an
            // ack (with RSSI) even when the channel's acks are off — useful for range-test pings.
            bool keywordAck = !wasDm && MatchesAckTrigger(msg.Channel, msg.Text);
            if (!wasDm && (_chatAckOn.Contains(msg.Channel) || keywordAck))
            {
                // Report how well we received it (RSSI/SNR/hops) if the channel asks for it, or always on a keyword match.
                string ackSignal = (keywordAck || _chatAckSignalOn.Contains(msg.Channel)) ? AckSignalText(msg) : "";
                SendChatAck(msg.PacketId, msg.Channel, ackSignal);   // tell the sender we received it (on its channel)
                _chatSendAllowedUtc = DateTime.UtcNow + ChatSendDelay;   // let that ack send before we chat
                if (_chatHoldTimer != null)   // grey out Send now; re-enable when the hold ends
                {
                    _chatHoldTimer.Stop();
                    _chatHoldTimer.Interval = ChatSendDelay + TimeSpan.FromMilliseconds(200);
                    _chatHoldTimer.Start();
                }
            }
            ApplyConnectionState();
        }
        }
        finally { _incomingRxTime = 0; }
    }

    /// <summary>Builds the trailing signal/route tag for a received chat line: the hop count / "direct" / relay
    /// (always shown — that's routing info), plus the RF signal (RSSI/SNR) only when <paramref name="includeSignal"/>
    /// is set (the "RSSI" checkbox gates that part alone). Returns "" (no tag) when there's nothing to show.
    /// Leading spaces included so it can be appended straight onto the message text.</summary>
    private string SignalTag(MeshTextMessage msg, bool includeSignal)
    {
        var parts = new List<string>();
        if (msg.Hops is int hops && hops > 0)
        {
            // Relayed: the RSSI/SNR is the last hop (relay→us), so name that relay alongside its signal.
            parts.Add($"{hops} hop{(hops == 1 ? "" : "s")}");
            string relay = _mesh?.DescribeRelayNode(msg.RelayNode) ?? "";
            if (relay.Length > 0) parts.Add($"via {relay}");
        }
        else if (msg.IsDirect)
        {
            parts.Add("direct");
        }
        if (includeSignal && (msg.RxRssi != 0 || msg.RxSnr != 0))
            parts.Add($"RSSI {msg.RxRssi} dBm · SNR {msg.RxSnr:0.#} dB");
        return parts.Count > 0 ? "   [" + string.Join(" · ", parts) + "]" : "";
    }

    /// <summary>Opponent acknowledged the move we sent at this ply. Handled even after the game ends so
    /// the checkmating (or stalemating) move still shows as delivered.</summary>
    private void HandleMoveAck(ProtocolMessage pm)
    {
        if (pm.GameId != _gameId) return;
        var movePending = _pending.Values.FirstOrDefault(p => p.IsMove && p.Ply == pm.Ply);
        if (movePending == null) return;
        _pending.Remove(movePending.Id);
        if (movePending.Entry != null) { movePending.Entry.Text = movePending.BaseText + MarkDelivered; movePending.Entry.Foreground = AckedText; }
        Status($"Opponent acknowledged {movePending.Label}.");
        ApplyConnectionState();   // grey Resend if this move had been awaiting it
    }

    private void ApplyIncoming(ProtocolMessage pm)
    {
        if (pm.Kind != MessageKind.Move) return;

        if (pm.Ply <= _ply)
        {
            // We already have this move; re-acknowledge so a retrying sender can stop.
            SendMoveAck(pm.Ply);
            return;
        }
        if (pm.Ply != _ply + 1)
        {
            Status($"Out-of-order move (ply {pm.Ply}, expected {_ply + 1}).");
            return;
        }
        // Only accept the opponent's move.
        if (_board.SideToMove == _myColor) return;

        var mover = _board.SideToMove;
        var legal = _board.FindLegalMove(pm.Move);
        if (legal == null)
        {
            Status($"Received illegal move {pm.Move.ToUci()} — boards may be out of sync.");
            return;
        }

        // The opponent could only reply if our previous move reached them — treat it as an ack.
        ConfirmPendingMove();
        if (_awaitingOpponent) { _awaitingOpponent = false; ApplyConnectionState(); }

        bool isCapture = !_board[legal.Value.To].IsEmpty;   // destination occupied → the opponent captured
        _board.MakeMove(legal.Value);
        _ply++;
        _moveHistory.Add(legal.Value.ToUci());
        _lastMove = (legal.Value.From, legal.Value.To);
        AddMoveEntry(Stamp() + MoveLine(_ply, mover, legal.Value));   // received from opponent (no ack mark)
        Render();
        StartMoveWave(legal.Value.To, isCapture);   // rainbow ripple from the opponent's piece (+ red on light squares if a capture)
        FlashNotify();
        PlayChessSound();

        // As the receiving player, confirm the move back to the sender. Hold off our own next move for a
        // few seconds so this ack is fully transmitted before we send a move on the same channel.
        SendMoveAck(_ply);
        _moveSendAllowedUtc = DateTime.UtcNow + MoveSendDelay;

        if (!CheckForEnd()) UpdateTurnStatus();
    }

    /// <summary>Broadcast an acknowledgement for a move we received.</summary>
    private async void SendMoveAck(int ply)
    {
        if (_mesh == null) return;
        await AckJitterDelay();
        try { await _mesh.SendTextAsync(ProtocolMessage.EncodeAck(_gameId, ply), encrypt: false); }
        catch { /* best effort; a retry from the sender will prompt another ack */ }
    }

    // ---- Chat ------------------------------------------------------------------------

    private void ChatBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter falls through to the TextBox to insert a newline (AcceptsReturn).
        // Intercepted in PreviewKeyDown so this runs before the TextBox's own Enter handling.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { SendChat(); e.Handled = true; }
    }

    private void ChatBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateCharCounter();

    /// <summary>Live "&lt;wire chars&gt; / &lt;max&gt;" counter under Send. Counts the bytes actually sent:
    /// the (trimmed) text, or its AES base64 length when a channel key is set. Turns red over the limit.</summary>
    private void UpdateCharCounter()
    {
        if (CharCounter == null) return;
        string text = ChatBox.Text.Trim();
        string key = _mesh?.GetChannelKey(_chatTxChannel) ?? "";   // wire length depends on the chat TX channel's key
        // The radio limit is on the payload BYTES, so count UTF-8 bytes (emoji are multi-byte); for an app-keyed
        // channel the payload is the AES base64 ciphertext (ASCII, so length == bytes).
        int wireLen = text.Length == 0 ? 0 : (key.Length > 0 ? AesText.Encrypt(text, key).Length : System.Text.Encoding.UTF8.GetByteCount(text));
        CharCounter.Text = $"{wireLen} / {MaxChatChars}";
        CharCounter.Foreground = wireLen > MaxChatChars ? WarningText : CounterNormal;
    }

    private void ChatSendBtn_Click(object sender, RoutedEventArgs e) => SendChat();

    private System.Windows.Controls.Primitives.Popup? _emojiPopup;

    /// <summary>Toggles a small popup of common smileys/symbols; picking one inserts it at the chat caret.</summary>
    private void EmojiBtn_Click(object sender, RoutedEventArgs e)
    {
        _emojiPopup ??= BuildEmojiPopup();
        _emojiPopup.PlacementTarget = EmojiBtn;
        _emojiPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        _emojiPopup.IsOpen = !_emojiPopup.IsOpen;
    }

    private System.Windows.Controls.Primitives.Popup BuildEmojiPopup()
    {
        string[] emojis =
        {
            "🙂","😀","😁","😂","😉","😎","😍","🤔","😴","😢","😡","🤣","😅","🙃","🥳","😬",
            "👍","👎","👌","🙏","👏","💪","🤝","✌️","🫡","🎉","❤️","🔥","✅","❌","⚠️","❓",
            "♟️","♚","♛","♜","♞","♝","⏳","📍","📡","🔋","☀️","🌧️","❄️","⚡","🌙","⭐",
        };
        var panel = new WrapPanel { Width = 250, Margin = new Thickness(3) };
        foreach (var em in emojis)
        {
            var b = new Button
            {
                Content = em, Width = 28, Height = 28, FontSize = 15, Margin = new Thickness(1),
                Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xE0, 0xE0)), Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (_, _) => { InsertAtCaret(ChatBox, em); _emojiPopup!.IsOpen = false; };
            panel.Children.Add(b);
        }
        return new System.Windows.Controls.Primitives.Popup
        {
            StaysOpen = false,   // closes when you click elsewhere
            Child = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x2D, 0x2D, 0x30)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3F, 0x3F, 0x46)),
                BorderThickness = new Thickness(1),
                Child = panel,
            },
        };
    }

    /// <summary>Inserts text at the textbox caret, leaving the caret after it and focus in the box.</summary>
    private static void InsertAtCaret(TextBox box, string text)
    {
        int caret = box.CaretIndex;
        box.Text = box.Text.Insert(caret, text);
        box.CaretIndex = caret + text.Length;
        box.Focus();
    }

    /// <summary>Right-click → Reply: targets the selected chat message so the next send links to it, and switches
    /// the chat TX to wherever that message came from — a DM back to the sender for a direct message, or the
    /// channel it arrived on for a group message (so the reply goes to the same place).</summary>
    private void Reply_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null) return;
        if (ChatList.SelectedItem is not LogEntry entry || entry.PacketId == 0)
        {
            Status("Select a chat message to reply to.");
            return;
        }
        _replyToId = entry.PacketId;
        string original = _chatById.TryGetValue(entry.PacketId, out var t) ? t : entry.Text;
        ReplyText.Text = $"↳ Replying to: {Snippet(original, 60)}";
        ReplyBanner.Visibility = Visibility.Visible;

        // Point the TX at the source of the message we're replying to (received messages only — a reply to one of
        // your own sent messages keeps the current TX).
        if (entry.Rx is { } rx)
        {
            if (rx.IsDmTo(_mesh.MyNodeNum) && rx.FromNode != 0 && _nodePrefs.GetValueOrDefault(rx.FromNode)?.Block != true)
            {
                // DM: enable a direct message back to the sender and select it.
                _nodePrefs[rx.FromNode] = new DeviceCache.NodePrefs { Dm = true, Block = false };
                if (_currentHost.Length > 0) DeviceCache.SetNodePref(_currentHost, rx.FromNode, true, false);
                _chatTxDest = rx.FromNode;
                RebuildChatTxCombo();
            }
            else if (!rx.IsDirectMessage && _chatListen.Contains(rx.Channel))
            {
                // Group message: switch the TX channel to the one it arrived on.
                _chatTxDest = null;
                _chatTxChannel = rx.Channel;
                RebuildChatTxCombo();
            }
        }

        ChatBox.Focus();
    }

    /// <summary>Right-click → React → emoji: sends an emoji reaction (tapback) to the selected message and shows
    /// it under that message. Reacts on the same place the message lives (a DM back to the sender if it was a DM,
    /// else the channel it's on).</summary>
    private async void React_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null) return;
        if (sender is not MenuItem mi || mi.Header is not string emoji) return;
        if (ChatList.SelectedItem is not LogEntry entry || entry.PacketId == 0)
        {
            Status("Select a chat message to react to.");
            return;
        }
        uint targetId = entry.PacketId;
        uint channel = entry.Channel != uint.MaxValue ? entry.Channel : _chatTxChannel;
        uint? dest = entry.Rx is { } rx && rx.IsDmTo(_mesh.MyNodeNum) ? rx.FromNode : (uint?)null;
        try
        {
            await _mesh.SendReactionAsync(emoji, targetId, channel, dest);
            AddReaction(targetId, emoji, _mesh.MyNodeNum);   // show our own reaction immediately
        }
        catch (Exception ex) { Status($"Reaction failed: {ex.Message}"); }
    }

    /// <summary>Records an emoji reaction against a message (by its packet id) and updates that row's reaction
    /// line. A given node reacting with the same emoji counts once.</summary>
    private void AddReaction(uint targetId, string emoji, uint node)
    {
        if (string.IsNullOrEmpty(emoji)) return;
        if (!_reactions.TryGetValue(targetId, out var list)) _reactions[targetId] = list = new List<(string, uint)>();
        if (list.Any(r => r.Emoji == emoji && r.Node == node)) return;   // de-dup
        bool stick = IsScrolledToBottom(ChatList);
        list.Add((emoji, node));
        if (_chatEntryById.TryGetValue(targetId, out var entry))
            entry.Reactions = FormatReactions(list);
        StickToBottom(ChatList, stick);
    }

    // "👍 ❤️ 2" — each distinct emoji once, with a count when more than one node reacted with it.
    private static string FormatReactions(List<(string Emoji, uint Node)> list) =>
        string.Join("    ", list.GroupBy(r => r.Emoji).Select(g => g.Count() > 1 ? $"{g.Key} {g.Count()}" : g.Key));

    /// <summary>Right-click → DM: enables direct messages for the selected message's sender and selects it as the
    /// chat TX target, so the next message you send goes straight to that node.</summary>
    private void Dm_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null) return;
        if (ChatList.SelectedItem is not LogEntry entry || entry.Rx is not { } msg || msg.FromNode == 0)
        {
            Status("Select a received message to DM its sender.");
            return;
        }
        uint node = msg.FromNode;
        if (_nodePrefs.GetValueOrDefault(node)?.Block == true)
        {
            Status($"{_mesh.DescribeNode(node)} is blocked — unblock it in Nodes to send a DM.");
            return;
        }
        // Enable DM for the node (so it's listed as a TX target) and select it as the current TX destination.
        _nodePrefs[node] = new DeviceCache.NodePrefs { Dm = true, Block = false };
        if (_currentHost.Length > 0) DeviceCache.SetNodePref(_currentHost, node, true, false);
        _chatTxDest = node;
        RebuildChatTxCombo();   // lists the DM target and selects it (it reads _chatTxDest)
        Status($"Direct message to {_mesh.DescribeNode(node)} — type your message and Send.");
        ChatBox.Focus();
    }

    /// <summary>Right-click → Request node info: asks the selected message's sender for its NodeInfo (name,
    /// hardware, role). Handy when a node messages us but isn't in the node list yet — the reply (handled by the
    /// poll loop) adds/updates it. Only received messages carry a sender.</summary>
    private async void RequestNodeInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null) return;
        if (ChatList.SelectedItem is not LogEntry entry || entry.Rx is not { } msg || msg.FromNode == 0)
        {
            Status("Select a received message to request its sender's node info.");
            return;
        }
        string who = _mesh.DescribeNode(msg.FromNode);
        Status($"Requesting node info from {who} (!{msg.FromNode:x8})…");
        try { await _mesh.RequestNodeInfoAsync(msg.FromNode); }
        catch (Exception ex) { Status($"Request failed: {ex.Message}"); }
    }

    /// <summary>Right-click → Node info: opens the full "all info" window for the selected message's sender —
    /// the same node-details + telemetry-history view as the Nodes list's "Show all info" button.</summary>
    private void NodeInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null) return;
        if (ChatList.SelectedItem is not LogEntry entry || entry.Rx is not { } msg || msg.FromNode == 0)
        {
            Status("Select a received message to see its sender's node info.");
            return;
        }
        var node = _mesh.GetNodes().FirstOrDefault(n => n.Num == msg.FromNode);
        if (node == null)
        {
            Status($"No node entry yet for !{msg.FromNode:x8} — use \"Request node info\" first, then try again.");
            return;
        }
        ShowTelemetryHistory(node, this);
    }

    /// <summary>Opens a node's most recent known location in Google Maps (default browser). When the node has never
    /// shared a position, pops up a warning (owned by <paramref name="owner"/>) so it's not a silent no-op. Successes
    /// and other errors go to the supplied status callback.</summary>
    private void OpenNodeInMaps(uint num, Window owner, Action<string> report)
    {
        if (_mesh == null) return;
        if (_mesh.GetNodePosition(num) is not { } pos)
        {
            string who = _mesh.DescribeNode(num);
            report($"No position data found for {who}.");
            MessageBox.Show(owner,
                $"No position data found for {who}.\n\nThis node hasn't shared its location yet. Use \"Request position\" to ask for it, then try again.",
                "No location", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string url = MeshtasticHttpClient.GoogleMapsUrl(pos.Latitude, pos.Longitude);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            report($"Opened {_mesh.DescribeNode(num)}'s last location in your browser.");
        }
        catch (Exception ex) { report($"Couldn't open the map: {ex.Message}"); }
    }

    /// <summary>Right-click on a chat message → Open location in Google Maps: opens the sender's last known fix.</summary>
    private void OpenMessageLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null) return;
        if (ChatList.SelectedItem is not LogEntry entry || entry.Rx is not { } msg || msg.FromNode == 0)
        {
            Status("Select a received message to open its sender's location.");
            return;
        }
        OpenNodeInMaps(msg.FromNode, this, Status);
    }

    /// <summary>Right-click → Remove message: deletes the selected chat row from the list and from the per-channel
    /// cache, so it won't reappear on reconnect. Also drops any in-flight/ack tracking tied to it.</summary>
    private void RemoveMessage_Click(object sender, RoutedEventArgs e)
    {
        if (ChatList.SelectedItem is not LogEntry entry || entry.Channel == uint.MaxValue)
        {
            Status("Select a chat message to remove.");
            return;
        }

        // Drop it from the display.
        ChatList.Items.Remove(entry);

        // Remove its cached copy (by stable id when we have one, else by exact text/detail for legacy rows).
        if (_currentHost.Length > 0)
            DeviceCache.RemoveChat(_currentHost, entry.Channel, entry.CacheId, entry.Text, entry.Detail);

        // If it was one of our own sent messages, stop tracking/retrying it.
        if (entry.PacketId != 0)
        {
            _pending.Remove(entry.PacketId);
            _chatAckers.Remove(entry.PacketId);
            _chatById.Remove(entry.PacketId);
            if (_replyToId == entry.PacketId) ClearReply();   // can't reply to a message we just deleted
        }
        ApplyConnectionState();   // a removed in-flight message no longer holds the send lock
    }

    private void ReplyCancel_Click(object sender, RoutedEventArgs e) => ClearReply();

    /// <summary>Clears the pending reply target and hides the reply banner.</summary>
    private void ClearReply()
    {
        _replyToId = 0;
        ReplyBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>A dim "↳ re: …" reference to the message a reply answers, or "" when not a reply. Quotes the
    /// original text when we have it cached, otherwise notes it's an earlier message.</summary>
    private string ReplyRef(uint replyId)
    {
        if (replyId == 0) return "";
        string original = _chatById.TryGetValue(replyId, out var t) ? Snippet(t, 40) : "(earlier message)";
        return $"↳ re: {original}";
    }

    private static string Snippet(string s, int max) => s.Length > max ? s.Substring(0, max) + "…" : s;

    /// <summary>True while a chat message is still awaiting its first ack (and hasn't timed out).
    /// Only one chat may be in flight at a time.</summary>
    // One chat in flight at a time — the next send waits until this one is confirmed (an explicit ack, an
    // overheard relay, or a reply on the channel) or times out. This applies to relay-confirm (ack-off) chats
    // and DMs too, so we don't flood the mesh faster than it can forward.
    private bool ChatInFlight => _pending.Values.Any(p => p.IsChat);

    private async void SendChat()
    {
        if (_mesh == null) return;
        // One chat in flight at a time — wait for it to be confirmed (acked, or relay-heard) or time out.
        if (ChatInFlight)
        {
            Status("Wait for your last chat to be confirmed (or time out) before sending another.");
            return;
        }
        string text = ChatBox.Text.Trim();
        if (text.Length == 0) return;

        // Brief hold after receiving a message so its CHATACK finishes sending before we transmit.
        var wait = _chatSendAllowedUtc - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            Status($"Acknowledging the last message — you can send in {Math.Ceiling(wait.TotalSeconds):0}s.");
            return;   // keep the text in the box
        }

        // Enforce the wire-length limit on what's actually transmitted: the plain text, or — if the chat
        // TX channel has an app key — the AES-256 base64 ciphertext (which is longer). Warn and don't send.
        string key = _mesh.GetChannelKey(_chatTxChannel);
        bool encrypted = key.Length > 0;
        int wireLen = encrypted ? AesText.Encrypt(text, key).Length : System.Text.Encoding.UTF8.GetByteCount(text);
        if (wireLen > MaxChatChars)
        {
            AddSystemWarning($"{Stamp()}Chat NOT sent — {wireLen} chars" +
                (encrypted ? " once encrypted" : "") + $" exceeds the {MaxChatChars}-char limit. Shorten your message.");
            return;   // keep the text in the box so it can be edited
        }

        ChatBox.Clear();
        bool isDm = _chatTxDest.HasValue;
        string dmName = isDm ? (_mesh.DescribeNode(_chatTxDest!.Value)) : "";
        // Show which channel we transmitted on (matching the [channel] prefix on received lines); for a DM,
        // show the recipient instead and a "DM" tag in the metadata.
        string chan = (!isDm && _chatListen.Count > 1) ? $"[{_chatTxChannel}] " : "";
        string msgLine = isDm ? $"You → {dmName}: {text}" : $"You: {text}";   // the prominent message line
        string dmTag = isDm ? "DM  " : "";
        // If replying, link this send to that message and show the quoted reference in the metadata.
        uint replyId = _replyToId;
        string replyRef = ReplyRef(replyId);
        ClearReply();
        string detailBase = $"{Stamp()}{dmTag}{chan}".Trim();    // dim metadata line; marks append here
        if (replyRef.Length > 0) detailBase = $"{replyRef}  ·  {detailBase}".Trim(' ', '·');
        // A DM is confirmed by the recipient's routing ack (handled in MarkAcked). On ack-off channels a
        // broadcast is confirmed by overhearing a relay instead of a CHATACK; either way the message is held
        // "in flight" (no new send) until confirmed or the retries run out.
        bool relayConfirm = isDm || !_chatAckOn.Contains(_chatTxChannel);

        var entry = AddChatLine(msgLine, detailBase + MarkSending, PendingText);   // amber while awaiting confirmation
        try
        {
            uint id = await _mesh.SendTextAsync(text, _chatTxChannel, destination: _chatTxDest, replyId: replyId);
            entry.PacketId = id;          // so this message can itself be replied to / reacted to
            entry.Channel = _chatTxChannel;
            RouteRx(entry, isDm, isDm ? _chatTxDest!.Value : 0, incoming: false);   // hide if its target is filtered out
            _chatEntryById[id] = entry;   // so reactions can attach to this row
            _chatById[id] = text;         // remember its text for quoting in future replies
            entry.CacheId = CacheChat(_chatTxChannel, msgLine, detailBase);   // persist (latest 100 per channel)
            _pending[id] = new PendingSend
            {
                Id = id,
                Payload = text,
                LastSentUtc = DateTime.UtcNow,
                Attempts = 1,
                IsMove = false,
                IsChat = true,
                IsRelayConfirm = relayConfirm,
                IsDm = isDm,
                ReplyId = replyId,
                Channel = _chatTxChannel,
                Label = isDm ? $"direct message to {dmName}" : "chat message",
                Entry = entry,
                BaseText = detailBase,   // for chat, BaseText holds the dim detail line (status marks append to it)
                // Latest moment we'll still be waiting if nothing is heard: each attempt waits AckTimeout, and
                // we make up to MaxSendAttempts of them before giving up. The Send button counts down to this;
                // a CHATACK / relay / reply removes the pending earlier, which cancels the countdown.
                SendDeadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(MaxSendAttempts * AckTimeout.TotalSeconds),
            };
            // Track acks for every channel broadcast (not DMs, which the firmware routing-acks): even when WE don't
            // send acks on this channel, the recipient might still ack us (their ack setting, or our keyword auto-ack),
            // and we should show that as "acked" — with RSSI when the ack carries it — rather than dropping it.
            if (!isDm) _chatAckers[id] = new ChatAckInfo { Entry = entry, BaseText = detailBase };
            ApplyConnectionState();   // disable Send until confirmed or the retries run out
        }
        catch (Exception ex)
        {
            entry.Detail = detailBase + MarkFailed;
            entry.Foreground = WarningText;   // red — couldn't send
            Status($"Chat could not be sent: {ex.Message}");
        }
    }


    // The device's rx_time (epoch s) for the message currently being dispatched, or 0 (use local now).
    private uint _incomingRxTime;

    /// <summary>Receive-time prefix for a log line: the device's rx_time (→ local) when dispatching an
    /// incoming message, otherwise this machine's local time. The timestamp is never transmitted.</summary>
    private string Stamp()
    {
        DateTime when = _incomingRxTime != 0
            ? DateTimeOffset.FromUnixTimeSeconds(_incomingRxTime).LocalDateTime
            : DateTime.Now;
        return $"[{when:MM-dd HH:mm:ss}] ";
    }

    private LogEntry AddChat(string line) => Append(ChatList, line);

    /// <summary>Adds a chat row: the message (prominent) plus a dim metadata line (timestamp/channel/signal/marks).
    /// <paramref name="rx"/> is the raw received message (null for sent rows) for the "More information" dialog.</summary>
    private LogEntry AddChatLine(string message, string detail, Brush color, MeshTextMessage? rx = null)
    {
        bool stick = IsScrolledToBottom(ChatList);
        var entry = new LogEntry { Text = message, Detail = detail, Foreground = color, Rx = rx };
        ChatList.Items.Add(entry);
        StickToBottom(ChatList, stick);
        return entry;
    }

    /// <summary>Logs a system/event message to the right-panel "System messages" list (never the chat).</summary>
    private LogEntry AddSystem(string line) => Append(SystemList, line);

    /// <summary>Logs a system/event message in red (for warnings/errors).</summary>
    private LogEntry AddSystemWarning(string line) => Append(SystemList, line, WarningText);

    /// <summary>Appends a row to a log list. Auto-scrolls to the new row only when the list was already
    /// at the bottom — so if you've scrolled up to read history, incoming rows don't yank you down.</summary>
    private static LogEntry Append(ListBox list, string text, Brush? color = null)
    {
        bool stick = IsScrolledToBottom(list);
        var entry = new LogEntry { Text = text, Foreground = color ?? NormalText };
        list.Items.Add(entry);
        StickToBottom(list, stick);
        return entry;
    }

    private static bool IsScrolledToBottom(ListBox list)
    {
        var sv = FindScrollViewer(list);
        if (sv == null) return true;   // not realized yet → treat as bottom so early rows scroll in
        return sv.VerticalOffset >= sv.ScrollableHeight - 2.0;
    }

    /// <summary>Scrolls a log list to the very bottom — but only when it was already pinned there
    /// (<paramref name="wasAtBottom"/>), so reading scrolled-up history isn't interrupted. Deferred to a
    /// later dispatcher pass so it runs AFTER WPF has laid out the just-added/just-grown row; otherwise
    /// ScrollIntoView fires before the (possibly multi-line) row is realized and stops short of the bottom.
    /// Call after adding a row OR mutating an existing row's Text/Detail (e.g. when ack info arrives).</summary>
    private static void StickToBottom(ListBox list, bool wasAtBottom)
    {
        if (!wasAtBottom) return;
        list.Dispatcher.BeginInvoke(new Action(() => FindScrollViewer(list)?.ScrollToEnd()),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is ScrollViewer sv) return sv;
        if (root == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    // ---- Copy moves / chat -----------------------------------------------------------

    private static string ItemText(object item) =>
        item is LogEntry e ? (e.Detail.Length > 0 ? $"{e.Text}   {e.Detail}" : e.Text) : item?.ToString() ?? "";

    private static void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { /* clipboard can be momentarily unavailable */ }
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && sender is ListBox lb)
        {
            CopyToClipboard(string.Join(Environment.NewLine, lb.SelectedItems.Cast<object>().Select(ItemText)));
            e.Handled = true;
        }
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (TargetList(sender) is { } lb)
            CopyToClipboard(string.Join(Environment.NewLine, lb.SelectedItems.Cast<object>().Select(ItemText)));
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (TargetList(sender) is { } lb)
            CopyToClipboard(string.Join(Environment.NewLine, lb.Items.Cast<object>().Select(ItemText)));
    }

    private static ListBox? TargetList(object menuItemSender) =>
        ((menuItemSender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget as ListBox;

    /// <summary>Right-click "More information": shows the full signal/relay breakdown for the selected
    /// received chat message — including every candidate relay node (since relay_node is only a last byte).</summary>
    private void MessageInfo_Click(object sender, RoutedEventArgs e)
    {
        if (TargetList(sender) is not ListBox lb || lb.SelectedItem is not LogEntry entry)
            return;

        string baseText;
        if (entry.Rx is { } msg)
            // Received messages carry their own signal/relay info.
            baseText = BuildMessageDetails(msg);
        else if (_chatAckers.Values.FirstOrDefault(i => i.Entry == entry) is { Ackers.Count: > 0 } ackInfo)
            // Sent chat message: the acknowledgement(s) we received, with the acker's reported signal.
            baseText = BuildAckDetails(ackInfo);
        else if (!string.IsNullOrEmpty(entry.RelayAckInfo))
            // Sent broadcast confirmed by an implicit relay ack.
            baseText = $"Delivered (heard rebroadcast on the mesh):{Environment.NewLine}  {entry.RelayAckInfo}";
        else
            baseText = "No additional details for this line (only messages received over the mesh, or sent " +
                       "messages that have been acknowledged, carry signal/relay information).";

        // Append who reacted, if anyone has.
        string reactions = ReactionDetails(entry.PacketId);
        if (reactions.Length > 0) baseText = $"{baseText}{Environment.NewLine}{Environment.NewLine}{reactions}";

        ShowNotice("Message details", baseText);
    }

    /// <summary>"Reactions:" plus a line per emoji listing the nodes that reacted with it (or "you"). "" when none.</summary>
    private string ReactionDetails(uint packetId)
    {
        if (packetId == 0 || !_reactions.TryGetValue(packetId, out var list) || list.Count == 0) return "";
        var lines = new List<string> { "Reactions:" };
        foreach (var g in list.GroupBy(r => r.Emoji))
        {
            string who = string.Join(", ", g.Select(r => r.Node == _mesh!.MyNodeNum ? "you" : _mesh!.DescribeNode(r.Node)));
            lines.Add($"  {g.Key}  {who}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildAckDetails(ChatAckInfo info)
    {
        var lines = new List<string> { $"Acknowledged by {info.Ackers.Count} node{(info.Ackers.Count == 1 ? "" : "s")}:" };
        foreach (var a in info.Ackers)
        {
            lines.Add($"  • {_mesh?.DescribeNode(a)}  (!{a:x8})");
            // Direction 1: how the acker heard our original message (reported back inside its ack).
            lines.Add(info.AckerSignals.TryGetValue(a, out var them) && them.Length > 0
                ? $"      how they heard your message: {them}"
                : "      how they heard your message: not reported (enable \"…with RSSI/SNR/hops\" on the channel to include it)");
            // Direction 2: how our node heard their ack packet coming back.
            lines.Add(info.MyReception.TryGetValue(a, out var me) && me.Length > 0
                ? $"      how your node heard their ack: {me}"
                : "      how your node heard their ack: no signal reported by your radio");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildMessageDetails(MeshTextMessage msg)
    {
        var lines = new List<string>
        {
            $"From: {_mesh?.DescribeNode(msg.FromNode)}  (!{msg.FromNode:x8})",
            $"Channel: {msg.Channel}",
        };
        DateTime when = msg.RxTime != 0 ? DateTimeOffset.FromUnixTimeSeconds(msg.RxTime).LocalDateTime : DateTime.Now;
        lines.Add($"Received: {when:yyyy-MM-dd HH:mm:ss}");
        lines.Add($"Packet id: {msg.PacketId}");
        lines.Add(msg.Hops is int hops ? $"Hops: {hops}{(hops == 0 ? " (direct)" : "")}" : "Hops: unknown (older sender firmware)");
        if (msg.RxRssi != 0 || msg.RxSnr != 0)
            lines.Add($"Last-hop signal: RSSI {msg.RxRssi} dBm · SNR {msg.RxSnr:0.#} dB");

        if (msg.RelayNode == 0)
            lines.Add("Relay node: not reported by the sender's firmware");
        else
        {
            lines.Add($"Relay node (last byte of the node id): 0x{msg.RelayNode:x2}");
            var candidates = _mesh?.GetNodes().Where(n => (byte)(n.Num & 0xFF) == msg.RelayNode).ToList() ?? new List<MeshNode>();
            if (candidates.Count == 0)
                lines.Add("  No known node ends in this byte — use Nodes… → Update nodes to try to resolve it.");
            else
            {
                lines.Add(candidates.Count == 1
                    ? "  The relay was:"
                    : $"  relay_node is only one byte, so it could be any of these {candidates.Count} known nodes that share it:");
                lines.AddRange(candidates.Select(n => $"   • {n.Display}  (!{n.Num:x8})"));
            }
        }
        if (msg.DecryptFailed) lines.Add("⚠ Decryption failed (wrong/missing channel key).");
        return string.Join(Environment.NewLine, lines);
    }

    // ---- Delivery acknowledgement / retry --------------------------------------------

    /// <summary>
    /// A mesh routing ACK for one of our sent packets. Moves and ack-tracked chat are confirmed by their
    /// own application-level ACK/CHATACK (the routing ack proved unreliable for those). But ack-OFF chat
    /// uses this: for a broadcast sent with want_ack, the firmware emits a routing ack as an *implicit*
    /// acknowledgement once it hears the message rebroadcast (relayed) on the mesh — exactly the "another
    /// node retransmitted it" signal we want.
    /// </summary>
    private void MarkAcked(MeshAck ack)
    {
        if (!_pending.TryGetValue(ack.PacketId, out var p) || !p.IsChat) return;
        bool stick = IsScrolledToBottom(ChatList);   // updating the row's detail below can grow it

        if (p.IsDm)
        {
            // A relay ack (we overheard our DM rebroadcast — FromNode 0) only proves it propagated, NOT that the
            // recipient got it. Show "relayed" but keep waiting for the firmware's routing verdict (delivered/NAK).
            if (ack.FromNode == 0)
            {
                if (!ack.Failed)
                {
                    p.RelayHeard = true;
                    if (p.Entry != null)
                    {
                        p.Entry.RelayAckInfo = RelayDescription(ack, includeSignal: true).Trim();
                        p.Entry.Detail = p.BaseText + MarkDelivered + RelayDescription(ack, includeSignal: _showSignal);
                        p.Entry.Foreground = RelayedText;   // teal — relayed; still awaiting delivery confirmation
                    }
                    StickToBottom(ChatList, stick);
                }
                return;   // not terminal — the recipient's routing ack (or the timeout) decides delivered/failed
            }

            // The firmware's Routing verdict from the recipient/an intermediate (FromNode set): an ack = delivered,
            // a NAK = failed. Terminal — there's nothing for us to resend.
            _pending.Remove(ack.PacketId);
            if (p.Entry != null)
            {
                if (ack.Failed)
                {
                    p.Entry.Detail = p.BaseText + MarkFailed + $" not delivered ({ack.FailReason})";
                    p.Entry.Foreground = WarningText;   // red — the recipient never acknowledged
                    Status($"Direct message not delivered ({ack.FailReason}).");
                }
                else
                {
                    p.Entry.Detail = p.BaseText + MarkDelivered + " delivered";
                    p.Entry.Foreground = AckedText;     // green — acknowledged by the recipient
                }
            }
            StickToBottom(ChatList, stick);
            ApplyConnectionState();   // release — the next chat can be sent
            return;
        }

        // Broadcast relay ack (we overheard a rebroadcast). Only a success counts; ignore a NAK.
        if (ack.Failed) return;

        // Inline detail honours the "RSSI" checkbox; the full version (always with signal) is kept on the
        // row so "More information" can show RSSI/SNR/hops even when the checkbox hides them inline.
        if (p.Entry != null)
        {
            p.Entry.RelayAckInfo = RelayDescription(ack, includeSignal: true).Trim();
            p.Entry.Detail = p.BaseText + MarkDelivered + RelayDescription(ack, includeSignal: _showSignal);
            p.Entry.Foreground = RelayedText;   // teal — confirmed by overhearing a rebroadcast
        }
        StickToBottom(ChatList, stick);

        if (p.IsRelayConfirm)
        {
            // Ack-off channel: a relay is the only confirmation we get, so it's terminal.
            _pending.Remove(ack.PacketId);
            ApplyConnectionState();   // release — the next chat can be sent
        }
        else
        {
            // Ack-on channel: "relayed" is an INTERMEDIATE state — keep the message in flight and keep waiting for
            // the channel ack (which will turn it green). If no ack arrives, it stays relayed (see the timeout).
            p.RelayHeard = true;
        }
    }

    /// <summary>" relayed by &lt;node&gt; [RSSI/SNR]" for a relay ack that named the relayer, else " relayed".
    /// The RSSI/SNR is only included when <paramref name="includeSignal"/> is true (the inline chat detail
    /// honours the "RSSI" checkbox; the "More information" copy always includes it).</summary>
    private string RelayDescription(MeshAck ack, bool includeSignal)
    {
        // Prefer the firmware-reported last-hop relayer; otherwise the node the (implicit) ack came from.
        // Note: older radio firmware reports neither (relay_node absent, implicit ack from our own node),
        // in which case the relayer is simply not in the data and we just say "relayed".
        string who = _mesh?.DescribeRelayNode(ack.RelayNode) ?? "";
        if (who.Length == 0 && ack.FromNode != 0 && ack.FromNode != (_mesh?.MyNodeNum ?? 0))
            who = _mesh?.DescribeNode(ack.FromNode) ?? "";
        if (who.Length == 0) return " relayed";
        string sig = (includeSignal && (ack.RxRssi != 0 || ack.RxSnr != 0)) ? $" [RSSI {ack.RxRssi} dBm · SNR {ack.RxSnr:0.#} dB]" : "";
        return $" relayed by {who}{sig}";
    }

    /// <summary>Acknowledge a chat message we received, so its sender can see we got it. Sent on the
    /// channel the message arrived on (which may differ from the channel we transmit chat on).</summary>
    private async void SendChatAck(uint chatPacketId, uint channel, string signal = "")
    {
        if (_mesh == null) return;
        await AckJitterDelay();
        try { await _mesh.SendTextAsync(ProtocolMessage.EncodeChatAck(chatPacketId, signal), channel, encrypt: false); }
        catch { /* best effort */ }
    }

    /// <summary>Compact RSSI/SNR/hops description of a received message, for embedding in a chat ack
    /// (no '|', no brackets). Empty when the device reported no signal and the hop count is unknown.</summary>
    private static string AckSignalText(MeshTextMessage m)
    {
        if (m.Hops is int hops && hops > 0) return $"{hops} hop{(hops == 1 ? "" : "s")}";
        if (m.RxRssi != 0 || m.RxSnr != 0) return $"{(m.IsDirect ? "direct " : "")}RSSI {m.RxRssi} dBm SNR {m.RxSnr:0.#} dB";
        return m.IsDirect ? "direct" : "";
    }

    /// <summary>Strips the "RSSI … dB" portion from an <see cref="AckSignalText"/> string, leaving the hop /
    /// "direct" prefix intact — so the "RSSI" checkbox can hide signal while the hop count stays visible.
    /// "direct RSSI -45 dBm SNR 8 dB" → "direct"; "2 hops" → "2 hops"; "" → "".</summary>
    private static string StripRssi(string s)
    {
        int i = s.IndexOf("RSSI", StringComparison.Ordinal);
        return i < 0 ? s : s.Substring(0, i).Trim();
    }

    /// <summary>Records that <paramref name="ackerNum"/> acknowledged one of our chat messages.</summary>
    private void RegisterChatAck(uint chatPacketId, uint ackerNum, string signal = "", string myReception = "")
    {
        if (!_chatAckers.TryGetValue(chatPacketId, out var info)) return;
        if (!info.Ackers.Add(ackerNum)) return;          // already recorded this acker
        if (!string.IsNullOrEmpty(signal)) info.AckerSignals[ackerNum] = signal;
        if (!string.IsNullOrEmpty(myReception)) info.MyReception[ackerNum] = myReception;
        bool wasInFlight = ChatInFlight;
        _pending.Remove(chatPacketId);                   // delivered — stop retrying
        UpdateChatAckerText(info);
        if (wasInFlight) ApplyConnectionState();         // ack received — allow sending again
    }

    /// <summary>A message received on the channel our in-flight chat was sent to is taken as an implicit
    /// acknowledgement: the recipient is clearly active on that channel, so mark our message delivered
    /// (green) and release the in-flight lock so another chat can be sent.</summary>
    private void ConfirmChatByIncomingReply(uint channel)
    {
        // DMs are confirmed solely by the firmware's Routing verdict (see MarkAcked), not by an unrelated
        // reply on the same channel — so only broadcast chat is confirmed this way.
        var p = _pending.Values.FirstOrDefault(x => x.IsChat && !x.IsDm && x.Channel == channel);
        if (p == null) return;
        bool stick = IsScrolledToBottom(ChatList);
        _pending.Remove(p.Id);
        _chatAckers.Remove(p.Id);
        if (p.Entry != null) { p.Entry.Detail = p.BaseText + MarkDelivered + " (reply received)"; p.Entry.Foreground = AckedText; }
        StickToBottom(ChatList, stick);
        ApplyConnectionState();   // release — the next chat can be sent
    }

    /// <summary>Re-renders every chat ack line (e.g. once the node list resolves numbers to names).</summary>
    // The radio stamps received packets (and each node's "last heard") with its own clock. If that clock is set
    // ahead of real time, stamps land in the future. When we have positive evidence of that — a node heard later
    // than "now" — push this computer's time to the radio (set_time_only). Conservative on purpose: we only write
    // when clearly ahead (a future stamp can't come from anything but a fast radio clock), never to "fix" a clock
    // that merely looks behind (indistinguishable from a quiet mesh). Fire-and-forget, best-effort.
    private async void SyncDeviceClockIfAhead()
    {
        var mesh = _mesh;
        if (mesh == null) return;
        long nowS = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long deviceProxy = mesh.GetNodes().Select(n => n.LastHeard).DefaultIfEmpty(0).Max();   // most recent device-clock stamp
        long aheadBy = deviceProxy - nowS;
        if (aheadBy < 120) return;   // not meaningfully ahead — leave the clock alone
        string ahead = aheadBy >= 86400 ? $"{aheadBy / 86400}d" : aheadBy >= 3600 ? $"{aheadBy / 3600}h" : $"{aheadBy / 60}m";
        try
        {
            var err = await mesh.SetDeviceTimeAsync((uint)nowS);
            AddSystem(Stamp() + (err == null
                ? $"Device clock was ~{ahead} ahead - corrected from this computer."
                : $"Couldn't correct the device clock (~{ahead} ahead): {err}"));
        }
        catch { /* best-effort - time sync never blocks the connection */ }
    }

    private void RefreshChatAckerNames()
    {
        foreach (var info in _chatAckers.Values)
            if (info.Ackers.Count > 0)
                UpdateChatAckerText(info);
    }

    private void UpdateChatAckerText(ChatAckInfo info)
    {
        bool stick = IsScrolledToBottom(ChatList);
        string names = string.Join(", ", info.Ackers.Select(a =>
        {
            string n = _mesh?.DescribeNode(a) ?? a.ToString();
            // Hop info ("2 hops" / "direct") always shows; only the RSSI/SNR portion is gated by the "RSSI"
            // checkbox. The full underlying values stay stored on the ChatAckInfo for "More information".
            string Show(string raw) => _showSignal ? raw : StripRssi(raw);
            var sig = new List<string>();
            if (info.AckerSignals.TryGetValue(a, out var them) && them.Length > 0 && Show(them) is { Length: > 0 } t) sig.Add($"them: {t}");
            if (info.MyReception.TryGetValue(a, out var me) && me.Length > 0 && Show(me) is { Length: > 0 } m) sig.Add($"me: {m}");
            return sig.Count > 0 ? $"{n} [{string.Join(" · ", sig)}]" : n;
        }));
        info.Entry.Detail = $"{info.BaseText} {MarkDelivered.Trim()} acked by: {names}";
        info.Entry.Foreground = AckedText;   // green — acknowledged
        StickToBottom(ChatList, stick);      // ack text can grow the row → keep it in view
    }

    /// <summary>An opponent reply proves our last move arrived; mark it delivered.</summary>
    private void ConfirmPendingMove()
    {
        var move = _pending.Values.FirstOrDefault(p => p.IsMove);
        if (move == null) return;
        _pending.Remove(move.Id);
        if (move.Entry != null) { move.Entry.Text = move.BaseText + MarkDelivered; move.Entry.Foreground = AckedText; }
        ApplyConnectionState();   // grey Resend if this move had been awaiting it
    }

    /// <summary>
    /// Resends a pending message under a NEW packet id. The mesh dedupes by packet id, so reusing
    /// the original id would be silently dropped and never reach the recipient. Move acks match by
    /// ply (id-independent); chat acks match by id, so the ack-tracking is re-keyed to the new id.
    /// </summary>
    private async Task ResendPendingAsync(PendingSend p)
    {
        if (_mesh == null) return;
        try
        {
            uint oldId = p.Id;
            // Chat resends go on the chat TX channel (marked so the receiver knows it's a resend); everything
            // else (moves/control) on the chess channel. A fresh id avoids mesh dedupe either way.
            uint newId;
            if (p.IsChat) { p.Channel = _chatTxChannel; newId = await _mesh.SendTextAsync(ProtocolMessage.ChatResendPrefix + p.Payload, _chatTxChannel, replyId: p.ReplyId); }
            else newId = await _mesh.SendTextAsync(p.Payload);
            if (newId == oldId) return;
            if (_pending.Remove(oldId)) { p.Id = newId; _pending[newId] = p; }
            if (_chatAckers.Remove(oldId, out var info)) _chatAckers[newId] = info;
        }
        catch { /* a failed resend just means another timeout/retry next cycle */ }
    }

    private async Task CheckPendingAcksAsync()
    {
        CheckReceptionStall();   // piggyback the 1s timer for the reception watchdog
        if (_checkingAcks || _mesh == null || !_synced || _pending.Count == 0) return;
        _checkingAcks = true;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var p in _pending.Values.ToList())
            {
                if (p.NeedsResend) continue;   // exhausted auto-retries; waiting on the manual Resend button
                if (now - p.LastSentUtc < AckTimeout) continue;

                if (p.Attempts < MaxSendAttempts)
                {
                    p.Attempts++;
                    p.LastSentUtc = now;
                    if (p.IsDm)
                    {
                        // The firmware does its own hop-by-hop retransmit for a DM and reports an ack or a
                        // NAK, so we never resend it ourselves — just keep waiting for the firmware's verdict.
                    }
                    else
                    {
                        // Auto-resend up to the attempt limit.
                        string missing = p.IsRelayConfirm ? "relay heard" : "acknowledgement";
                        Status($"No {missing} for {p.Label} — resending (attempt {p.Attempts}/{MaxSendAttempts})...");
                        await ResendPendingAsync(p);
                    }
                }
                else if (p.IsMove)
                {
                    // Auto-retries exhausted: no prompt. Flag the move for manual resend (red), and the
                    // Resend button in the moves panel lights up so the user can try it again.
                    p.NeedsResend = true;
                    if (p.Entry != null) p.Entry.Foreground = WarningText;   // red — not acknowledged
                    Status($"{p.Label} not acknowledged after {MaxSendAttempts} attempts — press Resend to try again.");
                    ApplyConnectionState();
                }
                else if (p.IsJoin)
                {
                    // No JOINACK from the host after the attempt limit — abort the join.
                    _pending.Remove(p.Id);
                    FailJoin($"The host did not respond after {MaxSendAttempts} attempts.");
                }
                else if (p.IsResign)
                {
                    // No RESIGNACK after the attempt limit — keep the game and let the user retry.
                    _pending.Remove(p.Id);
                    FailResign($"No response after {MaxSendAttempts} attempts.");
                }
                else if (p.IsCreate)
                {
                    // No CREATEACK after the attempt limit — nobody heard the new game; abort it.
                    _pending.Remove(p.Id);
                    FailCreate($"No one responded after {MaxSendAttempts} attempts.");
                }
                else if (p.IsSave)
                {
                    // No SAVEACK after the attempt limit — give up; the game continues.
                    _pending.Remove(p.Id);
                    FailSave($"No response after {MaxSendAttempts} attempts.");
                }
                else if (p.IsDm)
                {
                    // DM window elapsed with no firmware verdict (no ack and no NAK). If we at least overheard it
                    // relayed, leave it in the teal "relayed" state (note it was never confirmed delivered);
                    // otherwise the recipient is likely offline/out of range — leave it as plainly sent.
                    _pending.Remove(p.Id);
                    if (p.RelayHeard)
                    {
                        if (p.Entry != null) p.Entry.Detail += " (no delivery confirmation)";   // row already shows "relayed by …" in teal
                        Status("Direct message relayed but not confirmed delivered (the recipient may be offline).");
                    }
                    else
                    {
                        if (p.Entry != null) { p.Entry.Detail = p.BaseText + MarkSent + " (no delivery confirmation)"; p.Entry.Foreground = NormalText; }
                        Status("No delivery confirmation from the recipient — it may be offline or out of range.");
                    }
                    ApplyConnectionState();
                }
                else if (p.IsRelayConfirm)
                {
                    // Ack-off chat: no relay heard after the attempt limit — it went out but we couldn't
                    // confirm anyone relayed it. A rebroadcast is the only delivery signal on an ack-off
                    // channel and we never saw one, so treat it as a failed send (red ✗) and allow sending again.
                    _pending.Remove(p.Id);
                    if (p.Entry != null) { p.Entry.Detail = p.BaseText + MarkFailed + " (no relay heard)"; p.Entry.Foreground = WarningText; }
                    Status("No relay heard — message failed; you can send another.");
                    ApplyConnectionState();
                }
                else
                {
                    // Ack-tracked chat: no channel ack after the attempt limit. If we at least overheard a relay,
                    // leave it in the (teal) relayed state; otherwise mark it failed. Either way, release the lock.
                    _pending.Remove(p.Id);
                    if (p.RelayHeard) Status("Relayed but not acknowledged — message was sent.");   // row already shows the relayed state
                    else FailPending(p);
                    ApplyConnectionState();
                }
            }
        }
        finally
        {
            _checkingAcks = false;
        }
    }

    /// <summary>A chat message gave up after the retry limit — mark it failed (✗, red).</summary>
    private void FailPending(PendingSend p)
    {
        if (p.Entry != null) { p.Entry.Detail = p.BaseText + MarkFailed; p.Entry.Foreground = WarningText; }
        Status("A chat message was not delivered.");
    }

    // ---- Nodes config dialog ---------------------------------------------------------

    private void NodesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null) return;

        var dialog = new Window
        {
            Title = "Nodes",
            Owner = this,
            Width = 520,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x25, 0x26)),
        };

        var root = new DockPanel { Margin = new Thickness(10) };

        var header = new TextBlock
        {
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xE0, 0xE0)),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(header, Dock.Top);

        var status = new TextBlock
        {
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xB0, 0xB0, 0xB0)),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        DockPanel.SetDock(status, Dock.Bottom);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var updateBtn = new Button { Content = "Update nodes", Width = 110, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
        var mapBtn = new Button { Content = "Map", Width = 70, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
        mapBtn.Click += (_, _) => ShowMap();
        var closeBtn = new Button { Content = "Close", Width = 80, Height = 28 };
        buttons.Children.Add(updateBtn);
        buttons.Children.Add(mapBtn);
        buttons.Children.Add(closeBtn);

        var greyBrush = new SolidColorBrush(MediaColor.FromRgb(0xB0, 0xB0, 0xB0));
        var nameBrush = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xE0, 0xE0));
        var consolas = new System.Windows.Media.FontFamily("Consolas");

        // node num → (row text cell, node), rebuilt by Populate; lets telemetry refresh rows without a rebuild.
        var envCells = new Dictionary<uint, (TextBlock Cell, MeshNode Node)>();


        // Toolbar: a search box (filters by name/short name/!hex) and a sort selector (Name / DM / Blocked).
        var controls = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(controls, Dock.Top);

        var sortCombo = new ComboBox { Width = 110, Height = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        foreach (var s in new[] { "Name", "Type", "Hardware", "Heard", "Signal", "DM", "Blocked", "Environment" }) sortCombo.Items.Add(s);
        sortCombo.SelectedIndex = 0;
        var sortLabel = new TextBlock { Text = "Sort:", Foreground = greyBrush, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(sortCombo, Dock.Right);
        DockPanel.SetDock(sortLabel, Dock.Right);
        controls.Children.Add(sortCombo);
        controls.Children.Add(sortLabel);

        var searchLabel = new TextBlock { Text = "Search:", Foreground = greyBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var searchBox = new TextBox
        {
            Height = 22, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = nameBrush,
            CaretBrush = nameBrush,
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3F, 0x3F, 0x46)),
        };
        DockPanel.SetDock(searchLabel, Dock.Left);
        controls.Children.Add(searchLabel);
        controls.Children.Add(searchBox);   // last child fills the remaining width

        var list = new ListBox
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(0x1E, 0x1E, 0x1E)),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        // No horizontal scroll: rows fit the width, so the right-docked DM/Block checkboxes are always visible
        // and the (now longer) node info truncates with an ellipsis instead of pushing them off-screen.
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

        // One row per node: the name, plus DM and Block checkboxes (own node has neither). Block wins over DM
        // (checking Block clears+disables DM), matching the persisted rule in DeviceCache.SetNodePref.
        FrameworkElement BuildNodeRow(MeshNode n)
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };
            if (!n.IsSelf)
            {
                var pref = _nodePrefs.GetValueOrDefault(n.Num);
                bool dmOn = pref?.Dm == true;
                bool blockOn = pref?.Block == true;

                var dmCb = new CheckBox
                {
                    Content = "DM", IsChecked = dmOn, IsEnabled = !blockOn, Foreground = greyBrush,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Width = 46,
                    ToolTip = "List this node as a TX target so you can send it direct messages.",
                };
                var blkCb = new CheckBox
                {
                    Content = "Block", IsChecked = blockOn, Foreground = greyBrush,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), Width = 62,
                    ToolTip = "Ignore incoming direct messages from this node.",
                };

                bool updating = false;
                void Apply()
                {
                    if (updating) return;
                    bool b = blkCb.IsChecked == true;
                    bool d = !b && dmCb.IsChecked == true;
                    if (!d && !b) _nodePrefs.Remove(n.Num);
                    else _nodePrefs[n.Num] = new DeviceCache.NodePrefs { Dm = d, Block = b };
                    if (_currentHost.Length > 0) DeviceCache.SetNodePref(_currentHost, n.Num, d, b);
                    RebuildChatTxCombo();   // reflect the change in the chat TX dropdown immediately
                }
                blkCb.Checked += (_, _) => { updating = true; dmCb.IsChecked = false; dmCb.IsEnabled = false; updating = false; Apply(); };
                blkCb.Unchecked += (_, _) => { dmCb.IsEnabled = true; Apply(); };
                dmCb.Checked += (_, _) => Apply();
                dmCb.Unchecked += (_, _) => Apply();

                DockPanel.SetDock(blkCb, Dock.Right);
                DockPanel.SetDock(dmCb, Dock.Right);
                row.Children.Add(blkCb);
                row.Children.Add(dmCb);
            }

            // Right-click menu for EVERY node — including our own ★ node, which has no DM/Block boxes. Opens the full
            // info window or the node's last location on a map; our own node also gets the manual broadcasts.
            var menu = new ContextMenu();
            var showTelemItem = new MenuItem { Header = "Show all info" };
            showTelemItem.Click += (_, _) => ShowTelemetryHistory(n, dialog);
            var mapItem = new MenuItem { Header = "Open last location in Google Maps" };
            mapItem.Click += (_, _) => OpenNodeInMaps(n.Num, dialog, s => status.Text = s);
            menu.Items.Add(showTelemItem);
            menu.Items.Add(mapItem);
            // Our own node: offer the manual broadcasts (announce this node / its position to the whole mesh).
            if (n.IsSelf)
            {
                menu.Items.Add(new Separator());
                var bcastInfoItem = new MenuItem { Header = "Broadcast my node info" };
                bcastInfoItem.Click += async (_, _) =>
                {
                    status.Text = "Broadcasting node info to the mesh…";
                    try { await _mesh!.BroadcastOwnNodeInfoAsync(); status.Text = "Node info broadcast sent."; }
                    catch (Exception ex) { status.Text = $"Broadcast failed: {ex.Message}"; }
                };
                var bcastPosItem = new MenuItem { Header = "Broadcast my position" };
                bcastPosItem.Click += async (_, _) =>
                {
                    status.Text = "Broadcasting position to the mesh…";
                    try { await _mesh!.BroadcastOwnPositionAsync(); status.Text = "Position broadcast sent."; }
                    catch (Exception ex) { status.Text = $"Broadcast failed: {ex.Message}"; }
                };
                menu.Items.Add(bcastInfoItem);
                menu.Items.Add(bcastPosItem);
            }
            // Other nodes: let the user forget the node on the device. Recovers DMs after the node reinstalled its
            // firmware (its stored PKI public key is stale) — once removed, the device re-learns the new key.
            else
            {
                menu.Items.Add(new Separator());
                var removeItem = new MenuItem { Header = "Remove from device (forget node)" };
                removeItem.Click += async (_, _) =>
                {
                    if (MessageBox.Show(dialog,
                            $"Remove {n.Display} from the device's node database?\n\n" +
                            "Use this when the node reinstalled its firmware and direct messages stopped working " +
                            "(its stored public key is stale). After removal the device re-learns the node — and its " +
                            "new key — the next time it hears from it.",
                            "Remove node", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;
                    status.Text = $"Removing {n.Display} from the device…";
                    try
                    {
                        await _mesh!.RemoveNodeAsync(n.Num);
                        if (_currentHost.Length > 0) DeviceCache.RemoveNode(_currentHost, n.Num);
                        _nodePrefs.Remove(n.Num);
                        RebuildChatTxCombo();
                        _nodesRepopulate?.Invoke();
                        status.Text = $"Removed {n.Display}. It will reappear (with a fresh key) when next heard — then DM should work.";
                    }
                    catch (Exception ex) { status.Text = $"Remove failed: {ex.Message}"; }
                };
                menu.Items.Add(removeItem);
            }
            row.ContextMenu = menu;

            string rowText = NodeRowText(n);
            var name = new TextBlock
            {
                Text = rowText,
                ToolTip = rowText,   // full text on hover (the row truncates with an ellipsis)
                Foreground = nameBrush,
                FontFamily = consolas,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            envCells[n.Num] = (name, n);   // so live telemetry can update this row's text in place
            row.Children.Add(name);   // last child fills the remaining width
            return row;
        }

        // The row text: name (+ ★ for self), with an environment summary appended for nodes that send
        // telemetry — e.g. "… — !a1b2c3d4   21.5°C · 45%RH · dp 9.3°C".
        string NodeRowText(MeshNode n)
        {
            string text = (n.IsSelf ? "★ " : "") + n.Display;
            // Per-node hardware comes from its User broadcast; for our own node fall back to the device metadata.
            string hw = !string.IsNullOrEmpty(n.HwModel) ? n.HwModel : (n.IsSelf ? _mesh!.HardwareModel ?? "" : "");
            if (hw.Length > 0) text += $"   {hw}";
            if (!string.IsNullOrEmpty(n.Role)) text += $"   [{n.Role}]";
            // Last-heard uses the radio's clock; a radio set ahead of real time stamps it in the future. Show the
            // actual date and flag it instead of the misleading "just now" AgoText gives a future stamp.
            if (n.LastHeard > 0)
                text += DateTimeOffset.FromUnixTimeSeconds(n.LastHeard) > DateTimeOffset.UtcNow.AddMinutes(1)
                    ? $"   heard {DateTimeOffset.FromUnixTimeSeconds(n.LastHeard).LocalDateTime:yyyy-MM-dd HH:mm} (device clock ahead)"
                    : $"   heard {AgoText(n.LastHeard)}";
            if (!n.IsSelf && _mesh!.GetSignal(n.Num) is { } sig)
                text += $"   {SignalSummary(sig)}";
            if (_mesh!.GetEnvironment(n.Num) is { } e)
                text += $"   {EnvSummary(e)} · @ {e.Timestamp:HH:mm:ss}";
            return text;
        }

        // Formats a node's latest radio metrics. RSSI/SNR only describe the node's own link when we heard it
        // directly (0 hops); for a relayed packet they're the last-hop relay's signal, so it's labelled as such.
        static string SignalSummary((int Rssi, float Snr, int? Hops, long When) s)
        {
            string sig = $"RSSI {s.Rssi} dBm · SNR {s.Snr:0.#} dB";
            return s.Hops switch
            {
                0      => $"{sig} · direct",
                null   => sig,                                  // hop count unknown
                1      => $"via 1 hop · last-hop {sig}",
                var h  => $"via {h} hops · last-hop {sig}",
            };
        }

        // Name used for the secondary (alphabetical) ordering and as the search target's primary part.
        static string SortName(MeshNode n) =>
            !string.IsNullOrWhiteSpace(n.LongName) ? n.LongName
            : !string.IsNullOrWhiteSpace(n.ShortName) ? n.ShortName
            : $"~{n.Num:x8}";   // unnamed nodes sort after named ones

        const string DefaultHint = "DM: list a node as a chat TX target for direct messages.  Block: ignore its DMs.  " +
                                    "\"Update nodes\" refreshes the list from the device.";

        void Populate()
        {
            list.Items.Clear();
            envCells.Clear();
            string query = (searchBox.Text ?? "").Trim();
            string mode = sortCombo.SelectedItem as string ?? "Name";

            var allNodes = _mesh!.GetNodes();
            // Include nodes we hold DM/Block prefs for that the device's node DB doesn't know — e.g. a DM
            // arrived from a node we never received NodeInfo for (EnsureDmEnabled flips its DM flag on), or a
            // cached pref outlived the node. Without a row here such a node is listed as a TX target
            // ("DM → !hex") yet impossible to un-check; a placeholder row makes its prefs toggleable.
            var known = new HashSet<uint>(allNodes.Select(n => n.Num));
            var orphans = _nodePrefs.Keys
                .Where(num => !known.Contains(num) && num != _mesh.MyNodeNum)
                .Select(num => new MeshNode(num, string.Empty, string.Empty, false));
            var nodes = allNodes.Concat(orphans);
            if (query.Length > 0)
                nodes = nodes.Where(n => n.Display.Contains(query, StringComparison.OrdinalIgnoreCase));

            // Own node always pins to the top; then the chosen sort, with name as the tiebreaker.
            var ordered = nodes.OrderByDescending(n => n.IsSelf);
            ordered = mode switch
            {
                "Type"        => ordered.ThenBy(n => string.IsNullOrEmpty(n.Role) ? "￿" : n.Role, StringComparer.OrdinalIgnoreCase),
                "Hardware"    => ordered.ThenBy(n => string.IsNullOrEmpty(n.HwModel) ? "￿" : n.HwModel, StringComparer.OrdinalIgnoreCase),
                "Heard"       => ordered.ThenByDescending(n => n.LastHeard),   // most recently heard first; unknown (0) last
                // Best link first: fewer hops dominate (direct above relayed), then stronger RSSI; no reading sinks last.
                "Signal"      => ordered.ThenByDescending(n => _mesh!.GetSignal(n.Num) is { } s ? -(double)(s.Hops ?? 50) * 1000 + s.Rssi : double.NegativeInfinity),
                "DM"          => ordered.ThenByDescending(n => _nodePrefs.GetValueOrDefault(n.Num)?.Dm == true),
                "Blocked"     => ordered.ThenByDescending(n => _nodePrefs.GetValueOrDefault(n.Num)?.Block == true),
                "Environment" => ordered.ThenByDescending(n => _mesh!.GetEnvironment(n.Num) != null),
                _             => ordered,
            };
            foreach (var n in ordered.ThenBy(SortName, StringComparer.OrdinalIgnoreCase))
                list.Items.Add(BuildNodeRow(n));

            int total = known.Count + orphans.Count();
            header.Text = query.Length > 0 ? $"Nodes: {list.Items.Count} of {total}" : $"Known nodes: {total}";
            status.Text = total == 0
                ? "No nodes loaded yet. Click \"Update nodes\" to fetch them from the device."
                : list.Items.Count == 0 ? $"No nodes match \"{query}\"." : DefaultHint;
        }
        Populate();

        // Route traceroute / node-info replies to this dialog's status line while it's open. Populate() runs
        // first (it refreshes names + resets the hint), then we overwrite the hint with the result line.
        _nodeDiagHandler = line => { Populate(); status.Text = line; };
        // Live telemetry updates the env text on existing rows in place (no rebuild → no scroll jump / reorder).
        _nodesRefresh = () => { foreach (var kv in envCells) { var t = NodeRowText(kv.Value.Node); kv.Value.Cell.Text = t; kv.Value.Cell.ToolTip = t; } };
        _nodesRepopulate = Populate;   // full rebuild when newly heard nodes need rows added
        _nodeStatusHandler = s => status.Text = s;
        dialog.Closed += (_, _) => { _nodeDiagHandler = null; _nodesRefresh = null; _nodesRepopulate = null; _nodeStatusHandler = null; };

        searchBox.TextChanged += (_, _) => Populate();
        sortCombo.SelectionChanged += (_, _) => Populate();

        updateBtn.Click += async (_, _) =>
        {
            updateBtn.IsEnabled = false;
            closeBtn.IsEnabled = false;
            await UpdateNodesAsync(s => status.Text = s);
            Populate();
            updateBtn.IsEnabled = true;
            closeBtn.IsEnabled = true;
        };
        closeBtn.Click += (_, _) => dialog.Close();

        root.Children.Add(header);
        root.Children.Add(controls);
        root.Children.Add(status);
        root.Children.Add(buttons);
        root.Children.Add(list);   // fills remaining space
        dialog.Content = root;
        dialog.ShowDialog();
    }

    /// <summary>Generates an OpenStreetMap/Leaflet page of all known node positions (with a search box,
    /// centred on Stockholm by default) and opens it in the default browser. No in-app map control is used,
    /// so there's no extra dependency; the tiles need an internet connection.</summary>
    private void ShowMap()
    {
        if (_mesh == null) return;
        var positions = _mesh.GetNodePositions();
        if (_currentHost.Length > 0) DeviceCache.SaveNodePositions(_currentHost, _mesh.GetNodePositionMap());   // cache for next time
        try
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chessmesh-nodes-map.html");
            System.IO.File.WriteAllText(path, NodeMap.Html(positions));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Status(positions.Count == 0
                ? "Opened the map (no nodes have a known position yet — right-click a node → Request position)."
                : $"Opened the map with {positions.Count} node position(s).");
        }
        catch (Exception ex) { Status($"Could not open the map: {ex.Message}"); }
    }

    /// <summary>Relative "X ago" for an epoch-seconds timestamp ("" when unknown/future).</summary>
    private static string AgoText(long epochSeconds)
    {
        if (epochSeconds <= 0) return "";
        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        if (span < TimeSpan.Zero) return "just now";
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s ago";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    /// <summary>A compact one-line summary of an environment reading (no timestamp): "21.5°C · 45%RH · dp 9.3°C · 1013 hPa".</summary>
    private static string EnvSummary(MeshEnvironment e)
    {
        var parts = new List<string> { $"{e.TemperatureC:0.#}°C" };
        if (e.RelativeHumidity > 0) parts.Add($"{e.RelativeHumidity:0}%RH");
        if (e.DewPointC is double dp) parts.Add($"dp {dp:0.#}°C");
        if (e.BarometricPressure > 0) parts.Add($"{e.BarometricPressure:0} hPa");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Opens a window listing every environment telemetry reading cached from a node this session, newest
    /// first, each with its timestamp. Snapshot of the in-memory cache; Refresh re-reads it.
    /// </summary>
    private void ShowTelemetryHistory(MeshNode target, Window owner)
    {
        if (_mesh == null) return;

        var grey = new SolidColorBrush(MediaColor.FromRgb(0xB0, 0xB0, 0xB0));
        var white = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xE0, 0xE0));

        var win = new Window
        {
            Title = "Node info",
            Owner = owner,
            Width = 560,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x25, 0x26)),
        };

        var root = new DockPanel { Margin = new Thickness(12) };

        var header = new TextBlock
        {
            Text = $"All information for {target.Display}",
            Foreground = white, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };
        DockPanel.SetDock(header, Dock.Top);

        // Everything we know about the node (identity, hardware, role, signal, position, latest telemetry, prefs).
        var details = new TextBlock
        {
            Foreground = white, TextWrapping = TextWrapping.Wrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(details, Dock.Top);

        var telemHeader = new TextBlock { Text = "Telemetry history:", Foreground = white, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 2) };
        DockPanel.SetDock(telemHeader, Dock.Top);

        var sub = new TextBlock { Foreground = grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(sub, Dock.Top);

        var status = new TextBlock { Foreground = grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(status, Dock.Top);

        var list = new ListBox
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = white, BorderThickness = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            SelectionMode = SelectionMode.Extended,
        };

        void Load()
        {
            // Node details (refreshed live as signal/telemetry/position arrive), plus this app's DM/Block prefs.
            var pref = _nodePrefs.GetValueOrDefault(target.Num);
            details.Text = _mesh!.GetNodeInfoText(target.Num) + Environment.NewLine +
                           $"DM: {(pref?.Dm == true ? "on" : "off")}    Block: {(pref?.Block == true ? "on" : "off")}";

            var history = _mesh!.GetEnvironmentHistory(target.Num);
            list.Items.Clear();
            foreach (var e in history.Reverse())   // newest first
                list.Items.Add($"{e.Timestamp:yyyy-MM-dd HH:mm:ss}   {EnvSummary(e)}");
            sub.Text = history.Count == 0
                ? "No telemetry cached for this node. Use \"Request telemetry\", or wait for a broadcast."
                : $"{history.Count} reading{(history.Count == 1 ? "" : "s")} cached (newest first).";
        }
        Load();

        // Reload live while this window is open (HandleNodeDiagnostics fires this when telemetry arrives).
        _telemetryRefresh = Load;

        var requestBtn = new Button { Content = "Request telemetry", Width = 120, Height = 28, Margin = new Thickness(0, 8, 8, 0) };
        requestBtn.Click += async (_, _) =>
        {
            status.Text = "Requesting telemetry… (a node without an environment sensor won't reply)";
            try { await _mesh!.RequestTelemetryAsync(target.Num); }
            catch (Exception ex) { status.Text = $"Request failed: {ex.Message}"; }
        };
        var deleteBtn = new Button { Content = "Delete", Width = 80, Height = 28, Margin = new Thickness(0, 8, 8, 0) };
        deleteBtn.Click += (_, _) =>
        {
            if (MessageBox.Show(win, $"Delete all cached telemetry for {target.Display}?", "Delete telemetry",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _mesh!.ClearEnvironment(target.Num);
            if (_currentHost.Length > 0) DeviceCache.ClearTelemetry(_currentHost, target.Num);
            Load();
            _nodesRefresh?.Invoke();   // drop the latest-reading summary from the node row too
            status.Text = "Telemetry deleted.";
        };
        var copyBtn = new Button { Content = "Copy", Width = 80, Height = 28, Margin = new Thickness(0, 8, 8, 0) };
        copyBtn.Click += (_, _) =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(header.Text);
            sb.AppendLine(details.Text);
            sb.AppendLine("Telemetry history:");
            foreach (var item in list.Items) sb.AppendLine(item?.ToString());
            try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard may be momentarily locked by another app */ }
        };
        var closeBtn = new Button { Content = "Close", Width = 80, Height = 28, Margin = new Thickness(0, 8, 0, 0) };
        closeBtn.Click += (_, _) => win.Close();
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btnPanel.Children.Add(deleteBtn);
        btnPanel.Children.Add(copyBtn);
        btnPanel.Children.Add(closeBtn);
        DockPanel.SetDock(btnPanel, Dock.Bottom);

        // Node actions (moved here from the node right-click menu). Replies arrive via the poll loop and refresh
        // the details above; traceroute opens its own window.
        var infoBtn = new Button { Content = "Request info", Width = 96, Height = 28, Margin = new Thickness(0, 8, 8, 0) };
        infoBtn.Click += async (_, _) =>
        {
            status.Text = $"Requesting info from {target.Display}…";
            try { await _mesh!.RequestNodeInfoAsync(target.Num); status.Text = $"Requested node info from {target.Display}."; }
            catch (Exception ex) { status.Text = $"Request failed: {ex.Message}"; }
        };
        var posBtn = new Button { Content = "Request position", Width = 110, Height = 28, Margin = new Thickness(0, 8, 8, 0) };
        posBtn.Click += async (_, _) =>
        {
            status.Text = $"Requesting position from {target.Display}…";
            try { await _mesh!.RequestPositionAsync(target.Num); status.Text = $"Requested position from {target.Display}."; }
            catch (Exception ex) { status.Text = $"Position request failed: {ex.Message}"; }
        };
        var traceBtn = new Button { Content = "Traceroute", Width = 96, Height = 28, Margin = new Thickness(0, 8, 8, 0) };
        traceBtn.Click += (_, _) => ShowTraceroute(target, win);
        var noiseBtn = new Button { Content = "Request noise floor", Width = 130, Height = 28, Margin = new Thickness(0, 8, 0, 0) };
        noiseBtn.Click += async (_, _) =>
        {
            status.Text = $"Requesting noise floor from {target.Display}…";
            try { await _mesh!.RequestNoiseFloorAsync(target.Num); }
            catch (Exception ex) { status.Text = $"Request failed: {ex.Message}"; }
        };
        // WrapPanel so the action buttons flow onto a second line rather than overflowing the window.
        var actionPanel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actionPanel.Children.Add(infoBtn);
        actionPanel.Children.Add(posBtn);
        actionPanel.Children.Add(requestBtn);   // Request telemetry — grouped with the other request buttons
        actionPanel.Children.Add(traceBtn);
        actionPanel.Children.Add(noiseBtn);
        DockPanel.SetDock(actionPanel, Dock.Bottom);

        win.Closed += (_, _) => _telemetryRefresh = null;

        root.Children.Add(header);
        root.Children.Add(details);
        root.Children.Add(telemHeader);
        root.Children.Add(sub);
        root.Children.Add(status);
        root.Children.Add(btnPanel);      // window/telemetry buttons — bottom-most row
        root.Children.Add(actionPanel);   // node-action buttons — row above
        root.Children.Add(list);          // fills remaining space
        win.Content = root;
        win.ShowDialog();
    }

    /// <summary>
    /// Opens a dedicated traceroute window for a node: sends the request and shows the route path when the
    /// reply arrives (delivered via the poll loop → <see cref="_tracerouteWaiters"/>), or a clear timeout
    /// message after 30s if the node never responds.
    /// </summary>
    private void ShowTraceroute(MeshNode target, Window owner)
    {
        if (_mesh == null) return;

        var grey = new SolidColorBrush(MediaColor.FromRgb(0xB0, 0xB0, 0xB0));
        var white = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xE0, 0xE0));

        var win = new Window
        {
            Title = "Traceroute",
            Owner = owner,
            Width = 480,
            Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x25, 0x26)),
        };

        var root = new DockPanel { Margin = new Thickness(12) };

        var header = new TextBlock
        {
            Text = $"Traceroute to {target.Display}",
            Foreground = white, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(header, Dock.Top);

        uint sendChannel = _mesh.ChannelForNode(target.Num);
        var statusLine = new TextBlock
        {
            Text = $"Sending request on channel {sendChannel}… waiting for a reply (up to 30s).",
            Foreground = grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(statusLine, Dock.Top);

        var hopList = new ListBox
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = white, BorderThickness = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            SelectionMode = SelectionMode.Extended,
        };

        // Copy gathers the whole window's text (header, status, hops, diagnostics) to the clipboard — the info
        // is most useful when it can be pasted into a report.
        var copyBtn = new Button { Content = "Copy", Width = 80, Height = 28, Margin = new Thickness(0, 8, 8, 0) };
        copyBtn.Click += (_, _) =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(header.Text);
            sb.AppendLine(statusLine.Text);
            foreach (var item in hopList.Items) sb.AppendLine(item?.ToString());
            try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard may be momentarily locked by another app */ }
        };
        var closeBtn = new Button { Content = "Close", Width = 80, Height = 28, Margin = new Thickness(0, 8, 0, 0) };
        closeBtn.Click += (_, _) => win.Close();
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btnPanel.Children.Add(copyBtn);
        btnPanel.Children.Add(closeBtn);
        DockPanel.SetDock(btnPanel, Dock.Bottom);

        root.Children.Add(header);
        root.Children.Add(statusLine);
        root.Children.Add(btnPanel);
        root.Children.Add(hopList);   // fills remaining space
        win.Content = root;

        var sentId = new uint[1];   // set once the request is sent; lets us match its delivery ack/NAK
        bool gotReply = false;
        _pollObserver = r =>
        {
            // We set want_ack, so the routing ack/NAK for our request shows whether it reached the node.
            if (sentId[0] == 0 || gotReply) return;
            foreach (var a in r.Acks)
                if (a.PacketId == sentId[0])
                    statusLine.Text = a.Failed
                        ? $"Request not delivered on channel {sendChannel}: {a.FailReason}. The node may not be reachable on that channel."
                        : $"Request delivered (ack) on channel {sendChannel}. Waiting for the route reply…";
        };

        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            statusLine.Text = $"No response (timed out) on channel {sendChannel}. Likely causes: the node isn't on " +
                              $"channel {sendChannel}; the node is offline/out of range; or another HTTP client is draining " +
                              "this radio's packet queue (only one HTTP client at a time).";
        };

        // Deliver the reply (matched by the traced node) into this window.
        _tracerouteWaiters[target.Num] = t =>
        {
            timeout.Stop();
            gotReply = true;
            string me = _mesh!.DescribeNode(_mesh.MyNodeNum);
            string dest = _mesh.DescribeNode(t.Node);

            // Node labels for a path: from → intermediate hops → to. An unknown hop (node 0, inserted by the
            // firmware when it can't fill a gap) shows as "Unknown". No endpoint filtering, so the per-link SNR
            // arrays line up with the links of this list.
            List<string> PathLabels(string fromLabel, IEnumerable<uint> mids, string toLabel)
            {
                var path = new List<string> { fromLabel };
                foreach (var h in mids) path.Add(h == 0 ? "Unknown" : _mesh.DescribeNode(h));
                path.Add(toLabel);
                return path;
            }

            // Print each node, with the SNR of the link leading to the next node shown between them (matching the
            // native app). snr[i] is the link between nodes[i] and nodes[i+1].
            void RenderPath(string title, List<string> nodes, List<int> snr)
            {
                int hops = nodes.Count - 1;
                hopList.Items.Add($"{title}  ({hops} hop{(hops == 1 ? "" : "s")}):");
                for (int i = 0; i < nodes.Count; i++)
                {
                    hopList.Items.Add($"   {i}.  {nodes[i]}");
                    if (i < nodes.Count - 1)
                        hopList.Items.Add($"        ↓ {(i < snr.Count ? MeshTraceroute.SnrLabel(snr[i]) : "SNR ?")}");
                }
            }

            var toward = PathLabels($"{me}   (you)", t.Route, dest);
            var back = PathLabels(dest, t.RouteBack, $"{me}   (you)");

            statusLine.Text = "Route received:";
            hopList.Items.Clear();
            RenderPath($"Towards {dest}", toward, t.SnrTowards);
            hopList.Items.Add("");
            RenderPath("Back to you", back, t.SnrBack);
        };

        win.Closed += (_, _) => { timeout.Stop(); _tracerouteWaiters.Remove(target.Num); _pollObserver = null; };

        async Task SendTraceroute()
        {
            try { sentId[0] = await _mesh!.SendTracerouteAsync(target.Num); }
            catch (Exception ex) { statusLine.Text = $"Failed to send request: {ex.Message}"; }
        }

        timeout.Start();
        _ = SendTraceroute();
        win.ShowDialog();
    }

    /// <summary>
    /// Asks the device to resend its full config and drains it (refreshing the node + channel
    /// lists), reporting progress. Pauses polling during the dump and dispatches any moves/chat
    /// that arrive so nothing is dropped if this is done mid-game.
    /// </summary>
    private async Task UpdateNodesAsync(Action<string> report)
    {
        if (_mesh == null) return;

        bool wasPolling = _pollTimer.IsEnabled;
        _pollTimer.Stop();
        _refreshing = true;
        ApplyConnectionState();
        try
        {
            report("Requesting node list from device…");
            await _mesh.RequestConfigAsync();

            const int chunk = 40;
            while (true)
            {
                var r = await _mesh.ReceiveAsync(chunk);
                foreach (var ack in r.Acks) MarkAcked(ack);
                foreach (var msg in r.Texts) Dispatch(msg);
                HandleNodeDiagnostics(r);
                report($"Updating… {_mesh.GetNodes().Count} nodes so far");
                if (r.PacketCount < chunk) break;
            }

            RebuildChatTxCombo();   // channel names may have refreshed
            DeviceCache.Save(_currentHost, _mesh.GetAvailableChannels(), _mesh.MyNodeNum, _mesh.GetNodeNameMap(), _mesh.GetNodeRoleMap(), _mesh.GetNodeHwMap());
            DeviceCache.SaveNodePositions(_currentHost, _mesh.GetNodePositionMap());   // cache positions for the map
            RefreshChatAckerNames();   // resolve any acker numbers to names now that nodes are loaded
            report($"Done — {_mesh.GetNodes().Count} nodes known.");
        }
        catch (Exception ex)
        {
            report($"Update failed: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
            _lastRxUtc = DateTime.UtcNow; _rxStallWarned = false;   // node update re-subscribed — re-arm the watchdog
            ApplyConnectionState();
            if (wasPolling) _pollTimer.Start();
        }
    }

    // ---- End / status ----------------------------------------------------------------

    private bool CheckForEnd()
    {
        var status = _board.GetStatus();
        if (status == GameStatus.Checkmate)
        {
            EndGame($"Checkmate — {_board.SideToMove.Opposite()} wins.");
            return true;
        }
        if (status == GameStatus.Stalemate)
        {
            EndGame("Stalemate — draw.");
            return true;
        }
        return false;
    }

    private void EndGame(string message)
    {
        _gameOver = true;
        _playing = false;
        _resigning = false;
        // Allow starting/joining another game; keep chat + polling running.
        ApplyConnectionState();
        _selected = null;
        _legalForSelected.Clear();
        Render();
        Status(message);
        AddSystem(Stamp() + $"— {message} —");
        ShowNotice("Game over", message);
    }

    /// <summary>Builds a dark modeless dialog with a wrapped message and a button row.
    /// MODELESS is deliberate: a modal MessageBox blocks the DispatcherTimer poll while open, so
    /// the app stops reading mesh traffic (and e.g. fails to acknowledge a freshly-created game)
    /// until the user dismisses it. Show() keeps the poll running.</summary>
    private Window BuildModeless(string title, string message, double maxWidth = 360)
    {
        var dlg = new Window
        {
            Title = title,
            Owner = this,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x25, 0x26)),
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xE0, 0xE0)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = maxWidth,
            Margin = new Thickness(0, 0, 0, 16),
        });
        dlg.Content = panel;
        return dlg;
    }

    /// <summary>A modeless OK notice (non-blocking replacement for an informational MessageBox).</summary>
    private void ShowNotice(string title, string message)
    {
        var dlg = BuildModeless(title, message);
        var ok = new Button { Content = "OK", Width = 80, Height = 28, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => dlg.Close();
        ((StackPanel)dlg.Content).Children.Add(ok);
        dlg.Show();
    }

    /// <summary>A modeless Yes/No confirm (non-blocking replacement for a YesNo MessageBox).
    /// Exactly one of the callbacks runs when the user picks, on the UI thread.</summary>
    private void ShowConfirm(string title, string message, string yesText, string noText, Action onYes, Action onNo)
    {
        var dlg = BuildModeless(title, message);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var yes = new Button { Content = yesText, MinWidth = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var no = new Button { Content = noText, MinWidth = 90, Height = 28, IsCancel = true };
        bool done = false;
        yes.Click += (_, _) => { if (done) return; done = true; dlg.Close(); onYes(); };
        no.Click  += (_, _) => { if (done) return; done = true; dlg.Close(); onNo(); };
        // If the user closes the window with the X, treat it as "no".
        dlg.Closed += (_, _) => { if (!done) { done = true; onNo(); } };
        row.Children.Add(yes);
        row.Children.Add(no);
        ((StackPanel)dlg.Content).Children.Add(row);
        dlg.Show();
    }

    private void UpdateTurnStatus()
    {
        string check = _board.GetStatus() == GameStatus.Check ? " (check!)" : "";
        bool mine = _board.SideToMove == _myColor;
        Status(mine
            ? $"Your move — {_myColor}{check}.  Game '{_gameId}', channel {_mesh?.ChannelIndex}."
            : $"Waiting for {_board.SideToMove}{check}...");
    }

    private static string MoveLine(int ply, GameColor mover, Move move) =>
        $"{ply,3}. {mover,-5} {move.ToUci()}";

    private LogEntry AddMoveEntry(string text) => Append(MoveList, text);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private const uint FLASHW_TRAY = 0x2;        // flash the taskbar button
    private const uint FLASHW_TIMERNOFG = 0xC;   // keep flashing until the window is brought to the foreground

    /// <summary>Flashes the taskbar button to notify of an incoming move/message (no-op if focused).</summary>
    private void FlashNotify()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    private void Status(string text) => StatusText.Text = text;
}
