using System.Text.Json;
using ChessOverMesh.Mesh;

namespace ChessOverMesh.Maui;

/// <summary>
/// Remembers each device's channel list and node number in app-private JSON, keyed by host, so a
/// reconnect can skip the want-config dump (and its long "sync") and reception is live immediately.
/// Ported from the desktop app; identical format apart from the storage location.
/// </summary>
internal static class DeviceCache
{
    public sealed class CachedChannel
    {
        public uint Index { get; set; }
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
    }

    /// <summary>Per-node chat preferences: whether to surface DMs from/to this node (and list it as a TX
    /// target), and whether to ignore incoming DMs from it. Block wins over Dm. A node with neither flag isn't
    /// stored. Mirrors the desktop app.</summary>
    public sealed class NodePrefs
    {
        public bool Dm { get; set; }
        public bool Block { get; set; }
    }

    /// <summary>One cached chat line: the rendered message text, its dim metadata line, and the local time it was
    /// shown (for ordering when reloading several channels together). Mirrors the desktop app.</summary>
    public sealed class ChatMessage
    {
        public string Text { get; set; } = "";
        public string Detail { get; set; } = "";
        public DateTime Time { get; set; }
        public string? Id { get; set; }   // stable id so a single message can be removed from the cache
        public uint RxTime { get; set; }  // the message's device rx_time (epoch s, 0 if unknown), for proxy catch-up
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

    /// <summary>One node's last-known location (degrees) with the local last-heard / position timestamps (epoch s,
    /// 0 if unknown). Lets the map and "open in Google Maps" work across reconnects. Mirrors the desktop app.</summary>
    public sealed class CachedPosition
    {
        public double Lat { get; set; }
        public double Lon { get; set; }
        public long LastHeard { get; set; }
        public long PosTime { get; set; }
    }

    public sealed class Entry
    {
        public uint NodeNum { get; set; }
        public List<CachedChannel> Channels { get; set; } = new();
        public Dictionary<uint, string> NodeNames { get; set; } = new();
        public Dictionary<uint, string> NodeRoles { get; set; } = new();   // node num -> device role
        public Dictionary<uint, string> NodeHw { get; set; } = new();      // node num -> hardware model
        public Dictionary<uint, string> ChannelKeys { get; set; } = new();
        public Dictionary<uint, NodePrefs> NodePrefs { get; set; } = new();   // node num -> DM/Block flags

        // Per-channel chat history (channel index → latest messages, oldest first) and per-node telemetry history.
        public Dictionary<uint, List<ChatMessage>> Chat { get; set; } = new();
        public Dictionary<uint, List<TelemetryReading>> Telemetry { get; set; } = new();
        public Dictionary<uint, CachedPosition> Positions { get; set; } = new();   // node num -> last-known position

        public uint? ChessChannel { get; set; }
        public List<uint> ChatListen { get; set; } = new();
        public uint? ChatTxChannel { get; set; }
        public List<uint> ChatAckOn { get; set; } = new();
        public List<uint> ChatAckSignalOn { get; set; } = new();   // channels whose ack also reports RSSI/SNR/hops
        // Channel for position/telemetry/node-info requests + manual node-info/position broadcasts (null = auto).
        public uint? UtilityChannel { get; set; }
        // Per-channel auto-ack keywords (channel index → lowercased triggers): a received message whose text
        // contains any of these gets a CHATACK-with-RSSI regardless of the channel's ack setting.
        public Dictionary<uint, List<string>> AckTriggers { get; set; } = new();
    }

    public sealed record ChannelPrefs(uint? ChessChannel, List<uint> ChatListen, uint? ChatTxChannel);

    private static readonly string FilePath =
        Path.Combine(FileSystem.AppDataDirectory, "devices.json");

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath))
                       ?? new Dictionary<string, Entry>();
        }
        catch { /* ignore a corrupt/unreadable cache */ }
        return new Dictionary<string, Entry>();
    }

    private static void Persist(Dictionary<string, Entry> all) =>
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));

    public static Entry? Get(string host)
    {
        var entry = Load().GetValueOrDefault(host);
        return entry is { Channels.Count: > 0 } ? entry : null;
    }

    public static void Save(string host, IEnumerable<MeshChannel> channels, uint nodeNum,
                            IReadOnlyDictionary<uint, string>? nodeNames = null,
                            IReadOnlyDictionary<uint, string>? nodeRoles = null,
                            IReadOnlyDictionary<uint, string>? nodeHw = null)
    {
        try
        {
            var all = Load();
            var existing = all.GetValueOrDefault(host);
            all[host] = new Entry
            {
                NodeNum = nodeNum,
                Channels = channels.Select(c => new CachedChannel { Index = c.Index, Name = c.Name, Role = c.Role }).ToList(),
                NodeNames = nodeNames != null
                    ? new Dictionary<uint, string>(nodeNames)
                    : existing?.NodeNames ?? new Dictionary<uint, string>(),
                NodeRoles = nodeRoles != null
                    ? new Dictionary<uint, string>(nodeRoles)
                    : existing?.NodeRoles ?? new Dictionary<uint, string>(),
                NodeHw = nodeHw != null
                    ? new Dictionary<uint, string>(nodeHw)
                    : existing?.NodeHw ?? new Dictionary<uint, string>(),
                ChannelKeys = existing?.ChannelKeys ?? new Dictionary<uint, string>(),
                NodePrefs = existing?.NodePrefs ?? new Dictionary<uint, NodePrefs>(),   // preserve DM/Block flags
                Chat = existing?.Chat ?? new Dictionary<uint, List<ChatMessage>>(),            // preserve chat history
                Telemetry = existing?.Telemetry ?? new Dictionary<uint, List<TelemetryReading>>(),   // preserve telemetry
                Positions = existing?.Positions ?? new Dictionary<uint, CachedPosition>(),     // preserve node positions
                ChessChannel = existing?.ChessChannel,
                ChatListen = existing?.ChatListen ?? new List<uint>(),
                ChatTxChannel = existing?.ChatTxChannel,
                ChatAckOn = existing?.ChatAckOn ?? new List<uint>(),
                ChatAckSignalOn = existing?.ChatAckSignalOn ?? new List<uint>(),
                UtilityChannel = existing?.UtilityChannel,
                AckTriggers = existing?.AckTriggers ?? new Dictionary<uint, List<string>>(),
            };
            Persist(all);
        }
        catch { /* caching is best-effort */ }
    }

    public static IReadOnlyList<MeshChannel> ToMeshChannels(Entry e) =>
        e.Channels.Select(c => new MeshChannel(c.Index, c.Name, c.Role)).ToList();

    // ---- Chat history (per channel) ----
    public const int MaxChatPerChannel = 100;

    /// <summary>The latest chat messages cached per channel for a device (channel index → messages), empty if none.</summary>
    public static IReadOnlyDictionary<uint, List<ChatMessage>> GetChat(string host) =>
        Load().GetValueOrDefault(host)?.Chat ?? new Dictionary<uint, List<ChatMessage>>();

    /// <summary>Appends one chat message to a channel's history, keeping only the latest <see cref="MaxChatPerChannel"/>.</summary>
    public static void AppendChat(string host, uint channel, ChatMessage message)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            if (!entry.Chat.TryGetValue(channel, out var list)) entry.Chat[channel] = list = new List<ChatMessage>();
            list.Add(message);
            if (list.Count > MaxChatPerChannel) list.RemoveRange(0, list.Count - MaxChatPerChannel);
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>Removes a single cached chat message from a channel (by id, else by exact Text+Detail). First match only.</summary>
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

    /// <summary>Deletes the cached chat history for every device (leaves channel/node/telemetry data intact).</summary>
    public static void ClearAllChat()
    {
        try
        {
            var all = Load();
            bool changed = false;
            foreach (var entry in all.Values)
                if (entry.Chat.Count > 0) { entry.Chat.Clear(); changed = true; }
            if (changed) Persist(all);
        }
        catch { /* best effort */ }
    }

    // ---- Telemetry history (per node) ----

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

    // ---- Node positions (per node) ----

    /// <summary>The persisted last-known positions for a device (node num → position), empty if none.</summary>
    public static IReadOnlyDictionary<uint, CachedPosition> GetPositions(string host) =>
        Load().GetValueOrDefault(host)?.Positions ?? new Dictionary<uint, CachedPosition>();

    /// <summary>Replaces the stored node positions for a device from the live position map.</summary>
    public static void SaveNodePositions(string host, IReadOnlyDictionary<uint, (double Lat, double Lon, long LastHeard, long PosTime)> positions)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            entry.Positions = positions.ToDictionary(kv => kv.Key,
                kv => new CachedPosition { Lat = kv.Value.Lat, Lon = kv.Value.Lon, LastHeard = kv.Value.LastHeard, PosTime = kv.Value.PosTime });
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    public static ChannelPrefs? GetChannelPrefs(string host)
    {
        var entry = Load().GetValueOrDefault(host);
        if (entry == null) return null;
        return new ChannelPrefs(entry.ChessChannel, new List<uint>(entry.ChatListen), entry.ChatTxChannel);
    }

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

    /// <summary>The utility/info channel for a device (position/telemetry/node-info requests + manual broadcasts),
    /// or null for automatic (per-node channel for requests, primary for broadcasts).</summary>
    public static uint? GetUtilityChannel(string host) => Load().GetValueOrDefault(host)?.UtilityChannel;

    /// <summary>Sets the utility/info channel for a device (null = automatic).</summary>
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

    /// <summary>Forgets a single node in the disk cache (name/role/hardware/prefs/telemetry), so a node removed
    /// from the device isn't re-seeded from cache on the next connect.</summary>
    public static void RemoveNode(string host, uint nodeNum)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host);
            if (entry == null) return;
            entry.NodeNames.Remove(nodeNum);
            entry.NodeRoles.Remove(nodeNum);
            entry.NodeHw.Remove(nodeNum);
            entry.NodePrefs.Remove(nodeNum);
            entry.Telemetry.Remove(nodeNum);
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }

    /// <summary>The saved per-node DM/Block flags for a device (node num → prefs); empty if none stored.</summary>
    public static IReadOnlyDictionary<uint, NodePrefs> GetNodePrefs(string host) =>
        Load().GetValueOrDefault(host)?.NodePrefs ?? new Dictionary<uint, NodePrefs>();

    /// <summary>Sets a node's DM/Block flags. Block wins (a blocked node is stored with Dm=false). A node with
    /// neither flag is removed from the store.</summary>
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

    public static IReadOnlyCollection<uint> GetChatAckOn(string host) =>
        Load().GetValueOrDefault(host)?.ChatAckOn ?? new List<uint>();

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

    public static bool IsChatAckEnabled(string host, uint channelIndex) =>
        Load().GetValueOrDefault(host)?.ChatAckOn.Contains(channelIndex) ?? false;

    public static IReadOnlyCollection<uint> GetAckSignalOn(string host) =>
        Load().GetValueOrDefault(host)?.ChatAckSignalOn ?? new List<uint>();

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

    public static string GetChannelKey(string host, uint channelIndex)
    {
        var entry = Load().GetValueOrDefault(host);
        var stored = entry?.ChannelKeys.GetValueOrDefault(channelIndex);
        return stored == null ? string.Empty : SecretProtector.Unprotect(stored);
    }

    public static void SetChannelKey(string host, uint channelIndex, string key)
    {
        try
        {
            var all = Load();
            var entry = all.GetValueOrDefault(host) ?? new Entry();
            if (string.IsNullOrEmpty(key)) entry.ChannelKeys.Remove(channelIndex);
            else entry.ChannelKeys[channelIndex] = SecretProtector.Protect(key);
            all[host] = entry;
            Persist(all);
        }
        catch { /* best effort */ }
    }
}
