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
public sealed record FinalizedTurn(string Text, long SpeechEndMs, long FinalizedMs, int SegmentCount, int TurnId = 0, int? RevisesTurnId = null,
    Audio.SpeechEvidence Evidence = default, long SpeechStartMs = -1);

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
    private readonly Dictionary<string, System.Text.StringBuilder> _partials = new();
    private readonly Dictionary<string, bool> _partialTrusted = new();
    private readonly Dictionary<string, int> _partialCheckedAt = new();   // partial length at the last trust check
    private readonly Queue<Audio.SpeechEvidence> _pendingEvidence = new();
    // Evidence keyed by the transcription item it belongs to (exact); the queue is only a fallback when ids are unknown.
    private readonly Dictionary<string, Audio.SpeechEvidence> _evidenceByItem = new();
    private Audio.SpeechEvidence _turnEvidence;
    private long _turnStartMs = -1;
    private string? _segmentsText;   // cached join of completed segments (long monologues: no O(n²) rebuilding)
    private int _droppedSegments;
    private long _speechStoppedAt = -1, _lastSegmentAt = -1, _settleDeadline = -1;
    private int _awaitingSegments;
    private int _nextTurnId;
    private (int Id, string Text, long SpeechStoppedAt, Audio.SpeechEvidence Evidence)? _lastFinal;
    private (int Id, string Text, Audio.SpeechEvidence Evidence)? _reopened;
    private long _reopenGapMs = -1;   // speech_stopped of the reopened turn → speech_started of the new speech

    public TurnState State { get; private set; } = TurnState.Idle;
    public IReadOnlyList<Knowledge.TermAlias>? Aliases { get; set; }
    /// <summary>
    /// Segment filter: returns false for a segment that must not join the turn (recognizer artefact, prompt echo, no
    /// speech in its audio). Applied when the segment's transcript completes, with that segment's acoustic evidence.
    /// </summary>
    public Func<string, Audio.SpeechEvidence, bool>? SegmentFilter { get; set; }
    /// <summary>
    /// Trust boundary for in-progress (partial) transcripts: only partials this accepts are shown as live text. Raw
    /// partials of a beep ("Job interview") must never reach the screen; completed segments are judged by
    /// <see cref="SegmentFilter"/>. Null = show every partial.
    /// </summary>
    public Func<string, bool>? PartialFilter { get; set; }
    /// <summary>Acoustic evidence of the segments accepted into the current turn so far.</summary>
    public Audio.SpeechEvidence TurnEvidence => _turnEvidence;
    /// <summary>True when the live text holds trusted words (accepted segments or trusted partials).</summary>
    public bool HasTrustedText => _segments.Count > 0 || _partialTrusted.Values.Any(t => t);
    /// <summary>Target-scoped term normalization applied to finalized text (e.g. MiCA → MiCAR when the target uses MiCAR).</summary>
    public Func<string, string>? TermNormalizer { get; set; }
    public double Sensitivity { get; set; } = 1.0; // >1 waits longer, <1 answers faster
    public int QuestionCompleteMs { get; set; } = 600;
    public int DefaultMs { get; set; } = 800;
    public int IncompleteMs { get; set; } = 1700;
    public int MissingTranscriptTimeoutMs { get; set; } = 4000;
    public int MaxSegmentsPerTurn { get; set; } = 400;
    /// <summary>Bound on the text kept for one turn (a 5-minute monologue is ~5,000 characters). The beginning and the
    /// most recent speech are kept; the middle of an extremely long turn is elided.</summary>
    public int MaxTurnChars { get; set; } = 8000;
    /// <summary>Bound on one in-progress partial transcript (tail kept).</summary>
    public int MaxPartialChars { get; set; } = 4000;
    /// <summary>Interviewer speech resuming within this time after the previous turn's speech stopped continues that turn.</summary>
    public int ContinuationWindowMs { get; set; } = 2500;
    /// <summary>
    /// A standalone follow-up ("Tell me more.", "Why?") inside the continuation window is its own turn only when it starts at
    /// least this long after the previous turn's speech_stopped event (≈ 2 s of real silence once the VAD tail and the
    /// speech_started lag are added): time for the candidate to have started answering. Sooner, the interviewer is still
    /// asking — "What is your biggest weakness? … Explain." is one multi-part question, merged as in rc.4.
    /// </summary>
    public int FollowUpReplyAllowanceMs { get; set; } = 1800;

    public event Action<TurnState>? StateChanged;
    public event Action<string>? LiveTextChanged;
    public event Action<FinalizedTurn>? TurnFinalized;
    /// <summary>A standalone follow-up arrived within the continuation window and became its own turn (diagnostics).</summary>
    public event Action<FollowUpKind>? FollowUpSplit;

    public TurnDetector(IClock clock) => _clock = clock;

    public void Start() { Reset(); _lastFinal = null; SetState(TurnState.Listening); }
    public void Stop() { Reset(); _lastFinal = null; SetState(TurnState.Idle); }

    public void Reset()
    {
        _segments.Clear(); _partials.Clear(); _partialTrusted.Clear(); _partialCheckedAt.Clear(); _segmentsText = null; _droppedSegments = 0;
        _speechStoppedAt = _lastSegmentAt = _settleDeadline = -1;
        _awaitingSegments = 0;
        _reopened = null; _reopenGapMs = -1;
        _turnEvidence = default; _turnStartMs = -1;
        _pendingEvidence.Clear();
        _evidenceByItem.Clear();
    }

    /// <summary>Interviewer speech started. Returns true when it continues the just-finalized question (not an interruption).</summary>
    public bool OnSpeechStarted()
    {
        if (State == TurnState.Idle) return false;
        _settleDeadline = -1; // interviewer resumed: cancel pending finalization
        _awaitingSegments++;
        if (_turnStartMs < 0) _turnStartMs = _clock.NowMs;
        bool continuation = false;
        if (_segments.Count == 0 && _partials.Count == 0 && _reopened == null && _lastFinal is { } last && _clock.NowMs - last.SpeechStoppedAt <= ContinuationWindowMs)
        {
            _reopened = (last.Id, last.Text, last.Evidence);
            _reopenGapMs = _clock.NowMs - last.SpeechStoppedAt;
            continuation = true;
        }
        SetState(TurnState.SpeechStarted);
        return continuation;
    }

    /// <summary>
    /// The consumer rejected this turn (no speech, prompt echo, logistics, remark to someone else): later speech must
    /// not be merged into it — a real question right after a beep starts a fresh turn.
    /// </summary>
    public void Forget(int turnId)
    {
        if (_lastFinal is { } l && l.Id == turnId) _lastFinal = null;
        if (_reopened is { } r && r.Id == turnId) _reopened = null;
    }

    /// <summary>True while a just-finalized question can still be continued by the interviewer.</summary>
    public bool InContinuationWindow => _lastFinal is { } l && _clock.NowMs - l.SpeechStoppedAt <= ContinuationWindowMs;

    /// <param name="evidence">Acoustic evidence for the segment that just ended (attached to its transcript).</param>
    /// <param name="itemId">Transcription item the segment belongs to, when the provider reports it.</param>
    public void OnSpeechStopped(Audio.SpeechEvidence evidence = default, string? itemId = null)
    {
        if (State == TurnState.Idle) return;
        if (!string.IsNullOrEmpty(itemId))
        {
            _evidenceByItem[itemId] = evidence;
            if (_evidenceByItem.Count > 16) _evidenceByItem.Remove(_evidenceByItem.Keys.First());
        }
        else
        {
            _pendingEvidence.Enqueue(evidence);
            while (_pendingEvidence.Count > 8) _pendingEvidence.Dequeue();
        }
        _speechStoppedAt = _clock.NowMs;
        SetState(TurnState.PossibleEnd);
        ScheduleSettle();
    }

    public void OnPartial(string itemId, string delta)
    {
        if (State == TurnState.Idle) return;
        if (!_partials.TryGetValue(itemId, out var sb)) _partials[itemId] = sb = new System.Text.StringBuilder();
        sb.Append(delta);
        if (sb.Length > MaxPartialChars) sb.Remove(0, sb.Length - MaxPartialChars);
        // An untrusted or short partial is re-checked on every delta (a sentence that outgrows its audio loses its trust
        // at once); a long trusted one after every ~40 new characters (cheap for minute-long monologues).
        bool trusted = _partialTrusted.GetValueOrDefault(itemId);
        if (PartialFilter == null) trusted = true;
        else if (!trusted || sb.Length <= 240 || sb.Length - _partialCheckedAt.GetValueOrDefault(itemId) >= 40)
        {
            trusted = PartialFilter(sb.ToString());
            _partialCheckedAt[itemId] = sb.Length;
        }
        _partialTrusted[itemId] = trusted;
        LiveTextChanged?.Invoke(DisplayText());
    }

    public void OnSegmentCompleted(string itemId, string transcript)
    {
        if (State == TurnState.Idle) return;
        _partials.Remove(itemId);
        _partialTrusted.Remove(itemId);
        _partialCheckedAt.Remove(itemId);
        if (_awaitingSegments > 0) _awaitingSegments--;
        var text = transcript.Trim();
        var idx = _segments.FindIndex(s => s.ItemId == itemId);
        if (idx >= 0) { _segments[idx] = (itemId, text); _segmentsText = null; } // revised transcript replaces, never duplicates
        else
        {
            Audio.SpeechEvidence segEvidence;
            if (_evidenceByItem.Remove(itemId, out var byItem)) segEvidence = byItem;
            else segEvidence = _pendingEvidence.Count > 0 ? _pendingEvidence.Dequeue() : Audio.SpeechEvidence.Unheard;
            if (text.Length > 0 && SegmentFilter != null && !SegmentFilter(text, segEvidence))
            {
                // Dropped: the turn continues (or ends) as if this sound had not been transcribed.
                _lastSegmentAt = _clock.NowMs;
                LiveTextChanged?.Invoke(DisplayText());
                if (_segments.Count == 0 && _partials.Count == 0 && _awaitingSegments == 0 && State is TurnState.PossibleEnd or TurnState.TurnSettling)
                {
                    _speechStoppedAt = _settleDeadline = -1; _turnStartMs = -1; _turnEvidence = default; _reopened = null;
                    SetState(TurnState.Listening);
                }
                return;
            }
            _turnEvidence += segEvidence;
            if (text.Length > 0 && !(_segments.Count > 0 && SameText(_segments[^1].Text, text))) // a re-delivered segment is not new speech
            {
                _segments.Add((itemId, text));
                _segmentsText = null;
                Bound();
            }
        }
        _lastSegmentAt = _clock.NowMs;
        LiveTextChanged?.Invoke(DisplayText());
        if (State is TurnState.PossibleEnd or TurnState.TurnSettling) ScheduleSettle();
    }

    /// <summary>
    /// Transcription connection dropped: in-flight partials are lost, but completed segments are kept and finalized
    /// normally (a question spoken just before a reconnect is still answered).
    /// </summary>
    public void OnConnectionInterrupted()
    {
        _partials.Clear();
        _partialTrusted.Clear();
        _partialCheckedAt.Clear();
        _awaitingSegments = 0;
        // Transcripts of the dropped connection never arrive: their evidence must not shift onto later segments.
        _pendingEvidence.Clear();
        _evidenceByItem.Clear();
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
        if (AnnouncedMultiPart.IsMatch(t) && TextNormalizer.QuestionMarkCount(t) <= 1) return IncompleteMs;
        var lastSentence = Regex.Split(t, @"(?<=[.?!？؟])\s+|(?<=[。！？])").LastOrDefault(x => x.Length > 0) ?? t;
        if (TextNormalizer.EndsWithQuestionMark(t)) return QuestionCompleteMs;
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
        // Word counts are only meaningful for Latin script: a whole Chinese, Arabic or Persian question counts as < 2 "words".
        if (TextNormalizer.IsLatinScript(t) && TextNormalizer.WordCount(t) < 2)
        {
            // One word is a real follow-up when it is asked ("Metrics?", "Timeline?") or is an interrogative a recognizer
            // wrote with falling punctuation ("Why.", "How", "Really."); acknowledgements and fillers are not ("Okay?",
            // "Hm?", "Sorry?" — the last asks the interviewer to repeat, not the candidate to answer).
            var word = t.TrimEnd('?', '？', '؟', '.', '!', '…', ' ');
            if (word.Length == 0 || Pleasantry.IsMatch(word) || Filler.IsMatch(word)) return true;
            return !(TextNormalizer.EndsWithQuestionMark(t) || OneWordAsk.IsMatch(word) || Intelligence.FollowUpIntent.Classify(t) != Intelligence.FollowUpKind.None);
        }
        return false;
    }

    private static readonly Regex OneWordAsk = new(@"^(why|how|really|example|examples|which|meaning|explain|elaborate|when|where|who|what)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Filler = new(@"^(h+m+|m+h*m+|uh+|um+|huh|eh|ah+|oh+|sorry|pardon|hello|hi|hey|bye)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void Finalize(long now)
    {
        var text = TextNormalizer.CleanTranscript(CurrentText(), Aliases);
        if (TermNormalizer != null) text = TermNormalizer(text);
        var speechEnd = _speechStoppedAt;
        var count = _segments.Count;
        var reopened = _reopened;
        var reopenGap = _reopenGapMs;
        // Evidence for segments whose transcript never arrived still belongs to this turn.
        var evidence = _turnEvidence;
        while (_pendingEvidence.Count > 0) evidence += _pendingEvidence.Dequeue();
        foreach (var e in _evidenceByItem.Values) evidence += e;
        var startMs = _turnStartMs;
        Reset();
        if (IsNonQuestion(text)) { SetState(TurnState.Listening); return; } // an acknowledgement never reopens or creates a turn

        int? revises = null;
        // A short follow-up that points back at the answer ("Tell me more.", "Why?", "Why.", "Give me another example.") after
        // a complete question is a NEW ask once the candidate could have started answering — it starts at least
        // FollowUpReplyAllowanceMs after the previous speech stopped: merging it would replace the previous question's answer
        // instead of answering the follow-up. Said straight after the question ("What is your biggest weakness? … Explain."),
        // it is part of one multi-part question and merges, as in rc.4, as does a follow-up after an unfinished question
        // ("…and" + "go on"). Whether the previous answer was already on screen is deliberately not used: a prepared answer
        // appears at once, long before the candidate can have said anything.
        if (reopened is { } follow && FollowUpIntent.IsStandaloneFollowUp(text) && IsCompleteAsk(follow.Text) && reopenGap >= FollowUpReplyAllowanceMs)
        {
            reopened = null;
            FollowUpSplit?.Invoke(FollowUpIntent.Classify(text));
        }
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
            // The continued turn's words were spoken across both turns' audio: judge them against both.
            evidence = prev.Evidence + evidence;
            if (string.Equals(text, prev.Text, StringComparison.Ordinal)) { RememberFinal(prev.Id, text, speechEnd, evidence); SetState(TurnState.Listening); return; }
            revises = prev.Id;
        }

        var id = revises ?? ++_nextTurnId;
        RememberFinal(id, text, speechEnd, evidence);
        SetState(TurnState.QuestionFinalized);
        TurnFinalized?.Invoke(new FinalizedTurn(text, speechEnd, now, count, id, revises, evidence, startMs));
        SetState(TurnState.Listening);
    }

    /// <summary>The previous turn ended as a complete ask (terminal punctuation, no dangling conjunction or article).</summary>
    private static bool IsCompleteAsk(string text)
    {
        var t = text.TrimEnd();
        return t.Length > 0 && !TrailingIncomplete.IsMatch(t) && ".?!？؟。！".Contains(t[^1]);
    }

    private void RememberFinal(int id, string text, long speechEnd, Audio.SpeechEvidence evidence) =>
        _lastFinal = (id, text, speechEnd >= 0 ? speechEnd : _clock.NowMs, evidence);

    private static bool SameText(string a, string b) => TextNormalizer.Fingerprint(a) == TextNormalizer.Fingerprint(b);

    /// <summary>The new segment repeats the previous question (mostly the same words) rather than adding a part.</summary>
    private static bool IsRestatement(string previous, string next)
    {
        var p = new HashSet<string>(TextNormalizer.Fingerprint(previous).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var n = TextNormalizer.Fingerprint(next).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (n.Length == 0 || p.Count == 0) return false;
        return n.Count(p.Contains) / (double)n.Length >= 0.8;
    }

    /// <summary>Keeps a very long turn bounded: the opening segment (often the framing) and the most recent speech.</summary>
    private void Bound()
    {
        int total = _segments.Sum(s => s.Text.Length + 1);
        while ((total > MaxTurnChars || _segments.Count > MaxSegmentsPerTurn) && _segments.Count > 2)
        {
            total -= _segments[1].Text.Length + 1;
            _segments.RemoveAt(1);
            _droppedSegments++;
        }
    }

    public string CurrentText()
    {
        var segments = SegmentsText();
        if (_partials.Count == 0) return segments;
        var sb = new StringBuilder(segments);
        foreach (var p in _partials.Values) { if (p.Length == 0) continue; if (sb.Length > 0) sb.Append(' '); sb.Append(p.ToString().Trim()); }
        return sb.ToString();
    }

    private string SegmentsText()
    {
        if (_segmentsText == null)
        {
            var b = new StringBuilder();
            for (int i = 0; i < _segments.Count; i++)
            {
                if (b.Length > 0) b.Append(' ');
                if (i == 1 && _droppedSegments > 0) b.Append("… ");
                b.Append(_segments[i].Text);
            }
            _segmentsText = b.ToString();
        }
        return _segmentsText;
    }

    /// <summary>
    /// Longest live text a consumer shows (only its tail is ever displayed). <see cref="DisplayText"/> builds at most this
    /// much, so a minute-long partial does not allocate the whole transcript on every delta.
    /// </summary>
    public int DisplayTailChars { get; set; } = int.MaxValue;

    /// <summary>What may be shown as live text: accepted segments plus the partials that passed <see cref="PartialFilter"/>
    /// (the last <see cref="DisplayTailChars"/> characters of it).</summary>
    public string DisplayText()
    {
        var segments = SegmentsText();
        var parts = new List<string>();
        if (segments.Length > 0) parts.Add(segments);
        foreach (var (id, p) in _partials)
        {
            if (p.Length == 0 || !_partialTrusted.GetValueOrDefault(id, PartialFilter == null)) continue;
            int take = Math.Min(p.Length, DisplayTailChars);
            parts.Add((take < p.Length ? p.ToString(p.Length - take, take) : p.ToString()).Trim());
        }
        if (parts.Count == 1 && parts[0].Length <= DisplayTailChars) return parts[0];
        // Walk back from the newest part until the tail is full.
        var tail = new List<string>();
        int len = 0;
        for (int i = parts.Count - 1; i >= 0 && len < DisplayTailChars; i--)
        {
            var part = parts[i];
            int room = DisplayTailChars - len;
            tail.Add(part.Length > room ? part[^room..] : part);
            len += Math.Min(part.Length, room) + 1;
        }
        tail.Reverse();
        return string.Join(' ', tail);
    }

    /// <summary>Number of segments elided from the middle of the current turn (diagnostics).</summary>
    public int DroppedSegments => _droppedSegments;

    private void SetState(TurnState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }
}
