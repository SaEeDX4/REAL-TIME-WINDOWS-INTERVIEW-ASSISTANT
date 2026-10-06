using System.Collections.Concurrent;

namespace InterviewAssistant.Core.Diagnostics;

public sealed record LatencySample(
    string Question, string Source,
    long SpeechEndToFinalizedMs, long MatchMs, long FinalizedToFirstTokenMs, long FinalizedToFirstBulletMs,
    long FinalizedToCompleteMs, long SpeechEndToFirstBulletMs, DateTime At);

/// <summary>Thread-safe counters + bounded latency history for the diagnostics panel and soak tests.</summary>
public sealed class SessionMetrics
{
    private readonly ConcurrentQueue<LatencySample> _samples = new();
    public int Capacity { get; init; } = 200;
    private int _questions, _cacheAnswers, _llmAnswers, _fallbackAnswers, _apiErrors, _duplicates, _reconnects, _llmRequests;
    public long AudioMsSent;

    public int Questions => _questions;
    public int CacheAnswers => _cacheAnswers;
    public int LlmAnswers => _llmAnswers;
    public int FallbackAnswers => _fallbackAnswers;
    public int ApiErrors => _apiErrors;
    public int DuplicatesSuppressed => _duplicates;
    public int Reconnects => _reconnects;
    public int LlmRequests => _llmRequests;
    public long ApproxLlmInputChars, ApproxLlmOutputChars;

    public void Add(LatencySample s)
    {
        _samples.Enqueue(s);
        while (_samples.Count > Capacity && _samples.TryDequeue(out _)) { }
    }
    public IReadOnlyList<LatencySample> Samples => _samples.ToArray();
    public void IncQuestions() => Interlocked.Increment(ref _questions);
    public void IncCache() => Interlocked.Increment(ref _cacheAnswers);
    public void IncLlm() => Interlocked.Increment(ref _llmAnswers);
    public void IncFallback() => Interlocked.Increment(ref _fallbackAnswers);
    public void IncApiError() => Interlocked.Increment(ref _apiErrors);
    public void IncDuplicate() => Interlocked.Increment(ref _duplicates);
    public void IncReconnect() => Interlocked.Increment(ref _reconnects);
    public void IncLlmRequest() => Interlocked.Increment(ref _llmRequests);

    /// <summary>Approximate session cost in USD from configurable unit prices (estimate only).</summary>
    public double EstimatedCostUsd(double transcribePerMinute, double inputPerMTok, double outputPerMTok) =>
        AudioMsSent / 60000.0 * transcribePerMinute + ApproxLlmInputChars / 4.0 / 1e6 * inputPerMTok + ApproxLlmOutputChars / 4.0 / 1e6 * outputPerMTok;

    public static long Percentile(IEnumerable<long> values, double p)
    {
        var arr = values.Where(v => v >= 0).OrderBy(v => v).ToArray();
        if (arr.Length == 0) return -1;
        var idx = (int)Math.Ceiling(p / 100.0 * arr.Length) - 1;
        return arr[Math.Clamp(idx, 0, arr.Length - 1)];
    }
}
