using InterviewAssistant.Core.Intelligence;

namespace InterviewAssistant.Core.Providers;

public enum ConnectionStatus { Disconnected, Connecting, Connected, Reconnecting, Failed }

public enum ProviderErrorKind { InvalidApiKey, RateLimited, Network, Timeout, ServerError, BadRequest, Cancelled, Unknown }

public sealed class ProviderException : Exception
{
    public ProviderErrorKind Kind { get; }
    public ProviderException(ProviderErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
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
