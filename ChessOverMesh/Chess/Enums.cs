namespace ChessOverMesh.Chess;

public enum PieceType
{
    None = 0,
    Pawn,
    Knight,
    Bishop,
    Rook,
    Queen,
    King
}

public enum Color
{
    White = 0,
    Black = 1
}

public static class ColorExtensions
{
    public static Color Opposite(this Color c) => c == Color.White ? Color.Black : Color.White;
}

/// <summary>Outcome of applying a move to the board.</summary>
public enum GameStatus
{
    Ongoing,
    Check,
    Checkmate,
    Stalemate
}
