using System.Text;

namespace ChessOverMesh.Chess;

public readonly struct Piece
{
    public PieceType Type { get; }
    public Color Color { get; }
    public Piece(PieceType type, Color color) { Type = type; Color = color; }
    public bool IsEmpty => Type == PieceType.None;
    public static readonly Piece Empty = new(PieceType.None, Color.White);
}

/// <summary>
/// A full-rules chess board: tracks piece placement, side to move, castling rights
/// and the en-passant target, and generates only fully-legal moves.
/// </summary>
public sealed class Board
{
    private readonly Piece[] _squares = new Piece[64];

    public Color SideToMove { get; private set; } = Color.White;
    public bool WhiteCanCastleKingside { get; private set; } = true;
    public bool WhiteCanCastleQueenside { get; private set; } = true;
    public bool BlackCanCastleKingside { get; private set; } = true;
    public bool BlackCanCastleQueenside { get; private set; } = true;
    /// <summary>Square that can be captured en-passant this move, or -1.</summary>
    public int EnPassantTarget { get; private set; } = -1;
    public int FullMoveNumber { get; private set; } = 1;

    public Piece this[int square] => _squares[square];

    public static Board CreateStartingPosition()
    {
        var b = new Board();
        PieceType[] back = { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
                             PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook };
        for (int file = 0; file < 8; file++)
        {
            b._squares[Move.SquareOf(file, 0)] = new Piece(back[file], Color.White);
            b._squares[Move.SquareOf(file, 1)] = new Piece(PieceType.Pawn, Color.White);
            b._squares[Move.SquareOf(file, 6)] = new Piece(PieceType.Pawn, Color.Black);
            b._squares[Move.SquareOf(file, 7)] = new Piece(back[file], Color.Black);
        }
        return b;
    }

    public Board Clone()
    {
        var b = new Board();
        Array.Copy(_squares, b._squares, 64);
        b.SideToMove = SideToMove;
        b.WhiteCanCastleKingside = WhiteCanCastleKingside;
        b.WhiteCanCastleQueenside = WhiteCanCastleQueenside;
        b.BlackCanCastleKingside = BlackCanCastleKingside;
        b.BlackCanCastleQueenside = BlackCanCastleQueenside;
        b.EnPassantTarget = EnPassantTarget;
        b.FullMoveNumber = FullMoveNumber;
        return b;
    }

    // ---- FEN (board snapshot for transmission / save) --------------------------------

    private static char PieceChar(Piece p)
    {
        char c = p.Type switch
        {
            PieceType.Pawn => 'p', PieceType.Knight => 'n', PieceType.Bishop => 'b',
            PieceType.Rook => 'r', PieceType.Queen => 'q', PieceType.King => 'k', _ => '?'
        };
        return p.Color == Color.White ? char.ToUpperInvariant(c) : c;
    }

    /// <summary>Serializes the full position (placement, side, castling, en passant, move number) as FEN.</summary>
    public string ToFen()
    {
        var sb = new StringBuilder();
        for (int rank = 7; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < 8; file++)
            {
                var p = _squares[Move.SquareOf(file, rank)];
                if (p.IsEmpty) { empty++; continue; }
                if (empty > 0) { sb.Append(empty); empty = 0; }
                sb.Append(PieceChar(p));
            }
            if (empty > 0) sb.Append(empty);
            if (rank > 0) sb.Append('/');
        }
        sb.Append(SideToMove == Color.White ? " w " : " b ");
        string castle = (WhiteCanCastleKingside ? "K" : "") + (WhiteCanCastleQueenside ? "Q" : "")
                      + (BlackCanCastleKingside ? "k" : "") + (BlackCanCastleQueenside ? "q" : "");
        sb.Append(castle.Length == 0 ? "-" : castle);
        sb.Append(' ');
        sb.Append(EnPassantTarget >= 0 ? Move.SquareName(EnPassantTarget) : "-");
        sb.Append(" 0 ").Append(FullMoveNumber);   // halfmove clock isn't tracked here
        return sb.ToString();
    }

    /// <summary>Reconstructs a board from FEN. Throws FormatException on malformed input.</summary>
    public static Board FromFen(string fen)
    {
        var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new FormatException("FEN needs at least placement and side fields.");

        var b = new Board();
        var ranks = parts[0].Split('/');
        if (ranks.Length != 8) throw new FormatException("FEN placement must have 8 ranks.");
        for (int r = 0; r < 8; r++)
        {
            int rank = 7 - r;   // FEN lists rank 8 first
            int file = 0;
            foreach (char ch in ranks[r])
            {
                if (char.IsDigit(ch)) { file += ch - '0'; continue; }
                if (file > 7) throw new FormatException("FEN rank overflows.");
                PieceType type = char.ToLowerInvariant(ch) switch
                {
                    'p' => PieceType.Pawn, 'n' => PieceType.Knight, 'b' => PieceType.Bishop,
                    'r' => PieceType.Rook, 'q' => PieceType.Queen, 'k' => PieceType.King,
                    _ => throw new FormatException($"Bad FEN piece '{ch}'."),
                };
                Color color = char.IsUpper(ch) ? Color.White : Color.Black;
                b._squares[Move.SquareOf(file, rank)] = new Piece(type, color);
                file++;
            }
        }

        b.SideToMove = parts[1] == "b" ? Color.Black : Color.White;
        string castle = parts.Length > 2 ? parts[2] : "-";
        b.WhiteCanCastleKingside = castle.Contains('K');
        b.WhiteCanCastleQueenside = castle.Contains('Q');
        b.BlackCanCastleKingside = castle.Contains('k');
        b.BlackCanCastleQueenside = castle.Contains('q');
        b.EnPassantTarget = parts.Length > 3 && parts[3] != "-" ? Move.ParseSquare(parts[3]) : -1;
        b.FullMoveNumber = parts.Length > 5 && int.TryParse(parts[5], out int fmn) && fmn > 0 ? fmn : 1;
        return b;
    }

    /// <summary>Half-moves played so far (0 from the start), derived from the move number and side.</summary>
    public int HalfMovesPlayed() => (FullMoveNumber - 1) * 2 + (SideToMove == Color.Black ? 1 : 0);

    // ---- Move legality / application -------------------------------------------------

    /// <summary>Returns the fully-legal move matching the requested from/to/promotion, or null.</summary>
    public Move? FindLegalMove(Move requested)
    {
        foreach (var m in GenerateLegalMoves())
        {
            if (m.From == requested.From && m.To == requested.To)
            {
                // Match promotion if the candidate is a promotion move.
                if (IsPromotionMove(m))
                {
                    if (requested.Promotion == PieceType.None && m.Promotion == PieceType.Queen)
                        return m; // default promotion to queen
                    if (m.Promotion == requested.Promotion)
                        return m;
                }
                else
                {
                    return m;
                }
            }
        }
        return null;
    }

    public bool IsLegal(Move requested) => FindLegalMove(requested) != null;

    private bool IsPromotionMove(Move m)
    {
        var p = _squares[m.From];
        if (p.Type != PieceType.Pawn) return false;
        int toRank = Move.RankOf(m.To);
        return toRank == 7 || toRank == 0;
    }

    /// <summary>Applies a move assumed to already be legal, advancing the side to move.</summary>
    public void MakeMove(Move move)
    {
        Piece moving = _squares[move.From];
        Color me = moving.Color;
        int newEnPassant = -1;

        // En-passant capture: remove the pawn that sits behind the target square.
        if (moving.Type == PieceType.Pawn && move.To == EnPassantTarget && _squares[move.To].IsEmpty)
        {
            int capturedSquare = me == Color.White ? move.To - 8 : move.To + 8;
            _squares[capturedSquare] = Piece.Empty;
        }

        // Castling: relocate the rook.
        if (moving.Type == PieceType.King && Math.Abs(Move.FileOf(move.To) - Move.FileOf(move.From)) == 2)
        {
            int rank = Move.RankOf(move.From);
            if (Move.FileOf(move.To) == 6) // kingside
            {
                _squares[Move.SquareOf(5, rank)] = _squares[Move.SquareOf(7, rank)];
                _squares[Move.SquareOf(7, rank)] = Piece.Empty;
            }
            else // queenside
            {
                _squares[Move.SquareOf(3, rank)] = _squares[Move.SquareOf(0, rank)];
                _squares[Move.SquareOf(0, rank)] = Piece.Empty;
            }
        }

        // Double pawn push sets the en-passant target.
        if (moving.Type == PieceType.Pawn && Math.Abs(Move.RankOf(move.To) - Move.RankOf(move.From)) == 2)
            newEnPassant = me == Color.White ? move.From + 8 : move.From - 8;

        // Move the piece (handling promotion).
        Piece placed = moving;
        if (moving.Type == PieceType.Pawn && (Move.RankOf(move.To) == 7 || Move.RankOf(move.To) == 0))
            placed = new Piece(move.Promotion == PieceType.None ? PieceType.Queen : move.Promotion, me);

        _squares[move.To] = placed;
        _squares[move.From] = Piece.Empty;

        UpdateCastlingRights(move, moving);

        EnPassantTarget = newEnPassant;
        if (me == Color.Black) FullMoveNumber++;
        SideToMove = me.Opposite();
    }

    private void UpdateCastlingRights(Move move, Piece moving)
    {
        // King move loses both rights for that color.
        if (moving.Type == PieceType.King)
        {
            if (moving.Color == Color.White) { WhiteCanCastleKingside = false; WhiteCanCastleQueenside = false; }
            else { BlackCanCastleKingside = false; BlackCanCastleQueenside = false; }
        }

        // A rook leaving (or being captured on) a corner removes that side's right.
        void TouchCorner(int square)
        {
            switch (square)
            {
                case 0: WhiteCanCastleQueenside = false; break;   // a1
                case 7: WhiteCanCastleKingside = false; break;    // h1
                case 56: BlackCanCastleQueenside = false; break;  // a8
                case 63: BlackCanCastleKingside = false; break;   // h8
            }
        }
        TouchCorner(move.From);
        TouchCorner(move.To);
    }

    // ---- Status ----------------------------------------------------------------------

    public GameStatus GetStatus()
    {
        bool inCheck = IsInCheck(SideToMove);
        bool anyMove = GenerateLegalMoves().Count > 0;
        if (!anyMove)
            return inCheck ? GameStatus.Checkmate : GameStatus.Stalemate;
        return inCheck ? GameStatus.Check : GameStatus.Ongoing;
    }

    public bool IsInCheck(Color color)
    {
        int king = FindKing(color);
        return king >= 0 && IsSquareAttacked(king, color.Opposite());
    }

    private int FindKing(Color color)
    {
        for (int sq = 0; sq < 64; sq++)
            if (_squares[sq].Type == PieceType.King && _squares[sq].Color == color)
                return sq;
        return -1;
    }

    // ---- Move generation -------------------------------------------------------------

    public List<Move> GenerateLegalMoves()
    {
        var legal = new List<Move>();
        Color us = SideToMove;
        foreach (var m in GeneratePseudoLegalMoves(us))
        {
            var copy = Clone();
            copy.MakeMove(m);
            // A move is legal only if it does not leave our own king in check.
            if (!copy.IsInCheck(us))
                legal.Add(m);
        }
        return legal;
    }

    private IEnumerable<Move> GeneratePseudoLegalMoves(Color color)
    {
        for (int sq = 0; sq < 64; sq++)
        {
            Piece p = _squares[sq];
            if (p.IsEmpty || p.Color != color) continue;
            switch (p.Type)
            {
                case PieceType.Pawn: foreach (var m in PawnMoves(sq, color)) yield return m; break;
                case PieceType.Knight: foreach (var m in StepMoves(sq, color, KnightOffsets)) yield return m; break;
                case PieceType.King: foreach (var m in KingMoves(sq, color)) yield return m; break;
                case PieceType.Bishop: foreach (var m in SlideMoves(sq, color, BishopDirs)) yield return m; break;
                case PieceType.Rook: foreach (var m in SlideMoves(sq, color, RookDirs)) yield return m; break;
                case PieceType.Queen: foreach (var m in SlideMoves(sq, color, QueenDirs)) yield return m; break;
            }
        }
    }

    private static readonly (int df, int dr)[] KnightOffsets =
        { (1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2) };
    private static readonly (int df, int dr)[] KingOffsets =
        { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) };
    private static readonly (int df, int dr)[] BishopDirs = { (1, 1), (1, -1), (-1, 1), (-1, -1) };
    private static readonly (int df, int dr)[] RookDirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    private static readonly (int df, int dr)[] QueenDirs =
        { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    private IEnumerable<Move> StepMoves(int from, Color color, (int df, int dr)[] offsets)
    {
        int file = Move.FileOf(from), rank = Move.RankOf(from);
        foreach (var (df, dr) in offsets)
        {
            int nf = file + df, nr = rank + dr;
            if (nf is < 0 or > 7 || nr is < 0 or > 7) continue;
            int to = Move.SquareOf(nf, nr);
            if (_squares[to].IsEmpty || _squares[to].Color != color)
                yield return new Move(from, to);
        }
    }

    private IEnumerable<Move> SlideMoves(int from, Color color, (int df, int dr)[] dirs)
    {
        int file = Move.FileOf(from), rank = Move.RankOf(from);
        foreach (var (df, dr) in dirs)
        {
            int nf = file + df, nr = rank + dr;
            while (nf is >= 0 and <= 7 && nr is >= 0 and <= 7)
            {
                int to = Move.SquareOf(nf, nr);
                if (_squares[to].IsEmpty)
                {
                    yield return new Move(from, to);
                }
                else
                {
                    if (_squares[to].Color != color) yield return new Move(from, to);
                    break;
                }
                nf += df; nr += dr;
            }
        }
    }

    private IEnumerable<Move> PawnMoves(int from, Color color)
    {
        int dir = color == Color.White ? 1 : -1;
        int startRank = color == Color.White ? 1 : 6;
        int promoRank = color == Color.White ? 7 : 0;
        int file = Move.FileOf(from), rank = Move.RankOf(from);

        // Single / double push.
        int oneRank = rank + dir;
        if (oneRank is >= 0 and <= 7)
        {
            int one = Move.SquareOf(file, oneRank);
            if (_squares[one].IsEmpty)
            {
                foreach (var m in WithPromotions(from, one, oneRank == promoRank)) yield return m;
                if (rank == startRank)
                {
                    int two = Move.SquareOf(file, rank + 2 * dir);
                    if (_squares[two].IsEmpty)
                        yield return new Move(from, two);
                }
            }
        }

        // Captures (including en-passant).
        foreach (int cf in new[] { file - 1, file + 1 })
        {
            if (cf is < 0 or > 7) continue;
            int cr = rank + dir;
            if (cr is < 0 or > 7) continue;
            int to = Move.SquareOf(cf, cr);
            bool isEnPassant = to == EnPassantTarget;
            bool enemyThere = !_squares[to].IsEmpty && _squares[to].Color != color;
            if (enemyThere || isEnPassant)
                foreach (var m in WithPromotions(from, to, cr == promoRank)) yield return m;
        }
    }

    private static IEnumerable<Move> WithPromotions(int from, int to, bool promote)
    {
        if (!promote)
        {
            yield return new Move(from, to);
            yield break;
        }
        yield return new Move(from, to, PieceType.Queen);
        yield return new Move(from, to, PieceType.Rook);
        yield return new Move(from, to, PieceType.Bishop);
        yield return new Move(from, to, PieceType.Knight);
    }

    private IEnumerable<Move> KingMoves(int from, Color color)
    {
        foreach (var m in StepMoves(from, color, KingOffsets)) yield return m;

        // Castling — squares empty, rights intact, king not in/through check.
        int rank = Move.RankOf(from);
        bool kingside = color == Color.White ? WhiteCanCastleKingside : BlackCanCastleKingside;
        bool queenside = color == Color.White ? WhiteCanCastleQueenside : BlackCanCastleQueenside;
        Color enemy = color.Opposite();

        if (Move.FileOf(from) == 4 && !IsSquareAttacked(from, enemy))
        {
            if (kingside &&
                _squares[Move.SquareOf(5, rank)].IsEmpty &&
                _squares[Move.SquareOf(6, rank)].IsEmpty &&
                !IsSquareAttacked(Move.SquareOf(5, rank), enemy) &&
                !IsSquareAttacked(Move.SquareOf(6, rank), enemy))
            {
                yield return new Move(from, Move.SquareOf(6, rank));
            }
            if (queenside &&
                _squares[Move.SquareOf(3, rank)].IsEmpty &&
                _squares[Move.SquareOf(2, rank)].IsEmpty &&
                _squares[Move.SquareOf(1, rank)].IsEmpty &&
                !IsSquareAttacked(Move.SquareOf(3, rank), enemy) &&
                !IsSquareAttacked(Move.SquareOf(2, rank), enemy))
            {
                yield return new Move(from, Move.SquareOf(2, rank));
            }
        }
    }

    /// <summary>True if <paramref name="square"/> is attacked by any piece of <paramref name="by"/>.</summary>
    public bool IsSquareAttacked(int square, Color by)
    {
        int tf = Move.FileOf(square), tr = Move.RankOf(square);

        // Pawn attacks: a pawn of color `by` attacks diagonally forward.
        int pawnDir = by == Color.White ? 1 : -1;
        foreach (int df in new[] { -1, 1 })
        {
            int sf = tf + df, sr = tr - pawnDir; // square a `by` pawn would sit on
            if (sf is >= 0 and <= 7 && sr is >= 0 and <= 7)
            {
                Piece p = _squares[Move.SquareOf(sf, sr)];
                if (p.Type == PieceType.Pawn && p.Color == by) return true;
            }
        }

        // Knights.
        foreach (var (df, dr) in KnightOffsets)
        {
            int sf = tf + df, sr = tr + dr;
            if (sf is < 0 or > 7 || sr is < 0 or > 7) continue;
            Piece p = _squares[Move.SquareOf(sf, sr)];
            if (p.Type == PieceType.Knight && p.Color == by) return true;
        }

        // Kings (adjacent).
        foreach (var (df, dr) in KingOffsets)
        {
            int sf = tf + df, sr = tr + dr;
            if (sf is < 0 or > 7 || sr is < 0 or > 7) continue;
            Piece p = _squares[Move.SquareOf(sf, sr)];
            if (p.Type == PieceType.King && p.Color == by) return true;
        }

        // Sliding: bishops/queens on diagonals, rooks/queens on ranks/files.
        if (SliderAttacks(tf, tr, by, BishopDirs, PieceType.Bishop)) return true;
        if (SliderAttacks(tf, tr, by, RookDirs, PieceType.Rook)) return true;

        return false;
    }

    private bool SliderAttacks(int tf, int tr, Color by, (int df, int dr)[] dirs, PieceType slider)
    {
        foreach (var (df, dr) in dirs)
        {
            int nf = tf + df, nr = tr + dr;
            while (nf is >= 0 and <= 7 && nr is >= 0 and <= 7)
            {
                Piece p = _squares[Move.SquareOf(nf, nr)];
                if (!p.IsEmpty)
                {
                    if (p.Color == by && (p.Type == slider || p.Type == PieceType.Queen))
                        return true;
                    break;
                }
                nf += df; nr += dr;
            }
        }
        return false;
    }

    // ---- Rendering -------------------------------------------------------------------

    /// <summary>ASCII board from the given color's perspective.</summary>
    public string Render(Color perspective = Color.White)
    {
        var sb = new StringBuilder();
        bool whiteView = perspective == Color.White;
        sb.AppendLine();
        for (int r = 0; r < 8; r++)
        {
            int rank = whiteView ? 7 - r : r;
            sb.Append($"  {rank + 1} ");
            for (int f = 0; f < 8; f++)
            {
                int file = whiteView ? f : 7 - f;
                Piece p = _squares[Move.SquareOf(file, rank)];
                sb.Append(' ');
                sb.Append(Glyph(p));
            }
            sb.AppendLine();
        }
        sb.Append("    ");
        for (int f = 0; f < 8; f++)
        {
            int file = whiteView ? f : 7 - f;
            sb.Append(' ');
            sb.Append((char)('a' + file));
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private static char Glyph(Piece p)
    {
        if (p.IsEmpty) return '.';
        char c = p.Type switch
        {
            PieceType.Pawn => 'p',
            PieceType.Knight => 'n',
            PieceType.Bishop => 'b',
            PieceType.Rook => 'r',
            PieceType.Queen => 'q',
            PieceType.King => 'k',
            _ => '?'
        };
        return p.Color == Color.White ? char.ToUpperInvariant(c) : c;
    }
}
