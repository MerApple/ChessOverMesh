using ChessOverMesh.Mesh;
using Meshtastic.Proxy;

const string Usage =
    "Meshtastic.Proxy — share one Meshtastic device with several GUI/MAUI clients over TLS.\n\n" +
    "Usage: Meshtastic.Proxy --device <target> [--port <listen>] [--pfx <file>] [--pfx-pass <pw>]\n\n" +
    "  --device, -d   The device to proxy: host[:port] for the TCP stream API (default port 4403),\n" +
    "                 or http(s)://host for the HTTP API.\n" +
    "  --port,   -p   TCP port the proxy listens on for clients (TLS). Default 4403.\n" +
    "  --pfx          PFX certificate for the TLS server. Generated (self-signed) if missing.\n" +
    "  --pfx-pass     Password for the PFX. Default 'meshtastic'.\n" +
    "  --user, -u     Require clients to sign in with this username (env: PROXY_USER). Omit = no auth.\n" +
    "  --pass         Password clients must supply (env: PROXY_PASS). Used only when --user is set.\n" +
    "  --verbose, -v  Log every packet forwarded to the device and broadcast to clients (for debugging).\n\n" +
    "Clients connect with the address  proxy://<this-host>[:<port>]  using the normal Connect button.";

string deviceTarget = "";
int listenPort = 4403;
string pfxPath = "meshtastic-proxy.pfx";
string pfxPass = "meshtastic";
string? authUser = Environment.GetEnvironmentVariable("PROXY_USER");
string? authPass = Environment.GetEnvironmentVariable("PROXY_PASS");
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
        case "--user" or "-u" when val != null: authUser = val; i++; break;
        case "--pass" when val != null: authPass = val; i++; break;
        case "--verbose" or "-v": verbose = true; break;
        case "--help" or "-h": Console.WriteLine(Usage); return 0;
    }
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

var cert = SelfSignedCert.GetOrCreate(pfxPath, pfxPass);
var hub = new ProxyHub(ConnectDevice, cert, listenPort, Log, verbose, authUser, authPass);
if (!string.IsNullOrEmpty(authUser)) Log($"Client authentication ENABLED (user '{authUser}'). Clients must sign in.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Log("Stopping…"); };

Log($"Meshtastic.Proxy: device '{deviceTarget}', listening on :{listenPort} (TLS). Clients connect with proxy://<host>:{listenPort}.");
try { await hub.RunAsync(cts.Token); }
catch (OperationCanceledException) { }
catch (Exception ex) { Log($"Proxy error: {ex.Message}"); }

Log("Proxy stopped.");
return 0;
