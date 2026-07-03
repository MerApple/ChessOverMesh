using ChessOverMesh.Mesh;
using Meshtastic.Proxy;

const string Usage =
    "Meshtastic.Proxy — share one Meshtastic device with several GUI/MAUI clients over TLS.\n\n" +
    "Usage: Meshtastic.Proxy --device <target> [--port <listen>] [--pfx <file>] [--pfx-pass <pw>] [--users <file>]\n\n" +
    "  --device, -d   The device to proxy: host[:port] for the TCP stream API (default port 4403),\n" +
    "                 or http(s)://host for the HTTP API.\n" +
    "  --port,   -p   TCP port the proxy listens on for clients (TLS). Default 4403.\n" +
    "  --pfx          PFX certificate for the TLS server. Generated (self-signed) if missing.\n" +
    "  --pfx-pass     Password for the PFX. Default 'meshtastic'.\n" +
    "  --users        JSON file of accounts (env: PROXY_USERS). Each has a username, a hashed password, and a\n" +
    "                 canUseDevice flag. A restricted user (canUseDevice:false) can connect and chat with the\n" +
    "                 other clients but nothing it sends reaches the radio. Overrides --user/--pass.\n" +
    "  --user, -u     Legacy single account (env: PROXY_USER); always device-enabled. Ignored when --users is set.\n" +
    "  --pass         Password for the legacy --user account (env: PROXY_PASS).\n" +
    "  --verbose, -v  Log every packet forwarded to the device and broadcast to clients (for debugging).\n\n" +
    "Manage the users file (these run and exit, no --device needed):\n" +
    "  --add-user <name> [--pass <pw>] [--restricted] [--users <file>]   Add/replace a user (prompts for the\n" +
    "                 password if --pass is omitted; --restricted makes them local-chat-only). Default file users.json.\n" +
    "  --hash-pass <pw>                                                  Print a password hash for hand-editing.\n\n" +
    "Clients connect with the address  proxy://<this-host>[:<port>]  using the normal Connect button.";

string deviceTarget = "";
int listenPort = 4403;
string pfxPath = "meshtastic-proxy.pfx";
string pfxPass = "meshtastic";
string? authUser = Environment.GetEnvironmentVariable("PROXY_USER");
string? authPass = Environment.GetEnvironmentVariable("PROXY_PASS");
string? usersPath = Environment.GetEnvironmentVariable("PROXY_USERS");
string? addUser = null;
string? hashPass = null;
bool restricted = false;
bool verbose = false;

void Log(string m) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string? val = i + 1 < args.Length ? args[i + 1] : null;
    switch (a)
    {
        case "--device" or "-d" when val != null: deviceTarget = val; i++; break;
        case "--port" or "-p" when val != null && int.TryParse(val, out var pp): listenPort = pp; i++; break;
        case "--pfx" when val != null: pfxPath = val; i++; break;
        case "--pfx-pass" when val != null: pfxPass = val; i++; break;
        case "--users" when val != null: usersPath = val; i++; break;
        case "--user" or "-u" when val != null: authUser = val; i++; break;
        case "--pass" when val != null: authPass = val; i++; break;
        case "--add-user" when val != null: addUser = val; i++; break;
        case "--hash-pass" when val != null: hashPass = val; i++; break;
        case "--restricted": restricted = true; break;
        case "--verbose" or "-v": verbose = true; break;
        case "--help" or "-h": Console.WriteLine(Usage); return 0;
    }
}

// ---- Helper subcommands: manage the users file, then exit (no device connection). ----
if (hashPass != null) { Console.WriteLine(UserStore.HashPassword(hashPass)); return 0; }

if (addUser != null)
{
    string pw = authPass ?? ReadPasswordMasked($"Password for '{addUser}': ");
    if (pw.Length == 0) { Console.Error.WriteLine("Empty password — aborting."); return 1; }
    UserStore.UpsertUser(usersPath ?? "users.json", addUser, pw, canUseDevice: !restricted, Log);
    return 0;
}

if (deviceTarget.Length == 0) { Console.Error.WriteLine("Missing --device.\n\n" + Usage); return 1; }

// A factory the hub calls to (re)connect to the device. TCP throws if the port is closed (clean retry); HTTP is
// connectionless, so it returns immediately and a failed first read drives the hub's reconnect instead.
async Task<IMeshTransport> ConnectDevice(CancellationToken ct)
{
    if (deviceTarget.StartsWith("http", StringComparison.OrdinalIgnoreCase))
    {
        Log($"Using HTTP device {deviceTarget}.");
        return new HttpMeshTransport(deviceTarget);
    }
    string host = deviceTarget;
    int port = TcpStreamMeshTransport.DefaultPort;
    int colon = deviceTarget.LastIndexOf(':');
    if (colon > 0 && int.TryParse(deviceTarget[(colon + 1)..], out var dp)) { host = deviceTarget[..colon]; port = dp; }
    Log($"Connecting to device {host}:{port} (TCP)…");
    return await TcpStreamMeshTransport.ConnectAsync(host, port, TimeSpan.FromSeconds(10), ct);
}

// Build the credential store: the --users JSON file if given, else the legacy single --user (device-enabled), else
// none (open proxy).
UserStore? users = null;
if (!string.IsNullOrEmpty(usersPath))
    users = UserStore.Load(usersPath, Log);
else if (!string.IsNullOrEmpty(authUser))
    users = UserStore.FromUsers(new[] { new ProxyUser(authUser, UserStore.HashPassword(authPass ?? ""), CanUseDevice: true) });

var cert = SelfSignedCert.GetOrCreate(pfxPath, pfxPass);
var hub = new ProxyHub(ConnectDevice, cert, listenPort, Log, verbose, users);
if (users is { Count: > 0 }) Log($"Client authentication ENABLED ({users.Count} user(s)). Clients must sign in.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Log("Stopping…"); };

Log($"Meshtastic.Proxy: device '{deviceTarget}', listening on :{listenPort} (TLS). Clients connect with proxy://<host>:{listenPort}.");
try { await hub.RunAsync(cts.Token); }
catch (OperationCanceledException) { }
catch (Exception ex) { Log($"Proxy error: {ex.Message}"); }

Log("Proxy stopped.");
return 0;

// Reads a line from the console without echoing it. Falls back to a plain read when input is redirected (piped).
static string ReadPasswordMasked(string prompt)
{
    Console.Write(prompt);
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Remove(sb.Length - 1, 1); continue; }
        if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
    }
    return sb.ToString();
}
