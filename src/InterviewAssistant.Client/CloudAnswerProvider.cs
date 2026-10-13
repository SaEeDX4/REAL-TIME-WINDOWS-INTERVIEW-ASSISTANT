using System.Runtime.CompilerServices;
using InterviewAssistant.Contracts;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.Client;

/// <summary>
/// IAnswerProvider backed by the server proxy: the model and the provider key live on the server.
/// Purpose "auto" (default for the live window) uses the live lease while one is active and the lease-free
/// "manual" purpose otherwise — typed questions work while paused without opening (or billing) a live session.
/// </summary>
public sealed class CloudAnswerProvider(BackendClient api, Func<Guid?> sessionId, Func<Guid?>? prepJobId = null, Func<Guid?>? deviceId = null) : IAnswerProvider
{
    public string Name => "Cloud";
    /// <summary>"auto", "live", "manual" or "prep".</summary>
    public string Purpose { get; init; } = "auto";
    /// <summary>Purpose actually used by the most recent request (diagnostics/tests).</summary>
    public string? LastPurpose { get; private set; }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        var session = Purpose is "auto" or AnswerPurposes.Live ? sessionId() : null;
        var purpose = Purpose switch
        {
            AnswerPurposes.Prep => AnswerPurposes.Prep,
            AnswerPurposes.Manual => AnswerPurposes.Manual,
            AnswerPurposes.Live => AnswerPurposes.Live,
            _ => session != null ? AnswerPurposes.Live : AnswerPurposes.Manual,
        };
        var body = messages.Select(m => new AnswerMessage(m.Role, m.Content)).ToList();
        IAsyncEnumerator<string>? e = null;
        bool first;
        try
        {
            e = Open(purpose, session, body, maxTokens, ct);
            first = await MoveAsync(e, ct);
        }
        catch (BackendException ex) when (Purpose == "auto" && purpose == AnswerPurposes.Live && ex.Code == "no_active_session")
        {
            // The lease ended between our check and the request (pause, timeout): answer as a typed question instead.
            if (e != null) await e.DisposeAsync();
            purpose = AnswerPurposes.Manual;
            e = Open(purpose, null, body, maxTokens, ct);
            try { first = await MoveAsync(e, ct); }
            catch (BackendException ex2) { await e.DisposeAsync(); throw Map(ex2); }
        }
        catch (BackendException ex) { if (e != null) await e.DisposeAsync(); throw Map(ex); }
        LastPurpose = purpose;
        await using (e!)
        {
            if (!first) yield break;
            yield return e.Current;
            while (true)
            {
                bool more;
                try { more = await MoveAsync(e, ct); }
                catch (BackendException ex) { throw Map(ex); }
                if (!more) yield break;
                yield return e.Current;
            }
        }
    }

    private IAsyncEnumerator<string> Open(string purpose, Guid? session, List<AnswerMessage> body, int maxTokens, CancellationToken ct) =>
        api.StreamAnswerAsync(new AnswerStreamRequest(purpose == AnswerPurposes.Live ? session : null, purpose == AnswerPurposes.Prep ? prepJobId?.Invoke() : null,
            body, maxTokens, purpose, deviceId?.Invoke()), ct).GetAsyncEnumerator(ct);

    private static async Task<bool> MoveAsync(IAsyncEnumerator<string> e, CancellationToken ct)
    {
        try { return await e.MoveNextAsync(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new ProviderException(ProviderErrorKind.Cancelled, "Cancelled"); }
    }

    /// <summary>Server error → provider error kind. Known API codes keep their meaning; the code and correlation id travel along.</summary>
    public static ProviderException Map(BackendException ex)
    {
        var kind = ex.Code switch
        {
            "no_active_session" or "session_ended" => ProviderErrorKind.SessionEnded,
            "daily_cap" or "manual_daily_cap" or "prep_ai_limit" or "prep_limit" or "allowance_exhausted" => ProviderErrorKind.QuotaExceeded,
            "plan_required" => ProviderErrorKind.PlanRequired,
            "global_cap" or "ai_unavailable" or "ai_not_configured" or "maintenance" or "ai_interrupted" or "stream_truncated" => ProviderErrorKind.ServiceUnavailable,
            "device_revoked" or "device_unknown" or "device_limit" => ProviderErrorKind.DeviceRevoked,
            "account_disabled" or "account_deleted" => ProviderErrorKind.AccountDisabled,
            "client_update_required" => ProviderErrorKind.UpdateRequired,
            "signed_out" or "invalid_token" => ProviderErrorKind.NotSignedIn,
            "ai_rate_limited" or "rate_limited" => ProviderErrorKind.RateLimited,
            "invalid_messages" or "invalid_role" or "prompt_too_large" or "invalid_purpose" => ProviderErrorKind.BadRequest,
            _ => ex.Status switch
            {
                0 => ProviderErrorKind.Network,
                401 => ProviderErrorKind.NotSignedIn,
                402 => ProviderErrorKind.PlanRequired,
                400 or 413 => ProviderErrorKind.BadRequest,
                408 or 504 => ProviderErrorKind.Timeout,
                426 => ProviderErrorKind.UpdateRequired,
                429 => ProviderErrorKind.RateLimited,
                >= 500 => ProviderErrorKind.ServiceUnavailable,
                _ => ProviderErrorKind.Unknown,
            },
        };
        return new ProviderException(kind, ex.Message, ex) { Code = ex.Code, CorrelationId = ex.CorrelationId };
    }
}
