using System.IO;
using System.Text.Json;
using ChessOverMesh.Mesh;

namespace ChessOverMesh.Gui;

/// <summary>
/// Remembers each device's channel list and node number on disk, keyed by host. This lets a
/// reconnect skip the want-config dump (and its long "sync"), so reception is live immediately.
/// </summary>
internal static class DeviceCache
{
    public sealed class CachedChannel
    {
        public uint Index { get; set; }
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public bool Uplink { get; set; }
        public bool Downlink { get; set; }
        public int PositionPrecision { get; set; } = -1;   // -1 = unknown (device didn't report module_settings)
        public bool HasKey { get; set; }                   // whether the channel has a PSK (the key itself is NOT cached)
    }

    /// <summary>Per-node chat preferences: whether to surface DMs from/to this node in the chat (and list it
    /// as a TX target), and whether to ignore incoming DMs from it. Block wins over Dm (a blocked node is never
    /// DM-enabled). A node with neither flag set isn't stored.</summary>
    public sealed class NodePrefs
    {
        public bool Dm { get; set; }
        public bool Block { get; set; }
    }

    /// <summary>One cached environment telemetry reading (temperature °C, humidity %, pressure hPa) with the
    /// device rx_time (epoch s, 0 if unknown) and the local time it was received. Persisted per node.</summary>
    public sealed class TelemetryReading
    {
        public float Temperature { get; set; }
        public float Humidity { get; set; }
        public float Pressure { get; set; }
        public uint RxTime { get; set; }
        public DateTime ReceivedAt { get; set; }
    }

    /// <summary>One cached chat line: the rendered message text, its dim metadata line, and the local time it
    /// was shown (for ordering when reloading several channels together).</summary>
    public sealed class ChatMessage
    {
        public string Text { get; set; } = "";
        public string Detail { get; set; } = "";
        public DateTime Time { get; set; }
        public string? Id { get; set; }   // stable id so a single message can be removed from the cache (null for legacy entries)
        public uint RxTime { get; set; }  // the message's device rx_time (epoch s, 0 if unknown), for proxy catch-up
        public DateTime ExpiresAt { get; set; }   // sender-set self-destruct time (local); default = never expires
    }

    /// <summary>A cached node position (decimal degrees), persisted so the map shows last-known locations.</summary>
    public sealed class CachedPosition
    {
        public double Lat { get; set; }
        public double Lon { get; set; }
        public long LastHeard { get; set; }   // epoch seconds the node was last heard (0 if unknown)
        public long PosTime { get; set; }      // epoch seconds this position was recorded (0 if unknown)
    }

    /// <summary>Per-channel options we set on the device: MQTT uplink/downlink and whether position is shared.
    /// Cached so the toggles persist across runs — in particular position, which the device can't report back.</summary>
    public sealed class ChannelOptions
    {
        public bool Uplink { get; set; }
        public bool Downlink { get; set; }
        public bool Position { get; set; }
    }

    public sealed class Entry
    {
        public uint NodeNum { get; set; }
        public List<CachedChannel> Channels { get; set; } = new();
        public Dictionary<uint, ChannelOptions> ChannelOptions { get; set; } = new();   // channel index -> last-set options
        public Dictionary<uint, string> NodeNames { get; set; } = new();
        public Dictionary<uint, string> NodeShortNames { get; set; } = new();   // node num -> short name (e.g. "ABCD")
        public Dictionary<uint, bool> NodeFavorites { get; set; } = new();     // node num -> device "favorite" flag
        public Dictionary<uint, bool> NodeIgnored { get; set; } = new();       // node num -> device "ignored" flag
        public Dictionary<uint, uint> NodeHopsAway { get; set; } = new();      // node num -> hops away (mesh distance)
        public Dictionary<uint, long> NodeLastHeard { get; set; } = new();     // node num -> last-heard epoch seconds
        public Dictionary<uint, string> NodeRoles { get; set; } = new();     // node num -> device role (Client/Router/…)
        public Dictionary<uint, string> NodeHw { get; set; } = new();        // node num -> hardware model (Heltec V3/…)
        public Dictionary<uint, CachedPosition> Positions { get; set; } = new();   // node num -> last-known position
        public Dictionary<uint, string> ChannelKeys { get; set; } = new();   // legacy app-level AES keys (unused)

        // Per-node DM/Block flags (node num → prefs). Only nodes with a flag set are stored.
        public Dictionary<uint, NodePrefs> NodePrefs { get; set; } = new();

        // Per-node environment telemetry history (node num → readings, oldest first), persisted across runs.
        public Dictionary<uint, List<TelemetryReading>> Telemetry { get; set; } = new();

        // Per-channel chat history (channel index → latest messages, oldest first), persisted across runs.
        public Dictionary<uint, List<ChatMessage>> Chat { get; set; } = new();

        // Per-channel auto-delete age (channel index → hours to keep; absent/0 = keep forever).
        // Legacy: retention is now stored in ChannelRetentionMinutes; a value here is read as hours*60 unless the
        // minutes map overrides it (kept only so pre-existing hour settings survive an upgrade).
        public Dictionary<uint, int> ChannelRetentionHours { get; set; } = new();

        // Per-channel receiver auto-delete age in minutes (channel index → minutes; absent/0 = keep forever).
        // Supersedes ChannelRetentionHours for any channel present here.
        public Dictionary<uint, int> ChannelRetentionMinutes { get; set; } = new();

        // Per-channel sender self-destruct: when sending on this channel, stamp outgoing messages to auto-delete
        // after this many minutes on every receiver (and locally). Channel index → minutes; absent/0 = off.
        public Dictionary<uint, int> ChannelSendTtlMinutes { get; set; } = new();

        // Channel selections: which channel chess uses, which channels chat listens to, and the chat TX channel.
        public uint? ChessChannel { get; set; }
        public List<uint> ChatListen { get; set; } = new();
        public uint? ChatTxChannel { get; set; }

        // Channel used for position/telemetry/node-info requests and the manual node-info/position broadcasts.
        // null = automatic (requests follow the target node's channel; broadcasts use the primary channel 0).
        public uint? UtilityChannel { get; set; }

        // Channels where chat acks are turned ON (acks default off, so we store the exceptions).
        public List<uint> ChatAckOn { get; set; } = new();

        // Channels where the chat ack should also include the acker's received RSSI/SNR/hops (default off).
        public List<uint> ChatAckSignalOn { get; set; } = new();

        // Per-channel auto-ack keywords (channel index → lowercased trigger strings). A received message whose text
        // contains any of these triggers a CHATACK-with-RSSI regardless of the channel's ack setting.
        public Dictionary<uint, List<string>> AckTriggers { get; set; } = new();
    }

    /// <summary>The persisted channel selections for a device (null if none saved yet).</summary>
    public sealed record ChannelPrefs(uint? ChessChannel, List<uint> ChatListen, uint? ChatTxChannel);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChessOverMesh", "devices.json");

    // ---- Optional per-device AES encryption ----------------------------------------------------------------
    // The file is Dictionary<string, Stored>: each device is either plaintext (Data) or encrypted (Verifier +
    // Cipher, AES via AesText keyed on the device password). The password is NEVER persisted — it's held in
    // memory for the session once the user unlocks (or sets) it. A locked device (encrypted, no session
    // password) is omitted from Load() until unlocked; the connect flow prompts before any cache read.
    private sealed class Stored
    {
        public Entry? Data { get; set; }        // plaintext (no password)
        public string? Verifier { get; set; }   // AesText.Encrypt(VerifierToken, password) — checks the password
        public string? Cipher { get; set; }      // AesText.Encrypt(JSON(Entry), password) — the encrypted entry
    }

    private const string VerifierToken = "ChessOverMesh cache v1";
    private static readonly Dictionary<string, string> _passwords = new();   // host -> password (this session only)
    // Hosts we've legitimately handled this session (unlocked, or had their password set/removed). Persist only
    // writes plaintext over an on-disk encrypted blob for a host in this set — so a stray write for a locked
    // device can never clobber its encrypted cache.
    private static readonly HashSet<string> _unlocked = new();

    // Reads the raw on-disk map, migrating an old Dictionary<string,Entry> file (raw Entry per host) transparently.
    private static Dictionary<string, Stored> LoadRaw()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(FilePath));
            if (raw == null) return new();
            var result = new Dictionary<string, Stored>();
            foreach (var (host, el) in raw)
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                bool wrapper = el.TryGetProperty("Data", out _) || el.TryGetProperty("Cipher", out _) || el.TryGetProperty("Verifier", out _);
                result[host] = wrapper ? (el.Deserialize<Stored>() ?? new Stored())
                                       : new Stored { Data = el.Deserialize<Entry>() };   // legacy raw Entry
            }
            return result;
        }
        catch { return new(); }
    }

    private static void WriteRaw(Dictionary<string, Stored> raw)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>True when this device's cache is encrypted on disk.</summary>
    public static bool IsEncrypted(string host) =>
        LoadRaw().TryGetValue(host, out var s) && (s.Verifier != null || s.Cipher != null);

    /// <summary>True when this device's password is known for this session (or it isn't encrypted).</summary>
    public static bool IsUnlocked(string host) => _passwords.ContainsKey(host);

    /// <summary>Verifies a password against the device's stored verifier; stores it for the session on success.</summary>
    public static bool Unlock(string host, string password)
    {
        var raw = LoadRaw();
        if (!raw.TryGetValue(host, out var s) || s.Verifier == null) return true;   // not encrypted — nothing to unlock
        if (AesText.TryDecrypt(s.Verifier, password, out var token) && token == VerifierToken)
        {
            _passwords[host] = password;
            _unlocked.Add(host);
            return true;
        }
        return false;
    }

    /// <summary>Enables/changes (non-empty) or disables (empty) cache encryption for a device, re-persisting now.</summary>
    public static void SetPassword(string host, string password)
    {
        var all = Load();
        _unlocked.Add(host);   // authorised — Persist may rewrite this host (encrypted or plaintext)
        if (string.IsNullOrEmpty(password)) _passwords.Remove(host);
        else _passwords[host] = password;
        if (!all.ContainsKey(host)) all[host] = new Entry();   // ensure it's written in the chosen form
        Persist(all);
    }

    /// <summary>Wipes a device's cached data and resets it to unencrypted (the "delete cache / forgot password" reset).</summary>
    public static void ClearDevice(string host)
    {
        _passwords.Remove(host);
        _unlocked.Remove(host);
        var raw = LoadRaw();
        if (raw.Remove(host)) WriteRaw(raw);
    }

    /// <summary>Drops a device's session password + authorisation (call on user disconnect so reconnect re-prompts).</summary>
    public static void ForgetSession(string host)
    {
        _passwords.Remove(host);
        _unlocked.Remove(host);
    }

    private static Dictionary<string, Entry> Load()
    {
        var result = new Dictionary<string, Entry>();
        foreach (var (host, s) in LoadRaw())
        {
            if (s.Data != null) { result[host] = s.Data; continue; }          // plaintext
            if (s.Verifier == null && s.Cipher == null) continue;             // empty/unknown
            if (!_passwords.TryGetValue(host, out var pw)) continue;          // encrypted + locked → omit
            if (s.Cipher == null) { result[host] = new Entry(); continue; }   // encrypted, enabled, no data yet
            if (AesText.TryDecrypt(s.Cipher, pw, out var json))
                try { if (JsonSerializer.Deserialize<Entry>(json) is { } e) result[host] = e; } catch { }
        }
        return result;
    }

    // Writes `all` back, preserving locked hosts' encrypted blobs and encrypting hosts with a session password.
    private static void Persist(Dictionary<string, Entry> all)
    {
        try
        {
            var raw = LoadRaw();   // keep locked hosts' Stored blobs (they're absent from `all`)
            foreach (var (host, entry) in all)
            {
                if (_passwords.TryGetValue(host, out var pw) && !string.IsNullOrEmpty(pw))
                    raw[host] = new Stored
                    {
                        Verifier = AesText.Encrypt(VerifierToken, pw),
                        Cipher = AesText.Encrypt(JsonSerializer.Serialize(entry), pw),
                    };
                else if (raw.TryGetValue(host, out var ex) && (ex.Verifier != null || ex.Cipher != null) && !_unlocked.Contains(host))
                    continue;   // encrypted on disk and not authorised this session — never clobber it with plaintext
                else
                    raw[host] = new Stored { Data = entry };
            }
            WriteRaw(raw);
        }
        catch { /* best effort */ }
    }

    public static Entry? Get(string host)
    {
        var entry = Load().GetValueOrDefault(host);
        return entry is { Channels.Count: > 0 } ? entry : null;
    }

    public static void Save(string host, IEnumerable<MeshChannel> channels, uint nodeNum,
                            IReadOnlyDictionary<uint, string>? nodeNames = null,
                            IReadOnlyDictionary<uint, string>? nodeRoles = null,
                            IReadOnlyDictionary<uint, string>? nodeHw = null,
                            IReadOnlyDictionary<uint, string>? nodeShortNames = null,
                            IReadOnlyDictionary<uint, bool>? nodeFavorites = null,
                            IReadOnlyDictionary<uint, bool>? nodeIgnored = null,
                            IReadOnlyDictionary<uint, uint>? nodeHopsAway = null,
                            IReadOnlyDictionary<uint, long>? nodeLastHeard = null)
    {
        try
        {
            var all = Load();
            var existing = all.GetValueOrDefault(host);
            all[host] = new Entry
            {
                NodeNum = nodeNum,
                Channels = channels.Select(c => new CachedChannel { Index = c.Index, Name = c.Name, Role = c.Role,
                    Uplink = c.UplinkEnabled, Downlink = c.DownlinkEnabled, PositionPrecision = c.PositionPrecision,
                    HasKey = c.HasKey }).ToList(),
                // Keep previously cached names/roles unless a fresh set is supplied.
                NodeNames = nodeNames != null
                    ? new Dictionary<uint, string>(nodeNames)
                    : existing?.NodeNames ?? new Dictionary<uint, string>(),
                NodeShortNames = nodeShortNames != null
                    ? new Dictionary<uint, string>(nodeShortNames)
                    : existing?.NodeShortNames ?? new Dictionary<uint, string>(),
                NodeFavorites = nodeFavorites != null
                    ? new Dictionary<uint, bool>(nodeFavorites)
                    : existing?.NodeFavorites ?? new Dictionary<uint, bool>(),
                NodeIgnored = nodeIgnored != null
                    ? new Dictionary<uint, bool>(nodeIgnored)
                    : existing?.NodeIgnored ?? new Dictionary<uint, bool>(),
                NodeHopsAway = nodeHopsAway != null
                    ? new Dictionary<uint, uint>(nodeHopsAway)
                    : existing?.NodeHopsAway ?? new Dictionary<uint, uint>(),
                NodeLastHeard = nodeLastHeard != null
                    ? new Dictionary<uint, long>(nodeLastHeard)
                    : existing?.NodeLastHeard ?? new Dictionary<uint, long>(),
                NodeRoles = nodeRoles != null
                    ? new Dictionary<uint, string>(nodeRoles)
                    : existing?.NodeRoles ?? new Dictionary<uint, string>(),
                NodeHw = nodeHw != null
                    ? new Dictionary<uint, string>(nodeHw)
                    : existing?.NodeHw ?? new Dictionary<uint, string>(),
                Positions = existing?.Positions ?? new Dictionary<uint, CachedPosition>(),   // preserve node positions
                ChannelOptions = existing?.ChannelOptions ?? new Dictionary<uint, ChannelOptions>(),   // preserve channel options
                ChannelKeys = existing?.ChannelKeys ?? new Dictionary<uint, string>(),   // preserve per-channel keys
                NodePrefs = existing?.NodePrefs ?? new Dictionary<uint, NodePrefs>(),     // preserve DM/Block flags
                Telemetry = existing?.Telemetry ?? new Dictionary<uint, List<TelemetryReading>>(),   // preserve telemetry history
                Chat = existing?.Chat ?? new Dictionary<uint, List<ChatMessage>>(),                  // preserve chat history
                ChannelRetentionHours = existing?.ChannelRetentionHours ?? new Dictionary<uint, int>(),   // preserve retention (legacy)
                ChannelRetentionMinutes = existing?.ChannelRetentionMinutes ?? new Dictionary<uint, int>(),   // preserve retention
                ChannelSendTtlMinutes = existing?.ChannelSendTtlMinutes ?? new Dictionary<uint, int>(),  // preserve send TTLs
                // Preserve channel selections across a channel-list save.
                ChessChannel = existing?.ChessChannel,
                ChatListen = existing?.ChatListen ?? new List<uint>(),
                ChatTxChannel = existing?.ChatTxChannel,
                UtilityChannel = existing?.UtilityChannel,
                ChatAckOn = existing?.ChatAckOn ?? new List<uint>(),
                ChatAckSignalOn = existing?.ChatAckSignalOn ?? new List<uint>(),
                AckTriggers = existing?.AckTriggers ?? new Dictionary<uint, List<string>>(),
            };
            Persist(all);
        }
        catch { /* caching is best-effort */ }
    }

    // PSK value is never cached, so Psk is "" and the channel reports HasKey only (the UI shows "🔒 set — click
    // Fetch" for keyed channels and reads the real key from the device on demand).
    public static IReadOnlyList<MeshChannel> ToMeshChannels(Entry e) =>
        e.Channels.Select(c => new MeshChannel(c.Index, c.Name, c.Role, c.HasKey, "", c.Uplink, c.Downlink, c.PositionPrecision)).ToList();

    /// <summary>The saved channel selections for a device, or null if none stored yet.</summary>
    public static ChannelPrefs? GetChannelPrefs(string host)
    {
        var entry = Load().GetValueOrDefault(host);
        if (entry == null) return null;
        return new ChannelPrefs(entry.ChessChannel, new List<uint>(entry.ChatListen), entry.ChatTxChannel);
    }

    /// <summary>Persists which channel chess uses, which channels chat listens to, and the chat TX channel.</summary>
    public static void SaveChannelPrefs(string host, uint chessChannel, IEnumerable<uint> chatListen, uint chatTxChannel)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            entry.ChessChannel = chessChannel;
            entry.ChatListen = chatListen.Distinct().OrderBy(i => i).ToList();
            entry.ChatTxChannel = chatTxChannel;
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>The channel used for position/telemetry/node-info requests and the manual node-info/position
    /// broadcasts for a device, or null for automatic (per-node channel for requests, primary for broadcasts).</summary>
    public static uint? GetUtilityChannel(string host) => Load().GetValueOrDefault(host)?.UtilityChannel;

    /// <summary>Sets the utility channel for a device (null = automatic).</summary>
    public static void SetUtilityChannel(string host, uint? channel)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            entry.UtilityChannel = channel;
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>The saved per-node DM/Block flags for a device (node num → prefs); empty if none stored.</summary>
    public static IReadOnlyDictionary<uint, NodePrefs> GetNodePrefs(string host) =>
        Load().GetValueOrDefault(host)?.NodePrefs ?? new Dictionary<uint, NodePrefs>();

    /// <summary>Sets a node's DM/Block flags. Block wins (a blocked node is stored with Dm=false). A node
    /// with neither flag set is removed from the store.</summary>
    public static void SetNodePref(string host, uint nodeNum, bool dm, bool block)
    {
        try
        {
            if (block) dm = false;   // block wins over DM
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            if (!dm && !block) entry.NodePrefs.Remove(nodeNum);
            else entry.NodePrefs[nodeNum] = new NodePrefs { Dm = dm, Block = block };
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>Forgets a single node in the disk cache (name/role/hardware/position/prefs/telemetry), so a node
    /// removed from the device isn't re-seeded from cache on the next connect.</summary>
    public static void RemoveNode(string host, uint nodeNum)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host);
            if (entry == null) return;
            entry.NodeNames.Remove(nodeNum);
            entry.NodeShortNames.Remove(nodeNum);
            entry.NodeFavorites.Remove(nodeNum);
            entry.NodeIgnored.Remove(nodeNum);
            entry.NodeHopsAway.Remove(nodeNum);
            entry.NodeLastHeard.Remove(nodeNum);
            entry.NodeRoles.Remove(nodeNum);
            entry.NodeHw.Remove(nodeNum);
            entry.Positions.Remove(nodeNum);
            entry.NodePrefs.Remove(nodeNum);
            entry.Telemetry.Remove(nodeNum);
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>The persisted per-channel options for a device (channel index → options), empty if none.</summary>
    public static IReadOnlyDictionary<uint, ChannelOptions> GetChannelOptions(string host) =>
        Load().GetValueOrDefault(host)?.ChannelOptions ?? new Dictionary<uint, ChannelOptions>();

    /// <summary>Stores the options last set for one channel.</summary>
    public static void SaveChannelOption(string host, uint index, ChannelOptions options)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            entry.ChannelOptions[index] = options;
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>The persisted node positions for a device (node num → position), empty if none.</summary>
    public static IReadOnlyDictionary<uint, CachedPosition> GetPositions(string host) =>
        Load().GetValueOrDefault(host)?.Positions ?? new Dictionary<uint, CachedPosition>();

    /// <summary>Replaces the stored node positions for a device.</summary>
    public static void SaveNodePositions(string host, IReadOnlyDictionary<uint, (double Lat, double Lon, long LastHeard, long PosTime)> positions)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            entry.Positions = positions.ToDictionary(kv => kv.Key, kv => new CachedPosition { Lat = kv.Value.Lat, Lon = kv.Value.Lon, LastHeard = kv.Value.LastHeard, PosTime = kv.Value.PosTime });
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>The persisted environment telemetry history for a device (node num → readings), empty if none.</summary>
    public static IReadOnlyDictionary<uint, List<TelemetryReading>> GetTelemetry(string host) =>
        Load().GetValueOrDefault(host)?.Telemetry ?? new Dictionary<uint, List<TelemetryReading>>();

    /// <summary>Replaces the stored telemetry history for one node (empty list removes it).</summary>
    public static void SaveTelemetry(string host, uint nodeNum, IEnumerable<TelemetryReading> readings)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            var list = readings.ToList();
            if (list.Count == 0) entry.Telemetry.Remove(nodeNum);
            else entry.Telemetry[nodeNum] = list;
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>Deletes the stored telemetry history for one node.</summary>
    public static void ClearTelemetry(string host, uint nodeNum) => SaveTelemetry(host, nodeNum, Array.Empty<TelemetryReading>());

    /// <summary>The latest chat messages cached per channel for a device (channel index → messages), empty if none.</summary>
    public const int MaxChatPerChannel = 100;   // default; the live cap comes from AppSettings.ChatMessageLimit
    public static IReadOnlyDictionary<uint, List<ChatMessage>> GetChat(string host) =>
        Load().GetValueOrDefault(host)?.Chat ?? new Dictionary<uint, List<ChatMessage>>();

    /// <summary>Appends one chat message to a channel's history, keeping only the latest
    /// <see cref="AppSettings.ChatMessageLimit"/> per channel.</summary>
    public static void AppendChat(string host, uint channel, ChatMessage message)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            if (!entry.Chat.TryGetValue(channel, out var list)) entry.Chat[channel] = list = new List<ChatMessage>();
            list.Add(message);
            int cap = AppSettings.ChatMessageLimit;
            if (cap > 0 && list.Count > cap) list.RemoveRange(0, list.Count - cap);
            // Age cap for this channel (per-device), if set.
            int retMins = EffectiveRetentionMinutes(entry, channel);
            if (retMins > 0)
            {
                var cutoff = DateTime.Now.AddMinutes(-retMins);
                list.RemoveAll(m => m.Time != default && m.Time < cutoff);
            }
            // Sender self-destruct: drop any message whose expiry has already passed.
            var expiryNow = DateTime.Now;
            list.RemoveAll(m => m.ExpiresAt != default && m.ExpiresAt <= expiryNow);
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>Removes a single cached chat message from a channel. Matches by <paramref name="id"/> when the
    /// stored message has one; otherwise falls back to matching the exact Text+Detail (for legacy entries cached
    /// before ids existed). Removes only the first match.</summary>
    public static void RemoveChat(string host, uint channel, string? id, string text, string detail)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host);
            if (entry == null || !entry.Chat.TryGetValue(channel, out var list)) return;
            int idx = !string.IsNullOrEmpty(id)
                ? list.FindIndex(m => m.Id == id)
                : list.FindIndex(m => string.IsNullOrEmpty(m.Id) && m.Text == text && m.Detail == detail);
            if (idx < 0) return;
            list.RemoveAt(idx);
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>Deletes the cached chat history for one channel.</summary>
    public static void ClearChat(string host, uint channel)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host);
            if (entry == null || !entry.Chat.Remove(channel)) return;
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>A channel's effective receiver auto-delete age in minutes: the minutes setting if present, else the
    /// legacy hours setting (×60), else 0 (keep forever).</summary>
    private static int EffectiveRetentionMinutes(Entry e, uint channel)
    {
        if (e.ChannelRetentionMinutes.TryGetValue(channel, out var mins) && mins > 0) return mins;
        if (e.ChannelRetentionHours.TryGetValue(channel, out var hrs) && hrs > 0) return hrs * 60;
        return 0;
    }

    /// <summary>Per-channel receiver auto-delete age for a device (channel index → minutes; empty if none set).
    /// Merges the minutes setting with any legacy hours setting (minutes wins).</summary>
    public static IReadOnlyDictionary<uint, int> GetChannelRetention(string host)
    {
        var entry = Load().GetValueOrDefault(host);
        var result = new Dictionary<uint, int>();
        if (entry == null) return result;
        foreach (var ch in entry.ChannelRetentionHours.Keys.Concat(entry.ChannelRetentionMinutes.Keys).Distinct())
        {
            int mins = EffectiveRetentionMinutes(entry, ch);
            if (mins > 0) result[ch] = mins;
        }
        return result;
    }

    /// <summary>Sets a channel's receiver auto-delete age (minutes; 0/negative removes it = keep forever). Also
    /// clears any legacy hours entry for the channel so the minutes value is authoritative.</summary>
    public static void SetChannelRetention(string host, uint channel, int minutes)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            entry.ChannelRetentionHours.Remove(channel);   // superseded by the minutes value
            if (minutes > 0) entry.ChannelRetentionMinutes[channel] = minutes;
            else entry.ChannelRetentionMinutes.Remove(channel);
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>Deletes cached chat messages older than each channel's configured retention (auto-delete).</summary>
    public static void PruneChatByRetention(string host)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host);
            if (entry == null) return;
            var channels = entry.ChannelRetentionHours.Keys.Concat(entry.ChannelRetentionMinutes.Keys).Distinct().ToList();
            if (channels.Count == 0) return;
            bool changed = false;
            foreach (var channel in channels)
            {
                int mins = EffectiveRetentionMinutes(entry, channel);
                if (mins <= 0 || !entry.Chat.TryGetValue(channel, out var list)) continue;
                var cutoff = DateTime.Now.AddMinutes(-mins);
                int removed = list.RemoveAll(m => m.Time != default && m.Time < cutoff);
                if (removed > 0) changed = true;
                if (list.Count == 0) { entry.Chat.Remove(channel); changed = true; }
            }
            if (!changed) return;
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>Deletes cached chat messages whose sender-set self-destruct time has passed (any channel).</summary>
    public static void PruneChatByExpiry(string host)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host);
            if (entry == null || entry.Chat.Count == 0) return;
            var now = DateTime.Now;
            bool changed = false;
            foreach (var channel in entry.Chat.Keys.ToList())
            {
                var list = entry.Chat[channel];
                int removed = list.RemoveAll(m => m.ExpiresAt != default && m.ExpiresAt <= now);
                if (removed > 0) changed = true;
                if (list.Count == 0) { entry.Chat.Remove(channel); changed = true; }
            }
            if (!changed) return;
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>Per-channel sender self-destruct minutes for a device (channel index → minutes; empty if none set).</summary>
    public static IReadOnlyDictionary<uint, int> GetChannelSendTtl(string host) =>
        Load().GetValueOrDefault(host)?.ChannelSendTtlMinutes ?? new Dictionary<uint, int>();

    /// <summary>Sets a channel's sender self-destruct minutes (outgoing messages auto-delete after this on every
    /// receiver and locally). 0/negative removes it (= don't stamp a lifetime).</summary>
    public static void SetChannelSendTtl(string host, uint channel, int minutes)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            if (minutes > 0) entry.ChannelSendTtlMinutes[channel] = minutes;
            else entry.ChannelSendTtlMinutes.Remove(channel);
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>Deletes the cached chat history for every device (leaves channel/node/telemetry data intact).</summary>
    public static void ClearAllChat()
    {
        try
        {
            var all = Load();
            bool changed = false;
            foreach (var entry in all.Values)
                if (entry.Chat.Count > 0) { entry.Chat.Clear(); changed = true; }
            if (!changed) return;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>All cached app-level AES keys for a device (channel index → decrypted key).</summary>
    public static IReadOnlyDictionary<uint, string> GetChannelKeys(string host)
    {
        var result = new Dictionary<uint, string>();
        var entry = Load().GetValueOrDefault(host);
        if (entry != null)
            foreach (var kv in entry.ChannelKeys)
            {
                var key = SecretProtector.Unprotect(kv.Value);
                if (key.Length > 0) result[kv.Key] = key;
            }
        return result;
    }

    /// <summary>Channels (for a device) where chat acks are turned on. Acks default off.</summary>
    public static IReadOnlyCollection<uint> GetChatAckOn(string host) =>
        Load().GetValueOrDefault(host)?.ChatAckOn ?? new List<uint>();

    /// <summary>Sets whether chat acks are sent for messages received on a device + channel (default off).</summary>
    public static void SetChatAck(string host, uint channelIndex, bool enabled)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            if (enabled) { if (!entry.ChatAckOn.Contains(channelIndex)) entry.ChatAckOn.Add(channelIndex); }
            else entry.ChatAckOn.Remove(channelIndex);
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>True when chat acks are enabled for a device + channel (default off).</summary>
    public static bool IsChatAckEnabled(string host, uint channelIndex) =>
        Load().GetValueOrDefault(host)?.ChatAckOn.Contains(channelIndex) ?? false;

    /// <summary>Channels (for a device) where the chat ack should also report the acker's RSSI/SNR/hops.</summary>
    public static IReadOnlyCollection<uint> GetAckSignalOn(string host) =>
        Load().GetValueOrDefault(host)?.ChatAckSignalOn ?? new List<uint>();

    /// <summary>Sets whether the chat ack for a device + channel includes the acker's signal (default off).</summary>
    public static void SetAckSignal(string host, uint channelIndex, bool enabled)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            if (enabled) { if (!entry.ChatAckSignalOn.Contains(channelIndex)) entry.ChatAckSignalOn.Add(channelIndex); }
            else entry.ChatAckSignalOn.Remove(channelIndex);
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>True when the chat ack for a device + channel includes the acker's signal (default off).</summary>
    public static bool IsAckSignalEnabled(string host, uint channelIndex) =>
        Load().GetValueOrDefault(host)?.ChatAckSignalOn.Contains(channelIndex) ?? false;

    /// <summary>The per-channel auto-ack keyword lists for a device (channel index → lowercased triggers).</summary>
    public static IReadOnlyDictionary<uint, List<string>> GetAckTriggers(string host) =>
        Load().GetValueOrDefault(host)?.AckTriggers ?? new Dictionary<uint, List<string>>();

    /// <summary>Sets the auto-ack keywords for a device + channel (stored lowercased; empty list removes the entry).</summary>
    public static void SetAckTriggers(string host, uint channelIndex, IEnumerable<string> triggers)
    {
        try
        {
            var list = triggers.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            if (list.Count == 0) entry.AckTriggers.Remove(channelIndex);
            else entry.AckTriggers[channelIndex] = list;
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>The cached AES key for a given device + channel, or "" if none. (DPAPI-decrypted.)</summary>
    public static string GetChannelKey(string host, uint channelIndex)
    {
        var entry = Load().GetValueOrDefault(host);
        var stored = entry?.ChannelKeys.GetValueOrDefault(channelIndex);
        return stored == null ? string.Empty : SecretProtector.Unprotect(stored);
    }

    /// <summary>Stores (or clears, if empty) the AES key for a device + channel, protected with DPAPI.</summary>
    public static void SetChannelKey(string host, uint channelIndex, string key)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            if (string.IsNullOrEmpty(key))
                entry.ChannelKeys.Remove(channelIndex);
            else
                entry.ChannelKeys[channelIndex] = SecretProtector.Protect(key);
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }
}
