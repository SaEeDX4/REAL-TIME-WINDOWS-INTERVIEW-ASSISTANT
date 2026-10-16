using System.Diagnostics;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.App.Services;

/// <summary>Minimal round-trip to the answer model to validate key + measure first-token latency.</summary>
public static class AnswerServiceProbe
{
    public sealed record Result(bool Ok, long FirstTokenMs, string? Error);

    public static async Task<Result> RunAsync(Func<string?> key, string model)
    {
        using var p = new OpenAiChatAnswerProvider(key, new ChatProviderOptions { Model = string.IsNullOrWhiteSpace(model) ? "gpt-5.4-mini" : model });
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await foreach (var _ in p.StreamAsync(new[] { new ChatMessage("user", "Reply with the single word: ready") }, 5, cts.Token))
                return new Result(true, sw.ElapsedMilliseconds, null);
            return new Result(false, -1, Loc.T("set_key_no_reply"));
        }
        catch (ProviderException ex) { return new Result(false, -1, ex.Message); }
        catch (OperationCanceledException) { return new Result(false, -1, Loc.T("set_key_timeout")); }
    }
}
