using System.IO;
using System.Text.Json;
using ChessOverMesh.Chess;

namespace ChessOverMesh.Game;

/// <summary>
/// A saved game: the board position as FEN (which encodes piece placement, whose turn is next,
/// castling, en passant and the move number) plus the colour of the player who saved this copy.
/// The position is self-contained, so a game can be resumed without replaying any move history.
/// </summary>
public sealed class GameSave
{
    public string Fen { get; set; } = "";
    public string MyColor { get; set; } = "white";
}

public static class GameStorage
{
    /// <summary>Default folder for saved games, so both players can use the same filename.</summary>
    public static readonly string DefaultFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChessOverMesh", "saves");

    public static string PathFor(string fileName)
    {
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) fileName += ".json";
        return Path.Combine(DefaultFolder, fileName);
    }

    public static bool Save(string path, GameSave game)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(game, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch { return false; }
    }

    public static GameSave? Load(string path)
    {
        try { return JsonSerializer.Deserialize<GameSave>(File.ReadAllText(path)); }
        catch { return null; }
    }

    /// <summary>Rebuilds the board from the saved FEN; reports the half-move count.</summary>
    public static Board Rebuild(GameSave game, out int ply)
    {
        var board = Board.FromFen(game.Fen);
        ply = board.HalfMovesPlayed();
        return board;
    }
}
