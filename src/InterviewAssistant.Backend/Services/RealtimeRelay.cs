using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using InterviewAssistant.Contracts;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

/// <summary>Opens the upstream realtime transcription WebSocket with a server-held credential.</summary>
public interface IRealtimeUpstreamConnector
{
    Task<WebSocket> ConnectAsync(RealtimeCredential credential, CancellationToken ct);
}

public sealed class OpenAiRealtimeUpstreamConnector : IRealtimeUpstreamConnector
{
    public async Task<WebSocket> ConnectAsync(RealtimeCredential credential, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + credential.ClientSecret);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(TimeSpan.FromSeconds(10));
        try { await ws.ConnectAsync(new Uri(credential.WebSocketUrl), connect.Token); }
        catch { ws.Dispose(); throw; }
        return ws;
    }
}

/// <summary>
/// Live relays by lease. Ending a lease (stop, expiry, allowance exhausted, max duration, device revoked) closes its
/// relay immediately in this process; relays also re-check the lease in the database, which covers other replicas.
/// One relay per lease: a reconnect replaces the previous connection.
/// </summary>
public sealed class RealtimeRelayRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _relays = new();

    public CancellationTokenSource Register(Guid sessionId, CancellationToken requestAborted)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        _relays.AddOrUpdate(sessionId, cts, (_, previous) => { Cancel(previous); return cts; });
        return cts;
    }

    public void Unregister(Guid sessionId, CancellationTokenSource cts) => _relays.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(sessionId, cts));

    public void Close(Guid sessionId) { if (_relays.TryRemove(sessionId, out var cts)) Cancel(cts); }

    public int ActiveCount => _relays.Count;

    private static void Cancel(CancellationTokenSource cts) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
}

public sealed class RelayOptions
{
    /// <summary>How often a relay re-reads its lease from the database (covers leases ended on another replica).</summary>
    public int LeaseCheckSeconds { get; set; } = 2;
    /// <summary>Upper bound for <see cref="LeaseCheckSeconds"/>, kept below the billing margin of <see cref="SessionService.RelayCheckMarginSeconds"/>.</summary>
    public const int MaxLeaseCheckSeconds = 2;
    /// <summary>Largest client message accepted (an audio append of ~2.7 s of 24 kHz PCM16 in base64).</summary>
    public int MaxClientMessageBytes { get; set; } = 256 * 1024;
}

/// <summary>
/// Server-authoritative live transcription. The desktop never receives a provider credential: it opens a WebSocket to
/// this service, which mints the upstream credential itself, forwards audio and transcription events, and closes the
/// connection the moment the billable lease is no longer active. Only audio-buffer messages are forwarded upstream,
/// so a client cannot reconfigure the session (model, prompt) through the relay.
/// </summary>
public sealed class RealtimeRelay
{
    private static readonly HashSet<string> AllowedClientTypes = new(StringComparer.Ordinal)
    {
        "input_audio_buffer.append", "input_audio_buffer.commit", "input_audio_buffer.clear",
    };

    private readonly IRealtimeUpstreamConnector _upstream;
    private readonly RealtimeRelayRegistry _registry;
    private readonly RelayOptions _opt;
    private readonly ILogger<RealtimeRelay> _log;
    public RealtimeRelay(IRealtimeUpstreamConnector upstream, RealtimeRelayRegistry registry, IOptions<RelayOptions> opt, ILogger<RealtimeRelay> log)
    { _upstream = upstream; _registry = registry; _opt = opt.Value; _log = log; }

    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false, MaxDepth = 4 };

    /// <summary>
    /// Validates a client message and returns a <b>rebuilt</b> copy holding only <c>type</c> (and <c>audio</c> for an
    /// append) — the client's own bytes are never forwarded, so duplicate keys, trailing content or extra fields can
    /// never reach the provider. Null when the message is not an allowed audio-buffer message.
    /// </summary>
    public static byte[]? RebuildClientMessage(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, StrictJson);   // rejects trailing content, duplicate keys, invalid UTF-8
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return null;
            var type = t.GetString()!;
            if (!AllowedClientTypes.Contains(type)) return null;
            string? audio = null;
            if (type == "input_audio_buffer.append")
            {
                if (!root.TryGetProperty("audio", out var a) || a.ValueKind != JsonValueKind.String) return null;
                audio = a.GetString();
                if (string.IsNullOrEmpty(audio) || !IsBase64(audio)) return null;
            }
            using var ms = new MemoryStream(json.Length + 16);
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("type", type);
                if (audio != null) w.WriteString("audio", audio);
                w.WriteEndObject();
            }
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static bool IsBase64(string s)
    {
        if (s.Length % 4 != 0) return false;
        foreach (var c in s) if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')) return false;
        return true;
    }

    public static bool IsAllowedClientMessage(ReadOnlySpan<byte> json) => RebuildClientMessage(json.ToArray()) != null;

    /// <param name="leaseActive">Reads the lease state from the database (called every <see cref="RelayOptions.LeaseCheckSeconds"/>).</param>
    public async Task RunAsync(HttpContext http, Guid sessionId, RealtimeCredential upstreamCredential, Func<CancellationToken, Task<bool>> leaseActive)
    {
        WebSocket upstream;
        try { upstream = await _upstream.ConnectAsync(upstreamCredential, http.RequestAborted); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            _log.LogWarning("Relay for session {SessionId}: upstream unavailable ({Error})", sessionId, ex.GetType().Name);
            http.Response.StatusCode = 502;
            await http.Response.WriteAsJsonAsync(new ApiError("realtime_unavailable", "Live transcription is temporarily unavailable.", http.Items["cid"] as string));
            return;
        }
        using var _u = upstream;
        using var client = await http.WebSockets.AcceptWebSocketAsync();
        // stop: the lease ended (registry) or the client went away; pumps: cancelled only after the close frame is sent.
        var stop = _registry.Register(sessionId, http.RequestAborted);
        using var pumps = new CancellationTokenSource();
        using var clientSend = new SemaphoreSlim(1, 1);
        string reason;
        long forwarded = 0, dropped = 0;
        var toUpstream = PumpAsync(client, upstream, filter: true, null, pumps.Token, () => forwarded++, () => dropped++);
        var toClient = PumpAsync(upstream, client, filter: false, clientSend, pumps.Token, null, null);
        var monitor = MonitorLeaseAsync(leaseActive, stop.Token);
        var stopped = Task.Delay(Timeout.Infinite, stop.Token);
        var first = await Task.WhenAny(toUpstream, toClient, monitor, stopped);
        if (http.RequestAborted.IsCancellationRequested) reason = "client_gone";
        else if (first == monitor || first == stopped) reason = "lease_ended";   // includes a failed lease check (fail closed)
        else reason = first == toClient ? "upstream_closed" : "client_closed";
        try
        {
            await clientSend.WaitAsync(TimeSpan.FromSeconds(2));
            await CloseQuietly(client, reason == "lease_ended" ? WebSocketCloseStatus.PolicyViolation : WebSocketCloseStatus.NormalClosure, reason);
        }
        finally
        {
            pumps.Cancel();
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            _registry.Unregister(sessionId, stop);
            await CloseQuietly(upstream, WebSocketCloseStatus.NormalClosure, "relay_closed");
            try { await Task.WhenAll(toUpstream, toClient, monitor, stopped).WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* cancellation / closed sockets */ }
            stop.Dispose();
            _log.LogInformation("Relay for session {SessionId} closed: {Reason} ({Forwarded} client messages forwarded, {Dropped} dropped)", sessionId, reason, forwarded, dropped);
        }
    }

    private async Task MonitorLeaseAsync(Func<CancellationToken, Task<bool>> leaseActive, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!await leaseActive(ct)) return;   // first check right away: a lease that ended while connecting upstream
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_opt.LeaseCheckSeconds, 1, RelayOptions.MaxLeaseCheckSeconds)), ct);
        }
    }

    private async Task PumpAsync(WebSocket from, WebSocket to, bool filter, SemaphoreSlim? sendLock, CancellationToken ct, Action? onForward, Action? onDrop)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && from.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult r;
            bool tooLarge = false;
            do
            {
                r = await from.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                if (filter && message.Length + r.Count > _opt.MaxClientMessageBytes) tooLarge = true;
                else if (!tooLarge) message.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            ReadOnlyMemory<byte> outgoing = message.GetBuffer().AsMemory(0, (int)message.Length);
            if (filter)
            {
                var rebuilt = tooLarge || r.MessageType != WebSocketMessageType.Text ? null : RebuildClientMessage(outgoing);
                if (rebuilt == null) { onDrop?.Invoke(); continue; }
                outgoing = rebuilt;
            }
            if (sendLock != null) await sendLock.WaitAsync(ct);
            try { await to.SendAsync(outgoing, r.MessageType, true, ct); }
            finally { sendLock?.Release(); }
            onForward?.Invoke();
        }
    }

    private static async Task CloseQuietly(WebSocket ws, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(status, reason, t.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { ws.Abort(); }
    }
}

/// <summary>Credential returned to the desktop: a relay endpoint on this service, never a provider secret.</summary>
public static class RelayCredential
{
    /// <summary>Relative to the API base address (the desktop resolves it against its configured service URL).</summary>
    public static string PathFor(Guid sessionId) => $"api/v1/sessions/{sessionId}/realtime";
    public static RealtimeCredential For(Guid sessionId, string model, DateTime expiresUtc) =>
        new("", expiresUtc, model, PathFor(sessionId), "", Relay: true);
}
