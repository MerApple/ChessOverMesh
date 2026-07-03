using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshtastic.Proxy;

/// <summary>One configured proxy account: a username, its PBKDF2 password hash, and whether it may use the radio.</summary>
internal sealed record ProxyUser(string Username, string PasswordHash, bool CanUseDevice);

/// <summary>
/// The proxy's multi-user credential store, loaded from a small JSON file:
/// <code>
/// { "users": [
///     { "username": "alice", "passwordHash": "pbkdf2_sha256$210000$&lt;salt&gt;$&lt;hash&gt;", "canUseDevice": true  },
///     { "username": "bob",   "passwordHash": "pbkdf2_sha256$210000$&lt;salt&gt;$&lt;hash&gt;", "canUseDevice": false }
/// ] }
/// </code>
/// Passwords are stored only as salted PBKDF2-SHA256 hashes. A user with <c>canUseDevice: false</c> may connect,
/// receive the full config/mesh sync, and chat with the other connected clients, but nothing they send is written
/// to the radio (enforced in <see cref="ProxyHub"/>).
/// </summary>
internal sealed class UserStore
{
    // PBKDF2-SHA256 parameters. Verification reads the iteration count out of each stored hash, so these only govern
    // newly-created hashes and can be raised later without invalidating existing entries.
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Scheme = "pbkdf2_sha256";

    private readonly Dictionary<string, ProxyUser> _users;   // username (ordinal) -> user

    private UserStore(Dictionary<string, ProxyUser> users) => _users = users;

    public int Count => _users.Count;

    /// <summary>Builds a store from users already in memory (used for the legacy single <c>--user</c>/<c>--pass</c>).</summary>
    public static UserStore FromUsers(IEnumerable<ProxyUser> users)
    {
        var map = new Dictionary<string, ProxyUser>(StringComparer.Ordinal);
        foreach (var u in users) map[u.Username] = u;
        return new UserStore(map);
    }

    /// <summary>Loads and parses the JSON user file. Returns null if the file is missing or has no usable users;
    /// malformed rows are logged and skipped rather than aborting the whole file.</summary>
    public static UserStore? Load(string path, Action<string> log)
    {
        if (!File.Exists(path)) { log($"User file '{path}' not found — starting with no users (open proxy)."); return null; }

        UserFile? file;
        try { file = JsonSerializer.Deserialize<UserFile>(File.ReadAllText(path), JsonOpts); }
        catch (Exception ex) { log($"Failed to parse user file '{path}': {ex.Message}"); return null; }

        var map = new Dictionary<string, ProxyUser>(StringComparer.Ordinal);
        foreach (var row in file?.Users ?? new())
        {
            if (string.IsNullOrEmpty(row.Username) || string.IsNullOrEmpty(row.PasswordHash))
            { log($"Skipping a user row in '{path}': missing username or passwordHash."); continue; }
            map[row.Username] = new ProxyUser(row.Username, row.PasswordHash, row.CanUseDevice);
        }
        if (map.Count == 0) { log($"User file '{path}' contained no usable users."); return null; }
        return new UserStore(map);
    }

    /// <summary>Verifies credentials and returns the matched user, or null. Always runs a PBKDF2 verify (against a dummy
    /// hash when the username is unknown) so a missing user isn't distinguishable by timing.</summary>
    public ProxyUser? Authenticate(string user, string pass)
    {
        if (_users.TryGetValue(user, out var found))
            return VerifyPassword(pass, found.PasswordHash) ? found : null;
        VerifyPassword(pass, DummyHash);   // constant-work path for unknown users
        return null;
    }

    // ---- Password hashing (shared with the --add-user / --hash-pass CLI helpers) ----

    /// <summary>Hashes a password as <c>pbkdf2_sha256$&lt;iterations&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>.</summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Constant-time verify of a password against a stored <c>pbkdf2_sha256$…</c> string.</summary>
    public static bool VerifyPassword(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iter)) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[2]); expected = Convert.FromBase64String(parts[3]); }
        catch { return false; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Adds or replaces a user in the JSON file (creating it if absent) and writes it back, pretty-printed.</summary>
    public static void UpsertUser(string path, string user, string password, bool canUseDevice, Action<string> log)
    {
        UserFile file = new();
        if (File.Exists(path))
            try { file = JsonSerializer.Deserialize<UserFile>(File.ReadAllText(path), JsonOpts) ?? new(); }
            catch (Exception ex) { log($"Existing user file '{path}' is unreadable ({ex.Message}); refusing to overwrite."); return; }

        var row = file.Users.FirstOrDefault(u => u.Username == user);
        bool existed = row != null;
        if (row == null) { row = new UserRow { Username = user }; file.Users.Add(row); }
        row.PasswordHash = HashPassword(password);
        row.CanUseDevice = canUseDevice;

        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonWriteOpts));
        log($"{(existed ? "Updated" : "Added")} user '{user}' ({(canUseDevice ? "device-enabled" : "restricted")}) in '{path}'.");
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    // Relaxed encoder so the Base64 '+' / '/' in a hash stay literal (readable when hand-editing) instead of \uXXXX.
    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // A precomputed hash of a fixed string, used only to spend equal work when authenticating an unknown username.
    private static readonly string DummyHash = HashPassword("\0proxy-dummy\0");

    private sealed class UserFile
    {
        [JsonPropertyName("users")] public List<UserRow> Users { get; set; } = new();
    }

    private sealed class UserRow
    {
        [JsonPropertyName("username")] public string Username { get; set; } = "";
        [JsonPropertyName("passwordHash")] public string PasswordHash { get; set; } = "";
        [JsonPropertyName("canUseDevice")] public bool CanUseDevice { get; set; } = true;
    }
}
