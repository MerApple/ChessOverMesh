using ChessOverMesh.Chess;
using ChessOverMesh.Mesh;

namespace ChessOverMesh.Game;

/// <summary>
/// Drives one game of chess between this player and a remote player, exchanging
/// moves as text messages over a Meshtastic channel.
/// </summary>
public sealed class ChessSession
{
    private readonly MeshtasticHttpClient _mesh;
    private readonly Board _board = Board.CreateStartingPosition();
    private readonly Color _myColor;
    private readonly string _gameId;

    /// <summary>Number of half-moves applied so far (used as the ply counter / dedup key).</summary>
    private int _ply;
    private bool _opponentResigned;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);

    public ChessSession(MeshtasticHttpClient mesh, Color myColor, string gameId)
    {
        _mesh = mesh;
        _myColor = myColor;
        _gameId = gameId;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"\nGame '{_gameId}' — you are {_myColor}.");
        Console.WriteLine("Enter moves in coordinate notation (e.g. e2e4, g7g8q). Type 'help' for commands.");
        PrintBoard();

        while (!ct.IsCancellationRequested)
        {
            // Pull in anything the opponent sent since last loop.
            await PullOpponentMovesAsync(ct);

            if (_opponentResigned)
            {
                Console.WriteLine("\nOpponent resigned. You win!");
                return;
            }

            var status = _board.GetStatus();
            if (IsGameOver(status)) { AnnounceEnd(status); return; }

            if (_board.SideToMove == _myColor)
                await HandleLocalTurnAsync(ct);
            else
                await WaitForOpponentAsync(ct);
        }
    }

    // ---- Local turn ------------------------------------------------------------------

    private async Task HandleLocalTurnAsync(CancellationToken ct)
    {
        while (true)
        {
            Console.Write($"\n{_myColor} to move > ");
            string? line = Console.ReadLine();
            if (line == null) { return; }
            line = line.Trim();
            if (line.Length == 0) continue;

            switch (line.ToLowerInvariant())
            {
                case "help":
                    PrintHelp();
                    continue;
                case "board":
                    PrintBoard();
                    continue;
                case "moves":
                    PrintLegalMoves();
                    continue;
                case "quit":
                case "exit":
                    throw new OperationCanceledException();
                case "resign":
                    await _mesh.SendTextAsync(ProtocolMessage.EncodeResign(_gameId, _myColor), ct: ct);
                    Console.WriteLine("You resigned. Opponent wins.");
                    throw new OperationCanceledException();
            }

            // Normalize separators like "e2-e4" or "e2 e4".
            string token = new string(line.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (!Move.TryParseUci(token, out var requested))
            {
                Console.WriteLine("  ? Couldn't parse that. Use coordinate notation like e2e4 (or 'help').");
                continue;
            }

            var legal = _board.FindLegalMove(requested);
            if (legal == null)
            {
                Console.WriteLine("  ? Illegal move. Type 'moves' to list legal moves.");
                continue;
            }

            _board.MakeMove(legal.Value);
            _ply++;
            await _mesh.SendTextAsync(ProtocolMessage.EncodeMove(_gameId, _ply, legal.Value), ct: ct);
            Console.WriteLine($"  -> sent {legal.Value.ToUci()} (ply {_ply})");
            PrintBoard();
            return;
        }
    }

    // ---- Opponent turn ---------------------------------------------------------------

    private async Task WaitForOpponentAsync(CancellationToken ct)
    {
        Console.WriteLine($"\nWaiting for {_myColor.Opposite()} to move...");
        while (!ct.IsCancellationRequested)
        {
            bool progressed = await PullOpponentMovesAsync(ct);
            if (progressed || _opponentResigned) return;
            if (IsGameOver(_board.GetStatus())) return;
            await Task.Delay(PollInterval, ct);
        }
    }

    /// <summary>Applies any newly-received, in-order opponent moves. Returns true if state advanced.</summary>
    private async Task<bool> PullOpponentMovesAsync(CancellationToken ct)
    {
        bool advanced = false;
        List<MeshTextMessage> incoming;
        try
        {
            incoming = await _mesh.ReceiveTextMessagesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  (mesh receive error: {ex.Message})");
            return false;
        }

        foreach (var msg in incoming)
        {
            if (!ProtocolMessage.TryParse(msg.Text, out var pm)) continue;
            if (pm.GameId != _gameId) continue;

            if (pm.Kind == MessageKind.Resign)
            {
                _opponentResigned = true;
                return advanced;
            }

            if (pm.Kind != MessageKind.Move) continue;

            // Duplicate or stale (mesh can deliver repeats / out of order).
            if (pm.Ply <= _ply) continue;
            if (pm.Ply != _ply + 1)
            {
                Console.WriteLine($"  (out-of-order move ply {pm.Ply}, expected {_ply + 1} — ignoring)");
                continue;
            }

            // It must be the opponent's turn for us to accept their move.
            if (_board.SideToMove == _myColor) continue;

            var legal = _board.FindLegalMove(pm.Move);
            if (legal == null)
            {
                Console.WriteLine($"  (received illegal move {pm.Move.ToUci()} — boards may be out of sync)");
                continue;
            }

            _board.MakeMove(legal.Value);
            _ply++;
            advanced = true;
            Console.WriteLine($"\nOpponent played {legal.Value.ToUci()} (ply {_ply}).");
            PrintBoard();
        }
        return advanced;
    }

    // ---- Helpers ---------------------------------------------------------------------

    private static bool IsGameOver(GameStatus s) =>
        s is GameStatus.Checkmate or GameStatus.Stalemate;

    private void AnnounceEnd(GameStatus status)
    {
        if (status == GameStatus.Checkmate)
        {
            // Side to move is checkmated, so the other side won.
            Color winner = _board.SideToMove.Opposite();
            Console.WriteLine($"\nCheckmate! {winner} wins.");
        }
        else if (status == GameStatus.Stalemate)
        {
            Console.WriteLine("\nStalemate — draw.");
        }
    }

    private void PrintBoard()
    {
        Console.WriteLine(_board.Render(_myColor));
        var status = _board.GetStatus();
        if (status == GameStatus.Check)
            Console.WriteLine($"  {_board.SideToMove} is in check.");
    }

    private void PrintLegalMoves()
    {
        var moves = _board.GenerateLegalMoves().Select(m => m.ToUci()).OrderBy(s => s).ToList();
        Console.WriteLine($"  Legal moves ({moves.Count}): {string.Join(" ", moves)}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
          Commands:
            e2e4        make a move (coordinate notation; add q/r/b/n to promote, e.g. e7e8q)
            board       redraw the board
            moves       list all legal moves
            resign      resign the game
            help        show this help
            quit        leave
        """);
    }
}
