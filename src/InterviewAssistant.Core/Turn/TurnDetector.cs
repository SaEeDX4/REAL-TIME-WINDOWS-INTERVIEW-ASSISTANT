using System.Text;
using System.Text.RegularExpressions;
using InterviewAssistant.Core.Diagnostics;
using InterviewAssistant.Core.Intelligence;

namespace InterviewAssistant.Core.Turn;

public enum TurnState { Idle, Listening, SpeechStarted, PossibleEnd, TurnSettling, QuestionFinalized }

public sealed record FinalizedTurn(string Text, long SpeechEndMs, long FinalizedMs, int SegmentCount);

/// <summary>
/// Adaptive end-of-turn detector. Server VAD splits speech into segments at short pauses; this class decides
/// when the interviewer's whole TURN is over, so multi-part questions separated by pauses become one question.
///
/// The settle delay is measured from the moment speech stopped (not from transcript arrival), so transcription
/// latency and settle time overlap. The delay adapts to how "complete" the text sounds:
///  - ends with "?" / clear question form          -> short   (QuestionCompleteMs)
///  - ends with conjunction / comma / preposition  -> long    (IncompleteMs)
///  - otherwise                                     -> medium  (DefaultMs)
/// Any new speech during settling cancels finalization and the turn keeps growing.
/// </summary>
public sealed class TurnDetector
{
    private readonly IClock _clock;
    private readonly List<(string ItemId, string Text)> _segments = new();
    private readonly Dictionary<string, string> _partials = new();
    private long _speechStoppedAt = -1, _lastSegmentAt = -1, _settleDeadline = -1;
    private int _awaitingSegments;

    public TurnState State { get; private set; } = TurnState.Idle;
    public double Sensitivity { get; set; } = 1.0; // >1 waits longer, <1 answers faster
    public int QuestionCompleteMs { get; set; } = 450;
    public int DefaultMs { get; set; } = 750;
    public int IncompleteMs { get; set; } = 1700;
    public int MissingTranscriptTimeoutMs { get; set; } = 4000;
    public int MaxSegmentsPerTurn { get; set; } = 40;

    public event Action<TurnState>? StateChanged;
    public event Action<string>? LiveTextChanged;
    public event Action<FinalizedTurn>? TurnFinalized;

    public TurnDetector(IClock clock) => _clock = clock;

    public void Start() { Reset(); SetState(TurnState.Listening); }
    public void Stop() { Reset(); SetState(TurnState.Idle); }

    public void Reset()
    {
        _segments.Clear(); _partials.Clear();
        _speechStoppedAt = _lastSegmentAt = _settleDeadline = -1;
        _awaitingSegments = 0;
    }

    public void OnSpeechStarted()
    {
        if (State == TurnState.Idle) return;
        _settleDeadline = -1; // interviewer resumed: cancel pending finalization
        _awaitingSegments++;
        SetState(TurnState.SpeechStarted);
    }

    public void OnSpeechStopped()
    {
        if (State == TurnState.Idle) return;
        _speechStoppedAt = _clock.NowMs;
        SetState(TurnState.PossibleEnd);
        ScheduleSettle();
    }

    public void OnPartial(string itemId, string delta)
    {
        if (State == TurnState.Idle) return;
        _partials[itemId] = _partials.GetValueOrDefault(itemId, "") + delta;
        LiveTextChanged?.Invoke(CurrentText());
    }

    public void OnSegmentCompleted(string itemId, string transcript)
    {
        if (State == TurnState.Idle) return;
        _partials.Remove(itemId);
        if (_awaitingSegments > 0) _awaitingSegments--;
        var text = transcript.Trim();
        var idx = _segments.FindIndex(s => s.ItemId == itemId);
        if (idx >= 0) _segments[idx] = (itemId, text); // revised transcript replaces, never duplicates
        else if (text.Length > 0) { _segments.Add((itemId, text)); if (_segments.Count > MaxSegmentsPerTurn) _segments.RemoveAt(0); }
        _lastSegmentAt = _clock.NowMs;
        LiveTextChanged?.Invoke(CurrentText());
        if (State is TurnState.PossibleEnd or TurnState.TurnSettling) ScheduleSettle();
    }

    /// <summary>Called periodically (~50 ms). Finalizes when the settle deadline passes.</summary>
    public void Tick()
    {
        var now = _clock.NowMs;
        if (State == TurnState.TurnSettling && _settleDeadline >= 0 && now >= _settleDeadline && _awaitingSegments == 0)
        {
            Finalize(now);
            return;
        }
        // Speech stopped but the transcript for the last segment never arrived: don't hang forever.
        if (State is TurnState.PossibleEnd or TurnState.TurnSettling && _speechStoppedAt >= 0 && now - _speechStoppedAt > MissingTranscriptTimeoutMs)
        {
            _awaitingSegments = 0;
            if (_segments.Count > 0 || _partials.Count > 0) Finalize(now);
            else { Reset(); SetState(TurnState.Listening); }
        }
    }

    private void ScheduleSettle()
    {
        if (_segments.Count == 0) return; // wait for text
        var text = CurrentText();
        var delay = (int)(AdaptiveDelayMs(text) * Sensitivity);
        var anchor = _speechStoppedAt >= 0 ? _speechStoppedAt : _clock.NowMs;
        _settleDeadline = Math.Max(anchor + delay, _lastSegmentAt);
        SetState(TurnState.TurnSettling);
    }

    private static readonly Regex TrailingIncomplete = new(@"(\b(and|or|but|so|because|then|also|plus|the|a|an|to|of|with|about|for|in|on|like|which|that|if|how|what|your|my|our|is|are|would|could|um|uh|specifically)|,|…|-)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuestionStart = new(@"^(how|what|why|when|where|which|who|can|could|would|will|do|does|did|have|has|is|are|should|tell|describe|walk|explain|give)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public int AdaptiveDelayMs(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return DefaultMs;
        if (TrailingIncomplete.IsMatch(t)) return IncompleteMs;
        var lastSentence = Regex.Split(t, @"(?<=[.?!])\s+").LastOrDefault() ?? t;
        if (t.EndsWith('?')) return QuestionCompleteMs;
        if (QuestionStart.IsMatch(lastSentence) && t.EndsWith('.')) return (QuestionCompleteMs + DefaultMs) / 2;
        return DefaultMs;
    }

    private static readonly Regex Pleasantry = new(@"^(ok(ay)?|great|thanks?|thank you|perfect|nice|good|right|sure|yes|yeah|no|mm-?hmm|alright|cool|interesting|i see|got it|wonderful|excellent|very good)[\s,.!]*((thanks?|thank you|great|perfect|okay)[\s,.!]*)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True for short acknowledgements that should never trigger an answer.</summary>
    public static bool IsNonQuestion(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return true;
        if (Pleasantry.IsMatch(t)) return true;
        return TextNormalizer.WordCount(t) < 2;
    }

    private void Finalize(long now)
    {
        var text = TextNormalizer.CleanTranscript(CurrentText());
        var speechEnd = _speechStoppedAt;
        var count = _segments.Count;
        Reset();
        if (IsNonQuestion(text)) { SetState(TurnState.Listening); return; }
        SetState(TurnState.QuestionFinalized);
        TurnFinalized?.Invoke(new FinalizedTurn(text, speechEnd, now, count));
        SetState(TurnState.Listening);
    }

    public string CurrentText()
    {
        var sb = new StringBuilder();
        foreach (var s in _segments) { if (sb.Length > 0) sb.Append(' '); sb.Append(s.Text); }
        foreach (var p in _partials.Values) { if (p.Length == 0) continue; if (sb.Length > 0) sb.Append(' '); sb.Append(p.Trim()); }
        return sb.ToString();
    }

    private void SetState(TurnState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }
}
