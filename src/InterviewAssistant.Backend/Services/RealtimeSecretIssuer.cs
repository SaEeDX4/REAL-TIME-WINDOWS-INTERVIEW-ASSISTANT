using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

public interface IRealtimeSecretIssuer
{
    /// <summary>Mints a secret for the first model the provider accepts (primary, then fallbacks).</summary>
    Task<RealtimeCredential> IssueAsync(IReadOnlyList<string> models, string prompt, string? language, CancellationToken ct);
}

/// <summary>
/// Mints short-lived OpenAI Realtime client secrets (POST /v1/realtime/client_secrets) with the session configuration
/// baked in. The permanent API key never leaves the server; the desktop connects to OpenAI directly with the ephemeral
/// secret (lowest latency) and must renew it through an active lease.
/// </summary>
public sealed class OpenAiRealtimeSecretIssuer : IRealtimeSecretIssuer
{
    private readonly HttpClient _http;
    private readonly OpenAiOptions _opt;
    private readonly TimeProvider _time;
    private readonly ILogger<OpenAiRealtimeSecretIssuer> _log;
    public const string WebSocketUrl = "wss://api.openai.com/v1/realtime?intent=transcription";
    public OpenAiRealtimeSecretIssuer(HttpClient http, IOptions<OpenAiOptions> opt, TimeProvider time, ILogger<OpenAiRealtimeSecretIssuer> log) { _http = http; _opt = opt.Value; _time = time; _log = log; }

    public async Task<RealtimeCredential> IssueAsync(IReadOnlyList<string> models, string prompt, string? language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey)) throw new ApiException(503, "ai_not_configured", "Live transcription is not available right now.");
        ApiException? last = null;
        foreach (var model in models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct())
        {
            try { return await IssueOneAsync(model, prompt, language, ct); }
            catch (ModelRejectedException ex) { _log.LogWarning("Transcription model {Model} rejected by provider ({Status}); trying fallback", model, ex.Status); last = new ApiException(502, "realtime_secret_failed", "Live transcription could not be started."); }
        }
        throw last ?? new ApiException(503, "ai_not_configured", "No transcription model is configured.");
    }

    private sealed class ModelRejectedException(int status) : Exception { public int Status { get; } = status; }

    private async Task<RealtimeCredential> IssueOneAsync(string model, string prompt, string? language, CancellationToken ct)
    {
        var transcription = new JsonObject { ["model"] = model, ["prompt"] = prompt };
        if (!string.IsNullOrWhiteSpace(language) && language != "auto") transcription["language"] = language;
        var body = new JsonObject
        {
            ["expires_after"] = new JsonObject { ["anchor"] = "created_at", ["seconds"] = _opt.ClientSecretTtlSeconds },
            ["session"] = new JsonObject
            {
                ["type"] = "transcription",
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                        ["transcription"] = transcription,
                        ["turn_detection"] = new JsonObject { ["type"] = "server_vad", ["threshold"] = 0.5, ["prefix_padding_ms"] = 300, ["silence_duration_ms"] = 400 },
                    },
                },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, _opt.BaseUrl.TrimEnd('/') + "/v1/realtime/client_secrets") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("OpenAI unreachable while minting a transcription secret: {Error}", ex.GetType().Name);
            throw new ApiException(503, "ai_unavailable", "The transcription service is temporarily unavailable.");
        }
        using var _ = resp;
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            _log.LogError("OpenAI client_secrets failed for {Model}: HTTP {Status} {Code}", model, code, ErrorCode(text));
            if (code is 400 or 403 or 404) throw new ModelRejectedException(code);
            throw new ApiException(code == 429 ? 429 : 502, code == 429 ? "ai_rate_limited" : "realtime_secret_failed", "Live transcription could not be started. Please try again.");
        }
        var node = JsonNode.Parse(text)!;
        var secret = node["value"] ?? node["client_secret"]?["value"];
        var exp = node["expires_at"] ?? node["client_secret"]?["expires_at"];
        if (secret == null) throw new ApiException(502, "realtime_secret_failed", "Unexpected response from the transcription provider.");
        var expires = exp != null ? DateTimeOffset.FromUnixTimeSeconds(exp.GetValue<long>()).UtcDateTime : _time.GetUtcNow().UtcDateTime.AddSeconds(_opt.ClientSecretTtlSeconds);
        return new RealtimeCredential(secret.GetValue<string>(), expires, model, WebSocketUrl, prompt);
    }

    private static string? ErrorCode(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["code"]?.ToString(); } catch (System.Text.Json.JsonException) { return null; }
    }
}
