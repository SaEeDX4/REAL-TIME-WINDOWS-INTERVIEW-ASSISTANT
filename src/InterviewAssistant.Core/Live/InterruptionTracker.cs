namespace InterviewAssistant.Core.Live;

public enum AdaptiveLevel { Normal, Concise, Rapid }

/// <summary>
/// Heuristic, NOT certainty: without the candidate microphone we cannot know whether the interviewer interrupted
/// the candidate or simply asked the next question quickly. We estimate an <see cref="InterruptionConfidence"/> from
/// how soon interviewer speech resumed relative to how long the shown answer takes to say, plus repeated short turns.
/// First likely interruption → Concise; repeated within 5 minutes → Rapid; three calm answers step back down.
/// </summary>
public sealed class InterruptionTracker
{
    public const double SpeakingWordsPerSecond = 2.4; // ~145 wpm professional speech
    public AdaptiveLevel Level { get; private set; } = AdaptiveLevel.Normal;
    public bool ManuallyLocked { get; private set; }
    private long _answerShownMs = -1;
    private int _answerWords;
    private int _answerIndex;
    private int _calmAnswers;
    private readonly List<long> _recentEventsMs = new();
    public event Action<AdaptiveLevel>? LevelChanged;

    public void AnswerShown(long nowMs, int words, int questionIndex)
    {
        _answerShownMs = nowMs; _answerWords = words; _answerIndex = questionIndex;
    }

    /// <summary>Called when interviewer speech starts. Returns an event when it looks like an interruption/fast follow.</summary>
    public InterruptionEvent? InterviewerSpeechStarted(long nowMs)
    {
        if (_answerShownMs < 0 || _answerWords == 0) return null;
        var elapsed = nowMs - _answerShownMs;
        var expected = (long)(_answerWords / SpeakingWordsPerSecond * 1000) + 1500; // + time to start speaking
        _answerShownMs = -1; // one evaluation per answer
        if (elapsed >= expected * 0.85) { Calm(); return null; }
        var confidence = Math.Clamp(1.0 - (double)elapsed / expected, 0, 1);
        _recentEventsMs.RemoveAll(t => nowMs - t > 5 * 60_000);
        if (_recentEventsMs.Count > 0) confidence = Math.Min(1, confidence + 0.15); // repeated short-turn pattern
        if (confidence < 0.35) { Calm(); return null; }
        _recentEventsMs.Add(nowMs);
        _calmAnswers = 0;
        if (!ManuallyLocked)
        {
            var next = _recentEventsMs.Count >= 2 ? AdaptiveLevel.Rapid : AdaptiveLevel.Concise;
            if (next > Level) SetLevel(next);
        }
        return new InterruptionEvent
        {
            AfterQuestionIndex = _answerIndex, Confidence = Math.Round(confidence, 2), ElapsedMs = elapsed, ExpectedMs = expected,
            Note = confidence >= 0.7 ? "likely interruption" : "fast follow-up (uncertain)",
        };
    }

    private void Calm()
    {
        if (ManuallyLocked || Level == AdaptiveLevel.Normal) return;
        if (++_calmAnswers >= 3) { _calmAnswers = 0; SetLevel(Level - 1); }
    }

    public void Reset() { ManuallyLocked = false; _recentEventsMs.Clear(); _calmAnswers = 0; SetLevel(AdaptiveLevel.Normal); }
    public void Lock(AdaptiveLevel level) { ManuallyLocked = true; SetLevel(level); }

    private void SetLevel(AdaptiveLevel l) { if (l == Level) return; Level = l; LevelChanged?.Invoke(l); }
}
