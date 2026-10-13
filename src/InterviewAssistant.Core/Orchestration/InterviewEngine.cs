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
///   audio -> transcriber -> TurnDetector -> (dedupe) -> classify + match -> cache answer OR LLM stream -> stable bullets.
/// All events may fire on background threads; UI must marshal to its dispatcher.
/// One finalized question causes at most one LLM request (none on a high-confidence cache hit). When the interviewer
/// continues a question after a pause, the provisional answer is cancelled and the SAME history entry is answered
/// again for the merged question (one question, one answer on screen).
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
    private readonly Dictionary<int, AnswerView> _turnAnswers = new();
    private readonly DuplicateGuard _dupes = new();
    private readonly SessionMemory _session = new();
    private readonly InterruptionTracker _interrupt = new();
    private readonly List<AnswerView> _history = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _answerCts;
    private CancellationTokenSource? _runCts;
    private Task? _tickLoop;
    private int _nextAnswerId;
    private long _lastAudioActivityMs;
    private bool _listening, _paused, _heardAudio, _noAudioWarned;

    public EngineOptions Options { get; }
    public SessionMetrics Metrics { get; } = new();
    public EngineStatus Status { get; private set; } = EngineStatus.Ready;
    public string? StatusDetail { get; private set; }
    public TurnDetector Turn => _turn;
    public QuestionMatcher Matcher => _matcher;
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

    /// <summary>Interviewer speech started (from transcriber, or tests): feeds the turn detector and interruption heuristic.</summary>
    public void OnInterviewerSpeechStarted()
    {
        bool continuation;
        lock (_gate) continuation = _turn.OnSpeechStarted();
        if (continuation) return; // the interviewer is finishing the same question, not interrupting the answer
        var ev = _interrupt.InterviewerSpeechStarted(_clock.NowMs);
        if (ev != null) { _session.Interruptions.Add(ev); InterruptionDetected?.Invoke(ev); }
    }

    public event Action<EngineStatus, string?>? StatusChanged;
    public event Action<string>? LiveTranscriptChanged;
    public event Action<AnswerView>? AnswerStarted;          // new answer card (question visible immediately)
    public event Action<AnswerView, string>? BulletAdded;    // append-only
    public event Action<AnswerView, string>? PendingTextChanged; // preview of the bullet being written
    public event Action<AnswerView>? AnswerCompleted;
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
        _turn = new TurnDetector(_clock) { Aliases = kb.Context.Aliases };
        _turn.LiveTextChanged += t => LiveTranscriptChanged?.Invoke(t);
        _turn.StateChanged += OnTurnState;
        _turn.TurnFinalized += t => _ = HandleFinalizedAsync(t);
        _interrupt.LevelChanged += l => AdaptiveChanged?.Invoke(l);

        if (_transcriber != null)
        {
            _transcriber.SpeechStarted += OnInterviewerSpeechStarted;
            _transcriber.SpeechStopped += () => { lock (_gate) _turn.OnSpeechStopped(); };
            _transcriber.PartialTranscript += (id, d) => { lock (_gate) _turn.OnPartial(id, d); };
            _transcriber.SegmentCompleted += (id, t) => { lock (_gate) _turn.OnSegmentCompleted(id, t); };
            _transcriber.StatusChanged += OnTranscriberStatus;
        }
    }

    // ---------------- lifecycle ----------------

    public async Task StartListeningAsync()
    {
        if (_listening && !_paused) return;
        _paused = false;
        _listening = true;
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        lock (_gate) _turn.Start();
        _lastAudioActivityMs = _clock.NowMs;
        _heardAudio = false; _noAudioWarned = false;
        if (_transcriber != null) await _transcriber.StartAsync(_runCts.Token).ConfigureAwait(false);
        if (Options.AutoTick) _tickLoop = TickLoopAsync(_runCts.Token);
        SetStatus(EngineStatus.Listening);
    }

    /// <summary>Pause: stop sending audio and stop answering, keep the connection warm briefly.</summary>
    public void Pause()
    {
        _paused = true;
        lock (_gate) _turn.Stop();
        SetStatus(EngineStatus.Paused);
    }

    public async Task StopAsync()
    {
        _listening = false; _paused = false;
        _runCts?.Cancel();
        CancelAnswer();
        lock (_gate) _turn.Stop();
        if (_transcriber != null) await _transcriber.StopAsync().ConfigureAwait(false);
        if (_tickLoop != null) { try { await _tickLoop.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        SetStatus(EngineStatus.Stopped);
    }

    public bool IsListening => _listening && !_paused;

    /// <summary>Feed converted 24 kHz mono PCM16 frames from the capture layer.</summary>
    public void OnAudioFrame(byte[] pcm16, double levelDb)
    {
        if (!IsListening) return;
        if (levelDb > -55) { _lastAudioActivityMs = _clock.NowMs; _heardAudio = true; }
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

    /// <summary>Advances time-based logic (turn settling, no-audio warning). Public for deterministic tests.</summary>
    public void Tick()
    {
        lock (_gate) _turn.Tick();
        var silentMs = _clock.NowMs - _lastAudioActivityMs;
        if (!IsListening) return;
        // Silence after the interviewer finishes is normal (the candidate is talking). Warn only when NOTHING has been
        // heard since listening started — that usually means the meeting plays on a different device — and only once.
        if (!_heardAudio && !_noAudioWarned && Status is EngineStatus.Listening or EngineStatus.Quiet && silentMs > Options.NoAudioWarningSeconds * 1000L)
        {
            _noAudioWarned = true;
            SetStatus(EngineStatus.NoAudio, "No interview audio detected. Check that Chrome/Meet/Zoom plays on the selected playback device.");
        }
        else if (Status == EngineStatus.Listening && silentMs > Options.QuietAfterSeconds * 1000L)
            SetStatus(EngineStatus.Quiet);
        else if (Status is EngineStatus.NoAudio or EngineStatus.Quiet && silentMs < 1000)
            SetStatus(EngineStatus.Listening);
    }

    private void OnTurnState(TurnState s)
    {
        if (!IsListening || Status is EngineStatus.Reconnecting or EngineStatus.ApiError) return;
        if (Status == EngineStatus.Answering && s != TurnState.SpeechStarted) return;
        switch (s)
        {
            case TurnState.SpeechStarted: SetStatus(EngineStatus.SpeechDetected); break;
            case TurnState.PossibleEnd:
            case TurnState.TurnSettling: SetStatus(EngineStatus.Finalizing); break;
            case TurnState.Listening: if (Status is EngineStatus.SpeechDetected or EngineStatus.Finalizing) SetStatus(EngineStatus.Listening); break;
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
                lock (_gate) { _turn.OnConnectionInterrupted(); } // keep completed segments: the question is still answered
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
        if (previous != null)
        {
            // Multi-part continuation / restatement: replace the provisional answer of the same question.
            Metrics.IncMerged();
            Log?.Invoke("Question continued — answering the whole question: " + turn.Text);
            _dupes.Register(turn.Text, now);
        }
        else
        {
            if (_dupes.IsDuplicate(turn.Text, now)) { Metrics.IncDuplicate(); Log?.Invoke("Duplicate suppressed: " + turn.Text); return; }
            _dupes.Register(turn.Text, now);
            Metrics.IncQuestions();
        }
        await AnswerAsync(turn.Text, Options.DefaultStyle, allowCache: true, speechEndMs: turn.SpeechEndMs, finalizedMs: turn.FinalizedMs, turnId: turn.TurnId, replaces: previous).ConfigureAwait(false);
    }

    /// <summary>Manual fallback: typed/pasted question. Works without audio or transcription.</summary>
    public Task SubmitManualQuestionAsync(string text)
    {
        var q = TextNormalizer.CleanTranscript(text, _kb.Context.Aliases);
        if (q.Length == 0) return Task.CompletedTask;
        _dupes.Register(q, _clock.NowMs);
        Metrics.IncQuestions();
        return AnswerAsync(q, Options.DefaultStyle, allowCache: true, speechEndMs: -1, finalizedMs: _clock.NowMs);
    }

    /// <summary>User-requested variant of the current question (Shorter / Technical / Example / Full / Regenerate). Always uses the LLM.</summary>
    public Task RequestVariantAsync(AnswerStyle style)
    {
        var q = Current?.Question;
        if (q == null) return Task.CompletedTask;
        return AnswerAsync(q, style, allowCache: false, speechEndMs: -1, finalizedMs: _clock.NowMs, isVariant: true);
    }

    public IReadOnlyList<AnswerView> History { get { lock (_history) return _history.ToList(); } }

    private void CancelAnswer()
    {
        var cts = Interlocked.Exchange(ref _answerCts, null);
        if (cts != null) { cts.Cancel(); cts.Dispose(); }
    }

    private async Task AnswerAsync(string question, AnswerStyle style, bool allowCache, long speechEndMs, long finalizedMs, bool isVariant = false, int turnId = 0, AnswerView? replaces = null)
    {
        // A newer question supersedes any in-flight generation.
        if (replaces != null) replaces.Superseded = true;
        CancelAnswer();
        var cts = new CancellationTokenSource();
        _answerCts = cts;
        var ct = cts.Token;

        var t0 = _clock.NowMs;
        var cls = QuestionClassifier.Classify(question, _kb.Context, _evidence);
        var match = _matcher.Match(question);
        var matchMs = _clock.NowMs - t0;

        // Language: detect per question (falls back to the locked interview language); memory is NOT reset on switches.
        var det = LanguageDetector.Detect(question);
        var detected = det.Confidence >= 0.5 ? det.Code : "und";
        var answerLang = LanguageRegistry.ResolveAnswerLanguage(AnswerLanguageOverride ?? _kb.Context.AnswerLanguage, _kb.Context.InterviewLanguage, detected);

        // Adaptive pace: shorten automatically after likely interruptions (default style only).
        var effective = style;
        if (style == AnswerStyle.Balanced && !isVariant)
            effective = _interrupt.Level switch { AdaptiveLevel.Concise => AnswerStyle.Concise, AdaptiveLevel.Rapid => AnswerStyle.Rapid, _ => style };
        var presentation = isVariant ? Presentation.Answer : Presentation;

        var reused = replaces?.Record;
        if (reused != null)
        {
            reused.Original = question; reused.DetectedLanguage = detected; reused.AnswerLanguage = answerLang; reused.Category = cls.Category; reused.Mode = cls.Mode.ToString();
            reused.MatchedQuestionId = match.Question?.QuestionId; reused.AnswerBullets.Clear(); reused.StoryIds.Clear();
        }
        var record = isVariant ? null : reused ?? _session.Add(new QuestionRecord
        {
            Original = question, DetectedLanguage = detected, AnswerLanguage = answerLang, Category = cls.Category, Mode = cls.Mode.ToString(),
            IntentKey = SessionMemory.IntentKeyFor(question, cls.Category, match.Confidence >= MatchConfidence.Medium ? match.Question : null),
            MatchedQuestionId = match.Question?.QuestionId, FollowUpOf = cls.IsFollowUp ? _session.Last?.Index : null,
            ReferencesEarlierAnswer = SessionMemory.ReferencesEarlier(question), Presentation = presentation.ToString(),
            Entities = new[] { _kb.Context.CompanyName }.Concat(_kb.Context.Products).Where(e => e.Length > 1 && question.Contains(e, StringComparison.OrdinalIgnoreCase)).ToList(),
        });
        if (record != null) foreach (var e in record.Entities) _session.CompanyTopics.Add(e);
        if (record?.ReferencesEarlierAnswer == true) // "you already mentioned 60,000…" → that metric counts as used
            foreach (var m in Ingestion.ResumeParser.ExtractMetrics(question)) _session.MetricUse[m] = _session.MetricUse.GetValueOrDefault(m) + 1;

        var view = new AnswerView
        {
            Id = Interlocked.Increment(ref _nextAnswerId), Question = question, Style = effective, Mode = cls.Mode, Category = cls.Category,
            MatchedQuestionId = match.Question?.QuestionId, MatchScore = match.Score, QuestionIndex = record?.Index ?? _session.Last?.Index ?? 0,
            DetectedLanguage = detected, AnswerLanguage = answerLang, Presentation = presentation, Adaptive = _interrupt.Level,
            ReplacesAnswerId = replaces?.Id, TurnId = turnId, Record = record,
        };
        Current = view;
        lock (_history)
        {
            var at = replaces == null ? -1 : _history.IndexOf(replaces);
            if (at >= 0) _history[at] = view;   // same question → same history entry
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
        bool useCache = allowCache && Options.UseFastCache && bank != null && bank.Confidence >= Options.MinCacheConfidence && languageOk &&
                        (match.Confidence == MatchConfidence.High && (!cls.IsFollowUp || match.Score >= 0.75)) &&
                        !(_provider != null && (repeatsStory || referencesEarlier)); // prefer a fresh answer instead of repeating a story
        // Without any LLM available, a Medium match is still far better than nothing.
        if (!useCache && allowCache && _provider == null && bank != null && match.Confidence >= MatchConfidence.Low) useCache = true;
        _prompts.LanguageRule = LanguageRegistry.AnswerLanguageRule(answerLang);
        _prompts.MemoryContext = _session.BuildPromptContext();
        _prompts.ExtraInstruction = referencesEarlier ? "The interviewer refers to something already mentioned: do NOT repeat it — use a different verified story/angle or go deeper as asked." :
                                    repeatsStory ? "Avoid re-using the stories already used unless no other evidence fits." : null;

        try
        {
            if (presentation == Presentation.Coach)
            {
                firstBullet = await CoachAsync(view, question, cls, match, useCache, answerLang, ct).ConfigureAwait(false);
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
                    (firstToken, firstBullet) = await StreamLlmAsync(view, question, cls, match, effective, ct).ConfigureAwait(false);
                    Metrics.IncLlm();
                }
                catch (ProviderException ex) when (!ct.IsCancellationRequested)
                {
                    Metrics.IncApiError();
                    Log?.Invoke($"Answer provider error: {ex.Kind} {ex.Code} {ex.Message}");
                    view.Error = ex.Kind;
                    var message = ProviderException.UserMessage(ex.Kind, Options.DeveloperKeyMode);
                    // Fall back to a prepared answer only when it is a SAFE match (never a confidently wrong one).
                    if (view.Bullets.Count == 0 && bank != null && match.Confidence >= MatchConfidence.Medium && languageOk)
                    {
                        EmitCached(view, bank, AnswerSource.CacheFallback);
                        view.Note = "Prepared answer — " + message.Replace(" Prepared answers still work.", "");
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
            else
            {
                view.Source = AnswerSource.Error;
                view.Note = Options.DeveloperKeyMode ? "No prepared answer and no AI provider configured — add an API key in Settings" : "No prepared answer for this question, and AI answers are unavailable.";
            }
        }
        catch (OperationCanceledException) { view.Note ??= "Superseded"; }
        finally
        {
            view.IsComplete = true;
            var bullets = view.SnapshotBullets();
            if (record != null && !view.Superseded)
            {
                var metrics = bullets.SelectMany(Ingestion.ResumeParser.ExtractMetrics).Where(m => _kb.Profile.VerifiedNumbers.Contains(m)).Distinct();
                _session.CompleteAnswer(record, bullets, view.StoryIds, metrics);
                record.Source = view.Source.ToString();
                record.CoachKeywords = view.Coach?.Keywords.ToList() ?? new();
                record.FirstBulletLatencyMs = firstBullet >= 0 ? firstBullet - finalizedMs : -1;
            }
            if (firstBullet >= 0 && !isVariant && !view.Superseded)
            {
                var words = view.Coach != null ? 25 : bullets.Sum(TextNormalizer.WordCount);
                _interrupt.AnswerShown(firstBullet, words, view.QuestionIndex);
            }
            AnswerCompleted?.Invoke(view);
            if (ReferenceEquals(_answerCts, cts)) { _answerCts = null; cts.Dispose(); }
            if (Status is EngineStatus.Answering && !view.Superseded)
                SetStatus(!IsListening ? (_paused ? EngineStatus.Paused : EngineStatus.Ready) : view.Bullets.Count > 0 ? EngineStatus.AnswerReady : EngineStatus.Listening);

            var done = _clock.NowMs;
            if (!view.Superseded)
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
    private async Task<long> CoachAsync(AnswerView view, string question, Classification cls, MatchResult match, bool useCache, string lang, CancellationToken ct)
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
                var ctx = _retriever.Retrieve(question, cls, match, maxStories: 2, maxSnippets: 3, storyPenalty: _session.StoryPenalty);
                view.StoryIds.AddRange(ctx.Stories.Select(s => s.Id));
                var messages = CoachPrompt.Build(_kb, question, cls, ctx, lang, _session.BuildPromptContext(3));
                var sb = new System.Text.StringBuilder();
                Metrics.IncLlmRequest();
                await foreach (var d in _provider.StreamAsync(messages, 80, ct).ConfigureAwait(false)) sb.Append(d);
                var parsed = CoachOutput.Parse(sb.ToString());
                if (parsed != null) { view.Source = AnswerSource.Llm; Metrics.IncLlm(); return EmitCoach(view, parsed); }
                view.ValidationFlags.Add("COACH_FORMAT: model output not in contract, used local fallback");
            }
            catch (ProviderException ex) when (!ct.IsCancellationRequested) { Metrics.IncApiError(); Log?.Invoke("Coach provider error: " + ex.Message); }
        }
        view.Source = bank != null ? AnswerSource.CacheFallback : AnswerSource.Error;
        var structure = DefaultStructure(cls.Mode);
        return EmitCoach(view, bank != null && bank.CoachKeywords.Count == 3 ? CoachOutput.FromPrepared(bank.CoachKeywords, bank.AnswerStructure) : CoachOutput.FromPrepared(CoachPrompt.Keywords(question), structure));
    }

    private static string DefaultStructure(AnswerMode m) => m switch
    {
        AnswerMode.Verified => "Situation → what I did → result", AnswerMode.Bridge => "Honest bridge → approach → closest proof", _ => "Direct answer → approach → measure",
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

    private async Task<(long FirstToken, long FirstBullet)> StreamLlmAsync(AnswerView view, string question, Classification cls, MatchResult match, AnswerStyle style, CancellationToken ct)
    {
        view.Source = AnswerSource.Llm;
        var ctx = _retriever.Retrieve(question, cls, match, storyPenalty: _session.StoryPenalty);
        view.StoryIds.AddRange(ctx.Stories.Select(s => s.Id));
        var messages = _prompts.Build(question, cls, ctx, style, Array.Empty<ConversationTurn>());
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
            foreach (var b in parser.Push(delta)) { AcceptBullet(view, parser, b, style); if (firstBullet < 0) firstBullet = _clock.NowMs; }
            if (parser.Pending.Length > 0) PendingTextChanged?.Invoke(view, parser.Pending);
        }
        foreach (var b in parser.Complete()) { AcceptBullet(view, parser, b, style); if (firstBullet < 0) firstBullet = _clock.NowMs; }
        PendingTextChanged?.Invoke(view, "");
        return (firstToken, firstBullet);
    }

    private static readonly System.Text.RegularExpressions.Regex BridgeCue = new(
        @"\b(haven't|have not|hasn't|not (personally|directly|yet)|never (owned|built|done|implemented|led)|didn't|did not|no direct|closest|not .{0,30}end-to-end)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    public const string HonestBridge = "I haven't owned that exact work end-to-end, but here is how I'd approach it.";

    private void AcceptBullet(AnswerView view, BulletStreamParser parser, string bullet, AnswerStyle style)
    {
        // The classifier's BRIDGE (direct experience question without confirmed evidence) wins over the model's MODE line.
        if (parser.Mode.HasValue && view.Mode != AnswerMode.Bridge) view.Mode = parser.Mode.Value;
        var max = AnswerStyleSpec.MaxBullets(style);
        if (view.Bullets.Count >= max) return; // hard cap: the default answer must fit without scrolling
        // Fact validation before display: unsupported first-person past claims become "I'd …" in every mode.
        var report = _validator.Validate(new[] { bullet }, view.Mode, AnswerStyle.Technical);
        var text = bullet;
        foreach (var issue in report.Issues)
        {
            view.ValidationFlags.Add($"{issue.Kind}: {issue.Detail}");
            if (issue.Kind == "UNSUPPORTED_CLAIM") text = FactValidator.SoftenClaim(text);
        }
        // Direct "have you personally…" without evidence: the first thing on screen is always an honest bridge.
        if (view.BridgePending)
        {
            view.BridgePending = false;
            if (!BridgeCue.IsMatch(text) && style != AnswerStyle.Full)
            {
                view.ValidationFlags.Add("BRIDGE_INSERTED: model answer did not start with an honest bridge");
                view.AppendBullet(HonestBridge);
                BulletAdded?.Invoke(view, HonestBridge);
                if (view.Bullets.Count >= max) return;
            }
        }
        view.AppendBullet(text);
        BulletAdded?.Invoke(view, text);
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
