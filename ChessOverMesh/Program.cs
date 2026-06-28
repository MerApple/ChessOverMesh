using ChessOverMesh.Chess;
using ChessOverMesh.Game;
using ChessOverMesh.Mesh;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("======================================");
Console.WriteLine(" Chess over Meshtastic (HTTP API)");
Console.WriteLine("======================================");

if (args.Contains("--selftest"))
{
    Console.WriteLine("\nRunning chess engine self-test...");
    return ChessOverMesh.Chess.SelfTest.Run();
}

if (args.Contains("--savetest"))
{
    // Saves are FEN-based: play a few moves, save, reload, and confirm the position survives.
    var src = Board.CreateStartingPosition();
    foreach (var uci in new[] { "e2e4", "e7e5", "g1f3" })
    {
        Move.TryParseUci(uci, out var m);
        src.MakeMove(src.FindLegalMove(m)!.Value);
    }
    var g = new GameSave { Fen = src.ToFen(), MyColor = "white" };
    string path = GameStorage.PathFor("selftest");
    bool saved = GameStorage.Save(path, g);
    var loaded = GameStorage.Load(path);
    var board = GameStorage.Rebuild(loaded!, out int ply);

    bool knightF3 = board[Move.ParseSquare("f3")] is { Type: PieceType.Knight, Color: ChessOverMesh.Chess.Color.White };
    bool pawnE4 = board[Move.ParseSquare("e4")] is { Type: PieceType.Pawn, Color: ChessOverMesh.Chess.Color.White };
    bool pawnE5 = board[Move.ParseSquare("e5")] is { Type: PieceType.Pawn, Color: ChessOverMesh.Chess.Color.Black };
    bool blackToMove = board.SideToMove == ChessOverMesh.Chess.Color.Black;

    Console.WriteLine($"\n  saved+loaded: {(saved && loaded != null ? "OK" : "FAIL")}  (file: {path})");
    Console.WriteLine($"  fen = {loaded!.Fen}");
    Console.WriteLine($"  half-moves = {ply} (expect 3): {(ply == 3 ? "OK" : "FAIL")}");
    Console.WriteLine($"  knight on f3 (white): {(knightF3 ? "OK" : "FAIL")}");
    Console.WriteLine($"  pawns e4(white)/e5(black): {(pawnE4 && pawnE5 ? "OK" : "FAIL")}");
    Console.WriteLine($"  side to move = Black: {(blackToMove ? "OK" : "FAIL")}");
    Console.WriteLine($"  reload preserves FEN: {(board.ToFen() == src.ToFen() ? "OK" : "FAIL")}");
    bool ok = saved && loaded != null && ply == 3 && knightF3 && pawnE4 && pawnE5 && blackToMove
              && board.ToFen() == src.ToFen();
    Console.WriteLine(ok ? "\nSave/load self-test PASSED." : "\nSave/load self-test FAILED.");
    return ok ? 0 : 1;
}

if (args.Contains("--boardtest"))
{
    // The host transmits the position to a joiner via a BOARD message; verify the round-trip.
    var src = Board.CreateStartingPosition();
    foreach (var uci in new[] { "e2e4", "c7c5", "g1f3" })
    {
        Move.TryParseUci(uci, out var m);
        src.MakeMove(src.FindLegalMove(m)!.Value);
    }
    string fen = src.ToFen();
    string wire = ProtocolMessage.EncodeBoard("ab12", ChessOverMesh.Chess.Color.Black, fen);
    bool parsed = ProtocolMessage.TryParse(wire, out var pm);
    var rebuilt = parsed ? Board.FromFen(pm.Fen) : null;

    Console.WriteLine($"\n  encoded: {wire}");
    Console.WriteLine($"  parsed kind/color: {pm.Kind}/{pm.AnnouncedColor}");
    Console.WriteLine($"  fen survives: {(rebuilt?.ToFen() == fen ? "OK" : "FAIL")}");
    bool ok = parsed && pm.Kind == MessageKind.Board && pm.AnnouncedColor == ChessOverMesh.Chess.Color.Black
              && rebuilt != null && rebuilt.ToFen() == fen;
    Console.WriteLine(ok ? "\nBOARD transmit self-test PASSED." : "\nBOARD transmit self-test FAILED.");
    return ok ? 0 : 1;
}

if (args.Contains("--newtest"))
{
    // A game announcement carries the creator's colour and, when resuming a save, the filename.
    string withSave = ProtocolMessage.EncodeNew("ab12", ChessOverMesh.Chess.Color.White, "my game 1");
    string noSave = ProtocolMessage.EncodeNew("cd34", ChessOverMesh.Chess.Color.Black);

    bool p1 = ProtocolMessage.TryParse(withSave, out var m1);
    bool p2 = ProtocolMessage.TryParse(noSave, out var m2);

    Console.WriteLine($"\n  with-save encoded: {withSave}");
    Console.WriteLine($"  parsed kind/color/name: {m1.Kind}/{m1.AnnouncedColor}/'{m1.SaveName}'");
    Console.WriteLine($"  no-save encoded:   {noSave}");
    Console.WriteLine($"  parsed kind/color/name: {m2.Kind}/{m2.AnnouncedColor}/'{m2.SaveName}'");

    bool ok = p1 && m1.Kind == MessageKind.Create && m1.AnnouncedColor == ChessOverMesh.Chess.Color.White
              && m1.SaveName == "my game 1"
              && p2 && m2.Kind == MessageKind.Create && m2.AnnouncedColor == ChessOverMesh.Chess.Color.Black
              && string.IsNullOrEmpty(m2.SaveName);
    Console.WriteLine(ok ? "\nNEW announcement self-test PASSED." : "\nNEW announcement self-test FAILED.");
    return ok ? 0 : 1;
}

if (args.Contains("--cryptotest"))
{
    string plain = "CHX|abcd|MOVE|1|e2e4";
    string enc = AesText.Encrypt(plain, "secret");
    bool roundtrip = AesText.TryDecrypt(enc, "secret", out var dec) && dec == plain;
    bool wrongKey = AesText.TryDecrypt(enc, "wrongkey", out _);
    bool plainPass = AesText.TryDecrypt("not encrypted text", "secret", out _);
    Console.WriteLine($"\n  plaintext : {plain}");
    Console.WriteLine($"  encrypted : {enc}");
    Console.WriteLine($"  round-trip with correct key : {(roundtrip ? "OK" : "FAIL")}");
    Console.WriteLine($"  wrong key rejected          : {(!wrongKey ? "OK" : "FAIL")}");
    Console.WriteLine($"  plaintext passthrough (no decode) : {(!plainPass ? "OK" : "FAIL")}");
    bool ok = roundtrip && !wrongKey && !plainPass;
    Console.WriteLine(ok ? "\nCrypto self-test PASSED." : "\nCrypto self-test FAILED.");
    return ok ? 0 : 1;
}

if (args.Contains("--channels"))
{
    int hi = Array.IndexOf(args, "--channels");
    string dhost = hi + 1 < args.Length ? args[hi + 1] : "";
    if (string.IsNullOrWhiteSpace(dhost)) { Console.Error.WriteLine("Usage: --channels <host>"); return 1; }
    if (!dhost.StartsWith("http", StringComparison.OrdinalIgnoreCase)) dhost = "http://" + dhost;

    Console.WriteLine($"Fetching channels from {dhost}...");
    using var dmesh = new MeshtasticHttpClient(dhost);
    await dmesh.InitializeAsync();
    var chans = dmesh.GetAvailableChannels();
    Console.WriteLine($"Found {chans.Count} channel(s):");
    foreach (var c in chans)
        Console.WriteLine($"  [{c.Index}] {c.DisplayName} ({c.Role})");
    return 0;
}

// Diagnostic: connect to a device and print EVERY incoming text packet on ANY channel (no
// channel filtering, no decryption), so we can see what the radio is actually receiving.
//   --listen <host> [seconds]
if (args.Contains("--listen"))
{
    int hi = Array.IndexOf(args, "--listen");
    string lhost = hi + 1 < args.Length ? args[hi + 1] : "";
    int secs = hi + 2 < args.Length && int.TryParse(args[hi + 2], out var s) ? s : 40;
    if (string.IsNullOrWhiteSpace(lhost)) { Console.Error.WriteLine("Usage: --listen <host> [seconds]"); return 1; }
    if (!lhost.StartsWith("http", StringComparison.OrdinalIgnoreCase)) lhost = "http://" + lhost;

    Console.WriteLine($"Connecting to {lhost}...");
    using var lmesh = new MeshtasticHttpClient(lhost);
    await lmesh.InitializeAsync();
    Console.WriteLine($"  MyNodeNum = !{lmesh.MyNodeNum:x8}");
    Console.WriteLine("  Channels:");
    foreach (var c in lmesh.GetAvailableChannels())
        Console.WriteLine($"    [{c.Index}] '{c.DisplayName}' ({c.Role})");
    Console.WriteLine($"Listening {secs}s for ALL incoming text on ANY channel (no filtering)...");
    var until = DateTime.Now.AddSeconds(secs);
    int seen = 0;
    while (DateTime.Now < until)
    {
        var res = await lmesh.ReceiveAsync();
        foreach (var m in res.Texts)
        {
            seen++;
            Console.WriteLine($"  RX  ch={m.Channel}  from=!{m.FromNode:x8}  rxTime={m.RxTime}  decryptFail={m.DecryptFailed}");
            Console.WriteLine($"      text: {m.Text}");
        }
        if (res.PacketCount == 0) await Task.Delay(1500);
    }
    Console.WriteLine($"Done. {seen} text packet(s) received in {secs}s.");
    return 0;
}

// Diagnostic: send one text message from a device on a given channel index.
//   --send <host> <channelIndex> <text...>
if (args.Contains("--send"))
{
    int hi = Array.IndexOf(args, "--send");
    string shost = hi + 1 < args.Length ? args[hi + 1] : "";
    if (string.IsNullOrWhiteSpace(shost) || hi + 3 >= args.Length)
    { Console.Error.WriteLine("Usage: --send <host> <channelIndex> <text>"); return 1; }
    if (!shost.StartsWith("http", StringComparison.OrdinalIgnoreCase)) shost = "http://" + shost;
    uint chIdx = uint.Parse(args[hi + 2]);
    string text = string.Join(' ', args[(hi + 3)..]);

    using var smesh = new MeshtasticHttpClient(shost, chIdx);
    await smesh.InitializeAsync();
    Console.WriteLine($"Sending on ch={chIdx} from {shost}: '{text}'");
    uint id = await smesh.SendTextAsync(text);
    Console.WriteLine($"Sent. packet id = {id}");
    return 0;
}

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    CliOptions.PrintUsage();
    return 0;
}

// Resolve connection + game settings (prompt for anything not supplied on the command line).
string host = options.Host ?? Prompt("Device base URL (e.g. http://192.168.1.50): ");
if (string.IsNullOrWhiteSpace(host))
{
    Console.Error.WriteLine("A device host is required.");
    return 1;
}
if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase))
    host = "http://" + host;

Color color = options.Color ?? PromptColor();

string gameId = options.GameId ?? PromptGameId(color);

using var mesh = new MeshtasticHttpClient(host, destination: null);

try
{
    Console.WriteLine($"\nConnecting to {host}...");
    await mesh.InitializeAsync();
    Console.WriteLine(mesh.MyNodeNum != 0
        ? $"Connected. This node: {mesh.DescribeNode(mesh.MyNodeNum)} ({mesh.MyNodeNum})."
        : "Connected (node info unavailable — continuing anyway).");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not reach the Meshtastic device at {host}: {ex.Message}");
    Console.Error.WriteLine("Check the device is on WiFi and its HTTP API is enabled, then retry.");
    return 1;
}

// Pick the channel: honor --channel if given, otherwise let the player choose
// from the channels the device actually reported.
mesh.ChannelIndex = SelectChannel(mesh.GetAvailableChannels(), options.Channel);
Console.WriteLine($"Using channel index {mesh.ChannelIndex}.");

Console.WriteLine($"\nShare this game id with your opponent so they join the same game: {gameId}");
Console.WriteLine("(They must run this program with the opposite color and the same game id.)");

var session = new ChessSession(mesh, color, gameId);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await session.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nGame ended.");
}
return 0;

// ---- local helpers -------------------------------------------------------------------

static string Prompt(string label)
{
    Console.Write(label);
    return (Console.ReadLine() ?? string.Empty).Trim();
}

static uint SelectChannel(IReadOnlyList<MeshChannel> channels, uint? requested)
{
    // Explicit --channel always wins.
    if (requested.HasValue)
    {
        var match = channels.FirstOrDefault(c => c.Index == requested.Value);
        if (match.Index == requested.Value)
            Console.WriteLine($"Selected channel {requested.Value}: {match.DisplayName} [{match.Role}]");
        else if (channels.Count > 0)
            Console.WriteLine($"Warning: channel {requested.Value} isn't in the device's enabled list.");
        return requested.Value;
    }

    if (channels.Count == 0)
    {
        // Device didn't report channels; fall back to asking for a raw index.
        string raw = Prompt("Channel index to use [0]: ");
        return uint.TryParse(raw, out uint idx) ? idx : 0u;
    }

    if (channels.Count == 1)
    {
        Console.WriteLine($"Only one channel available: {channels[0].Index} {channels[0].DisplayName} [{channels[0].Role}].");
        return channels[0].Index;
    }

    Console.WriteLine("\nAvailable channels on this device:");
    foreach (var c in channels)
        Console.WriteLine($"  [{c.Index}] {c.DisplayName,-16} {c.Role}");

    while (true)
    {
        string s = Prompt($"Select channel index [{channels[0].Index}]: ");
        if (string.IsNullOrWhiteSpace(s)) return channels[0].Index;
        if (uint.TryParse(s, out uint idx) && channels.Any(c => c.Index == idx))
            return idx;
        Console.WriteLine("Please enter one of the listed channel indices.");
    }
}

static Color PromptColor()
{
    while (true)
    {
        string s = Prompt("Play as (white/black): ").ToLowerInvariant();
        if (s is "w" or "white") return Color.White;
        if (s is "b" or "black") return Color.Black;
        Console.WriteLine("Please type 'white' or 'black'.");
    }
}

static string PromptGameId(Color color)
{
    if (color == Color.White)
    {
        // White starts the game; offer a generated id the player can accept.
        string suggested = GameIdGenerator.New();
        string s = Prompt($"Game id [{suggested}]: ");
        return string.IsNullOrWhiteSpace(s) ? suggested : s;
    }
    string entered = Prompt("Game id (get this from the white player): ");
    return string.IsNullOrWhiteSpace(entered) ? GameIdGenerator.New() : entered;
}

static class GameIdGenerator
{
    public static string New() => Guid.NewGuid().ToString("N").Substring(0, 4);
}

sealed class CliOptions
{
    public string? Host { get; private set; }
    public Color? Color { get; private set; }
    public string? GameId { get; private set; }
    public uint? Channel { get; private set; }
    public bool ShowHelp { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--host" or "-h": o.Host = Next(); break;
                case "--color" or "-c":
                    string? c = Next()?.ToLowerInvariant();
                    o.Color = c is "b" or "black" ? ChessOverMesh.Chess.Color.Black : ChessOverMesh.Chess.Color.White;
                    break;
                case "--game" or "-g": o.GameId = Next(); break;
                case "--channel":
                    if (uint.TryParse(Next(), out uint ch)) o.Channel = ch;
                    break;
                case "--help" or "-?": o.ShowHelp = true; break;
            }
        }
        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
        Usage: ChessOverMesh [--host <url>] [--color white|black] [--game <id>] [--channel <n>]

          --host     Base URL of the Meshtastic device HTTP API (e.g. http://192.168.1.50)
          --color    Which side you play (white or black)
          --game     Shared game id; the white player picks one and shares it
          --channel  Meshtastic channel index to use. If omitted, the program lists
                     the device's channels after connecting and lets you choose.

        Two players each run this against their own Meshtastic node, on the same
        channel, with the same --game id and opposite colors. Moves are exchanged
        as text messages over the mesh.
        """);
    }
}
