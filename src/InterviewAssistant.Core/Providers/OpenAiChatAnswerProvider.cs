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
    /// <summary>Fast non-reasoning model by default (low first-token latency). Configurable in Settings.</summary>
    public string Model { get; set; } = "gpt-4.1-mini";
    public double Temperature { get; set; } = 0.4;
    public TimeSpan FirstTokenTimeout { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>Reasoning effort sent only for reasoning-model families (gpt-5*, o*).</summary>
    public string ReasoningEffort { get; set; } = "minimal";
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
    public string Name => "OpenAI " + Options.Model;
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

    internal string BuildBody(IReadOnlyList<ChatMessage> messages, int maxTokens)
    {
        var body = new JsonObject
        {
            ["model"] = Options.Model,
            ["stream"] = true,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
        };
        if (IsReasoningModel(Options.Model))
        {
            body["reasoning_effort"] = Options.ReasoningEffort;
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

        var json = BuildBody(messages, maxTokens);
        ApproxInputChars += json.Length;
        RequestCount++;
        using var req = new HttpRequestMessage(HttpMethod.Post, Options.Endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var firstTokenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        firstTokenCts.CancelAfter(Options.FirstTokenTimeout);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, firstTokenCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException(ProviderErrorKind.Timeout, "Answer service timed out"); }
        catch (HttpRequestException ex) { throw new ProviderException(ProviderErrorKind.Network, "Network error: " + ex.Message, ex); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var err = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                throw MapError(resp.StatusCode, err);
            }
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
