using InterviewAssistant.Core.Audio;
using InterviewAssistant.Core.Diagnostics;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Live;
using InterviewAssistant.Core.Providers;
using InterviewAssistant.Core.Turn;

namespace InterviewAssistant.Core.Orchestration;

/// <summary>
/// Orchestrates the live pipeline:
///   audio (+ acoustic speech evidence) → transcriber → TurnDetector → UtteranceGate (speech? echo? question? for whom?)
///   → QuestionExtractor (core ask + context) → classify + match → AnswerPlanner (local) → prepared answer OR one LLM
///   stream → FactValidator + AnswerQualityGate → stable bullets.
/// All events may fire on background threads; UI must marshal to its dispatcher.
/// One finalized question causes at most one LLM request (none on a high-confidence cache hit). Non-speech sounds,
/// prompt echoes, logistics and remarks addressed to someone else never reach the answer path. A continued question
/// replaces the SAME history entry; a variant reworks the SAME history entry it was requested for.
/// </summary>
public sealed class InterviewEngine : IAsyncDisposable
{
    private readonly KnowledgeBase _kb;
    private readonly ITranscriber? _transcriber;
    private readonly IAnswerProvider? _provider;
    private readonly IClock _clock;
    private readonly QuestionMatcher _matcher;
    private readonly ContextRetriever _retriever;
    private readonly PromptBuilder _prompts;
    private readonly FactValidator _validator;
    private readonly TurnDetector _turn;
    private readonly EvidenceIndex _evidence;
    private readonly UtteranceGate _gate;
    private readonly PendingContext _pending = new();
    private readonly SpeechEvidenceTimeline _timeline = new();
    private long _segmentStartedAt = -1;
    /// <summary>Start of the most recent segment's evidence window (kept after speech stops: partials arrive late).</summary>
    private long _partialWindowFrom = -1;
    private bool _inSegment;
    private readonly Dictionary<int, AnswerView> _turnAnswers = new();
    private TaskCompletionSource<bool>? _hold;
    private long _holdUntil = -1;
    private bool _holdExtended;
    private readonly DuplicateGuard _dupes = new();
    private readonly SessionMemory _session = new();
    private readonly InterruptionTracker _interrupt = new();
    private readonly List<AnswerView> _history = new();
    private readonly object _gate_ = new();
    private CancellationTokenSource? _answerCts, _variantCts;
    private CancellationTokenSource? _runCts;
    private Task? _tickLoop;
    private int _nextAnswerId;
    private long _lastAudioActivityMs;
    private bool _listening, _paused, _heardAudio, _noAudioWarned;
    private string? _liveText;
    private bool _liveDirty;
    private long _liveEmittedAt = long.MinValue / 2;

    public EngineOptions Options { get; }
    public SessionMetrics Metrics { get; } = new();
    public EngineStatus Status { get; private set; } = EngineStatus.Ready;
    public string? StatusDetail { get; private set; }
    public TurnDetector Turn => _turn;
    public QuestionMatcher Matcher => _matcher;
    public UtteranceGate Gate => _gate;
    /// <summary>The answer for the most recent QUESTION (a variant of an older item never changes it).</summary>
    public AnswerView? Current { get; private set; }
    public SessionMemory Memory => _session;
    public KnowledgeBase Knowledge => _kb;
    public AdaptiveLevel Adaptive => _interrupt.Level;
    /// <summary>Answer (3 bullets) or Coach (3 keywords + structure). Switchable live via hotkey.</summary>
    public Presentation Presentation { get; set; } = Presentation.Answer;
    /// <summary>null = use target setting ("same" follows the interviewer); otherwise a language code.</summary>
    public string? AnswerLanguageOverride { get; set; }
    public event Action<AdaptiveLevel>? AdaptiveChanged;
    public event Action<InterruptionEvent>? InterruptionDetected;
    /// <summary>A finalized turn that was not answered (kind + reason, never the transcript) — diagnostics only.</summary>
    public event Action<UtteranceVerdict>? TurnSuppressed;

    public Presentation TogglePresentation() => Presentation = Presentation == Presentation.Answer ? Presentation.Coach : Presentation.Answer;
    public void ResetAdaptive() => _interrupt.Reset();

    /// <summary>Restores memory after a crash/disconnect (same session).</summary>
    public void RestoreMemory(SessionMemory m)
    {
        _session.Questions.AddRange(m.Questions); _session.Interruptions.AddRange(m.Interruptions);
        foreach (var kv in m.StoryUse) _session.StoryUse[kv.Key] = kv.Value;
        foreach (var kv in m.StoryLastUsedAt) _session.StoryLastUsedAt[kv.Key] = kv.Value;
        foreach (var kv in m.MetricUse) _session.MetricUse[kv.Key] = kv.Value;
        foreach (var kv in m.TopicCounts) _session.TopicCounts[kv.Key] = kv.Value;
        _session.RollingSummary = m.RollingSummary;
    }

    /// <summary>Interviewer speech started (from transcriber, or tests): feeds the turn detector and the hold.</summary>
    public void OnInterviewerSpeechStarted()
    {
        bool continuation;
        lock (_gate_)
        {
            if (!_inSegment) { _segmentStartedAt = _clock.NowMs; _inSegment = true; _partialWindowFrom = _segmentStartedAt - Options.EvidenceLeadMs; }
            continuation = _turn.OnSpeechStarted();
            // The interviewer is finishing the same question: keep a pending AI request on hold until the turn settles.
            if (continuation && _hold != null) _holdExtended = true;
        }
        // Interruption/pace is evaluated when the turn is accepted as a real question (a beep or a remark to a colleague
        // must never shorten the candidate's answers) — see HandleFinalizedAsync.
    }

    private void OnInterviewerSpeechStopped()
    {
        lock (_gate_)
        {
            var now = _clock.NowMs;
            // Without the segment's start (lost in a reconnect) its audio window is unknown: abstain, never judge the words
            // by a fragment of the audio.
            var evidence = _segmentStartedAt >= 0 ? _timeline.Sum(_segmentStartedAt - Options.EvidenceLeadMs, now) : SpeechEvidence.Unheard;
            _inSegment = false; _segmentStartedAt = -1;
            _turn.OnSpeechStopped(evidence, _transcriber?.LastStoppedItemId);
        }
    }

    public event Action<EngineStatus, string?>? StatusChanged;
    public event Action<string>? LiveTranscriptChanged;
    public event Action<AnswerView>? AnswerStarted;          // new answer card (question visible immediately)
    public event Action<AnswerView, string>? BulletAdded;    // append-only
    public event Action<AnswerView, string>? PendingTextChanged; // preview of the bullet being written
    public event Action<AnswerView>? AnswerCompleted;
    /// <summary>A variant was cancelled or produced nothing: (failed variant, original answer now back in its history slot).</summary>
    public event Action<AnswerView, AnswerView>? AnswerRestored;
    public event Action<LatencySample>? LatencyMeasured;
    public event Action<string>? Log;

    public InterviewEngine(KnowledgeBase kb, ITranscriber? transcriber, IAnswerProvider? provider, EngineOptions? options = null, IClock? clock = null)
    {
        _kb = kb;
        _transcriber = transcriber;
        _provider = provider;
        Options = options ?? new EngineOptions();
        _clock = clock ?? new SystemClock();
        _matcher = new QuestionMatcher(kb.Questions);
        _retriever = new ContextRetriever(kb);
        _prompts = new PromptBuilder(kb);
        _evidence = EvidenceIndex.For(kb);
        _validator = new FactValidator(kb.Profile, kb.Context, _evidence);
        var names = new[] { kb.Context.CandidateName, kb.Profile.Name, kb.Profile.PreferredName };
        var terms = kb.Context.TranscriptionTerms().Concat(kb.Context.Employers).Concat(kb.Profile.Experience.Select(e => e.Company));
        _gate = new UtteranceGate(new PromptEchoDetector(kb.Context.BuildTranscriptionPrompt(), kb.Context.TranscriptionTerms()), names, terms);
        _turn = new TurnDetector(_clock) { Aliases = kb.Context.Aliases, TermNormalizer = kb.Context.NormalizeTerms, SegmentFilter = KeepSegment, PartialFilter = TrustPartial };
        _turn.LiveTextChanged += t =>
        {
            _liveText = t; _liveDirty = true;
            // Words are only "being transcribed" once they are trusted; until then the status stays neutral.
            if (Status == EngineStatus.HearingAudio && _turn.HasTrustedText) SetStatus(EngineStatus.SpeechDetected);
        };
        _turn.StateChanged += OnTurnState;
        _turn.TurnFinalized += t => _ = HandleFinalizedAsync(t);
        _interrupt.LevelChanged += l => AdaptiveChanged?.Invoke(l);

        if (_transcriber != null)
        {
            _transcriber.SpeechStarted += OnInterviewerSpeechStarted;
            _transcriber.SpeechStopped += OnInterviewerSpeechStopped;
            _transcriber.PartialTranscript += (id, d) => { lock (_gate_) _turn.OnPartial(id, d); };
            _transcriber.SegmentCompleted += (id, t) => { lock (_gate_) _turn.OnSegmentCompleted(id, t); };
            _transcriber.StatusChanged += OnTranscriberStatus;
        }
    }

    // ---------------- lifecycle ----------------

    /// <summary>Starts or resumes listening. The interview session (memory, history, numbering) continues across pauses.</summary>
    public async Task StartListeningAsync()
    {
        if (_listening && !_paused) return;
        _paused = false;
        _listening = true;
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        lock (_gate_) { _turn.Start(); _inSegment = false; _segmentStartedAt = -1; }
        _timeline.Clear();
        _lastAudioActivityMs = _clock.NowMs;
        _heardAudio = false; _noAudioWarned = false;
        if (_transcriber != null) await _transcriber.StartAsync(_runCts.Token).ConfigureAwait(false);
        if (Options.AutoTick) _tickLoop = TickLoopAsync(_runCts.Token);
        SetStatus(EngineStatus.Listening);
    }

    /// <summary>Pause: stop sending audio and stop detecting turns. History and memory are kept; typed questions still work.</summary>
    public void Pause()
    {
        lock (_gate_) ReleaseHold(true); // a typed or pending question still gets its answer while paused
        _paused = true;
        lock (_gate_) _turn.Stop();
        _pending.Clear();
        _liveText = ""; _liveDirty = true; FlushLiveText(force: true);
        SetStatus(EngineStatus.Paused);
    }

    /// <summary>
    /// Closes the transcription connection while paused (a cloud pause ends the billable lease, and with it the relay);
    /// <see cref="StartListeningAsync"/> reconnects. History, memory and typed questions are unaffected.
    /// </summary>
    public async Task SuspendTranscriptionAsync()
    {
        if (_transcriber != null && _paused) await _transcriber.StopAsync().ConfigureAwait(false);
    }

    /// <summary>Reconnects transcription if its connection loop ended while listening (e.g. a replaced live lease).</summary>
    public async Task EnsureTranscriptionAsync()
    {
        if (_transcriber != null && IsListening && _runCts != null) await _transcriber.StartAsync(_runCts.Token).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        lock (_gate_) ReleaseHold(true);
        _listening = false; _paused = false;
        _runCts?.Cancel();
        CancelAnswer(includeVariants: true);
        lock (_gate_) _turn.Stop();
        _pending.Clear();
        if (_transcriber != null) await _transcriber.StopAsync().ConfigureAwait(false);
        if (_tickLoop != null) { try { await _tickLoop.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        _interrupt.Reset();   // pace adaptation belongs to one interview
        SetStatus(EngineStatus.Stopped);
    }

    public bool IsListening => _listening && !_paused;
    public bool IsPaused => _listening && _paused;

    /// <summary>Feed converted 24 kHz mono PCM16 frames from the capture layer (capture thread).</summary>
    public void OnAudioFrame(byte[] pcm16, double levelDb)
    {
        if (!IsListening) return;
        var now = _clock.NowMs;
        if (levelDb > -55) { _lastAudioActivityMs = now; _heardAudio = true; }
        _timeline.Add(now, AcousticAnalyzer.Analyze(pcm16));
        Interlocked.Add(ref Metrics.AudioMsSent, pcm16.Length / 48);
        _transcriber?.SendAudio(pcm16);
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Options.TickMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) Tick();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Advances time-based logic (turn settling, live text, no-audio warning). Public for deterministic tests.</summary>
    public void Tick()
    {
        lock (_gate_)
        {
            _turn.Tick();
            if (_hold != null && !_holdExtended && _clock.NowMs >= _holdUntil) ReleaseHold(true);
        }
        FlushLiveText(force: false);
        var silentMs = _clock.NowMs - _lastAudioActivityMs;
        if (!IsListening) return;
        // Silence after the interviewer finishes is normal (the candidate is talking). Warn only when NOTHING has been
        // heard since listening started — that usually means the meeting plays on a different device — and only once.
        if (!_heardAudio && !_noAudioWarned && Status is EngineStatus.Listening or EngineStatus.Quiet && silentMs > Options.NoAudioWarningSeconds * 1000L)
        {
            _noAudioWarned = true;
            SetStatus(EngineStatus.NoAudio, Loc.T("detail_no_audio"));
        }
        else if (Status == EngineStatus.Listening && silentMs > Options.QuietAfterSeconds * 1000L)
            SetStatus(EngineStatus.Quiet);
        else if (Status is EngineStatus.NoAudio or EngineStatus.Quiet && silentMs < 1000)
            SetStatus(EngineStatus.Listening);
    }

    /// <summary>Throttled live transcript: at most one update per <see cref="EngineOptions.LiveTextIntervalMs"/>, tail only.</summary>
    private void FlushLiveText(bool force)
    {
        if (!_liveDirty) return;
        var now = _clock.NowMs;
        if (!force && now - _liveEmittedAt < Options.LiveTextIntervalMs) return;
        _liveDirty = false;
        _liveEmittedAt = now;
        var t = _liveText ?? "";
        if (t.Length > Options.LiveTextMaxChars) t = "…" + t[^Options.LiveTextMaxChars..].TrimStart();
        LiveTranscriptChanged?.Invoke(t);
    }

    private void OnTurnState(TurnState s)
    {
        // A continuation that turned out to be "okay"/"mm-hmm" (or never transcribed) releases the held request.
        if (s == TurnState.Listening && _hold != null && _holdExtended) ReleaseHold(true);
        if (!IsListening || Status is EngineStatus.Reconnecting or EngineStatus.ApiError) return;
        if (Status == EngineStatus.Answering && s != TurnState.SpeechStarted) return;
        switch (s)
        {
            // Sound started: neutral until trusted words arrive (a beep is never shown as "transcribing").
            case TurnState.SpeechStarted: SetStatus(_turn.HasTrustedText ? EngineStatus.SpeechDetected : EngineStatus.HearingAudio); break;
            // "Question detected" only for text the gate would answer with the evidence heard so far.
            case TurnState.TurnSettling:
                if (_gate.Evaluate(_turn.CurrentText(), _turn.TurnEvidence).Answerable) SetStatus(EngineStatus.Finalizing);
                else if (_turn.HasTrustedText) SetStatus(EngineStatus.SpeechDetected);
                break;
            case TurnState.Listening: if (Status is EngineStatus.HearingAudio or EngineStatus.SpeechDetected or EngineStatus.Finalizing) SetStatus(EngineStatus.Listening); break;
        }
    }

    private void OnTranscriberStatus(ConnectionStatus s, string? detail)
    {
        switch (s)
        {
            case ConnectionStatus.Connected:
                if (IsListening) SetStatus(EngineStatus.Listening, detail);
                break;
            case ConnectionStatus.Reconnecting:
                Metrics.IncReconnect();
                SetStatus(EngineStatus.Reconnecting, detail);
                lock (_gate_) { _turn.OnConnectionInterrupted(); _inSegment = false; _segmentStartedAt = -1; } // keep completed segments
                break;
            case ConnectionStatus.Failed:
                Metrics.IncApiError();
                SetStatus(EngineStatus.ApiError, detail);
                break;
        }
        Log?.Invoke($"Transcriber: {s} {detail}");
    }

    // ---------------- question handling ----------------

    private async Task HandleFinalizedAsync(FinalizedTurn turn)
    {
        var now = _clock.NowMs;
        AnswerView? previous = null;
        if (turn.RevisesTurnId is int rid) lock (_turnAnswers) _turnAnswers.TryGetValue(rid, out previous);

        var verdict = _gate.Evaluate(turn.Text, turn.Evidence);
        if (!verdict.Answerable)
        {
            Count(verdict, turn);
            // A continuation that is not itself answerable keeps the earlier answer; context waits for the actual ask.
            if (previous == null && verdict.Kind == UtteranceKind.Context) _pending.Add(turn.TurnId, turn.Text, now);
            // Anything that is not interviewer context must never be merged into the next question.
            else if (previous == null) lock (_gate_) _turn.Forget(turn.TurnId);
            return;
        }
        if (verdict.Support == AcousticSupport.Weak) { Metrics.IncWeakSupport(); Log?.Invoke("Question accepted with weak acoustic support (" + turn.Evidence + ")"); }

        // Context statements merged into this (continued) turn are part of its text now.
        if (turn.RevisesTurnId is int merged && previous == null) _pending.Remove(merged);
        var question = QuestionExtractor.Extract(turn.Text, _pending.Take(now), _kb.Context, _gate);

        if (previous != null)
        {
            // Multi-part continuation / restatement: replace the provisional answer of the same question.
            Metrics.IncMerged();
            Log?.Invoke($"Question continued — answering the whole question ({TextNormalizer.WordCount(question.CoreQuestion)} words)");
            _dupes.Register(question.CoreQuestion, now);
        }
        else
        {
            if (_dupes.IsDuplicate(question.CoreQuestion, now)) { Metrics.IncDuplicate(); Log?.Invoke("Duplicate question suppressed"); return; }
            _dupes.Register(question.CoreQuestion, now);
            Metrics.IncQuestions();
            // Pace: evaluated only for real, accepted interviewer questions (from when the interviewer started speaking).
            var ev = _interrupt.InterviewerSpeechStarted(turn.SpeechStartMs >= 0 ? turn.SpeechStartMs : now);
            if (ev != null) { _session.Interruptions.Add(ev); InterruptionDetected?.Invoke(ev); }
        }
        await AnswerAsync(question, Options.DefaultStyle, allowCache: true, speechEndMs: turn.SpeechEndMs, finalizedMs: turn.FinalizedMs,
            kind: AnswerKind.Live, turnId: turn.TurnId, replaces: previous).ConfigureAwait(false);
    }

    /// <summary>
    /// Trust boundary for live partial transcripts (called on every partial delta): shown only when the sound heard for the
    /// current segment so far behaves like a voice that could have said the words, and the words are not the prompt or a
    /// prompt-flavoured sentence over little speech. With no audio analysed (tests without capture) partials are shown.
    /// </summary>
    private bool TrustPartial(string partial)
    {
        var now = _clock.NowMs;
        var from = _partialWindowFrom >= 0 ? _partialWindowFrom : now - Options.EvidenceLeadMs;
        var evidence = _timeline.Sum(from, now);
        var text = partial.Trim();
        if (text.Length == 0) return false;
        // Live words are shown only once the audio clearly behaves like a voice: a pitch track long enough to rule out a
        // held tone (buzz, note, instrument) and enough voiced sound for every syllable. Anything less waits for the end of
        // the turn, where the full evidence decides — a short real "Why?" simply appears when it is final.
        if (!evidence.PitchJudged) return false;
        if (SpeechPlausibility.Assess(text, evidence) != AcousticSupport.Strong) return false;
        // Prompt echoes are short reproductions of the prompt; long voiced partials are not worth the comparison cost.
        return TextNormalizer.WordCount(text) > 80 || !_gate.Echo.Check(text, evidence).IsEcho;
    }

    /// <summary>Drops artefact segments (beep echo, no speech) before they can merge with real speech in the same turn.</summary>
    private bool KeepSegment(string text, SpeechEvidence evidence)
    {
        var v = _gate.EvaluateSegment(TextNormalizer.CleanTranscript(text, _kb.Context.Aliases), evidence);
        if (v == null) return true;
        Count(v, TextNormalizer.WordCount(text));
        return false;
    }

    private void Count(UtteranceVerdict v, FinalizedTurn turn) => Count(v, TextNormalizer.WordCount(turn.Text));

    private void Count(UtteranceVerdict v, int words)
    {
        switch (v.Kind)
        {
            case UtteranceKind.NonSpeech: Metrics.IncNonSpeech(); break;
            case UtteranceKind.ShortAcousticEvent: Metrics.IncShortEvent(); break;
            case UtteranceKind.PromptEcho: Metrics.IncPromptEcho(); break;
            case UtteranceKind.Noise: Metrics.IncInvalid(); break;
            case UtteranceKind.OtherDirected: Metrics.IncOtherDirected(); break;
            case UtteranceKind.Logistics: Metrics.IncLogistics(); break;
            case UtteranceKind.Context: Metrics.IncContextHeld(); break;
            case UtteranceKind.ImplausibleTranscript: Metrics.IncImplausible(); break;
            case UtteranceKind.ContextHallucination: Metrics.IncContextHallucination(); break;
        }
        // Kind, reason and size only — the transcript itself is never logged.
        Log?.Invoke($"Turn not answered: {v.Kind} ({v.Reason}; {words} words)");
        TurnSuppressed?.Invoke(v);
    }

    /// <summary>Manual fallback: typed/pasted question. Works without audio or transcription, also while paused.</summary>
    public Task SubmitManualQuestionAsync(string text)
    {
        var q = _kb.Context.NormalizeTerms(TextNormalizer.CleanTranscript(text, _kb.Context.Aliases));
        if (q.Length == 0) return Task.CompletedTask;
        _dupes.Register(q, _clock.NowMs);
        Metrics.IncQuestions();
        // A typed question is exactly what the user wants answered: no gate, but the core ask is still extracted.
        var question = TextNormalizer.WordCount(q) > 40 ? QuestionExtractor.Extract(q, "", _kb.Context, _gate) : ExtractedQuestion.Plain(q);
        return AnswerAsync(question, Options.DefaultStyle, allowCache: true, speechEndMs: -1, finalizedMs: _clock.NowMs, kind: AnswerKind.Typed);
    }

    /// <summary>
    /// Reworks the answer of ONE specific history item (Shorter / Technical / Example / Full / Regenerate). The history
    /// entry, its numbering and the interview memory stay the same — a variant is not a new interviewer question.
    /// </summary>
    public Task RequestVariantAsync(int answerId, AnswerStyle style)
    {
        AnswerView? target;
        lock (_history) target = _history.FirstOrDefault(a => a.Id == answerId);
        if (target == null) return Task.CompletedTask;
        var question = target.Extracted ?? ExtractedQuestion.Plain(target.Question);
        Metrics.IncVariant();
        return AnswerAsync(question, style, allowCache: false, speechEndMs: -1, finalizedMs: _clock.NowMs, kind: AnswerKind.Variant, turnId: target.TurnId, replaces: target);
    }

    /// <summary>Variant of the most recent question (hotkeys/tests). The UI passes the displayed item explicitly.</summary>
    public Task RequestVariantAsync(AnswerStyle style) => Current == null ? Task.CompletedTask : RequestVariantAsync(Current.Id, style);

    public IReadOnlyList<AnswerView> History { get { lock (_history) return _history.ToList(); } }

    private void CancelAnswer(bool includeVariants)
    {
        var cts = Interlocked.Exchange(ref _answerCts, null);
        if (cts != null) { cts.Cancel(); cts.Dispose(); }
        if (!includeVariants) return;
        var v = Interlocked.Exchange(ref _variantCts, null);
        if (v != null) { v.Cancel(); v.Dispose(); }
    }

    private async Task AnswerAsync(ExtractedQuestion xq, AnswerStyle style, bool allowCache, long speechEndMs, long finalizedMs, AnswerKind kind, int turnId = 0, AnswerView? replaces = null)
    {
        bool isVariant = kind == AnswerKind.Variant;
        var question = xq.CoreQuestion;
        // A variant of an answer that was still streaming must still complete that question's memory (stories, metrics).
        bool replacesUnfinished = isVariant && replaces is { IsComplete: false };
        if (replaces != null) replaces.Superseded = true;
        CancellationTokenSource cts = new();
        if (isVariant)
        {
            // One variant at a time; a variant of the item that is still streaming replaces that stream.
            var old = Interlocked.Exchange(ref _variantCts, cts);
            if (old != null) { old.Cancel(); old.Dispose(); }
            if (replaces != null && ReferenceEquals(replaces, Current) && !replaces.IsComplete) CancelAnswer(includeVariants: false);
        }
        else
        {
            // A new question supersedes any in-flight generation, including variants of older items.
            CancelAnswer(includeVariants: true);
            var previous = Interlocked.Exchange(ref _answerCts, cts);
            if (previous != null) { try { previous.Cancel(); previous.Dispose(); } catch (ObjectDisposedException) { } }
        }
        var ct = cts.Token;

        var t0 = _clock.NowMs;
        var cls = QuestionClassifier.Classify(question, _kb.Context, _evidence);
        var match = _matcher.Match(question);
        var matchMs = _clock.NowMs - t0;

        // Language: detect per question (falls back to the locked interview language); memory is NOT reset on switches.
        var det = LanguageDetector.Detect(question);
        var detected = det.Confidence >= 0.5 ? det.Code : "und";
        var answerLang = LanguageRegistry.ResolveAnswerLanguage(AnswerLanguageOverride ?? _kb.Context.AnswerLanguage, _kb.Context.InterviewLanguage, detected);

        // Adaptive pace: shorten automatically after likely interruptions (default style, live questions only).
        var effective = style;
        if (style == AnswerStyle.Balanced && kind == AnswerKind.Live)
            effective = _interrupt.Level switch { AdaptiveLevel.Concise => AnswerStyle.Concise, AdaptiveLevel.Rapid => AnswerStyle.Rapid, _ => style };
        var presentation = isVariant ? Presentation.Answer : Presentation;

        QuestionRecord? record;
        string? prevStyle = null;
        if (isVariant)
        {
            record = replaces?.Record;   // same question: never a new record
            if (record != null) { prevStyle = record.Style; record.Style = style.ToString(); record.VariantCount++; }
        }
        else if (replaces?.Record is { } reused)
        {
            reused.Original = question; reused.Context = xq.ContextSummary; reused.DetectedLanguage = detected; reused.AnswerLanguage = answerLang;
            reused.Category = cls.Category; reused.Mode = cls.Mode.ToString(); reused.MatchedQuestionId = match.Question?.QuestionId;
            reused.AnswerBullets.Clear(); reused.StoryIds.Clear(); reused.QualityFlags.Clear();
            record = reused;
        }
        else
        {
            record = _session.Add(new QuestionRecord
            {
                Original = question, Context = xq.ContextSummary, Typed = kind == AnswerKind.Typed, DetectedLanguage = detected, AnswerLanguage = answerLang,
                Category = cls.Category, Mode = cls.Mode.ToString(),
                IntentKey = SessionMemory.IntentKeyFor(question, cls.Category, match.Confidence >= MatchConfidence.Medium ? match.Question : null),
                MatchedQuestionId = match.Question?.QuestionId, FollowUpOf = cls.IsFollowUp ? _session.Last?.Index : null,
                ReferencesEarlierAnswer = SessionMemory.ReferencesEarlier(question), Presentation = presentation.ToString(),
                Entities = xq.Entities.Count > 0 ? xq.Entities.ToList()
                    : new[] { _kb.Context.CompanyName }.Concat(_kb.Context.Products).Where(e => e.Length > 1 && question.Contains(e, StringComparison.OrdinalIgnoreCase)).ToList(),
            });
        }
        if (record != null && !isVariant) foreach (var e in record.Entities) _session.CompanyTopics.Add(e);
        if (record?.ReferencesEarlierAnswer == true && !isVariant) // "you already mentioned 60,000…" → that metric counts as used
            foreach (var m in Ingestion.ResumeParser.ExtractMetrics(question)) _session.MetricUse[m] = _session.MetricUse.GetValueOrDefault(m) + 1;

        var ctx = _retriever.Retrieve(question, cls, match, storyPenalty: _session.StoryPenalty);
        var plan = AnswerPlanner.Plan(xq, cls, match, ctx, _kb, _evidence);

        var view = new AnswerView
        {
            Id = Interlocked.Increment(ref _nextAnswerId), Question = question, Style = effective, Mode = cls.Mode, Category = cls.Category,
            MatchedQuestionId = match.Question?.QuestionId, MatchScore = match.Score, QuestionIndex = record?.Index ?? replaces?.QuestionIndex ?? _session.Last?.Index ?? 0,
            DetectedLanguage = detected, AnswerLanguage = answerLang, Presentation = presentation, Adaptive = _interrupt.Level,
            ReplacesAnswerId = replaces?.Id, TurnId = turnId, Record = record, Kind = kind,
            ContextSummary = xq.ContextSummary, OriginalText = xq.Original, SubQuestions = xq.SubQuestions, Extracted = xq,
            Plan = plan, Confidence = plan.Confidence,
        };
        if (!isVariant || ReferenceEquals(replaces, Current)) Current = view;
        lock (_history)
        {
            var at = replaces == null ? -1 : _history.IndexOf(replaces);
            if (at >= 0) _history[at] = view;   // same question → same history entry (continuation or variant)
            else { _history.Add(view); if (_history.Count > Options.HistoryCapacity) _history.RemoveAt(0); }
        }
        if (turnId > 0) lock (_turnAnswers) { _turnAnswers[turnId] = view; if (_turnAnswers.Count > 64) _turnAnswers.Remove(_turnAnswers.Keys.Min()); }
        AnswerStarted?.Invoke(view);
        SetStatus(EngineStatus.Answering);

        long firstToken = -1, firstBullet = -1;
        var bank = match.Question;
        bool languageOk = bank != null && LanguageRegistry.Find(bank.Language)?.Code == answerLang;
        bool repeatsStory = bank != null && bank.EffectiveStoryIds.Any(_session.RecentlyUsedStories(2).Contains);
        bool referencesEarlier = record?.ReferencesEarlierAnswer == true;
        bool personal = plan.Personal != PersonalTopic.None;
        bool useCache = allowCache && Options.UseFastCache && bank != null && bank.Confidence >= Options.MinCacheConfidence && languageOk && !personal &&
                        (match.Confidence == MatchConfidence.High && (!cls.IsFollowUp || match.Score >= 0.75)) &&
                        !(_provider != null && (repeatsStory || referencesEarlier)); // prefer a fresh answer instead of repeating a story
        // Without any LLM available, a Medium match is still far better than nothing.
        if (!useCache && allowCache && _provider == null && bank != null && match.Confidence >= MatchConfidence.Low && !personal) useCache = true;
        var request = new PromptRequest(
            LanguageRule: LanguageRegistry.AnswerLanguageRule(answerLang),
            MemoryContext: _session.BuildPromptContext(),
            ExtraInstruction: referencesEarlier ? "The interviewer refers to something already mentioned: do NOT repeat it — use a different verified story/angle or go deeper as asked." :
                              repeatsStory ? "Avoid re-using the stories already used unless no other evidence fits." : null,
            InterviewerContext: xq.PromptContext, SubQuestions: xq.SubQuestions, Constraints: xq.Constraints, Plan: plan, Boundaries: _kb.Profile.Boundaries);

        try
        {
            if (plan.PersonalWithoutData)
            {
                // Never invent hobbies, motivations or preferences: show honest guidance instantly (no model call).
                view.Source = AnswerSource.Guidance;
                foreach (var b in AnswerPlanner.PersonalGuidance(plan.Personal, answerLang)) { view.AppendBullet(b); BulletAdded?.Invoke(view, b); }
                firstBullet = firstToken = _clock.NowMs;
                Metrics.IncGuidance();
            }
            else if (presentation == Presentation.Coach)
            {
                firstBullet = await CoachAsync(view, question, cls, match, useCache, answerLang, ctx, ct).ConfigureAwait(false);
                firstToken = firstBullet;
            }
            else if (useCache)
            {
                EmitCached(view, bank!, _provider == null && match.Confidence < MatchConfidence.High ? AnswerSource.CacheFallback : AnswerSource.Cache);
                firstBullet = _clock.NowMs;
                Metrics.IncCache();
            }
            else if (_provider != null)
            {
                try
                {
                    if (kind == AnswerKind.Live && replaces == null && Options.ContinuationHoldMs > 0 && IsListening && !await HoldForContinuationAsync(ct).ConfigureAwait(false))
                        throw new OperationCanceledException(ct);
                    (firstToken, firstBullet) = await StreamLlmAsync(view, question, cls, ctx, effective, request, ct).ConfigureAwait(false);
                    Metrics.IncLlm();
                }
                catch (ProviderException ex) when (!ct.IsCancellationRequested)
                {
                    Metrics.IncApiError();
                    Log?.Invoke($"Answer provider error: {ex.Kind} {ex.Code} {ex.Message}");
                    view.Error = ex.Kind;
                    var message = ProviderException.UserMessage(ex.Kind, Options.DeveloperKeyMode);
                    // Fall back to a prepared answer only when it is a SAFE match (never a confidently wrong one).
                    if (view.Bullets.Count == 0 && bank != null && match.Confidence >= MatchConfidence.Medium && languageOk && !personal)
                    {
                        EmitCached(view, bank, AnswerSource.CacheFallback);
                        view.Note = Loc.T("note_prepared_fallback");
                        firstBullet = _clock.NowMs;
                        Metrics.IncFallback();
                    }
                    else
                    {
                        view.Source = view.Bullets.Count > 0 ? AnswerSource.Llm : AnswerSource.Error;
                        view.Note = message;
                    }
                    // Transient service errors don't turn the whole live status red; session/account problems do.
                    if (ex.Kind is not (ProviderErrorKind.RateLimited or ProviderErrorKind.Timeout or ProviderErrorKind.QuotaExceeded or ProviderErrorKind.BadRequest))
                        SetStatus(EngineStatus.ApiError, message);
                }
            }
            else if (personal && plan.PersonalFacts.Count > 0)
            {
                // No AI available: the user's own confirmed answer, verbatim.
                view.Source = AnswerSource.CacheFallback;
                foreach (var b in plan.PersonalFacts.Take(3)) { view.AppendBullet(b); BulletAdded?.Invoke(view, b); }
                firstBullet = _clock.NowMs;
            }
            else
            {
                view.Source = AnswerSource.Error;
                view.Note = Loc.T(Options.DeveloperKeyMode ? "err_no_provider_dev" : "err_no_provider");
            }
        }
        catch (OperationCanceledException) { view.Note ??= Loc.T("note_superseded"); }
        catch (ProviderException ex) when (ex.Kind == ProviderErrorKind.Cancelled || ct.IsCancellationRequested) { view.Note ??= Loc.T("note_superseded"); }
        finally
        {
            view.IsComplete = true;
            var bullets = view.SnapshotBullets();
            // A variant that was cancelled (next question, Stop, another variant) or produced nothing must not wipe the
            // answer it was reworking: the original goes back into its history slot and its question record is untouched.
            if (isVariant && replaces != null && (ct.IsCancellationRequested || bullets.Count == 0))
            {
                lock (_history) { var at = _history.IndexOf(view); if (at >= 0) _history[at] = replaces; }
                if (turnId > 0) lock (_turnAnswers) { if (_turnAnswers.TryGetValue(turnId, out var v) && ReferenceEquals(v, view)) _turnAnswers[turnId] = replaces; }
                if (ReferenceEquals(Current, view)) Current = replaces;
                replaces.Superseded = false;
                view.Superseded = true;
                if (record != null) { record.Style = prevStyle ?? record.Style; record.VariantCount = Math.Max(0, record.VariantCount - 1); }
                AnswerRestored?.Invoke(view, replaces);
            }
            if (!view.Superseded && bullets.Count > 0 && view.Source is AnswerSource.Llm)
            {
                var quality = AnswerQualityGate.Assess(xq, plan, bullets, RecentAnswers(view), view.Style);
                view.ValidationFlags.AddRange(quality.Flags);
            }
            if (record != null && !view.Superseded)
            {
                var metrics = bullets.SelectMany(Ingestion.ResumeParser.ExtractMetrics).Where(m => _kb.Profile.VerifiedNumbers.Contains(m)).Distinct();
                if (isVariant && !replacesUnfinished) { record.AnswerBullets = bullets.ToList(); }
                else
                {
                    _session.CompleteAnswer(record, bullets, view.StoryIds, metrics);
                    record.FirstBulletLatencyMs = firstBullet >= 0 ? firstBullet - finalizedMs : -1;
                }
                record.Source = view.Source.ToString();
                record.CoachKeywords = view.Coach?.Keywords.ToList() ?? new();
                record.Confidence = view.Confidence.ToString();
                record.QualityFlags = view.ValidationFlags.Where(f => f.StartsWith("QUALITY_") || f.StartsWith("DROPPED_") || f.StartsWith("BRIDGE_")).Select(f => f.Split(':')[0]).Distinct().ToList();
            }
            // Only answers to heard questions inform the pace heuristic (typed questions and variants never do).
            if (firstBullet >= 0 && kind == AnswerKind.Live && !view.Superseded)
            {
                var words = view.Coach != null ? 25 : bullets.Sum(TextNormalizer.WordCount);
                _interrupt.AnswerShown(firstBullet, words, view.QuestionIndex);
            }
            AnswerCompleted?.Invoke(view);
            // Atomic hand-back: only the owner of the slot clears it (a newer answer/variant may already hold it).
            if (isVariant) { if (ReferenceEquals(Interlocked.CompareExchange(ref _variantCts, null, cts), cts)) cts.Dispose(); }
            else if (ReferenceEquals(Interlocked.CompareExchange(ref _answerCts, null, cts), cts)) cts.Dispose();
            if (Status is EngineStatus.Answering && !view.Superseded)
                SetStatus(!IsListening ? (_paused ? EngineStatus.Paused : EngineStatus.Ready) : view.Bullets.Count > 0 ? EngineStatus.AnswerReady : EngineStatus.Listening);

            var done = _clock.NowMs;
            if (!view.Superseded && !isVariant)
            {
                var sample = new LatencySample(question, view.Source.ToString(),
                    speechEndMs >= 0 ? finalizedMs - speechEndMs : -1, matchMs,
                    firstToken >= 0 ? firstToken - finalizedMs : -1, firstBullet >= 0 ? firstBullet - finalizedMs : -1,
                    done - finalizedMs, speechEndMs >= 0 && firstBullet >= 0 ? firstBullet - speechEndMs : -1, DateTime.UtcNow);
                Metrics.Add(sample);
                LatencyMeasured?.Invoke(sample);
            }
        }
    }

    private IEnumerable<IReadOnlyList<string>> RecentAnswers(AnswerView exclude)
    {
        List<AnswerView> recent;
        lock (_history) recent = _history.Where(a => a.Id != exclude.Id && a.IsComplete && !a.Superseded).TakeLast(4).ToList();
        return recent.Select(a => a.SnapshotBullets());
    }

    /// <summary>Waits <see cref="EngineOptions.ContinuationHoldMs"/> (engine time, released by Tick). False when superseded.</summary>
    private Task<bool> HoldForContinuationAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool> tcs;
        lock (_gate_)
        {
            ReleaseHold(false);
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _hold = tcs; _holdUntil = _clock.NowMs + Options.ContinuationHoldMs; _holdExtended = false;
        }
        var reg = ct.Register(() => tcs.TrySetResult(false));
        return tcs.Task.ContinueWith(t => { reg.Dispose(); return t.Result; }, TaskScheduler.Default);
    }

    private void ReleaseHold(bool proceed)
    {
        var h = _hold;
        _hold = null; _holdExtended = false; _holdUntil = -1;
        h?.TrySetResult(proceed);
    }

    private void EmitCached(AnswerView view, BankQuestion q, AnswerSource source)
    {
        view.Source = source;
        view.Mode = q.Mode;
        view.StoryIds.AddRange(q.EffectiveStoryIds);
        var bullets = view.Style == AnswerStyle.Full && q.OptionalFullAnswer.Length > 0 ? new List<string> { q.OptionalFullAnswer }
                    : q.ShortBullets.Take(AnswerStyleSpec.MaxBullets(view.Style)).ToList();
        foreach (var b in bullets) { view.AppendBullet(b); BulletAdded?.Invoke(view, b); }
    }

    /// <summary>Coach Mode: prepared keywords instantly (same language), otherwise a tiny AI request; deterministic fallback.</summary>
    private async Task<long> CoachAsync(AnswerView view, string question, Classification cls, MatchResult match, bool useCache, string lang, RetrievedContext retrieved, CancellationToken ct)
    {
        var bank = match.Question;
        if (useCache && bank != null)
        {
            view.Source = AnswerSource.Cache;
            view.StoryIds.AddRange(bank.EffectiveStoryIds);
            Metrics.IncCache();
            var kw = bank.CoachKeywords.Count == 3 ? bank.CoachKeywords : CoachPrompt.Keywords(bank.CanonicalQuestion + " " + string.Join(" ", bank.ShortBullets.Take(1)));
            var st = bank.AnswerStructure.Length > 0 ? bank.AnswerStructure : DefaultStructure(bank.Mode);
            return EmitCoach(view, CoachOutput.FromPrepared(kw, st));
        }
        if (_provider != null)
        {
            try
            {
                var ctx = new RetrievedContext(retrieved.Stories.Take(2).ToList(), retrieved.Snippets.Take(3).ToList(), retrieved.Reference);
                view.StoryIds.AddRange(ctx.Stories.Select(s => s.Id));
                var messages = CoachPrompt.Build(_kb, question, cls, ctx, lang, _session.BuildPromptContext(3));
                var sb = new System.Text.StringBuilder();
                Metrics.IncLlmRequest();
                await foreach (var d in _provider.StreamAsync(messages, 80, ct).ConfigureAwait(false)) sb.Append(d);
                var parsed = CoachOutput.Parse(sb.ToString());
                if (parsed != null) { view.Source = AnswerSource.Llm; Metrics.IncLlm(); return EmitCoach(view, parsed); }
                view.ValidationFlags.Add("COACH_FORMAT: model output not in contract, used local fallback");
            }
            catch (ProviderException ex) when (!ct.IsCancellationRequested) { Metrics.IncApiError(); Log?.Invoke("Coach provider error: " + ex.Kind); }
        }
        view.Source = bank != null ? AnswerSource.CacheFallback : AnswerSource.Error;
        var structure = DefaultStructure(cls.Mode);
        return EmitCoach(view, bank != null && bank.CoachKeywords.Count == 3 ? CoachOutput.FromPrepared(bank.CoachKeywords, bank.AnswerStructure) : CoachOutput.FromPrepared(CoachPrompt.Keywords(question), structure));
    }

    private static string DefaultStructure(AnswerMode m) => m switch
    {
        AnswerMode.Verified => Loc.T("coach_structure_verified"), AnswerMode.Bridge => Loc.T("coach_structure_bridge"), _ => Loc.T("coach_structure_default"),
    };

    private long EmitCoach(AnswerView view, CoachOutput coach)
    {
        view.Coach = coach;
        view.AppendBullet(coach.KeywordLine);
        BulletAdded?.Invoke(view, coach.KeywordLine);
        view.AppendBullet(coach.Structure);
        BulletAdded?.Invoke(view, coach.Structure);
        return _clock.NowMs;
    }

    private async Task<(long FirstToken, long FirstBullet)> StreamLlmAsync(AnswerView view, string question, Classification cls, RetrievedContext ctx, AnswerStyle style, PromptRequest request, CancellationToken ct)
    {
        view.Source = AnswerSource.Llm;
        view.StoryIds.AddRange(ctx.Stories.Select(s => s.Id));
        var messages = _prompts.Build(question, cls, ctx, style, Array.Empty<ConversationTurn>(), request);
        var maxTokens = AnswerStyleSpec.For(style).MaxTokens;
        var parser = new BulletStreamParser();
        long firstToken = -1, firstBullet = -1;
        view.BridgePending = cls.Mode == AnswerMode.Bridge;
        Metrics.IncLlmRequest();
        Interlocked.Add(ref Metrics.ApproxLlmInputChars, messages.Sum(m => m.Content.Length));

        await foreach (var delta in _provider!.StreamAsync(messages, maxTokens, ct).ConfigureAwait(false))
        {
            if (firstToken < 0) firstToken = _clock.NowMs;
            Interlocked.Add(ref Metrics.ApproxLlmOutputChars, delta.Length);
            foreach (var b in parser.Push(delta)) { if (AcceptBullet(view, parser, b, style) && firstBullet < 0) firstBullet = _clock.NowMs; }
            if (parser.Pending.Length > 0) PendingTextChanged?.Invoke(view, parser.Pending);
        }
        foreach (var b in parser.Complete()) { if (AcceptBullet(view, parser, b, style) && firstBullet < 0) firstBullet = _clock.NowMs; }
        PendingTextChanged?.Invoke(view, "");
        return (firstToken, firstBullet);
    }

    private static readonly System.Text.RegularExpressions.Regex BridgeCue = new(
        @"\b(haven't|have not|hasn't|not (personally|directly|yet)|never (owned|built|done|implemented|led)|didn't|did not|no direct|closest|not .{0,30}end-to-end)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PastResult = new(
        @"\b(reduced|increased|grew|improved|saved|raised|boosted|achieved|generated|doubled|tripled|lowered|cut|delivered|drove)\b(?!.*\b(I'd|I would|would|could|aim|target|goal|expect)\b)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    public const string HonestBridge = "I haven't owned that exact work end-to-end, but here is how I'd approach it.";

    /// <summary>The honest bridge and the cues that show the model already wrote one, per answer language.</summary>
    private static readonly Dictionary<string, (string Bridge, System.Text.RegularExpressions.Regex Cue)> Bridges = new()
    {
        ["es"] = ("No he hecho exactamente ese trabajo de principio a fin, pero así es como lo abordaría.", Cue(@"\b(no he|nunca|no directamente|no personalmente|lo más cercano)\b")),
        ["fr"] = ("Je n'ai pas mené ce travail exact de bout en bout, mais voici comment je l'aborderais.", Cue(@"(je n'ai pas|jamais|pas directement|pas personnellement|le plus proche)")),
        ["de"] = ("Genau diese Arbeit habe ich noch nicht vollständig verantwortet, aber so würde ich vorgehen.", Cue(@"\b(nicht|noch nie|keine direkte|am nächsten)\b")),
        ["pt"] = ("Não fui responsável exatamente por esse trabalho de ponta a ponta, mas é assim que eu o abordaria.", Cue(@"\b(não|nunca|mais próximo)\b")),
        ["it"] = ("Non ho gestito esattamente questo lavoro dall'inizio alla fine, ma ecco come lo affronterei.", Cue(@"\b(non ho|mai|non direttamente|più vicino)\b")),
        ["ar"] = ("لم أتولَّ هذا العمل بعينه من البداية إلى النهاية، لكن هكذا سأتعامل معه.", Cue(@"(لم |لا |ليس)")),
        ["fa"] = ("این کار را دقیقاً از ابتدا تا انتها انجام نداده‌ام، اما این‌طور به آن می‌پردازم.", Cue(@"(نکرده|نداشته|نبوده|هرگز|نداده)")),
        ["zh"] = ("我没有完整负责过这项具体工作，但我会这样着手。", Cue(@"(没有|未曾|从未|不是直接)")),
        ["hi"] = ("मैंने ठीक यही काम शुरू से अंत तक नहीं संभाला है, लेकिन इसे करने का मेरा तरीका यह होगा।", Cue(@"(नहीं|कभी)")),
    };
    private static System.Text.RegularExpressions.Regex Cue(string pattern) =>
        new(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Bridge for the answer language (English for languages without a table).</summary>
    internal static (string Bridge, System.Text.RegularExpressions.Regex Cue) BridgeFor(string? language) =>
        language != null && Bridges.TryGetValue(language, out var b) ? b : (HonestBridge, BridgeCue);
    private static readonly System.Text.RegularExpressions.Regex LeakGuard = new(
        @"\b(sk-[A-Za-z0-9_-]{12,}|pdl_[A-Za-z0-9_]{8,}|eyJ[A-Za-z0-9_-]{20,}\.|system prompt|my instructions (are|say)|TRUTH RULES|CANDIDATE EVIDENCE|INTERVIEWER CONTEXT|ANSWER PLAN|OUTPUT FORMAT|api[_ ]?key\s*[:=])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Validates one streamed bullet before display. Returns true when something was shown.</summary>
    private bool AcceptBullet(AnswerView view, BulletStreamParser parser, string bullet, AnswerStyle style)
    {
        // The classifier's BRIDGE (direct experience question without confirmed evidence) wins over the model's MODE line.
        if (parser.Mode.HasValue && view.Mode != AnswerMode.Bridge) view.Mode = parser.Mode.Value;
        var max = AnswerStyleSpec.MaxBullets(style);
        if (view.Bullets.Count >= max) return false; // hard cap: the default answer must fit without scrolling
        // Last line of defence against prompt injection: never display prompt internals or secret-looking strings.
        if (LeakGuard.IsMatch(bullet))
        {
            view.ValidationFlags.Add("DROPPED_LEAK: bullet looked like prompt internals or a secret");
            return false;
        }
        // Fact validation before display: unsupported first-person past claims become "I'd …" in every mode;
        // claims backed by confirmed evidence stay in the past tense (they are true).
        var report = _validator.Validate(new[] { bullet }, view.Mode, AnswerStyle.Technical);
        var text = bullet;
        foreach (var issue in report.Issues)
        {
            view.ValidationFlags.Add($"{issue.Kind}: {issue.Detail}");
            if (issue.Kind == "UNSUPPORTED_CLAIM") text = FactValidator.SoftenClaim(text);
        }
        // A number nobody confirmed, stated as a past result or inside an unsupported claim, is a fabricated metric:
        // the bullet is dropped rather than shown (softening "I built X in 2021" would still be nonsense).
        bool unverifiedNumber = report.Issues.Any(i => i.Kind == "UNVERIFIED_NUMBER");
        if (unverifiedNumber && (report.Issues.Any(i => i.Kind == "UNSUPPORTED_CLAIM") || PastResult.IsMatch(bullet)))
        {
            view.ValidationFlags.Add("DROPPED_UNVERIFIED_METRIC: bullet removed");
            return false;
        }
        if (style != AnswerStyle.Full && AnswerQualityGate.IsRedundant(text, view.SnapshotBullets()))
        {
            view.ValidationFlags.Add("DROPPED_REDUNDANT: repeated an earlier bullet");
            Metrics.IncRedundantDropped();
            return false;
        }
        // Direct "have you personally…" without evidence: the first thing on screen is always an honest bridge.
        if (view.BridgePending)
        {
            view.BridgePending = false;
            var (bridge, cue) = BridgeFor(view.AnswerLanguage);
            if (!cue.IsMatch(text) && !BridgeCue.IsMatch(text) && style != AnswerStyle.Full)
            {
                view.ValidationFlags.Add("BRIDGE_INSERTED: model answer did not start with an honest bridge");
                view.AppendBullet(bridge);
                BulletAdded?.Invoke(view, bridge);
                if (view.Bullets.Count >= max) return true;
            }
        }
        view.AppendBullet(text);
        BulletAdded?.Invoke(view, text);
        return true;
    }

    private void SetStatus(EngineStatus s, string? detail = null)
    {
        if (Status == s && StatusDetail == detail) return;
        Status = s; StatusDetail = detail;
        StatusChanged?.Invoke(s, detail);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_transcriber != null) await _transcriber.DisposeAsync().ConfigureAwait(false);
        _runCts?.Dispose();
    }
}
