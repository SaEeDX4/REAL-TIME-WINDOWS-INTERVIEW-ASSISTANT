using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InterviewAssistant.Core.Intelligence;

namespace InterviewAssistant.Core.Providers;

public sealed class ChatProviderOptions
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    /// <summary>
    /// Default: gpt-5.4-mini with reasoning off (OpenAI, Mar 2026: ~0.7 s first token, ~175 tok/s). Configurable in Settings.
    /// </summary>
    public string Model { get; set; } = "gpt-5.4-mini";
    /// <summary>Used automatically if the configured model is rejected as unknown/unavailable for this account.</summary>
    public string FallbackModel { get; set; } = "gpt-4.1-mini";
    public double Temperature { get; set; } = 0.4;
    public TimeSpan FirstTokenTimeout { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>"auto" picks the lowest-latency effort the model family accepts (gpt-5.x: none; gpt-5/5-mini/o*: minimal).</summary>
    public string ReasoningEffort { get; set; } = "auto";
}

/// <summary>
/// Streaming Chat Completions client (server-sent events). One HttpClient for the app lifetime (connection
/// reuse keeps TLS warm, which matters for first-token latency). Maps HTTP failures to ProviderErrorKind.
/// </summary>
public sealed class OpenAiChatAnswerProvider : IAnswerProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    public ChatProviderOptions Options { get; }
    public string Name => "OpenAI " + ActiveModel;
    public int RequestCount { get; private set; }
    public long ApproxInputChars { get; private set; }
    public long ApproxOutputChars { get; private set; }

    public OpenAiChatAnswerProvider(Func<string?> apiKeyProvider, ChatProviderOptions? options = null, HttpMessageHandler? handler = null)
    {
        _apiKey = apiKeyProvider;
        Options = options ?? new ChatProviderOptions();
        _http = handler == null
            ? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(8) })
            : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request cancellation instead
    }

    public static bool IsReasoningModel(string model) =>
        model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o3", StringComparison.OrdinalIgnoreCase) || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lowest-latency reasoning effort accepted by the model family.</summary>
    public static string AutoEffort(string model)
    {
        var m = model.ToLowerInvariant();
        // gpt-5.1 and later (gpt-5.4-mini, ...) accept "none"; the original gpt-5 family and o-series use "minimal"/"low".
        if (System.Text.RegularExpressions.Regex.IsMatch(m, @"^gpt-5\.\d")) return "none";
        if (m.StartsWith("gpt-5")) return "minimal";
        return "low";
    }

    private string? _activeModel;
    private bool _omitReasoning;
    /// <summary>The model actually in use (differs from Options.Model after an automatic fallback).</summary>
    public string ActiveModel => _activeModel ?? Options.Model;

    internal string BuildBody(IReadOnlyList<ChatMessage> messages, int maxTokens)
    {
        var model = ActiveModel;
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
        };
        if (IsReasoningModel(model))
        {
            if (!_omitReasoning) body["reasoning_effort"] = Options.ReasoningEffort == "auto" ? AutoEffort(model) : Options.ReasoningEffort;
            body["max_completion_tokens"] = maxTokens + 600; // reasoning tokens count toward the limit
        }
        else
        {
            body["temperature"] = Options.Temperature;
            body["max_completion_tokens"] = maxTokens;
        }
        return body.ToJsonString();
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new ProviderException(ProviderErrorKind.InvalidApiKey, "No API key configured");

        using var firstTokenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        firstTokenCts.CancelAfter(Options.FirstTokenTimeout);

        // Up to 3 attempts, only for configuration rejections that are fixable automatically (never mid-stream):
        //  1) unsupported reasoning_effort -> resend without it; 2) unknown/unavailable model -> fallback model.
        HttpResponseMessage resp;
        int attempt = 0;
        while (true)
        {
            var json = BuildBody(messages, maxTokens);
            ApproxInputChars += json.Length;
            RequestCount++;
            using var req = new HttpRequestMessage(HttpMethod.Post, Options.Endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, firstTokenCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException(ProviderErrorKind.Timeout, "Answer service timed out"); }
            catch (HttpRequestException ex) { throw new ProviderException(ProviderErrorKind.Network, "Network error: " + ex.Message, ex); }
            if (resp.IsSuccessStatusCode) break;

            var err = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            var mapped = MapError(resp.StatusCode, err);
            resp.Dispose();
            attempt++;
            if (attempt < 3 && mapped.Kind == ProviderErrorKind.BadRequest)
            {
                if (!_omitReasoning && err.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) { _omitReasoning = true; continue; }
                if (ActiveModel != Options.FallbackModel && !string.IsNullOrEmpty(Options.FallbackModel) &&
                    (err.Contains("model", StringComparison.OrdinalIgnoreCase) || resp.StatusCode == HttpStatusCode.NotFound))
                { _activeModel = Options.FallbackModel; _omitReasoning = false; continue; }
            }
            throw mapped;
        }

        using (resp)
        {
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            bool gotFirst = false;
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(gotFirst ? ct : firstTokenCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException(ProviderErrorKind.Timeout, "No answer tokens received in time"); }
                catch (IOException ex) { throw new ProviderException(ProviderErrorKind.Network, "Stream interrupted: " + ex.Message, ex); }
                if (line == null) yield break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;
                var delta = ParseDelta(data);
                if (string.IsNullOrEmpty(delta)) continue;
                gotFirst = true;
                ApproxOutputChars += delta.Length;
                yield return delta;
            }
        }
    }

    internal static string? ParseDelta(string data)
    {
        try
        {
            var node = JsonNode.Parse(data);
            return node?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal static ProviderException MapError(HttpStatusCode code, string body)
    {
        string message = body;
        try { message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body; } catch (JsonException) { }
        if (message.Length > 300) message = message[..300];
        return code switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProviderException(ProviderErrorKind.InvalidApiKey, "API key rejected: " + message),
            HttpStatusCode.TooManyRequests => new ProviderException(ProviderErrorKind.RateLimited, "Rate limited: " + message),
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound => new ProviderException(ProviderErrorKind.BadRequest, "Request rejected: " + message),
            _ when (int)code >= 500 => new ProviderException(ProviderErrorKind.ServerError, $"Server error {(int)code}"),
            _ => new ProviderException(ProviderErrorKind.Unknown, $"HTTP {(int)code}: {message}"),
        };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception) { return ""; } // error body is diagnostic only
    }

    public void Dispose() => _http.Dispose();
}
