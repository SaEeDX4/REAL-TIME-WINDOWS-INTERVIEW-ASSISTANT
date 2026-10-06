using System.Runtime.CompilerServices;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.Tests;

public sealed class FakeTranscriber : ITranscriber
{
    public event Action? SpeechStarted;
    public event Action? SpeechStopped;
    public event Action<string, string>? PartialTranscript;
    public event Action<string, string>? SegmentCompleted;
    public event Action<ConnectionStatus, string?>? StatusChanged;
    public ConnectionStatus Status { get; private set; }
    public long FramesReceived;
    private int _id;

    public Task StartAsync(CancellationToken ct) { Status = ConnectionStatus.Connected; StatusChanged?.Invoke(Status, null); return Task.CompletedTask; }
    public Task StopAsync() { Status = ConnectionStatus.Disconnected; return Task.CompletedTask; }
    public void SendAudio(byte[] pcm16Frame) => Interlocked.Increment(ref FramesReceived);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Start() => SpeechStarted?.Invoke();
    public void Stop() => SpeechStopped?.Invoke();
    public string Complete(string text, string? id = null) { id ??= "item" + (++_id); PartialTranscript?.Invoke(id, text[..Math.Min(5, text.Length)]); SegmentCompleted?.Invoke(id, text); return id; }
    public void Raise(ConnectionStatus s, string? d) { Status = s; StatusChanged?.Invoke(s, d); }
}

public sealed class FakeProvider : IAnswerProvider
{
    public string Name => "fake";
    public int Requests;
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();
    public Func<string, string[]> Responder { get; set; } = _ => new[] { "MODE: HYPOTHETICAL\n", "• I'd start by defining the goal ", "and the user problem clearly.\n", "• Then I'd prioritise by value, risk and effort with Engineering.\n", "• Finally I'd measure adoption and retention after release." };
    public ProviderException? Throw { get; set; }
    public int DelayMsPerDelta { get; set; }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref Requests);
        lock (Calls) Calls.Add(messages);
        if (Throw != null) throw Throw;
        foreach (var d in Responder(messages[^1].Content))
        {
            if (DelayMsPerDelta > 0) await Task.Delay(DelayMsPerDelta, ct);
            ct.ThrowIfCancellationRequested();
            yield return d;
        }
    }
}
