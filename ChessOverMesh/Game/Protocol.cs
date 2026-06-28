using ChessOverMesh.Chess;

namespace ChessOverMesh.Game;

public enum MessageKind { Move, Resign, ResignAck, Ack, ChatAck, Create, CreateAck, Join, Board, Save, SaveAck, Cancel, Ended, Unknown }

/// <summary>
/// A parsed chess protocol message. Messages are plain text so they travel over a
/// normal Meshtastic channel and remain human-readable in the app:
///   CHX|&lt;gameId&gt;|MOVE|&lt;ply&gt;|&lt;uci&gt;     e.g. "CHX|7f3a|MOVE|1|e2e4"
///   CHX|&lt;gameId&gt;|RESIGN
/// The CHX prefix lets us ignore ordinary chatter on the same channel.
/// </summary>
public readonly struct ProtocolMessage
{
    public const string Prefix = "CHX";
    private const char Sep = '|';

    /// <summary>Marker prepended to a channel-chat message when it is retransmitted after no acknowledgement,
    /// so the receiver can tell it's a resend (plain chat has no envelope to carry such a flag).</summary>
    public const string ChatResendPrefix = "↻ ";

    public MessageKind Kind { get; init; }
    public string GameId { get; init; }
    public int Ply { get; init; }
    public Move Move { get; init; }
    /// <summary>For a RESIGN message, which side resigned (null if not specified).</summary>
    public Color? ResignColor { get; init; }
    /// <summary>For a CHATACK message, the packet id of the chat message being acknowledged.</summary>
    public uint ChatPacketId { get; init; }
    /// <summary>For a CHATACK message, the acker's received signal (RSSI/SNR/hops) for the acked chat,
    /// when the channel is configured to report it. Empty when not included.</summary>
    public string AckSignal { get; init; }
    /// <summary>For a SAVE message, the requested filename.</summary>
    public string SaveName { get; init; }
    /// <summary>For a SAVEACK message, whether the opponent saved successfully.</summary>
    public bool SaveOk { get; init; }
    /// <summary>For a NEW (Create) / JOIN / BOARD message, the announcing/joining player's colour.</summary>
    public Color? AnnouncedColor { get; init; }
    /// <summary>For a BOARD message, the position as FEN (the joiner sets up from this).</summary>
    public string Fen { get; init; }

    public static string EncodeMove(string gameId, int ply, Move move) =>
        string.Join(Sep, Prefix, gameId, "MOVE", ply, move.ToUci());

    public static string EncodeResign(string gameId, Color resigning) =>
        string.Join(Sep, Prefix, gameId, "RESIGN", resigning.ToString().ToLowerInvariant());

    /// <summary>Acknowledgement that a RESIGN was received.</summary>
    public static string EncodeResignAck(string gameId) =>
        string.Join(Sep, Prefix, gameId, "RESIGNACK");

    /// <summary>Application-level acknowledgement that the move at <paramref name="ply"/> was received.</summary>
    public static string EncodeAck(string gameId, int ply) =>
        string.Join(Sep, Prefix, gameId, "ACK", ply);

    /// <summary>Acknowledgement that a chat message (identified by its packet id) was received.
    /// <paramref name="signal"/> optionally carries the acker's received RSSI/SNR/hops for that chat
    /// (the channel must not contain '|'); empty omits it.</summary>
    public static string EncodeChatAck(uint chatPacketId, string? signal = null) =>
        string.IsNullOrEmpty(signal)
            ? string.Join(Sep, Prefix, "CHATACK", chatPacketId)
            : string.Join(Sep, Prefix, "CHATACK", chatPacketId, signal);

    /// <summary>Announces a newly created, open game, the creator's colour, and (if the game resumes
    /// a saved file) the save's filename — joiners must load the same file to join.</summary>
    public static string EncodeNew(string gameId, Color creatorColor, string? saveName = null) =>
        string.IsNullOrEmpty(saveName)
            ? string.Join(Sep, Prefix, gameId, "NEW", creatorColor.ToString().ToLowerInvariant())
            : string.Join(Sep, Prefix, gameId, "NEW", creatorColor.ToString().ToLowerInvariant(), saveName);

    /// <summary>Acknowledgement that a new-game announcement was received.</summary>
    public static string EncodeCreateAck(string gameId) =>
        string.Join(Sep, Prefix, gameId, "CREATEACK");

    /// <summary>Announces that a player has joined a game as the given colour.</summary>
    public static string EncodeJoin(string gameId, Color joinerColor) =>
        string.Join(Sep, Prefix, gameId, "JOIN", joinerColor.ToString().ToLowerInvariant());

    /// <summary>The host's reply to a JOIN: confirms entry and carries the current board so the joiner
    /// can play on without a file. <paramref name="joinerColor"/> is the colour the joiner plays.</summary>
    public static string EncodeBoard(string gameId, Color joinerColor, string fen) =>
        string.Join(Sep, Prefix, gameId, "BOARD", joinerColor.ToString().ToLowerInvariant(), fen);

    /// <summary>Requests that the opponent save the game under the given filename and end it.</summary>
    public static string EncodeSave(string gameId, string fileName) =>
        string.Join(Sep, Prefix, gameId, "SAVE", fileName);

    /// <summary>Acknowledges a save request: ok = saved, no = declined/failed.</summary>
    public static string EncodeSaveAck(string gameId, bool ok) =>
        string.Join(Sep, Prefix, gameId, "SAVEACK", ok ? "ok" : "no");

    /// <summary>Notifies the channel that a game has been cancelled (courtesy, not acknowledged).</summary>
    public static string EncodeCancel(string gameId) =>
        string.Join(Sep, Prefix, gameId, "CANCEL");

    /// <summary>Tells the sender of a move that this game is no longer running here (already ended),
    /// so they should end it on their side too.</summary>
    public static string EncodeEnded(string gameId) =>
        string.Join(Sep, Prefix, gameId, "ENDED");

    private static Color? ParseColor(string s) =>
        s.Equals("white", StringComparison.OrdinalIgnoreCase) ? Color.White
        : s.Equals("black", StringComparison.OrdinalIgnoreCase) ? Color.Black
        : null;

    /// <summary>Parses a channel text payload; returns false for anything that isn't our protocol.</summary>
    public static bool TryParse(string text, out ProtocolMessage message)
    {
        message = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split(Sep);
        if (parts.Length < 3 || parts[0] != Prefix) return false;

        // Chat acks aren't game-scoped: CHX|CHATACK|<packetId>
        if (parts[1] == "CHATACK")
        {
            if (!uint.TryParse(parts[2], out uint chatId)) return false;
            string signal = parts.Length >= 4 ? string.Join(Sep, parts[3..]) : "";
            message = new ProtocolMessage { Kind = MessageKind.ChatAck, ChatPacketId = chatId, AckSignal = signal };
            return true;
        }

        string gameId = parts[1];
        switch (parts[2].ToUpperInvariant())
        {
            case "MOVE":
                if (parts.Length < 5) return false;
                if (!int.TryParse(parts[3], out int ply)) return false;
                if (!Move.TryParseUci(parts[4], out var mv)) return false;
                message = new ProtocolMessage { Kind = MessageKind.Move, GameId = gameId, Ply = ply, Move = mv };
                return true;

            case "ACK":
                if (parts.Length < 4 || !int.TryParse(parts[3], out int ackPly)) return false;
                message = new ProtocolMessage { Kind = MessageKind.Ack, GameId = gameId, Ply = ackPly };
                return true;

            case "RESIGN":
                Color? who = parts.Length >= 4 ? ParseColor(parts[3]) : null;
                message = new ProtocolMessage { Kind = MessageKind.Resign, GameId = gameId, ResignColor = who };
                return true;

            case "RESIGNACK":
                message = new ProtocolMessage { Kind = MessageKind.ResignAck, GameId = gameId };
                return true;

            case "NEW":
                message = new ProtocolMessage
                {
                    Kind = MessageKind.Create,
                    GameId = gameId,
                    AnnouncedColor = parts.Length >= 4 ? ParseColor(parts[3]) : null,
                    SaveName = parts.Length >= 5 ? parts[4] : "",
                };
                return true;

            case "CREATEACK":
                message = new ProtocolMessage { Kind = MessageKind.CreateAck, GameId = gameId };
                return true;

            case "JOIN":
                message = new ProtocolMessage
                {
                    Kind = MessageKind.Join,
                    GameId = gameId,
                    AnnouncedColor = parts.Length >= 4 ? ParseColor(parts[3]) : null,
                };
                return true;

            case "BOARD":
                if (parts.Length < 5) return false;
                message = new ProtocolMessage
                {
                    Kind = MessageKind.Board,
                    GameId = gameId,
                    AnnouncedColor = ParseColor(parts[3]),
                    Fen = string.Join(Sep, parts[4..]),   // FEN contains spaces but no '|'
                };
                return true;

            case "SAVE":
                if (parts.Length < 4) return false;
                message = new ProtocolMessage { Kind = MessageKind.Save, GameId = gameId, SaveName = parts[3] };
                return true;

            case "SAVEACK":
                message = new ProtocolMessage
                {
                    Kind = MessageKind.SaveAck,
                    GameId = gameId,
                    SaveOk = parts.Length >= 4 && parts[3].Equals("ok", StringComparison.OrdinalIgnoreCase),
                };
                return true;

            case "CANCEL":
                message = new ProtocolMessage { Kind = MessageKind.Cancel, GameId = gameId };
                return true;

            case "ENDED":
                message = new ProtocolMessage { Kind = MessageKind.Ended, GameId = gameId };
                return true;

            default:
                return false;
        }
    }
}
