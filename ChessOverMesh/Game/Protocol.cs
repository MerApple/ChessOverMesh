using System.Text;
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
    public const string ChatResendPrefix = "â†» ";

    /// <summary>Delimiter for the optional self-destruct (auto-delete) header a sender can prepend to a chat
    /// message: <c>\x01&lt;seconds&gt;\x01&lt;body&gt;</c>. Plain chat has no envelope, so the sender's chosen
    /// lifetime rides in the text itself (hidden by the app's AES layer when a channel key is set). It's a
    /// cooperative hint â€” a stock Meshtastic client that doesn't understand the header just shows the raw text.
    /// SOH (U+0001) never appears in ordinary chat and is a single UTF-8 byte, so the header is ~5â€“7 bytes.</summary>
    private const char TtlMark = '\u0001';

    /// <summary>Wraps <paramref name="body"/> with a self-destruct header carrying <paramref name="seconds"/>
    /// (the sender-chosen lifetime). Returns the body unchanged when <paramref name="seconds"/> is not positive.</summary>
    public static string EncodeChatTtl(int seconds, string body) =>
        seconds > 0 ? $"{TtlMark}{seconds}{TtlMark}{body}" : body;

    /// <summary>Parses a self-destruct header off <paramref name="text"/>. On success sets
    /// <paramref name="seconds"/> to the sender-chosen lifetime and <paramref name="body"/> to the remaining
    /// message; on failure returns false with <paramref name="body"/> = the original text and seconds = 0.</summary>
    public static bool TryDecodeChatTtl(string text, out int seconds, out string body)
    {
        seconds = 0;
        body = text;
        if (text.Length < 2 || text[0] != TtlMark) return false;
        int end = text.IndexOf(TtlMark, 1);
        if (end < 2) return false;   // need at least one digit between the marks
        if (!int.TryParse(text.AsSpan(1, end - 1), out var s) || s <= 0) return false;
        seconds = s;
        body = text.Substring(end + 1);
        return true;
    }

    // ---- Long-message chunking ----------------------------------------------------------------------------
    // When "split long messages" is on for a channel, a chat message that doesn't fit one packet is encrypted
    // as a whole (if the channel has an app key), then the ciphertext (or plaintext) is sliced into parts, and
    // each part is transmitted as its own packet carrying a SHORT PLAINTEXT header that stays OUTSIDE the AES
    // layer: <STX><groupId><STX><part><STX><total><STX><bodySlice>. The receiver peels the header off BEFORE
    // decrypting (the reverse of the TTL header), buffers the slices, concatenates them in order and decrypts
    // the whole — so the sequence markers are readable on the wire while the body stays encrypted. STX (U+0002)
    // never appears in ordinary chat or in AES base64, so it can't collide with real content.

    /// <summary>Delimiter for the chunk header (STX, U+0002) — a single-byte control char absent from chat and
    /// from base64, so it unambiguously frames the plaintext sequence markers around an encrypted body slice.</summary>
    private const char ChunkMark = (char)2;

    /// <summary>Hard cap on the number of parts a single message may split into (keeps one long paste from
    /// hogging mesh airtime). A message that would need more parts than this is refused, not sent.</summary>
    public const int MaxChatChunks = 5;

    /// <summary>Frames one slice of a split message: <c>&lt;STX&gt;groupId&lt;STX&gt;part&lt;STX&gt;total&lt;STX&gt;bodySlice</c>.
    /// <paramref name="part"/> is 1-based. The header is plaintext even when <paramref name="bodySlice"/> is ciphertext.</summary>
    public static string EncodeChatChunk(string groupId, int part, int total, string bodySlice) =>
        $"{ChunkMark}{groupId}{ChunkMark}{part}{ChunkMark}{total}{ChunkMark}{bodySlice}";

    /// <summary>Parses a chunk header off <paramref name="text"/>. On success returns the group id (correlates the
    /// parts of one message), this part's 1-based index, the total part count, and the body slice. Returns false
    /// (leaving outputs empty) for anything that isn't a chunk — i.e. all ordinary chat and protocol traffic.</summary>
    public static bool TryDecodeChatChunk(string text, out string groupId, out int part, out int total, out string bodySlice)
    {
        groupId = ""; part = 0; total = 0; bodySlice = "";
        if (text.Length < 8 || text[0] != ChunkMark) return false;
        int a = text.IndexOf(ChunkMark, 1); if (a < 2) return false;             // groupId in [1, a)
        int b = text.IndexOf(ChunkMark, a + 1); if (b < 0) return false;         // part in (a, b)
        int c = text.IndexOf(ChunkMark, b + 1); if (c < 0) return false;         // total in (b, c)
        if (!int.TryParse(text.AsSpan(a + 1, b - a - 1), out part) || part < 1) return false;
        if (!int.TryParse(text.AsSpan(b + 1, c - b - 1), out total) || total < 1 || total > MaxChatChunks) return false;
        if (part > total) return false;
        groupId = text.Substring(1, a - 1);
        bodySlice = text.Substring(c + 1);
        return true;
    }

    /// <summary>Splits <paramref name="text"/> into whole-character slices each no larger than
    /// <paramref name="sliceBudget"/> UTF-8 bytes (never splitting a surrogate pair). Returns null when it would
    /// need more than <paramref name="maxChunks"/> slices, or when <paramref name="sliceBudget"/> is not positive.</summary>
    public static List<string>? SliceByBytes(string text, int sliceBudget, int maxChunks)
    {
        if (sliceBudget <= 0) return null;
        var slices = new List<string>();
        var sb = new StringBuilder();
        int bytes = 0;
        for (int i = 0; i < text.Length;)
        {
            int adv = char.IsSurrogatePair(text, i) ? 2 : 1;   // keep a surrogate pair whole
            int cb = Encoding.UTF8.GetByteCount(text.AsSpan(i, adv));
            if (bytes + cb > sliceBudget && sb.Length > 0)
            {
                slices.Add(sb.ToString());
                sb.Clear();
                bytes = 0;
                if (slices.Count >= maxChunks) return null;   // still more to place, but no slices left
            }
            sb.Append(text, i, adv);
            bytes += cb;
            i += adv;
        }
        if (sb.Length > 0) slices.Add(sb.ToString());
        return slices.Count == 0 || slices.Count > maxChunks ? null : slices;
    }

    /// <summary>Splits an already-encrypted (or plaintext) <paramref name="payload"/> into framed chunks (each
    /// carrying a sequence header), each no larger than <paramref name="maxWireLen"/> UTF-8 bytes on the wire.
    /// Returns the framed parts, or null when the payload would need more than <paramref name="maxChunks"/> parts
    /// (caller should then refuse to send). <paramref name="groupId"/> ties the parts together on the receiver.</summary>
    public static List<string>? SplitChatChunks(string payload, string groupId, int maxWireLen, int maxChunks = MaxChatChunks)
    {
        // Header overhead upper bound: STX×4 + groupId + part digits + total digits (parts capped at MaxChatChunks,
        // so each index is a single digit, but reserve two apiece for safety).
        int overhead = 4 + groupId.Length + 2 + 2;
        var slices = SliceByBytes(payload, maxWireLen - overhead, maxChunks);
        if (slices == null) return null;

        int totalParts = slices.Count;
        var chunks = new List<string>(totalParts);
        for (int i = 0; i < totalParts; i++)
            chunks.Add(EncodeChatChunk(groupId, i + 1, totalParts, slices[i]));
        return chunks;
    }

    /// <summary>Splits plaintext <paramref name="text"/> into up to <paramref name="maxChunks"/> raw slices, each no
    /// larger than <paramref name="maxWireLen"/> UTF-8 bytes, with NO sequence header. Used for the "headers off"
    /// split mode (only allowed when the channel has no app key): each slice is sent as an ordinary, independent
    /// chat message. Returns null when it would need more than <paramref name="maxChunks"/> slices.</summary>
    public static List<string>? SplitPlainChunks(string text, int maxWireLen, int maxChunks = MaxChatChunks) =>
        SliceByBytes(text, maxWireLen, maxChunks);

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
    /// a saved file) the save's filename â€” joiners must load the same file to join.</summary>
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
