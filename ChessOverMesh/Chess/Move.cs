namespace ChessOverMesh.Chess;

/// <summary>
/// A chess move expressed with from/to squares (0..63, a1 = 0) and an optional
/// promotion piece. Serialized over the mesh using long algebraic / UCI notation
/// (e.g. "e2e4", "e7e8q", "e1g1" for castling).
/// </summary>
public readonly struct Move : IEquatable<Move>
{
    public int From { get; }
    public int To { get; }
    public PieceType Promotion { get; }

    public Move(int from, int to, PieceType promotion = PieceType.None)
    {
        From = from;
        To = to;
        Promotion = promotion;
    }

    public bool Equals(Move other) =>
        From == other.From && To == other.To && Promotion == other.Promotion;

    public override bool Equals(object? obj) => obj is Move m && Equals(m);
    public override int GetHashCode() => (From << 12) | (To << 6) | (int)Promotion;

    public static int FileOf(int square) => square & 7;
    public static int RankOf(int square) => square >> 3;
    public static int SquareOf(int file, int rank) => rank * 8 + file;

    /// <summary>Converts a square index (0..63) to algebraic coordinates, e.g. 0 -> "a1".</summary>
    public static string SquareName(int square)
    {
        char file = (char)('a' + FileOf(square));
        char rank = (char)('1' + RankOf(square));
        return $"{file}{rank}";
    }

    /// <summary>Parses algebraic coordinates ("e4") into a square index, or -1 if invalid.</summary>
    public static int ParseSquare(string s)
    {
        if (s.Length != 2) return -1;
        int file = s[0] - 'a';
        int rank = s[1] - '1';
        if (file is < 0 or > 7 || rank is < 0 or > 7) return -1;
        return SquareOf(file, rank);
    }

    /// <summary>Long algebraic / UCI text for this move, e.g. "e2e4" or "g7g8q".</summary>
    public string ToUci()
    {
        string s = SquareName(From) + SquareName(To);
        if (Promotion != PieceType.None)
            s += PromotionChar(Promotion);
        return s;
    }

    public override string ToString() => ToUci();

    public static bool TryParseUci(string text, out Move move)
    {
        move = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().ToLowerInvariant();
        if (text.Length is < 4 or > 5) return false;

        int from = ParseSquare(text.Substring(0, 2));
        int to = ParseSquare(text.Substring(2, 2));
        if (from < 0 || to < 0) return false;

        PieceType promo = PieceType.None;
        if (text.Length == 5)
        {
            promo = text[4] switch
            {
                'q' => PieceType.Queen,
                'r' => PieceType.Rook,
                'b' => PieceType.Bishop,
                'n' => PieceType.Knight,
                _ => PieceType.None
            };
            if (promo == PieceType.None) return false;
        }

        move = new Move(from, to, promo);
        return true;
    }

    private static char PromotionChar(PieceType t) => t switch
    {
        PieceType.Queen => 'q',
        PieceType.Rook => 'r',
        PieceType.Bishop => 'b',
        PieceType.Knight => 'n',
        _ => '?'
    };
}
