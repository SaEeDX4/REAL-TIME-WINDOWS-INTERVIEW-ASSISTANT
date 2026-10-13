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
    public static string UserMessage(ProviderErrorKind kind, bool developerKeyMode = false) => kind switch
    {
        ProviderErrorKind.InvalidApiKey => developerKeyMode ? "API key rejected — open Settings" : "Please sign in again (Home → Account).",
        ProviderErrorKind.NotSignedIn => "Please sign in again (Home → Account).",
        ProviderErrorKind.SessionEnded => "Your live session has ended. Press Resume.",
        ProviderErrorKind.QuotaExceeded => "Daily answer limit reached. Prepared answers still work.",
        ProviderErrorKind.PlanRequired => "AI answers need an active plan. Upgrade in Home → Account. Prepared answers still work.",
        ProviderErrorKind.DeviceRevoked => "This device is no longer registered. Sign in again in Home → Account.",
        ProviderErrorKind.AccountDisabled => "This account is disabled. Contact support.",
        ProviderErrorKind.UpdateRequired => "Please update the app to keep using AI answers.",
        ProviderErrorKind.RateLimited => "Too many requests right now — wait a few seconds and press Regenerate.",
        ProviderErrorKind.Network => "No connection to the service. Prepared answers still work.",
        ProviderErrorKind.Timeout => "The AI took too long to respond — press Regenerate.",
        ProviderErrorKind.ServiceUnavailable or ProviderErrorKind.ServerError => "Service temporarily unavailable. Prepared answers still work.",
        ProviderErrorKind.BadRequest => "This question could not be sent to the AI — try rephrasing it.",
        ProviderErrorKind.Cancelled => "Cancelled.",
        _ => "AI answer unavailable right now — press Regenerate.",
    };
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
