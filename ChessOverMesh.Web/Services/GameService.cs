using ChessOverMesh.Chess;
using ChessOverMesh.Game;
using ChessOverMesh.Mesh;
using ChessBoard = ChessOverMesh.Chess.Board;

namespace ChessOverMesh.Web.Services;

/// <summary>
/// Server-side orchestration for one chess-over-Meshtastic game, ported from the WPF MainWindow flow but
/// reusing the same engine (<see cref="ChessBoard"/>), protocol (<see cref="ProtocolMessage"/>) and transport
/// (<see cref="MeshtasticHttpClient"/>). A single background loop polls the device's /fromradio queue (which is
/// a single-consumer queue). ALL mesh access and state mutation is serialised through one gate, and every send
/// is awaited inside it, so the loop and UI commands never touch the radio or board concurrently. The UI
/// subscribes to <see cref="Changed"/>.
/// </summary>
public sealed class GameService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MeshtasticHttpClient? _mesh;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    // ---- Connection state ----
    public bool Connected { get; private set; }
    public string Host { get; private set; } = "";
    public uint MyNodeNum { get; private set; }
    public uint ChessChannel { get; private set; }

    // ---- Game state ----
    public ChessBoard? Board { get; private set; }
    public Color MyColor { get; private set; }
    public string GameId { get; private set; } = "";
    public int Ply { get; private set; }
    public bool Playing { get; private set; }
    public bool GameOver { get; private set; }
    public bool AwaitingOpponent { get; private set; }
    public bool Joining { get; private set; }
    public (int From, int To)? LastMove { get; private set; }
    public string Status { get; private set; } = "Not connected.";
    public bool MoveNeedsResend { get; private set; }

    public bool MyTurn => Playing && !GameOver && !AwaitingOpponent && Board is { } b && b.SideToMove == MyColor;

    private readonly List<string> _log = new();
    public IReadOnlyList<string> Log { get { lock (_log) return _log.ToList(); } }

    public sealed record OpenGame(string GameId, uint CreatorNode, Color CreatorColor, uint Channel);
    private readonly Dictionary<string, OpenGame> _openGames = new();
    public IReadOnlyList<OpenGame> OpenGames { get { lock (_openGames) return _openGames.Values.ToList(); } }

    private sealed class Pending { public string Payload = ""; public int Attempts; public DateTime LastSent; public int Ply; }
    private Pending? _pendingMove, _pendingCreate, _pendingJoin, _pendingResign;

    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(12);
    private const int MaxAttempts = 3;

    public event Action? Changed;
    private void Notify() => Changed?.Invoke();

    private void AddLog(string msg)
    {
        lock (_log)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss}  {msg}");
            if (_log.Count > 300) _log.RemoveRange(0, _log.Count - 300);
        }
    }

    // ---------------------------------------------------------------- Connect

    public async Task ConnectAsync(string host, uint channel)
    {
        if (Connected) return;
        host = host.Trim();
        if (host.Length == 0) { Status = "Enter a device host."; Notify(); return; }
        if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "http://" + host;

        Status = "Connecting…"; Notify();
        await _gate.WaitAsync();
        try
        {
            var mesh = new MeshtasticHttpClient(host);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                await mesh.InitializeAsync(cts.Token);
            mesh.ChannelIndex = channel;
            _mesh = mesh;
            Host = host; MyNodeNum = mesh.MyNodeNum; ChessChannel = channel; Connected = true;
            Status = $"Connected to {host} (node !{MyNodeNum:x8}), chess on channel {channel}.";
            AddLog(Status);
        }
        catch (Exception ex)
        {
            _mesh?.Dispose(); _mesh = null;
            Status = $"Connect failed: {ex.Message}"; AddLog(Status); Notify();
            return;
        }
        finally { _gate.Release(); }

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => PollLoopAsync(_loopCts.Token));
        Notify();
    }

    public async Task DisconnectAsync()
    {
        _loopCts?.Cancel();
        if (_loopTask != null) { try { await _loopTask; } catch { } }
        _loopCts?.Dispose(); _loopCts = null; _loopTask = null;

        await _gate.WaitAsync();
        try
        {
            _mesh?.Dispose(); _mesh = null;
            Connected = false; Playing = false; GameOver = false; AwaitingOpponent = false; Joining = false;
            Board = null; GameId = ""; Ply = 0; LastMove = null; MoveNeedsResend = false;
            _pendingMove = _pendingCreate = _pendingJoin = _pendingResign = null;
            lock (_openGames) _openGames.Clear();
            Status = "Disconnected."; AddLog(Status);
        }
        finally { _gate.Release(); }
        Notify();
    }

    public async Task ResetToLobbyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Playing = false; GameOver = false; AwaitingOpponent = false; Joining = false;
            Board = null; GameId = ""; Ply = 0; LastMove = null; MoveNeedsResend = false;
            _pendingMove = _pendingCreate = _pendingJoin = _pendingResign = null;
            Status = "Back in lobby.";
        }
        finally { _gate.Release(); }
        Notify();
    }

    // ---------------------------------------------------------------- Poll loop

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _gate.WaitAsync(ct);
                try
                {
                    if (_mesh == null) return;
                    var result = await _mesh.ReceiveAsync(ct: ct);
                    foreach (var msg in result.Texts) await DispatchAsync(msg);
                    await CheckPendingAsync();
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AddLog($"Receive error: {ex.Message}"); }

            Notify();
            try { await Task.Delay(TimeSpan.FromSeconds(1.3), ct); } catch { return; }
        }
    }

    private async Task CheckPendingAsync()
    {
        if (_mesh == null) return;
        var now = DateTime.UtcNow;

        if (_pendingMove != null && now - _pendingMove.LastSent >= AckTimeout)
        {
            if (_pendingMove.Attempts < MaxAttempts) { _pendingMove.Attempts++; _pendingMove.LastSent = now; await _mesh.SendTextAsync(_pendingMove.Payload); AddLog($"Resending move ({_pendingMove.Attempts}/{MaxAttempts})."); }
            else if (!MoveNeedsResend) { MoveNeedsResend = true; AddLog("Move not acknowledged — click Resend move."); }
        }
        if (_pendingCreate != null && now - _pendingCreate.LastSent >= AckTimeout && _pendingCreate.Attempts < MaxAttempts)
        {
            _pendingCreate.Attempts++; _pendingCreate.LastSent = now; await _mesh.SendTextAsync(_pendingCreate.Payload);
        }
        if (_pendingJoin != null && now - _pendingJoin.LastSent >= AckTimeout)
        {
            if (_pendingJoin.Attempts < MaxAttempts) { _pendingJoin.Attempts++; _pendingJoin.LastSent = now; await _mesh.SendTextAsync(_pendingJoin.Payload); }
            else { _pendingJoin = null; Joining = false; AddLog("No reply to join — the host didn't answer. Try again."); }
        }
        if (_pendingResign != null && now - _pendingResign.LastSent >= AckTimeout)
        {
            if (_pendingResign.Attempts < MaxAttempts) { _pendingResign.Attempts++; _pendingResign.LastSent = now; await _mesh.SendTextAsync(_pendingResign.Payload); }
            else { _pendingResign = null; EndGame("Resignation not acknowledged — game ended locally."); }
        }
    }

    // ---------------------------------------------------------------- Dispatch (incoming)

    private async Task DispatchAsync(MeshTextMessage msg)
    {
        if (_mesh == null) return;
        if (!ProtocolMessage.TryParse(msg.Text, out var pm)) return;   // POC: chess protocol only, no chat
        if (msg.Channel != _mesh.ChannelIndex) return;                 // chess channel only

        switch (pm.Kind)
        {
            case MessageKind.Create: await HandleAnnouncedAsync(pm, msg); break;
            case MessageKind.CreateAck: if (pm.GameId == GameId && _pendingCreate != null) { _pendingCreate = null; AddLog("Opponent saw the game — waiting for them to join."); } break;
            case MessageKind.Join: await HandleJoinRequestAsync(pm); break;
            case MessageKind.Board: HandleBoard(pm); break;
            case MessageKind.Move: await ApplyIncomingAsync(pm); break;
            case MessageKind.Ack: HandleMoveAck(pm); break;
            case MessageKind.Resign: await HandleResignAsync(pm); break;
            case MessageKind.ResignAck: if (pm.GameId == GameId && _pendingResign != null) { _pendingResign = null; EndGame("You resigned. Opponent wins."); } break;
            case MessageKind.Ended: if (pm.GameId == GameId && Playing) { _pendingMove = null; EndGame("Opponent ended the game."); } break;
        }
    }

    private async Task HandleAnnouncedAsync(ProtocolMessage pm, MeshTextMessage msg)
    {
        if (pm.GameId == GameId && Playing) return;
        await _mesh!.SendTextAsync(ProtocolMessage.EncodeCreateAck(pm.GameId), encrypt: false);
        bool isNew;
        lock (_openGames)
        {
            isNew = !_openGames.ContainsKey(pm.GameId);
            _openGames[pm.GameId] = new OpenGame(pm.GameId, msg.FromNode, pm.AnnouncedColor ?? Color.White, msg.Channel);
        }
        if (isNew) AddLog($"Game [{pm.GameId}] available from !{msg.FromNode:x8} (you'd play {(pm.AnnouncedColor ?? Color.White).Opposite()}).");
    }

    private async Task HandleJoinRequestAsync(ProtocolMessage pm)
    {
        if (pm.GameId != GameId || !Playing) return;   // a join for the game we host
        await _mesh!.SendTextAsync(ProtocolMessage.EncodeBoard(GameId, MyColor.Opposite(), Board!.ToFen()), encrypt: false);
        if (AwaitingOpponent) { AwaitingOpponent = false; AddLog($"Opponent joined as {MyColor.Opposite()}. Game on."); UpdateStatus(); }
    }

    private void HandleBoard(ProtocolMessage pm)
    {
        if (!Joining || pm.GameId != GameId) return;
        _pendingJoin = null;
        var color = pm.AnnouncedColor ?? MyColor;
        Joining = false;
        BeginGame(color, GameId, pm.Fen, awaiting: false);
        AddLog($"Joined game [{GameId}] as {color}.");
    }

    private async Task ApplyIncomingAsync(ProtocolMessage pm)
    {
        if (Board is not { } board || !Playing || GameOver || pm.GameId != GameId) return;
        if (pm.Ply <= Ply) { await SendMoveAckAsync(pm.Ply); return; }              // already have it — re-ack
        if (pm.Ply != Ply + 1) { AddLog($"Out-of-order move (ply {pm.Ply}, expected {Ply + 1}) — ignored."); return; }
        if (board.SideToMove == MyColor) return;                                    // only the opponent's move
        var legal = board.FindLegalMove(pm.Move);
        if (legal == null) { AddLog($"Received illegal move {pm.Move.ToUci()} — boards out of sync."); return; }

        _pendingMove = null; MoveNeedsResend = false;                              // their move confirms ours
        if (AwaitingOpponent) AwaitingOpponent = false;
        board.MakeMove(legal.Value); Ply++;
        LastMove = (legal.Value.From, legal.Value.To);
        AddLog($"Opponent played {legal.Value.ToUci()}.");
        await SendMoveAckAsync(Ply);
        if (!CheckForEnd()) UpdateStatus();
    }

    private void HandleMoveAck(ProtocolMessage pm)
    {
        if (pm.GameId != GameId) return;
        if (_pendingMove != null && _pendingMove.Ply == pm.Ply)
        {
            _pendingMove = null; MoveNeedsResend = false;
            AddLog($"Move (ply {pm.Ply}) acknowledged.");
        }
    }

    private async Task HandleResignAsync(ProtocolMessage pm)
    {
        if (pm.GameId != GameId) return;
        await _mesh!.SendTextAsync(ProtocolMessage.EncodeResignAck(GameId), encrypt: false);
        if (Playing && !GameOver) EndGame("Opponent resigned. You win!");
    }

    private Task SendMoveAckAsync(int ply) => _mesh!.SendTextAsync(ProtocolMessage.EncodeAck(GameId, ply), encrypt: false);

    // Best-effort send: a transient device/HTTP failure is logged, never thrown (the pending-send retry loop
    // re-attempts control/move payloads). Keeps a flaky radio from faulting the UI circuit.
    private async Task TrySendAsync(string text, bool encrypt, string what)
    {
        if (_mesh == null) return;
        try { await _mesh.SendTextAsync(text, encrypt: encrypt); }
        catch (Exception ex) { AddLog($"Send {what} failed (will retry): {ex.Message}"); }
    }

    // ---------------------------------------------------------------- Commands (UI)

    public async Task CreateGameAsync(string colorChoice)
    {
        await _gate.WaitAsync();
        try
        {
            if (_mesh == null || Playing) return;
            Color color = colorChoice switch
            {
                "white" => Color.White,
                "black" => Color.Black,
                _ => new Random().Next(2) == 0 ? Color.White : Color.Black,
            };
            string gameId = Guid.NewGuid().ToString("N")[..4];
            BeginGame(color, gameId, null, awaiting: true);
            string payload = ProtocolMessage.EncodeNew(gameId, color);
            // Register the pending send first so the poll loop retries even if this first attempt throws.
            _pendingCreate = new Pending { Payload = payload, Attempts = 1, LastSent = DateTime.UtcNow };
            Status = $"Game [{gameId}] created — waiting for opponent (you are {color}).";
            AddLog(Status);
            await TrySendAsync(payload, encrypt: true, what: "game announcement");
        }
        catch (Exception ex) { Status = $"Create failed: {ex.Message}"; AddLog(Status); }
        finally { _gate.Release(); }
        Notify();
    }

    public async Task JoinGameAsync(string gameId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_mesh == null || Playing) return;
            OpenGame? game; lock (_openGames) _openGames.TryGetValue(gameId, out game);
            if (game == null) return;
            Color myColor = game.CreatorColor.Opposite();
            _mesh.ChannelIndex = game.Channel; ChessChannel = game.Channel;
            MyColor = myColor; GameId = gameId; Joining = true;
            lock (_openGames) _openGames.Remove(gameId);
            string payload = ProtocolMessage.EncodeJoin(gameId, myColor);
            _pendingJoin = new Pending { Payload = payload, Attempts = 1, LastSent = DateTime.UtcNow };
            Status = $"Joining game [{gameId}] as {myColor}…"; AddLog(Status);
            await TrySendAsync(payload, encrypt: true, what: "join request");
        }
        catch (Exception ex) { Joining = false; Status = $"Join failed: {ex.Message}"; AddLog(Status); }
        finally { _gate.Release(); }
        Notify();
    }

    public async Task MoveAsync(int from, int to, PieceType promotion)
    {
        await _gate.WaitAsync();
        try
        {
            if (_mesh == null || Board is not { } board || !MyTurn) return;
            var legal = board.FindLegalMove(new Move(from, to, promotion));
            if (legal == null) { AddLog($"Illegal move {new Move(from, to, promotion).ToUci()}."); return; }

            board.MakeMove(legal.Value); Ply++;
            LastMove = (legal.Value.From, legal.Value.To);
            string payload = ProtocolMessage.EncodeMove(GameId, Ply, legal.Value);
            // Apply locally + register the pending send first; the poll loop retransmits if the send fails.
            _pendingMove = new Pending { Payload = payload, Attempts = 1, LastSent = DateTime.UtcNow, Ply = Ply };
            MoveNeedsResend = false;
            AddLog($"You played {legal.Value.ToUci()} (ply {Ply}).");
            await TrySendAsync(payload, encrypt: true, what: "move");
            if (!CheckForEnd()) UpdateStatus();
        }
        catch (Exception ex) { Status = $"Move error: {ex.Message}"; AddLog(Status); }
        finally { _gate.Release(); }
        Notify();
    }

    public async Task ResendMoveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_mesh == null || _pendingMove == null) return;
            _pendingMove.Attempts = 1; _pendingMove.LastSent = DateTime.UtcNow; MoveNeedsResend = false;
            await TrySendAsync(_pendingMove.Payload, encrypt: true, what: "move");
            AddLog("Move resent.");
        }
        catch (Exception ex) { Status = $"Resend failed: {ex.Message}"; AddLog(Status); }
        finally { _gate.Release(); }
        Notify();
    }

    public async Task ResignAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_mesh == null || !Playing || GameOver) return;
            string payload = ProtocolMessage.EncodeResign(GameId, MyColor);
            _pendingResign = new Pending { Payload = payload, Attempts = 1, LastSent = DateTime.UtcNow };
            AddLog("Resignation sent — waiting for acknowledgement…");
            await TrySendAsync(payload, encrypt: true, what: "resign");
        }
        catch (Exception ex) { Status = $"Resign failed: {ex.Message}"; AddLog(Status); }
        finally { _gate.Release(); }
        Notify();
    }

    // ---------------------------------------------------------------- Helpers

    private void BeginGame(Color color, string gameId, string? fen, bool awaiting)
    {
        MyColor = color; GameId = gameId; AwaitingOpponent = awaiting;
        if (!string.IsNullOrEmpty(fen)) { Board = ChessBoard.FromFen(fen); Ply = Board.HalfMovesPlayed(); }
        else { Board = ChessBoard.CreateStartingPosition(); Ply = 0; }
        LastMove = null; GameOver = false; Playing = true; MoveNeedsResend = false; _pendingMove = null;
        lock (_openGames) _openGames.Remove(gameId);
        UpdateStatus();
    }

    private bool CheckForEnd()
    {
        if (Board is not { } b) return false;
        var s = b.GetStatus();
        if (s == GameStatus.Checkmate) { EndGame($"Checkmate — {b.SideToMove.Opposite()} wins."); return true; }
        if (s == GameStatus.Stalemate) { EndGame("Stalemate — draw."); return true; }
        return false;
    }

    private void EndGame(string msg)
    {
        GameOver = true; Playing = false; AwaitingOpponent = false; Joining = false;
        Status = msg; AddLog(msg);
    }

    private void UpdateStatus()
    {
        if (Board is not { } b) return;
        if (AwaitingOpponent) { Status = $"Waiting for an opponent to join [{GameId}] (you are {MyColor})."; return; }
        string check = b.GetStatus() == GameStatus.Check ? " — check!" : "";
        Status = b.SideToMove == MyColor ? $"Your move ({MyColor}){check}" : $"Opponent's move ({MyColor.Opposite()}){check}";
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }
}
