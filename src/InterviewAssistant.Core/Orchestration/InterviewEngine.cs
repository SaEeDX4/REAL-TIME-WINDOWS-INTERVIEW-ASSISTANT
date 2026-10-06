using InterviewAssistant.Core.Diagnostics;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Providers;
using InterviewAssistant.Core.Turn;

namespace InterviewAssistant.Core.Orchestration;

/// <summary>
/// Orchestrates the live pipeline:
///   audio -> transcriber -> TurnDetector -> (dedupe) -> classify + match -> cache answer OR LLM stream -> stable bullets.
/// All events may fire on background threads; UI must marshal to its dispatcher.
/// One finalized question causes at most one LLM request (none on a high-confidence cache hit).
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
    private readonly DuplicateGuard _dupes = new();
    private readonly ConversationMemory _memory = new();
    private readonly List<AnswerView> _history = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _answerCts;
    private CancellationTokenSource? _runCts;
    private Task? _tickLoop;
    private int _nextAnswerId;
    private long _lastAudioActivityMs;
    private bool _listening, _paused;

    public EngineOptions Options { get; }
    public SessionMetrics Metrics { get; } = new();
    public EngineStatus Status { get; private set; } = EngineStatus.Ready;
    public string? StatusDetail { get; private set; }
    public TurnDetector Turn => _turn;
    public QuestionMatcher Matcher => _matcher;
    public AnswerView? Current { get; private set; }

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
        _validator = new FactValidator(kb.Profile);
        _turn = new TurnDetector(_clock);
        _turn.LiveTextChanged += t => LiveTranscriptChanged?.Invoke(t);
        _turn.StateChanged += OnTurnState;
        _turn.TurnFinalized += t => _ = HandleFinalizedAsync(t);

        if (_transcriber != null)
        {
            _transcriber.SpeechStarted += () => { lock (_gate) _turn.OnSpeechStarted(); };
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
        if (levelDb > -55) _lastAudioActivityMs = _clock.NowMs;
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
        if (IsListening && Status == EngineStatus.Listening && _clock.NowMs - _lastAudioActivityMs > Options.NoAudioWarningSeconds * 1000L)
            SetStatus(EngineStatus.NoAudio, "No audio detected on the selected playback device");
        else if (Status == EngineStatus.NoAudio && _clock.NowMs - _lastAudioActivityMs < 1000)
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
                lock (_gate) { _turn.Reset(); }
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
        if (_dupes.IsDuplicate(turn.Text, now)) { Metrics.IncDuplicate(); Log?.Invoke("Duplicate suppressed: " + turn.Text); return; }
        _dupes.Register(turn.Text, now);
        Metrics.IncQuestions();
        await AnswerAsync(turn.Text, Options.DefaultStyle, allowCache: true, speechEndMs: turn.SpeechEndMs, finalizedMs: turn.FinalizedMs).ConfigureAwait(false);
    }

    /// <summary>Manual fallback: typed/pasted question. Works without audio or transcription.</summary>
    public Task SubmitManualQuestionAsync(string text)
    {
        var q = TextNormalizer.CleanTranscript(text);
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

    private async Task AnswerAsync(string question, AnswerStyle style, bool allowCache, long speechEndMs, long finalizedMs, bool isVariant = false)
    {
        // A newer question supersedes any in-flight generation.
        CancelAnswer();
        var cts = new CancellationTokenSource();
        _answerCts = cts;
        var ct = cts.Token;

        var t0 = _clock.NowMs;
        var cls = QuestionClassifier.Classify(question);
        var match = _matcher.Match(question);
        var matchMs = _clock.NowMs - t0;

        var view = new AnswerView
        {
            Id = Interlocked.Increment(ref _nextAnswerId), Question = question, Style = style, Mode = cls.Mode, Category = cls.Category,
            MatchedQuestionId = match.Question?.QuestionId, MatchScore = match.Score,
        };
        Current = view;
        lock (_history) { _history.Add(view); if (_history.Count > Options.HistoryCapacity) _history.RemoveAt(0); }
        AnswerStarted?.Invoke(view);
        SetStatus(EngineStatus.Answering);

        long firstToken = -1, firstBullet = -1;
        bool useCache = allowCache && Options.UseFastCache && match.Question != null &&
                        (match.Confidence == MatchConfidence.High && (!cls.IsFollowUp || match.Score >= 0.75));
        // Without any LLM available, a Medium match is still far better than nothing.
        if (!useCache && allowCache && _provider == null && match.Question != null && match.Confidence >= MatchConfidence.Low) useCache = true;

        try
        {
            if (useCache)
            {
                EmitCached(view, match.Question!, _provider == null && match.Confidence < MatchConfidence.High ? AnswerSource.CacheFallback : AnswerSource.Cache);
                firstBullet = _clock.NowMs;
                Metrics.IncCache();
            }
            else if (_provider != null)
            {
                try
                {
                    (firstToken, firstBullet) = await StreamLlmAsync(view, question, cls, match, style, ct).ConfigureAwait(false);
                    Metrics.IncLlm();
                }
                catch (ProviderException ex) when (!ct.IsCancellationRequested)
                {
                    Metrics.IncApiError();
                    Log?.Invoke("Answer provider error: " + ex.Message);
                    if (view.Bullets.Count == 0 && match.Question != null && match.Confidence >= MatchConfidence.Low)
                    {
                        EmitCached(view, match.Question, AnswerSource.CacheFallback);
                        view.Note = "Prepared answer (AI unavailable: " + ex.Kind + ")";
                        firstBullet = _clock.NowMs;
                        Metrics.IncFallback();
                    }
                    else
                    {
                        view.Source = view.Bullets.Count > 0 ? AnswerSource.Llm : AnswerSource.Error;
                        view.Note = ex.Kind == ProviderErrorKind.InvalidApiKey ? "API key rejected — open Settings" : "AI unavailable (" + ex.Kind + ") — type the question or press Regenerate";
                    }
                    SetStatus(EngineStatus.ApiError, ex.Message);
                }
            }
            else
            {
                view.Source = AnswerSource.Error;
                view.Note = "No prepared answer and no AI provider configured — add an API key in Settings";
            }
        }
        catch (OperationCanceledException) { view.Note ??= "Superseded"; }
        finally
        {
            view.IsComplete = true;
            AnswerCompleted?.Invoke(view);
            if (view.Bullets.Count > 0 && !isVariant) _memory.Add(question, view.Bullets);
            if (ReferenceEquals(_answerCts, cts)) { _answerCts = null; cts.Dispose(); }
            if (Status is EngineStatus.Answering) SetStatus(IsListening ? EngineStatus.Listening : EngineStatus.Ready);

            var done = _clock.NowMs;
            var sample = new LatencySample(question, view.Source.ToString(),
                speechEndMs >= 0 ? finalizedMs - speechEndMs : -1, matchMs,
                firstToken >= 0 ? firstToken - finalizedMs : -1, firstBullet >= 0 ? firstBullet - finalizedMs : -1,
                done - finalizedMs, speechEndMs >= 0 && firstBullet >= 0 ? firstBullet - speechEndMs : -1, DateTime.UtcNow);
            Metrics.Add(sample);
            LatencyMeasured?.Invoke(sample);
        }
    }

    private void EmitCached(AnswerView view, BankQuestion q, AnswerSource source)
    {
        view.Source = source;
        view.Mode = q.Mode;
        var bullets = view.Style == AnswerStyle.Full ? new List<string> { q.OptionalFullAnswer } : q.ShortBullets;
        foreach (var b in bullets) { view.Bullets.Add(b); BulletAdded?.Invoke(view, b); }
    }

    private async Task<(long FirstToken, long FirstBullet)> StreamLlmAsync(AnswerView view, string question, Classification cls, MatchResult match, AnswerStyle style, CancellationToken ct)
    {
        view.Source = AnswerSource.Llm;
        var ctx = _retriever.Retrieve(question, cls, match);
        var messages = _prompts.Build(question, cls, ctx, style, _memory.Snapshot());
        var maxTokens = AnswerStyleSpec.For(style).MaxTokens;
        var parser = new BulletStreamParser();
        long firstToken = -1, firstBullet = -1;
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

    private void AcceptBullet(AnswerView view, BulletStreamParser parser, string bullet, AnswerStyle style)
    {
        if (parser.Mode.HasValue) view.Mode = parser.Mode.Value;
        // Fact validation before display: soften unsupported past claims; flag unverified numbers in history answers.
        var report = _validator.Validate(new[] { bullet }, view.Mode, AnswerStyle.Technical);
        var text = bullet;
        foreach (var issue in report.Issues)
        {
            view.ValidationFlags.Add($"{issue.Kind}: {issue.Detail}");
            if (issue.Kind == "UNSUPPORTED_CLAIM") text = FactValidator.SoftenClaim(text);
        }
        if (view.Bullets.Count >= 6) return; // hard cap: never flood the reader
        view.Bullets.Add(text);
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
