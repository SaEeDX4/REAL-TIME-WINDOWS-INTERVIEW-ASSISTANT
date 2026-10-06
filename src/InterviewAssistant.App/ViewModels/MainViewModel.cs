using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewAssistant.App.Services;
using InterviewAssistant.Core.Diagnostics;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.App.ViewModels;

public sealed class BulletVm
{
    public required string Text { get; init; }
    public bool IsParagraph { get; init; }
}

/// <summary>
/// Composition root + presentation state for the live window. Engine/capture events arrive on background
/// threads and are marshalled with BeginInvoke (never blocking audio or network threads). Answer bullets are
/// append-only so text never shifts while Shervin is reading.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _ui;
    public AppSettings Settings { get; }
    public KnowledgeBase? Knowledge { get; private set; }
    private InterviewEngine? _engine;
    private OpenAiChatAnswerProvider? _provider;
    private OpenAiRealtimeTranscriber? _transcriber;
    private readonly AudioCaptureService _capture = new();
    private readonly DispatcherTimer _diagTimer;
    private int _displayedAnswerId = -1;
    private long _lastMeterTick;
    private double _peakDb = -100;
    private TimeSpan _lastCpu;
    private DateTime _lastCpuAt = DateTime.UtcNow;

    public ObservableCollection<BulletVm> Bullets { get; } = new();

    public MainViewModel(Dispatcher ui)
    {
        _ui = ui;
        Settings = AppSettings.Load();
        _capture.FrameReady += OnAudioFrame;
        _capture.StatusChanged += s => Post(() => AudioStatus = s);
        _diagTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _diagTimer.Tick += (_, _) => RefreshDiagnostics();
        _diagTimer.Start();

        StartPauseCommand = new RelayCommand(async () => await ToggleListeningAsync());
        StopCommand = new RelayCommand(async () => await StopAsync(), () => IsListening || IsPaused);
        ShorterCommand = new RelayCommand(() => Variant(AnswerStyle.VeryShort), HasQuestion);
        TechnicalCommand = new RelayCommand(() => Variant(AnswerStyle.Technical), HasQuestion);
        ExampleCommand = new RelayCommand(() => Variant(AnswerStyle.Example), HasQuestion);
        FullCommand = new RelayCommand(() => Variant(AnswerStyle.Full), HasQuestion);
        RegenerateCommand = new RelayCommand(() => Variant(AnswerStyle.Balanced), HasQuestion);
        PreviousCommand = new RelayCommand(() => Navigate(-1), () => HistoryPosition > 0);
        NextCommand = new RelayCommand(() => Navigate(+1), () => _engine != null && HistoryPosition < _engine.History.Count - 1);
    }

    public ICommand StartPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ShorterCommand { get; }
    public ICommand TechnicalCommand { get; }
    public ICommand ExampleCommand { get; }
    public ICommand FullCommand { get; }
    public ICommand RegenerateCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }

    private bool HasQuestion() => _engine?.Current != null && _provider != null;

    // ---------------- bindable state ----------------
    private string _statusText = "READY"; public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    private Brush _statusBrush = Brushes.Gray; public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    private bool _statusPulse; public bool StatusPulse { get => _statusPulse; set => Set(ref _statusPulse, value); }
    private string? _statusDetail; public string? StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }
    private string _question = ""; public string Question { get => _question; set { if (Set(ref _question, value)) Raise(nameof(HasQuestionText)); } }
    public bool HasQuestionText => Question.Length > 0;
    private string _liveTranscript = ""; public string LiveTranscript { get => _liveTranscript; set => Set(ref _liveTranscript, value); }
    private string _pending = ""; public string Pending { get => _pending; set => Set(ref _pending, value); }
    private string? _note; public string? Note { get => _note; set => Set(ref _note, value); }
    private string _answerMeta = ""; public string AnswerMeta { get => _answerMeta; set => Set(ref _answerMeta, value); }
    private double _audioLevel; public double AudioLevel { get => _audioLevel; set => Set(ref _audioLevel, value); }
    private string _audioStatus = "—"; public string AudioStatus { get => _audioStatus; set => Set(ref _audioStatus, value); }
    private bool _isListening; public bool IsListening { get => _isListening; set { if (Set(ref _isListening, value)) Raise(nameof(StartPauseLabel)); } }
    private bool _isPaused; public bool IsPaused { get => _isPaused; set { if (Set(ref _isPaused, value)) Raise(nameof(StartPauseLabel)); } }
    public string StartPauseLabel => IsListening ? "Pause" : IsPaused ? "Resume" : "Start";
    private string _diagnostics = ""; public string Diagnostics { get => _diagnostics; set => Set(ref _diagnostics, value); }
    private int _historyPosition = -1; public int HistoryPosition { get => _historyPosition; set { if (Set(ref _historyPosition, value)) Raise(nameof(HistoryLabel)); } }
    public string HistoryLabel => _engine == null || _engine.History.Count == 0 ? "" : $"{HistoryPosition + 1} / {_engine.History.Count}";
    public double AnswerFontSize => Settings.AnswerFontSize;
    public double QuestionFontSize => Settings.QuestionFontSize;
    public bool IsThinking => _engine?.Current is { IsComplete: false } && Bullets.Count == 0;

    // ---------------- composition ----------------

    public string? Initialize()
    {
        var dir = KnowledgeBase.FindDirectory(AppContext.BaseDirectory);
        if (dir == null) return "Knowledge folder not found next to the application.";
        try { Knowledge = KnowledgeBase.Load(dir); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            AppLog.Error("Knowledge load failed", ex);
            return "Knowledge pack could not be loaded: " + ex.Message;
        }
        BuildEngine();
        AppLog.Info($"Knowledge loaded: {Knowledge.Questions.Count} prepared questions, {Knowledge.Stories.Count} stories, {Knowledge.Snippets.Count} snippets");
        return null;
    }

    /// <summary>(Re)creates providers + engine from current settings. Safe to call after Settings change while stopped.</summary>
    public void BuildEngine()
    {
        if (Knowledge == null) return;
        if (_engine != null) _ = _engine.DisposeAsync().AsTask(); // releases the previous transcriber connection
        _provider?.Dispose();
        _provider = new OpenAiChatAnswerProvider(SecretStore.GetApiKey, ChatOptions());
        _transcriber = new OpenAiRealtimeTranscriber(SecretStore.GetApiKey, TranscriberOptions());
        _transcriber.Diagnostic += m => AppLog.Info(m);
        _engine = new InterviewEngine(Knowledge, _transcriber, _provider, new EngineOptions { UseFastCache = Settings.UseFastCache });
        _engine.Turn.Sensitivity = Settings.VadSensitivity;
        _engine.StatusChanged += (s, d) => Post(() => ApplyStatus(s, d));
        _engine.LiveTranscriptChanged += t => Post(() => LiveTranscript = t);
        _engine.AnswerStarted += a => Post(() => ShowAnswer(a, isNew: true));
        _engine.BulletAdded += (a, _) => Post(() => { if (a.Id == _displayedAnswerId) { SyncBullets(a); Pending = ""; Raise(nameof(IsThinking)); } });
        _engine.PendingTextChanged += (a, p) => Post(() => { if (a.Id == _displayedAnswerId) Pending = p; });
        _engine.AnswerCompleted += a => Post(() => { if (a.Id == _displayedAnswerId) { Pending = ""; Note = a.Note; AnswerMeta = Meta(a); Raise(nameof(IsThinking)); } LiveTranscript = ""; });
        _engine.LatencyMeasured += s => AppLog.Info($"Latency [{s.Source}] finalize {s.SpeechEndToFinalizedMs} ms, first bullet {s.FinalizedToFirstBulletMs} ms, total {s.FinalizedToCompleteMs} ms");
        _engine.Log += m => AppLog.Info(m);
        ApplyStatus(EngineStatus.Ready, null);
    }

    private void Post(Action a) => _ui.BeginInvoke(a, DispatcherPriority.Normal);

    // ---------------- audio ----------------

    /// <summary>Extra consumer of the exact production capture stream (used by Test Transcription).</summary>
    private Action<byte[]>? _audioTap;

    private void OnAudioFrame(byte[] frame, double db)
    {
        _engine?.OnAudioFrame(frame, db);
        _audioTap?.Invoke(frame);
        _peakDb = Math.Max(_peakDb, db);
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastMeterTick) < 60) return; // ~16 Hz UI updates, never per-packet
        Interlocked.Exchange(ref _lastMeterTick, now);
        var level = Math.Clamp((_peakDb + 60) / 60.0, 0, 1);
        _peakDb = -100;
        Post(() => AudioLevel = level);
    }

    public void StartAudioPreview() { if (!_capture.IsRunning) _capture.Start(Settings.PlaybackDeviceId); }
    public void RestartAudio() { _capture.Stop(); _capture.Start(Settings.PlaybackDeviceId); }
    public string CurrentDeviceName => _capture.CurrentDeviceName;
    public AudioCaptureService Capture => _capture;

    public RealtimeTranscriberOptions TranscriberOptions() => new() { Model = Settings.TranscriptionModel, Protocol = Settings.RealtimeProtocol };
    public ChatProviderOptions ChatOptions() => new() { Model = Settings.AnswerModel };

    /// <summary>
    /// Live transcription test through the SAME capture service, converter, framer and OpenAiRealtimeTranscriber class
    /// used in interviews (separate instance so it never triggers answers).
    /// </summary>
    public async Task<string> TestTranscriptionAsync(Action<string> onTranscript, Action<string> onStatus, TimeSpan duration, CancellationToken ct)
    {
        StartAudioPreview();
        await using var t = new OpenAiRealtimeTranscriber(SecretStore.GetApiKey, TranscriberOptions());
        var text = new System.Text.StringBuilder();
        var partials = new Dictionary<string, string>();
        void Render() { var all = text + (partials.Count > 0 ? " " + string.Join(" ", partials.Values) : ""); Post(() => onTranscript(all.Trim())); }
        t.PartialTranscript += (id, d) => { lock (partials) { partials[id] = partials.GetValueOrDefault(id, "") + d; Render(); } };
        t.SegmentCompleted += (id, tr) => { lock (partials) { partials.Remove(id); if (tr.Length > 0) text.Append(' ').Append(tr.Trim()); Render(); } };
        t.SpeechStarted += () => Post(() => onStatus("Speech detected…"));
        t.StatusChanged += (st, d) => Post(() => onStatus($"{st} {d}"));
        t.Diagnostic += m => Post(() => onStatus(m));
        _audioTap = t.SendAudio;
        try
        {
            await t.StartAsync(ct);
            try { await Task.Delay(duration, ct); } catch (OperationCanceledException) { }
        }
        finally { _audioTap = null; }
        return $"{t.ActiveModel}: {t.Status}";
    }

    /// <summary>AI test through the production answer pipeline (classify → retrieve → prompt → stream → validate).</summary>
    public async Task<(AnswerView Answer, LatencySample? Sample, string Model)> TestAiAsync(string question, Action<string> onBullet)
    {
        if (Knowledge == null) throw new InvalidOperationException("Knowledge not loaded");
        using var provider = new OpenAiChatAnswerProvider(SecretStore.GetApiKey, ChatOptions());
        var engine = new InterviewEngine(Knowledge, null, provider, new EngineOptions { AutoTick = false, UseFastCache = false });
        engine.BulletAdded += (_, b) => Post(() => onBullet(b));
        await engine.SubmitManualQuestionAsync(question);
        return (engine.Current!, engine.Metrics.Samples.LastOrDefault(), provider.ActiveModel);
    }

    // ---------------- controls ----------------

    public async Task ToggleListeningAsync()
    {
        if (_engine == null) return;
        if (IsListening) { _engine.Pause(); IsListening = false; IsPaused = true; return; }
        if (!SecretStore.HasKey) { ShowLocalNote("Add your OpenAI API key in Settings to enable live listening. Manual questions still work with prepared answers."); return; }
        StartAudioPreview();
        await _engine.StartListeningAsync();
        IsListening = true; IsPaused = false;
    }

    public async Task StopAsync()
    {
        if (_engine == null) return;
        await _engine.StopAsync();
        IsListening = false; IsPaused = false;
        LiveTranscript = "";
        if (Settings.SaveSessionTranscript) SaveSession();
    }

    public async Task SubmitManualAsync(string text)
    {
        if (_engine == null || string.IsNullOrWhiteSpace(text)) return;
        await _engine.SubmitManualQuestionAsync(text);
    }

    private void Variant(AnswerStyle style) => _ = _engine?.RequestVariantAsync(style);

    private void Navigate(int delta)
    {
        if (_engine == null) return;
        var h = _engine.History;
        var idx = Math.Clamp(HistoryPosition + delta, 0, h.Count - 1);
        if (idx < 0 || idx >= h.Count) return;
        HistoryPosition = idx;
        ShowAnswer(h[idx], isNew: false);
    }

    private void ShowAnswer(AnswerView a, bool isNew)
    {
        _displayedAnswerId = a.Id;
        Question = a.Question;
        Bullets.Clear();
        SyncBullets(a);
        Pending = "";
        Note = a.Note;
        AnswerMeta = Meta(a);
        if (isNew && _engine != null) HistoryPosition = _engine.History.Count - 1;
        Raise(nameof(HistoryLabel));
        Raise(nameof(IsThinking));
    }

    /// <summary>
    /// Idempotent, append-only render: shows bullets of the answer not yet on screen. Safe however many times
    /// AnswerStarted/BulletAdded notifications arrive (prevents duplicate bullets).
    /// </summary>
    private void SyncBullets(AnswerView a)
    {
        var snapshot = a.SnapshotBullets();
        for (int i = Bullets.Count; i < snapshot.Count; i++) Bullets.Add(new BulletVm { Text = snapshot[i], IsParagraph = a.Style == AnswerStyle.Full });
    }

    private string Meta(AnswerView a)
    {
        var style = a.Style == AnswerStyle.Balanced ? "" : a.Style + " · ";
        var src = a.Source switch { AnswerSource.Cache => "prepared", AnswerSource.CacheFallback => "prepared (fallback)", AnswerSource.Llm => "AI", _ => "" };
        return Settings.ShowDiagnostics ? $"{style}{src} · {a.Mode.ToString().ToLowerInvariant()} · {a.Category.ToLowerInvariant()} · match {a.MatchScore:0.00}" : style.TrimEnd(' ', '·');
    }

    private void ShowLocalNote(string text) { Note = text; }

    private void ApplyStatus(EngineStatus s, string? detail)
    {
        var res = Application.Current.Resources;
        (string text, string brush, bool pulse) = s switch
        {
            EngineStatus.Listening => ("LISTENING", "B.Listening", true),
            EngineStatus.SpeechDetected => ("SPEECH DETECTED", "B.Listening", true),
            EngineStatus.Finalizing => ("FINALIZING QUESTION", "B.Thinking", true),
            EngineStatus.Answering => ("ANSWERING", "B.Accent", true),
            EngineStatus.Paused => ("PAUSED", "B.TextFaint", false),
            EngineStatus.Reconnecting => ("RECONNECTING", "B.Thinking", true),
            EngineStatus.NoAudio => ("NO AUDIO", "B.Thinking", false),
            EngineStatus.ApiError => ("API ERROR", "B.Error", false),
            EngineStatus.Stopped => ("STOPPED", "B.TextFaint", false),
            _ => ("READY", "B.TextDim", false),
        };
        StatusText = text;
        StatusBrush = (Brush)res[brush];
        StatusPulse = pulse;
        StatusDetail = detail;
        if (s == EngineStatus.ApiError && _engine != null && _engine.IsListening == false) IsListening = false;
    }

    // ---------------- diagnostics ----------------

    private void RefreshDiagnostics()
    {
        if (!Settings.ShowDiagnostics || _engine == null) return;
        var m = _engine.Metrics;
        var proc = Process.GetCurrentProcess();
        var cpuNow = proc.TotalProcessorTime;
        var cpu = (cpuNow - _lastCpu).TotalMilliseconds / Math.Max(1, (DateTime.UtcNow - _lastCpuAt).TotalMilliseconds) / Environment.ProcessorCount * 100;
        _lastCpu = cpuNow; _lastCpuAt = DateTime.UtcNow;
        var s = m.Samples;
        string P(Func<LatencySample, long> f) => $"p50 {SessionMetrics.Percentile(s.Select(f), 50)} / p95 {SessionMetrics.Percentile(s.Select(f), 95)} ms";
        var last = s.LastOrDefault();
        var sb = new StringBuilder();
        sb.AppendLine($"Audio: {_capture.CurrentDeviceName} [{AudioStatus}]  sent {m.AudioMsSent / 1000.0:0}s  transcriber {_transcriber?.Status}  reconnects {m.Reconnects}");
        sb.AppendLine($"Questions {m.Questions}  prepared {m.CacheAnswers}  AI {m.LlmAnswers}  fallback {m.FallbackAnswers}  dupes suppressed {m.DuplicatesSuppressed}  API errors {m.ApiErrors}  LLM requests {m.LlmRequests}");
        if (last != null) sb.AppendLine($"Last: speech end→question {last.SpeechEndToFinalizedMs} ms · match {last.MatchMs} ms · first token {last.FinalizedToFirstTokenMs} ms · first bullet {last.FinalizedToFirstBulletMs} ms · complete {last.FinalizedToCompleteMs} ms · speech end→first bullet {last.SpeechEndToFirstBulletMs} ms");
        if (s.Count > 1) sb.AppendLine($"Finalize {P(x => x.SpeechEndToFinalizedMs)} · first bullet {P(x => x.FinalizedToFirstBulletMs)} · end-to-answer {P(x => x.SpeechEndToFirstBulletMs)}");
        sb.AppendLine($"CPU {cpu:0.0}%  memory {proc.WorkingSet64 / 1048576} MB  managed {GC.GetTotalMemory(false) / 1048576} MB  est. cost ${m.EstimatedCostUsd(Settings.PriceTranscribePerMinute, Settings.PriceInputPerMTok, Settings.PriceOutputPerMTok):0.000}");
        foreach (var line in AppLog.Recent.TakeLast(4)) sb.AppendLine(line);
        Diagnostics = sb.ToString().TrimEnd();
    }

    public void RefreshFonts() { Raise(nameof(AnswerFontSize)); Raise(nameof(QuestionFontSize)); }

    private void SaveSession()
    {
        if (_engine == null) return;
        try
        {
            var sb = new StringBuilder($"# Interview session {DateTime.Now:yyyy-MM-dd HH:mm}\n\n");
            foreach (var a in _engine.History) { sb.AppendLine($"## {a.Question}"); foreach (var b in a.Bullets) sb.AppendLine("- " + b); sb.AppendLine(); }
            File.WriteAllText(Path.Combine(AppPaths.Sessions, $"session-{DateTime.Now:yyyyMMdd-HHmm}.md"), sb.ToString());
        }
        catch (IOException ex) { AppLog.Error("Session save failed", ex); }
    }

    public IAnswerProvider? Provider => _provider;
    public InterviewEngine? Engine => _engine;

    public async ValueTask DisposeAsync()
    {
        _diagTimer.Stop();
        _capture.Dispose();
        if (_engine != null) await _engine.DisposeAsync();
        _provider?.Dispose();
    }
}
