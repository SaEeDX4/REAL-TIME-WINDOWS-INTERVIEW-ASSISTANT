using System.Text;
using System.Text.RegularExpressions;
using InterviewAssistant.Core.Diagnostics;
using InterviewAssistant.Core.Intelligence;

namespace InterviewAssistant.Core.Turn;

public enum TurnState { Idle, Listening, SpeechStarted, PossibleEnd, TurnSettling, QuestionFinalized }

/// <summary>
/// A finalized interviewer turn. <see cref="RevisesTurnId"/> is set when the interviewer continued the previous
/// question after a pause (multi-part question) or restated it: the consumer must replace that turn's answer
/// instead of creating a new one.
/// </summary>
public sealed record FinalizedTurn(string Text, long SpeechEndMs, long FinalizedMs, int SegmentCount, int TurnId = 0, int? RevisesTurnId = null);

/// <summary>
/// Adaptive end-of-turn detector. Server VAD splits speech into segments at short pauses; this class decides
/// when the interviewer's whole TURN is over, so multi-part questions separated by pauses become one question.
///
/// Two layers keep latency low without splitting multi-part questions:
///  1. Adaptive settle, measured from the moment speech stopped (so transcription latency and settle overlap):
///     a complete-sounding question settles quickly, an open ending ("…and", "…which") or an announced multi-part
///     question ("I have two questions…") waits longer. New speech during settling cancels finalization.
///  2. Continuation window: the server's speech_stopped event already lags real silence by the VAD window, and
///     a resumed speech_started arrives a few hundred ms after the interviewer resumes, so for pauses of ~0.6–1.5 s
///     the first part may already be finalized. In a real interview the CANDIDATE speaks after a question, so
///     interviewer speech that resumes within <see cref="ContinuationWindowMs"/> is a continuation (or a restatement)
///     of the same question: it is merged into that turn and emitted with <see cref="FinalizedTurn.RevisesTurnId"/>.
///     Pleasantries ("okay", "great") never reopen a turn.
/// </summary>
public sealed class TurnDetector
{
    private readonly IClock _clock;
    private readonly List<(string ItemId, string Text)> _segments = new();
    private readonly Dictionary<string, string> _partials = new();
    private long _speechStoppedAt = -1, _lastSegmentAt = -1, _settleDeadline = -1;
    private int _awaitingSegments;
    private int _nextTurnId;
    private (int Id, string Text, long SpeechStoppedAt)? _lastFinal;
    private (int Id, string Text)? _reopened;

    public TurnState State { get; private set; } = TurnState.Idle;
    public IReadOnlyList<Knowledge.TermAlias>? Aliases { get; set; }
    public double Sensitivity { get; set; } = 1.0; // >1 waits longer, <1 answers faster
    public int QuestionCompleteMs { get; set; } = 600;
    public int DefaultMs { get; set; } = 800;
    public int IncompleteMs { get; set; } = 1700;
    public int MissingTranscriptTimeoutMs { get; set; } = 4000;
    public int MaxSegmentsPerTurn { get; set; } = 40;
    /// <summary>Interviewer speech resuming within this time after the previous turn's speech stopped continues that turn.</summary>
    public int ContinuationWindowMs { get; set; } = 2500;

    public event Action<TurnState>? StateChanged;
    public event Action<string>? LiveTextChanged;
    public event Action<FinalizedTurn>? TurnFinalized;

    public TurnDetector(IClock clock) => _clock = clock;

    public void Start() { Reset(); _lastFinal = null; SetState(TurnState.Listening); }
    public void Stop() { Reset(); _lastFinal = null; SetState(TurnState.Idle); }

    public void Reset()
    {
        _segments.Clear(); _partials.Clear();
        _speechStoppedAt = _lastSegmentAt = _settleDeadline = -1;
        _awaitingSegments = 0;
        _reopened = null;
    }

    /// <summary>Interviewer speech started. Returns true when it continues the just-finalized question (not an interruption).</summary>
    public bool OnSpeechStarted()
    {
        if (State == TurnState.Idle) return false;
        _settleDeadline = -1; // interviewer resumed: cancel pending finalization
        _awaitingSegments++;
        bool continuation = false;
        if (_segments.Count == 0 && _partials.Count == 0 && _reopened == null && _lastFinal is { } last && _clock.NowMs - last.SpeechStoppedAt <= ContinuationWindowMs)
        {
            _reopened = (last.Id, last.Text);
            continuation = true;
        }
        SetState(TurnState.SpeechStarted);
        return continuation;
    }

    /// <summary>True while a just-finalized question can still be continued by the interviewer.</summary>
    public bool InContinuationWindow => _lastFinal is { } l && _clock.NowMs - l.SpeechStoppedAt <= ContinuationWindowMs;

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
        else if (text.Length > 0 && !(_segments.Count > 0 && SameText(_segments[^1].Text, text))) // a re-delivered segment is not new speech
        {
            _segments.Add((itemId, text));
            if (_segments.Count > MaxSegmentsPerTurn) _segments.RemoveAt(0);
        }
        _lastSegmentAt = _clock.NowMs;
        LiveTextChanged?.Invoke(CurrentText());
        if (State is TurnState.PossibleEnd or TurnState.TurnSettling) ScheduleSettle();
    }

    /// <summary>
    /// Transcription connection dropped: in-flight partials are lost, but completed segments are kept and finalized
    /// normally (a question spoken just before a reconnect is still answered).
    /// </summary>
    public void OnConnectionInterrupted()
    {
        _partials.Clear();
        _awaitingSegments = 0;
        if (_segments.Count == 0) { _reopened = null; if (State != TurnState.Idle) SetState(TurnState.Listening); return; }
        if (_speechStoppedAt < 0) _speechStoppedAt = _clock.NowMs;
        ScheduleSettle();
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

    private static readonly Regex TrailingIncomplete = new(@"(\b(and|or|but|so|because|then|also|plus|the|a|an|to|of|with|about|for|in|on|like|which|that|if|how|what|your|my|our|is|are|would|could|um|uh|specifically|as well as|in addition)|,|…|-)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuestionStart = new(@"^(how|what|why|when|where|which|who|can|could|would|will|do|does|did|have|has|is|are|should|tell|describe|walk|explain|give)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "I have two questions", "a couple of things", "first, …" announce more than one part.
    private static readonly Regex AnnouncedMultiPart = new(@"\b(two|three|couple of|a few) (questions|things|parts|points)\b|\btwo-part\b|\b(first(ly)?|to start)\s*,", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public int AdaptiveDelayMs(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return DefaultMs;
        if (TrailingIncomplete.IsMatch(t)) return IncompleteMs;
        if (AnnouncedMultiPart.IsMatch(t) && t.Count(c => c == '?') <= 1) return IncompleteMs;
        var lastSentence = Regex.Split(t, @"(?<=[.?!])\s+").LastOrDefault() ?? t;
        if (t.EndsWith('?')) return QuestionCompleteMs;
        if (QuestionStart.IsMatch(lastSentence) && t.EndsWith('.')) return (QuestionCompleteMs + DefaultMs) / 2;
        return DefaultMs;
    }

    private static readonly Regex Pleasantry = new(@"^(ok(ay)?|great|thanks?|thank you|perfect|nice|good|right|sure|yes|yeah|no|mm-?hmm|alright|cool|interesting|i see|got it|wonderful|excellent|very good)[\s,.!]*((thanks?|thank you|great|perfect|okay)[\s,.!]*)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RestartCue = new(@"^(sorry|let me (rephrase|put it|ask (it|that) (differently|another way))|i mean|rather|to rephrase|what i('m| am) asking is|actually,? let me)\b[\s,.:—-]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Sorry, let me rephrase: how would…" → "how would…" (cues may be chained).</summary>
    private static string StripRestartCues(string text)
    {
        var t = text.Trim();
        for (int i = 0; i < 3; i++)
        {
            var m = RestartCue.Match(t);
            if (!m.Success || m.Length == 0 || m.Length >= t.Length) break;
            t = t[m.Length..].TrimStart();
        }
        return t;
    }

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
        var text = TextNormalizer.CleanTranscript(CurrentText(), Aliases);
        var speechEnd = _speechStoppedAt;
        var count = _segments.Count;
        var reopened = _reopened;
        Reset();
        if (IsNonQuestion(text)) { SetState(TurnState.Listening); return; } // an acknowledgement never reopens or creates a turn

        int? revises = null;
        if (reopened is { } prev)
        {
            var restated = StripRestartCues(text);
            if (restated.Length < text.Length && restated.Length > 0)
            {
                text = char.ToUpperInvariant(restated[0]) + restated[1..];   // restated: the new wording replaces the old
            }
            else if (IsRestatement(prev.Text, text))
                text = TextNormalizer.WordCount(text) >= TextNormalizer.WordCount(prev.Text) ? text : prev.Text;
            else
                text = prev.Text.TrimEnd() + " " + text;            // continued: one multi-part question
            if (string.Equals(text, prev.Text, StringComparison.Ordinal)) { RememberFinal(prev.Id, text, speechEnd); SetState(TurnState.Listening); return; }
            revises = prev.Id;
        }

        var id = revises ?? ++_nextTurnId;
        RememberFinal(id, text, speechEnd);
        SetState(TurnState.QuestionFinalized);
        TurnFinalized?.Invoke(new FinalizedTurn(text, speechEnd, now, count, id, revises));
        SetState(TurnState.Listening);
    }

    private void RememberFinal(int id, string text, long speechEnd) => _lastFinal = (id, text, speechEnd >= 0 ? speechEnd : _clock.NowMs);

    private static bool SameText(string a, string b) => TextNormalizer.Fingerprint(a) == TextNormalizer.Fingerprint(b);

    /// <summary>The new segment repeats the previous question (mostly the same words) rather than adding a part.</summary>
    private static bool IsRestatement(string previous, string next)
    {
        var p = new HashSet<string>(TextNormalizer.Fingerprint(previous).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var n = TextNormalizer.Fingerprint(next).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (n.Length == 0 || p.Count == 0) return false;
        return n.Count(p.Contains) / (double)n.Length >= 0.8;
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
