using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Providers;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

public interface IAnswerProviderFactory
{
    IAnswerProvider Create(string model, IReadOnlyList<string> fallbacks, int firstTokenTimeoutMs);
}

/// <summary>Uses the validated Core OpenAI streaming client with the SERVER key and server-chosen model.</summary>
public sealed class OpenAiAnswerProviderFactory : IAnswerProviderFactory
{
    private readonly IHttpMessageHandlerFactory _handlers;
    private readonly OpenAiOptions _opt;
    public OpenAiAnswerProviderFactory(IHttpMessageHandlerFactory handlers, IOptions<OpenAiOptions> opt) { _handlers = handlers; _opt = opt.Value; }

    public IAnswerProvider Create(string model, IReadOnlyList<string> fallbacks, int firstTokenTimeoutMs)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey)) throw new ApiException(503, "ai_not_configured", "The AI service is not configured on the server.");
        var key = _opt.ApiKey;
        return new OpenAiChatAnswerProvider(() => key, new ChatProviderOptions
        {
            Model = model, FallbackModel = fallbacks.FirstOrDefault() ?? "", FirstTokenTimeout = TimeSpan.FromMilliseconds(firstTokenTimeoutMs),
            Endpoint = _opt.BaseUrl.TrimEnd('/') + "/v1/chat/completions",
        }, new ProviderHandler(_handlers.CreateHandler("openai")));
    }

    /// <summary>Routes the Core client through the pooled "openai" handler chain (test-replaceable). Pooled handlers ignore Dispose.</summary>
    private sealed class ProviderHandler(HttpMessageHandler pooled) : DelegatingHandler(pooled);
}

public static class AnswerRequestGuard
{
    /// <summary>Validates and bounds a client-built prompt (role whitelist, size caps).</summary>
    public static IReadOnlyList<ChatMessage> Sanitize(AnswerStreamRequest req, int maxChars = 24_000)
    {
        if (req.Messages == null || req.Messages.Count is 0 or > 6) throw ApiException.BadRequest("invalid_messages", "1–6 messages required.");
        var total = 0;
        var list = new List<ChatMessage>();
        foreach (var m in req.Messages)
        {
            if (m.Role is not ("system" or "user" or "assistant")) throw ApiException.BadRequest("invalid_role", "Invalid message role.");
            total += m.Content?.Length ?? 0;
            list.Add(new ChatMessage(m.Role, m.Content ?? ""));
        }
        if (total > maxChars) throw ApiException.BadRequest("prompt_too_large", "Prompt is too large.");
        return list;
    }
}
