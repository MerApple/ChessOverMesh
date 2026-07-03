using System.Collections.ObjectModel;
using ChessOverMesh.Chess;
using ChessOverMesh.Game;
using ChessOverMesh.Mesh;
using GameColor = ChessOverMesh.Chess.Color;
using Color = Microsoft.Maui.Graphics.Color;

namespace ChessOverMesh.Maui;

/// <summary>
/// Android port of the desktop MainWindow. Stage 1: connection, board + tap-to-move, the
/// moves/system/chat panels, channel chat, the create/join/resign/save/cancel lobby, and the
/// delivery ack + retry engine — all driving the shared ChessOverMesh core over Meshtastic HTTP.
/// </summary>
public partial class MainPage : ContentPage
{
    // ---- Board palette ----
    static readonly Color LightSquare = Color.FromRgb(0xEE, 0xEE, 0xD2);
    static readonly Color DarkSquare = Color.FromRgb(0x76, 0x96, 0x56);
    static readonly Color SelectColor = Color.FromRgb(0xF6, 0xF6, 0x69);
    static readonly Color LastMoveColor = Color.FromRgb(0xBB, 0xCB, 0x44);
    static readonly Color CheckColor = Color.FromRgb(0xE5, 0x73, 0x73);
    static readonly Color WhitePiece = Color.FromRgb(0xFA, 0xFA, 0xFA);
    static readonly Color BlackPiece = Color.FromRgb(0x20, 0x20, 0x20);

    const string RoleAuto = "Auto", RoleWhite = "White", RoleBlack = "Black";

    readonly Label[] _cells = new Label[64];      // glyph labels (transparent bg so their shadow outlines the piece)
    readonly Grid[] _cellBg = new Grid[64];        // per-square coloured background behind each glyph
    double _boardSize;

    readonly ObservableCollection<LogEntry> _moves = new();
    readonly ObservableCollection<LogEntry> _system = new();
    readonly ObservableCollection<LogEntry> _chat = new();

    IDispatcherTimer? _pollTimer;
    IDispatcherTimer? _ackTimer;
    IDispatcherTimer? _chatHoldTimer;
    IDispatcherTimer? _probeTimer;   // TCP reachability probe (connection-loss detection)
    IDispatcherTimer? _autoReconnectTimer;   // ticks every second to count down to the next retry
    bool _autoReconnecting;
    string _autoReconnectHost = "";
    int _reconnectCountdown;                 // seconds until the next auto-reconnect attempt
    const int ReconnectIntervalSeconds = 15; // wait between auto-reconnect attempts (the countdown resets to this)
    bool _polling, _refreshing;

    MeshtasticHttpClient? _mesh;
    Board _board = Board.CreateStartingPosition();
    GameColor _myColor = GameColor.White;
    string _gameId = "";
    int _ply;
    bool _playing, _gameOver, _connected, _connecting, _synced, _syncing;
    string _currentHost = "";
    bool _transportIsIp = true;   // true for WiFi (TCP probe applies); false for BLE (no IP to probe)
    Func<Task>? _reconnectBle;    // when set (a BLE connection), auto-reconnect rebuilds the BLE link via this
    string _probeHost = "";       // host:port the reachability probe dials (the API port actually in use)
    int _probePort;
    string _syncStatus = "";      // mesh-sync progress, shown on the Device tab (not the Chess status line)

    // Reception watchdog: the device's live-packet subscription can drop (reboot, or another client stealing
    // the shared /fromradio queue), after which messages silently stop. Warn once — and on receive errors —
    // suggesting "Update nodes" (which re-sends want_config and re-subscribes).
    DateTime _lastRxUtc = DateTime.UtcNow;
    bool _rxStallWarned;
    bool _isProxy;                  // connected through Meshtastic.Proxy (a proxy:// link), not directly to a device
    DateTime _suppressAcksUntil;    // don't auto-ack until this time (covers the proxy's backfill burst after connect)
    static readonly TimeSpan ReceiveStallTimeout = TimeSpan.FromSeconds(120);

    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    // Bluetooth needs longer: the first handshake read triggers the radio's bond/PIN dialog, and the user needs
    // time to read and enter the code before the connect is cancelled.
    static readonly TimeSpan BleConnectTimeout = TimeSpan.FromSeconds(60);

    // Connection-loss detection via a TCP CONNECT probe (not an HTTP-response timeout): the ESP32's network
    // stack accepts connections from a task separate from the busy main loop, so a connect succeeds fast even
    // when HTTP responses are slow — reliably telling "alive but slow" from "dead" with no false positives. Only
    // probes when a poll hasn't recently succeeded; N consecutive failed connects → connection lost.
    int _probeFailures;
    bool _probing;
    DateTime _lastPollOkUtc = DateTime.UtcNow;
    const int ConnectionLostThreshold = 3;
    static readonly TimeSpan TcpProbeTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan ProbeIdleGrace = TimeSpan.FromSeconds(4);

    int? _selected;
    List<Move> _legalForSelected = new();
    (int from, int to)? _lastMove;

    // ---- Channels & chat ----
    uint _chatTxChannel;
    uint? _chatTxDest;   // when set, chat sends go as a direct message to this node (else broadcast on _chatTxChannel)
    uint _replyToId;          // packet id the next send replies to (0 = not replying)
    string _replyToSnippet = "";   // a snippet of the message being replied to, for the reply banner / metadata
    readonly HashSet<uint> _chatListen = new();
    // RX view filter: which targets are HIDDEN from the chat view (default empty = everything shown), and the
    // unread count per hidden target. A target is a channel (IsDm=false, Id=index) or a DM peer (IsDm=true, Id=node).
    readonly HashSet<(bool IsDm, uint Id)> _rxHidden = new();
    readonly Dictionary<(bool IsDm, uint Id), int> _unread = new();
    readonly Dictionary<uint, DeviceCache.NodePrefs> _nodePrefs = new();   // node num -> DM/Block flags (loaded on connect)
    readonly Dictionary<uint, LogEntry> _chatEntryById = new();   // packet id → its chat row (for attaching reactions)
    readonly Dictionary<uint, List<(string Emoji, uint Node)>> _reactions = new();   // target packet id → reactions

    /// <summary>The emoji set offered when reacting to a message (the common "tapback" set).</summary>
    public static readonly string[] ReactionEmojis = { "👍", "❤️", "😂", "😮", "😢", "👎" };
    readonly Dictionary<uint, Action<MeshTraceroute>> _tracerouteWaiters = new();   // node num -> open traceroute page callback
    Action? _telemetryRefresh;   // set by an open telemetry page so it reloads when new telemetry arrives
    bool _showSignal = true;
    readonly HashSet<uint> _chatAckOn = new();
    readonly HashSet<uint> _chatAckSignalOn = new();   // channels whose chat ack also reports RSSI/SNR/hops
    // Per-channel auto-ack keywords: a received message whose (lowercased) text contains any of these triggers a
    // CHATACK-with-RSSI regardless of the channel's ack setting (handy for range-test pings).
    readonly Dictionary<uint, List<string>> _ackTriggers = new();

    // ---- Notification sounds (asset "" = off) ----
    string _chessSound = "", _chatSound = "";
    int _chessVolume = 80, _chatVolume = 80;

    // ---- Per-list fonts (family "" = platform default) ----
    string _movesFamily = "monospace", _systemFamily = "monospace", _chatFamily = "", _nodesFamily = "monospace";
    double _movesSize = 13, _systemSize = 13, _chatSize = 15, _nodesSize = 13;

    // The Chat tab registers itself here so a chat font/size change also restyles its TX composer.
    internal ChatTabPage? ChatTab;

    // An open Nodes page registers itself here so a nodes font/size change restyles it live. The current font is
    // exposed so a freshly-opened Nodes page can seed its rows.
    internal NodesPage? NodesPageRef;
    public (string Family, double Size) NodesFont => (_nodesFamily, _nodesSize);

    // System-message categories hidden by the filter.
    readonly HashSet<SysCategory> _hiddenSysCats = new();

    int _retentionTick;   // counts ack-timer ticks; a chat auto-delete sweep runs every 60

    sealed record ChannelItem(uint Index, string Label);

    readonly List<string> _moveHistory = new();
    GameSave? _loadedGame;
    string _loadedName = "";

    // ---- Delivery ack / retry ----
    static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(15);
    const int MaxSendAttempts = 2;
    static readonly TimeSpan MaxAckJitter = TimeSpan.FromSeconds(2);
    static Task AckJitterDelay() => Task.Delay(Random.Shared.Next((int)MaxAckJitter.TotalMilliseconds + 1));

    const int MaxChatChars = 200;
    /// <summary>The chat wire-length limit (chars actually transmitted), for the composer's character counter.</summary>
    public static int MaxChatLength => MaxChatChars;

    /// <summary>The self-destruct lifetime (minutes) that outgoing messages on the current TX channel are stamped
    /// with, or 0 when the channel has no send-TTL. The send-TTL is per broadcast channel, so a DM uses the same
    /// lookup as a broadcast (both keyed by <c>_chatTxChannel</c>), matching <see cref="SendChatMessageAsync"/>.</summary>
    public int ChatSendTtlMinutes() =>
        _currentHost.Length > 0 ? DeviceCache.GetChannelSendTtl(_currentHost).GetValueOrDefault(_chatTxChannel) : 0;

    /// <summary>The wire length (chars actually transmitted) of a prospective chat message on the current TX
    /// channel: the AES-base64 ciphertext length when the channel has an app key, else the trimmed text length.
    /// Mirrors the check in <see cref="SendChatAsync"/> so the composer's counter agrees with the send limit.</summary>
    public int ChatWireLength(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return 0;
        // Self-destruct rides an SOH-delimited TTL header in the wire text (see SendChatMessageAsync), so it adds a
        // few bytes in front of the body. Measure the exact string that gets sent — header included — or the counter
        // under-reports (and, on an app-keyed channel, would encrypt the wrong plaintext length).
        string wireText = ProtocolMessage.EncodeChatTtl(ChatSendTtlMinutes() * 60, t);
        string key = _mesh?.GetChannelKey(_chatTxChannel) ?? "";
        return key.Length > 0 ? AesText.Encrypt(wireText, key).Length : wireText.Length;
    }
    static readonly TimeSpan MoveSendDelay = TimeSpan.FromSeconds(5);
    DateTime _moveSendAllowedUtc = DateTime.MinValue;
    static readonly TimeSpan ChatSendDelay = TimeSpan.FromSeconds(5);
    DateTime _chatSendAllowedUtc = DateTime.MinValue;

    readonly Dictionary<uint, PendingSend> _pending = new();
    bool _checkingAcks;
    readonly Dictionary<uint, ChatAckInfo> _chatAckers = new();
    // Acks we OVERHEARD other nodes send for a message we received (keyed by that received message's packet id), so
    // its "Message details" can list who else acknowledged it. Separate from _chatAckers, which is our own sends.
    readonly Dictionary<uint, ChatAckInfo> _overheardAcks = new();
    readonly Dictionary<string, PendingGame> _pendingGames = new();

    sealed record PendingGame(string GameId, uint CreatorNode, GameColor CreatorColor, uint Channel, string SaveName);

    bool _joining;
    PendingGame? _joinGame;
    GameColor _joinColor;
    bool _resigning;
    bool _awaitingOpponent;
    ChatAckInfo? _createInfo;

    sealed class ChatAckInfo
    {
        public LogEntry Entry = null!;
        public string BaseText = "";
        public readonly HashSet<uint> Ackers = new();
        public readonly Dictionary<uint, string> AckerSignals = new();   // acker node → how THEY heard our message (if reported)
        public readonly Dictionary<uint, string> MyReception = new();    // acker node → how OUR radio heard their ack packet
    }

    const string MarkSending = " …", MarkDelivered = " ✓", MarkFailed = " ✗", MarkSent = " ↗";

    sealed class PendingSend
    {
        public uint Id;
        public string Payload = "";
        public DateTime LastSentUtc;
        public int Attempts;
        public bool IsMove, IsJoin, IsResign, IsCreate, IsSave, IsChat, IsRelayConfirm;
        public bool IsDm;   // a direct message — confirmed by the recipient's firmware routing ack/NAK, not a relay
        public uint Channel;               // for chat: the channel it was sent on (any reply there confirms it)
        public string SaveFileName = "";
        public int Ply;
        public string Label = "";
        public LogEntry? Entry;
        public string BaseText = "";
        public bool NeedsResend;
        public bool RelayHeard;            // for ack-on chat: we overheard a relay (intermediate "relayed" state)
        public DateTime SendDeadlineUtc;   // for chat: when the wait gives up if unconfirmed — drives the Send countdown
    }

    uint _incomingRxTime;

    public MainPage()
    {
        InitializeComponent();

        MovesView.ItemsSource = _moves;
        SystemView.ItemsSource = _system;

        _showSignal = AppSettings.ShowSignal;
        _chessSound = AppSettings.ChessSoundPath ?? SoundLibrary.DefaultChess();
        _chatSound = AppSettings.ChatSoundPath ?? SoundLibrary.DefaultChat();
        _chessVolume = AppSettings.ChessVolume;
        _chatVolume = AppSettings.ChatVolume;
        _movesFamily = AppSettings.MovesFont ?? "monospace"; _movesSize = AppSettings.MovesSize;
        _systemFamily = AppSettings.SystemFont ?? "monospace"; _systemSize = AppSettings.SystemSize;
        _chatFamily = AppSettings.ChatFont ?? ""; _chatSize = AppSettings.ChatSize;
        _nodesFamily = AppSettings.NodesFont ?? "monospace"; _nodesSize = AppSettings.NodesSize;
        LoadColors();

        BuildBoard();
        Render();
        SelectTab(TabMoves, MovesView);
        ApplyChessboardVisibility();
        LoadSystemFilter();

        _pollTimer = Dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(2.5);
        _pollTimer.Tick += async (_, _) => await PollAsync();

        _ackTimer = Dispatcher.CreateTimer();
        _ackTimer.Interval = TimeSpan.FromSeconds(1);
        _ackTimer.Tick += async (_, _) =>
        {
            await CheckPendingAcksAsync();
            TickChatSendCountdown();
            UpdateExpiryCountdowns();   // live "deletes in …" countdown + self-destruct removal, every second
            if (++_retentionTick >= 60) { _retentionTick = 0; ApplyChatRetention(); }   // auto-delete sweep ~once a minute
        };
        _ackTimer.Start();

        _probeTimer = Dispatcher.CreateTimer();
        _probeTimer.Interval = TimeSpan.FromSeconds(2.5);
        _probeTimer.Tick += async (_, _) => await ProbeAsync();
        _probeTimer.Start();

        _autoReconnectTimer = Dispatcher.CreateTimer();
        _autoReconnectTimer.Interval = TimeSpan.FromSeconds(1);
        _autoReconnectTimer.Tick += async (_, _) => await AutoReconnectTickAsync();

        _chatHoldTimer = Dispatcher.CreateTimer();
        _chatHoldTimer.Tick += (_, _) => { _chatHoldTimer!.Stop(); ApplyConnectionState(); };

        ApplyConnectionState();
    }

    // ---- Colours ----
    static void LoadColors()
    {
        Palette.Normal = ParseHex(AppSettings.NormalColor) ?? Palette.Normal;
        Palette.Pending = ParseHex(AppSettings.PendingColor) ?? Palette.Pending;
        Palette.Acked = ParseHex(AppSettings.AckedColor) ?? Palette.Acked;
        Palette.Relayed = ParseHex(AppSettings.RelayedColor) ?? Palette.Relayed;
        Palette.Cached = ParseHex(AppSettings.CachedColor) ?? Palette.Cached;
        Palette.Warning = ParseHex(AppSettings.WarningColor) ?? Palette.Warning;

        Palette.SysGame = ParseHex(AppSettings.SysGameColor) ?? Palette.SysGame;
        Palette.SysConnection = ParseHex(AppSettings.SysConnectionColor) ?? Palette.SysConnection;
        Palette.SysNodes = ParseHex(AppSettings.SysNodesColor) ?? Palette.SysNodes;
        Palette.SysPosition = ParseHex(AppSettings.SysPositionColor) ?? Palette.SysPosition;
        Palette.SysTelemetry = ParseHex(AppSettings.SysTelemetryColor) ?? Palette.SysTelemetry;
        Palette.SysTraceroute = ParseHex(AppSettings.SysTracerouteColor) ?? Palette.SysTraceroute;
        Palette.SysAdmin = ParseHex(AppSettings.SysAdminColor) ?? Palette.SysAdmin;
        Palette.SysRequests = ParseHex(AppSettings.SysRequestsColor) ?? Palette.SysRequests;
        Palette.SysWarnings = ParseHex(AppSettings.SysWarningsColor) ?? Palette.SysWarnings;
    }

    internal static Color SysCategoryColor(SysCategory cat) => cat switch
    {
        SysCategory.Connection => Palette.SysConnection,
        SysCategory.Nodes => Palette.SysNodes,
        SysCategory.Position => Palette.SysPosition,
        SysCategory.Telemetry => Palette.SysTelemetry,
        SysCategory.Traceroute => Palette.SysTraceroute,
        SysCategory.Admin => Palette.SysAdmin,
        SysCategory.Requests => Palette.SysRequests,
        SysCategory.Warnings => Palette.SysWarnings,
        _ => Palette.SysGame,
    };

    static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return Color.FromArgb(hex.StartsWith("#") ? hex : "#" + hex); } catch { return null; }
    }

    // ---- Board sizing ----
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0) return;
        double size = Math.Floor(Math.Min(width - 16, height * 0.5));
        if (size < 80 || Math.Abs(size - _boardSize) < 1) return;
        _boardSize = size;
        BoardGrid.WidthRequest = size;
        BoardGrid.HeightRequest = size;
        double font = size / 8 * 0.6;
        foreach (var c in _cells) if (c != null) c.FontSize = font;
    }

    // ---- Board construction & rendering ----
    void BuildBoard()
    {
        BoardGrid.Children.Clear();
        BoardGrid.RowDefinitions.Clear();
        BoardGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < 8; i++)
        {
            BoardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
            BoardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        }

        for (int sq = 0; sq < 64; sq++)
        {
            int square = sq;   // capture for the tap closure
            var (row, col) = SquareToCell(sq);

            var glyph = new Label
            {
                FontSize = 28,
                FontAttributes = FontAttributes.Bold,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                BackgroundColor = Colors.Transparent,
                InputTransparent = true,   // let taps fall through to the background grid
            };
            var bg = new Grid { BackgroundColor = BaseColor(sq) };
            bg.Add(glyph);

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => OnCellTapped(square);
            bg.GestureRecognizers.Add(tap);

            Grid.SetRow(bg, row);
            Grid.SetColumn(bg, col);
            BoardGrid.Children.Add(bg);
            _cells[square] = glyph;
            _cellBg[square] = bg;
        }
    }

    (int row, int col) SquareToCell(int sq)
    {
        int rank = Move.RankOf(sq), file = Move.FileOf(sq);
        return _myColor == GameColor.White ? (7 - rank, file) : (rank, 7 - file);
    }

    void Render()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            _cellBg[sq].BackgroundColor = BaseColor(sq);
            SetGlyph(sq);
        }
        if (_lastMove is { } lm)
        {
            _cellBg[lm.from].BackgroundColor = LastMoveColor;
            _cellBg[lm.to].BackgroundColor = LastMoveColor;
        }
        if (_board.GetStatus() == GameStatus.Check)
        {
            int king = FindKing(_board.SideToMove);
            if (king >= 0) _cellBg[king].BackgroundColor = CheckColor;
        }
        if (_selected is { } sel)
        {
            _cellBg[sel].BackgroundColor = SelectColor;
            foreach (var m in _legalForSelected)
                _cellBg[m.To].BackgroundColor = _board[m.To].IsEmpty ? MarkerTint(BaseColor(m.To)) : SelectColor;
        }
    }

    // A subtle dot-like tint on empty legal targets (no overlay shapes on a Button, so tint the square).
    static Color MarkerTint(Color baseColor) =>
        new(baseColor.Red * 0.78f, baseColor.Green * 0.78f, baseColor.Blue * 0.78f);

    Color BaseColor(int sq) => (Move.FileOf(sq) + Move.RankOf(sq)) % 2 == 0 ? DarkSquare : LightSquare;

    void SetGlyph(int sq)
    {
        var p = _board[sq];
        if (p.IsEmpty) { _cells[sq].Text = ""; _cells[sq].Shadow = null; return; }
        string glyph = p.Type switch
        {
            PieceType.King => "♚",
            PieceType.Queen => "♛",
            PieceType.Rook => "♜",
            PieceType.Bishop => "♝",
            PieceType.Knight => "♞",
            PieceType.Pawn => "♟",
            _ => ""
        };
        bool white = p.Color == GameColor.White;
        _cells[sq].Text = glyph;
        _cells[sq].TextColor = white ? WhitePiece : BlackPiece;
        // Outline the glyph with a contrasting halo so white pieces are legible on the light squares (and
        // black pieces on the dark squares): a zero-offset shadow traces the glyph shape since the label's
        // own background is transparent.
        _cells[sq].Shadow = new Shadow
        {
            Brush = white ? Brush.Black : Brush.White,
            Radius = 4,
            Opacity = 1f,
            Offset = new Point(0, 0),
        };
    }

    int FindKing(GameColor color)
    {
        for (int sq = 0; sq < 64; sq++)
            if (_board[sq].Type == PieceType.King && _board[sq].Color == color) return sq;
        return -1;
    }

    // ---- Connection state ----
    void ApplyConnectionState()
    {
        bool canConfigure = _connected && _synced && !_playing && !_refreshing && !_joining;   // wait for sync
        CreateBtn.IsEnabled = canConfigure;
        JoinBtn.IsEnabled = canConfigure;
        ResignBtn.IsEnabled = _connected && _playing && !_gameOver && !_resigning;
        SaveBtn.IsEnabled = _connected && _playing && !_gameOver;
        ResendBtn.IsEnabled = _connected && _pending.Values.Any(p => p.IsMove && p.NeedsResend);
        CancelBtn.IsEnabled = _connected && _playing && !_gameOver && (_awaitingOpponent || _resigning);

        // The Device tab owns the Host entry / Connect button and the firmware/voltage readout; let it refresh.
        StateChanged?.Invoke();
    }

    // Drives the Connect/Disconnect button and device-info readout on the Device tab (WiFi path). Prefers the
    // TCP stream API (port 4403) — the same fast link the native app uses, which streams the node DB in one
    // burst instead of one slow HTTP round-trip per packet — and falls back to the HTTP REST API if 4403 is
    // closed.
    async Task<string> ConnectAsync(string hostInput)
    {
        string host = (hostInput ?? "").Trim();
        if (host.Length == 0 || host == "http://")
            return "Enter the device's address, e.g. http://192.168.1.50";
        _isProxy = false;   // a direct device connection (the proxy:// branch below sets this back to true)

        // Proxy: "proxy://host[:port]" connects (TLS) to a Meshtastic.Proxy that shares one device with several
        // apps. The proxy speaks the same framed protocol, so from here it's an ordinary client connection.
        if (host.StartsWith("proxy://", StringComparison.OrdinalIgnoreCase))
            return await ConnectViaProxyAsync(host);

        if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "http://" + host;

        string ipHost;
        try { ipHost = new Uri(host).Host; }
        catch { return "That doesn't look like a valid address. Try e.g. http://192.168.1.50"; }
        const string failHint = "Check it's powered on, on the same WiFi, and its API is enabled.";
        _reconnectBle = null;   // a WiFi connect — auto-reconnect uses the host, not the BLE rebuild

        // 1) Try the fast TCP stream API first.
        TcpStreamMeshTransport? tcp = null;
        try { tcp = await TcpStreamMeshTransport.ConnectAsync(ipHost, TcpStreamMeshTransport.DefaultPort, TimeSpan.FromSeconds(5)); }
        catch { tcp = null; }
        if (tcp != null)
        {
            var t = tcp;
            _probeHost = ipHost; _probePort = TcpStreamMeshTransport.DefaultPort;
            var res = await ConnectCoreAsync(() => new MeshtasticHttpClient(t), host, $"{ipHost} (fast TCP)", isIp: true, failHint);
            if (_connected) return res;   // otherwise the handshake failed over TCP — fall back to HTTP below
        }

        // 2) Fall back to the HTTP REST API.
        _probeHost = ipHost; _probePort = SafePort(host, 80);
        return await ConnectCoreAsync(() => new MeshtasticHttpClient(host), host, host, isIp: true, failHint);
    }

    static int SafePort(string url, int fallback)
    {
        try { var p = new Uri(url).Port; return p > 0 ? p : fallback; } catch { return fallback; }
    }

    // Connects to a Meshtastic.Proxy over TLS ("proxy://host[:port]"). The proxy multiplexes one device to several
    // apps, so this is just an ordinary client connection over an encrypted stream — the rest of the flow is shared.
    async Task<string> ConnectViaProxyAsync(string proxyUrl)
    {
        string rest = proxyUrl.Substring("proxy://".Length).Trim('/');
        string ph = rest;
        int pp = TcpStreamMeshTransport.DefaultPort;
        int colon = rest.LastIndexOf(':');
        if (colon > 0 && int.TryParse(rest[(colon + 1)..], out var pv)) { ph = rest[..colon]; pp = pv; }
        if (ph.Length == 0) return "Enter the proxy address, e.g. proxy://192.168.1.50:4403";

        // Use the remembered login (if any); if the proxy demands auth we don't have, prompt and retry. A proxy
        // with no auth connects on the first try and never prompts.
        TcpStreamMeshTransport? proxy = null;
        var saved = AppSettings.GetProxyCred(ph);
        string? user = saved?.User, pass = saved?.Pass;
        while (proxy == null)
        {
            try { proxy = await TcpStreamMeshTransport.ConnectTlsAsync(ph, pp, TimeSpan.FromSeconds(8), user, pass); }
            catch (ProxyAuthException ax)
            {
                var page = new ProxyCredentialsPage(ph, ax.Rejected ? ax.Message : null, user);
                await Navigation.PushModalAsync(page);
                var (ok, u, p, remember) = await page.Result;
                if (Navigation.ModalStack.Contains(page)) await Navigation.PopModalAsync();
                if (!ok) return "Proxy sign-in cancelled.";
                user = u; pass = p;
                if (remember) AppSettings.SetProxyCred(ph, user, pass);
                else AppSettings.ClearProxyCred(ph);
            }
            catch (Exception ex) { return $"Couldn't reach the proxy at {ph}:{pp} — {ex.Message}"; }
        }

        _reconnectBle = null;
        _probeHost = ph; _probePort = pp;
        _isProxy = true;   // a proxy link supports message catch-up (backfill); a direct device does not
        return await ConnectCoreAsync(() => new MeshtasticHttpClient(proxy!), proxyUrl, $"proxy {ph}:{pp}",
            isIp: true, "Check the proxy is running and reachable.");
    }

    /// <summary>Connect over an already-established BLE link (the Device tab handles scan + GATT connect, then
    /// hands the ready transport here). <paramref name="cacheKey"/> keys this device's channel prefs/cache.
    /// <paramref name="rebuild"/>, when supplied, re-establishes the BLE link for auto-reconnect (re-runs the GATT
    /// connect and returns a fresh transport).</summary>
    public Task<string> ConnectViaTransportAsync(IMeshTransport transport, string label, string cacheKey,
                                                 Func<Task<IMeshTransport>>? rebuild = null)
    {
        _isProxy = false;   // a direct BLE device connection — no proxy catch-up
        // Remember how to rebuild this BLE link so auto-reconnect can re-establish it when it drops.
        _reconnectBle = rebuild == null ? null : async () =>
        {
            var fresh = await rebuild();
            await ConnectViaTransportAsync(fresh, label, cacheKey, rebuild);
        };
        return ConnectCoreAsync(() => new MeshtasticHttpClient(transport), cacheKey, label, isIp: false,
            "Check the radio is powered on and in range.", BleConnectTimeout);
    }

    // Shared connect: runs the want_config handshake on a fresh client (re-subscribes the device so message
    // forwarding is re-armed, drains the backlog in the background), then arms state + background sync. The
    // build delegate constructs the client over the chosen transport; isIp toggles the TCP reachability probe.
    async Task<string> ConnectCoreAsync(Func<MeshtasticHttpClient> build, string cacheKey, string label,
                                        bool isIp, string failHint, TimeSpan? timeout = null)
    {
        if (_connecting) return "Already connecting.";
        _connecting = true;
        var connectTimeout = timeout ?? ConnectTimeout;
        _lastPollOkUtc = DateTime.UtcNow; _probeFailures = 0;   // give the first poll a chance before the probe runs
        ApplyConnectionState();
        Status($"Connecting to {label} (timeout {connectTimeout.TotalSeconds:0}s)...");

        using var cts = new CancellationTokenSource(connectTimeout);
        MeshtasticHttpClient? mesh = null;
        string result;
        try
        {
            mesh = build();
            await mesh.InitializeAsync(cts.Token);
            _mesh?.Dispose();
            _mesh = mesh; mesh = null;
            _mesh.AdminActivity += OnAdminActivity;      // log admin messages (sent/received) to system messages (Admin)
            _mesh.IncomingRequest += OnIncomingRequest;  // log position/telemetry/noise-floor requests from others (Requests)
            _currentHost = cacheKey; _transportIsIp = isIp; _connected = true; _synced = false;
            if (isIp) { AppSettings.LastHost = cacheKey; AppSettings.AddRecentHost(cacheKey); }   // remember for the Host dropdown
            // If this device's cache is encrypted, unlock (or delete) it before reading or writing any cache.
            if (!await EnsureCacheUnlockedAsync(cacheKey)) { Disconnect("Cache locked — connection cancelled."); return "Cache locked — connection cancelled."; }
            DeviceCache.Save(cacheKey, _mesh.GetAvailableChannels(), _mesh.MyNodeNum);
            LoadChannelPrefs(cacheKey, _mesh.GetAvailableChannels());
            result = "Connected.";
            Status("Connected.");
            SetSyncStatus("Connected; syncing with the mesh…");
            ApplyConnectionState();
            // Keep the process alive while the screen is off so the receive loop survives sleep, and ask for
            // notification permission so we can alert on new messages in the background. WiFi/HTTP also needs a
            // WiFi + CPU wake lock (isIp) to keep polling; BLE wakes on packets without one.
            BackgroundConnection.Start(label, keepWifiAwake: isIp);
            // If the user swipes the app away, close the link cleanly first (matters for BLE — see CleanupOnAppClose).
            BackgroundConnection.CleanupOnAppClose = async () =>
            {
                var m = _mesh; _mesh = null;
                if (m != null) { try { await m.CloseAsync(); } catch { /* shutting down */ } }
            };
            _ = BackgroundConnection.EnsureNotificationPermissionAsync();
            _ = RunSyncAsync();
        }
        catch (OperationCanceledException)
        {
            result = $"Connection timed out after {connectTimeout.TotalSeconds:0}s. {failHint}";
            Status(result);
        }
        catch (Exception ex)
        {
            result = $"Connection failed: {ex.Message}. {failHint}";
            Status("Connection failed.");
        }
        finally
        {
            mesh?.Dispose();
            _connecting = false;
            ApplyConnectionState();
        }
        return result;
    }

    /// <summary>If the device's cache is encrypted, loops a password prompt until it unlocks or the user deletes
    /// the cache. Returns false only if the user cancels (caller aborts the connection).</summary>
    async Task<bool> EnsureCacheUnlockedAsync(string host)
    {
        string? err = null;
        while (DeviceCache.IsEncrypted(host) && !DeviceCache.IsUnlocked(host))
        {
            var page = new PasswordPromptPage(err);
            await Navigation.PushModalAsync(page);
            var (pw, delete) = await page.Result;
            if (Navigation.ModalStack.Contains(page)) await Navigation.PopModalAsync();
            if (delete)
            {
                bool ok = await DisplayAlert("Delete cache",
                    "Delete all cached data for this device and reset its password? This cannot be undone.", "Delete", "Cancel");
                if (ok) { DeviceCache.ClearDevice(host); return true; }
                continue;
            }
            if (pw == null) return false;   // cancelled / backed out
            if (DeviceCache.Unlock(host, pw)) return true;
            err = "Wrong password — try again.";
        }
        return true;
    }

    void Disconnect(string statusMessage, bool forgetCache = true)
    {
        // Keep the cache password across a dropped-connection/auto-reconnect (forgetCache:false) so it doesn't
        // re-prompt on every blip; forget it on a user-initiated disconnect so reconnect asks again.
        if (forgetCache && _currentHost.Length > 0) DeviceCache.ForgetSession(_currentHost);
        BackgroundConnection.CleanupOnAppClose = null;   // no live link to clean up on app close anymore
        BackgroundConnection.Stop();   // drop the keep-alive foreground service + its ongoing notification
        _pollTimer!.Stop();
        _mesh?.Dispose();
        _mesh = null;
        _syncStatus = "";
        _connected = _synced = _syncing = _playing = _gameOver = false;
        _probeFailures = 0;
        _selected = null;
        _legalForSelected.Clear();
        _lastMove = null;
        _pending.Clear();
        _chatAckers.Clear();
        _overheardAcks.Clear();
        _pendingGames.Clear();
        _joining = false; _joinGame = null; _resigning = false; _createInfo = null;
        _moveHistory.Clear();
        _loadedGame = null; _loadedName = "";
        _rxHidden.Clear(); _unread.Clear();   // reset the RX view filter for the next device
        _board = Board.CreateStartingPosition();
        _ply = 0;
        BuildBoard();
        Render();
        ApplyConnectionState();
        Status(statusMessage);
    }

    void LoadChannelPrefs(string host, IReadOnlyList<MeshChannel> channels)
    {
        _chatAckOn.Clear();
        foreach (var i in DeviceCache.GetChatAckOn(host)) _chatAckOn.Add(i);
        _chatAckSignalOn.Clear();
        foreach (var i in DeviceCache.GetAckSignalOn(host)) _chatAckSignalOn.Add(i);
        LoadAckTriggers(host);
        _nodePrefs.Clear();
        foreach (var kv in DeviceCache.GetNodePrefs(host)) _nodePrefs[kv.Key] = new DeviceCache.NodePrefs { Dm = kv.Value.Dm, Block = kv.Value.Block };
        if (_mesh != null)
            foreach (var kv in DeviceCache.GetChannelKeys(host)) _mesh.SetChannelKey(kv.Key, kv.Value);
        // Restore the utility/info channel (position/telemetry/node-info requests + manual broadcasts); null = auto.
        _mesh?.SetUtilityChannel(DeviceCache.GetUtilityChannel(host));

        // Seed cached telemetry history so it's available immediately on (re)connect, before any live reading.
        if (_mesh != null)
        {
            var seed = new Dictionary<uint, List<MeshEnvironment>>();
            foreach (var kv in DeviceCache.GetTelemetry(host)) seed[kv.Key] = kv.Value.Select(ToEnv).ToList();
            _mesh.SeedEnvironment(seed);

            // Seed cached node names/roles/hardware so nodes the device has since forgotten still show in the
            // nodes list (and aren't dropped from the saved cache when we next persist the live set).
            var cached = DeviceCache.Get(host);
            if (cached != null) _mesh.SeedNodes(cached.NodeNames, cached.NodeRoles, cached.NodeHw, nodeShortNames: cached.NodeShortNames,
                nodeFavorites: cached.NodeFavorites, nodeIgnored: cached.NodeIgnored, nodeHopsAway: cached.NodeHopsAway, nodeLastHeard: cached.NodeLastHeard);

            // Seed cached node positions so the map / "open in Google Maps" works immediately on reconnect, before
            // any live position is heard again (MAUI now persists positions, matching the desktop app).
            var pos = DeviceCache.GetPositions(host);
            if (pos.Count > 0)
                _mesh.SeedNodes(nodePositions: pos.ToDictionary(kv => kv.Key,
                    kv => (kv.Value.Lat, kv.Value.Lon, kv.Value.LastHeard, kv.Value.PosTime)));

            // Seed cached position tracks so the map's right-click "recent positions" view works immediately on reconnect.
            var ph = DeviceCache.GetPositionHistory(host);
            if (ph.Count > 0)
                _mesh.SeedPositionHistory(ph.ToDictionary(kv => kv.Key,
                    kv => kv.Value.Select(p => (p.Lat, p.Lon, p.LastHeard, p.PosTime)).ToList()));
        }

        var available = channels.Select(c => c.Index).ToHashSet();
        uint primary = channels.OrderBy(c => c.Index).Select(c => (uint?)c.Index).FirstOrDefault() ?? 0;
        var prefs = DeviceCache.GetChannelPrefs(host);

        uint chess = prefs?.ChessChannel ?? primary;
        if (!available.Contains(chess)) chess = primary;
        if (_mesh != null) _mesh.ChannelIndex = chess;

        // The selected channels = what's shown in chat AND what you can send to (chosen via the chat RX dropdown).
        _chatListen.Clear();
        if (prefs?.ChatListen is { } saved)
            foreach (var i in saved) _chatListen.Add(i);             // a previously-saved selection (may be empty)
        else
            foreach (var i in ReceiveChannels()) _chatListen.Add(i);  // first connect: show + TX every enabled channel

        _chatTxChannel = prefs?.ChatTxChannel ?? chess;
        if (!_chatListen.Contains(_chatTxChannel)) _chatTxChannel = _chatListen.FirstOrDefault();
        RebuildChatTxPicker();
        DeviceCache.PruneChatByRetention(host);   // drop cache messages past their auto-delete age before loading
        DeviceCache.PruneChatByExpiry(host);      // …and any past their sender-set self-destruct time
        LoadCachedChat(host);
    }

    // Renders the per-channel cached chat history (for listened channels) into the chat log on connect, oldest
    // first, followed by a divider. No-op if chat already has rows (avoids dupes on reconnect).
    void LoadCachedChat(string host)
    {
        if (host.Length == 0 || _chat.Count > 0) return;
        var chat = DeviceCache.GetChat(host);
        var msgs = new List<(DateTime Time, string Text, string Detail, uint Channel, string? Id, DateTime ExpiresAt)>();
        foreach (var ch in ReceiveChannels())   // all enabled channels (RX shows everything; the filter hides)
            if (chat.TryGetValue(ch, out var list))
                foreach (var m in list) msgs.Add((m.Time, m.Text, m.Detail, ch, m.Id, m.ExpiresAt));
        if (msgs.Count == 0) return;
        foreach (var m in msgs.OrderBy(m => m.Time))
        {
            var e = AddChatLine(m.Text, m.Detail, Palette.Cached);   // grey — saved history from a previous session
            e.Channel = m.Channel;
            e.CacheId = m.Id;
            e.Time = m.Time;   // preserve original time for age-based auto-delete
            if (m.ExpiresAt != default)   // resume the self-destruct countdown for a still-pending message
            {
                e.ExpiresAt = m.ExpiresAt;
                e.Expiry = "🕓 " + ExpiryCountdown(m.ExpiresAt - DateTime.Now);
            }
        }
        var divider = AddChatLine("──────── saved history above · live messages below ────────", "", Color.FromArgb("#8A8A8A"));
        divider.Channel = uint.MaxValue;
    }

    // Caches a chat line (latest 100 per channel) for the current device; returns the stable id stamped on the row.
    string? CacheChat(uint channel, string text, string detail, uint rxTime = 0, DateTime expiresAt = default)
    {
        if (_currentHost.Length == 0 || !AppSettings.CacheMessages) return null;   // caching disabled in System settings
        string id = Guid.NewGuid().ToString("N");
        DeviceCache.AppendChat(_currentHost, channel, new DeviceCache.ChatMessage { Text = text, Detail = detail, Time = DateTime.Now, Id = id, RxTime = rxTime, ExpiresAt = expiresAt });
        return id;
    }

    // The newest message rx_time we have cached for a device — sent to Meshtastic.Proxy so it backfills only newer.
    static long MaxCachedRxTime(string host)
    {
        long max = 0;
        foreach (var list in DeviceCache.GetChat(host).Values)
            foreach (var m in list) if (m.RxTime > max) max = m.RxTime;
        return max;
    }

    static MeshEnvironment ToEnv(DeviceCache.TelemetryReading r) => new(r.Temperature, r.Humidity, r.Pressure, r.RxTime, r.ReceivedAt);
    static DeviceCache.TelemetryReading ToReading(MeshEnvironment e) =>
        new() { Temperature = e.TemperatureC, Humidity = e.RelativeHumidity, Pressure = e.BarometricPressure, RxTime = e.RxTime, ReceivedAt = e.ReceivedAt };

    /// <summary>Removes a chat row from the per-channel cache (used by the Chat tab's "Remove message").</summary>
    public void RemoveCachedChat(LogEntry entry)
    {
        if (_currentHost.Length > 0 && entry.Channel != uint.MaxValue)
            DeviceCache.RemoveChat(_currentHost, entry.Channel, entry.CacheId, entry.Text, entry.Detail);
    }

    // ---- Chat TX channel picker ----
    // The Chess tab no longer hosts a chat composer (chat is its own tab). Keep _chatTxChannel pointing at a
    // valid listened channel; the Chat tab builds its own TX picker from ChatTxChannels()/CurrentChatTx.
    void RebuildChatTxPicker()
    {
        if (_chatListen.Count > 0 && !_chatListen.Contains(_chatTxChannel))
            _chatTxChannel = _chatListen.First();
        // Drop a DM destination whose node is no longer DM-enabled (un-checked or blocked).
        if (_chatTxDest is uint d && !(_nodePrefs.TryGetValue(d, out var p) && p.Dm && !p.Block))
            _chatTxDest = null;
        ApplyConnectionState();
    }

    // ---- Channels / Colours / Sound dialogs ----
    async void OnChannelsClicked(object? sender, EventArgs e)
    {
        if (_mesh == null) return;
        bool wasPolling = _pollTimer!.IsRunning;
        _pollTimer.Stop();
        _refreshing = true;
        ApplyConnectionState();
        Status("Managing channels — polling paused.");
        try
        {
            var page = new ChannelsPage(_mesh, _currentHost, _mesh.GetAvailableChannels(), _mesh.ChannelIndex, _playing);
            await Navigation.PushModalAsync(page);
            var result = await page.Completion;

            _mesh.ChannelIndex = result.ChessChannel;
            // Chat channel selection now lives in the chat RX dropdown, not this page — just keep a valid TX channel.
            if (!_chatListen.Contains(_chatTxChannel))
                _chatTxChannel = _chatListen.Contains(result.ChessChannel) ? result.ChessChannel : _chatListen.FirstOrDefault();
            RebuildChatTxPicker();

            if (_currentHost.Length > 0)
            {
                DeviceCache.Save(_currentHost, result.Channels.Where(c => !c.IsDisabled), _mesh.MyNodeNum);
                DeviceCache.SaveChannelPrefs(_currentHost, _mesh.ChannelIndex, _chatListen, _chatTxChannel);
            }
            _chatAckOn.Clear();
            foreach (var i in DeviceCache.GetChatAckOn(_currentHost)) _chatAckOn.Add(i);
            _chatAckSignalOn.Clear();
            foreach (var i in DeviceCache.GetAckSignalOn(_currentHost)) _chatAckSignalOn.Add(i);
            LoadAckTriggers(_currentHost);
            Status($"Chess on channel [{_mesh.ChannelIndex}]; chat listening on {_chatListen.Count} channel(s).");
        }
        catch (Exception ex) { Status($"Channel manager error: {ex.Message}"); }
        finally
        {
            _refreshing = false;
            _lastRxUtc = DateTime.UtcNow; _rxStallWarned = false;   // polling was paused — re-arm the watchdog
            ApplyConnectionState();
            if (wasPolling) _pollTimer.Start();
        }
    }

    /// <summary>Opens the device settings editor (reads current config, writes changes via admin messages).
    /// Polling is paused while it's open so its fetch/admin writes can drain the radio queue for their acks —
    /// the same pattern as the channel manager.</summary>
    public async void OpenDeviceSettings()
    {
        if (_mesh == null) return;
        bool wasPolling = _pollTimer!.IsRunning;
        _pollTimer.Stop();
        _refreshing = true;
        ApplyConnectionState();
        Status("Device settings — polling paused.");
        try
        {
            var page = new DeviceConfigPage(_mesh);
            await Navigation.PushModalAsync(page);
            await page.Completion;
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

    async void OnColorsClicked(object? sender, EventArgs e)
    {
        var fonts = new List<ColorSettingsPage.FontChoice>
        {
            new("Moves", _movesFamily, _movesSize, ApplyMovesFont),
            new("System messages", _systemFamily, _systemSize, ApplySystemFont),
            new("Chat text", _chatFamily, _chatSize, ApplyChatFont),
            new("Nodes", _nodesFamily, _nodesSize, ApplyNodesFont),
        };
        await Navigation.PushModalAsync(new ColorSettingsPage(fonts));
    }

    async void OnSoundClicked(object? sender, EventArgs e)
    {
        var page = new SoundSettingsPage(_chessSound, _chessVolume, _chatSound, _chatVolume);
        await Navigation.PushModalAsync(page);
        var r = await page.Completion;
        _chessSound = r.ChessSound; _chessVolume = r.ChessVolume;
        _chatSound = r.ChatSound; _chatVolume = r.ChatVolume;
        AppSettings.ChessSoundPath = _chessSound; AppSettings.ChessVolume = _chessVolume;
        AppSettings.ChatSoundPath = _chatSound; AppSettings.ChatVolume = _chatVolume;
    }

    void PlayChessSound() => SoundService.Play(_chessSound, _chessVolume);
    void PlayChatSound() => SoundService.Play(_chatSound, _chatVolume);

    /// <summary>Applies the "Show chessboard" setting: when off, the board, game buttons and the Moves/System
    /// toggle are hidden and only the system-messages list is shown (the bottom tab is renamed by AppShell).
    /// Called at startup and live from the System settings switch.</summary>
    public void ApplyChessboardVisibility()
    {
        bool show = AppSettings.ShowChessboard;
        GameButtons.IsVisible = show;   // New game / Join / Resign … (chess actions)
        BoardHost.IsVisible = show;     // the board
        TabButtons.IsVisible = show;    // Moves / System toggle + Copy
        SelectTab(show ? TabMoves : TabSystem, show ? MovesView : SystemView);
    }

    // ---- Tabs ----
    void OnTabClicked(object? sender, EventArgs e)
    {
        if (sender == TabMoves) SelectTab(TabMoves, MovesView);
        else if (sender == TabSystem) SelectTab(TabSystem, SystemView);
    }

    // Copies the currently-shown log (Moves or System) to the clipboard so it can be pasted/shared for diagnostics.
    async void OnCopyLogClicked(object? sender, EventArgs e)
    {
        var src = SystemView.IsVisible ? _system : _moves;
        var sb = new System.Text.StringBuilder();
        foreach (var le in src)
        {
            sb.Append(le.Text);
            if (!string.IsNullOrEmpty(le.Detail)) { sb.Append("   "); sb.Append(le.Detail); }
            sb.Append('\n');
        }
        try
        {
            await Clipboard.Default.SetTextAsync(sb.ToString());
            Status($"Copied {src.Count} {(SystemView.IsVisible ? "System" : "Moves")} line(s) to the clipboard.");
        }
        catch (Exception ex) { Status($"Copy failed: {ex.Message}"); }
    }

    async void OnSystemFilterClicked(object? sender, EventArgs e)
        => await Navigation.PushModalAsync(new SystemFilterPage(this));

    void SelectTab(Button tab, CollectionView view)
    {
        MovesView.IsVisible = view == MovesView;
        SystemView.IsVisible = view == SystemView;
        SystemFilterBtn.IsVisible = view == SystemView;   // the filter only applies to the System list
        foreach (var t in new[] { TabMoves, TabSystem })
        {
            bool selected = t == tab;
            t.BackgroundColor = selected ? Color.FromRgb(0x3F, 0x6F, 0x4F) : Color.FromRgb(0x2D, 0x2D, 0x30);
            // Explicit text colour: the platform default is too dark to read on these backgrounds.
            t.TextColor = selected ? Colors.White : Color.FromRgb(0xC8, 0xC8, 0xC8);
        }
    }

    // ---- Create / load / join ----
    /// <summary>True when a valid chess channel is selected. Chess can't run on the primary channel (0), so if
    /// none is set this prompts the user to pick or create one in Channels and returns false.</summary>
    async Task<bool> EnsureChessChannelSelectedAsync()
    {
        if (_mesh != null && _mesh.ChannelIndex != 0) return true;
        await DisplayAlert("Choose a chess channel",
            "No chess channel is set for this device.\n\n" +
            "Chess needs a dedicated secondary channel — it can't use the primary channel (0). " +
            "Open Channels to pick an existing channel for chess, or create one, then start a game.",
            "OK");
        return false;
    }

    async void OnCreateClicked(object? sender, EventArgs e)
    {
        if (_mesh == null) return;
        if (!await EnsureChessChannelSelectedAsync()) return;   // chess needs a non-primary channel to host
        string choice = await DisplayActionSheet("Create game", "Cancel", null, "New game", "Load saved game…");
        if (choice == "Load saved game…")
        {
            if (!await LoadGameFromFile()) return;
        }
        else if (choice == "New game") { _loadedGame = null; _loadedName = ""; }
        else return;

        string? id = await DisplayPromptAsync("Create game", "Game id:", "Next", "Cancel", initialValue: NewGameId());
        if (id == null) return;
        id = id.Trim();
        if (id.Length == 0) id = NewGameId();

        string role = RoleAuto;
        if (_loadedGame == null)
        {
            string r = await DisplayActionSheet("Your colour", "Cancel", null, RoleAuto, RoleWhite, RoleBlack);
            if (r == null || r == "Cancel") return;
            role = r;
        }
        StartHostedGame(id, role);
    }

    void StartHostedGame(string gameId, string role)
    {
        if (_mesh == null) return;
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
        _loadedGame = null; _loadedName = "";
        AnnounceCreate(gameId, color, saveName);
    }

    async void AnnounceCreate(string gameId, GameColor color, string saveName = "")
    {
        if (_mesh == null) return;
        string resume = string.IsNullOrEmpty(saveName) ? "" : $" resuming '{saveName}'";
        string baseText = Stamp() + $"Created game '{gameId}' as {color}{resume} (channel {_mesh.ChannelIndex})";
        _createInfo = new ChatAckInfo { Entry = AddSystem(baseText + MarkSending, Palette.Pending), BaseText = baseText };

        string payload = ProtocolMessage.EncodeNew(gameId, color, saveName);
        try
        {
            uint id = await _mesh.SendTextAsync(payload);
            _pending[id] = new PendingSend { Id = id, Payload = payload, LastSentUtc = DateTime.UtcNow, Attempts = 1, IsCreate = true, Label = $"game '{gameId}'" };
            Status($"Game '{gameId}' created as {color} — waiting for acknowledgement…");
        }
        catch (Exception ex) { FailCreate($"Could not announce the game: {ex.Message}"); }
    }

    void HandleCreateAck(ProtocolMessage pm, MeshTextMessage msg)
    {
        if (_createInfo == null || pm.GameId != _gameId) return;
        if (!_createInfo.Ackers.Add(msg.FromNode)) return;
        var cp = _pending.Values.FirstOrDefault(p => p.IsCreate);
        if (cp != null) _pending.Remove(cp.Id);
        string names = string.Join(", ", _createInfo.Ackers.Select(a => _mesh?.DescribeNode(a) ?? a.ToString()));
        _createInfo.Entry.Text = $"{_createInfo.BaseText} {MarkDelivered.Trim()} acked by: {names}";
        _createInfo.Entry.TextColor = Palette.Acked;
        Status($"Game '{_gameId}' acknowledged. Waiting for an opponent to join…");
    }

    async void FailCreate(string reason)
    {
        var cp = _pending.Values.FirstOrDefault(p => p.IsCreate);
        if (cp != null) _pending.Remove(cp.Id);
        string gid = _gameId;
        if (_createInfo != null)
        {
            _createInfo.Entry.Text = $"{_createInfo.BaseText} {MarkFailed.Trim()} — not acknowledged; game not created";
            _createInfo.Entry.TextColor = Palette.Warning;
        }
        _createInfo = null;
        _playing = _gameOver = _awaitingOpponent = false;
        _gameId = "";
        _board = Board.CreateStartingPosition(); _ply = 0;
        _moveHistory.Clear();
        _selected = null; _legalForSelected.Clear(); _lastMove = null;
        BuildBoard(); Render(); ApplyConnectionState();
        Status("Game creation not acknowledged — try creating again.");
        await DisplayAlert("No acknowledgement", $"No one acknowledged your new game '{gid}'.\n\n{reason}", "OK");
    }

    static string NewGameId() => Guid.NewGuid().ToString("N").Substring(0, 4);

    async void OnJoinClicked(object? sender, EventArgs e)
    {
        if (_mesh == null) return;
        if (!await EnsureChessChannelSelectedAsync()) return;   // chess channel needed to receive announcements & join
        var games = _pendingGames.Values.ToList();
        if (games.Count == 0) { await DisplayAlert("Join a game", "No open games yet. Wait for someone to create one.", "OK"); return; }
        var labels = games.Select(g => $"'{g.GameId}' by {_mesh.DescribeNode(g.CreatorNode)} (you'd be {g.CreatorColor.Opposite()})").ToArray();
        string pick = await DisplayActionSheet("Open games", "Cancel", null, labels);
        if (pick == null || pick == "Cancel") return;
        int i = Array.IndexOf(labels, pick);
        if (i < 0) return;
        JoinGame(games[i]);
    }

    async void JoinGame(PendingGame game)
    {
        if (_mesh == null) return;
        GameColor myColor = game.CreatorColor.Opposite();
        _mesh.ChannelIndex = game.Channel;
        _pendingGames.Remove(game.GameId);
        _joining = true; _joinGame = game; _joinColor = myColor;
        ApplyConnectionState();

        string payload = ProtocolMessage.EncodeJoin(game.GameId, myColor);
        try
        {
            uint id = await _mesh.SendTextAsync(payload);
            _pending[id] = new PendingSend { Id = id, Payload = payload, LastSentUtc = DateTime.UtcNow, Attempts = 1, IsJoin = true, Label = $"join '{game.GameId}'" };
            Status($"Joining {_mesh.DescribeNode(game.CreatorNode)}'s game '{game.GameId}' as {myColor} — waiting for the host to send the board…");
        }
        catch (Exception ex) { FailJoin($"Could not send join request: {ex.Message}"); }
    }

    void HandleBoard(ProtocolMessage pm)
    {
        if (!_joining || _joinGame == null || pm.GameId != _joinGame.GameId) return;
        var joinEntry = _pending.Values.FirstOrDefault(p => p.IsJoin);
        if (joinEntry != null) _pending.Remove(joinEntry.Id);
        var game = _joinGame;
        var color = pm.AnnouncedColor ?? _joinColor;
        _joining = false; _joinGame = null;
        BeginGame(color, game.GameId, pm.Fen);
        AddSystem(Stamp() + $"— Joined {_mesh?.DescribeNode(game.CreatorNode)}'s game '{game.GameId}' as {color} (board received from host). —");
        Status($"Joined game '{game.GameId}' — you are {color}.");
    }

    async void FailJoin(string reason)
    {
        var joinEntry = _pending.Values.FirstOrDefault(p => p.IsJoin);
        if (joinEntry != null) _pending.Remove(joinEntry.Id);
        string gameId = _joinGame?.GameId ?? "";
        _joining = false; _joinGame = null;
        ApplyConnectionState();
        Status("Join failed — no acknowledgement. Try joining again.");
        await DisplayAlert("No acknowledgement", $"No acknowledgement from the host for game '{gameId}'.\n\n{reason}", "OK");
    }

    void BeginGame(GameColor color, string gameId, string? fen = null, bool awaitingOpponent = false)
    {
        _awaitingOpponent = awaitingOpponent;
        _myColor = color;
        _gameId = gameId;
        _moveHistory.Clear();
        _moves.Clear();
        if (!string.IsNullOrWhiteSpace(fen)) { _board = Board.FromFen(fen); _ply = _board.HalfMovesPlayed(); }
        else { _board = Board.CreateStartingPosition(); _ply = 0; }
        _lastMove = null; _selected = null; _legalForSelected.Clear();
        _gameOver = false; _playing = true;
        _pending.Clear(); _createInfo = null;
        _pendingGames.Remove(gameId);
        ApplyConnectionState();
        BuildBoard(); Render(); UpdateTurnStatus();
    }

    async void SendControl(string text, bool encrypt = true, bool delay = false)
    {
        if (_mesh == null) return;
        if (delay) await AckJitterDelay();
        try { await _mesh.SendTextAsync(text, encrypt: encrypt); } catch { }
    }

    // ---- Cancel ----
    async void OnCancelClicked(object? sender, EventArgs e)
    {
        if (!_playing || _gameOver || !(_awaitingOpponent || _resigning)) return;
        if (!await DisplayAlert("Cancel game", "Cancel this game?", "Yes", "No")) return;
        CancelGame($"Game '{_gameId}' cancelled.");
    }

    void CancelGame(string status)
    {
        if (_gameId.Length > 0) SendControl(ProtocolMessage.EncodeCancel(_gameId));
        LeaveGame(status);
    }

    void LeaveGame(string status)
    {
        foreach (var key in _pending.Where(kv => kv.Value.IsMove || kv.Value.IsJoin || kv.Value.IsResign || kv.Value.IsCreate || kv.Value.IsSave)
                                    .Select(kv => kv.Key).ToList())
            _pending.Remove(key);
        _playing = _gameOver = _resigning = _awaitingOpponent = false;
        _createInfo = null; _gameId = "";
        _board = Board.CreateStartingPosition(); _ply = 0;
        _moveHistory.Clear();
        _selected = null; _legalForSelected.Clear(); _lastMove = null;
        BuildBoard(); Render(); ApplyConnectionState();
        Status(status);
    }

    void HandleCancel(ProtocolMessage pm)
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

    void HandleEnded(ProtocolMessage pm)
    {
        if (pm.GameId != _gameId || !_playing || _gameOver) return;
        foreach (var key in _pending.Where(kv => kv.Value.IsMove).Select(kv => kv.Key).ToList()) _pending.Remove(key);
        AddSystem(Stamp() + $"— Opponent reports game '{pm.GameId}' has already ended on their side. —");
        EndGame("Opponent has ended the game.");
    }

    // ---- Save / load ----
    GameSave CurrentSave() => new() { Fen = _board.ToFen(), MyColor = _myColor == GameColor.White ? "white" : "black" };

    static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace("|", "_").Trim();
    }

    async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (!_playing || _gameOver || _mesh == null) return;
        if (_pending.Values.Any(p => p.IsSave)) return;
        string? name = await DisplayPromptAsync("Save game", "Filename (both players save under this name):", "Save", "Cancel", initialValue: _gameId);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = SanitizeFileName(name);

        bool savedOk = GameStorage.Save(GameStorage.PathFor(name), CurrentSave());
        string baseText = Stamp() + $"Save request '{name}' sent to opponent — awaiting acknowledgement";
        LogEntry reqEntry = AddSystem(baseText + MarkSending, Palette.Pending);
        string payload = ProtocolMessage.EncodeSave(_gameId, name);
        try
        {
            uint id = await _mesh.SendTextAsync(payload);
            _pending[id] = new PendingSend { Id = id, Payload = payload, LastSentUtc = DateTime.UtcNow, Attempts = 1, IsSave = true, SaveFileName = name, Label = $"save '{name}'", Entry = reqEntry, BaseText = baseText };
        }
        catch (Exception ex)
        {
            reqEntry.Text = baseText + MarkFailed + $" — could not send ({ex.Message})";
            reqEntry.TextColor = Palette.Warning;
        }
        EndGame(savedOk ? $"Saved as '{name}' and ended the game — opponent invited to save a copy."
                        : $"Game ended, but writing your copy of '{name}' failed.");
    }

    async void HandleSaveRequest(ProtocolMessage pm, MeshTextMessage msg)
    {
        if (pm.GameId != _gameId || !_playing || _gameOver) return;
        string who = _mesh?.DescribeNode(msg.FromNode) ?? "Opponent";
        string saveName = pm.SaveName;
        bool yes = await DisplayAlert("Game ended",
            $"{who} ended the game and saved it as '{saveName}'.\n\nSave a copy on your side too?", "Save a copy", "End without saving");
        if (yes)
        {
            bool ok = GameStorage.Save(GameStorage.PathFor(saveName), CurrentSave());
            SendControl(ProtocolMessage.EncodeSaveAck(_gameId, ok), encrypt: false, delay: true);
            EndGame(ok ? $"Game saved as '{saveName}'." : $"Game ended, but writing your copy of '{saveName}' failed.");
        }
        else
        {
            SendControl(ProtocolMessage.EncodeSaveAck(_gameId, false), encrypt: false, delay: true);
            AddSystem(Stamp() + $"— Declined to save '{saveName}'. Game ended. —");
            EndGame($"Game ended without saving (declined '{saveName}').");
        }
    }

    void HandleSaveAck(ProtocolMessage pm)
    {
        var sp = _pending.Values.FirstOrDefault(p => p.IsSave);
        if (sp == null || pm.GameId != _gameId) return;
        _pending.Remove(sp.Id);
        if (sp.Entry != null)
        {
            sp.Entry.Text = sp.BaseText + $" {MarkDelivered.Trim()} " + (pm.SaveOk ? "opponent saved their copy" : "opponent declined (your copy is saved)");
            sp.Entry.TextColor = pm.SaveOk ? Palette.Acked : Palette.Normal;
        }
        Status(pm.SaveOk ? $"Opponent saved '{sp.SaveFileName}' too." : $"Opponent declined to save '{sp.SaveFileName}' (your copy is saved).");
    }

    void FailSave(string reason)
    {
        var sp = _pending.Values.FirstOrDefault(p => p.IsSave);
        if (sp == null) return;
        _pending.Remove(sp.Id);
        if (sp.Entry != null) { sp.Entry.Text = sp.BaseText + $" {MarkFailed.Trim()} not acknowledged by opponent"; sp.Entry.TextColor = Palette.Warning; }
        Status($"Opponent didn't acknowledge the save of '{sp.SaveFileName}' (your copy is saved).");
    }

    async Task<bool> LoadGameFromFile()
    {
        Directory.CreateDirectory(GameStorage.DefaultFolder);
        var files = Directory.Exists(GameStorage.DefaultFolder)
            ? Directory.GetFiles(GameStorage.DefaultFolder, "*.json").Select(Path.GetFileNameWithoutExtension).Where(s => s != null).Cast<string>().ToArray()
            : Array.Empty<string>();
        if (files.Length == 0) { await DisplayAlert("Load game", "No saved games found.", "OK"); return false; }
        string pick = await DisplayActionSheet("Load a saved game", "Cancel", null, files);
        if (pick == null || pick == "Cancel") return false;

        var game = GameStorage.Load(GameStorage.PathFor(pick));
        if (game == null) { await DisplayAlert("Load failed", "Couldn't read that save file.", "OK"); return false; }
        _loadedGame = game;
        _loadedName = pick;
        bool black = game.MyColor.Equals("black", StringComparison.OrdinalIgnoreCase);
        _myColor = black ? GameColor.Black : GameColor.White;
        _board = GameStorage.Rebuild(game, out int ply);
        _ply = ply; _lastMove = null;
        BuildBoard(); Render();
        return true;
    }

    // ---- Lobby ----
    void HandleGameAnnounced(ProtocolMessage pm, MeshTextMessage msg)
    {
        if (pm.GameId == _gameId && _playing) return;
        SendControl(ProtocolMessage.EncodeCreateAck(pm.GameId), encrypt: false, delay: true);
        bool isNew = !_pendingGames.ContainsKey(pm.GameId);
        var creatorColor = pm.AnnouncedColor ?? GameColor.White;
        string saveName = pm.SaveName ?? "";
        _pendingGames[pm.GameId] = new PendingGame(pm.GameId, msg.FromNode, creatorColor, msg.Channel, saveName);
        if (isNew)
        {
            string resumes = string.IsNullOrEmpty(saveName) ? "" : $" (resuming '{saveName}')";
            AddSystem(Stamp() + $"{_mesh?.DescribeNode(msg.FromNode)} started game '{pm.GameId}'{resumes} (they're {creatorColor}). Tap Join to play as {creatorColor.Opposite()}.");
        }
        ApplyConnectionState();
    }

    void HandleGameJoined(ProtocolMessage pm, MeshTextMessage msg)
    {
        _pendingGames.Remove(pm.GameId);
        if (pm.GameId == _gameId && _playing)
        {
            SendControl(ProtocolMessage.EncodeBoard(_gameId, _myColor.Opposite(), _board.ToFen()), encrypt: false);
            _awaitingOpponent = false;
            AddSystem(Stamp() + $"{_mesh?.DescribeNode(msg.FromNode)} joined as {_myColor.Opposite()} — sent them the board. Game on!");
            NotifyBackground("Opponent joined", $"{_mesh?.DescribeNode(msg.FromNode)} joined as {_myColor.Opposite()} — game on!");
            UpdateTurnStatus();
        }
        ApplyConnectionState();
    }

    // ---- Resign ----
    async void OnResignClicked(object? sender, EventArgs e)
    {
        if (!_playing || _gameOver || _resigning || _mesh == null) return;
        if (!await DisplayAlert("Resign", "Resign this game?", "Yes", "No")) return;
        _resigning = true;
        ApplyConnectionState();
        string payload = ProtocolMessage.EncodeResign(_gameId, _myColor);
        try
        {
            uint id = await _mesh.SendTextAsync(payload);
            _pending[id] = new PendingSend { Id = id, Payload = payload, LastSentUtc = DateTime.UtcNow, Attempts = 1, IsResign = true, Label = "resignation" };
            Status("Resigning — waiting for your opponent to acknowledge…");
        }
        catch (Exception ex) { FailResign($"Could not send resignation: {ex.Message}"); }
    }

    void HandleResignAck(ProtocolMessage pm)
    {
        if (!_resigning || pm.GameId != _gameId) return;
        var rp = _pending.Values.FirstOrDefault(p => p.IsResign);
        if (rp != null) _pending.Remove(rp.Id);
        _resigning = false;
        EndGame("You resigned. Opponent wins.");
    }

    async void FailResign(string reason)
    {
        var rp = _pending.Values.FirstOrDefault(p => p.IsResign);
        if (rp != null) _pending.Remove(rp.Id);
        _resigning = false;
        ApplyConnectionState();
        bool cancel = await DisplayAlert("No acknowledgement",
            $"Your opponent didn't acknowledge your resignation.\n\n{reason}\n\nCancel the game anyway?", "Cancel game", "Keep waiting");
        if (cancel) CancelGame("Game cancelled (resignation not acknowledged).");
        else Status("Resignation not acknowledged — resign or cancel again when ready.");
    }

    void HandleResign(ProtocolMessage pm)
    {
        if (pm.GameId != _gameId) return;
        SendControl(ProtocolMessage.EncodeResignAck(_gameId), encrypt: false, delay: true);
        if (_gameOver || !_playing) return;
        NotifyBackground("Opponent resigned", "You win!");
        EndGame("Opponent resigned. You win!");
    }

    // ---- Interaction ----
    async void OnCellTapped(int sq)
    {
        if (!_playing || _gameOver) return;
        if (_board.SideToMove != _myColor) { Status("Not your turn — waiting for opponent."); return; }

        if (_selected == null) { SelectSquare(sq); return; }
        var candidates = _legalForSelected.Where(m => m.To == sq).ToList();
        if (candidates.Count > 0) { await CommitLocalMoveAsync(await ResolvePromotion(candidates)); return; }
        if (!_board[sq].IsEmpty && _board[sq].Color == _myColor) SelectSquare(sq);
        else { _selected = null; _legalForSelected.Clear(); Render(); }
    }

    void SelectSquare(int sq)
    {
        var piece = _board[sq];
        if (piece.IsEmpty || piece.Color != _myColor) { _selected = null; _legalForSelected.Clear(); Render(); return; }
        _selected = sq;
        _legalForSelected = _board.GenerateLegalMoves().Where(m => m.From == sq).ToList();
        Render();
    }

    async Task<Move> ResolvePromotion(List<Move> candidates)
    {
        var promo = candidates.Where(m => m.Promotion != PieceType.None).ToList();
        if (promo.Count == 0) return candidates[0];
        string pick = await DisplayActionSheet("Promote to", null, null, "Queen", "Rook", "Bishop", "Knight");
        PieceType chosen = pick switch { "Rook" => PieceType.Rook, "Bishop" => PieceType.Bishop, "Knight" => PieceType.Knight, _ => PieceType.Queen };
        return promo.FirstOrDefault(m => m.Promotion == chosen, promo.First(m => m.Promotion == PieceType.Queen));
    }

    async Task CommitLocalMoveAsync(Move move)
    {
        if (_mesh == null) return;
        var snapshot = _board.Clone();
        int prevPly = _ply;
        var prevLast = _lastMove;
        var mover = _board.SideToMove;
        _board.MakeMove(move);
        _ply++;
        _moveHistory.Add(move.ToUci());
        _lastMove = (move.From, move.To);
        _selected = null; _legalForSelected.Clear();
        string baseText = Stamp() + MoveLine(_ply, mover, move);
        var entry = AddMove(baseText + MarkSending, Palette.Pending);
        Render();

        string payload = ProtocolMessage.EncodeMove(_gameId, _ply, move);
        try
        {
            var wait = _moveSendAllowedUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                Status($"Move made — transmitting in {Math.Ceiling(wait.TotalSeconds):0}s (finishing acknowledgement)…");
                await Task.Delay(wait);
            }
            uint id = await _mesh.SendTextAsync(payload);
            _pending[id] = new PendingSend { Id = id, Payload = payload, LastSentUtc = DateTime.UtcNow, Attempts = 1, IsMove = true, Ply = _ply, Label = $"move {move.ToUci()}", Entry = entry, BaseText = baseText };
            Status($"Sent {move.ToUci()} — awaiting acknowledgement...");
        }
        catch (Exception ex)
        {
            _board = snapshot; _ply = prevPly; _lastMove = prevLast;
            if (_moveHistory.Count > 0) _moveHistory.RemoveAt(_moveHistory.Count - 1);
            _moves.Remove(entry);
            Render();
            await DisplayAlert("Send failed", $"Move could not be sent:\n{ex.Message}\n\nReverted — please try again.", "OK");
        }
        if (!CheckForEnd()) UpdateTurnStatus();
    }

    async void OnResendClicked(object? sender, EventArgs e)
    {
        var p = _pending.Values.FirstOrDefault(m => m.IsMove && m.NeedsResend);
        if (p == null) return;
        p.NeedsResend = false; p.Attempts = 1; p.LastSentUtc = DateTime.UtcNow;
        if (p.Entry != null) p.Entry.TextColor = Palette.Pending;
        Status($"Resending {p.Label}…");
        ApplyConnectionState();
        await ResendPendingAsync(p);
    }

    // ---- Receiving ----
    async Task PollAsync()
    {
        if (_polling || _connecting || _refreshing || _syncing || _mesh == null) return;
        if (!_mesh.TransportConnected) return;   // dead socket — ProbeAsync tears it down
        _polling = true;
        try
        {
            var result = await _mesh.ReceiveAsync();
            _lastPollOkUtc = DateTime.UtcNow; _probeFailures = 0;   // the device answered — definitely alive
            BackgroundPoll.LastForegroundPollUtc = DateTime.UtcNow; // tell the background job the foreground is live
            if (result.PacketCount > 0) { _lastRxUtc = DateTime.UtcNow; _rxStallWarned = false; }
            foreach (var ack in result.Acks) MarkAcked(ack);
            foreach (var msg in result.Texts) Dispatch(msg);
            HandleReceiveExtras(result);
        }
        catch (Exception ex) { Status($"Mesh receive error: {ex.Message}"); }   // slow/failed poll: the TCP probe (ProbeAsync) decides if the link is truly lost
        finally { _polling = false; }
    }

    // Declares the device connection lost after repeated failed TCP probes (e.g. the radio was powered off).
    // Tears the connection down so the button flips back to "Connect", and posts a message asking to reconnect.
    void HandleConnectionLost()
    {
        if (!_connected) return;
        string host = _currentHost;
        var rebuildBle = _reconnectBle;   // Disconnect() leaves this intact so auto-reconnect can rebuild the link
        AddSystem(Stamp() + "— Connection to the device was lost (it may have been turned off, slept, or left the network). —", Palette.Warning, SysCategory.Connection);
        Disconnect("Connection lost.", forgetCache: false);   // keep the cache password so auto-reconnect doesn't re-prompt
        // Auto-reconnect works for WiFi (reconnect to the host) and BLE (rebuild the GATT link).
        bool canReconnect = (_transportIsIp && host.Length > 0) || rebuildBle != null;
        if (AppSettings.AutoReconnect && canReconnect)
        {
            _reconnectBle = rebuildBle;   // Disconnect cleared transport state; keep the BLE rebuild for retries
            StartAutoReconnect(host);
        }
        else
            Status("Connection lost — tap Connect to reconnect.");
    }

    // Auto-reconnect: after the device drops (e.g. the phone slept and the TCP socket closed), retry every
    // ReconnectIntervalSeconds — first attempt immediately — until it reconnects or the user cancels. A 1-second
    // timer counts down between attempts so the user can see when the next try happens (and that the loop is
    // alive, not hung). Off unless enabled.
    public bool IsAutoReconnecting => _autoReconnecting;
    // Seconds until the next attempt while counting down; 0 while an attempt is actually in flight. The Device tab
    // shows this on the "Stop reconnecting" button.
    public int ReconnectCountdown => _autoReconnecting && !_connecting ? _reconnectCountdown : 0;
    public void CancelAutoReconnect() => StopAutoReconnect(reconnected: false);

    void StartAutoReconnect(string host)
    {
        _autoReconnecting = true;
        _autoReconnectHost = host;
        AddSystem(Stamp() + $"— Auto-reconnect on: retrying every {ReconnectIntervalSeconds}s. Disconnect to cancel. —", Palette.Warning, SysCategory.Connection);
        ApplyConnectionState();
        _autoReconnectTimer!.Start();
        _ = TryAutoReconnectAsync();   // first attempt right away; the timer counts down to the next one
    }

    // Fires once a second while auto-reconnecting: counts the seconds down (shown in the status bar and on the
    // button), then launches the next attempt at zero. While an attempt is in flight we don't count down — we
    // show "trying…" so a slow connect doesn't look like a frozen countdown.
    async Task AutoReconnectTickAsync()
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
            Status($"Device offline — next reconnect attempt in {_reconnectCountdown}s. Disconnect to cancel.");
            ApplyConnectionState();   // refresh the button's countdown text
            return;
        }
        await TryAutoReconnectAsync();   // countdown elapsed — try now (resets the countdown for the next round)
    }

    async Task TryAutoReconnectAsync()
    {
        if (!_autoReconnecting || _connecting || _connected || _mesh != null) return;
        _reconnectCountdown = ReconnectIntervalSeconds;   // arm the next countdown before the (slow) attempt
        Status("Auto-reconnect: trying to reach the device…");
        try
        {
            if (_reconnectBle != null) await _reconnectBle();   // BLE: rebuild the GATT link
            else await ConnectAsync(_autoReconnectHost);         // WiFi: reconnect to the host
        }
        catch { /* stays quiet during auto-reconnect; the next tick retries */ }
        if (_connected) StopAutoReconnect(reconnected: true);
    }

    void StopAutoReconnect(bool reconnected)
    {
        if (!_autoReconnecting) return;
        _autoReconnecting = false;
        _autoReconnectTimer!.Stop();
        _reconnectCountdown = 0;
        if (reconnected) AddSystem(Stamp() + "— Reconnected. —", cat: SysCategory.Connection);
        else Status("Auto-reconnect cancelled — tap Connect to reconnect.");
        ApplyConnectionState();
    }


    // TCP reachability probe (runs on the 2.5s probe timer). A bare TCP connect to the device's HTTP port: the
    // ESP32 accepts it from its network task (separate from the busy main loop), so a connect succeeds quickly
    // even when HTTP responses are slow — a failed connect reliably means the device is unreachable. To stay
    // near-free it only probes when a poll hasn't recently succeeded. N consecutive failed connects → lost.
    async Task ProbeAsync()
    {
        if (_probing || _connecting || _refreshing || _syncing || !_connected || _mesh == null) return;
        // A persistent transport (TCP) can have its socket die while the device stays reachable — a fresh TCP
        // probe wouldn't catch that, so check the live link directly. Tear down cleanly (no auto-reconnect loop);
        // the user reconnects with one tap.
        if (!_mesh.TransportConnected) { HandleConnectionLost(); return; }
        // Persistent links (TCP/BLE) report their own health; don't open a competing connection to probe them — the
        // single-client TCP stream port (4403) would refuse it because our live connection holds the slot, a false
        // "lost". The TransportConnected check above is the real test for these; only HTTP needs the probe below.
        if (_mesh.TransportSelfReportsLiveness) return;
        if (!_transportIsIp) return;   // BLE has no IP socket to probe; rely on read/write failures instead
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
    static async Task<bool> IsDeviceReachableAsync(string host, int port, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(host) || port <= 0) return true;   // unknown target — don't declare it lost
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch { return false; }
    }

    /// <summary>Warns once (a red system message + status) that reception may have stalled and points the
    /// user at Update nodes to resync. Re-arms after traffic resumes (or a successful node update).</summary>
    void WarnReceptionStall(string reason)
    {
        if (_rxStallWarned) return;
        _rxStallWarned = true;
        AddSystem(Stamp() + $"— {reason} If messages stop appearing, tap Nodes to update and resync with the device. —", Palette.Warning, SysCategory.Connection);
        Status("Reception may have stalled — tap Nodes to update and resync.");
    }

    /// <summary>Watchdog (runs on the 1s ack timer): warns once if traffic that was flowing goes silent
    /// past the stall timeout (a genuinely quiet channel can trip this; the message is gentle and one-shot).</summary>
    void CheckReceptionStall()
    {
        if (!_connected || !_synced || _syncing || _refreshing || _rxStallWarned) return;
        if (DateTime.UtcNow - _lastRxUtc <= ReceiveStallTimeout) return;
        WarnReceptionStall($"No messages received from the device for {ReceiveStallTimeout.TotalSeconds:0}s.");
    }

    async Task RunSyncAsync()
    {
        if (_mesh == null) return;
        _syncing = true;
        int total = 0;
        const int chunk = 25;
        SetSyncStatus("Syncing with the mesh…");
        bool syncOk = true;
        try
        {
            while (_mesh != null)
            {
                var result = await _mesh.ReceiveAsync(chunk);
                foreach (var ack in result.Acks) MarkAcked(ack);
                foreach (var msg in result.Texts) Dispatch(msg);
                total += result.PacketCount;
                SetSyncStatus($"Syncing with the mesh… {total} packet{(total == 1 ? "" : "s")} drained");
                if (result.PacketCount < chunk) break;
            }
        }
        catch (Exception ex) { syncOk = false; SetSyncStatus($"Sync error: {ex.Message}"); }

        if (syncOk) SetSyncStatus($"Synced — {total} packet{(total == 1 ? "" : "s")} drained.");
        _syncing = false; _synced = true;
        var now = DateTime.UtcNow;
        foreach (var p in _pending.Values) p.LastSentUtc = now;
        _lastRxUtc = now; _rxStallWarned = false;   // reception is live now — arm the stall watchdog
        if (_mesh != null)
        {
            // Channels can finish streaming in during the sync (after the initial handshake), so the saved chess
            // channel may have been missing when LoadChannelPrefs first ran. Re-apply it now that the full list
            // is known — otherwise create/join would have no valid chess channel.
            try
            {
                if (!_playing)
                {
                    var available = _mesh.GetAvailableChannels().Select(c => c.Index).ToHashSet();
                    if (DeviceCache.GetChannelPrefs(_currentHost)?.ChessChannel is uint savedChess
                        && savedChess != 0 && available.Contains(savedChess) && _mesh.ChannelIndex != savedChess)
                    {
                        _mesh.ChannelIndex = savedChess;
                        Status($"Chess channel restored to [{savedChess}].");
                    }
                }
            }
            catch { /* non-fatal: keep the connection even if the channel re-check fails */ }
            DeviceCache.Save(_currentHost, _mesh.GetAvailableChannels(), _mesh.MyNodeNum, _mesh.GetNodeNameMap(), _mesh.GetNodeRoleMap(), _mesh.GetNodeHwMap(), _mesh.GetNodeShortNameMap(), _mesh.GetNodeFavoriteMap(), _mesh.GetNodeIgnoredMap(), _mesh.GetNodeHopsAwayMap(), _mesh.GetNodeLastHeardMap());
            RefreshChatAckerNames();
            SyncDeviceClockIfAhead();   // correct a radio whose clock is set in the future (bad "last heard" stamps)
            // On a proxy link, ask it to replay any received messages we missed while away (newer than our newest
            // cached one). Suppress acks briefly so we don't re-ack the replayed (old) burst back onto the mesh.
            if (_isProxy)
            {
                _suppressAcksUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                try { await _mesh.SendProxyBackfillRequestAsync(MaxCachedRxTime(_currentHost)); } catch { }
            }
        }
        ApplyConnectionState();
        if (_playing) UpdateTurnStatus();
        else Status($"Ready ({total} packets synced). Create or Join a game, or chat.");
        _pollTimer!.Start();
    }

    void Dispatch(MeshTextMessage msg)
    {
        if (_mesh == null) return;
        bool onChess = msg.Channel == _mesh.ChannelIndex;
        bool onChat = IsReceiveChannel(msg.Channel);   // chat is received on ALL enabled channels; the RX filter chooses what shows
        bool wasDm = msg.IsDmTo(_mesh.MyNodeNum);   // a direct message addressed to us specifically
        // A DM that OUR node sent to another node, seen by the other apps sharing this node via the proxy. Show it
        // in that peer's DM thread as an outgoing message, so every connected app sees the same DM conversation.
        bool sentDmFromUs = msg.FromNode == _mesh.MyNodeNum && msg.IsDirectMessage && msg.ToNode != _mesh.MyNodeNum;
        if (wasDm && _nodePrefs.GetValueOrDefault(msg.FromNode)?.Block == true) return;   // blocked node — ignore its DMs
        // An emoji reaction (tapback): attach it to the target message instead of showing it as its own chat line.
        if (msg.IsReaction)
        {
            if (msg.ReplyId != 0 && (onChat || wasDm || sentDmFromUs)) AddReaction(msg.ReplyId, msg.Text, msg.FromNode);
            return;
        }
        if (!onChess && !onChat && !wasDm && !sentDmFromUs) return;

        _incomingRxTime = msg.RxTime;
        try
        {
            if (ProtocolMessage.TryParse(msg.Text, out var pm))
            {
                if (pm.Kind == MessageKind.ChatAck) { if (onChat) RegisterChatAck(pm.ChatPacketId, msg.FromNode, pm.AckSignal, AckSignalText(msg)); }
                else if (!onChess) { }
                else if (pm.Kind == MessageKind.Create) HandleGameAnnounced(pm, msg);
                else if (pm.Kind == MessageKind.CreateAck) HandleCreateAck(pm, msg);
                else if (pm.Kind == MessageKind.Join) HandleGameJoined(pm, msg);
                else if (pm.Kind == MessageKind.Board) HandleBoard(pm);
                else if (pm.Kind == MessageKind.ResignAck) HandleResignAck(pm);
                else if (pm.Kind == MessageKind.Resign) HandleResign(pm);
                else if (pm.Kind == MessageKind.Save) HandleSaveRequest(pm, msg);
                else if (pm.Kind == MessageKind.SaveAck) HandleSaveAck(pm);
                else if (pm.Kind == MessageKind.Cancel) HandleCancel(pm);
                else if (pm.Kind == MessageKind.Ended) HandleEnded(pm);
                else if (pm.Kind == MessageKind.Ack) HandleMoveAck(pm);
                else if (pm.GameId == _gameId && _playing && !_gameOver) ApplyIncoming(pm);
                else if (pm.Kind == MessageKind.Move && pm.GameId == _gameId)
                    SendControl(ProtocolMessage.EncodeEnded(pm.GameId), encrypt: false);
            }
            else if (onChat || wasDm || sentDmFromUs)
            {
                bool isDm = wasDm || sentDmFromUs;
                uint dmPeer = sentDmFromUs ? msg.ToNode : msg.FromNode;   // the conversation peer (recipient for outgoing)
                string who = _mesh?.DescribeNode(dmPeer) ?? dmPeer.ToString();
                string sig = _showSignal ? SignalTag(msg) : "";
                // A DM shows a "DM ←"/"DM →" tag; a channel message shows which channel it arrived on.
                string chan = isDm ? "" : $"{ChannelLabel(msg.Channel)} ";
                // A resent chat carries a marker prefix; strip it and note "resent" in the metadata instead.
                string body = msg.Text;
                // Sender self-destruct header (if any): peel off the chosen lifetime and honour it — the message
                // deletes itself (screen + cache) once it's this old, counted from when the radio received it.
                int ttlSeconds = 0;
                if (ProtocolMessage.TryDecodeChatTtl(body, out var ttlS, out var strippedBody)) { ttlSeconds = ttlS; body = strippedBody; }
                bool resent = body.StartsWith(ProtocolMessage.ChatResendPrefix, StringComparison.Ordinal);
                if (resent) body = body.Substring(ProtocolMessage.ChatResendPrefix.Length);
                string detail = $"{Stamp()}{chan}{sig}".Trim();   // dim metadata line under the message
                if (resent) detail = detail.Length > 0 ? detail + "  · resent" : "resent";
                // Note when the device got this off MQTT rather than over the air (shown regardless of the signal toggle).
                if (msg.ViaMqtt) detail = detail.Length > 0 ? detail + "  · via MQTT" : "via MQTT";
                // If this is a reply, quote what it answers so it's clear it's replying to (one of) your messages.
                string replyRef = ReplyRefFor(msg.ReplyId);
                if (replyRef.Length > 0) detail = detail.Length > 0 ? $"{replyRef}  ·  {detail}" : replyRef;
                string dmTag = sentDmFromUs ? "DM → " : (wasDm ? "DM ← " : "");
                // The part after the "<name>: " prefix; stored on the row so it can be re-rendered with the real
                // name once this node's info arrives (an unknown sender first shows as "!hex").
                string bodyPart = msg.DecryptFailed ? $"{msg.Text}  ⚠ decryption failed (wrong/missing key)" : body;
                LogEntry entry = AddChatLine($"{dmTag}{who}: {bodyPart}", detail, msg.DecryptFailed ? Palette.Warning : Palette.Normal, msg);
                entry.ChatNameBody = sentDmFromUs ? null : bodyPart;   // outgoing DMs aren't re-rendered (the prefix differs)
                entry.Channel = msg.Channel;
                // Honour the sender's self-destruct: expiry is counted from the radio's receive time (falls back to
                // now), so an old backfilled message already past its lifetime is dropped on the next sweep.
                DateTime rxLocal = msg.RxTime != 0 ? DateTimeOffset.FromUnixTimeSeconds(msg.RxTime).LocalDateTime : DateTime.Now;
                DateTime expiresAt = (ttlSeconds > 0 && !msg.DecryptFailed) ? rxLocal.AddSeconds(ttlSeconds) : default;
                if (expiresAt != default)
                {
                    entry.ExpiresAt = expiresAt;
                    entry.Expiry = "🕓 " + ExpiryCountdown(expiresAt - DateTime.Now);
                }
                TrimChatChannel(msg.Channel);   // cap on-screen rows per channel
                if (!msg.DecryptFailed) entry.CacheId = CacheChat(msg.Channel, entry.Text, entry.Detail, msg.RxTime, expiresAt);   // persist (latest N per channel)
                // Apply the RX filter: hide the row if its channel/DM is hidden, and count it as unread there
                // instead of notifying (so a hidden conversation just shows a badge in the RX list).
                bool shown = RouteRx(entry, isDm, dmPeer, incoming: !sentDmFromUs);
                if (shown && !sentDmFromUs)   // an outgoing DM mirrored from a peer app isn't a new incoming alert
                {
                    Notify();
                    if (DateTime.UtcNow >= _suppressAcksUntil)   // stay silent during the proxy backfill burst after connect
                    {
                        NotifyBackground(wasDm ? $"DM from {who}" : who, msg.DecryptFailed ? "⚠ message (decryption failed)" : body);
                        PlayChatSound();
                        (Shell.Current as AppShell)?.FlashChatTab();   // draw attention to the Chat tab if it's not open
                    }
                }
                // A reply on the channel we just sent to confirms our in-flight chat (turns it green, frees Send).
                ConfirmChatByIncomingReply(msg.Channel);
                // A DM from a node we hadn't DM-enabled flips its DM flag on (and lists it as a TX target) so we can reply.
                if (wasDm) EnsureDmEnabled(msg.FromNode);
                else if (sentDmFromUs) EnsureDmEnabled(msg.ToNode);   // list the peer so the shared DM thread is navigable
                // Channel chat acks are per-channel (default off); a DM is routing-acked by the firmware, so we don't
                // chat-ack it. A keyword match forces an RSSI ack even when the channel's acks are off (range-test pings).
                // Never ack a message sent by our OWN node: through Meshtastic.Proxy several apps share this node id,
                // so a peer client's message arrives as "from us" — only one of us (the sender) should be acked, and
                // a node acking itself is pointless airtime.
                bool fromUs = _mesh != null && msg.FromNode == _mesh.MyNodeNum;
                // Don't ack replayed history: the initial post-connect backlog drain (!_synced) and the proxy
                // backfill burst are both old messages — acking them would flood the mesh. Only live messages
                // (heard after sync) get an ack.
                bool ackSuppressed = !_synced || DateTime.UtcNow < _suppressAcksUntil;
                bool keywordAck = !wasDm && !fromUs && !ackSuppressed && MatchesAckTrigger(msg.Channel, msg.Text);
                if (!wasDm && !fromUs && !ackSuppressed && (_chatAckOn.Contains(msg.Channel) || keywordAck))
                {
                    string ackSignal = (keywordAck || _chatAckSignalOn.Contains(msg.Channel)) ? AckSignalText(msg) : "";
                    SendChatAck(msg.PacketId, msg.Channel, ackSignal);
                    if (keywordAck)   // log keyword-triggered acks so range-test pings are visible even with channel acks off
                        AddSystem(Stamp() + $"Keyword auto-ack sent to {_mesh?.DescribeNode(msg.FromNode)} on channel {msg.Channel}" +
                            (ackSignal.Length > 0 ? $" — heard {ackSignal}" : "") + ".", cat: SysCategory.Warnings);
                    _chatSendAllowedUtc = DateTime.UtcNow + ChatSendDelay;
                    if (_chatHoldTimer != null) { _chatHoldTimer.Stop(); _chatHoldTimer.Interval = ChatSendDelay + TimeSpan.FromMilliseconds(200); _chatHoldTimer.Start(); }
                }
                ApplyConnectionState();
            }
        }
        finally { _incomingRxTime = 0; }
    }

    string SignalTag(MeshTextMessage msg)
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
        if (msg.RxRssi != 0 || msg.RxSnr != 0)
            parts.Add($"RSSI {msg.RxRssi} dBm · SNR {msg.RxSnr:0.#} dB");
        return parts.Count > 0 ? "   [" + string.Join(" · ", parts) + "]" : "";
    }

    void HandleMoveAck(ProtocolMessage pm)
    {
        if (pm.GameId != _gameId) return;
        var movePending = _pending.Values.FirstOrDefault(p => p.IsMove && p.Ply == pm.Ply);
        if (movePending == null) return;
        _pending.Remove(movePending.Id);
        if (movePending.Entry != null) { movePending.Entry.Text = movePending.BaseText + MarkDelivered; movePending.Entry.TextColor = Palette.Acked; }
        Status($"Opponent acknowledged {movePending.Label}.");
        ApplyConnectionState();
    }

    void ApplyIncoming(ProtocolMessage pm)
    {
        if (pm.Kind != MessageKind.Move) return;
        if (pm.Ply <= _ply) { SendMoveAck(pm.Ply); return; }
        if (pm.Ply != _ply + 1) { Status($"Out-of-order move (ply {pm.Ply}, expected {_ply + 1})."); return; }
        if (_board.SideToMove == _myColor) return;
        var mover = _board.SideToMove;
        var legal = _board.FindLegalMove(pm.Move);
        if (legal == null) { Status($"Received illegal move {pm.Move.ToUci()} — boards may be out of sync."); return; }

        ConfirmPendingMove();
        if (_awaitingOpponent) { _awaitingOpponent = false; ApplyConnectionState(); }
        _board.MakeMove(legal.Value);
        _ply++;
        _moveHistory.Add(legal.Value.ToUci());
        _lastMove = (legal.Value.From, legal.Value.To);
        AddMove(Stamp() + MoveLine(_ply, mover, legal.Value));
        Render();
        Notify();
        NotifyBackground("Your opponent moved", $"{mover}: {legal.Value.ToUci()}");
        PlayChessSound();
        SendMoveAck(_ply);
        _moveSendAllowedUtc = DateTime.UtcNow + MoveSendDelay;
        if (!CheckForEnd()) UpdateTurnStatus();
    }

    async void SendMoveAck(int ply)
    {
        if (_mesh == null) return;
        await AckJitterDelay();
        try { await _mesh.SendTextAsync(ProtocolMessage.EncodeAck(_gameId, ply), encrypt: false); } catch { }
    }

    // ---- Chat ----
    // One chat in flight at a time — the next send waits until this one is confirmed (an explicit ack, an
    // overheard relay, or a reply on the channel) or times out. This applies to relay-confirm (ack-off) chats
    // too, so we don't flood the mesh faster than it can forward.
    bool ChatInFlight => _pending.Values.Any(p => p.IsChat);

    /// <summary>Seconds until the chat Send button unblocks — while a message is in flight (waiting for an
    /// ack/relay/reply, or for the wait to give up) or during the brief post-receive ack hold. 0 when sending is
    /// allowed. The Chat tab shows this as a live countdown on the Send button; a relay/ack/reply removes the
    /// pending and zeroes it early.</summary>
    public int ChatSendCountdown
    {
        get
        {
            if (!_connected || !_synced) return 0;
            var now = DateTime.UtcNow;
            var chat = _pending.Values.FirstOrDefault(p => p.IsChat);
            if (chat == null && now >= _chatSendAllowedUtc) return 0;   // not blocked
            DateTime blockedUntil = _chatSendAllowedUtc;
            if (chat != null && chat.SendDeadlineUtc > blockedUntil) blockedUntil = chat.SendDeadlineUtc;
            int secs = (int)Math.Ceiling((blockedUntil - now).TotalSeconds);
            return secs > 0 ? secs : 0;
        }
    }

    bool _chatCountdownActive;   // true on the previous tick — so we fire one final refresh when it unblocks

    // Once a second while the Send button is counting down (in-flight or post-receive hold), nudge the UI so the
    // Chat tab's button text ticks down. We also fire once more on the tick it unblocks, to reset it to "Send".
    void TickChatSendCountdown()
    {
        bool blocked = _connected && (ChatInFlight || DateTime.UtcNow < _chatSendAllowedUtc);
        if (blocked || _chatCountdownActive) StateChanged?.Invoke();
        _chatCountdownActive = blocked;
    }

    /// <summary>Sends a chat message on the current TX channel. Returns "" when accepted (so the caller clears
    /// its input), or a human-readable reason when blocked (in-flight / cooldown / over-length) so the Chat tab
    /// can show it — the Chess-tab status line is invisible while the user is chatting.</summary>
    async Task<string> SendChatMessageAsync(string raw)
    {
        if (_mesh == null) return "Not connected to a device.";
        if (!_synced) return "Still syncing with the mesh — you can send once sync completes.";
        string text = raw.Trim();
        if (text.Length == 0) return "Type a message first.";
        if (ChatInFlight) return "Waiting for your last message to be confirmed (relayed or acknowledged) before sending another.";

        var wait = _chatSendAllowedUtc - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            return $"Just acknowledged an incoming message — you can send in {Math.Ceiling(wait.TotalSeconds):0}s.";

        // Sender self-destruct: if this channel has a send-TTL set, ride the chosen lifetime (seconds) in the wire
        // text so every receiver — and our own copy — deletes it after that. The displayed/cached text stays clean.
        int ttlSeconds = (_currentHost.Length > 0 ? DeviceCache.GetChannelSendTtl(_currentHost).GetValueOrDefault(_chatTxChannel) : 0) * 60;
        string wireText = ProtocolMessage.EncodeChatTtl(ttlSeconds, text);

        string key = _mesh.GetChannelKey(_chatTxChannel);
        bool encrypted = key.Length > 0;
        int wireLen = (encrypted ? AesText.Encrypt(wireText, key) : wireText).Length;
        if (wireLen > MaxChatChars)
            return $"Message is too long: {wireLen}/{MaxChatChars} chars{(encrypted ? " (once encrypted)" : "")}.";

        // Direct message vs. channel broadcast: a DM goes to the selected node (stamped with the current chat
        // channel for the non-PKI fallback); a broadcast goes to the whole channel.
        bool isDm = _chatTxDest.HasValue;
        string dmName = isDm ? (_mesh.DescribeNode(_chatTxDest!.Value)) : "";
        // Always show which channel we transmitted on (and flag app-level encryption, which a peer without the
        // same key can't read — a common reason a sent message never appears for the other side); for a DM show
        // the recipient instead.
        string chan = isDm ? $"DM → {dmName} " : $"{ChannelLabel(_chatTxChannel)}{(encrypted ? " 🔒" : "")} ";
        string msgLine = isDm ? $"You → {dmName}: {text}" : $"You: {text}";   // the prominent message line
        string detailBase = $"{Stamp()}{chan}".Trim();   // dim metadata line (timestamp + channel); marks append here
        // If replying, link this send to that message and show the quoted reference in the metadata.
        uint replyId = _replyToId;
        string replyRef = ReplyRef();
        if (replyRef.Length > 0) detailBase = $"{replyRef}  ·  {detailBase}".Trim(' ', '·');
        ClearReply();
        // A DM is confirmed by the recipient relaying/replying (the firmware also routing-acks it); an ack-off
        // channel broadcast is confirmed by overhearing a relay. Either way we hold "in flight" until confirmed.
        bool relayConfirm = isDm || !_chatAckOn.Contains(_chatTxChannel);
        var entry = AddChatLine(msgLine, detailBase + MarkSending, Palette.Pending);
        // Start our own copy's self-destruct countdown immediately (before the ack) so it's visible right away.
        DateTime expiresAt = ttlSeconds > 0 ? DateTime.Now.AddSeconds(ttlSeconds) : default;
        if (ttlSeconds > 0)
        {
            entry.ExpiresAt = expiresAt;
            entry.Expiry = "🕓 " + ExpiryCountdown(TimeSpan.FromSeconds(ttlSeconds));
        }
        try
        {
            uint id = await _mesh.SendTextAsync(wireText, _chatTxChannel, destination: _chatTxDest, replyId: replyId);
            entry.PacketId = id;          // so this sent message can itself be replied to / reacted to
            entry.Channel = _chatTxChannel;
            TrimChatChannel(_chatTxChannel);   // cap on-screen rows per channel
            RouteRx(entry, isDm, isDm ? _chatTxDest!.Value : 0, incoming: false);   // hide if its target is filtered out
            entry.CacheId = CacheChat(_chatTxChannel, msgLine, detailBase, 0, expiresAt);   // persist (latest 100 per channel)
            _chatEntryById[id] = entry;   // so reactions can attach to this row
            _pending[id] = new PendingSend { Id = id, Payload = text, LastSentUtc = DateTime.UtcNow, Attempts = 1, IsChat = true, IsRelayConfirm = relayConfirm, IsDm = isDm, Channel = _chatTxChannel, Label = isDm ? $"direct message to {dmName}" : "chat message", Entry = entry, BaseText = detailBase,
                // Latest moment we'll still be waiting if nothing is heard (MaxSendAttempts windows of AckTimeout
                // each). The Send button counts down to this; a relay/ack/reply removes the pending earlier and
                // cancels the countdown.
                SendDeadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(MaxSendAttempts * AckTimeout.TotalSeconds) };
            // Track acks for every channel broadcast (not DMs): even when WE don't send acks on this channel, the
            // recipient may still ack us (their ack setting, or our keyword auto-ack), and we should show "acked" —
            // with RSSI when the ack carries it — rather than dropping it.
            if (!isDm) _chatAckers[id] = new ChatAckInfo { Entry = entry, BaseText = detailBase };
            ApplyConnectionState();
        }
        catch (Exception ex)
        {
            // Surface the reason on the chat line itself — the status line lives on the Chess tab.
            entry.Detail = detailBase + MarkFailed + $"  — not sent: {ex.Message}";
            entry.TextColor = Palette.Warning;
        }
        return "";   // accepted (the chat line shows its own delivery state)
    }

    // Sets the TX channel (used by the dedicated Chat page's picker), persisting it and syncing the inline picker.
    void SetChatTxChannel(uint channel)
    {
        _chatTxChannel = channel;
        if (_mesh != null && _currentHost.Length > 0)
            DeviceCache.SaveChannelPrefs(_currentHost, _mesh.ChannelIndex, _chatListen, _chatTxChannel);
        RebuildChatTxPicker();
    }

    /// <summary>A chat TX target: a channel broadcast (IsDm=false, Id=channel index) or a direct message
    /// (IsDm=true, Id=node number).</summary>
    public sealed record ChatTxTarget(bool IsDm, uint Id, string Label);

    // ---- Shared chat state, exposed for the dedicated Chat tab (which shares this page's live state) ----
    public ObservableCollection<LogEntry> ChatLog => _chat;
    /// <summary>Returns "" if the message was accepted, or a reason string if it was blocked (to show the user).</summary>
    public Task<string> SendChatAsync(string text) => SendChatMessageAsync(text);

    /// <summary>The chat TX targets: the listened channels (broadcasts) followed by any DM-enabled, non-blocked
    /// nodes (direct messages).</summary>
    public IReadOnlyList<ChatTxTarget> ChatTxTargets()
    {
        var names = _mesh?.GetAvailableChannels().ToDictionary(c => c.Index, c => c.DisplayName) ?? new();
        // Flag channels with an app encryption key with the same 🔒 shown on sent chat lines (see SendChat).
        var list = _chatListen.OrderBy(i => i)
            .Select(i => new ChatTxTarget(false, i,
                $"[{i}] {(names.TryGetValue(i, out var n) ? n : "")}".TrimEnd()
                + ((_mesh?.GetChannelKey(i).Length ?? 0) > 0 ? " 🔒" : "")))
            .ToList();
        if (_mesh != null)
        {
            var nodes = _mesh.GetNodes().Where(n => !n.IsSelf).ToDictionary(n => n.Num, n => n);
            // Only DMs currently shown in the RX dropdown are TX targets (you can send to what you receive).
            foreach (var num in _nodePrefs.Where(kv => kv.Value.Dm && !kv.Value.Block && !_rxHidden.Contains((true, kv.Key)))
                                          .Select(kv => kv.Key).OrderBy(n => n))
            {
                string label = nodes.TryGetValue(num, out var nd) ? NodeShortLabel(nd) : $"!{num:x8}";
                list.Add(new ChatTxTarget(true, num, $"DM → {label}"));
            }
        }
        return list;
    }

    // Every enabled device channel index (those in channel settings). Chat is received and shown on all of these.
    List<uint> ReceiveChannels() =>
        _mesh?.GetAvailableChannels().Where(c => !c.IsDisabled).Select(c => c.Index).OrderBy(i => i).ToList() ?? new List<uint>();
    bool IsReceiveChannel(uint channel) => _mesh?.GetAvailableChannels().Any(c => !c.IsDisabled && c.Index == channel) ?? false;

    /// <summary>The RX (view-filter) targets: ALL enabled device channels followed by the DM peers — independent
    /// of the TX channel set, so you can show/hide any channel the device has.</summary>
    public IReadOnlyList<ChatTxTarget> RxTargets()
    {
        var chans = _mesh?.GetAvailableChannels().Where(c => !c.IsDisabled).OrderBy(c => c.Index).ToList() ?? new();
        var list = chans.Select(c => new ChatTxTarget(false, c.Index, $"[{c.Index}] {c.DisplayName}".TrimEnd())).ToList();
        if (_mesh != null)
        {
            var nodes = _mesh.GetNodes().Where(n => !n.IsSelf).ToDictionary(n => n.Num, n => n);
            foreach (var num in _nodePrefs.Where(kv => kv.Value.Dm && !kv.Value.Block).Select(kv => kv.Key).OrderBy(n => n))
            {
                string label = nodes.TryGetValue(num, out var nd) ? NodeShortLabel(nd) : $"!{num:x8}";
                list.Add(new ChatTxTarget(true, num, $"DM → {label}"));
            }
        }
        return list;
    }

    // ---- RX view filter (which channels/DMs are shown in chat) ----
    static (bool IsDm, uint Id) RxKey(LogEntry e) => e.DmPeer != 0 ? (true, e.DmPeer) : (false, e.Channel);

    // Whether an RX target is shown (and so a valid TX target): a channel is shown when it's in the selected set;
    // a DM is shown unless it's been hidden in the RX dropdown.
    bool IsRxVisible(bool isDm, uint id) => isDm ? !_rxHidden.Contains((true, id)) : _chatListen.Contains(id);

    // Persists the current channel selection (and TX channel) for this device.
    void SaveChatSelection()
    {
        if (_currentHost.Length > 0 && _mesh != null)
            DeviceCache.SaveChannelPrefs(_currentHost, _mesh.ChannelIndex, _chatListen, _chatTxChannel);
    }

    // Stamps a chat row's RX target, sets whether it's shown (per the filter), and — for an incoming message on a
    // hidden target — bumps that target's unread count. Returns whether the row is shown.
    bool RouteRx(LogEntry e, bool isDm, uint peer, bool incoming)
    {
        e.DmPeer = isDm ? peer : 0;
        var key = isDm ? (true, peer) : (false, e.Channel);
        bool shown = IsRxVisible(key.Item1, key.Item2);
        e.Visible = shown;
        if (incoming && !shown) { _unread[key] = _unread.GetValueOrDefault(key) + 1; StateChanged?.Invoke(); }
        return shown;
    }

    void RecomputeChatVisibility()
    {
        foreach (var e in _chat)
            if (e.Channel != uint.MaxValue) { var k = RxKey(e); e.Visible = IsRxVisible(k.IsDm, k.Id); }   // dividers always show
    }

    public bool IsRxHidden(bool isDm, uint id) => !IsRxVisible(isDm, id);
    public int RxUnread(bool isDm, uint id) => _unread.GetValueOrDefault((isDm, id));
    public int TotalUnread => _unread.Values.Sum();

    // Show/hide an RX target. A channel toggles the selected-channel set (shown in chat AND a TX target, persisted);
    // a DM toggles the runtime hidden set. Either way the TX target list is refreshed.
    public void SetRxHidden(bool isDm, uint id, bool hidden)
    {
        if (isDm)
        {
            if (hidden) _rxHidden.Add((true, id));
            else { _rxHidden.Remove((true, id)); _unread.Remove((true, id)); }
        }
        else
        {
            if (hidden) { _chatListen.Remove(id); _unread.Remove((false, id)); }
            else _chatListen.Add(id);
            if (!_chatListen.Contains(_chatTxChannel)) _chatTxChannel = _chatListen.FirstOrDefault();
            SaveChatSelection();
        }
        RecomputeChatVisibility();
        RebuildChatTxPicker();
        StateChanged?.Invoke();
    }

    public void ShowAllRx()
    {
        _chatListen.Clear();
        foreach (var ch in ReceiveChannels()) _chatListen.Add(ch);
        _rxHidden.Clear(); _unread.Clear();
        SaveChatSelection(); RecomputeChatVisibility(); RebuildChatTxPicker(); StateChanged?.Invoke();
    }

    public void HideAllRx()
    {
        _chatListen.Clear();
        foreach (var t in RxTargets().Where(t => t.IsDm)) _rxHidden.Add((true, t.Id));
        SaveChatSelection(); RecomputeChatVisibility(); RebuildChatTxPicker(); StateChanged?.Invoke();
    }

    /// <summary>Deletes all chat messages for one RX target (a channel or a DM peer) — both the rows shown in chat
    /// and the cached history for this device.</summary>
    public void DeleteChatTarget(bool isDm, uint id)
    {
        // A channel's whole cached bucket can be wiped in one go; DM rows live mixed in their arrival channel's
        // bucket, so those are removed one at a time by their cached id.
        if (!isDm && _currentHost.Length > 0) DeviceCache.ClearChat(_currentHost, id);

        for (int i = _chat.Count - 1; i >= 0; i--)
        {
            var e = _chat[i];
            if (e.Channel == uint.MaxValue) continue;   // keep the saved-history divider
            bool match = isDm ? e.DmPeer == id : (e.DmPeer == 0 && e.Channel == id);
            if (!match) continue;
            if (isDm && _currentHost.Length > 0) DeviceCache.RemoveChat(_currentHost, e.Channel, e.CacheId, e.Text, e.Detail);
            if (e.PacketId != 0) { _chatEntryById.Remove(e.PacketId); _pending.Remove(e.PacketId); _chatAckers.Remove(e.PacketId); _overheardAcks.Remove(e.PacketId); }
            _chat.RemoveAt(i);
        }
        _unread.Remove((isDm, id));
        // Don't leave a lone "saved history above / live below" divider behind.
        if (_chat.Count == 1 && _chat[0].Channel == uint.MaxValue) _chat.Clear();
        StateChanged?.Invoke();
    }

    /// <summary>The currently-selected TX target (a DM destination if one is set, else the broadcast channel).</summary>
    public ChatTxTarget CurrentChatTxTarget()
    {
        var targets = ChatTxTargets();
        if (_chatTxDest is uint dest && targets.FirstOrDefault(t => t.IsDm && t.Id == dest) is { } dm) return dm;
        return targets.FirstOrDefault(t => !t.IsDm && t.Id == _chatTxChannel)
               ?? targets.FirstOrDefault(t => !t.IsDm)
               ?? new ChatTxTarget(false, _chatTxChannel, "");
    }

    /// <summary>Selects a chat TX target — a DM destination or a broadcast channel.</summary>
    public void SetChatTx(ChatTxTarget t)
    {
        if (t.IsDm) { _chatTxDest = t.Id; ApplyConnectionState(); }
        else { _chatTxDest = null; SetChatTxChannel(t.Id); }
    }

    /// <summary>A compact name for a node in the TX dropdown: short name, else long name, else !hex.</summary>
    static string NodeShortLabel(MeshNode n) =>
        !string.IsNullOrWhiteSpace(n.ShortName) ? n.ShortName
        : !string.IsNullOrWhiteSpace(n.LongName) ? n.LongName
        : $"!{n.Num:x8}";

    /// <summary>Turns on the DM flag for a node (so it appears as a TX target and we can reply) unless it's
    /// blocked or already enabled. Called when a DM arrives from a node we hadn't DM-enabled yet.</summary>
    void EnsureDmEnabled(uint nodeNum)
    {
        var pref = _nodePrefs.GetValueOrDefault(nodeNum);
        if (pref is { Block: true } || pref is { Dm: true }) return;   // blocked, or already enabled
        SetNodePref(nodeNum, dm: true, block: false);
    }

    // ---- Nodes list (the dedicated Nodes page shares this page's live node state) ----
    public IReadOnlyList<MeshNode> GetNodes() => _mesh?.GetNodes() ?? (IReadOnlyList<MeshNode>)Array.Empty<MeshNode>();
    public IReadOnlyList<MeshNodePosition> GetNodePositions() => _mesh?.GetNodePositions() ?? (IReadOnlyList<MeshNodePosition>)Array.Empty<MeshNodePosition>();
    /// <summary>node num → recent position track (oldest first) for the map's right-click "recent positions" view.</summary>
    public IReadOnlyDictionary<uint, List<(double Lat, double Lon, long LastHeard, long PosTime)>> GetPositionHistoryMap() =>
        _mesh?.GetPositionHistoryMap() ?? new Dictionary<uint, List<(double Lat, double Lon, long LastHeard, long PosTime)>>();
    /// <summary>A Google Maps URL for a node's last known location, or null if we have no fix for it yet.</summary>
    public string? NodeMapsUrl(uint num) =>
        _mesh?.GetNodePosition(num) is { } p ? MeshtasticHttpClient.GoogleMapsUrl(p.Latitude, p.Longitude) : null;
    public uint MyNodeNum => _mesh?.MyNodeNum ?? 0;
    /// <summary>This device's own hardware model (e.g. "Heltec V3"), or null if unknown.</summary>
    public string? OwnHardwareModel => _mesh?.HardwareModel;
    /// <summary>Node numbers we hold a DM/Block pref for (used to surface nodes not in the device DB).</summary>
    public IReadOnlyCollection<uint> NodePrefNums => _nodePrefs.Keys.ToList();
    /// <summary>The DM/Block flags for a node.</summary>
    public (bool Dm, bool Block) NodePrefFor(uint num)
    {
        var p = _nodePrefs.GetValueOrDefault(num);
        return (p?.Dm ?? false, p?.Block ?? false);
    }

    /// <summary>Sets a node's DM/Block flags, persists them, refreshes the chat TX targets, and notifies any
    /// open Nodes page. Block wins over DM.</summary>
    public void SetNodePref(uint num, bool dm, bool block)
    {
        if (block) dm = false;
        if (!dm && !block) _nodePrefs.Remove(num);
        else _nodePrefs[num] = new DeviceCache.NodePrefs { Dm = dm, Block = block };
        if (_currentHost.Length > 0) DeviceCache.SetNodePref(_currentHost, num, dm, block);
        RebuildChatTxPicker();
        NodesChanged?.Invoke();
    }

    /// <summary>Removes a node from the device's NodeDB (admin remove_by_nodenum) and forgets it locally. Recovers
    /// DMs after the node reinstalled its firmware (stale PKI public key): the device re-learns the new key when it
    /// next hears the node. Refreshes the Nodes page + chat TX targets.</summary>
    public async Task RemoveNodeAsync(uint num)
    {
        if (_mesh == null) return;
        await _mesh.RemoveNodeAsync(num);
        if (_currentHost.Length > 0) DeviceCache.RemoveNode(_currentHost, num);
        _nodePrefs.Remove(num);
        RebuildChatTxPicker();
        NodesChanged?.Invoke();
    }

    public async Task SetFavoriteNodeAsync(uint num, bool favorite)
    {
        if (_mesh == null) return;
        await _mesh.SetFavoriteNodeAsync(num, favorite);
        SaveNodeCache();
        NodesChanged?.Invoke();
    }

    public async Task SetIgnoredNodeAsync(uint num, bool ignored)
    {
        if (_mesh == null) return;
        await _mesh.SetIgnoredNodeAsync(num, ignored);
        SaveNodeCache();
        NodesChanged?.Invoke();
    }

    /// <summary>Runs a remote-admin action ("reboot"/"shutdown"/"nodedb"/"factory") against another node.
    /// Returns null on success or an error string.</summary>
    public Task<string?> RemoteAdminAsync(uint target, string action) => action switch
    {
        "reboot" => _mesh?.RemoteRebootAsync(target) ?? Task.FromResult<string?>("Not connected."),
        "shutdown" => _mesh?.RemoteShutdownAsync(target) ?? Task.FromResult<string?>("Not connected."),
        "nodedb" => _mesh?.RemoteNodeDbResetAsync(target) ?? Task.FromResult<string?>("Not connected."),
        "factory" => _mesh?.RemoteFactoryResetAsync(target) ?? Task.FromResult<string?>("Not connected."),
        _ => Task.FromResult<string?>("Unknown action."),
    };

    public Task<uint> RequestDeviceMetricsForAsync(uint num) => _mesh?.RequestDeviceMetricsAsync(num) ?? Task.FromResult(0u);

    /// <summary>Logs an admin message (sent/received) to system messages, tagged Admin. Marshals to the main
    /// thread since the mesh client may raise this off the poll thread.</summary>
    void OnAdminActivity(string text) => MainThread.BeginInvokeOnMainThread(() => AddSystem(Stamp() + text, cat: SysCategory.Admin));

    /// <summary>Logs an incoming request another node made of us (position/telemetry/noise-floor), tagged Requests.</summary>
    void OnIncomingRequest(string text) => MainThread.BeginInvokeOnMainThread(() => AddSystem(Stamp() + text, cat: SysCategory.Requests));

    /// <summary>Persists the node caches (names/short/role/hw/favorite/ignored/hops/last-heard) for this device.</summary>
    void SaveNodeCache()
    {
        if (_mesh == null || _currentHost.Length == 0) return;
        DeviceCache.Save(_currentHost, _mesh.GetAvailableChannels(), _mesh.MyNodeNum,
            _mesh.GetNodeNameMap(), _mesh.GetNodeRoleMap(), _mesh.GetNodeHwMap(), _mesh.GetNodeShortNameMap(),
            _mesh.GetNodeFavoriteMap(), _mesh.GetNodeIgnoredMap(), _mesh.GetNodeHopsAwayMap(), _mesh.GetNodeLastHeardMap());
    }

    /// <summary>Raised when node prefs change (so an open Nodes page can refresh its rows).</summary>
    public event Action? NodesChanged;

    // The radio stamps received packets (and thus each node's "last heard") with its own clock. If that clock is
    // set ahead of real time, stamps land in the future. When we have positive evidence of that — a node heard
    // later than "now" on the phone — push the phone's time to the radio (set_time_only). Conservative on purpose:
    // we only write when clearly ahead (a future stamp can't come from anything but a fast radio clock), never to
    // "fix" a clock that merely looks behind (indistinguishable from a quiet mesh). Fire-and-forget, best-effort.
    async void SyncDeviceClockIfAhead()
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
            Status(err == null
                ? $"Device clock was ~{ahead} ahead — corrected from the phone."
                : $"Couldn't correct the device clock (~{ahead} ahead): {err}");
        }
        catch { /* best-effort — time sync never blocks the connection */ }
    }

    /// <summary>A short signal summary for a node ("RSSI … · SNR …", with hop context), or null if none heard.</summary>
    public string? NodeSignal(uint num)
    {
        if (_mesh?.GetSignal(num) is not { } s) return null;
        string sig = $"RSSI {s.Rssi} dBm · SNR {s.Snr:0.#} dB";
        return s.Hops switch { 0 => $"{sig} · direct", null => sig, 1 => $"via 1 hop · {sig}", var h => $"via {h} hops · {sig}" };
    }

    /// <summary>A sort key for a node's link quality (higher is better): fewer hops dominate, then stronger RSSI;
    /// a node we've heard no signal from sinks to the bottom. Matches the desktop "Signal" sort.</summary>
    public double NodeSignalSortKey(uint num) =>
        _mesh?.GetSignal(num) is { } s ? -(double)(s.Hops ?? 50) * 1000 + s.Rssi : double.NegativeInfinity;

    /// <summary>True when the node has reported environment telemetry this session (for the "Environment" sort).</summary>
    public bool NodeHasEnvironment(uint num) => _mesh?.GetEnvironment(num) != null;

    /// <summary>Requests a position broadcast from a node (so it appears on the map).</summary>
    public Task RequestNodePositionAsync(uint num) => _mesh?.RequestPositionAsync(num) ?? Task.CompletedTask;

    /// <summary>Requests a node's LocalStats noise floor (dBm). The reply is logged + surfaced via the poll loop.</summary>
    public Task RequestNoiseFloorAsync(uint num) => _mesh?.RequestNoiseFloorAsync(num) ?? Task.CompletedTask;

    /// <summary>Broadcasts our own NodeInfo (name/hardware/role) to the whole mesh, on demand.</summary>
    public Task BroadcastOwnNodeInfoAsync() => _mesh?.BroadcastOwnNodeInfoAsync() ?? Task.CompletedTask;

    /// <summary>Broadcasts our own position to the whole mesh, on demand. Throws if the device has no fix to share.</summary>
    public Task BroadcastOwnPositionAsync() => _mesh?.BroadcastOwnPositionAsync() ?? Task.CompletedTask;

    /// <summary>Requests the sender's NodeInfo (name/hardware/role) — used by the chat long-press menu when a
    /// node messages us but isn't in the node list yet. Returns a status line; the reply (handled by the poll
    /// loop) adds/updates the node.</summary>
    public async Task<string> RequestNodeInfoForAsync(uint nodeNum)
    {
        if (_mesh == null || nodeNum == 0) return "No sender to request info from.";
        string who = _mesh.DescribeNode(nodeNum);
        try { await _mesh.RequestNodeInfoAsync(nodeNum); return $"Requested node info from {who} (!{nodeNum:x8}). It should appear in Nodes shortly."; }
        catch (Exception ex) { return $"Request failed: {ex.Message}"; }
    }

    /// <summary>The snippet of the message currently being replied to (for the Chat tab's reply banner), or null
    /// when not replying.</summary>
    public string? ReplyingTo => _replyToId != 0 ? _replyToSnippet : null;

    /// <summary>Reply to a chat message (chat long-press → Reply): links the next send to it and points the TX at
    /// where the message came from — a DM back to the sender for a direct message, or the channel it arrived on
    /// for a group message.</summary>
    public void StartReply(LogEntry entry)
    {
        uint id = entry.Rx?.PacketId ?? entry.PacketId;
        if (id == 0) return;
        _replyToId = id;
        _replyToSnippet = ReplySnippet(entry.Text);

        // Point the TX at the source of the message we're replying to (received messages only — replying to your
        // own sent message keeps the current TX).
        if (entry.Rx is { } rx)
        {
            if (rx.IsDmTo(MyNodeNum) && rx.FromNode != 0 && _nodePrefs.GetValueOrDefault(rx.FromNode)?.Block != true)
            {
                SetNodePref(rx.FromNode, dm: true, block: false);   // DM back to the sender
                _chatTxDest = rx.FromNode;
            }
            else if (!rx.IsDirectMessage && _chatListen.Contains(rx.Channel))
            {
                _chatTxDest = null;
                SetChatTxChannel(rx.Channel);   // group reply on the channel it arrived on
            }
        }
        ApplyConnectionState();   // refresh the Chat tab (reply banner + TX picker)
    }

    public void CancelReply() { _replyToId = 0; _replyToSnippet = ""; ApplyConnectionState(); }

    /// <summary>Sends an emoji reaction (tapback) to a message and shows it under that message. Reacts on the same
    /// place the message lives — a DM back to the sender if it was a DM, else the channel it's on.</summary>
    public async Task ReactToAsync(LogEntry entry, string emoji)
    {
        if (_mesh == null || entry.PacketId == 0 || string.IsNullOrEmpty(emoji)) return;
        uint channel = entry.Rx?.Channel ?? _chatTxChannel;
        uint? dest = entry.Rx is { } rx && rx.IsDmTo(MyNodeNum) ? rx.FromNode : (uint?)null;
        try { await _mesh.SendReactionAsync(emoji, entry.PacketId, channel, dest); AddReaction(entry.PacketId, emoji, MyNodeNum); }
        catch { /* reactions are best-effort */ }
    }

    // Records an emoji reaction against a message (by packet id) and updates that row's reaction line. A node
    // reacting with the same emoji counts once.
    void AddReaction(uint targetId, string emoji, uint node)
    {
        if (string.IsNullOrEmpty(emoji)) return;
        if (!_reactions.TryGetValue(targetId, out var list)) _reactions[targetId] = list = new List<(string, uint)>();
        if (list.Any(r => r.Emoji == emoji && r.Node == node)) return;   // de-dup
        list.Add((emoji, node));
        if (_chatEntryById.TryGetValue(targetId, out var e)) e.Reactions = FormatReactions(list);
    }

    // "👍 ❤️ 2" — each distinct emoji once, with a count when more than one node reacted with it.
    static string FormatReactions(List<(string Emoji, uint Node)> list) =>
        string.Join("    ", list.GroupBy(r => r.Emoji).Select(g => g.Count() > 1 ? $"{g.Key} {g.Count()}" : g.Key));

    void ClearReply() { _replyToId = 0; _replyToSnippet = ""; }

    // Strips our own "You: " / "who: " / "DM ← who: " prefix so the banner quotes just the message body.
    static string ReplySnippet(string text)
    {
        int colon = text.IndexOf(':');
        string body = colon >= 0 && colon < 40 ? text[(colon + 1)..].Trim() : text;
        return body.Length > 60 ? body[..60] + "…" : body;
    }

    // A dim "↳ re: …" reference for a reply's metadata line, or "" when not a reply.
    string ReplyRef() => _replyToId == 0 ? "" :
        $"↳ re: {(_replyToSnippet.Length > 40 ? _replyToSnippet[..40] + "…" : _replyToSnippet)}";

    // A dim "↳ re: …" reference for an *incoming* reply, quoting the message it answers (by packet id) when we
    // still have that row — so a reply to one of your messages clearly shows what it's replying to.
    string ReplyRefFor(uint replyId)
    {
        if (replyId == 0) return "";
        string original = _chatEntryById.TryGetValue(replyId, out var e) ? ReplySnippet(e.Text) : "(earlier message)";
        if (original.Length > 40) original = original[..40] + "…";
        return $"↳ re: {original}";
    }

    /// <summary>Enables direct messages for a node and selects it as the chat TX target (chat long-press → DM).
    /// Returns a status line. Refuses a blocked node.</summary>
    public string StartDmWith(uint nodeNum)
    {
        if (_mesh == null || nodeNum == 0) return "No sender to DM.";
        if (_nodePrefs.GetValueOrDefault(nodeNum)?.Block == true)
            return $"{_mesh.DescribeNode(nodeNum)} is blocked — unblock it in Nodes to send a DM.";
        SetNodePref(nodeNum, dm: true, block: false);   // enable DM (persists + refreshes the TX targets)
        _chatTxDest = nodeNum;                            // select it as the TX destination
        ApplyConnectionState();                           // refresh the Chat tab's TX picker to the DM
        return $"Direct message to {_mesh.DescribeNode(nodeNum)} — type your message and Send.";
    }

    /// <summary>A human-readable signal/relay breakdown for a received message (chat long-press → details).</summary>
    public string MessageDetails(MeshTextMessage msg)
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
            // relay_node is only the last byte of the relayer's id, so every known node ending in that byte is a
            // possible relay — list them all (mirrors the desktop GUI).
            var candidates = _mesh?.GetNodes().Where(n => (byte)(n.Num & 0xFF) == msg.RelayNode).ToList() ?? new List<MeshNode>();
            if (candidates.Count == 0)
                lines.Add("  No known node ends in this byte — use Nodes → Update nodes to try to resolve it.");
            else
            {
                lines.Add(candidates.Count == 1
                    ? "  The relay was:"
                    : $"  could be any of these {candidates.Count} known nodes that share that last byte:");
                lines.AddRange(candidates.Select(n => $"   • {n.Display}  (!{n.Num:x8})"));
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>The details to show for a chat row (long-press → "Message details"): for a received message its
    /// signal/relay breakdown; for a sent message the acknowledgement(s) we received, with the acker's reported
    /// RSSI/SNR/hops both ways. Null when the row carries no extra detail (e.g. a sent message not yet acked).</summary>
    public string? MessageDetailsFor(LogEntry entry)
    {
        string? baseText = entry.Rx is { } msg ? MessageDetails(msg)   // received message
            : _chatAckers.Values.FirstOrDefault(i => i.Entry == entry) is { Ackers.Count: > 0 } info
                ? BuildAckDetails(info, mine: true)   // sent + ack-tracked
                : null;
        // For a received message, append the acks we overheard OTHER nodes send for it.
        if (entry.Rx is not null && entry.PacketId != 0
            && _overheardAcks.TryGetValue(entry.PacketId, out var oh) && oh.Ackers.Count > 0)
            baseText = (baseText is null ? "" : baseText + "\n\n") + BuildAckDetails(oh, mine: false);
        // Append who reacted, if anyone has.
        string reactions = ReactionDetails(entry.PacketId);
        if (reactions.Length > 0) baseText = baseText is null ? reactions : baseText + "\n\n" + reactions;
        return baseText;
    }

    // "Reactions:" plus a line per emoji listing the nodes that reacted with it (or "you"). "" when none.
    string ReactionDetails(uint packetId)
    {
        if (packetId == 0 || !_reactions.TryGetValue(packetId, out var list) || list.Count == 0) return "";
        var lines = new List<string> { "Reactions:" };
        foreach (var g in list.GroupBy(r => r.Emoji))
        {
            string who = string.Join(", ", g.Select(r => r.Node == MyNodeNum ? "you" : (_mesh?.DescribeNode(r.Node) ?? r.Node.ToString())));
            lines.Add($"  {g.Key}  {who}");
        }
        return string.Join("\n", lines);
    }

    string BuildAckDetails(ChatAckInfo info, bool mine)
    {
        string header = mine
            ? $"Acknowledged by {info.Ackers.Count} node{(info.Ackers.Count == 1 ? "" : "s")}:"
            : $"Also acknowledged by {info.Ackers.Count} other node{(info.Ackers.Count == 1 ? "" : "s")}:";
        string whose = mine ? "your message" : "the message";
        var lines = new List<string> { header };
        foreach (var a in info.Ackers)
        {
            lines.Add($"  • {_mesh?.DescribeNode(a)}  (!{a:x8})");
            // Direction 1: how the acker heard the original message (reported back inside its ack).
            lines.Add(info.AckerSignals.TryGetValue(a, out var them) && them.Length > 0
                ? $"      how they heard {whose}: {them}"
                : $"      how they heard {whose}: not reported (enable \"…with RSSI/SNR/hops\" on the channel to include it)");
            // Direction 2: how our node heard their ack packet coming back.
            lines.Add(info.MyReception.TryGetValue(a, out var me) && me.Length > 0
                ? $"      how your node heard their ack: {me}"
                : "      how your node heard their ack: no signal reported by your radio");
        }
        return string.Join("\n", lines);
    }

    // Routes the non-text parts of a receive batch: traceroute replies to an open Traceroute page, a nudge to an
    // open Telemetry page when new telemetry arrives, and a Nodes-page refresh when node info / a new node lands.
    void HandleReceiveExtras(MeshReceiveResult result)
    {
        foreach (var t in result.Traceroutes)
        {
            if (t.IsRequest)   // someone traced us — the firmware auto-replies; just note it in the system log
            {
                AddSystem(Stamp() + $"Traceroute request received from {_mesh?.DescribeNode(t.Node)} — the device is replying.", cat: SysCategory.Requests);
                continue;
            }
            if (_tracerouteWaiters.TryGetValue(t.Node, out var waiter)) waiter(t);
        }
        // Position heard from another node (broadcast, or a reply to our request): note it in the system log.
        if (AppSettings.ShowPositionUpdates)
            foreach (var pos in result.Positions)
            {
                string name = pos.Name.Length > 0 ? pos.Name : $"!{pos.Node:x8}";
                AddSystem(Stamp() + $"Position received from {name}: {pos.Latitude.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}, {pos.Longitude.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}.", cat: SysCategory.Position);
            }
        if (result.Positions.Count > 0)
        {
            // Persist the refreshed positions so they survive a reconnect/restart (mirrors the desktop app).
            if (_currentHost.Length > 0 && _mesh != null)
            {
                DeviceCache.SaveNodePositions(_currentHost, _mesh.GetNodePositionMap());
                DeviceCache.SavePositionHistory(_currentHost, _mesh.GetPositionHistoryMap());
            }
            NodesChanged?.Invoke();
        }
        if (result.Telemetry.Count > 0)
        {
            _telemetryRefresh?.Invoke();
            // Persist each reporting node's telemetry history so it survives a reconnect.
            if (_currentHost.Length > 0 && _mesh != null)
                foreach (var num in result.Telemetry.Distinct())
                    DeviceCache.SaveTelemetry(_currentHost, num, _mesh.GetEnvironmentHistory(num).Select(ToReading));
        }
        // Noise floor replies (requested via the node info page): log them and refresh any open node page.
        foreach (var nf in result.NoiseFloors)
            AddSystem(Stamp() + $"Noise floor for {(nf.Name.Length > 0 ? nf.Name : $"!{nf.Node:x8}")}: {nf.NoiseFloorDbm} dBm", cat: SysCategory.Telemetry);
        if (result.NoiseFloors.Count > 0) { _telemetryRefresh?.Invoke(); NodesChanged?.Invoke(); StateChanged?.Invoke(); }   // StateChanged → Device tab noise-floor row

        // New-node / node-info events in the system log (optional).
        if (AppSettings.ShowNewNodeInfo)
        {
            foreach (var nn in result.NewNodes)
                AddSystem(Stamp() + $"New node heard: {(nn.Name.Length > 0 ? nn.Name : $"!{nn.Node:x8}")}.", cat: SysCategory.Nodes);
            foreach (var ni in result.NodeInfos)
            {
                string who = ni.Name.Length > 0 ? ni.Name : $"!{ni.Node:x8}";
                // want_response set = the other node is ASKING us for our info (the device auto-replies); otherwise
                // it's an answer/announcement carrying that node's info.
                AddSystem(Stamp() + (ni.IsRequest
                    ? $"Node info request received from {who} — replying with ours."
                    : $"Node info received from {who}."), cat: SysCategory.Nodes);
            }
        }
        if (result.NodeInfos.Count > 0 || result.NewNodes.Count > 0)
        {
            // A node we'd only seen as "!hex" now has a name — re-render its existing chat rows to show it.
            var named = result.NodeInfos.Select(n => n.Node).Concat(result.NewNodes.Select(n => n.Node)).ToHashSet();
            RefreshChatNamesFor(named);
            NodesChanged?.Invoke();
        }
    }

    // Re-renders existing received chat rows from the given nodes with their current display name, so messages that
    // first showed the sender as "!hex" update in place once that node's info (name) arrives. The body text is kept
    // verbatim from when the row was added; only the "<name>: " prefix is recomputed.
    void RefreshChatNamesFor(HashSet<uint> nodes)
    {
        if (nodes.Count == 0) return;
        foreach (var e in _chat)
        {
            if (e.ChatNameBody is not { } bodyPart || e.Rx is not { } rx || rx.FromNode == 0) continue;
            if (!nodes.Contains(rx.FromNode)) continue;
            string who = DescribeNode(rx.FromNode);
            string dmTag = e.DmPeer != 0 ? "DM ← " : "";
            e.Text = $"{dmTag}{who}: {bodyPart}";
        }
    }

    // ---- Per-node actions (node long-press menu on the Nodes page) ----
    public uint ChannelForNode(uint num) => _mesh?.ChannelForNode(num) ?? 0;
    public string DescribeNode(uint num) => _mesh?.DescribeNode(num) ?? $"!{num:x8}";

    /// <summary>Sends a traceroute request to a node; the reply arrives via the poll loop and is delivered to the
    /// callback registered with <see cref="RegisterTracerouteWaiter"/>.</summary>
    public Task<uint> SendTracerouteAsync(uint num) => _mesh?.SendTracerouteAsync(num) ?? Task.FromResult(0u);
    public void RegisterTracerouteWaiter(uint num, Action<MeshTraceroute> cb) => _tracerouteWaiters[num] = cb;
    public void UnregisterTracerouteWaiter(uint num) => _tracerouteWaiters.Remove(num);

    /// <summary>The environment-telemetry readings heard from a node this session (oldest first).</summary>
    public IReadOnlyList<MeshEnvironment> NodeEnvironmentHistory(uint num) =>
        _mesh?.GetEnvironmentHistory(num) ?? (IReadOnlyList<MeshEnvironment>)Array.Empty<MeshEnvironment>();

    /// <summary>Everything known about a node (identity, hardware, role, signal, position, latest telemetry) plus
    /// this app's DM/Block prefs — for the "Show all info" view.</summary>
    public string NodeInfoText(uint num)
    {
        if (_mesh == null) return "";
        var (dm, block) = NodePrefFor(num);
        return _mesh.GetNodeInfoText(num) + $"\nDM: {(dm ? "on" : "off")}    Block: {(block ? "on" : "off")}";
    }
    public Task<uint> RequestTelemetryForAsync(uint num) => _mesh?.RequestTelemetryAsync(num) ?? Task.FromResult(0u);
    public void ClearNodeEnvironment(uint num) { _mesh?.ClearEnvironment(num); if (_currentHost.Length > 0) DeviceCache.ClearTelemetry(_currentHost, num); }
    /// <summary>An open Telemetry page registers here so it reloads when fresh telemetry arrives (null to clear).</summary>
    public void SetTelemetryRefresh(Action? cb) => _telemetryRefresh = cb;

    // ---- Device tab: connection + live device-info readout (the Device tab owns the Host/Connect UI) ----
    public bool IsConnecting => _connecting;
    public event Action? StateChanged;
    public Task<string> ConnectToAsync(string host) => ConnectAsync(host);
    public void DisconnectDevice() => Disconnect("Disconnected.");
    public string? DeviceName => _mesh?.GetOwner()?.LongName;
    public string? HardwareModel => _mesh?.HardwareModel;
    public string? FirmwareVersion => _mesh?.FirmwareVersion;
    public (int Percent, float Voltage)? DeviceBattery => _mesh?.GetDeviceBattery();
    /// <summary>The connected device's own last-reported noise floor (dBm), or null if not requested/reported yet.
    /// Refresh it with <see cref="RequestNoiseFloorAsync"/> passing <see cref="MyNodeNum"/>.</summary>
    public int? DeviceNoiseFloor => _mesh is { } m ? m.GetNoiseFloor(m.MyNodeNum) : null;
    public string SyncStatus => _syncStatus;

    // Mesh-sync progress lives on the Device tab (via StateChanged), not the Chess status line.
    void SetSyncStatus(string text) { _syncStatus = text; StateChanged?.Invoke(); }

    // ---- Settings tab: open the modal sections via MainPage's existing handlers ----
    public bool IsConnected => _connected;
    public bool IsSynced => _synced;   // true once the post-connect mesh sync has finished
    public void OpenNodes() => OnNodesClicked(this, EventArgs.Empty);
    public void OpenChannels() => OnChannelsClicked(this, EventArgs.Empty);
    public void OpenColours() => OnColorsClicked(this, EventArgs.Empty);
    public void OpenSound() => OnSoundClicked(this, EventArgs.Empty);
    public async void OpenSystemMessages() => await Navigation.PushModalAsync(new SystemMessagesPage());
    public async void OpenSystemSettings() => await Navigation.PushModalAsync(new SystemSettingsPage(this));
    public async void OpenChatSettings() => await Navigation.PushModalAsync(new ChatSettingsPage(this));
    public async void OpenChessSettings() => await Navigation.PushModalAsync(new ChessSettingsPage(this));


    void MarkAcked(MeshAck ack)
    {
        if (!_pending.TryGetValue(ack.PacketId, out var p) || !p.IsChat) return;

        if (p.IsDm)
        {
            // A relay ack (we overheard our DM rebroadcast — FromNode 0) only proves it propagated, NOT that the
            // recipient got it. Show "relayed" but keep waiting for the firmware's routing verdict (delivered/NAK).
            if (ack.FromNode == 0)
            {
                if (!ack.Failed)
                {
                    p.RelayHeard = true;
                    if (p.Entry != null) { p.Entry.Detail = p.BaseText + MarkDelivered + RelayDescription(ack); p.Entry.TextColor = Palette.Relayed; }   // teal — relayed; awaiting delivery
                }
                return;   // not terminal — the recipient's routing ack (or the timeout) decides delivered/failed
            }

            // The firmware's Routing verdict from the recipient/an intermediate (FromNode set): ack = delivered,
            // NAK = failed. Terminal — there's nothing for us to resend.
            _pending.Remove(ack.PacketId);
            if (p.Entry != null)
            {
                if (ack.Failed)
                {
                    p.Entry.Detail = p.BaseText + MarkFailed + $" not delivered ({ack.FailReason})";
                    p.Entry.TextColor = Palette.Warning;   // red — the recipient never acknowledged
                    Status($"Direct message not delivered ({ack.FailReason}).");
                }
                else
                {
                    p.Entry.Detail = p.BaseText + MarkDelivered + " delivered";
                    p.Entry.TextColor = Palette.Acked;     // green — acknowledged by the recipient
                }
            }
            ApplyConnectionState();   // release — the next chat can be sent
            return;
        }

        // Broadcast relay ack (we overheard a rebroadcast). Only a success counts; ignore a NAK.
        if (ack.Failed) return;
        if (p.Entry != null) { p.Entry.Detail = p.BaseText + MarkDelivered + RelayDescription(ack); p.Entry.TextColor = Palette.Relayed; }   // teal — rebroadcast heard

        if (p.IsRelayConfirm)
        {
            // Ack-off channel: a relay is the only confirmation we get, so it's terminal.
            _pending.Remove(ack.PacketId);
            ApplyConnectionState();   // release — the next chat can be sent
        }
        else
        {
            // Ack-on channel: "relayed" is an INTERMEDIATE state — keep the message in flight and keep waiting for
            // the channel ack (which turns it green). If no ack arrives, it stays relayed (see the timeout).
            p.RelayHeard = true;
        }
    }

    /// <summary>" relayed by &lt;node&gt; [RSSI/SNR]" for a relay ack that named the relayer, else " relayed".</summary>
    string RelayDescription(MeshAck ack)
    {
        // Prefer the firmware-reported last-hop relayer; otherwise the node the (implicit) ack came from.
        string who = _mesh?.DescribeRelayNode(ack.RelayNode) ?? "";
        if (who.Length == 0 && ack.FromNode != 0 && ack.FromNode != (_mesh?.MyNodeNum ?? 0))
            who = _mesh?.DescribeNode(ack.FromNode) ?? "";
        if (who.Length == 0) return " relayed";
        string sig = (ack.RxRssi != 0 || ack.RxSnr != 0) ? $" [RSSI {ack.RxRssi} dBm · SNR {ack.RxSnr:0.#} dB]" : "";
        return $" relayed by {who}{sig}";
    }

    // Loads the per-channel auto-ack keyword lists for a device into _ackTriggers.
    void LoadAckTriggers(string host)
    {
        _ackTriggers.Clear();
        foreach (var kv in DeviceCache.GetAckTriggers(host)) _ackTriggers[kv.Key] = new List<string>(kv.Value);
    }

    // True when a received message's text (matched case-insensitively) matches any of the channel's auto-ack
    // keywords — meaning we reply with an RSSI ack even if acks are off for the channel. A keyword wrapped in
    // double quotes ("ping") matches only when the whole message equals it; an unquoted keyword (ping) matches
    // anywhere in the text (a substring), as before.
    bool MatchesAckTrigger(uint channel, string? text)
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

    async void SendChatAck(uint chatPacketId, uint channel, string signal = "")
    {
        if (_mesh == null) return;
        await AckJitterDelay();
        try { await _mesh.SendTextAsync(ProtocolMessage.EncodeChatAck(chatPacketId, signal), channel, encrypt: false); } catch { }
    }

    /// <summary>Compact RSSI/SNR/hops description of a received message, for embedding in a chat ack
    /// (no '|', no brackets). Empty when the device reported no signal and the hop count is unknown.</summary>
    static string AckSignalText(MeshTextMessage m)
    {
        if (m.Hops is int hops && hops > 0) return $"{hops} hop{(hops == 1 ? "" : "s")}";
        if (m.RxRssi != 0 || m.RxSnr != 0) return $"{(m.IsDirect ? "direct " : "")}RSSI {m.RxRssi} dBm SNR {m.RxSnr:0.#} dB";
        return m.IsDirect ? "direct" : "";
    }

    void RegisterChatAck(uint chatPacketId, uint ackerNum, string signal = "", string myReception = "")
    {
        if (_chatAckers.TryGetValue(chatPacketId, out var info))
        {
            if (!info.Ackers.Add(ackerNum)) return;
            if (!string.IsNullOrEmpty(signal)) info.AckerSignals[ackerNum] = signal;            // how they heard our message
            if (!string.IsNullOrEmpty(myReception)) info.MyReception[ackerNum] = myReception;   // how we heard their ack packet
            bool wasInFlight = ChatInFlight;
            _pending.Remove(chatPacketId);
            UpdateChatAckerText(info);
            if (wasInFlight) ApplyConnectionState();
            return;
        }
        // A CHATACK for a message someone ELSE sent that we also received: remember who acked it so the received
        // message's "Message details" can list them. We don't touch the received row's text — only its details.
        if (_chatEntryById.TryGetValue(chatPacketId, out var rxEntry) && rxEntry.Rx != null)
        {
            if (!_overheardAcks.TryGetValue(chatPacketId, out var oh))
                _overheardAcks[chatPacketId] = oh = new ChatAckInfo { Entry = rxEntry };
            if (!oh.Ackers.Add(ackerNum)) return;
            if (!string.IsNullOrEmpty(signal)) oh.AckerSignals[ackerNum] = signal;
            if (!string.IsNullOrEmpty(myReception)) oh.MyReception[ackerNum] = myReception;
        }
    }

    void RefreshChatAckerNames()
    {
        foreach (var info in _chatAckers.Values) if (info.Ackers.Count > 0) UpdateChatAckerText(info);
    }

    /// <summary>A reply on the channel our in-flight chat was sent to is taken as an implicit ack:
    /// mark our message delivered (green) and release the in-flight lock so another chat can be sent.</summary>
    void ConfirmChatByIncomingReply(uint channel)
    {
        // DMs are confirmed solely by the firmware's routing ack/NAK (MarkAcked), not by an unrelated reply
        // arriving on the same channel — so exclude them here, matching the desktop app.
        var p = _pending.Values.FirstOrDefault(x => x.IsChat && !x.IsDm && x.Channel == channel);
        if (p == null) return;
        _pending.Remove(p.Id);
        _chatAckers.Remove(p.Id);
        if (p.Entry != null) { p.Entry.Detail = p.BaseText + MarkDelivered + " (reply received)"; p.Entry.TextColor = Palette.Acked; }
        ApplyConnectionState();
    }

    void UpdateChatAckerText(ChatAckInfo info)
    {
        string names = string.Join(", ", info.Ackers.Select(a =>
        {
            string n = _mesh?.DescribeNode(a) ?? a.ToString();
            return info.AckerSignals.TryGetValue(a, out var s) && s.Length > 0 ? $"{n} [{s}]" : n;
        }));
        info.Entry.Detail = $"{info.BaseText} {MarkDelivered.Trim()} acked by: {names}";
        info.Entry.TextColor = Palette.Acked;
    }

    void ConfirmPendingMove()
    {
        var move = _pending.Values.FirstOrDefault(p => p.IsMove);
        if (move == null) return;
        _pending.Remove(move.Id);
        if (move.Entry != null) { move.Entry.Text = move.BaseText + MarkDelivered; move.Entry.TextColor = Palette.Acked; }
        ApplyConnectionState();
    }

    async Task ResendPendingAsync(PendingSend p)
    {
        if (_mesh == null) return;
        try
        {
            uint oldId = p.Id;
            uint newId;
            // Chat resends are marked so the receiver knows it's a resend; a fresh id avoids mesh dedupe.
            if (p.IsChat) { p.Channel = _chatTxChannel; newId = await _mesh.SendTextAsync(ProtocolMessage.ChatResendPrefix + p.Payload, _chatTxChannel); }
            else newId = await _mesh.SendTextAsync(p.Payload);
            if (newId == oldId) return;
            if (_pending.Remove(oldId)) { p.Id = newId; _pending[newId] = p; }
            if (_chatAckers.Remove(oldId, out var info)) _chatAckers[newId] = info;
        }
        catch { }
    }

    async Task CheckPendingAcksAsync()
    {
        CheckReceptionStall();   // piggyback the 1s timer for the reception watchdog
        if (_checkingAcks || _mesh == null || !_synced || _pending.Count == 0) return;
        _checkingAcks = true;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var p in _pending.Values.ToList())
            {
                if (p.NeedsResend) continue;
                if (now - p.LastSentUtc < AckTimeout) continue;

                if (p.Attempts < MaxSendAttempts)
                {
                    p.Attempts++; p.LastSentUtc = now;
                    if (p.IsDm)
                    {
                        // The firmware does its own hop-by-hop retransmit for a DM and reports an ack or a NAK, so
                        // we never resend it ourselves — just keep waiting for the firmware's verdict (MarkAcked).
                    }
                    else
                    {
                        string missing = p.IsRelayConfirm ? "relay heard" : "acknowledgement";
                        Status($"No {missing} for {p.Label} — resending (attempt {p.Attempts}/{MaxSendAttempts})...");
                        await ResendPendingAsync(p);
                    }
                }
                else if (p.IsMove)
                {
                    p.NeedsResend = true;
                    if (p.Entry != null) p.Entry.TextColor = Palette.Warning;
                    Status($"{p.Label} not acknowledged after {MaxSendAttempts} attempts — press Resend to try again.");
                    ApplyConnectionState();
                }
                else if (p.IsJoin) { _pending.Remove(p.Id); FailJoin($"The host did not respond after {MaxSendAttempts} attempts."); }
                else if (p.IsResign) { _pending.Remove(p.Id); FailResign($"No response after {MaxSendAttempts} attempts."); }
                else if (p.IsCreate) { _pending.Remove(p.Id); FailCreate($"No one responded after {MaxSendAttempts} attempts."); }
                else if (p.IsSave) { _pending.Remove(p.Id); FailSave($"No response after {MaxSendAttempts} attempts."); }
                else if (p.IsDm)
                {
                    // DM window elapsed with no firmware verdict. If we at least overheard it relayed, leave it in
                    // the teal "relayed" state (note it was never confirmed delivered); otherwise the recipient is
                    // likely offline/out of range — leave it as plainly sent.
                    _pending.Remove(p.Id);
                    if (p.RelayHeard)
                    {
                        if (p.Entry != null) p.Entry.Detail += " (no delivery confirmation)";   // row already shows "relayed by …" in teal
                        Status("Direct message relayed but not confirmed delivered (the recipient may be offline).");
                    }
                    else
                    {
                        if (p.Entry != null) { p.Entry.Detail = p.BaseText + MarkSent + " (no delivery confirmation)"; p.Entry.TextColor = Palette.Normal; }
                        Status("No delivery confirmation from the recipient — it may be offline or out of range.");
                    }
                    ApplyConnectionState();
                }
                else if (p.IsRelayConfirm)
                {
                    // No relay overheard within the window: treat it as a failed send (red ✗), since on an ack-off
                    // channel a rebroadcast is the only delivery signal we get and we never saw one.
                    _pending.Remove(p.Id);
                    if (p.Entry != null) { p.Entry.Detail = p.BaseText + MarkFailed + " (no relay heard)"; p.Entry.TextColor = Palette.Warning; }
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
        finally { _checkingAcks = false; }
    }

    void FailPending(PendingSend p)
    {
        if (p.Entry != null) { p.Entry.Detail = p.BaseText + MarkFailed; p.Entry.TextColor = Palette.Warning; }
        Status("A chat message was not delivered.");
    }

    // ---- Nodes ----
    // Opens the Nodes page (list with hardware/role/signal, DM & Block toggles, a map, and a per-node long-press
    // menu). Polling keeps running while it's open so traceroute / telemetry / node-info replies arrive via the
    // poll loop (only the explicit "Update nodes" fetch pauses it — see FetchNodesAsync).
    async void OnNodesClicked(object? sender, EventArgs e)
    {
        if (_mesh == null) return;
        try
        {
            var page = new NodesPage(this);
            await Navigation.PushModalAsync(page);
            await page.Completion;
        }
        catch (Exception ex) { Status($"Nodes error: {ex.Message}"); }
    }

    /// <summary>Fetches the device's node list (want-config drain). Pauses the poll loop for the drain so the two
    /// don't both consume the radio queue, then restores it. Returns the node count.</summary>
    public async Task<int> FetchNodesAsync(Action<string> report)
    {
        if (_mesh == null) return 0;
        bool wasPolling = _pollTimer!.IsRunning;
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
                HandleReceiveExtras(r);
                report($"Updating… {_mesh.GetNodes().Count} nodes so far");
                if (r.PacketCount < chunk) break;
            }
            DeviceCache.Save(_currentHost, _mesh.GetAvailableChannels(), _mesh.MyNodeNum, _mesh.GetNodeNameMap(), _mesh.GetNodeRoleMap(), _mesh.GetNodeHwMap(), _mesh.GetNodeShortNameMap(), _mesh.GetNodeFavoriteMap(), _mesh.GetNodeIgnoredMap(), _mesh.GetNodeHopsAwayMap(), _mesh.GetNodeLastHeardMap());
            RefreshChatAckerNames();
            return _mesh.GetNodes().Count;
        }
        finally
        {
            _refreshing = false;
            _lastRxUtc = DateTime.UtcNow; _rxStallWarned = false;   // polling was paused — re-arm the watchdog
            ApplyConnectionState();
            if (wasPolling) _pollTimer.Start();
        }
    }

    // ---- End / status ----
    bool CheckForEnd()
    {
        var status = _board.GetStatus();
        if (status == GameStatus.Checkmate) { EndGame($"Checkmate — {_board.SideToMove.Opposite()} wins."); return true; }
        if (status == GameStatus.Stalemate) { EndGame("Stalemate — draw."); return true; }
        return false;
    }

    async void EndGame(string message)
    {
        _gameOver = true; _playing = false; _resigning = false;
        ApplyConnectionState();
        _selected = null; _legalForSelected.Clear();
        Render();
        Status(message);
        AddSystem(Stamp() + $"— {message} —");
        await DisplayAlert("Game over", message, "OK");
    }

    void UpdateTurnStatus()
    {
        string check = _board.GetStatus() == GameStatus.Check ? " (check!)" : "";
        bool mine = _board.SideToMove == _myColor;
        Status(mine
            ? $"Your move — {_myColor}{check}.  Game '{_gameId}', channel {_mesh?.ChannelIndex}."
            : $"Waiting for {_board.SideToMove}{check}...");
    }

    static string MoveLine(int ply, GameColor mover, Move move) => $"{ply,3}. {mover,-5} {move.ToUci()}";

    // ---- Logging helpers ----
    string Stamp()
    {
        DateTime when = _incomingRxTime != 0 ? DateTimeOffset.FromUnixTimeSeconds(_incomingRxTime).LocalDateTime : DateTime.Now;
        return $"[{when:MM-dd HH:mm:ss}] ";
    }

    // Which channel a chat message was received/sent on — "Robotnic [1]" (or just "[1]" when unnamed).
    string ChannelLabel(uint index)
    {
        var channels = _mesh?.GetAvailableChannels();
        if (channels != null)
            foreach (var c in channels)
                if (c.Index == index && !string.IsNullOrWhiteSpace(c.Name))
                    return $"{c.Name} [{index}]";
        return $"[{index}]";
    }

    LogEntry AddMove(string text, Color? color = null) => Append(_moves, MovesView, text, color, _movesFamily, _movesSize);
    LogEntry AddSystem(string text, Color? color = null, SysCategory cat = SysCategory.Game)
    {
        // No explicit colour → use the category colour (game-flow rows that later show ack status pass an
        // explicit Pending/Acked/Warning colour, so status still wins there).
        var entry = Append(_system, SystemView, text, color ?? SysCategoryColor(cat), _systemFamily, _systemSize);
        entry.Category = cat;
        entry.Visible = !_hiddenSysCats.Contains(cat);
        TrimSystemMessages();
        return entry;
    }

    /// <summary>Trims the System list to <see cref="AppSettings.SystemMessageLimit"/> rows (oldest removed first).</summary>
    public void TrimSystemMessages()
    {
        int limit = AppSettings.SystemMessageLimit;
        while (limit > 0 && _system.Count > limit) _system.RemoveAt(0);
    }

    /// <summary>Trims the on-screen chat rows for one channel to <see cref="AppSettings.ChatMessageLimit"/>.</summary>
    void TrimChatChannel(uint channel)
    {
        if (channel == uint.MaxValue) return;
        int limit = AppSettings.ChatMessageLimit;
        if (limit <= 0) return;
        var rows = _chat.Where(e => e.Channel == channel).ToList();
        for (int i = 0; i < rows.Count - limit; i++) _chat.Remove(rows[i]);
    }

    /// <summary>Re-applies the chat message limit to every channel on screen (e.g. after it's lowered in settings).</summary>
    public void ApplyChatMessageLimit()
    {
        foreach (var ch in _chat.Select(e => e.Channel).Distinct().ToList()) TrimChatChannel(ch);
    }

    /// <summary>Removes on-screen chat rows older than the current device's per-channel auto-delete age.</summary>
    void PruneChatRam()
    {
        if (_currentHost.Length == 0) return;
        var retention = DeviceCache.GetChannelRetention(_currentHost);
        if (retention.Count == 0) return;
        var now = DateTime.Now;
        for (int i = _chat.Count - 1; i >= 0; i--)
        {
            var e = _chat[i];
            if (e.Time == default) continue;
            if (retention.TryGetValue(e.Channel, out var mins) && mins > 0 && e.Time < now.AddMinutes(-mins))
                _chat.RemoveAt(i);
        }
    }

    /// <summary>Auto-deletes chat older than each channel's retention — cache + on screen. Called on connect, on a
    /// periodic sweep, and when the retention setting changes.</summary>
    public void ApplyChatRetention()
    {
        if (_currentHost.Length == 0) return;
        DeviceCache.PruneChatByRetention(_currentHost);
        DeviceCache.PruneChatByExpiry(_currentHost);
        PruneChatRam();
    }

    /// <summary>Formats a self-destruct countdown for the dim line: "deletes in 1h 05m" / "deletes in 9:58" /
    /// "deletes in 8s".</summary>
    static string ExpiryCountdown(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        if (left.TotalHours >= 1) return $"deletes in {(int)left.TotalHours}h {left.Minutes:00}m";
        if (left.TotalMinutes >= 1) return $"deletes in {left.Minutes}:{left.Seconds:00}";
        return $"deletes in {left.Seconds}s";
    }

    /// <summary>Refreshes the live "deletes in …" countdown on every expiring chat row, and removes rows (screen +
    /// cache) whose sender-set self-destruct time has passed. Runs each second from the ack timer.</summary>
    void UpdateExpiryCountdowns()
    {
        var now = DateTime.Now;
        for (int i = _chat.Count - 1; i >= 0; i--)
        {
            var e = _chat[i];
            if (e.ExpiresAt is not DateTime exp) continue;
            if (now >= exp)
            {
                // Screen-only removal — no disk I/O in this per-second loop. The cache copy is dropped by the
                // batched PruneChatByExpiry (the ~60 s retention sweep) and again on the next connect (which prunes
                // before loading), so an expired message can never reappear even if its cache row lingers briefly.
                _chat.RemoveAt(i);
                continue;
            }
            string wanted = "🕓 " + ExpiryCountdown(exp - now);
            if (e.Expiry != wanted) e.Expiry = wanted;
        }
    }

    /// <summary>Channels for the connected device (live), else the cached list for the last device. For settings UIs.</summary>
    public IReadOnlyList<MeshChannel> GetAvailableChannels()
    {
        if (_mesh != null) return _mesh.GetAvailableChannels();
        var host = CurrentHost;
        var cached = host.Length > 0 ? DeviceCache.Get(host) : null;
        return cached != null ? DeviceCache.ToMeshChannels(cached) : Array.Empty<MeshChannel>();
    }

    /// <summary>The connected device's host, or the last-connected host when offline ("" if never connected).</summary>
    public string CurrentHost => _currentHost.Length > 0 ? _currentHost : (AppSettings.LastHost ?? "");
    // Chat has its own bottom tab now (no in-panel CollectionView here), so pass no view to scroll.
    LogEntry AddChat(string text, Color? color = null) => Append(_chat, null, text, color, _chatFamily, _chatSize);

    /// <summary>Adds a chat row: the message (prominent) plus a dim metadata line (timestamp/channel/signal/marks).</summary>
    LogEntry AddChatLine(string message, string detail, Color color, MeshTextMessage? rx = null)
    {
        var entry = Append(_chat, null, message, color, _chatFamily, _chatSize, detail);
        entry.Time = DateTime.Now;           // for age-based auto-delete (LoadCachedChat overrides with the cached time)
        entry.Rx = rx;                       // received rows carry the raw message (for node info / details / reply)
        entry.PacketId = rx?.PacketId ?? 0;  // received rows can be replied to / reacted to by id
        if (entry.PacketId != 0)
        {
            _chatEntryById[entry.PacketId] = entry;   // so reactions can attach to this row
            if (_reactions.TryGetValue(entry.PacketId, out var early)) entry.Reactions = FormatReactions(early);
        }
        return entry;
    }

    /// <summary>The categories currently hidden (so the filter page can show the toggle states).</summary>
    public IReadOnlyCollection<SysCategory> HiddenSysCats => _hiddenSysCats;

    // Loads the hidden system-message categories from settings.
    void LoadSystemFilter()
    {
        _hiddenSysCats.Clear();
        foreach (var name in (AppSettings.SystemFilterHidden ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse<SysCategory>(name, out var c)) _hiddenSysCats.Add(c);
    }

    /// <summary>Shows/hides a system-message category live (re-stamps every current row) and persists the choice.</summary>
    public void SetSystemCategoryHidden(SysCategory cat, bool hidden)
    {
        if (hidden) _hiddenSysCats.Add(cat); else _hiddenSysCats.Remove(cat);
        foreach (var e in _system) e.Visible = !_hiddenSysCats.Contains(e.Category);
        AppSettings.SystemFilterHidden = string.Join(",", _hiddenSysCats);
    }

    static LogEntry Append(ObservableCollection<LogEntry> list, CollectionView? view, string text, Color? color,
                           string family, double size, string detail = "")
    {
        var entry = new LogEntry { Text = text, Detail = detail, TextColor = color ?? Palette.Normal, FontFamily = family, FontSize = size };
        list.Add(entry);
        try { view?.ScrollTo(entry, position: ScrollToPosition.End, animate: false); } catch { }
        return entry;
    }

    // ---- Per-list fonts (live-applied + persisted) ----
    void ApplyMovesFont(string family, double size)
    {
        _movesFamily = family; _movesSize = size;
        foreach (var e in _moves) { e.FontFamily = family; e.FontSize = size; }
        AppSettings.MovesFont = family; AppSettings.MovesSize = size;
    }

    void ApplySystemFont(string family, double size)
    {
        _systemFamily = family; _systemSize = size;
        foreach (var e in _system) { e.FontFamily = family; e.FontSize = size; }
        AppSettings.SystemFont = family; AppSettings.SystemSize = size;
    }

    void ApplyChatFont(string family, double size)
    {
        _chatFamily = family; _chatSize = size;
        foreach (var e in _chat) { e.FontFamily = family; e.FontSize = size; }   // message line; detail stays small
        ChatTab?.ApplyComposerFont(family, size);   // keep the TX composer in sync with the chat text
        AppSettings.ChatFont = family; AppSettings.ChatSize = size;
    }

    void ApplyNodesFont(string family, double size)
    {
        _nodesFamily = family; _nodesSize = size;
        NodesPageRef?.ApplyFont(family, size);   // restyle an open Nodes page live; otherwise it seeds on next open
        AppSettings.NodesFont = family; AppSettings.NodesSize = size;
    }

    void Notify()
    {
        try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(120)); } catch { }
    }

    // Posts a status-bar notification for a new message/event, but only while the app is backgrounded (when it's
    // in front the on-screen chat/board already shows it). The foreground service keeps us alive to do this.
    void NotifyBackground(string title, string body)
    {
        if (AppState.IsForeground) return;
        try { BackgroundConnection.NotifyMessage(title, body); } catch { }
    }

    void Status(string text) => StatusText.Text = text;
}
