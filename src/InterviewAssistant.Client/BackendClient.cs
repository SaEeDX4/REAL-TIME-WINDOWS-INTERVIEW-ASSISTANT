using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InterviewAssistant.Contracts;

namespace InterviewAssistant.Client;

public sealed class BackendException(int status, string code, string message, string? correlationId) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? CorrelationId { get; } = correlationId;
    public bool IsUpdateRequired => Status == 426;
    public bool IsEntitlement => Status == 402 || Code is "device_limit" or "concurrent_limit" or "allowance_exhausted";
}

/// <summary>Typed client for /api/v1. Adds bearer token, client version and correlation id; retries once after a token refresh on 401.</summary>
public sealed class BackendClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly Func<bool, CancellationToken, Task<string?>> _token;
    private readonly string _version;

    /// <param name="token">Returns an access token; the bool asks for a forced refresh.</param>
    public BackendClient(HttpClient http, Func<bool, CancellationToken, Task<string?>> token, string clientVersion)
    {
        _http = http; _token = token; _version = clientVersion;
    }

    public static BackendClient ForAuth(HttpClient http, SupabaseAuth auth, string clientVersion) =>
        new(http, (force, ct) => auth.GetAccessTokenAsync(ct, force), clientVersion);

    public string? LastCorrelationId { get; private set; }

    // ---- public ----
    public Task<RuntimeConfig> ConfigAsync(CancellationToken ct) => Send<RuntimeConfig>(HttpMethod.Get, "config", null, ct, auth: false);
    public Task<List<PlanDto>> PlansAsync(CancellationToken ct) => Send<List<PlanDto>>(HttpMethod.Get, "billing/plans", null, ct, auth: false);

    // ---- account ----
    public Task<AccountDto> MeAsync(CancellationToken ct) => Send<AccountDto>(HttpMethod.Get, "me", null, ct);
    public Task<JsonElement> ExportAsync(CancellationToken ct) => Send<JsonElement>(HttpMethod.Get, "me/export", null, ct);
    public Task DeleteAccountAsync(CancellationToken ct) => Send<JsonElement?>(HttpMethod.Delete, "me", null, ct);
    public Task<UrlResponse> CheckoutAsync(string planCode, CancellationToken ct) => Send<UrlResponse>(HttpMethod.Post, "billing/checkout", new CheckoutRequest(planCode), ct);
    public Task<UrlResponse> PortalAsync(CancellationToken ct) => Send<UrlResponse>(HttpMethod.Post, "billing/portal", null, ct);

    // ---- devices ----
    public Task<DeviceDto> RegisterDeviceAsync(RegisterDeviceRequest r, CancellationToken ct) => Send<DeviceDto>(HttpMethod.Post, "devices", r, ct);
    public Task<List<DeviceDto>> DevicesAsync(CancellationToken ct) => Send<List<DeviceDto>>(HttpMethod.Get, "devices", null, ct);
    public Task RevokeDeviceAsync(Guid id, CancellationToken ct) => Send<JsonElement?>(HttpMethod.Delete, $"devices/{id}", null, ct);

    // ---- synced documents: kind = profiles | targets | reports ----
    public Task<List<SyncDocument>> ListAsync(string kind, Guid? parentId, CancellationToken ct) =>
        Send<List<SyncDocument>>(HttpMethod.Get, parentId == null ? kind : $"{kind}?parentId={parentId}", null, ct);
    public Task<SyncDocument> PutAsync(string kind, Guid id, UpsertDocumentRequest r, CancellationToken ct) => Send<SyncDocument>(HttpMethod.Put, $"{kind}/{id}", r, ct);
    public Task DeleteDocumentAsync(string kind, Guid id, CancellationToken ct) => Send<JsonElement?>(HttpMethod.Delete, $"{kind}/{id}", null, ct);

    // ---- preparation + sessions ----
    public Task<PrepJobResponse> StartPreparationAsync(CancellationToken ct) => Send<PrepJobResponse>(HttpMethod.Post, "preparation/jobs", null, ct);
    public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest r, CancellationToken ct) => Send<StartSessionResponse>(HttpMethod.Post, "sessions", r, ct);
    public Task<HeartbeatResponse> HeartbeatAsync(Guid id, long seq, CancellationToken ct) => Send<HeartbeatResponse>(HttpMethod.Post, $"sessions/{id}/heartbeat", new HeartbeatRequest(seq), ct);
    public Task<RealtimeCredential> RenewRealtimeSecretAsync(Guid id, CancellationToken ct) => Send<RealtimeCredential>(HttpMethod.Post, $"sessions/{id}/realtime-secret", null, ct);
    public Task StopSessionAsync(Guid id, CancellationToken ct) => Send<JsonElement?>(HttpMethod.Post, $"sessions/{id}/stop", null, ct);

    /// <summary>Streams answer deltas from the server-side model (SSE).</summary>
    public async IAsyncEnumerable<string> StreamAnswerAsync(AnswerStreamRequest r, [EnumeratorCancellation] CancellationToken ct)
    {
        using var resp = await SendRaw(HttpMethod.Post, "answers/stream", r, ct, auth: true, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) throw await ToError(resp);
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? evt = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) { evt = null; continue; }
            if (line.StartsWith("event:")) { evt = line[6..].Trim(); continue; }
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            if (evt == "error") throw new BackendException(502, "ai_interrupted", "The answer stream was interrupted.", LastCorrelationId);
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("delta", out var d) && d.GetString() is { Length: > 0 } s) yield return s;
        }
        throw new BackendException(502, "stream_truncated", "The answer stream ended unexpectedly.", LastCorrelationId);
    }

    private async Task<T> Send<T>(HttpMethod m, string path, object? body, CancellationToken ct, bool auth = true)
    {
        using var resp = await SendRaw(m, path, body, ct, auth, HttpCompletionOption.ResponseContentRead);
        if (!resp.IsSuccessStatusCode) throw await ToError(resp);
        if (resp.StatusCode == HttpStatusCode.NoContent || resp.Content.Headers.ContentLength == 0) return default!;
        return (await resp.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendRaw(HttpMethod m, string path, object? body, CancellationToken ct, bool auth, HttpCompletionOption completion)
    {
        for (int attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(m, "api/v1/" + path);
            if (body != null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
            req.Headers.Add("X-Client-Version", _version);
            var cid = Guid.NewGuid().ToString("n");
            req.Headers.Add("X-Correlation-Id", cid);
            LastCorrelationId = cid;
            if (auth)
            {
                var token = await _token(attempt > 0, ct) ?? throw new BackendException(401, "signed_out", "Please sign in.", null);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, completion, ct); }
            catch (HttpRequestException ex) { throw new BackendException(0, "offline", "Cannot reach the service. Check your connection.", cid) { Source = ex.Message }; }
            finally { req.Dispose(); }
            if (resp.StatusCode == HttpStatusCode.Unauthorized && auth && attempt == 0) { resp.Dispose(); continue; }
            return resp;
        }
    }

    private async Task<BackendException> ToError(HttpResponseMessage resp)
    {
        ApiError? e = null;
        try { e = await resp.Content.ReadFromJsonAsync<ApiError>(Json); } catch (JsonException) { } catch (NotSupportedException) { }
        return new BackendException((int)resp.StatusCode, e?.Code ?? "http_" + (int)resp.StatusCode,
            e?.Message ?? "The service returned an error.", e?.CorrelationId ?? LastCorrelationId);
    }
}
