using System.Runtime.CompilerServices;
using InterviewAssistant.Contracts;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.Client;

/// <summary>IAnswerProvider backed by the server proxy: the model and the provider key live on the server.</summary>
public sealed class CloudAnswerProvider(BackendClient api, Func<Guid?> sessionId, Func<Guid?>? prepJobId = null) : IAnswerProvider
{
    public string Name => "Cloud";
    public string Purpose { get; init; } = "live";

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        var req = new AnswerStreamRequest(Purpose == "live" ? sessionId() : null, Purpose == "prep" ? prepJobId?.Invoke() : null,
            messages.Select(m => new AnswerMessage(m.Role, m.Content)).ToList(), maxTokens, Purpose);
        IAsyncEnumerator<string> e;
        try { e = api.StreamAnswerAsync(req, ct).GetAsyncEnumerator(ct); }
        catch (BackendException ex) { throw Map(ex); }
        await using (e)
        {
            while (true)
            {
                bool more;
                try { more = await e.MoveNextAsync(); }
                catch (BackendException ex) { throw Map(ex); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new ProviderException(ProviderErrorKind.Cancelled, "Cancelled"); }
                if (!more) yield break;
                yield return e.Current;
            }
        }
    }

    private static ProviderException Map(BackendException ex) => new(ex.Status switch
    {
        401 => ProviderErrorKind.InvalidApiKey,
        429 => ProviderErrorKind.RateLimited,
        0 => ProviderErrorKind.Network,
        400 => ProviderErrorKind.BadRequest,
        >= 500 => ProviderErrorKind.ServerError,
        _ => ProviderErrorKind.Unknown,
    }, ex.Message + (ex.CorrelationId != null ? $" (ref {ex.CorrelationId[..8]})" : ""), ex);
}
