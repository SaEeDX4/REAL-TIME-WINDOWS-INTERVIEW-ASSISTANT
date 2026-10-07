using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

public interface IRealtimeSecretIssuer
{
    Task<RealtimeCredential> IssueAsync(string model, string prompt, string? language, CancellationToken ct);
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
    public OpenAiRealtimeSecretIssuer(HttpClient http, IOptions<OpenAiOptions> opt, TimeProvider time) { _http = http; _opt = opt.Value; _time = time; }

    public async Task<RealtimeCredential> IssueAsync(string model, string prompt, string? language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey)) throw new ApiException(503, "ai_not_configured", "The AI service is not configured on the server.");
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
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new ApiException(502, "realtime_secret_failed", $"Transcription credentials could not be created (HTTP {(int)resp.StatusCode}).");
        var node = JsonNode.Parse(text)!;
        var secret = node["value"] ?? node["client_secret"]?["value"];
        var exp = node["expires_at"] ?? node["client_secret"]?["expires_at"];
        if (secret == null) throw new ApiException(502, "realtime_secret_failed", "Unexpected response from the transcription provider.");
        var expires = exp != null ? DateTimeOffset.FromUnixTimeSeconds(exp.GetValue<long>()).UtcDateTime : _time.GetUtcNow().UtcDateTime.AddSeconds(_opt.ClientSecretTtlSeconds);
        return new RealtimeCredential(secret.GetValue<string>(), expires, model, "wss://api.openai.com/v1/realtime?intent=transcription", prompt);
    }
}
