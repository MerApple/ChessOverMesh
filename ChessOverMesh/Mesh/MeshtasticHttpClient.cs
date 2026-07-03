using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Meshtastic.Data;
using Meshtastic.Data.MessageFactories;
using Meshtastic.Extensions;
using Meshtastic.Protobufs;

namespace ChessOverMesh.Mesh;

/// <summary>A text message received from the mesh.</summary>
/// <summary>RxTime is the device's receive time (Unix epoch seconds) for the packet, or 0 if the
/// device clock isn't set. It is reported by the radio, not transmitted by the sender.
/// DecryptFailed is true when a channel key is set but the payload couldn't be decrypted with it
/// (foreign/plain/wrong-key traffic) — Text then holds the raw, undecrypted payload.
/// RxRssi/RxSnr are the radio's signal readings for the packet as it arrived (from the last hop);
/// 0/0 means the device didn't report them (e.g. an MQTT-sourced packet).
/// HopLimit is the remaining hop budget; HopStart is the sender's initial budget (0 when the sender's
/// firmware didn't include it, so the hop count can't be derived).
/// RelayNode is the last byte of the node that last relayed the packet to us (recent firmware), or 0
/// when not reported. For a relayed packet (Hops &gt; 0) RxRssi/RxSnr describe that relay→us link.
/// ToNode is the packet's destination: 0xffffffff for a broadcast (channel/group message), or a specific
/// node number for a direct message. Compare it to our own node number to detect a DM addressed to us.</summary>
public readonly record struct MeshTextMessage(
    uint FromNode, uint Channel, string Text, uint PacketId, uint RxTime, bool DecryptFailed = false,
    int RxRssi = 0, float RxSnr = 0, int HopLimit = 0, int HopStart = 0, byte RelayNode = 0, uint ToNode = 0,
    uint ReplyId = 0, bool IsReaction = false, bool ViaMqtt = false)
{
    /// <summary>Hops the packet travelled (HopStart − HopLimit), or null when HopStart is unknown
    /// (older sender firmware) so we can't tell direct from relayed.</summary>
    public int? Hops => HopStart > 0 ? Math.Max(0, HopStart - HopLimit) : (int?)null;

    /// <summary>True when the packet was received directly from the sender (zero hops).</summary>
    public bool IsDirect => Hops == 0;

    /// <summary>True when this packet was addressed to a single node rather than broadcast (i.e. a DM).
    /// Use <see cref="IsDmTo"/> to check it was addressed to us specifically.</summary>
    public bool IsDirectMessage => ToNode != 0 && ToNode != 0xffffffff;

    /// <summary>True when this is a direct message addressed to <paramref name="myNodeNum"/> (us).</summary>
    public bool IsDmTo(uint myNodeNum) => IsDirectMessage && ToNode == myNodeNum;
}

/// <summary>A channel configured on the device. HasKey is true when the channel has a non-empty PSK
/// (i.e. it's encrypted). Psk is that PSK as base64 (empty when none, or when only cached channels are
/// known — the PSK is only populated by a live device read, not the on-disk cache).</summary>
public readonly record struct MeshChannel(uint Index, string Name, string Role, bool HasKey = false, string Psk = "",
    bool UplinkEnabled = false, bool DownlinkEnabled = false, int PositionPrecision = -1)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name;
    public bool IsDisabled => string.Equals(Role, "Disabled", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Outcome of a channel admin write (create/update/disable). Error is null on success.</summary>
public readonly record struct ChannelOpResult(bool Ok, uint Index, string? Error);

/// <summary>Result of draining the radio: received texts, acked packet ids (mesh routing acks — for a
/// broadcast with want_ack the firmware emits one as an implicit ack when it hears the message relayed),
/// and total packets read.</summary>
/// <summary>A mesh acknowledgement of one of our sent packets. FromNode is the node the ack came from
/// (for a broadcast's implicit ack, the relayer that rebroadcast it). RelayNode is the last byte of the
/// last-hop relayer when the firmware reports it (else 0). RxRssi/RxSnr are our reading of that hop.
/// Failed is true when this is a Routing NAK — a delivery FAILURE the firmware reports for a want_ack
/// packet it couldn't deliver (chiefly direct messages, which the firmware retransmits then gives up on);
/// FailReason names the Routing error (e.g. "MaxRetransmit", "NoResponse"). Failed is false for a normal
/// (successful) ack and for a broadcast's implicit relay ack.</summary>
public readonly record struct MeshAck(uint PacketId, uint FromNode, byte RelayNode, int RxRssi, float RxSnr,
    bool Failed = false, string FailReason = "");

/// <summary>A traceroute reply to one of our requests: Node is the node we traced to (the reply's sender),
/// Route is the intermediate node numbers on the way to it (you→node), and RouteBack the intermediates on
/// the return (node→you). Endpoints (you / the node) are not included. RouteBack is empty if the responding
/// firmware didn't report a return path. SnrTowards/SnrBack are the per-link SNR readings (one per hop, in the
/// same order as the links of the full path you→…→node and back), each stored as SNR×4 (so dB = value/4); the
/// firmware uses -128 to mean "unknown". Empty when the firmware didn't report SNR.
/// IsRequest is true for an INCOMING traceroute request aimed at us (the firmware auto-answers it): Node is the
/// requester and the route/SNR lists are empty — it's surfaced only so the app can note that we were traced.</summary>
public readonly record struct MeshTraceroute(uint Node, List<uint> Route, List<int> SnrTowards, List<uint> RouteBack, List<int> SnrBack, bool IsRequest = false)
{
    /// <summary>True when this scaled SNR value (SNR×4) is the firmware's "unknown" marker.</summary>
    public static bool SnrUnknown(int scaled) => scaled <= -128;

    /// <summary>Formats a scaled SNR value (SNR×4) as "9.5 dB", or "?" when unknown.</summary>
    public static string SnrLabel(int scaled) => SnrUnknown(scaled) ? "SNR ?" : $"SNR {scaled / 4.0:0.##} dB";
}

/// <summary>A node's reply to our node-info request: its number and resolved display name (empty if the
/// reply carried no name).</summary>
public readonly record struct MeshNodeInfoReply(uint Node, string Name, bool IsRequest = false);

/// <summary>The latest environment telemetry heard from a node. TemperatureC is °C, RelativeHumidity is %,
/// BarometricPressure is hPa (0 when not reported). RxTime is the device's receive time (epoch s, 0 if
/// unknown). DewPointC is derived from temperature + humidity (Magnus-Tetens), null without humidity —
/// the sender never transmits dew point, so it's computed here exactly as the native app does.</summary>
public readonly record struct MeshEnvironment(float TemperatureC, float RelativeHumidity, float BarometricPressure,
    uint RxTime, DateTime ReceivedAt)
{
    public bool HasHumidity => RelativeHumidity > 0;

    /// <summary>When the reading was received: the device's rx_time (→ local) when it set its clock, else the
    /// local time we processed the packet.</summary>
    public DateTime Timestamp => RxTime != 0 ? DateTimeOffset.FromUnixTimeSeconds(RxTime).LocalDateTime : ReceivedAt;

    public double? DewPointC
    {
        get
        {
            if (RelativeHumidity <= 0) return null;
            const double a = 17.27, b = 237.7;
            double g = (a * TemperatureC) / (b + TemperatureC) + Math.Log(RelativeHumidity / 100.0);
            return (b * g) / (a - g);
        }
    }
}

public readonly record struct MeshReceiveResult(List<MeshTextMessage> Texts, List<MeshAck> Acks, int PacketCount,
    List<MeshTraceroute> Traceroutes, List<MeshNodeInfoReply> NodeInfos, List<uint> Telemetry,
    List<MeshNodeInfoReply> NewNodes, List<MeshPositionReport> Positions, List<MeshNoiseFloor> NoiseFloors);

/// <summary>A position packet heard from another node (its broadcast, or a reply to our request): the node, its
/// name, and the reported decimal-degree coordinates. Surfaced so the UI can note it in the system log.</summary>
public readonly record struct MeshPositionReport(uint Node, string Name, double Latitude, double Longitude);

/// <summary>A node's reported noise floor (LocalStats.noise_floor, dBm) — its measured ambient RF noise level,
/// from a LocalStats telemetry reply (e.g. to <see cref="MeshtasticHttpClient.RequestNoiseFloorAsync"/>).</summary>
public readonly record struct MeshNoiseFloor(uint Node, string Name, int NoiseFloorDbm);

/// <summary>A node's position for the map: number, name, decimal-degree coordinates, and timestamps (epoch
/// seconds, 0 if unknown) for when the node was last heard and when this position was recorded.</summary>
public readonly record struct MeshNodePosition(uint Num, string Name, double Latitude, double Longitude, long LastHeard, long PositionTime);

/// <summary>A node known to the device. Role is its device role (Client, Router, Repeater, Sensor, …), or
/// "" when unknown. LastHeard is when the device last heard from it (epoch seconds, 0 if unknown).</summary>
public readonly record struct MeshNode(uint Num, string LongName, string ShortName, bool IsSelf, string Role = "", long LastHeard = 0, string HwModel = "")
{
    // Device NodeDB flags + mesh distance, surfaced for the node list marker and "show all info".
    public bool IsFavorite { get; init; }
    public bool IsIgnored { get; init; }
    public uint? HopsAway { get; init; }

    public string Display =>
        string.IsNullOrWhiteSpace(LongName) && string.IsNullOrWhiteSpace(ShortName)
            ? $"!{Num:x8}"
            : $"{(string.IsNullOrWhiteSpace(LongName) ? "(no name)" : LongName)}" +
              $"{(string.IsNullOrWhiteSpace(ShortName) ? "" : $" ({ShortName})")} — !{Num:x8}";
}

/// <summary>
/// Talks to a Meshtastic device over its HTTP(S) REST API:
///   PUT  /api/v1/toradio      — submit a serialized ToRadio protobuf
///   GET  /api/v1/fromradio    — drain one serialized FromRadio protobuf per call
/// The wire payloads are the same protobufs the device uses over BLE/Serial/TCP,
/// just without the stream framing (HTTP delimits each message itself).
/// </summary>
public sealed class MeshtasticHttpClient : IDisposable
{
    private readonly IMeshTransport _transport;
    private readonly uint? _destination;
    private readonly DeviceStateContainer _state = new();
    private IReadOnlyList<MeshChannel>? _seededChannels;
    private readonly Dictionary<uint, string> _nameCache = new();   // node num -> name, seeded from disk cache
    private readonly Dictionary<uint, string> _shortNameCache = new();   // node num -> short name, seeded from disk cache
    private readonly Dictionary<uint, bool> _favoriteCache = new();      // node num -> device "favorite" flag
    private readonly Dictionary<uint, bool> _ignoredCache = new();       // node num -> device "ignored" flag
    private readonly Dictionary<uint, uint> _hopsAwayCache = new();      // node num -> hops away (mesh distance)
    private readonly Dictionary<uint, long> _lastHeardCache = new();     // node num -> last-heard (epoch s), standalone
    private readonly Dictionary<uint, (ByteString Key, DateTime When)> _sessionPasskeys = new();   // remote-admin session passkeys captured from admin responses
    private readonly Dictionary<uint, string> _roleCache = new();   // node num -> device role, seeded from disk cache
    private readonly Dictionary<uint, string> _hwCache = new();     // node num -> hardware model name, seeded from disk cache
    private readonly Dictionary<uint, (double Lat, double Lon, long LastHeard, long PosTime)> _positionCache = new();   // node num -> position + times, seeded from disk
    private readonly Dictionary<uint, List<(double Lat, double Lon, long LastHeard, long PosTime)>> _positionHistory = new();   // node num -> recent positions (oldest first)
    private const int MaxPositionHistory = 20;   // keep only the latest N positions per node (oldest dropped past this)
    private const double MinPositionMoveMeters = 15.0;   // only record a new track point once the node has moved this far from the last one
    private readonly Dictionary<uint, List<MeshEnvironment>> _environmentHistory = new();   // node num -> readings (oldest first)
    private const int MaxEnvironmentHistory = 100;   // keep only the latest N per node (oldest dropped past this)
    private readonly HashSet<uint> _sentTraceroutes = new();   // ids of traceroute requests awaiting a reply (matched by request_id)
    private readonly Dictionary<uint, uint> _lastHeardChannel = new();   // node num -> channel index we last decoded its packets on
    private readonly Dictionary<uint, (int Rssi, float Snr, int? Hops, long When)> _signalCache = new();   // node num -> latest radio metrics from a packet we heard directly/relayed

    /// <summary>This device's node number, populated by <see cref="InitializeAsync"/> or seeding.</summary>
    public uint MyNodeNum { get; private set; }

    /// <summary>False once a persistent transport (TCP/BLE) has dropped its socket — the link is dead even if the
    /// device is still reachable by a fresh connection. HTTP is always true (its loss is probed separately).</summary>
    public bool TransportConnected => _transport.IsConnected;

    /// <summary>True when the link is persistent and needs periodic heartbeats (TCP/BLE), false for HTTP.</summary>
    public bool TransportNeedsKeepAlive => _transport.NeedsKeepAlive;

    /// <summary>True when the transport reports its own liveness (TCP/BLE) so the external reachability probe should
    /// be skipped — opening a competing connection to a single-client port would be a false "lost". See
    /// <see cref="IMeshTransport.SelfReportsLiveness"/>.</summary>
    public bool TransportSelfReportsLiveness => _transport.SelfReportsLiveness;

    // ToRadio.heartbeat is field 7, an empty Heartbeat message — the bundled proto predates it, so encode the two
    // wire bytes by hand: tag (7<<3 | 2 = 0x3A) + length 0.
    private static readonly byte[] HeartbeatToRadio = { 0x3A, 0x00 };

    /// <summary>Sends a keep-alive heartbeat so the device doesn't close an idle persistent connection. Throws if
    /// the link is dead (which the caller can treat as a dropped connection).</summary>
    public Task SendHeartbeatAsync(CancellationToken ct = default) => _transport.WriteAsync(HeartbeatToRadio, ct);

    /// <summary>Asks a <c>Meshtastic.Proxy</c> to replay every cached text message newer than
    /// <paramref name="sinceEpoch"/> (the message rx_time), so this client catches up on what it missed while
    /// disconnected. A side-channel frame ("MPXY") the proxy recognises. Only the app's proxy-link path calls this —
    /// it is never sent to a real device, so direct-connection behaviour is unchanged.</summary>
    public Task SendProxyBackfillRequestAsync(long sinceEpoch, CancellationToken ct = default)
    {
        var buf = new byte[13];
        buf[0] = (byte)'M'; buf[1] = (byte)'P'; buf[2] = (byte)'X'; buf[3] = (byte)'Y'; buf[4] = 0x01;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(5), sinceEpoch);
        return _transport.WriteAsync(buf, ct);
    }

    /// <summary>Channel index used when sending. Change it to switch channels.</summary>
    public uint ChannelIndex { get; set; }

    // Optional app-level AES-256 passphrase per channel index. When a channel has a non-empty key,
    // outgoing text on it is encrypted (then base64) and incoming text decrypted — an extra layer on
    // top of the radio's own channel PSK. Channels without a key here travel as plaintext to the radio.
    private readonly Dictionary<uint, string> _channelKeys = new();

    /// <summary>Sets (or clears, when null/empty) the app-level AES key for a channel index.</summary>
    public void SetChannelKey(uint channel, string? key)
    {
        if (string.IsNullOrEmpty(key)) _channelKeys.Remove(channel);
        else _channelKeys[channel] = key;
    }

    /// <summary>The app-level AES key for a channel index, or "" if none.</summary>
    public string GetChannelKey(uint channel) => _channelKeys.GetValueOrDefault(channel, string.Empty);

    /// <summary>Connect over the device's HTTP API at <paramref name="baseUrl"/>.</summary>
    public MeshtasticHttpClient(string baseUrl, uint channelIndex = 0, uint? destination = null)
        : this(new HttpMeshTransport(baseUrl), channelIndex, destination) { }

    /// <summary>Connect over any link (HTTP, BLE, …). The client owns all protocol and parsing; the transport
    /// only moves serialized ToRadio/FromRadio bytes. Takes ownership of <paramref name="transport"/> (disposed
    /// with this client).</summary>
    public MeshtasticHttpClient(IMeshTransport transport, uint channelIndex = 0, uint? destination = null)
    {
        _transport = transport;
        ChannelIndex = channelIndex;
        _destination = destination;
    }

    /// <summary>
    /// Performs the want-config handshake so the device reports its node number,
    /// channels and known nodes. Best-effort: failures are non-fatal for gameplay.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await FetchConfigAsync(ct);
        MyNodeNum = _state.MyNodeInfo?.MyNodeNum ?? 0;
    }

    /// <summary>
    /// Seeds the channel list and node number from a cache, so a connect can skip the want-config
    /// dump entirely. With no dump requested the device stays in live-packet mode, so messages and
    /// moves are received immediately (no sync wait).
    /// </summary>
    public void Seed(IReadOnlyList<MeshChannel> channels, uint myNodeNum,
                     IReadOnlyDictionary<uint, string>? nodeNames = null,
                     IReadOnlyDictionary<uint, string>? nodeRoles = null,
                     IReadOnlyDictionary<uint, (double Lat, double Lon, long LastHeard, long PosTime)>? nodePositions = null,
                     IReadOnlyDictionary<uint, string>? nodeHw = null,
                     IReadOnlyDictionary<uint, string>? nodeShortNames = null)
    {
        _seededChannels = channels.ToList();
        MyNodeNum = myNodeNum;
        if (nodeNames != null)
        {
            _nameCache.Clear();
            foreach (var kv in nodeNames) _nameCache[kv.Key] = kv.Value;
        }
        if (nodeShortNames != null)
        {
            _shortNameCache.Clear();
            foreach (var kv in nodeShortNames) _shortNameCache[kv.Key] = kv.Value;
        }
        if (nodeRoles != null)
        {
            _roleCache.Clear();
            foreach (var kv in nodeRoles) _roleCache[kv.Key] = kv.Value;
        }
        if (nodeHw != null)
        {
            _hwCache.Clear();
            foreach (var kv in nodeHw) _hwCache[kv.Key] = kv.Value;
        }
        if (nodePositions != null)
        {
            _positionCache.Clear();
            foreach (var kv in nodePositions) _positionCache[kv.Key] = kv.Value;
        }
    }

    /// <summary>Merges cached node names/roles/hardware/positions into the in-memory caches WITHOUT touching the
    /// live channel state — call after a full connect so nodes the device has since forgotten still appear in
    /// <see cref="GetNodes"/> (and aren't dropped from the persisted cache on the next save). Unlike
    /// <see cref="Seed"/> this doesn't clear the caches (live data already merged in keeps its values).</summary>
    public void SeedNodes(IReadOnlyDictionary<uint, string>? nodeNames = null,
                          IReadOnlyDictionary<uint, string>? nodeRoles = null,
                          IReadOnlyDictionary<uint, string>? nodeHw = null,
                          IReadOnlyDictionary<uint, (double Lat, double Lon, long LastHeard, long PosTime)>? nodePositions = null,
                          IReadOnlyDictionary<uint, string>? nodeShortNames = null,
                          IReadOnlyDictionary<uint, bool>? nodeFavorites = null,
                          IReadOnlyDictionary<uint, bool>? nodeIgnored = null,
                          IReadOnlyDictionary<uint, uint>? nodeHopsAway = null,
                          IReadOnlyDictionary<uint, long>? nodeLastHeard = null)
    {
        if (nodeNames != null) foreach (var kv in nodeNames) _nameCache[kv.Key] = kv.Value;
        if (nodeShortNames != null) foreach (var kv in nodeShortNames) _shortNameCache[kv.Key] = kv.Value;
        if (nodeFavorites != null) foreach (var kv in nodeFavorites) _favoriteCache[kv.Key] = kv.Value;
        if (nodeIgnored != null) foreach (var kv in nodeIgnored) _ignoredCache[kv.Key] = kv.Value;
        if (nodeHopsAway != null) foreach (var kv in nodeHopsAway) _hopsAwayCache[kv.Key] = kv.Value;
        if (nodeLastHeard != null) foreach (var kv in nodeLastHeard) _lastHeardCache[kv.Key] = kv.Value;
        if (nodeRoles != null) foreach (var kv in nodeRoles) _roleCache[kv.Key] = kv.Value;
        if (nodeHw != null) foreach (var kv in nodeHw) _hwCache[kv.Key] = kv.Value;
        if (nodePositions != null) foreach (var kv in nodePositions) _positionCache[kv.Key] = kv.Value;
    }

    /// <summary>node-num → display name for every named node currently known (for caching).</summary>
    public IReadOnlyDictionary<uint, string> GetNodeNameMap()
    {
        var map = new Dictionary<uint, string>(_nameCache);   // keep cached names, let live data win
        foreach (var n in _state.Nodes)
        {
            string name = NameOf(n.User?.LongName, n.User?.ShortName);
            if (name.Length > 0) map[n.Num] = name;
        }
        return map;
    }

    /// <summary>node-num → short name for every node whose short name is known (cached + live), for caching.</summary>
    public IReadOnlyDictionary<uint, string> GetNodeShortNameMap()
    {
        var map = new Dictionary<uint, string>(_shortNameCache);   // keep cached short names, let live data win
        foreach (var n in _state.Nodes)
            if (!string.IsNullOrWhiteSpace(n.User?.ShortName)) map[n.Num] = n.User!.ShortName;
        return map;
    }

    /// <summary>node-num → device "favorite" flag (cached + live; only true entries kept), for caching.</summary>
    public IReadOnlyDictionary<uint, bool> GetNodeFavoriteMap()
    {
        var map = new Dictionary<uint, bool>(_favoriteCache);
        foreach (var n in _state.Nodes) if (n.IsFavorite) map[n.Num] = true; else map.Remove(n.Num);
        return map.Where(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>node-num → device "ignored" flag (cached + live; only true entries kept), for caching.</summary>
    public IReadOnlyDictionary<uint, bool> GetNodeIgnoredMap()
    {
        var map = new Dictionary<uint, bool>(_ignoredCache);
        foreach (var n in _state.Nodes) if (n.IsIgnored) map[n.Num] = true; else map.Remove(n.Num);
        return map.Where(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>node-num → hops away (cached + live), for caching.</summary>
    public IReadOnlyDictionary<uint, uint> GetNodeHopsAwayMap()
    {
        var map = new Dictionary<uint, uint>(_hopsAwayCache);
        foreach (var n in _state.Nodes) if (n.HasHopsAway) map[n.Num] = n.HopsAway;
        return map;
    }

    /// <summary>node-num → last-heard epoch seconds (cached + live), for caching a standalone last-heard.</summary>
    public IReadOnlyDictionary<uint, long> GetNodeLastHeardMap()
    {
        var map = new Dictionary<uint, long>(_lastHeardCache);
        foreach (var n in _state.Nodes) if (n.LastHeard > 0) map[n.Num] = n.LastHeard;
        return map;
    }

    /// <summary>node-num → device role for every node whose role is known (cached + live), for caching.</summary>
    public IReadOnlyDictionary<uint, string> GetNodeRoleMap()
    {
        var map = new Dictionary<uint, string>(_roleCache);   // keep cached roles, let live data win
        foreach (var n in _state.Nodes)
        {
            string role = RoleName(n.User?.Role);
            if (role.Length > 0) map[n.Num] = role;
        }
        return map;
    }

    /// <summary>node-num → hardware model name for every node whose hardware is known (cached + live), for caching.</summary>
    public IReadOnlyDictionary<uint, string> GetNodeHwMap()
    {
        var map = new Dictionary<uint, string>(_hwCache);   // keep cached hardware, let live data win
        foreach (var n in _state.Nodes)
        {
            if (n.User == null) continue;
            string hw = HwName(n.User.HwModel);
            if (hw.Length > 0) map[n.Num] = hw;
        }
        return map;
    }

    /// <summary>A readable name for a node's hardware model (e.g. "Heltec V4"), or "" when unset. The device only
    /// sends the model's enum *number*, and the bundled protobuf enum is outdated (it stops at 54), so newer
    /// hardware would otherwise show as a bare number. We keep our own value→name table (from the current
    /// Meshtastic mesh.proto) so models like Heltec V4 (110) resolve; anything not in the table shows "Model N".</summary>
    private static string HwName(Meshtastic.Protobufs.HardwareModel hw)
    {
        int v = (int)hw;
        if (v == 0) return string.Empty;   // UNSET
        return HardwareNames.TryGetValue(v, out var name) ? PrettyHardware(name) : $"Model {v}";
    }

    // Formats a SCREAMING_SNAKE_CASE model name for display: "HELTEC_V4" → "Heltec V4", "T_DECK_PRO" → "T Deck
    // Pro". Tokens containing a digit (V4, S3, RAK4631, T114) or ≤2 letters (T, M5…) are kept as-is; others are
    // title-cased.
    private static string PrettyHardware(string screaming)
    {
        var parts = screaming.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string w = parts[i];
            if (w.Length > 2 && !w.Any(char.IsDigit))
                parts[i] = char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
        }
        return string.Join(' ', parts);
    }

    // The canonical Meshtastic HardwareModel enum (meshtastic/mesh.proto). Kept here because the bundled protobuf
    // enum is older than the firmware reporting these numbers. Update when new models ship.
    private static readonly Dictionary<int, string> HardwareNames = new()
    {
        [1] = "TLORA_V2", [2] = "TLORA_V1", [3] = "TLORA_V2_1_1P6", [4] = "TBEAM", [5] = "HELTEC_V2_0",
        [6] = "TBEAM_V0P7", [7] = "T_ECHO", [8] = "TLORA_V1_1P3", [9] = "RAK4631", [10] = "HELTEC_V2_1",
        [11] = "HELTEC_V1", [12] = "LILYGO_TBEAM_S3_CORE", [13] = "RAK11200", [14] = "NANO_G1",
        [15] = "TLORA_V2_1_1P8", [16] = "TLORA_T3_S3", [17] = "NANO_G1_EXPLORER", [18] = "NANO_G2_ULTRA",
        [19] = "LORA_TYPE", [20] = "WIPHONE", [21] = "WIO_WM1110", [22] = "RAK2560", [23] = "HELTEC_HRU_3601",
        [24] = "HELTEC_WIRELESS_BRIDGE", [25] = "STATION_G1", [26] = "RAK11310", [27] = "SENSELORA_RP2040",
        [28] = "SENSELORA_S3", [29] = "CANARYONE", [30] = "RP2040_LORA", [31] = "STATION_G2", [32] = "LORA_RELAY_V1",
        [33] = "NRF52840DK", [34] = "PPR", [35] = "GENIEBLOCKS", [36] = "NRF52_UNKNOWN", [37] = "PORTDUINO",
        [38] = "ANDROID_SIM", [39] = "DIY_V1", [40] = "NRF52840_PCA10059", [41] = "DR_DEV", [42] = "M5STACK",
        [43] = "HELTEC_V3", [44] = "HELTEC_WSL_V3", [45] = "BETAFPV_2400_TX", [46] = "BETAFPV_900_NANO_TX",
        [47] = "RPI_PICO", [48] = "HELTEC_WIRELESS_TRACKER", [49] = "HELTEC_WIRELESS_PAPER", [50] = "T_DECK",
        [51] = "T_WATCH_S3", [52] = "PICOMPUTER_S3", [53] = "HELTEC_HT62", [54] = "EBYTE_ESP32_S3",
        [55] = "ESP32_S3_PICO", [56] = "CHATTER_2", [57] = "HELTEC_WIRELESS_PAPER_V1_0",
        [58] = "HELTEC_WIRELESS_TRACKER_V1_0", [59] = "UNPHONE", [60] = "TD_LORAC", [61] = "CDEBYTE_EORA_S3",
        [62] = "TWC_MESH_V4", [63] = "NRF52_PROMICRO_DIY", [64] = "RADIOMASTER_900_BANDIT_NANO",
        [65] = "HELTEC_CAPSULE_SENSOR_V3", [66] = "HELTEC_VISION_MASTER_T190", [67] = "HELTEC_VISION_MASTER_E213",
        [68] = "HELTEC_VISION_MASTER_E290", [69] = "HELTEC_MESH_NODE_T114", [70] = "SENSECAP_INDICATOR",
        [71] = "TRACKER_T1000_E", [72] = "RAK3172", [73] = "WIO_E5", [74] = "RADIOMASTER_900_BANDIT",
        [75] = "ME25LS01_4Y10TD", [76] = "RP2040_FEATHER_RFM95", [77] = "M5STACK_COREBASIC", [78] = "M5STACK_CORE2",
        [79] = "RPI_PICO2", [80] = "M5STACK_CORES3", [81] = "SEEED_XIAO_S3", [82] = "MS24SF1", [83] = "TLORA_C6",
        [84] = "WISMESH_TAP", [85] = "ROUTASTIC", [86] = "MESH_TAB", [87] = "MESHLINK", [88] = "XIAO_NRF52_KIT",
        [89] = "THINKNODE_M1", [90] = "THINKNODE_M2", [91] = "T_ETH_ELITE", [92] = "HELTEC_SENSOR_HUB",
        [93] = "MUZI_BASE", [94] = "HELTEC_MESH_POCKET", [95] = "SEEED_SOLAR_NODE", [96] = "NOMADSTAR_METEOR_PRO",
        [97] = "CROWPANEL", [98] = "LINK_32", [99] = "SEEED_WIO_TRACKER_L1", [100] = "SEEED_WIO_TRACKER_L1_EINK",
        [101] = "MUZI_R1_NEO", [102] = "T_DECK_PRO", [103] = "T_LORA_PAGER", [104] = "M5STACK_RESERVED",
        [105] = "WISMESH_TAG", [106] = "RAK3312", [107] = "THINKNODE_M5", [108] = "HELTEC_MESH_SOLAR",
        [109] = "T_ECHO_LITE", [110] = "HELTEC_V4", [111] = "M5STACK_C6L", [112] = "M5STACK_CARDPUTER_ADV",
        [113] = "HELTEC_WIRELESS_TRACKER_V2", [114] = "T_WATCH_ULTRA", [115] = "THINKNODE_M3", [116] = "WISMESH_TAP_V2",
        [117] = "RAK3401", [118] = "RAK6421", [119] = "THINKNODE_M4", [120] = "THINKNODE_M6", [121] = "MESHSTICK_1262",
        [122] = "TBEAM_1_WATT", [123] = "T5_S3_EPAPER_PRO", [124] = "TBEAM_BPF", [125] = "MINI_EPAPER_S3",
        [126] = "TDISPLAY_S3_PRO", [127] = "HELTEC_MESH_NODE_T096", [128] = "TRACKER_T1000_E_PRO",
        [129] = "THINKNODE_M7", [130] = "THINKNODE_M8", [131] = "THINKNODE_M9", [132] = "HELTEC_V4_R8",
        [133] = "HELTEC_MESH_NODE_T1", [134] = "STATION_G3", [135] = "T_IMPULSE_PLUS", [136] = "T_ECHO_CARD",
        [137] = "SEEED_WIO_TRACKER_L2", [138] = "CROWPANEL_P4", [139] = "HELTEC_MESH_TOWER_V2", [140] = "MESHNOLOGY_W10",
        [255] = "PRIVATE_HW",
    };

    /// <summary>The latest radio metrics heard from a node this session, or null if none seen. Rssi is in dBm
    /// and Snr in dB as the device's radio read the packet. Hops is the distance it travelled (0 = heard
    /// directly, so the metrics describe that node's own link; &gt; 0 = the metrics describe the last relay→us
    /// hop, not the node itself; null = the sender's initial hop budget wasn't reported, so distance is
    /// unknown). When is the device receive time (epoch seconds) of that packet.</summary>
    public (int Rssi, float Snr, int? Hops, long When)? GetSignal(uint nodeNum) =>
        _signalCache.TryGetValue(nodeNum, out var s) ? s : ((int, float, int?, long)?)null;

    /// <summary>A human-readable dump of everything known about a node — identity, hardware, role, last-heard,
    /// signal, position and its latest environment telemetry — for the apps' "Show all info" view (which lists
    /// the full telemetry history below this). Per-node DM/Block prefs live in the app, so the caller appends
    /// those.</summary>
    public string GetNodeInfoText(uint num)
    {
        var node = GetNodes().FirstOrDefault(n => n.Num == num);
        var ni = _state.Nodes.FirstOrDefault(x => x.Num == num);   // live NodeInfo (null for cached-only nodes)
        var lines = new List<string> { $"Number: !{num:x8}  ({num})" };
        if (!string.IsNullOrWhiteSpace(node.LongName)) lines.Add($"Name: {node.LongName}");
        if (!string.IsNullOrWhiteSpace(node.ShortName)) lines.Add($"Short name: {node.ShortName}");
        string hw = !string.IsNullOrEmpty(node.HwModel) ? node.HwModel : (num == MyNodeNum ? HardwareModel ?? "" : "");
        if (hw.Length > 0) lines.Add($"Hardware: {hw}");
        if (!string.IsNullOrEmpty(node.Role)) lines.Add($"Role: {node.Role}");
        if (num == MyNodeNum) lines.Add("This is your own node.");
        // Device NodeDB flags + mesh distance.
        if (node.IsFavorite) lines.Add("Favorite: yes");
        if (node.IsIgnored) lines.Add("Ignored (device): yes");
        if (node.HopsAway is uint ha) lines.Add($"Hops away: {ha}");
        if (ni?.ViaMqtt == true) lines.Add("Heard via MQTT: yes");
        if (ni != null && ni.Channel > 0) lines.Add($"Heard on channel: {ni.Channel}");
        // User identity extras (public key / MAC / licensed / messagability) — live nodes only.
        if (ni?.User is { } u)
        {
            if (!u.PublicKey.IsEmpty)
            {
                var pk = u.PublicKey.ToByteArray();
                lines.Add($"Public key: {Convert.ToHexString(pk, 0, Math.Min(4, pk.Length)).ToLowerInvariant()}… ({pk.Length} bytes)");
            }
            if (!u.Macaddr.IsEmpty)
                lines.Add($"MAC: {string.Join(":", u.Macaddr.ToByteArray().Select(b => b.ToString("x2")))}");
            if (u.IsLicensed) lines.Add("Licensed (ham) operator: yes");
            if (u.HasIsUnmessagable && u.IsUnmessagable) lines.Add("Direct messages: not supported by this node");
        }
        if (node.LastHeard > 0)
            lines.Add($"Last heard: {DateTimeOffset.FromUnixTimeSeconds(node.LastHeard).LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        if (GetSignal(num) is { } s)
        {
            string sig = $"RSSI {s.Rssi} dBm · SNR {s.Snr:0.#} dB";
            sig = s.Hops switch { 0 => $"{sig} · direct", null => sig, 1 => $"via 1 hop · {sig}", var h => $"via {h} hops · {sig}" };
            lines.Add($"Signal: {sig}");
        }
        else if (ni != null && ni.Snr != 0)
            lines.Add($"Signal (device-reported): SNR {ni.Snr:0.#} dB");
        var pos = GetNodePositions().FirstOrDefault(p => p.Num == num);
        if (pos.Num != 0 && (pos.Latitude != 0 || pos.Longitude != 0))
        {
            lines.Add($"Position: {pos.Latitude.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}, " +
                      $"{pos.Longitude.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}");
            if (pos.PositionTime > 0)
                lines.Add($"Position time: {DateTimeOffset.FromUnixTimeSeconds(pos.PositionTime).LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        }
        // Extended GPS detail from the raw live Position (altitude / sats / speed / precision).
        if (ni?.Position is { } p)
        {
            if (p.Altitude != 0) lines.Add($"Altitude: {p.Altitude} m");
            if (p.SatsInView > 0) lines.Add($"Satellites in view: {p.SatsInView}");
            if (p.GroundSpeed > 0)
                lines.Add($"Ground speed: {p.GroundSpeed} m/s" + (p.GroundTrack > 0 ? $" · heading {p.GroundTrack / 1e5:0.#}°" : ""));
            if (p.PrecisionBits > 0) lines.Add($"Position precision: {p.PrecisionBits} bits");
        }
        var env = GetEnvironmentHistory(num);
        if (env.Count > 0)
            lines.Add($"Latest telemetry: {EnvSummaryText(env[^1])}  (@ {env[^1].Timestamp:HH:mm:ss})");
        // Battery + device utilization, from the node's DeviceMetrics (>100 = powered/charging, no battery).
        if (ni?.DeviceMetrics is { } dm)
        {
            var batt = new List<string>();
            if (dm.BatteryLevel > 100) batt.Add("powered (charging / no battery)");
            else if (dm.BatteryLevel > 0) batt.Add($"{dm.BatteryLevel}%");
            if (dm.Voltage > 0) batt.Add($"{dm.Voltage:0.##} V");
            if (batt.Count > 0) lines.Add($"Battery: {string.Join(" · ", batt)}");
            if (dm.HasChannelUtilization) lines.Add($"Channel utilization: {dm.ChannelUtilization:0.#}%");
            if (dm.HasAirUtilTx) lines.Add($"Air-time TX: {dm.AirUtilTx:0.#}%");
            if (dm.HasUptimeSeconds && dm.UptimeSeconds > 0) lines.Add($"Uptime: {FormatUptime(dm.UptimeSeconds)}");
        }
        // Firmware version — only the local (connected) device reports it, via its metadata.
        if (num == MyNodeNum && FirmwareVersion is { } fw)
            lines.Add($"Firmware: {fw}");
        if (GetNoiseFloor(num) is int nf)
            lines.Add($"Noise floor: {nf} dBm");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatUptime(uint seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m {t.Seconds}s";
    }

    private static string EnvSummaryText(MeshEnvironment e)
    {
        var parts = new List<string> { $"{e.TemperatureC:0.#}°C" };
        if (e.RelativeHumidity > 0) parts.Add($"{e.RelativeHumidity:0}%RH");
        if (e.DewPointC is double dp) parts.Add($"dp {dp:0.#}°C");
        if (e.BarometricPressure > 0) parts.Add($"{e.BarometricPressure:0} hPa");
        return string.Join(" · ", parts);
    }

    // Records the latest radio metrics for a packet we heard from another node. Packets with no radio reading
    // (RSSI and SNR both 0 — e.g. MQTT-downlinked traffic the device didn't hear over the air) are ignored so
    // the Nodes window doesn't show a bogus "0 dBm".
    private void RecordSignal(MeshPacket pkt)
    {
        if (pkt.RxRssi == 0 && pkt.RxSnr == 0) return;
        int hopStart = (int)ReadVarintField(pkt, 15);   // hop_start (sender's initial budget); 0 when not reported
        int? hops = hopStart > 0 ? Math.Max(0, hopStart - (int)pkt.HopLimit) : (int?)null;
        long when = pkt.RxTime > 0 ? pkt.RxTime : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _signalCache[pkt.From] = (pkt.RxRssi, pkt.RxSnr, hops, when);
    }

    // Upserts a node into the device node DB from a live User broadcast (the library's AddFromRadio only adds
    // nodes from the config-dump NodeInfo variant, not from over-the-air NODEINFO_APP packets), so newly heard
    // nodes appear automatically without a full "Update nodes" resync. Keeps the name cache fresh too. Returns
    // true when this is the first time we've seen the node at all (not in the live DB nor the seeded cache), so
    // the caller can announce it — a node already known from a previous session is not reported as new.
    private bool MergeNodeUser(uint num, User user, uint rxTime)
    {
        bool isNew = _state.Nodes.All(n => n.Num != num) && !_nameCache.ContainsKey(num);
        var node = _state.Nodes.FirstOrDefault(n => n.Num == num);
        if (node == null) { node = new NodeInfo { Num = num }; _state.Nodes.Add(node); }
        node.User = user;
        if (rxTime > 0) node.LastHeard = rxTime;
        var name = NameOf(user.LongName, user.ShortName);
        if (name.Length > 0) _nameCache[num] = name;
        if (!string.IsNullOrWhiteSpace(user.ShortName)) _shortNameCache[num] = user.ShortName;
        var role = RoleName(user.Role);
        if (role.Length > 0) _roleCache[num] = role;
        var hw = HwName(user.HwModel);
        if (hw.Length > 0) _hwCache[num] = hw;
        return isNew;
    }

    /// <summary>The latest environment telemetry heard from a node, or null if it hasn't sent any.</summary>
    public MeshEnvironment? GetEnvironment(uint nodeNum) =>
        _environmentHistory.TryGetValue(nodeNum, out var list) && list.Count > 0 ? list[^1] : (MeshEnvironment?)null;

    /// <summary>Every environment reading cached for a node this session, oldest first (empty if none).</summary>
    public IReadOnlyList<MeshEnvironment> GetEnvironmentHistory(uint nodeNum) =>
        _environmentHistory.TryGetValue(nodeNum, out var list) ? list : (IReadOnlyList<MeshEnvironment>)Array.Empty<MeshEnvironment>();

    /// <summary>Seeds the environment history from persisted storage (called on connect), keeping only the
    /// most recent <see cref="MaxEnvironmentHistory"/> readings per node.</summary>
    public void SeedEnvironment(IReadOnlyDictionary<uint, List<MeshEnvironment>> history)
    {
        foreach (var kv in history)
        {
            var list = kv.Value.ToList();
            if (list.Count > MaxEnvironmentHistory) list.RemoveRange(0, list.Count - MaxEnvironmentHistory);
            _environmentHistory[kv.Key] = list;
        }
    }

    /// <summary>Drops all cached environment telemetry for a node.</summary>
    public void ClearEnvironment(uint nodeNum) => _environmentHistory.Remove(nodeNum);

    /// <summary>Display name for a node's device role (e.g. "Router Client"), or "" when unknown.</summary>
    private static string RoleName(Config.Types.DeviceConfig.Types.Role? role)
    {
        if (role == null) return string.Empty;
        // Roles newer than this bundled protobuf (its enum stops at 9 = LostAndFound) arrive as raw numbers —
        // name the known ones explicitly so they don't show as "[10]"/"[11]"/"[12]".
        switch ((int)role.Value)
        {
            case 10: return "TAK Tracker";
            case 11: return "Router Late";
            case 12: return "Client Base";
        }
        // Space out interior capitals so "RouterClient" reads as "Router Client".
        string s = role.Value.ToString();
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string NameOf(string? longName, string? shortName) =>
        !string.IsNullOrWhiteSpace(longName) ? longName!
        : !string.IsNullOrWhiteSpace(shortName) ? shortName!
        : string.Empty;

    /// <summary>
    /// Asks the device to (re)send its full config dump. Drain it afterwards with
    /// <see cref="ReceiveAsync"/> to refresh the channel and node lists. While the dump is in
    /// progress the device serves config instead of live packets.
    /// </summary>
    public async Task RequestConfigAsync(CancellationToken ct = default)
    {
        _seededChannels = null;        // fresh channels will arrive in the dump
        _state.Channels.Clear();
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateWantConfigMessage(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// All nodes known: those reported live by the device, plus any cached (seeded) node names not yet
    /// seen this session — so a cached connect shows the remembered nodes without a fresh full fetch.
    /// </summary>
    public IReadOnlyList<MeshNode> GetNodes()
    {
        var live = _state.Nodes
            .Select(n => new MeshNode(n.Num, n.User?.LongName ?? string.Empty, n.User?.ShortName ?? string.Empty,
                n.Num == MyNodeNum, RoleName(n.User?.Role), n.LastHeard,
                // Prefer the live hardware model; fall back to a cached value for nodes whose latest packet
                // carried no User (e.g. a position-only update).
                n.User != null && HwName(n.User.HwModel) is { Length: > 0 } hw ? hw : _hwCache.GetValueOrDefault(n.Num, string.Empty))
            {
                IsFavorite = n.IsFavorite,
                IsIgnored = n.IsIgnored,
                HopsAway = n.HasHopsAway ? n.HopsAway : (uint?)(_hopsAwayCache.TryGetValue(n.Num, out var ha) ? ha : null),
            });
        var liveNums = _state.Nodes.Select(n => n.Num).ToHashSet();
        var cached = _nameCache
            .Where(kv => !liveNums.Contains(kv.Key))                       // live data wins
            .Select(kv => new MeshNode(kv.Key, kv.Value, _shortNameCache.GetValueOrDefault(kv.Key, string.Empty), kv.Key == MyNodeNum,
                _roleCache.GetValueOrDefault(kv.Key, string.Empty),
                _lastHeardCache.TryGetValue(kv.Key, out var lh) ? lh
                    : (_positionCache.TryGetValue(kv.Key, out var pc) ? pc.LastHeard : 0),   // standalone, else via position
                _hwCache.GetValueOrDefault(kv.Key, string.Empty))
            {
                IsFavorite = _favoriteCache.GetValueOrDefault(kv.Key),
                IsIgnored = _ignoredCache.GetValueOrDefault(kv.Key),
                HopsAway = _hopsAwayCache.TryGetValue(kv.Key, out var ha) ? ha : (uint?)null,
            });
        return live.Concat(cached)
            .OrderByDescending(n => n.IsSelf)
            .ThenBy(n => n.LongName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Re-requests the full device config so the channel list is current. Use this to
    /// pick up channels that weren't captured on the first connect.
    /// </summary>
    public async Task RefreshChannelsAsync(CancellationToken ct = default)
    {
        _state.Channels.Clear();
        await FetchConfigAsync(ct);
        if (MyNodeNum == 0)
            MyNodeNum = _state.MyNodeInfo?.MyNodeNum ?? 0;
    }

    // A Meshtastic device always reports all 8 channel slots (indices 0..7) in its config dump.
    private const int ChannelSlotCount = 8;

    /// <summary>
    /// Sends want-config and drains the device's config dump (my-info, channels, config, nodes…).
    /// Uses ConfigureAwait(false) so a UI-thread caller doesn't pay a thread hop on every read.
    /// </summary>
    /// <param name="stopWhenChannelsComplete">
    /// If true (the default), stops once all 8 channel slots have been collected instead of reading
    /// the whole node database, keeping connect responsive on devices that know hundreds of nodes
    /// (a full drain can take well over a minute). The device emits channels cursor..7 within a
    /// single stream and the cursor persists across requests, so a single want_config can return a
    /// partial set; sending want_config again resets the cursor to 0, letting a second round fill in
    /// the rest. Remaining queued packets are drained afterward by the background poll loop.
    /// </param>
    private async Task FetchConfigAsync(CancellationToken ct, bool stopWhenChannelsComplete = true)
    {
        var seenChannels = new HashSet<int>();
        const int maxRounds = 4;
        int rounds = stopWhenChannelsComplete ? maxRounds : 1;

        for (int round = 0; round < rounds; round++)
        {
            int before = seenChannels.Count;
            var wantConfig = new ToRadioMessageFactory().CreateWantConfigMessage();
            await WriteToRadioAsync(wantConfig, ct).ConfigureAwait(false);

            // Drain this stream's packets. NOTE: always use all=false — requesting ?all=true on the
            // first read makes the device return config_complete immediately with zero channels.
            for (int i = 0; i < 5000; i++)
            {
                var fromRadio = await ReadFromRadioAsync(all: false, ct).ConfigureAwait(false);
                if (fromRadio == null) break;
                _state.AddFromRadio(fromRadio);

                if (fromRadio.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.ConfigCompleteId)
                    break;

                if (stopWhenChannelsComplete &&
                    fromRadio.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.Channel)
                {
                    seenChannels.Add(fromRadio.Channel.Index);
                    // The channel block runs cursor..7; index 7 ends it for this stream.
                    if (fromRadio.Channel.Index == ChannelSlotCount - 1)
                        break;
                }
            }

            if (!stopWhenChannelsComplete) break;
            if (seenChannels.Count >= ChannelSlotCount) break;
            // The device's channel cursor wraps 7->1 (index 0 is only emitted after a full
            // config_complete), so extra rounds stop adding indices. Bail once a round finds nothing new.
            if (seenChannels.Count == before) break;
        }
    }

    /// <summary>
    /// Broadcasts a text message on the configured channel with ack requested, and returns the
    /// packet id. Pass <paramref name="packetId"/> to resend an earlier message under the same id
    /// (so the mesh dedupes it and a late ack still matches).
    /// </summary>
    public Task<uint> SendTextAsync(string text, uint? packetId = null, bool encrypt = true, CancellationToken ct = default)
        => SendTextAsync(text, ChannelIndex, packetId, encrypt, ct: ct);

    /// <summary>Sends an emoji reaction (a "tapback") to a message — a text payload of the emoji with the emoji
    /// flag set and reply_id pointing at the target message's packet id. Goes on <paramref name="channel"/>, or
    /// as a direct message when <paramref name="destination"/> is set. Same wire format the native app uses.</summary>
    public Task<uint> SendReactionAsync(string emoji, uint targetPacketId, uint channel, uint? destination = null, CancellationToken ct = default)
        => SendTextAsync(emoji, channel, destination: destination, replyId: targetPacketId, emoji: true, ct: ct);

    /// <summary>
    /// Sends a text message on an explicit channel index (used to send chat on a different channel
    /// than chess). The radio encrypts per that channel's PSK; when <paramref name="encrypt"/> is true and
    /// an app-level key is set for the channel, the payload is additionally AES-encrypted on top.
    /// <paramref name="destination"/> overrides the client's default destination for this one send: pass a
    /// node number to send a direct message to that node (the firmware routes it via PKI when keys are known,
    /// otherwise it falls back to this channel's PSK), or leave null to broadcast on the channel.
    /// </summary>
    public async Task<uint> SendTextAsync(string text, uint channel, uint? packetId = null, bool encrypt = true,
                                          uint? destination = null, uint replyId = 0, bool emoji = false, CancellationToken ct = default)
    {
        string key = GetChannelKey(channel);
        string payload = (!encrypt || key.Length == 0) ? text : AesText.Encrypt(text, key);
        MeshPacket packet = new TextMessageFactory(_state, destination ?? _destination).CreateTextMessagePacket(payload, channel);
        if (replyId != 0)
            packet.Decoded.ReplyId = replyId;   // marks this as a reply to that message (the native app shows it threaded)
        if (emoji)
            packet.Decoded.Emoji = 1;           // marks the payload as an emoji reaction to the reply_id message (a "tapback")
        if (packetId.HasValue)
            packet.Id = packetId.Value;
        ToRadio toRadio = new ToRadioMessageFactory().CreateMeshPacketMessage(packet);
        await WriteToRadioAsync(toRadio, ct).ConfigureAwait(false);
        return packet.Id;
    }

    /// <summary>
    /// Sends a traceroute (RouteDiscovery) to <paramref name="dest"/> on the active channel. The reply,
    /// addressed back to us, arrives via <see cref="ReceiveAsync"/> as a <see cref="MeshTraceroute"/>.
    /// Returns the sent packet id.
    /// </summary>
    public async Task<uint> SendTracerouteAsync(uint dest, CancellationToken ct = default)
    {
        var packet = new TraceRouteMessageFactory(_state, dest).CreateRouteDiscoveryPacket(ChannelForNode(dest));
        packet.WantAck = true;             // match the native app; lets the firmware retransmit the request
        _sentTraceroutes.Add(packet.Id);   // the reply carries this as its request_id
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(packet), ct).ConfigureAwait(false);
        return packet.Id;
    }

    /// <summary>The channel index to address a node on: the channel its packets were last decoded on (where a
    /// reply can reach it). Falls back to the primary channel (0) when we haven't heard the node this session —
    /// the universal channel every node has, and where default/unconfigured nodes live. (The active chess
    /// channel is a poor fallback: most nodes aren't on it, which yields a NoChannel routing error.)
    /// NOTE: NodeInfo.channel is deliberately NOT used — it proved unreliable (reported a channel the node
    /// can't decrypt on).</summary>
    public uint ChannelForNode(uint nodeNum) =>
        _lastHeardChannel.TryGetValue(nodeNum, out var ch) ? ch : 0;

    /// <summary>Optional override channel for the "utility" packets — position / telemetry / node-info requests and
    /// the manual node-info / position broadcasts. Null = automatic: requests use the channel the target node was
    /// last heard on (<see cref="ChannelForNode"/>), broadcasts use the primary channel (0). When set, all of those
    /// go out on this channel index instead. Configured per device in the Channels settings.</summary>
    private uint? _utilityChannel;
    public uint? UtilityChannel => _utilityChannel;
    public void SetUtilityChannel(uint? channel) => _utilityChannel = channel;

    /// <summary>
    /// Requests a node's user info: sends our own User to <paramref name="dest"/> on the NodeInfo port with
    /// want_response set, so the node replies with its User (which the device merges into its node DB, and
    /// which <see cref="ReceiveAsync"/> surfaces as a <see cref="MeshNodeInfoReply"/>). Returns the packet id.
    /// </summary>
    /// <summary>Requests a node's current position (PositionApp with want_response). The reply updates the
    /// device's node DB (so <see cref="GetNodePositions"/> reflects it). Returns the sent packet id.</summary>
    public async Task<uint> RequestPositionAsync(uint dest, CancellationToken ct = default)
    {
        var packet = new MeshPacket
        {
            Channel = _utilityChannel ?? ChannelForNode(dest),
            To = dest,
            WantAck = true,
            Id = (uint)Random.Shared.Next(1, int.MaxValue),   // a real random packet id (avoid the 0/constant-id mesh-dedup trap)
            HopLimit = _state.GetHopLimitOrDefault(),
            Decoded = new Data { Portnum = PortNum.PositionApp, Payload = new Position().ToByteString(), WantResponse = true },
        };
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(packet), ct).ConfigureAwait(false);
        return packet.Id;
    }

    /// <summary>Every known node that has a position (cached + live), with name, coordinates and timestamps.</summary>
    public IReadOnlyList<MeshNodePosition> GetNodePositions() =>
        GetNodePositionMap().Select(kv => new MeshNodePosition(kv.Key, DescribeNode(kv.Key),
            kv.Value.Lat, kv.Value.Lon, kv.Value.LastHeard, kv.Value.PosTime)).ToList();

    /// <summary>node-num → (position + last-heard/position timestamps) for every node we have a fix for: the
    /// on-disk cache plus live data (live wins). Used both for the map and for persisting back to the cache.</summary>
    public IReadOnlyDictionary<uint, (double Lat, double Lon, long LastHeard, long PosTime)> GetNodePositionMap()
    {
        var map = new Dictionary<uint, (double Lat, double Lon, long LastHeard, long PosTime)>(_positionCache);   // cached first
        foreach (var n in _state.Nodes)
        {
            var p = n.Position;
            if (p == null || (p.LatitudeI == 0 && p.LongitudeI == 0)) continue;     // no fix
            map[n.Num] = (p.LatitudeI / 1e7, p.LongitudeI / 1e7, n.LastHeard, p.Time);   // live wins
        }
        return map;
    }

    /// <summary>The most recent known location for a single node, or null if we have no fix for it (neither a live
    /// position nor a cached one). Used to open the node's last location in a map.</summary>
    public MeshNodePosition? GetNodePosition(uint num)
    {
        var match = GetNodePositions().FirstOrDefault(p => p.Num == num);
        return match.Num == num ? match : null;   // FirstOrDefault yields Num==0 when there's no fix; real nodes are non-zero
    }

    /// <summary>Appends a fix to a node's position track, dropping any point within <see cref="MinPositionMoveMeters"/>
    /// of the last stored one (a rebroadcast or a near-stationary node — no meaningful movement) and keeping only the
    /// latest <see cref="MaxPositionHistory"/>.</summary>
    private void AppendPositionHistory(uint num, double lat, double lon, long lastHeard, long posTime)
    {
        if (!_positionHistory.TryGetValue(num, out var hist)) _positionHistory[num] = hist = new();
        if (hist.Count > 0)
        {
            var last = hist[^1];
            if (DistanceMeters(last.Lat, last.Lon, lat, lon) < MinPositionMoveMeters) return;   // hasn't moved far enough
        }
        hist.Add((lat, lon, lastHeard, posTime));
        if (hist.Count > MaxPositionHistory) hist.RemoveRange(0, hist.Count - MaxPositionHistory);
    }

    /// <summary>Great-circle (haversine) distance in metres between two lat/lon points.</summary>
    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371000.0;                 // Earth radius in metres
        const double d2r = Math.PI / 180.0;
        double dLat = (lat2 - lat1) * d2r, dLon = (lon2 - lon1) * d2r;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * d2r) * Math.Cos(lat2 * d2r) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>The recent positions tracked for a node this session (oldest first), up to the latest
    /// <see cref="MaxPositionHistory"/>. Empty if we've heard no standalone position from it.</summary>
    public IReadOnlyList<(double Lat, double Lon, long LastHeard, long PosTime)> GetPositionHistory(uint num) =>
        _positionHistory.TryGetValue(num, out var list) ? list : Array.Empty<(double, double, long, long)>();

    /// <summary>node num → recent position track (oldest first) for every node we've tracked, for the map and
    /// for persisting the tracks back to the cache.</summary>
    public IReadOnlyDictionary<uint, List<(double Lat, double Lon, long LastHeard, long PosTime)>> GetPositionHistoryMap() => _positionHistory;

    /// <summary>Seeds the position tracks from persisted storage (called on connect), keeping only the most recent
    /// <see cref="MaxPositionHistory"/> points per node.</summary>
    public void SeedPositionHistory(IReadOnlyDictionary<uint, List<(double Lat, double Lon, long LastHeard, long PosTime)>> history)
    {
        foreach (var kv in history)
        {
            var list = kv.Value.ToList();
            if (list.Count > MaxPositionHistory) list.RemoveRange(0, list.Count - MaxPositionHistory);
            _positionHistory[kv.Key] = list;
        }
    }

    /// <summary>A Google Maps URL that drops a pin at the given coordinates (works in the browser and the Maps app).</summary>
    public static string GoogleMapsUrl(double lat, double lon) =>
        "https://www.google.com/maps/search/?api=1&query=" +
        lat.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture) + "," +
        lon.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<uint> RequestNodeInfoAsync(uint dest, CancellationToken ct = default)
    {
        var me = _state.GetDeviceNodeInfo()?.User ?? new User { Id = $"!{MyNodeNum:x8}" };
        var packet = new MeshPacket
        {
            Channel = _utilityChannel ?? ChannelForNode(dest),
            To = dest,
            WantAck = true,
            Id = (uint)Random.Shared.Next(1, int.MaxValue),   // a real random packet id (avoid the 0/constant-id mesh-dedup trap)
            HopLimit = _state.GetHopLimitOrDefault(),
            Decoded = new Data
            {
                Portnum = PortNum.NodeinfoApp,
                Payload = me.ToByteString(),
                WantResponse = true,
            },
        };
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(packet), ct).ConfigureAwait(false);
        return packet.Id;
    }

    /// <summary>Broadcasts our own NodeInfo (User: name, hardware, role) to the whole mesh — the same packet the
    /// firmware's NodeInfo module emits on its timer, sent on demand. Every node that hears it adds/updates us in
    /// its node DB. Returns the sent packet id.</summary>
    public async Task<uint> BroadcastOwnNodeInfoAsync(CancellationToken ct = default)
    {
        var me = _state.GetDeviceNodeInfo()?.User ?? new User { Id = $"!{MyNodeNum:x8}" };
        var packet = new MeshPacket
        {
            Channel = _utilityChannel ?? 0,                    // utility channel if set, else the primary (every node has it)
            To = 0xffffffff,                                   // broadcast to the whole mesh
            Id = (uint)Random.Shared.Next(1, int.MaxValue),
            HopLimit = _state.GetHopLimitOrDefault(),
            Decoded = new Data { Portnum = PortNum.NodeinfoApp, Payload = me.ToByteString() },   // no want_response: this is an announcement, not a request
        };
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(packet), ct).ConfigureAwait(false);
        return packet.Id;
    }

    /// <summary>Broadcasts our own Position to the whole mesh — the same packet the firmware's Position module
    /// emits, sent on demand. Returns the sent packet id. Throws if the device has no fix to share (no GPS lock
    /// and no fixed position set).</summary>
    public async Task<uint> BroadcastOwnPositionAsync(CancellationToken ct = default)
    {
        var pos = _state.GetDeviceNodeInfo()?.Position;
        if (pos == null || (pos.LatitudeI == 0 && pos.LongitudeI == 0))
            throw new InvalidOperationException("This device has no position to share (no GPS fix and no fixed position set).");
        var packet = new MeshPacket
        {
            Channel = _utilityChannel ?? 0,
            To = 0xffffffff,
            Id = (uint)Random.Shared.Next(1, int.MaxValue),
            HopLimit = _state.GetHopLimitOrDefault(),
            Decoded = new Data { Portnum = PortNum.PositionApp, Payload = pos.ToByteString() },
        };
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(packet), ct).ConfigureAwait(false);
        return packet.Id;
    }

    /// <summary>
    /// Requests a node's environment telemetry (temperature/humidity/pressure): sends a Telemetry packet with
    /// the environment variant and want_response set, so a node with an environment sensor replies with its
    /// current readings (surfaced via <see cref="ReceiveAsync"/> as that node's <see cref="MeshEnvironment"/>).
    /// Environment telemetry isn't stored in the device's node DB, so this on-demand request — or catching a
    /// node's periodic broadcast — is the only way to obtain it. Returns the sent packet id.
    /// </summary>
    public Task<uint> RequestTelemetryAsync(uint dest, CancellationToken ct = default)
        => RequestTelemetryVariantAsync(dest, new Telemetry { EnvironmentMetrics = new EnvironmentMetrics() }, ct);

    /// <summary>Requests a node's device metrics (battery/voltage/utilization/uptime). The reply is merged into
    /// the node DB (see ReceiveAsync) so "show all info" reflects it. Returns the sent packet id.</summary>
    public Task<uint> RequestDeviceMetricsAsync(uint dest, CancellationToken ct = default)
        => RequestTelemetryVariantAsync(dest, new Telemetry { DeviceMetrics = new DeviceMetrics() }, ct);

    /// <summary>Requests a node's air-quality metrics (particulate/CO2, if it has such a sensor).</summary>
    public Task<uint> RequestAirQualityAsync(uint dest, CancellationToken ct = default)
        => RequestTelemetryVariantAsync(dest, new Telemetry { AirQualityMetrics = new AirQualityMetrics() }, ct);

    /// <summary>Requests a node's power metrics (channel voltages/currents, if it has an INA sensor).</summary>
    public Task<uint> RequestPowerMetricsAsync(uint dest, CancellationToken ct = default)
        => RequestTelemetryVariantAsync(dest, new Telemetry { PowerMetrics = new PowerMetrics() }, ct);

    private async Task<uint> RequestTelemetryVariantAsync(uint dest, Telemetry request, CancellationToken ct)
    {
        var packet = new MeshPacket
        {
            Channel = _utilityChannel ?? ChannelForNode(dest),
            To = dest,
            WantAck = true,
            Id = (uint)Random.Shared.Next(1, int.MaxValue),   // a real random packet id (avoid the 0/constant-id mesh-dedup trap)
            HopLimit = _state.GetHopLimitOrDefault(),
            Decoded = new Data
            {
                Portnum = PortNum.TelemetryApp,
                Payload = request.ToByteString(),
                WantResponse = true,
            },
        };
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(packet), ct).ConfigureAwait(false);
        return packet.Id;
    }

    // node num → last reported noise floor (LocalStats.noise_floor, dBm).
    private readonly Dictionary<uint, int> _noiseFloor = new();

    /// <summary>The node's most recently reported noise floor in dBm (from a LocalStats telemetry reply), or null
    /// if we've never received one for it.</summary>
    public int? GetNoiseFloor(uint num) => _noiseFloor.TryGetValue(num, out var v) ? v : null;

    /// <summary>Requests a node's LocalStats — to read its noise_floor (dBm) — by sending a Telemetry request with
    /// the local_stats variant (Telemetry field 6, empty), want_response set. The bundled protobuf doesn't model
    /// LocalStats, so the request payload is hand-built and the reply parsed by raw field number. The result is
    /// surfaced via <see cref="ReceiveAsync"/> as a <see cref="MeshNoiseFloor"/>. Pass <see cref="MyNodeNum"/> for
    /// our own node. Returns the sent packet id.</summary>
    public async Task<uint> RequestNoiseFloorAsync(uint dest, CancellationToken ct = default)
    {
        var payload = new List<byte>();
        WriteTag(payload, 6, (int)WireFormat.WireType.LengthDelimited);   // local_stats (field 6)
        WriteVarint(payload, 0);                                          // = {} (empty embedded message)
        var packet = new MeshPacket
        {
            Channel = _utilityChannel ?? ChannelForNode(dest),
            To = dest,
            WantAck = true,
            Id = (uint)Random.Shared.Next(1, int.MaxValue),
            HopLimit = _state.GetHopLimitOrDefault(),
            Decoded = new Data { Portnum = PortNum.TelemetryApp, Payload = ByteString.CopyFrom(payload.ToArray()), WantResponse = true },
        };
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(packet), ct).ConfigureAwait(false);
        return packet.Id;
    }

    // Reads int32 field <inner> from the embedded message at field <outer> in serialized protobuf <bytes> (null if
    // absent). Used for LocalStats.noise_floor: Telemetry.local_stats (6) → noise_floor (15, int32 dBm, unmodeled).
    private static int? ReadInt32SubField(byte[] bytes, int outer, int inner)
    {
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == outer && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                var sub = new CodedInputStream(input.ReadBytes().ToByteArray());
                uint t2;
                while ((t2 = sub.ReadTag()) != 0)
                {
                    if (WireFormat.GetTagFieldNumber(t2) == inner && WireFormat.GetTagWireType(t2) == WireFormat.WireType.Varint)
                        return sub.ReadInt32();
                    sub.SkipLastField();
                }
                return null;
            }
            input.SkipLastField();
        }
        return null;
    }

    // ---- Device configuration (read current config + write changes via admin messages) ----------------

    /// <summary>
    /// Requests the device's full config dump and drains it (up to <paramref name="timeout"/>) so the current
    /// Config / ModuleConfig / owner are available via the Get*Config accessors. The cache/seed connect skips
    /// the dump, so call this before showing settings.
    /// </summary>
    public async Task FetchDeviceConfigAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        _seededChannels = null;
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateWantConfigMessage(), ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var fr = await ReadFromRadioAsync(all: false, ct).ConfigureAwait(false);
            if (fr == null) { await Task.Delay(150, ct).ConfigureAwait(false); continue; }
            _state.AddFromRadio(fr);
            if (fr.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.ConfigCompleteId) break;
        }
        if (MyNodeNum == 0) MyNodeNum = _state.MyNodeInfo?.MyNodeNum ?? 0;
    }

    // Current config sections, cloned so the UI can edit a copy (and unexposed fields are preserved on write).
    public Config.Types.DeviceConfig? GetDeviceConfig() => _state.LocalConfig?.Device?.Clone();
    public Config.Types.LoRaConfig? GetLoRaConfig() => _state.LocalConfig?.Lora?.Clone();
    public Config.Types.PositionConfig? GetPositionConfig() => _state.LocalConfig?.Position?.Clone();
    public ModuleConfig.Types.TelemetryConfig? GetTelemetryConfig() => _state.LocalModuleConfig?.Telemetry?.Clone();
    // Module configs (read from the want-config dump; write via WriteModuleConfigAsync). Used by the "Modules" tab.
    public ModuleConfig.Types.MQTTConfig? GetMqttConfig() => _state.LocalModuleConfig?.Mqtt?.Clone();
    public ModuleConfig.Types.NeighborInfoConfig? GetNeighborInfoConfig() => _state.LocalModuleConfig?.NeighborInfo?.Clone();
    public ModuleConfig.Types.StoreForwardConfig? GetStoreForwardConfig() => _state.LocalModuleConfig?.StoreForward?.Clone();
    public ModuleConfig.Types.RangeTestConfig? GetRangeTestConfig() => _state.LocalModuleConfig?.RangeTest?.Clone();
    public User? GetOwner() => _state.GetDeviceNodeInfo()?.User?.Clone();

    // ---- Connected-device info (populated from the want_config dump: metadata + our own NodeInfo) ----

    /// <summary>Firmware version the device reported in its metadata (e.g. "2.5.4.abcdef"), or null if it
    /// hasn't sent any (older firmware over HTTP may omit it).</summary>
    public string? FirmwareVersion =>
        string.IsNullOrWhiteSpace(_state.Metadata?.FirmwareVersion) ? null : _state.Metadata!.FirmwareVersion;

    /// <summary>Hardware model the device reported (e.g. "HELTEC_V3"), or null if unknown.</summary>
    public string? HardwareModel =>
        _state.Metadata != null && _state.Metadata.HwModel != Meshtastic.Protobufs.HardwareModel.Unset
            ? HwName(_state.Metadata.HwModel) : null;

    /// <summary>The connected device's own battery telemetry from its NodeInfo: charge percent (0-100, or
    /// >100 when powered/charging) and pack voltage in volts. Null until the device has reported DeviceMetrics.
    /// Voltage is 0 when the device doesn't measure it.</summary>
    public (int Percent, float Voltage)? GetDeviceBattery()
    {
        var dm = _state.GetDeviceNodeInfo()?.DeviceMetrics;
        if (dm == null) return null;
        return ((int)dm.BatteryLevel, dm.Voltage);
    }

    /// <summary>Writes a Config section (DeviceConfig/LoRaConfig/…) to the device. Returns null on success, or
    /// an error string when the radio explicitly rejected it.</summary>
    public Task<string?> WriteConfigAsync(object section, CancellationToken ct = default)
        => RunAdminTransactionAsync(a => a.CreateSetConfigMessage(section), ct);

    /// <summary>The LoRa flags the bundled protobuf doesn't model, read from the device's current config (false
    /// when unset): ignore_mqtt (field 104), config_ok_to_mqtt (105), pa_fan_disabled (15).</summary>
    public (bool IgnoreMqtt, bool OkToMqtt, bool PaFanDisabled) GetLoRaExtras()
    {
        var lora = _state.LocalConfig?.Lora;
        if (lora == null) return (false, false, false);
        var bytes = lora.ToByteArray();
        return (ReadVarintField(bytes, 104) != 0, ReadVarintField(bytes, 105) != 0, ReadVarintField(bytes, 15) != 0);
    }

    /// <summary>Writes the LoRa config including the flags the bundled protobuf doesn't model. The modeled fields
    /// come from <paramref name="lora"/>; the extra bools are appended to the serialized LoRaConfig (re-parsed so
    /// they ride along in the message's unknown fields) so they reach the firmware. Returns null on success.</summary>
    public Task<string?> WriteLoRaConfigAsync(Config.Types.LoRaConfig lora, bool ignoreMqtt, bool okToMqtt, bool paFanDisabled, CancellationToken ct = default)
    {
        var bytes = new List<byte>(lora.ToByteArray());
        WriteTag(bytes, 15, 0);  WriteVarint(bytes, paFanDisabled ? 1u : 0u);   // pa_fan_disabled
        WriteTag(bytes, 104, 0); WriteVarint(bytes, ignoreMqtt ? 1u : 0u);      // ignore_mqtt
        WriteTag(bytes, 105, 0); WriteVarint(bytes, okToMqtt ? 1u : 0u);        // config_ok_to_mqtt
        var merged = Config.Types.LoRaConfig.Parser.ParseFrom(bytes.ToArray());
        return WriteConfigAsync(merged, ct);
    }

    /// <summary>The device's current led_heartbeat_disabled flag (DeviceConfig field 12, which the bundled protobuf
    /// doesn't model). False (LED heartbeat on) when unset.</summary>
    public bool GetLedHeartbeatDisabled()
    {
        var dev = _state.LocalConfig?.Device;
        return dev != null && ReadVarintField(dev.ToByteArray(), 12) != 0;
    }

    /// <summary>Writes the DeviceConfig including led_heartbeat_disabled (field 12), which the bundled protobuf
    /// doesn't model — appended to the serialized DeviceConfig (re-parsed so it rides along as an unknown field) so
    /// it reaches the firmware. The modeled fields come from <paramref name="device"/>. Returns null on success.</summary>
    public Task<string?> WriteDeviceConfigAsync(Config.Types.DeviceConfig device, bool ledHeartbeatDisabled, CancellationToken ct = default)
    {
        var bytes = new List<byte>(device.ToByteArray());
        WriteTag(bytes, 12, 0); WriteVarint(bytes, ledHeartbeatDisabled ? 1u : 0u);   // led_heartbeat_disabled
        var merged = Config.Types.DeviceConfig.Parser.ParseFrom(bytes.ToArray());
        return WriteConfigAsync(merged, ct);
    }

    /// <summary>The device's current Display config (screen-on timeout, units, etc.), or null until the config dump
    /// has been read. A clone, so edits don't touch the cached state until written back.</summary>
    public Config.Types.DisplayConfig? GetDisplayConfig() => _state.LocalConfig?.Display?.Clone();

    /// <summary>Writes the Device-tab settings — owner (optional), DeviceConfig (incl. the unmodeled
    /// led_heartbeat_disabled field) and DisplayConfig (screen-on) — in a SINGLE Begin/Commit transaction, so the
    /// radio applies them all and reboots once. Writing them as separate transactions is the bug behind "screen-on
    /// reverts after reboot": each transaction's Commit reboots the device, so the first reboot drops the later
    /// writes. Returns null on success, or the radio's error string for the first section it rejected.</summary>
    public async Task<string?> WriteDeviceSettingsAsync(User? owner, Config.Types.DeviceConfig device, bool ledHeartbeatDisabled,
                                                        Config.Types.DisplayConfig? display, CancellationToken ct = default)
    {
        var dest = _destination ?? (MyNodeNum != 0 ? MyNodeNum : (uint?)null);
        var admin = new AdminMessageFactory(_state, dest);
        var toRadio = new ToRadioMessageFactory();

        // led_heartbeat_disabled (field 12) isn't in the bundled protobuf — append it so it rides along.
        var devBytes = new List<byte>(device.ToByteArray());
        WriteTag(devBytes, 12, 0); WriteVarint(devBytes, ledHeartbeatDisabled ? 1u : 0u);
        var mergedDevice = Config.Types.DeviceConfig.Parser.ParseFrom(devBytes.ToArray());

        await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(admin.CreateBeginEditSettingsMessage()), ct).ConfigureAwait(false);

        // Each set goes inside the one open edit transaction; the Commit at the end applies them all with one reboot.
        async Task<string?> SendAndAck(MeshPacket pkt)
        {
            await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(pkt), ct).ConfigureAwait(false);
            var err = await AwaitRoutingAckAsync(pkt.Id, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            return err is { } e && e != Routing.Types.Error.None ? e.ToString() : null;
        }

        if (owner != null && await SendAndAck(admin.CreateSetOwnerMessage(owner)) is { } oe) return oe;
        if (await SendAndAck(admin.CreateSetConfigMessage(mergedDevice)) is { } de) return de;
        if (display != null && await SendAndAck(admin.CreateSetConfigMessage(display)) is { } se) return se;

        await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(admin.CreateCommitEditSettingsMessage()), ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Writes a ModuleConfig section (TelemetryConfig/…) to the device.</summary>
    public Task<string?> WriteModuleConfigAsync(object section, CancellationToken ct = default)
        => RunAdminTransactionAsync(a => a.CreateSetModuleConfigMessage(section), ct);

    /// <summary>Sets the device owner (long/short name).</summary>
    public Task<string?> SetOwnerAsync(User user, CancellationToken ct = default)
        => RunAdminTransactionAsync(a => a.CreateSetOwnerMessage(user), ct);

    public Task RebootAsync(int seconds = 5, CancellationToken ct = default)
        => SendAdminActionAsync(a => a.CreateRebootMessage(seconds, false), ct);
    public Task FactoryResetAsync(CancellationToken ct = default)
        => SendAdminActionAsync(a => a.CreateFactoryResetMessage(), ct);
    public Task NodeDbResetAsync(CancellationToken ct = default)
        => SendAdminActionAsync(a => a.CreateNodeDbResetMessage(), ct);

    /// <summary>Removes a single node from the device's NodeDB (admin remove_by_nodenum). Use this to recover from a
    /// stale PKI public key after the node reinstalled its firmware: once removed, the device re-learns the node
    /// (and its NEW public key) from the next NodeInfo it hears, so encrypted DMs work again. Also drops the node
    /// from our local state and caches so it disappears from the list until it's heard again.</summary>
    public async Task RemoveNodeAsync(uint nodeNum, CancellationToken ct = default)
    {
        await SendAdminActionAsync(a => a.CreateRemoveByNodenumMessage(nodeNum), ct).ConfigureAwait(false);
        var node = _state.Nodes.FirstOrDefault(n => n.Num == nodeNum);
        if (node != null) _state.Nodes.Remove(node);
        _nameCache.Remove(nodeNum);
        _shortNameCache.Remove(nodeNum);
        _favoriteCache.Remove(nodeNum);
        _ignoredCache.Remove(nodeNum);
        _hopsAwayCache.Remove(nodeNum);
        _lastHeardCache.Remove(nodeNum);
        _roleCache.Remove(nodeNum);
        _hwCache.Remove(nodeNum);
        _positionCache.Remove(nodeNum);
        _signalCache.Remove(nodeNum);
        _environmentHistory.Remove(nodeNum);
        _lastHeardChannel.Remove(nodeNum);
        _noiseFloor.Remove(nodeNum);
    }

    /// <summary>Shuts the device down after <paramref name="seconds"/> (no factory helper exists for this, so
    /// the admin packet is built directly, mirroring AdminMessageFactory).</summary>
    public Task ShutdownAsync(int seconds = 5, CancellationToken ct = default)
    {
        var packet = new MeshPacket
        {
            Channel = _state.GetAdminChannelIndex(),
            WantAck = false,
            To = _destination ?? (MyNodeNum != 0 ? MyNodeNum : 0u),
            Id = (uint)Random.Shared.Next(1, int.MaxValue),   // a real random packet id (avoid the 0/constant-id mesh-dedup trap)
            HopLimit = _state.GetHopLimitOrDefault(),
            Decoded = new Data { Portnum = PortNum.AdminApp, Payload = new AdminMessage { ShutdownSeconds = seconds }.ToByteString() },
        };
        return WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(packet), ct);
    }

    /// <summary>Marks a node as a favorite (or clears it) in the device's node DB via set_favorite_node /
    /// remove_favorite_node admin. Updates the in-memory state + cache so the UI reflects it immediately.
    /// Targets the local (connected) device.</summary>
    public Task SetFavoriteNodeAsync(uint nodeNum, bool favorite, CancellationToken ct = default)
    {
        var admin = favorite ? new AdminMessage { SetFavoriteNode = nodeNum }
                             : new AdminMessage { RemoveFavoriteNode = nodeNum };
        var node = _state.Nodes.FirstOrDefault(n => n.Num == nodeNum);
        if (node != null) node.IsFavorite = favorite;
        _favoriteCache[nodeNum] = favorite;
        return SendLocalAdminAsync(admin, ct);
    }

    /// <summary>Marks a node as ignored (or clears it) in the device's node DB via set_ignored_node /
    /// remove_ignored_node admin. This is the DEVICE-level ignore (distinct from the app-side DM "Block").</summary>
    public Task SetIgnoredNodeAsync(uint nodeNum, bool ignored, CancellationToken ct = default)
    {
        var admin = ignored ? new AdminMessage { SetIgnoredNode = nodeNum }
                            : new AdminMessage { RemoveIgnoredNode = nodeNum };
        var node = _state.Nodes.FirstOrDefault(n => n.Num == nodeNum);
        if (node != null) node.IsIgnored = ignored;
        _ignoredCache[nodeNum] = ignored;
        return SendLocalAdminAsync(admin, ct);
    }

    // Sends a prebuilt AdminMessage to the local device (fire-and-forget), mirroring ShutdownAsync's direct build.
    private Task SendLocalAdminAsync(AdminMessage admin, CancellationToken ct)
    {
        var packet = new MeshPacket
        {
            Channel = _state.GetAdminChannelIndex(),
            WantAck = false,
            To = _destination ?? (MyNodeNum != 0 ? MyNodeNum : 0u),
            Id = (uint)Random.Shared.Next(1, int.MaxValue),   // a real random packet id (avoid the 0/constant-id mesh-dedup trap)
            HopLimit = _state.GetHopLimitOrDefault(),
            Decoded = new Data { Portnum = PortNum.AdminApp, Payload = admin.ToByteString() },
        };
        return WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(packet), ct);
    }

    // ---- Remote admin (targets ANOTHER node over the admin channel) ----------------------------------------
    // Modern firmware requires a session passkey for admin mutations. We first prompt one with a
    // get_device_metadata request, capture the passkey from the reply (in ReceiveAsync, as the poll loop drains),
    // then stamp it into the mutating admin. Requires a shared admin channel (named "admin") on BOTH nodes.
    // NOTE: this path is unverified without a second node to test against.

    public Task<string?> RemoteRebootAsync(uint target, int seconds = 5, CancellationToken ct = default)
        => RemoteAdminAsync(target, new AdminMessage { RebootSeconds = seconds }, ct);
    public Task<string?> RemoteShutdownAsync(uint target, int seconds = 5, CancellationToken ct = default)
        => RemoteAdminAsync(target, new AdminMessage { ShutdownSeconds = seconds }, ct);
    public Task<string?> RemoteFactoryResetAsync(uint target, CancellationToken ct = default)
        => RemoteAdminAsync(target, new AdminMessage { FactoryResetDevice = 1 }, ct);
    public Task<string?> RemoteNodeDbResetAsync(uint target, CancellationToken ct = default)
        => RemoteAdminAsync(target, new AdminMessage { NodedbReset = true }, ct);
    public Task<string?> RemoteSetOwnerAsync(uint target, User owner, CancellationToken ct = default)
        => RemoteAdminAsync(target, new AdminMessage { SetOwner = owner }, ct);

    /// <summary>Runs a mutating admin against a REMOTE node: fetch a session passkey (get_device_metadata), then
    /// send the admin stamped with it. Returns null on success, or an error string. Requires a shared admin
    /// channel with the target node.</summary>
    public async Task<string?> RemoteAdminAsync(uint target, AdminMessage mutating, CancellationToken ct = default)
    {
        _sessionPasskeys.Remove(target);
        await SendRemoteAdminAsync(target, new AdminMessage { GetDeviceMetadataRequest = true }, wantResponse: true, ct).ConfigureAwait(false);
        // The passkey arrives on a later /fromradio drain (the app's poll loop), so wait for the cache to fill.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        ByteString? key = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_sessionPasskeys.TryGetValue(target, out var pk)) { key = pk.Key; break; }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        if (key == null)
            return "No session passkey received — is a shared admin channel (named \"admin\") configured on both nodes, and is the target in range?";
        mutating.SessionPasskey = key;
        var id = await SendRemoteAdminAsync(target, mutating, wantResponse: false, ct).ConfigureAwait(false);
        var err = await AwaitRoutingAckAsync(id, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        return err is { } e && e != Routing.Types.Error.None ? e.ToString() : null;
    }

    private async Task<uint> SendRemoteAdminAsync(uint target, AdminMessage admin, bool wantResponse, CancellationToken ct)
    {
        var packet = new MeshPacket
        {
            Channel = _state.GetAdminChannelIndex(),
            WantAck = true,
            To = target,
            Id = (uint)Random.Shared.Next(1, int.MaxValue),   // a real random packet id (avoid the 0/constant-id mesh-dedup trap)
            HopLimit = _state.GetHopLimitOrDefault(),
            Decoded = new Data { Portnum = PortNum.AdminApp, Payload = admin.ToByteString(), WantResponse = wantResponse },
        };
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(packet), ct).ConfigureAwait(false);
        return packet.Id;
    }

    // Begin → Set → (best-effort ack) → Commit, matching the channel write path. An explicit routing error
    // fails; "no ack" is treated as success (many firmwares don't ack a self-addressed admin packet).
    private async Task<string?> RunAdminTransactionAsync(Func<AdminMessageFactory, MeshPacket> buildSet, CancellationToken ct)
    {
        var dest = _destination ?? (MyNodeNum != 0 ? MyNodeNum : (uint?)null);
        var admin = new AdminMessageFactory(_state, dest);
        var toRadio = new ToRadioMessageFactory();
        await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(admin.CreateBeginEditSettingsMessage()), ct).ConfigureAwait(false);
        var setPkt = buildSet(admin);
        await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(setPkt), ct).ConfigureAwait(false);
        var error = await AwaitRoutingAckAsync(setPkt.Id, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        if (error is { } e && e != Routing.Types.Error.None)
            return e.ToString();
        await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(admin.CreateCommitEditSettingsMessage()), ct).ConfigureAwait(false);
        return null;
    }

    private Task SendAdminActionAsync(Func<AdminMessageFactory, MeshPacket> build, CancellationToken ct)
    {
        var dest = _destination ?? (MyNodeNum != 0 ? MyNodeNum : (uint?)null);
        var admin = new AdminMessageFactory(_state, dest);
        return WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(build(admin)), ct);
    }

    /// <summary>The device's own last known position (cloned), or null if it has none.</summary>
    public Position? GetOwnPosition() => _state.GetDeviceNodeInfo()?.Position?.Clone();

    /// <summary>Sets a manual fixed position (degrees + altitude in metres). The bundled AdminMessage predates
    /// set_fixed_position (field 41), so the admin payload is assembled by hand. Returns null on success.</summary>
    public Task<string?> SetFixedPositionAsync(double latitude, double longitude, int altitude, CancellationToken ct = default)
    {
        var pos = new Position
        {
            LatitudeI = (int)Math.Round(latitude * 1e7),
            LongitudeI = (int)Math.Round(longitude * 1e7),
            Altitude = altitude,
        };
        var payload = new List<byte>();
        WriteTag(payload, 41, 2);                      // set_fixed_position, length-delimited
        var posBytes = pos.ToByteArray();
        WriteVarint(payload, (uint)posBytes.Length);
        payload.AddRange(posBytes);
        return SendRawAdminAsync(payload.ToArray(), ct);
    }

    /// <summary>Sets the device's real-time clock (set_time_only, admin field 36 — absent from the bundled
    /// AdminMessage, so the payload is hand-built). <paramref name="epochSeconds"/> is unix time (UTC). Use this to
    /// correct a radio whose clock drifted ahead/behind, which otherwise stamps received packets (and thus node
    /// "last heard") with the wrong time. Returns null on success.</summary>
    public async Task<string?> SetDeviceTimeAsync(uint epochSeconds, CancellationToken ct = default)
    {
        var payload = new List<byte>();
        WriteTag(payload, 36, 0);            // set_time_only, varint
        WriteVarint(payload, epochSeconds);
        // set_time_only is fire-and-forget — the firmware applies the time but generates no reply. Asking for a
        // response would make it answer with a NO_RESPONSE routing error; don't, and treat a missing/NoResponse
        // ack as success (only an explicit NAK/auth error is a real failure).
        var err = await SendRawAdminAsync(payload.ToArray(), ct, wantResponse: false).ConfigureAwait(false);
        return err is null or "NoResponse" or "Timeout" ? null : err;
    }

    /// <summary>Clears the device's fixed position (remove_fixed_position, admin field 42).</summary>
    public Task<string?> RemoveFixedPositionAsync(CancellationToken ct = default)
    {
        var payload = new List<byte>();
        WriteTag(payload, 42, 0);   // remove_fixed_position, varint
        WriteVarint(payload, 1);    // true
        return SendRawAdminAsync(payload.ToArray(), ct);
    }

    // Sends a hand-built AdminMessage payload (for admin fields the bundled protobuf lacks). Returns the
    // routing error string, or null when accepted / not explicitly rejected.
    private async Task<string?> SendRawAdminAsync(byte[] adminPayload, CancellationToken ct, bool wantResponse = true)
    {
        var packet = new MeshPacket
        {
            Channel = _state.GetAdminChannelIndex(),
            WantAck = false,
            To = _destination ?? (MyNodeNum != 0 ? MyNodeNum : 0u),
            Id = (uint)Random.Shared.Next(1, int.MaxValue),   // a real random packet id (avoid the 0/constant-id mesh-dedup trap)
            HopLimit = _state.GetHopLimitOrDefault(),
            Decoded = new Data { Portnum = PortNum.AdminApp, Payload = ByteString.CopyFrom(adminPayload), WantResponse = wantResponse },
        };
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateMeshPacketMessage(packet), ct).ConfigureAwait(false);
        var error = await AwaitRoutingAckAsync(packet.Id, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        return error is { } e && e != Routing.Types.Error.None ? e.ToString() : null;
    }

    private static void WriteVarint(List<byte> b, uint v) { while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; } b.Add((byte)v); }
    private static void WriteTag(List<byte> b, int field, int wireType) => WriteVarint(b, (uint)((field << 3) | wireType));

    /// <summary>
    /// Drains all queued packets and returns the text messages among them (skipping our own echoes),
    /// together with the ids of any packets the mesh acknowledged (Routing acks for our want_ack sends),
    /// plus any traceroute and node-info replies to our requests.
    /// </summary>
    public async Task<MeshReceiveResult> ReceiveAsync(int maxPackets = 500, CancellationToken ct = default, TimeSpan? requestTimeout = null)
    {
        var messages = new List<MeshTextMessage>();
        var traceroutes = new List<MeshTraceroute>();
        var nodeInfos = new List<MeshNodeInfoReply>();
        var newNodes = new List<MeshNodeInfoReply>();   // nodes heard for the very first time this run
        var telemetryNodes = new List<uint>();
        var positions = new List<MeshPositionReport>();
        var noiseFloors = new List<MeshNoiseFloor>();
        // One ack per packet id; prefer an entry that identified the relay node over one that didn't.
        var ackMap = new Dictionary<uint, MeshAck>();
        void AddAck(MeshAck a)
        {
            // Keep the most informative ack per id: prefer one that named a relay, then one with a sender.
            if (!ackMap.TryGetValue(a.PacketId, out var ex)
                || (ex.RelayNode == 0 && a.RelayNode != 0)
                || (ex.RelayNode == 0 && ex.FromNode == 0 && a.FromNode != 0))
                ackMap[a.PacketId] = a;
        }
        int count = 0;

        for (int i = 0; i < maxPackets; i++)
        {
            var fromRadio = await ReadFromRadioAsync(all: false, ct, requestTimeout).ConfigureAwait(false);
            if (fromRadio == null) break;
            count++;
            try
            {
            _state.AddFromRadio(fromRadio);

            if (fromRadio.PayloadVariantCase != FromRadio.PayloadVariantOneofCase.Packet)
            {
                // A pushed NodeInfo frame is the device telling us its node DB changed (e.g. a node-info reply, or a
                // node freshly heard). The want_config dump is drained in InitializeAsync, so anything here is a LIVE
                // update — surface it so the UI logs it and refreshes, just like a NodeInfo packet would.
                if (fromRadio.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.NodeInfo
                    && fromRadio.NodeInfo is { User: { } nu } ni && ni.Num != MyNodeNum
                    && nodeInfos.All(x => x.Node != ni.Num))
                    nodeInfos.Add(new MeshNodeInfoReply(ni.Num, NameOf(nu.LongName, nu.ShortName)));
                continue;
            }

            var pkt = fromRadio.Packet;
            var decoded = pkt.Decoded;
            if (decoded == null) continue;

            // For packets from other nodes (our own echoes excluded): remember the channel index we decoded
            // it on (so we can later address that node on a channel it can actually decrypt), record the radio
            // metrics for the Nodes window, and keep a known node's last-heard fresh from any packet type.
            if (MyNodeNum == 0 || pkt.From != MyNodeNum)
            {
                _lastHeardChannel[pkt.From] = pkt.Channel;
                RecordSignal(pkt);
                if (pkt.RxTime > 0)
                {
                    var known = _state.Nodes.FirstOrDefault(n => n.Num == pkt.From);
                    if (known != null) known.LastHeard = pkt.RxTime;
                }
            }

            // A Routing packet referencing one of our sent ids is the firmware's verdict for that packet:
            // error None is a delivery ack; any other error is a NAK (a delivery failure — mainly for DMs,
            // which the firmware retransmits itself and then reports on). Surface both so the UI can show
            // delivered vs failed without running its own retransmit for DMs.
            if (decoded.Portnum == PortNum.RoutingApp && decoded.RequestId != 0)
            {
                var routing = fromRadio.GetPayload<Routing>();
                if (routing != null)
                {
                    bool failed = routing.ErrorReason != Routing.Types.Error.None;
                    AddAck(new MeshAck(decoded.RequestId, pkt.From, (byte)ReadVarintField(pkt, 19), pkt.RxRssi,
                        pkt.RxSnr, failed, failed ? routing.ErrorReason.ToString() : ""));
                }
                continue;
            }

            // Traceroute. A want_response packet is an incoming REQUEST (the firmware answers those itself —
            // skip). Anything else on this port is a RESPONSE: surface it (keyed by the responder) so a
            // waiting traceroute window can pick it up. We don't gate on to==us / request_id, which were
            // proving too strict — the GUI matches the response to its request by node.
            if (decoded.Portnum == PortNum.TracerouteApp)
            {
                if (!decoded.WantResponse && pkt.From != MyNodeNum)
                {
                    _sentTraceroutes.Remove(decoded.RequestId);
                    var (route, snrTo, back, snrBack) = ParseRouteDiscovery(decoded.Payload);
                    traceroutes.Add(new MeshTraceroute(pkt.From, route, snrTo, back, snrBack));
                }
                else if (decoded.WantResponse && pkt.To == MyNodeNum && pkt.From != MyNodeNum)
                {
                    // An incoming traceroute REQUEST aimed at us — the firmware answers it automatically; surface
                    // it (Node = the requester, no route) so the app can note that we were traced.
                    traceroutes.Add(new MeshTraceroute(pkt.From, new List<uint>(), new List<int>(),
                        new List<uint>(), new List<int>(), IsRequest: true));
                }
                continue;
            }

            // Node info. Any NODEINFO_APP packet — a node's own broadcast announcement OR a direct reply to our
            // request — carries the sender's User, so merge it into the node DB to pick up newly heard nodes
            // automatically (the library's AddFromRadio only adds nodes from the config dump, not live packets).
            // Only a packet addressed to us is surfaced as a reply (so the UI confirms a node-info request).
            if (decoded.Portnum == PortNum.NodeinfoApp)
            {
                if (pkt.From != MyNodeNum)
                {
                    var user = User.Parser.ParseFrom(decoded.Payload);
                    bool firstSeen = MergeNodeUser(pkt.From, user, pkt.RxTime);
                    // want_response set means this NodeInfo is a REQUEST ("here's me, send me yours"); otherwise it's
                    // a reply/announcement carrying the sender's info.
                    var reply = new MeshNodeInfoReply(pkt.From, NameOf(user.LongName, user.ShortName), decoded.WantResponse);
                    if (firstSeen) newNodes.Add(reply);
                    if (pkt.To == MyNodeNum) nodeInfos.Add(reply);
                }
                continue;
            }

            // Environment telemetry (temperature / humidity / pressure) — keep the latest per node for the UI.
            if (decoded.Portnum == PortNum.TelemetryApp)
            {
                var tel = Telemetry.Parser.ParseFrom(decoded.Payload);
                // An incoming telemetry REQUEST (want_response) aimed at us — the firmware answers it; surface it
                // and don't treat the empty request payload as a reading.
                if (decoded.WantResponse && pkt.To == MyNodeNum && pkt.From != MyNodeNum)
                {
                    string kind = tel.VariantCase switch
                    {
                        Telemetry.VariantOneofCase.LocalStats => "Noise floor",
                        Telemetry.VariantOneofCase.DeviceMetrics => "Device metrics",
                        Telemetry.VariantOneofCase.EnvironmentMetrics => "Environment telemetry",
                        Telemetry.VariantOneofCase.AirQualityMetrics => "Air-quality telemetry",
                        Telemetry.VariantOneofCase.PowerMetrics => "Power telemetry",
                        _ => "Telemetry",
                    };
                    IncomingRequest?.Invoke($"{kind} request received from {DescribeNode(pkt.From)} — the device is replying.");
                    continue;
                }
                if (tel.VariantCase == Telemetry.VariantOneofCase.EnvironmentMetrics)
                {
                    var em = tel.EnvironmentMetrics;
                    if (!_environmentHistory.TryGetValue(pkt.From, out var hist))
                        _environmentHistory[pkt.From] = hist = new List<MeshEnvironment>();
                    hist.Add(new MeshEnvironment(em.Temperature, em.RelativeHumidity, em.BarometricPressure, pkt.RxTime, DateTime.Now));
                    if (hist.Count > MaxEnvironmentHistory) hist.RemoveAt(0);
                    telemetryNodes.Add(pkt.From);
                }
                // Device metrics (battery/voltage/utilization/uptime) — merge into the node DB so "show all info"
                // reflects a requested or broadcast refresh, for any node (including our own).
                else if (tel.VariantCase == Telemetry.VariantOneofCase.DeviceMetrics)
                {
                    var node = _state.Nodes.FirstOrDefault(n => n.Num == pkt.From);
                    if (node != null) node.DeviceMetrics = tel.DeviceMetrics;
                    telemetryNodes.Add(pkt.From);
                }
                // LocalStats (Telemetry field 6) carries noise_floor (field 15, dBm) — not modeled by the bundled
                // protobuf, so read it from the raw payload. Surfaced so the UI can show the requested noise floor.
                else if (ReadInt32SubField(decoded.Payload.ToByteArray(), 6, 15) is int noise)
                {
                    _noiseFloor[pkt.From] = noise;
                    noiseFloors.Add(new MeshNoiseFloor(pkt.From, DescribeNode(pkt.From), noise));
                }
                continue;
            }

            // Position (a node's broadcast, or a reply to our request). AddFromRadio does NOT merge a standalone
            // POSITION_APP packet into the node DB — only NodeInfo carries a position — so we store the fix
            // ourselves. Without this, the map / "open in Google Maps" finds nothing for any node we haven't also
            // received NodeInfo from, even though we just logged "position received from <node>".
            if (decoded.Portnum == PortNum.PositionApp && pkt.From != MyNodeNum)
            {
                var p = Position.Parser.ParseFrom(decoded.Payload);
                // An incoming position REQUEST (want_response) aimed at us — the firmware answers it; surface it.
                // (Still store any position it carries, for the rare "exchange positions" case.)
                if (decoded.WantResponse && pkt.To == MyNodeNum)
                    IncomingRequest?.Invoke($"Position request received from {DescribeNode(pkt.From)} — the device is replying.");
                if (p.LatitudeI != 0 || p.LongitudeI != 0)   // ignore an empty/precision-stripped position
                {
                    positions.Add(new MeshPositionReport(pkt.From, DescribeNode(pkt.From), p.LatitudeI / 1e7, p.LongitudeI / 1e7));
                    long lastHeard = pkt.RxTime > 0 ? (long)pkt.RxTime
                        : (_positionCache.TryGetValue(pkt.From, out var prev) ? prev.LastHeard : 0);
                    _positionCache[pkt.From] = (p.LatitudeI / 1e7, p.LongitudeI / 1e7, lastHeard, p.Time);   // works with no NodeInfo
                    AppendPositionHistory(pkt.From, p.LatitudeI / 1e7, p.LongitudeI / 1e7, lastHeard, p.Time);   // keep a track of the latest N
                    var node = _state.Nodes.FirstOrDefault(n => n.Num == pkt.From);
                    if (node != null) node.Position = p;   // keep the live node DB in sync when we do know the node
                }
                continue;
            }

            // Admin responses (e.g. to our remote get_device_metadata) carry a session_passkey we must echo on the
            // next mutating admin to that node. Capture it so remote-admin ops can proceed.
            if (decoded.Portnum == PortNum.AdminApp)
            {
                try
                {
                    var am = AdminMessage.Parser.ParseFrom(decoded.Payload);
                    if (!am.SessionPasskey.IsEmpty) _sessionPasskeys[pkt.From] = (am.SessionPasskey, DateTime.UtcNow);
                    AdminActivity?.Invoke($"← Admin from {DescribeNode(pkt.From)}: {DescribeAdmin(am)}");
                }
                catch { /* not a parseable admin response */ }
                continue;
            }

            var text = fromRadio.GetPayload<string>();
            if (text == null) continue;
            // A packet whose sender is our own node. Normally that's our own message looping back; but when several
            // apps share one device through Meshtastic.Proxy they all share this node id, so a packet from our node
            // that we DIDN'T send is actually a PEER client's message — let it fall through to be shown.
            if (MyNodeNum != 0 && pkt.From == MyNodeNum && IsOwnSend(pkt.Id))
            {
                // Our own message coming back. Only a rebroadcast by ANOTHER node proves the broadcast actually
                // propagated — treat that as an ack so the sender sees "relayed by <node>". But the device also
                // loops our outgoing packet straight back (to confirm it queued/transmitted it); that echo
                // carries our OWN relay byte (the transmitter is us) or 0, and is NOT proof anyone received it,
                // so it must not mark the message relayed.
                byte relay = (byte)ReadVarintField(pkt, 19);
                if (relay != 0 && relay != (byte)(MyNodeNum & 0xFF))
                    AddAck(new MeshAck(pkt.Id, 0, relay, pkt.RxRssi, pkt.RxSnr));
                continue; // don't show our own echo as a received message
            }

            // Decrypt with this channel's app-level key if one is set; if it doesn't decrypt
            // (foreign/plain/wrong-key traffic), pass the raw payload through and flag it.
            bool decryptFailed = false;
            string channelKey = GetChannelKey(pkt.Channel);
            if (channelKey.Length > 0)
            {
                if (AesText.TryDecrypt(text, channelKey, out var decrypted)) text = decrypted;
                else decryptFailed = true;
            }

            messages.Add(new MeshTextMessage(pkt.From, pkt.Channel, text, pkt.Id, pkt.RxTime, decryptFailed,
                pkt.RxRssi, pkt.RxSnr, (int)pkt.HopLimit, (int)ReadVarintField(pkt, 15), (byte)ReadVarintField(pkt, 19),
                pkt.To, decoded.ReplyId, decoded.Emoji != 0, ReadVarintField(pkt, 14) != 0));   // via_mqtt = field 14
            }
            catch { /* skip a single packet we couldn't process; keep draining the rest of the batch */ }
        }

        return new MeshReceiveResult(messages, ackMap.Values.ToList(), count, traceroutes, nodeInfos, telemetryNodes, newNodes, positions, noiseFloors);
    }

    // Parses a RouteDiscovery payload for the forward route (field 1) + SNR (field 2) and the return route
    // (field 3) + SNR (field 4). The bundled protobuf only models field 1, so we read the bytes directly to also
    // recover route_back and the per-hop SNR arrays, which newer firmware includes in the reply. Routes are
    // packed repeated fixed32 (node numbers); SNR arrays are packed repeated int32 (SNR×4, -128 = unknown).
    private static (List<uint> Route, List<int> SnrTowards, List<uint> RouteBack, List<int> SnrBack) ParseRouteDiscovery(ByteString payload)
    {
        var route = new List<uint>();
        var back = new List<uint>();
        var snrTo = new List<int>();
        var snrBack = new List<int>();
        var input = new CodedInputStream(payload.ToByteArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            int field = WireFormat.GetTagFieldNumber(tag);
            var type = WireFormat.GetTagWireType(tag);
            var routeTarget = field == 1 ? route : field == 3 ? back : null;
            var snrTarget = field == 2 ? snrTo : field == 4 ? snrBack : null;
            if (routeTarget != null && type == WireFormat.WireType.LengthDelimited)
            {
                // Packed repeated fixed32: a length-delimited blob of little-endian 4-byte values.
                var bytes = input.ReadBytes().ToByteArray();
                for (int i = 0; i + 4 <= bytes.Length; i += 4)
                    routeTarget.Add(BitConverter.ToUInt32(bytes, i));
            }
            else if (routeTarget != null && type == WireFormat.WireType.Fixed32)   // unpacked (fallback)
                routeTarget.Add(input.ReadFixed32());
            else if (snrTarget != null && type == WireFormat.WireType.LengthDelimited)
            {
                // Packed repeated int32: a length-delimited blob of varints.
                var sub = new CodedInputStream(input.ReadBytes().ToByteArray());
                while (!sub.IsAtEnd) snrTarget.Add(sub.ReadInt32());
            }
            else if (snrTarget != null && type == WireFormat.WireType.Varint)   // unpacked (fallback)
                snrTarget.Add(input.ReadInt32());
            else
                input.SkipLastField();
        }
        return (route, snrTo, back, snrBack);
    }

    // Reads a varint field off the wire that the bundled protobuf predates (so it lands in the packet's
    // unknown fields). Re-serialize (unknown fields are retained) and scan for the tag ourselves.
    // Used for hop_start (field 15) and relay_node (field 19). Returns 0 when the field is absent.
    private static uint ReadVarintField(MeshPacket pkt, int fieldNumber) => ReadVarintField(pkt.ToByteArray(), fieldNumber);

    // Scans serialized protobuf bytes for a varint field (0 when absent). Used for fields the bundled protobuf
    // doesn't model — e.g. LoRaConfig.ignore_mqtt (104), config_ok_to_mqtt (105), pa_fan_disabled (15).
    private static uint ReadVarintField(byte[] bytes, int fieldNumber)
    {
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == fieldNumber &&
                WireFormat.GetTagWireType(tag) == WireFormat.WireType.Varint)
                return input.ReadUInt32();
            input.SkipLastField();
        }
        return 0;
    }

    /// <summary>Best-effort description of a relay_node value — only the LAST BYTE of the node that
    /// relayed the packet to us, so it can't always be resolved uniquely. Returns the node's name when
    /// exactly one known node matches that byte; otherwise "!..XX" (the byte), with "(one of N)" when
    /// several known nodes share it. Empty when the value is 0 (no relay reported).</summary>
    public string DescribeRelayNode(byte relayByte)
    {
        if (relayByte == 0) return "";
        var nums = new HashSet<uint>();
        foreach (var n in _state.Nodes) if ((byte)(n.Num & 0xFF) == relayByte) nums.Add(n.Num);
        foreach (var kv in _nameCache) if ((byte)(kv.Key & 0xFF) == relayByte) nums.Add(kv.Key);
        if (nums.Count == 1) return DescribeNode(nums.First());
        if (nums.Count == 0) return $"!..{relayByte:x2}";
        // Several known nodes share that last byte; list them as candidates since we can't tell which relayed.
        return $"!..{relayByte:x2} (one of: {string.Join(" / ", nums.Select(DescribeNode))})";
    }

    /// <summary>Convenience wrapper returning only the received text messages.</summary>
    public async Task<List<MeshTextMessage>> ReceiveTextMessagesAsync(CancellationToken ct = default)
        => (await ReceiveAsync(500, ct).ConfigureAwait(false)).Texts;

    public string DescribeNode(uint nodeNum)
    {
        var node = _state.Nodes.Find(n => n.Num == nodeNum);
        string name = NameOf(node?.User?.LongName, node?.User?.ShortName);
        if (name.Length == 0 && _nameCache.TryGetValue(nodeNum, out var cached)) name = cached;
        return name.Length > 0 ? name : $"!{nodeNum:x8}";
    }

    /// <summary>
    /// Channels configured on the device (enabled ones only), as discovered during
    /// <see cref="InitializeAsync"/>. Empty if the handshake didn't report any.
    /// </summary>
    public IReadOnlyList<MeshChannel> GetAvailableChannels()
    {
        if (_seededChannels != null)
            return _seededChannels;

        var channels = _state.Channels
            .Where(c => c.Role != Channel.Types.Role.Disabled)
            .GroupBy(c => c.Index)         // a config dump can be drained more than once
            .Select(g => g.Last())         // keep the most recent entry per index
            .Select(ToMeshChannel)
            .ToList();

        // Channel 0 is always the (enabled) primary on a Meshtastic device, but the HTTP config
        // stream only emits it right after a full config_complete — repeated want-config requests
        // cycle indices 1..7 and skip it. Guarantee it's present so the primary is always selectable.
        if (channels.All(c => c.Index != 0))
            channels.Add(new MeshChannel(0, string.Empty, Channel.Types.Role.Primary.ToString()));

        return channels.OrderBy(c => c.Index).ToList();
    }

    private static MeshChannel ToMeshChannel(Channel c)
    {
        var psk = c.Settings?.Psk;
        bool hasKey = (psk?.Length ?? 0) > 0;
        return new MeshChannel((uint)c.Index, c.Settings?.Name ?? string.Empty, c.Role.ToString(),
                               hasKey, hasKey ? Convert.ToBase64String(psk!.ToByteArray()) : string.Empty,
                               c.Settings?.UplinkEnabled ?? false, c.Settings?.DownlinkEnabled ?? false,
                               ReadPositionPrecision(c.Settings));
    }

    // Reads position_precision (ChannelSettings.module_settings (field 7) → position_precision (field 1)). The
    // bundled ChannelSettings predates module_settings, but protobuf keeps it as an UNKNOWN field that round-
    // trips through ToByteArray(), so we parse the bytes ourselves. Returns -1 when the device sent no
    // module_settings (so the value is unknown), else the precision (0 = no position, 32 = full).
    private static int ReadPositionPrecision(ChannelSettings? settings)
    {
        if (settings == null) return -1;
        try
        {
            var input = new CodedInputStream(settings.ToByteArray());
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagFieldNumber(tag) == 7 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    var sub = new CodedInputStream(input.ReadBytes().ToByteArray());   // module_settings sub-message
                    uint t2;
                    while ((t2 = sub.ReadTag()) != 0)
                    {
                        if (WireFormat.GetTagFieldNumber(t2) == 1 && WireFormat.GetTagWireType(t2) == WireFormat.WireType.Varint)
                            return (int)sub.ReadUInt32();
                        sub.SkipLastField();
                    }
                    return 0;   // module_settings present but no precision field → 0
                }
                input.SkipLastField();
            }
        }
        catch { /* malformed — treat as unknown */ }
        return -1;
    }

    // Returns the raw payload bytes of the first length-delimited field <fieldNumber> in <message>, or null
    // when it's absent. Used to lift the device's existing module_settings (field 7) out of a ChannelSettings.
    private static byte[]? ExtractLengthDelimitedField(byte[] message, int fieldNumber)
    {
        try
        {
            var input = new CodedInputStream(message);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagFieldNumber(tag) == fieldNumber &&
                    WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                    return input.ReadBytes().ToByteArray();
                input.SkipLastField();
            }
        }
        catch { /* malformed — treat as absent */ }
        return null;
    }

    // Reads the value of a varint field <fieldNumber> from <message>, or null when it's absent.
    private static uint? ReadVarintSubField(byte[] message, int fieldNumber)
    {
        try
        {
            var input = new CodedInputStream(message);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagFieldNumber(tag) == fieldNumber && WireFormat.GetTagWireType(tag) == WireFormat.WireType.Varint)
                    return input.ReadUInt32();
                input.SkipLastField();
            }
        }
        catch { /* malformed — treat as absent */ }
        return null;
    }

    // Builds a ModuleSettings sub-message with position_precision (field 1) set to <precision>, preserving the
    // device's is_client_muted (field 2) from <existing> if it was set. ModuleSettings has only these two fields.
    private static List<byte> BuildModuleSettings(byte[]? existing, uint precision)
    {
        var output = new List<byte>();
        WriteTag(output, 1, 0); WriteVarint(output, precision);                       // position_precision
        if (existing != null && ReadVarintSubField(existing, 2) is uint muted && muted != 0)
        { WriteTag(output, 2, 0); WriteVarint(output, muted); }                       // is_client_muted (preserved)
        return output;
    }

    // ---- Channel management (writes real Meshtastic channels to the device via admin messages) -------

    /// <summary>
    /// Re-reads the device's full channel set (all 8 slots, including Disabled ones so the caller can see
    /// which are free to create into). Forces a complete config drain rather than the channels-only
    /// shortcut, so slot 0 and the real names/roles/PSK-present flags are accurate.
    /// </summary>
    public async Task<IReadOnlyList<MeshChannel>> GetDeviceChannelsAsync(CancellationToken ct = default)
    {
        _lastChannelReadOk = false;   // set true by ReloadChannelsAsync only once the device actually answers with channels
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try { await ReloadChannelsAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* return what we have */ }
        return ProjectAllChannels();
    }

    /// <summary>
    /// Whether the most recent <see cref="GetDeviceChannelsAsync"/> actually read channel data from the device
    /// (vs. timing out with no response, in which case it returns only a synthetic primary channel). False after
    /// a failed/incomplete read; callers should treat that as a fetch failure rather than a real channel set.
    /// </summary>
    public bool LastChannelReadOk => _lastChannelReadOk;
    private bool _lastChannelReadOk;

    private async Task ReloadChannelsAsync(CancellationToken ct)
    {
        _seededChannels = null;            // cached channels must not mask a fresh read
        _state.Channels.Clear();

        // Ask for a full config dump and drain it, TOLERATING transient empty reads (FetchConfigAsync bails
        // on the first empty /fromradio body, which on a device in live-packet mode often happens before the
        // dump starts — so it returns no channels). Channels (slots 0–7) arrive before the node DB, so we
        // stop as soon as all 8 are seen instead of draining hundreds of nodes to config_complete.
        await WriteToRadioAsync(new ToRadioMessageFactory().CreateWantConfigMessage(), ct).ConfigureAwait(false);
        var seen = new HashSet<int>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var fromRadio = await ReadFromRadioAsync(all: false, ct).ConfigureAwait(false);
            if (fromRadio == null) { await Task.Delay(150, ct).ConfigureAwait(false); continue; }
            _state.AddFromRadio(fromRadio);
            if (fromRadio.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.Channel)
            {
                seen.Add(fromRadio.Channel.Index);
                _lastChannelReadOk = true;   // the device answered with real channel data
            }
            if (seen.Count >= ChannelSlotCount) break;     // all channel slots collected
            if (fromRadio.PayloadVariantCase == FromRadio.PayloadVariantOneofCase.ConfigCompleteId)
            {
                _lastChannelReadOk = true;   // a complete config dump counts as a successful read
                break;
            }
        }
        if (MyNodeNum == 0) MyNodeNum = _state.MyNodeInfo?.MyNodeNum ?? 0;
    }

    private IReadOnlyList<MeshChannel> ProjectAllChannels()
    {
        var list = _state.Channels
            .GroupBy(c => c.Index)
            .Select(g => g.Last())
            .Select(ToMeshChannel)
            .ToList();
        // Guarantee the primary (index 0) is present even if the dump didn't emit it (some firmware only
        // sends index 0 right after a full config_complete).
        if (list.All(c => c.Index != 0))
            list.Add(new MeshChannel(0, string.Empty, Channel.Types.Role.Primary.ToString()));
        return list.OrderBy(c => c.Index).ToList();
    }

    /// <summary>
    /// Creates a new channel (index null → first free/Disabled slot) or updates an existing one, writing
    /// name + PSK + role to the radio. PSK is "none"/empty → open channel, "random" → a fresh 256-bit key,
    /// otherwise SHA-256 of the passphrase. The primary channel (index 0) keeps its Primary role.
    /// </summary>
    public Task<ChannelOpResult> AddOrUpdateChannelAsync(uint? index, string name, string psk,
        Channel.Types.Role role = Channel.Types.Role.Secondary, CancellationToken ct = default)
        => WithChannelTimeout(index ?? 0, ct, async token =>
        {
            await EnsureChannelsLoadedAsync(token).ConfigureAwait(false);

            Channel? channel = index.HasValue
                ? FindChannel(index.Value)
                : _state.Channels.Where(c => c.Index > 0 && c.Role == Channel.Types.Role.Disabled)
                                 .OrderBy(c => c.Index).FirstOrDefault();

            if (channel == null)
                return index.HasValue
                    ? new ChannelOpResult(false, index.Value, $"Channel {index} was not found on the device.")
                    : new ChannelOpResult(false, 0, "All 8 channel slots are in use — disable one first.");

            channel.Settings ??= new ChannelSettings();
            channel.Settings.Name = name ?? string.Empty;
            ApplyPsk(channel.Settings, psk);
            if (channel.Index > 0)   // never change the primary's role
                channel.Role = role == Channel.Types.Role.Disabled ? Channel.Types.Role.Secondary : role;

            return await RunChannelAdminAsync(channel, token).ConfigureAwait(false);
        });

    /// <summary>
    /// Creates a new channel (index null → first free/Disabled slot) or updates an existing one, writing name,
    /// PSK, MQTT uplink/downlink and position-sharing in a SINGLE set_channel request. PSK semantics match
    /// <see cref="AddOrUpdateChannelAsync"/> ("none"/empty → open, "random" → a fresh 256-bit key, a base64 AES
    /// key → that raw key, otherwise the SHA-256 of the passphrase; a null psk leaves the existing key untouched).
    /// Position lives in module_settings.position_precision (32 = share, 0 = off), which the bundled protobuf
    /// doesn't model, so the channel bytes are hand-built (as in <see cref="SetChannelOptionsAsync"/>) to carry
    /// it alongside the typed name/PSK/uplink/downlink fields. Returns the affected index on success.
    /// </summary>
    public Task<ChannelOpResult> SetChannelAsync(uint? index, string name, string? psk,
        bool uplink, bool downlink, bool sharePosition,
        Channel.Types.Role role = Channel.Types.Role.Secondary, CancellationToken ct = default)
        => WithChannelTimeout(index ?? 0, ct, async token =>
        {
            await EnsureChannelsLoadedAsync(token).ConfigureAwait(false);

            Channel? channel = index.HasValue
                ? FindChannel(index.Value)
                : _state.Channels.Where(c => c.Index > 0 && c.Role == Channel.Types.Role.Disabled)
                                 .OrderBy(c => c.Index).FirstOrDefault();

            if (channel == null)
                return index.HasValue
                    ? new ChannelOpResult(false, index.Value, $"Channel {index} was not found on the device.")
                    : new ChannelOpResult(false, 0, "All 8 channel slots are in use — disable one first.");

            channel.Settings ??= new ChannelSettings();
            channel.Settings.Name = name ?? string.Empty;
            ApplyPsk(channel.Settings, psk);
            if (channel.Index > 0)   // never change the primary's role
                channel.Role = role == Channel.Types.Role.Disabled ? Channel.Types.Role.Secondary : role;

            uint idx = (uint)channel.Index;

            // Capture the device's existing module_settings (unknown field 7) and rebuild the channel bytes by
            // hand so position_precision travels with the typed fields without duplicating field 7 — see the
            // detailed note in SetChannelOptionsAsync.
            byte[]? existingModule = ExtractLengthDelimitedField(channel.Settings.ToByteArray(), 7);
            var clean = new ChannelSettings
            {
                Psk = channel.Settings.Psk,
                Name = channel.Settings.Name,
                Id = channel.Settings.Id,
                UplinkEnabled = uplink,
                DownlinkEnabled = downlink,
            };
            var settings = clean.ToByteArray().ToList();
            var module = BuildModuleSettings(existingModule, sharePosition ? 32u : 0u);
            WriteTag(settings, 7, 2); WriteVarint(settings, (uint)module.Count); settings.AddRange(module);

            var ch = new List<byte>();
            WriteTag(ch, 1, 0); WriteVarint(ch, (uint)channel.Index);
            WriteTag(ch, 2, 2); WriteVarint(ch, (uint)settings.Count); ch.AddRange(settings);
            WriteTag(ch, 3, 0); WriteVarint(ch, (uint)(int)channel.Role);

            var admin = new List<byte>();
            WriteTag(admin, 33, 2); WriteVarint(admin, (uint)ch.Count); admin.AddRange(ch);

            return await RunRawChannelAdminAsync(idx, admin.ToArray(), token).ConfigureAwait(false);
        });

    /// <summary>Disables (removes) a channel. The primary channel (index 0) cannot be disabled.</summary>
    public Task<ChannelOpResult> DisableChannelAsync(uint index, CancellationToken ct = default)
    {
        if (index == 0)
            return Task.FromResult(new ChannelOpResult(false, 0, "The primary channel (0) cannot be disabled."));

        return WithChannelTimeout(index, ct, async token =>
        {
            await EnsureChannelsLoadedAsync(token).ConfigureAwait(false);
            var channel = FindChannel(index);
            if (channel == null)
                return new ChannelOpResult(false, index, $"Channel {index} was not found on the device.");

            channel.Role = Channel.Types.Role.Disabled;
            return await RunChannelAdminAsync(channel, token).ConfigureAwait(false);
        });
    }

    /// <summary>Runs a channel admin op under an overall timeout so a hung request fails fast and clearly
    /// (rather than bubbling up the raw 30s HttpClient timeout).</summary>
    private async Task<ChannelOpResult> WithChannelTimeout(uint idx, CancellationToken ct,
        Func<CancellationToken, Task<ChannelOpResult>> op)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            return await op(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ChannelOpResult(false, idx,
                "Timed out talking to the radio. The device may not accept channel edits over the HTTP API " +
                "(newer firmware can require an admin key/session for channel changes).");
        }
        catch (Exception ex)
        {
            return new ChannelOpResult(false, idx, ex.Message);
        }
    }

    private Channel? FindChannel(uint index) =>
        _state.Channels.Where(c => c.Index == (int)index).LastOrDefault();

    private async Task EnsureChannelsLoadedAsync(CancellationToken ct)
    {
        // Admin writes mutate the live Channel objects in _state and target our own node, so both the
        // channel list and MyNodeNum must be populated (the cache/Seed connect path skips the dump).
        if (_state.Channels.Count == 0 || MyNodeNum == 0)
            await ReloadChannelsAsync(ct).ConfigureAwait(false);
    }

    private static void ApplyPsk(ChannelSettings settings, string? psk)
    {
        if (psk == null) return;                       // leave the existing key untouched
        if (psk.Length == 0 || psk == "none")
            settings.Psk = ByteString.Empty;
        else if (psk == "random")
            settings.Psk = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
        else if (TryDecodeRawKey(psk, out var raw))
            // A base64 string that decodes to a real AES key length is used as the raw PSK, so it
            // round-trips exactly to the base64 the device reports back (PSK (selected)).
            settings.Psk = ByteString.CopyFrom(raw);
        else
            // Otherwise it's a passphrase — hash it to a 256-bit key.
            settings.Psk = ByteString.CopyFrom(SHA256.HashData(Encoding.UTF8.GetBytes(psk)));
    }

    private static bool TryDecodeRawKey(string s, out byte[] key)
    {
        key = Array.Empty<byte>();
        try
        {
            var bytes = Convert.FromBase64String(s.Trim());
            if (bytes.Length is 16 or 32) { key = bytes; return true; }
        }
        catch (FormatException) { /* not base64 → treat as a passphrase */ }
        return false;
    }

    /// <summary>
    /// BeginEdit → SetChannel → CommitEdit. A routing ack is best-effort: an explicit error is decisive,
    /// but many firmwares don't ack a self-addressed admin packet over HTTP, so "no ack" is treated as
    /// success after the commit. (The caller re-reads the channel list to show the result — doing that
    /// read-back here too would double the work and overrun the operation timeout.)
    /// </summary>
    private async Task<ChannelOpResult> RunChannelAdminAsync(Channel channel, CancellationToken ct)
    {
        uint idx = (uint)channel.Index;
        var dest = _destination ?? (MyNodeNum != 0 ? MyNodeNum : (uint?)null);
        var admin = new AdminMessageFactory(_state, dest);
        var toRadio = new ToRadioMessageFactory();

        await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(admin.CreateBeginEditSettingsMessage()), ct).ConfigureAwait(false);

        var setPkt = admin.CreateSetChannelMessage(channel);
        await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(setPkt), ct).ConfigureAwait(false);

        // Best-effort ack: an explicit error stops us; otherwise commit and report success.
        var error = await AwaitRoutingAckAsync(setPkt.Id, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        if (error is { } e && e != Routing.Types.Error.None)
            return new ChannelOpResult(false, idx, $"The radio rejected the change: {e}.");

        await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(admin.CreateCommitEditSettingsMessage()), ct).ConfigureAwait(false);
        return new ChannelOpResult(true, idx, null);
    }

    /// <summary>
    /// Sets a channel's MQTT uplink/downlink and whether position may be shared on it, preserving the name,
    /// PSK and role. Uplink/downlink are typed fields; position lives in module_settings.position_precision
    /// (32 = share, 0 = off) which the bundled protobuf lacks, so that one field is appended by hand. NOTE:
    /// position can't be read back (it's an unknown field), so the caller decides it explicitly on each save.
    /// </summary>
    public Task<ChannelOpResult> SetChannelOptionsAsync(uint index, bool uplink, bool downlink, bool sharePosition, CancellationToken ct = default)
        => WithChannelTimeout(index, ct, async token =>
        {
            await EnsureChannelsLoadedAsync(token).ConfigureAwait(false);
            var channel = FindChannel(index);
            if (channel == null)
                return new ChannelOpResult(false, index, $"Channel {index} was not found on the device.");

            channel.Settings ??= new ChannelSettings();

            // The device's channel often already carries a module_settings (field 7) that the bundled proto
            // doesn't model — it round-trips as an UNKNOWN field. If we serialise channel.Settings as-is and
            // then append our own field 7, the message ends up with field 7 TWICE, which firmware rejects /
            // mishandles. So: capture the device's existing module_settings, rebuild ChannelSettings from its
            // TYPED fields only (dropping the unknown copy), then append exactly one fresh module_settings.
            byte[]? existingModule = ExtractLengthDelimitedField(channel.Settings.ToByteArray(), 7);
            var clean = new ChannelSettings
            {
                Psk = channel.Settings.Psk,
                Name = channel.Settings.Name,
                Id = channel.Settings.Id,
                UplinkEnabled = uplink,
                DownlinkEnabled = downlink,
            };
            var settings = clean.ToByteArray().ToList();
            var module = BuildModuleSettings(existingModule, sharePosition ? 32u : 0u);    // override position_precision only
            WriteTag(settings, 7, 2); WriteVarint(settings, (uint)module.Count); settings.AddRange(module);   // ChannelSettings.module_settings

            // Channel { index(1), settings(2), role(3) }.
            var ch = new List<byte>();
            WriteTag(ch, 1, 0); WriteVarint(ch, (uint)channel.Index);
            WriteTag(ch, 2, 2); WriteVarint(ch, (uint)settings.Count); ch.AddRange(settings);
            WriteTag(ch, 3, 0); WriteVarint(ch, (uint)(int)channel.Role);

            // AdminMessage { set_channel = 33 }.
            var admin = new List<byte>();
            WriteTag(admin, 33, 2); WriteVarint(admin, (uint)ch.Count); admin.AddRange(ch);

            return await RunRawChannelAdminAsync(index, admin.ToArray(), token).ConfigureAwait(false);
        });

    // Begin → (raw set_channel admin) → commit, mirroring RunChannelAdminAsync but with a hand-built payload.
    private async Task<ChannelOpResult> RunRawChannelAdminAsync(uint index, byte[] setChannelAdmin, CancellationToken ct)
    {
        var dest = _destination ?? (MyNodeNum != 0 ? MyNodeNum : (uint?)null);
        var admin = new AdminMessageFactory(_state, dest);
        var toRadio = new ToRadioMessageFactory();
        await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(admin.CreateBeginEditSettingsMessage()), ct).ConfigureAwait(false);

        var setPkt = new MeshPacket
        {
            Channel = _state.GetAdminChannelIndex(),
            WantAck = false,
            To = dest ?? (MyNodeNum != 0 ? MyNodeNum : 0u),
            Id = (uint)Random.Shared.Next(1, int.MaxValue),   // a real random packet id (avoid the 0/constant-id mesh-dedup trap)
            HopLimit = _state.GetHopLimitOrDefault(),
            Decoded = new Data { Portnum = PortNum.AdminApp, Payload = ByteString.CopyFrom(setChannelAdmin), WantResponse = true },
        };
        await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(setPkt), ct).ConfigureAwait(false);

        var error = await AwaitRoutingAckAsync(setPkt.Id, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        if (error is { } e && e != Routing.Types.Error.None)
            return new ChannelOpResult(false, index, $"The radio rejected the change: {e}.");

        await WriteToRadioAsync(toRadio.CreateMeshPacketMessage(admin.CreateCommitEditSettingsMessage()), ct).ConfigureAwait(false);
        return new ChannelOpResult(true, index, null);
    }

    /// <summary>
    /// Polls /fromradio until a Routing ack for <paramref name="requestId"/> arrives (returning its error,
    /// None on success) or the timeout elapses (returns null). Other packets seen meanwhile are folded into
    /// device state but not otherwise dispatched — callers run this with the UI poll loop paused.
    /// </summary>
    private async Task<Routing.Types.Error?> AwaitRoutingAckAsync(uint requestId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var fr = await ReadFromRadioAsync(all: false, ct).ConfigureAwait(false);
            if (fr == null) { await Task.Delay(150, ct).ConfigureAwait(false); continue; }
            _state.AddFromRadio(fr);
            if (fr.PayloadVariantCase != FromRadio.PayloadVariantOneofCase.Packet) continue;
            var decoded = fr.Packet.Decoded;
            // Match our request id; also accept a local ack carrying id 0 (self-addressed admin packets).
            if (decoded?.Portnum == PortNum.RoutingApp && (decoded.RequestId == requestId || decoded.RequestId == 0))
                return fr.GetPayload<Routing>()?.ErrorReason ?? Routing.Types.Error.None;
        }
        return null;
    }

    // Packet ids WE sent, so the device's loopback echo (from == MyNodeNum) can be told apart from a *peer* client's
    // send when several apps share one device through Meshtastic.Proxy (all clients share the device's node id). Our
    // own echo is suppressed; a peer's send falls through and is shown. Bounded so it can't grow without limit.
    private readonly HashSet<uint> _ownSentIds = new();
    private readonly Queue<uint> _ownSentOrder = new();

    /// <summary>True if <paramref name="id"/> is one of our own recent sends (vs. a peer client's via the proxy).</summary>
    private bool IsOwnSend(uint id) => id != 0 && _ownSentIds.Contains(id);

    private void RecordOwnSend(uint id)
    {
        if (id == 0 || !_ownSentIds.Add(id)) return;
        _ownSentOrder.Enqueue(id);
        if (_ownSentOrder.Count > 512) _ownSentIds.Remove(_ownSentOrder.Dequeue());
    }

    /// <summary>Raised for every admin message this client SENDS ("→ …") or RECEIVES ("← …"), with a
    /// human-readable description, so the app can log admin activity in system messages. May fire off the UI
    /// thread — subscribers should marshal to the UI thread.</summary>
    public event Action<string>? AdminActivity;

    /// <summary>Raised when another node sends US a request the firmware auto-answers (position / telemetry /
    /// noise-floor), with a human-readable description, so the app can log it in system messages. May fire off
    /// the UI thread — subscribers should marshal to the UI thread.</summary>
    public event Action<string>? IncomingRequest;

    // Human-readable label for an admin message (its oneof variant, plus the target node for node-directed ones).
    private static string DescribeAdmin(AdminMessage a)
    {
        var c = a.PayloadVariantCase;
        if (c == AdminMessage.PayloadVariantOneofCase.None)
            return a.SessionPasskey.IsEmpty ? "(empty)" : "session key";
        string extra = c switch
        {
            AdminMessage.PayloadVariantOneofCase.SetFavoriteNode => $" !{a.SetFavoriteNode:x8}",
            AdminMessage.PayloadVariantOneofCase.RemoveFavoriteNode => $" !{a.RemoveFavoriteNode:x8}",
            AdminMessage.PayloadVariantOneofCase.SetIgnoredNode => $" !{a.SetIgnoredNode:x8}",
            AdminMessage.PayloadVariantOneofCase.RemoveIgnoredNode => $" !{a.RemoveIgnoredNode:x8}",
            AdminMessage.PayloadVariantOneofCase.RemoveByNodenum => $" !{a.RemoveByNodenum:x8}",
            _ => "",
        };
        return c + extra;
    }

    private async Task WriteToRadioAsync(ToRadio toRadio, CancellationToken ct)
    {
        if (toRadio.Packet is { } p && p.Id != 0) RecordOwnSend(p.Id);
        // Surface outgoing admin messages so the app can log them (fired before the write, on the caller's thread).
        if (AdminActivity != null && toRadio.Packet?.Decoded is { Portnum: PortNum.AdminApp } dsent)
        {
            try { AdminActivity($"→ Admin to {DescribeNode(toRadio.Packet.To)}: {DescribeAdmin(AdminMessage.Parser.ParseFrom(dsent.Payload))}"); }
            catch { /* unparseable admin payload — skip logging */ }
        }
        _state.AddToRadio(toRadio);
        await _transport.WriteAsync(toRadio.ToByteArray(), ct).ConfigureAwait(false);
    }

    // <paramref name="requestTimeout"/> bounds THIS single request (separate from the transport's own default),
    // so a caller like the poll loop can fail fast when the device is unreachable instead of hanging.
    private async Task<FromRadio?> ReadFromRadioAsync(bool all, CancellationToken ct, TimeSpan? requestTimeout = null)
    {
        var bytes = await _transport.ReadAsync(all, ct, requestTimeout).ConfigureAwait(false);
        return bytes == null ? null : FromRadio.Parser.ParseFrom(bytes);
    }

    public void Dispose() => _transport.Dispose();

    /// <summary>Closes the link, awaiting the transport's clean shutdown when it supports one (e.g. BLE awaits the
    /// GATT disconnect). Used on app close so a Bluetooth link doesn't leak its GATT. Falls back to <see cref="Dispose"/>.</summary>
    public async Task CloseAsync()
    {
        if (_transport is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
        else _transport.Dispose();
    }
}
