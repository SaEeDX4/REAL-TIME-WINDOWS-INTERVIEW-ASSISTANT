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
    private int _questions, _cacheAnswers, _llmAnswers, _fallbackAnswers, _apiErrors, _duplicates, _reconnects, _llmRequests, _merged;
    private int _nonSpeech, _promptEcho, _shortEvent, _invalid, _otherDirected, _logistics, _contextHeld, _weakSupport, _implausible, _contextHallucination, _variants, _guidance, _redundantDropped;
    public long AudioMsSent;

    public int Questions => _questions;
    public int CacheAnswers => _cacheAnswers;
    public int LlmAnswers => _llmAnswers;
    public int FallbackAnswers => _fallbackAnswers;
    public int ApiErrors => _apiErrors;
    public int DuplicatesSuppressed => _duplicates;
    public int Reconnects => _reconnects;
    public int LlmRequests => _llmRequests;
    /// <summary>Multi-part questions joined after a pause (one answer instead of two).</summary>
    public int TurnsMerged => _merged;
    public long ApproxLlmInputChars, ApproxLlmOutputChars;
    /// <summary>Turns whose audio contained no human speech (non_speech_suppressed).</summary>
    public int NonSpeechSuppressed => _nonSpeech;
    /// <summary>Recognizer reproduced its vocabulary prompt (prompt_echo_suppressed).</summary>
    public int PromptEchoSuppressed => _promptEcho;
    /// <summary>Beeps, chimes, clicks, screenshot sounds (short_acoustic_event_suppressed).</summary>
    public int ShortAcousticEventSuppressed => _shortEvent;
    /// <summary>Recognizer artefacts and other invalid turns (invalid_turn_suppressed).</summary>
    public int InvalidTurnSuppressed => _invalid;
    public int OtherDirectedSuppressed => _otherDirected;
    public int LogisticsSuppressed => _logistics;
    /// <summary>Context statements held for the next question (not answered on their own).</summary>
    public int ContextHeld => _contextHeld;
    /// <summary>Questions accepted with only weak acoustic support for their words (diagnostics; they had a real voice).</summary>
    public int WeakSupportAccepted => _weakSupport;
    /// <summary>Transcripts with far more words than the voiced audio could hold (implausible_transcript_suppressed).</summary>
    public int ImplausibleTranscriptSuppressed => _implausible;
    /// <summary>Interview-flavoured sentences built from the prompt vocabulary over too little speech (context_hallucination_suppressed).</summary>
    public int ContextHallucinationSuppressed => _contextHallucination;
    public int Variants => _variants;
    public int GuidanceAnswers => _guidance;
    public int RedundantBulletsDropped => _redundantDropped;
    public int TotalSuppressed => _nonSpeech + _promptEcho + _shortEvent + _invalid + _otherDirected + _logistics + _implausible + _contextHallucination;

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
    public void IncMerged() => Interlocked.Increment(ref _merged);
    public void IncNonSpeech() => Interlocked.Increment(ref _nonSpeech);
    public void IncPromptEcho() => Interlocked.Increment(ref _promptEcho);
    public void IncShortEvent() => Interlocked.Increment(ref _shortEvent);
    public void IncInvalid() => Interlocked.Increment(ref _invalid);
    public void IncOtherDirected() => Interlocked.Increment(ref _otherDirected);
    public void IncLogistics() => Interlocked.Increment(ref _logistics);
    public void IncContextHeld() => Interlocked.Increment(ref _contextHeld);
    public void IncWeakSupport() => Interlocked.Increment(ref _weakSupport);
    public void IncImplausible() => Interlocked.Increment(ref _implausible);
    public void IncContextHallucination() => Interlocked.Increment(ref _contextHallucination);
    public void IncVariant() => Interlocked.Increment(ref _variants);
    public void IncGuidance() => Interlocked.Increment(ref _guidance);
    public void IncRedundantDropped() => Interlocked.Increment(ref _redundantDropped);

    /// <summary>Suppression counters with their diagnostic names (no transcript content).</summary>
    public IReadOnlyDictionary<string, int> Suppression => new Dictionary<string, int>
    {
        ["non_speech_suppressed"] = _nonSpeech, ["prompt_echo_suppressed"] = _promptEcho, ["short_acoustic_event_suppressed"] = _shortEvent,
        ["invalid_turn_suppressed"] = _invalid, ["other_directed_suppressed"] = _otherDirected, ["logistics_suppressed"] = _logistics,
        ["implausible_transcript_suppressed"] = _implausible, ["context_hallucination_suppressed"] = _contextHallucination,
        ["context_held"] = _contextHeld, ["weak_acoustic_support_accepted"] = _weakSupport,
    };

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
