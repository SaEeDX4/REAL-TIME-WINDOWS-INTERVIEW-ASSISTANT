using InterviewAssistant.Core.Intelligence;

namespace InterviewAssistant.Core.Providers;

public enum ConnectionStatus { Disconnected, Connecting, Connected, Reconnecting, Failed }

/// <summary>
/// Why an answer could not be generated. Service-specific kinds keep the server's meaning so the user sees an
/// actionable message (never "Unknown" for a known condition).
/// </summary>
public enum ProviderErrorKind
{
    InvalidApiKey, RateLimited, Network, Timeout, ServerError, BadRequest, Cancelled, Unknown,
    /// <summary>Sign-in expired or missing (service).</summary>
    NotSignedIn,
    /// <summary>The live session ended (lease gone) — press Resume.</summary>
    SessionEnded,
    /// <summary>Daily answer limit / plan allowance reached.</summary>
    QuotaExceeded,
    /// <summary>Plan does not include this (trial used up, subscription ended or paused).</summary>
    PlanRequired,
    /// <summary>This PC was removed from the account.</summary>
    DeviceRevoked,
    AccountDisabled,
    /// <summary>The AI service is temporarily unavailable or at capacity.</summary>
    ServiceUnavailable,
    /// <summary>The app version is too old for the service.</summary>
    UpdateRequired,
}

public sealed class ProviderException : Exception
{
    public ProviderErrorKind Kind { get; }
    /// <summary>Server error code (e.g. "no_active_session"), when the failure came from the service.</summary>
    public string? Code { get; init; }
    /// <summary>Server correlation id for support/diagnostics (never shown in the answer area).</summary>
    public string? CorrelationId { get; init; }
    public ProviderException(ProviderErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

    /// <summary>Short, actionable text for the answer area. Prepared answers are mentioned only when they really still work.</summary>
    /// <summary>Localized, actionable message for the current UI language (never "Unknown").</summary>
    public static string UserMessage(ProviderErrorKind kind, bool developerKeyMode = false) => Languages.Loc.T(kind switch
    {
        ProviderErrorKind.InvalidApiKey => developerKeyMode ? "err_auth_dev" : "err_auth",
        ProviderErrorKind.NotSignedIn => "err_not_signed_in",
        ProviderErrorKind.SessionEnded => "err_session_ended",
        ProviderErrorKind.QuotaExceeded => "err_quota",
        ProviderErrorKind.PlanRequired => "err_plan_required",
        ProviderErrorKind.DeviceRevoked => "err_device_revoked",
        ProviderErrorKind.AccountDisabled => "err_account_disabled",
        ProviderErrorKind.UpdateRequired => "err_update_required",
        ProviderErrorKind.RateLimited => "err_rate_limited",
        ProviderErrorKind.Network => "err_network",
        ProviderErrorKind.Timeout => "err_timeout",
        ProviderErrorKind.ServiceUnavailable or ProviderErrorKind.ServerError => "err_service_unavailable",
        ProviderErrorKind.BadRequest => "err_bad_request",
        ProviderErrorKind.Cancelled => "err_cancelled",
        _ => "err_unknown",
    });
}

/// <summary>Streaming speech-to-text. Events may be raised from a background thread.</summary>
public interface ITranscriber : IAsyncDisposable
{
    event Action? SpeechStarted;
    event Action? SpeechStopped;
    event Action<string, string>? PartialTranscript;   // itemId, delta
    event Action<string, string>? SegmentCompleted;    // itemId, transcript
    event Action<ConnectionStatus, string?>? StatusChanged;
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    /// <summary>Queues 24 kHz mono PCM16 audio. Never blocks; drops oldest audio under backpressure.</summary>
    void SendAudio(byte[] pcm16Frame);
    ConnectionStatus Status { get; }
}

/// <summary>Streaming answer generation. Yields raw text deltas.</summary>
public interface IAnswerProvider
{
    string Name { get; }
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct);
}
