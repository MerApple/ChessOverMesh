using System.Net.Http.Headers;

namespace ChessOverMesh.Mesh;

/// <summary>
/// The byte-level link to a Meshtastic device. Every link (HTTP, BLE, serial, TCP) carries the same
/// ToRadio/FromRadio protobufs — a transport only moves the serialized bytes. <see cref="MeshtasticHttpClient"/>
/// owns all protocol, parsing and state and talks to the device exclusively through this interface, so adding
/// a new link (e.g. Bluetooth LE) is just a new <see cref="IMeshTransport"/> with no client changes.
/// </summary>
public interface IMeshTransport : IDisposable
{
    /// <summary>Submits one serialized ToRadio protobuf to the device.</summary>
    Task WriteAsync(byte[] toRadio, CancellationToken ct);

    /// <summary>Returns the next serialized FromRadio protobuf, or null when the device's queue is empty.
    /// <paramref name="all"/> is an HTTP hint (drain everything); links without that notion ignore it.
    /// <paramref name="requestTimeout"/>, when set, bounds this single read separately from <paramref name="ct"/>.</summary>
    Task<byte[]?> ReadAsync(bool all, CancellationToken ct, TimeSpan? requestTimeout = null);

    /// <summary>False once a persistent link (TCP/BLE) has dropped — its socket died even though the device may
    /// still be reachable by a fresh connection. Connectionless links (HTTP) are always "connected"; their loss
    /// is detected separately by the reachability probe.</summary>
    bool IsConnected => true;

    /// <summary>True for a persistent link that the device may close when idle, so the client should send
    /// periodic heartbeats. Connectionless HTTP doesn't need them (every poll is a fresh request).</summary>
    bool NeedsKeepAlive => false;

    /// <summary>True when <see cref="IsConnected"/> authoritatively reflects this link's own liveness — a persistent
    /// socket (TCP/BLE) that faults the moment its link drops. For these the external reachability probe must NOT
    /// run: opening a SECOND connection to "probe" the device can be refused precisely because this live connection
    /// holds the device's single client slot (the TCP stream API on 4403 allows one client), which would look like a
    /// false "unreachable". HTTP is connectionless and always reports "connected", so it leaves this false and the
    /// probe keeps guarding it.</summary>
    bool SelfReportsLiveness => false;

    /// <summary>A short human-readable reason the link dropped (peer closed it, connection reset, keep-alive
    /// timeout, network unreachable, …), or null if it hasn't dropped or this transport doesn't track one. A
    /// persistent link sets it the moment it faults so the app can tell the user *why* it disconnected instead of a
    /// generic "connection lost".</summary>
    string? LastError => null;
}

/// <summary>
/// <see cref="IMeshTransport"/> over the device's HTTP(S) REST API:
///   PUT  /api/v1/toradio   — submit a serialized ToRadio protobuf
///   GET  /api/v1/fromradio — drain one serialized FromRadio protobuf per call
/// This is the original transport, factored out unchanged so BLE can sit alongside it.
/// </summary>
public sealed class HttpMeshTransport : IMeshTransport
{
    private const string ProtobufMediaType = "application/x-protobuf";
    private readonly HttpClient _http;
    // The device serves its HTTP API one request at a time (the ESP32 runs it from the main LoRa loop), and a
    // second concurrent connection is refused/reset. A send (PUT) that races the poll loop's GET would then be
    // silently lost. Serialise every request so the device only ever sees one at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HttpMeshTransport(string baseUrl)
    {
        // The device commonly serves HTTPS with a self-signed certificate; accept it.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            // Per-request safety net; overall connect timeout is enforced via a CancellationToken.
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(ProtobufMediaType));
    }

    public async Task WriteAsync(byte[] toRadio, CancellationToken ct)
    {
        var content = new ByteArrayContent(toRadio);
        content.Headers.ContentType = new MediaTypeHeaderValue(ProtobufMediaType);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var response = await _http.PutAsync("api/v1/toradio", content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]?> ReadAsync(bool all, CancellationToken ct, TimeSpan? requestTimeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (requestTimeout is { } t) cts.CancelAfter(t);
        await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
        try
        {
            using var response = await _http.GetAsync($"api/v1/fromradio?all={(all ? "true" : "false")}", cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            return bytes.Length == 0 ? null : bytes; // empty body == nothing queued
        }
        finally { _gate.Release(); }
    }

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
