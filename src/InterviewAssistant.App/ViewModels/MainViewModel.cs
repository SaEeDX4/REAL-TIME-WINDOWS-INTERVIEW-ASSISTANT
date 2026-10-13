using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewAssistant.App.Services;
using InterviewAssistant.Client;
using InterviewAssistant.Contracts;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Live;
using InterviewAssistant.Core.Persistence;
using InterviewAssistant.Core.Reports;
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
/// append-only so text never shifts while the candidate is reading.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _ui;
    public AppSettings Settings { get; }
    public KnowledgeBase? Knowledge { get; private set; }
    private InterviewEngine? _engine;
    private IAnswerProvider? _provider;
    private OpenAiRealtimeTranscriber? _transcriber;
    private RealtimeTranscriberOptions? _transcriberOptions;
    private CloudSession? _cloudSession;
    private DateTime _sessionStartedUtc;
    public CloudService Cloud { get; }
    public UpdateService Updates { get; }
    private readonly DispatcherTimer _updateTimer;
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
        Cloud = new CloudService(Settings);
        Updates = new UpdateService(Settings, Cloud.Options.UpdateUrl);
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += async (_, _) => await Updates.TickAsync(sessionActive: IsListening || IsPaused || _cloudSession != null);
        _updateTimer.Start();
        Cloud.Changed += () => Post(() => { Raise(nameof(AccountLabel)); Raise(nameof(UsingCloud)); Raise(nameof(MinutesLeftLabel)); Raise(nameof(ServiceBanner)); });
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
        ToggleCoachCommand = new RelayCommand(ToggleCoach);
        ResetAdaptiveCommand = new RelayCommand(ResetAdaptive);
    }

    public ICommand ToggleCoachCommand { get; }
    public ICommand ResetAdaptiveCommand { get; }

    // ---------------- localisation ----------------

    /// <summary>Effective UI language: setting, else Windows display language if supported, else English.</summary>
    public string UiLanguage => LanguageRegistry.Find(Settings.UiLanguage)?.Code ?? LanguageRegistry.Find(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)?.Code ?? "en";
    public string L(string key) => UiStrings.Get(UiLanguage, key);
    public FlowDirection UiFlowDirection => LanguageRegistry.IsRtl(UiLanguage) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public string LQuestion => L("question");
    public string LAnswer => IsCoachView ? L("coach") : L("answer");
    public string LWaiting => L("waiting");
    public string LTypeHint => L("type_hint");
    public string LShorter => L("shorter");
    public string LTechnical => L("technical");
    public string LExample => L("example");
    public string LFull => L("full");
    public string LKeywords => L("keywords");
    public string LStructure => L("structure");
    public void RefreshLanguage()
    {
        foreach (var n in new[] { nameof(UiLanguage), nameof(UiFlowDirection), nameof(LQuestion), nameof(LAnswer), nameof(LWaiting), nameof(LTypeHint), nameof(LShorter),
                     nameof(LTechnical), nameof(LExample), nameof(LFull), nameof(LKeywords), nameof(LStructure), nameof(StartPauseLabel), nameof(AdaptiveLabel), nameof(PresentationLabel) }) Raise(n);
        if (_engine != null) ApplyStatus(_engine.Status, _engine.StatusDetail);
    }

    // ---------------- coach / adaptive ----------------

    private bool _isCoachView; public bool IsCoachView { get => _isCoachView; private set { if (Set(ref _isCoachView, value)) Raise(nameof(LAnswer)); } }
    private string _coachKeywords = ""; public string CoachKeywords { get => _coachKeywords; private set => Set(ref _coachKeywords, value); }
    private string _coachStructure = ""; public string CoachStructure { get => _coachStructure; private set => Set(ref _coachStructure, value); }
    private string? _coachReminder; public string? CoachReminder { get => _coachReminder; private set => Set(ref _coachReminder, value); }
    private FlowDirection _answerFlow = FlowDirection.LeftToRight; public FlowDirection AnswerFlowDirection { get => _answerFlow; private set => Set(ref _answerFlow, value); }
    private AdaptiveLevel _adaptive; public AdaptiveLevel Adaptive { get => _adaptive; private set { if (Set(ref _adaptive, value)) Raise(nameof(AdaptiveLabel)); } }
    public string AdaptiveLabel => Adaptive switch { AdaptiveLevel.Concise => L("adaptive_concise"), AdaptiveLevel.Rapid => L("adaptive_rapid"), _ => "" };
    public Presentation Presentation => _engine?.Presentation ?? Presentation.Answer;
    public string PresentationLabel => Presentation == Presentation.Coach ? L("coach") : L("answer");

    /// <summary>Ctrl+Alt+C: next question is shown as Coach (3 keywords + structure) or full Answer bullets.</summary>
    public void ToggleCoach()
    {
        if (_engine == null) return;
        _engine.TogglePresentation();
        Raise(nameof(Presentation)); Raise(nameof(PresentationLabel));
        AppLog.Info("Presentation: " + _engine.Presentation);
    }

    /// <summary>Ctrl+Alt+R: back to normal answer length after the interviewer slows down.</summary>
    public void ResetAdaptive() { _engine?.ResetAdaptive(); Adaptive = AdaptiveLevel.Normal; }

    // ---------------- account ----------------

    /// <summary>Customer builds always use the service; only developer builds can switch to the own-key prototype path.</summary>
    public bool UsingCloud => !BuildFlavor.Developer || (Settings.Mode == "cloud" && Cloud.IsConfigured);
    public string AccountLabel => Cloud.State switch
    {
        CloudState.SignedIn when Cloud.Account is { } a => $"{a.Email} · {BillingLabel(a)}",
        CloudState.NotConfigured => BuildFlavor.Developer ? "Developer mode" : "Service not configured",
        CloudState.Connecting => "Connecting…",
        CloudState.ServiceUnavailable => "Service unavailable",
        CloudState.UpdateRequired => "Update required",
        CloudState.SessionExpired => "Session expired — sign in",
        CloudState.AccountDisabled => "Account disabled",
        _ => "Not signed in",
    };

    /// <summary>One-line plan text from the server's billing state (no entitlement logic here).</summary>
    public static string BillingLabel(AccountDto a) => a.BillingState switch
    {
        Contracts.BillingStates.Trial => $"Trial · {a.Usage.LiveSecondsRemaining / 60} min left",
        Contracts.BillingStates.TrialExhausted => "Trial used up",
        Contracts.BillingStates.Active => a.Entitlements.PlanName,
        Contracts.BillingStates.Canceling => $"{a.Entitlements.PlanName} · ends {a.Subscription?.CurrentPeriodEndUtc?.ToLocalTime():d}",
        Contracts.BillingStates.PastDue => $"{a.Entitlements.PlanName} · payment problem",
        Contracts.BillingStates.Paused => "Subscription paused",
        Contracts.BillingStates.Expired => "Subscription ended",
        _ => a.Entitlements.PlanName,
    };

    /// <summary>Overlay banner for states that block live help (empty when everything is fine).</summary>
    public string ServiceBanner => !UsingCloud ? "" : Cloud.State switch
    {
        CloudState.ServiceUnavailable or CloudState.UpdateRequired or CloudState.AccountDisabled or CloudState.SessionExpired or CloudState.NotConfigured => Cloud.Message ?? AccountLabel,
        CloudState.SignedIn when Cloud.Account?.BillingState is Contracts.BillingStates.TrialExhausted or Contracts.BillingStates.Expired or Contracts.BillingStates.Paused =>
            BillingLabel(Cloud.Account!) + " — upgrade or manage billing in Home → Account.",
        _ => "",
    };
    private int? _remainingSeconds; public string MinutesLeftLabel => UsingCloud && (_remainingSeconds ?? Cloud.Account?.Usage.LiveSecondsRemaining) is int r ? UiStrings.Format(UiLanguage, "minutes_left", r / 60) : "";

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
    public string StartPauseLabel => IsListening ? L("pause") : IsPaused ? L("resume") : L("start");
    private string _diagnostics = ""; public string Diagnostics { get => _diagnostics; set => Set(ref _diagnostics, value); }
    private int _historyPosition = -1; public int HistoryPosition { get => _historyPosition; set { if (Set(ref _historyPosition, value)) Raise(nameof(HistoryLabel)); } }
    public string HistoryLabel => _engine == null || _engine.History.Count == 0 ? "" : $"{HistoryPosition + 1} / {_engine.History.Count}";
    private double? _fitFontSize;
    /// <summary>Base size from the font preset, reduced by auto-fit (never below 70 %) so every bullet is visible without scrolling.</summary>
    public double AnswerFontSize => _fitFontSize ?? Settings.AnswerFontSize;
    public double CoachFontSize => Math.Round(AnswerFontSize * 1.18);
    public double BaseAnswerFontSize => Settings.AnswerFontSize;
    public double MinAnswerFontSize => Math.Max(13, Math.Round(Settings.AnswerFontSize * 0.7));
    private bool _compactSpacing;
    /// <summary>First auto-fit step: tighter bullet spacing and line height (still readable) before any font reduction.</summary>
    public bool CompactSpacing { get => _compactSpacing; set { if (Set(ref _compactSpacing, value)) { Raise(nameof(BulletMargin)); Raise(nameof(AnswerLineHeight)); } } }
    public Thickness BulletMargin => new(0, 0, 0, CompactSpacing ? 6 : 14);
    public double AnswerLineHeight => Math.Round(AnswerFontSize * (CompactSpacing ? 1.24 : 1.42), 1);

    public void SetFitFontSize(double? size)
    {
        var v = size is double d ? Math.Clamp(Math.Round(d * 2) / 2, MinAnswerFontSize, BaseAnswerFontSize) : (double?)null;
        if (v == BaseAnswerFontSize) v = null;
        if (v == _fitFontSize) return;
        _fitFontSize = v;
        Raise(nameof(AnswerFontSize)); Raise(nameof(CoachFontSize)); Raise(nameof(AnswerLineHeight));
    }
    public double QuestionFontSize => Settings.QuestionFontSize;
    public bool IsThinking => _engine?.Current is { IsComplete: false } && Bullets.Count == 0;

    // ---------------- composition ----------------

    public Workspace? Workspace { get; private set; }
    private string _activeLabel = ""; public string ActiveLabel { get => _activeLabel; set => Set(ref _activeLabel, value); }

    public string? Initialize()
    {
        try
        {
            Workspace = new Workspace(Path.Combine(AppPaths.Roaming, "workspace"), new DpapiProtector());
            var (kb, label) = Workspace.LoadActive(Settings.ActiveProfileId, Settings.ActiveTargetId);
            UseKnowledge(kb, label);
            if (Cloud.IsConfigured)
            {
                _ = Cloud.RefreshAsync(CancellationToken.None);
                // Keep plan/billing state current (e.g. after a renewal or a cancellation in the portal) and recover after network loss.
                _accountTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMinutes(10) };
                _accountTimer.Tick += (_, _) => { if (!_toggleBusy) _ = Cloud.RefreshAsync(CancellationToken.None); };
                _accountTimer.Start();
                System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) => { if (e.IsAvailable) Post(() => _ = Cloud.RefreshAsync(CancellationToken.None)); };
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            AppLog.Error("Workspace load failed", ex);
            UseKnowledge(KnowledgeBase.Empty(), "No interview prepared");
            return "Your local workspace could not be opened: " + ex.Message;
        }
        return null;
    }

    /// <summary>Switches the live engine to a profile/target pack (only while not listening).</summary>
    public void UseKnowledge(KnowledgeBase kb, string label)
    {
        Knowledge = kb;
        ActiveLabel = label;
        BuildEngine();
        AppLog.Info($"Knowledge loaded: {kb.Questions.Count} prepared questions, {kb.Stories.Count} stories, {kb.Snippets.Count} snippets ({(kb.Questions.Count == 0 ? "generic" : "target pack")})");
    }

    /// <summary>Loads the built-in sample profile (fixture) — explicit user action or self-test only.</summary>
    public bool LoadSample(string? dir)
    {
        var s = Workspace?.LoadSample(dir);
        if (s == null) return false;
        UseKnowledge(s.Value.Kb, s.Value.Label);
        return true;
    }

    /// <summary>(Re)creates providers + engine from current settings. Safe to call after Settings change while stopped.</summary>
    public void BuildEngine()
    {
        if (Knowledge == null) return;
        if (_engine != null) _ = _engine.DisposeAsync().AsTask(); // releases the previous transcriber connection
        (_provider as IDisposable)?.Dispose();
        _transcriberOptions = TranscriberOptions();
        if (UsingCloud && Cloud.Api != null)
        {
            // Cloud: the server picks the models and holds the provider key; the transcriber only ever sees short-lived secrets.
            // Live lease while listening; the lease-free "manual" purpose for typed questions while paused or stopped.
            _provider = new CloudAnswerProvider(Cloud.Api!, () => _cloudSession is { Active: true } s ? s.Id : null, deviceId: () => Settings.CloudDeviceId);
            _transcriberOptions.ServerConfiguredSession = true;   // the server mints the session config with each secret
            _transcriber = new OpenAiRealtimeTranscriber(ct => _cloudSession is { Active: true } s ? s.NextConnectionSecretAsync(ct) : Task.FromResult<string?>(null), _transcriberOptions);
        }
        else
        {
            _provider = new OpenAiChatAnswerProvider(SecretStore.GetApiKey, ChatOptions());
            _transcriber = new OpenAiRealtimeTranscriber(SecretStore.GetApiKey, _transcriberOptions);
        }
        _transcriber.Diagnostic += m => AppLog.Info(m);
        _engine = new InterviewEngine(Knowledge, _transcriber, _provider, new EngineOptions { UseFastCache = Settings.UseFastCache, DeveloperKeyMode = !UsingCloud });
        if (Settings.AnswerLanguage != "same" && LanguageRegistry.IsSupported(Settings.AnswerLanguage)) _engine.AnswerLanguageOverride = Settings.AnswerLanguage;
        if (Settings.StartInCoachMode) _engine.Presentation = Presentation.Coach;
        _engine.AdaptiveChanged += lvl => Post(() => Adaptive = lvl);
        _engine.Turn.Sensitivity = Settings.VadSensitivity;
        _engine.StatusChanged += (s, d) => Post(() => ApplyStatus(s, d));
        _engine.LiveTranscriptChanged += t => Post(() => LiveTranscript = t);
        _engine.AnswerStarted += a => Post(() => ShowAnswer(a, isNew: true));
        _engine.BulletAdded += (a, _) => Post(() => { if (a.Id == _displayedAnswerId) { SyncBullets(a); Pending = ""; Raise(nameof(IsThinking)); } });
        _engine.PendingTextChanged += (a, p) => Post(() => { if (a.Id == _displayedAnswerId) Pending = p; });
        _engine.AnswerCompleted += a => Post(() => { if (a.Id == _displayedAnswerId) { SyncBullets(a); Pending = ""; Note = a.Note; AnswerMeta = Meta(a); Raise(nameof(IsThinking)); } LiveTranscript = ""; });
        _engine.LatencyMeasured += s => AppLog.Info($"Latency [{s.Source}] finalize {s.SpeechEndToFinalizedMs} ms, first bullet {s.FinalizedToFirstBulletMs} ms, total {s.FinalizedToCompleteMs} ms");
        _engine.Log += m => AppLog.Info(m);
        ApplyStatus(EngineStatus.Ready, null);
        Raise(nameof(Presentation)); Raise(nameof(PresentationLabel));
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

    public RealtimeTranscriberOptions TranscriberOptions() => new()
    {
        Model = Settings.TranscriptionModel, Protocol = Settings.RealtimeProtocol,
        Prompt = Knowledge?.Context.BuildTranscriptionPrompt() ?? "Job interview.",
        Language = Knowledge?.Context.InterviewLanguage is { } l && l != "auto" ? l : "",
    };
    public ChatProviderOptions ChatOptions() => new() { Model = Settings.AnswerModel };

    /// <summary>
    /// Live transcription test through the SAME capture service, converter, framer and OpenAiRealtimeTranscriber class
    /// used in interviews (separate instance so it never triggers answers).
    /// </summary>
    public async Task<string> TestTranscriptionAsync(Action<string> onTranscript, Action<string> onStatus, TimeSpan duration, CancellationToken ct)
    {
        StartAudioPreview();
        await using var lease = UsingCloud ? await StartLeaseAsync(ct) : null;
        var opts = TranscriberOptions();
        if (lease != null) { opts.Endpoint = lease.Credential.WebSocketUrl; opts.ServerConfiguredSession = true; }
        await using var t = lease != null
            ? new OpenAiRealtimeTranscriber(c => lease.NextConnectionSecretAsync(c), opts)
            : new OpenAiRealtimeTranscriber(SecretStore.GetApiKey, opts);
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
        if (UsingCloud)
        {
            // The real server path (proxy, entitlements, server model) as a typed question: no live lease, no live minutes.
            var cloudEngine = new InterviewEngine(Knowledge, null, new CloudAnswerProvider(Cloud.Api!, () => null, deviceId: () => Settings.CloudDeviceId) { Purpose = AnswerPurposes.Manual },
                new EngineOptions { AutoTick = false, UseFastCache = false, DeveloperKeyMode = false });
            cloudEngine.BulletAdded += (_, b) => Post(() => onBullet(b));
            await cloudEngine.SubmitManualQuestionAsync(question);
            return (cloudEngine.Current!, cloudEngine.Metrics.Samples.LastOrDefault(), "cloud: " + (Cloud.Config?.AnswerModel ?? "server model"));
        }
        using var provider = new OpenAiChatAnswerProvider(SecretStore.GetApiKey, ChatOptions());
        var engine = new InterviewEngine(Knowledge, null, provider, new EngineOptions { AutoTick = false, UseFastCache = false });
        engine.BulletAdded += (_, b) => Post(() => onBullet(b));
        await engine.SubmitManualQuestionAsync(question);
        return (engine.Current!, engine.Metrics.Samples.LastOrDefault(), provider.ActiveModel);
    }

    // ---------------- controls ----------------

    private bool _toggleBusy;
    private DispatcherTimer? _accountTimer;

    /// <summary>Start / pause. Re-entrancy guarded: a double click can never start two leases.</summary>
    public async Task ToggleListeningAsync()
    {
        if (_engine == null || _toggleBusy) return;
        _toggleBusy = true;
        try { await ToggleListeningCoreAsync(); }
        finally { _toggleBusy = false; }
    }

    private async Task ToggleListeningCoreAsync()
    {
        if (_engine == null) return;
        if (IsListening)
        {
            _engine.Pause(); IsListening = false; IsPaused = true;
            await EndLeaseAsync();   // paused time is never billed
            return;
        }
        if (UsingCloud)
        {
            if (!Cloud.IsConfigured) { ShowLocalNote("Live help needs the Interview Assistant service, which is not configured in this build. Typed questions still work with prepared answers."); return; }
            if (!Cloud.IsSignedIn) { ShowLocalNote("Sign in (Home → Account) to start live listening. Typed questions still work with prepared answers."); return; }
            if (!await BeginLeaseAsync()) return;
        }
        else if (!SecretStore.HasKey) { ShowLocalNote("Developer build: add your OpenAI API key in Settings to enable live listening. Typed questions still work with prepared answers."); return; }
        StartAudioPreview();
        if (_sessionStartedUtc == default) _sessionStartedUtc = DateTime.UtcNow;
        await _engine.StartListeningAsync();
        IsListening = true; IsPaused = false;
    }

    public async Task StopAsync()
    {
        if (_engine == null) return;
        await _engine.StopAsync();
        await EndLeaseAsync();
        IsListening = false; IsPaused = false;
        LiveTranscript = "";
        if (Settings.SaveSessionTranscript) SaveSession();
        LastReportPath = SaveReport();
    }

    /// <summary>Starts the server lease for live listening and wires its events. False (with a readable note) when refused.</summary>
    private async Task<bool> BeginLeaseAsync()
    {
        try
        {
            _cloudSession = await StartLeaseAsync(CancellationToken.None);
            _transcriberOptions!.Endpoint = _cloudSession.Credential.WebSocketUrl;
            var session = _cloudSession;
            session.RemainingChanged += r => Post(() => { _remainingSeconds = r; Raise(nameof(MinutesLeftLabel)); });
            session.Warning += w => Post(() => Note = w);
            session.Ended += reason => Post(async () => { if (session == _cloudSession) await OnLeaseEndedAsync(reason); });
            _remainingSeconds = session.RemainingSeconds; Raise(nameof(MinutesLeftLabel));
            return true;
        }
        catch (BackendException ex)
        {
            ShowLocalNote(ex.Code switch
            {
                "allowance_exhausted" => "You have used all live minutes on your plan for this period. Upgrade in Home → Account. Typed questions still work.",
                "daily_live_cap" => "Today's live-minute limit is reached (it resets at 00:00 UTC). Typed questions still work.",
                "concurrent_limit" => "An interview session is already running for your account on another PC. Stop it there first.",
                "device_revoked" or "device_limit" => "This PC is not registered for your account (device limit). Manage devices in Home → Account.",
                "maintenance" => ex.Message,
                _ => CloudService.Describe(ex) + " Typed questions still work with prepared answers.",
            });
            if (ex.Status is 0 or >= 500) _ = Cloud.RefreshAsync(CancellationToken.None);
            return false;
        }
    }

    private async Task<CloudSession> StartLeaseAsync(CancellationToken ct)
    {
        var device = Settings.CloudDeviceId ?? await Cloud.EnsureDeviceAsync(ct);
        var language = Knowledge?.Context.InterviewLanguage is { } l && l != "auto" ? l : null;
        var target = await PublishTranscriptionContextAsync(ct);
        try { return await CloudSession.StartAsync(Cloud.Api!, device, target, language, ct); }
        catch (BackendException ex) when (ex.Code == "device_unknown")
        {
            device = await Cloud.EnsureDeviceAsync(ct);   // local id stale (e.g. reinstall) → re-register once
            return await CloudSession.StartAsync(Cloud.Api!, device, target, language, ct);
        }
    }

    /// <summary>
    /// Sends the active interview's transcription context (job title, company, names and terms — no résumé or job
    /// description text) as the user's own target document, so the server can bias recognition toward e.g. product
    /// names. Returns the target id for the lease, or null when no interview is prepared or the upload failed.
    /// </summary>
    private async Task<Guid?> PublishTranscriptionContextAsync(CancellationToken ct)
    {
        if (Knowledge == null || !Guid.TryParse(Settings.ActiveTargetId, out var targetId)) return null;
        var c = Knowledge.Context;
        var doc = new TranscriptionContextDocument(c.RoleTitle == "the role" ? "" : c.RoleTitle, c.CompanyName, c.TranscriptionTerms(), c.InterviewLanguage);
        try { await Cloud.Api!.PutTranscriptionContextAsync(targetId, doc, ct); return targetId; }
        catch (BackendException ex) when (ex.Status is not 0 and not 401)
        {
            AppLog.Warn("Interview vocabulary could not be sent (" + ex.Code + "); live transcription uses generic vocabulary.");
            return null;
        }
    }

    private async Task EndLeaseAsync()
    {
        var s = _cloudSession;
        _cloudSession = null;
        if (s != null) await s.DisposeAsync();
    }

    private async Task OnLeaseEndedAsync(string reason)
    {
        AppLog.Info("Lease ended: " + reason);
        _cloudSession = null;
        // Network loss, sleep/wake or a crash-replaced lease: the user still wants help — start a new lease once, silently.
        if (IsListening && reason is "heartbeat_timeout" or "session_ended" or "expired" or "not_found" && await BeginLeaseAsync())
        {
            AppLog.Info("Lease restarted automatically");
            return;
        }
        if (_engine != null && (IsListening || IsPaused)) { _engine.Pause(); IsListening = false; IsPaused = true; }
        Note = reason switch
        {
            "allowance_exhausted" => "Live minutes used up — listening paused. Prepared answers and manual questions still work.",
            "max_duration" => "Maximum session length reached — press Resume to continue.",
            "daily_cap" => "Daily live limit reached — listening paused.",
            "account_disabled" => "This account has been disabled — listening stopped.",
            "device_revoked" => "This PC was removed from your account — listening stopped.",
            _ => "The live session ended. Press Resume to continue.",
        };
        await Cloud.RefreshAsync(CancellationToken.None);
    }

    private string? _lastReportPath; public string? LastReportPath { get => _lastReportPath; private set => Set(ref _lastReportPath, value); }

    /// <summary>Post-interview report (honesty-labelled), saved encrypted in the workspace and as a local HTML file.</summary>
    public string? SaveReport()
    {
        if (_engine == null || Knowledge == null || _engine.Memory.Questions.Count == 0) return null;
        try
        {
            var id = Guid.NewGuid().ToString("n");
            var lang = LanguageRegistry.Find(Settings.ReportLanguage)?.Code ?? UiLanguage;
            var started = _sessionStartedUtc == default ? DateTime.UtcNow : _sessionStartedUtc;
            var pack = Settings.ActiveTargetId != null ? Workspace?.Store.GetPack(Settings.ActiveTargetId) : null;
            var report = ReportBuilder.Build(_engine.Memory, Knowledge, _engine.Metrics, id, started, DateTime.UtcNow, lang, pack,
                _engine.Metrics.Reconnects, _engine.Metrics.DuplicatesSuppressed, Knowledge.Context.CandidateName, ActiveLabel);
            Workspace?.Store.SaveSession(new StoredSession { Id = id, ProfileId = Settings.ActiveProfileId ?? "", TargetId = Settings.ActiveTargetId ?? "", StartedUtc = started, EndedUtc = DateTime.UtcNow, ReportJson = report.ToJson() });
            var path = Path.Combine(AppPaths.Sessions, $"report-{DateTime.Now:yyyyMMdd-HHmm}.html");
            File.WriteAllText(path, HtmlReportRenderer.Render(report, Branding.ProductName));
            _sessionStartedUtc = default;
            AppLog.Info($"Report saved ({report.Questions.Count} questions)");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AppLog.Error("Report save failed", ex);
            return null;
        }
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
        AnswerFlowDirection = LanguageRegistry.IsRtl(a.AnswerLanguage) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
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
        if (a.Coach is { } c)
        {
            IsCoachView = true;
            CoachKeywords = c.KeywordLine;
            CoachStructure = c.Structure;
            CoachReminder = c.Reminder;
            BulletsChanged?.Invoke();
            return;
        }
        IsCoachView = a.Presentation == Presentation.Coach;   // coach card stays visible while keywords are generated
        CoachKeywords = ""; CoachStructure = ""; CoachReminder = null;
        var snapshot = a.SnapshotBullets();
        for (int i = Bullets.Count; i < snapshot.Count; i++) Bullets.Add(new BulletVm { Text = snapshot[i], IsParagraph = a.Style == AnswerStyle.Full });
        BulletsChanged?.Invoke();
    }

    /// <summary>Raised after bullets/coach content change (the window re-fits text to avoid scrolling).</summary>
    public event Action? BulletsChanged;

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
        (string text, string brush, bool pulse, string? sub) = s switch
        {
            EngineStatus.Listening => (L("status_listening"), "B.Listening", true, L("detail_listening")),
            EngineStatus.Quiet => (L("status_quiet"), "B.Listening", false, L("detail_listening")),
            EngineStatus.SpeechDetected => (L("status_speech"), "B.Listening", true, null),
            EngineStatus.Finalizing => (L("status_finalizing"), "B.Thinking", true, null),
            EngineStatus.Answering => (L("status_answering"), "B.Accent", true, null),
            EngineStatus.AnswerReady => (L("status_answer_ready"), "B.Listening", false, L("detail_listening")),
            EngineStatus.Paused => (L("status_paused"), "B.TextFaint", false, L("detail_paused")),
            EngineStatus.Reconnecting => (L("status_reconnecting"), "B.Thinking", true, null),
            EngineStatus.NoAudio => (L("status_no_audio"), "B.Thinking", false, null),
            EngineStatus.ApiError => (L("status_api_error"), "B.Error", false, null),
            EngineStatus.Stopped => (L("status_stopped"), "B.TextFaint", false, null),
            _ => (L("status_ready"), "B.TextDim", false, null),
        };
        StatusText = text;
        StatusBrush = (Brush)res[brush];
        StatusPulse = pulse;
        // Engine detail (e.g. which device to check) wins; otherwise a calm explanation of the state.
        StatusDetail = detail ?? sub;
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

    public void RefreshFonts() { _fitFontSize = null; Raise(nameof(AnswerFontSize)); Raise(nameof(CoachFontSize)); Raise(nameof(AnswerLineHeight)); Raise(nameof(QuestionFontSize)); BulletsChanged?.Invoke(); }

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
        _updateTimer.Stop();
        _accountTimer?.Stop();
        _capture.Dispose();
        if (_engine != null) await _engine.DisposeAsync();
        await EndLeaseAsync();
        (_provider as IDisposable)?.Dispose();
    }
}
