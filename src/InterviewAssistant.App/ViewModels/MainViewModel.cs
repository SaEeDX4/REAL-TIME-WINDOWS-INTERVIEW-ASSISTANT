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
    /// <summary>Meaningful action results for the in-app notification area (never per click).</summary>
    public event Action<string, Views.ToastKind, string?>? Notify;
    public void Toast(string text, Views.ToastKind kind = Views.ToastKind.Info, string? detail = null) => Notify?.Invoke(text, kind, detail);
    /// <summary>A report was saved (Home refreshes its Reports list immediately).</summary>
    public event Action<string>? ReportSaved;

    private bool _showLiveIntro; public bool ShowLiveIntro { get => _showLiveIntro; set => Set(ref _showLiveIntro, value); }
    public void DismissLiveIntro(bool dontShowAgain)
    {
        ShowLiveIntro = false;
        if (dontShowAgain) { Settings.LiveIntroDismissed = true; Settings.Save(); }
    }

    public MainViewModel(Dispatcher ui)
    {
        _ui = ui;
        Settings = AppSettings.Load();
        Loc.Language = UiLanguage;
        Cloud = new CloudService(Settings);
        Updates = new UpdateService(Settings, Cloud.Options.UpdateUrl);
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += async (_, _) => await Updates.TickAsync(sessionActive: IsListening || IsPaused || _cloudSession != null);
        _updateTimer.Start();
        Cloud.Changed += () => Post(() => { Raise(nameof(AccountLabel)); Raise(nameof(UsingCloud)); RaiseMinutes(); Raise(nameof(ServiceBanner)); OnConnectivity(); OpenWorkspaceForCurrentAccount(); });
        Loc.LanguageChanged += () => Post(RefreshLanguage);
        _capture.FrameReady += OnAudioFrame;
        _capture.StatusChanged += s => Post(() => OnCaptureStatus(s));
        _diagTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _diagTimer.Tick += (_, _) => RefreshDiagnostics();
        _diagTimer.Start();

        StartPauseCommand = new RelayCommand(async () => await ToggleListeningAsync());
        StopCommand = new RelayCommand(async () => await StopAsync(), () => IsListening || IsPaused);
        ShorterCommand = new RelayCommand(() => Variant(AnswerStyle.VeryShort), CanRework);
        TechnicalCommand = new RelayCommand(() => Variant(AnswerStyle.Technical), CanRework);
        ExampleCommand = new RelayCommand(() => Variant(AnswerStyle.Example), CanRework);
        FullCommand = new RelayCommand(() => Variant(AnswerStyle.Full), CanRework);
        RegenerateCommand = new RelayCommand(() => Variant(AnswerStyle.Balanced), CanRework);
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
    public string L(string key) => Loc.T(key);
    public FlowDirection UiFlowDirection => Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
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
    /// <summary>Re-renders every text that is computed in code (XAML-bound {v:Loc} texts refresh by themselves).</summary>
    public void RefreshLanguage()
    {
        Loc.Language = UiLanguage;
        foreach (var n in new[] { nameof(UiLanguage), nameof(UiFlowDirection), nameof(LQuestion), nameof(LAnswer), nameof(LWaiting), nameof(LTypeHint), nameof(LShorter),
                     nameof(LTechnical), nameof(LExample), nameof(LFull), nameof(LKeywords), nameof(LStructure), nameof(StartPauseLabel), nameof(AdaptiveLabel), nameof(PresentationLabel),
                     nameof(AccountLabel), nameof(ServiceBanner), nameof(HistoryLabel), nameof(ActiveLabel) }) Raise(n);
        RaiseMinutes();
        if (_engine != null) ApplyStatus(_engine.Status, _engine.StatusDetail);
        if (_engine?.History.FirstOrDefault(a => a.Id == _displayedAnswerId) is { } shown) AnswerMeta = Meta(shown);
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
        CloudState.SignedIn when Cloud.Account is { } a => Loc.F("acct_signed_in_as", a.Email, BillingLabel(a)),
        CloudState.NotConfigured => Loc.T(BuildFlavor.Developer ? "acct_developer_mode" : "acct_not_configured"),
        CloudState.Connecting => Loc.T("acct_connecting"),
        CloudState.ServiceUnavailable => Loc.T("acct_unavailable"),
        CloudState.UpdateRequired => Loc.T("acct_update_required"),
        CloudState.SessionExpired => Loc.T("acct_session_expired"),
        CloudState.AccountDisabled => Loc.T("acct_disabled"),
        _ => Loc.T("acct_signed_out"),
    };

    /// <summary>One-line plan text from the server's billing state (no entitlement logic here).</summary>
    public static string BillingLabel(AccountDto a) => a.BillingState switch
    {
        Contracts.BillingStates.Trial => Loc.F("bill_trial", a.Usage.LiveSecondsRemaining / 60),
        Contracts.BillingStates.TrialExhausted => Loc.T("bill_trial_used"),
        Contracts.BillingStates.Active => a.Entitlements.PlanName,
        Contracts.BillingStates.Canceling => Loc.F("bill_ends", a.Entitlements.PlanName, a.Subscription?.CurrentPeriodEndUtc?.ToLocalTime().ToString("d", Loc.Culture)),
        Contracts.BillingStates.PastDue => Loc.F("bill_payment_problem", a.Entitlements.PlanName),
        Contracts.BillingStates.Paused => Loc.T("bill_paused"),
        Contracts.BillingStates.Expired => Loc.T("bill_ended"),
        _ => a.Entitlements.PlanName,
    };

    /// <summary>Persistent banner for states that block live help (empty when everything is fine). An outage stays visible until it recovers.</summary>
    public string ServiceBanner => !UsingCloud ? "" : Cloud.State switch
    {
        CloudState.ServiceUnavailable => Loc.T("banner_offline"),
        CloudState.UpdateRequired or CloudState.AccountDisabled or CloudState.SessionExpired or CloudState.NotConfigured => Cloud.Message ?? AccountLabel,
        CloudState.SignedIn when Cloud.Account?.BillingState is Contracts.BillingStates.TrialExhausted or Contracts.BillingStates.Expired or Contracts.BillingStates.Paused =>
            Loc.F("banner_upgrade", BillingLabel(Cloud.Account!)),
        _ => "",
    };

    private bool _wasOffline;

    /// <summary>One toast per connectivity transition (never repeated while the outage lasts; the banner stays).</summary>
    private void OnConnectivity()
    {
        if (!UsingCloud) return;
        var offline = Cloud.State == CloudState.ServiceUnavailable;
        if (offline == _wasOffline) return;
        _wasOffline = offline;
        if (offline) Toast(Loc.T("toast_connection_lost"), Views.ToastKind.Warning, Loc.T("toast_connection_lost_detail"));
        else if (Cloud.State == CloudState.SignedIn) Toast(Loc.T("toast_connection_restored"), Views.ToastKind.Success, Loc.T("toast_connection_restored_detail"));
    }
    private int? _periodRemainingSeconds, _todayRemainingSeconds;

    /// <summary>Plan minutes left this billing period (never confused with the per-day safety limit).</summary>
    private int? PeriodMinutesLeft => (_periodRemainingSeconds ?? Cloud.Account?.Usage.LiveSecondsRemaining) is int s ? s / 60 : null;
    private int? TodayMinutesLeft => (_todayRemainingSeconds ?? (Cloud.Account?.Usage.LiveSecondsAvailableToday is int t && t >= 0 ? t : null)) is int s ? s / 60 : null;
    private int PlanMinutes => Cloud.Account is { } a ? (a.BillingState == Contracts.BillingStates.Trial || a.BillingState == Contracts.BillingStates.TrialExhausted ? a.Entitlements.TrialMinutes : a.Entitlements.LiveMinutesPerPeriod) : 0;

    /// <summary>Compact header label: plan minutes for the period; today's figure is added only when it is the real limit.</summary>
    public string MinutesLeftLabel
    {
        get
        {
            if (!UsingCloud || PeriodMinutesLeft is not int period) return "";
            var label = PlanMinutes > 0 ? Loc.F("min_label", period, PlanMinutes) : Loc.F("min_label_plain", period);
            return TodayMinutesLeft is int today && today < period ? Loc.F("min_today", label, today) : label;
        }
    }

    public string MinutesTooltip
    {
        get
        {
            if (!UsingCloud || PeriodMinutesLeft is not int period) return "";
            var sb = new StringBuilder();
            sb.Append(PlanMinutes > 0 ? Loc.F("min_tt_period", period, PlanMinutes) : Loc.F("min_tt_period_plain", period));
            if (TodayMinutesLeft is int today)
            {
                var cap = Cloud.Account?.Usage.DailyLiveMinutesCap ?? 0;
                sb.Append('\n').Append(Loc.F("min_tt_today", today)).Append(cap > 0 ? " " + Loc.F("min_tt_cap", cap) : ".");
            }
            sb.Append('\n').Append(Loc.T("min_tt_paused"));
            return sb.ToString();
        }
    }

    private void RaiseMinutes() { Raise(nameof(MinutesLeftLabel)); Raise(nameof(MinutesTooltip)); }

    public ICommand StartPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ShorterCommand { get; }
    public ICommand TechnicalCommand { get; }
    public ICommand ExampleCommand { get; }
    public ICommand FullCommand { get; }
    public ICommand RegenerateCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }

    /// <summary>Variants rework the DISPLAYED history item (never "the latest question" implicitly).</summary>
    private bool CanRework() => _provider != null && _engine?.History.Any(a => a.Id == _displayedAnswerId) == true;

    // ---------------- bindable state ----------------
    private string _statusText = Loc.T("status_ready"); public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    private Brush _statusBrush = Brushes.Gray; public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    private bool _statusPulse; public bool StatusPulse { get => _statusPulse; set => Set(ref _statusPulse, value); }
    private string? _statusDetail; public string? StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }
    private string _question = ""; public string Question { get => _question; set { if (Set(ref _question, value)) { Raise(nameof(HasQuestionText)); Raise(nameof(QuestionFlowDirection)); } } }
    /// <summary>The interviewer's words follow their own script (an Arabic/Persian question reads right-to-left even in an English UI).</summary>
    public FlowDirection QuestionFlowDirection => TextFlow(Question);
    public FlowDirection LiveTranscriptFlowDirection => TextFlow(LiveTranscript);

    private static FlowDirection TextFlow(string text)
    {
        int rtl = 0, ltr = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            if (ch is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF') rtl++; else ltr++;
        }
        return rtl > ltr ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }
    public bool HasQuestionText => Question.Length > 0;
    private string _liveTranscript = ""; public string LiveTranscript { get => _liveTranscript; set { if (Set(ref _liveTranscript, value)) Raise(nameof(LiveTranscriptFlowDirection)); } }
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
    public string HistoryLabel => _engine == null || _engine.History.Count == 0 ? "" : Loc.F("history_label", HistoryPosition + 1, _engine.History.Count);
    private string _contextSummary = ""; public string ContextSummary { get => _contextSummary; set => Set(ref _contextSummary, value); }
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
            OpenWorkspaceForCurrentAccount();
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
            UseKnowledge(KnowledgeBase.Empty(), Loc.T("no_interview_prepared"));
            return Loc.F("workspace_open_failed", ex.Message);
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
        var s = Services.Workspace.LoadSample(dir);
        if (s == null) return false;
        UseKnowledge(s.Value.Kb, s.Value.Label);
        return true;
    }

    // ---------------- account-scoped workspace ----------------

    private string? _workspaceOwner;
    /// <summary>The workspace was opened, switched or closed (sign-in, sign-out, account switch).</summary>
    public event Action? WorkspaceChanged;
    public string? ActiveProfileId { get; private set; }
    public string? ActiveTargetId { get; private set; }
    public bool HasWorkspace => Workspace != null;

    /// <summary>Selects the interview used by the Live Assistant (stored per account workspace).</summary>
    public void SetActive(string? profileId, string? targetId)
    {
        ActiveProfileId = profileId; ActiveTargetId = targetId;
        Workspace?.Store.SetActive(profileId, targetId);
    }

    /// <summary>
    /// Opens the workspace of the signed-in account (or the local one in developer mode / self-test). Signed out:
    /// no workspace at all — no profile, interview, report or prepared answer of anybody is loaded or visible.
    /// Never switches during an interview session (sign-out stops the session first).
    /// </summary>
    public void OpenWorkspaceForCurrentAccount()
    {
        string? owner = App.SelfTestOutput != null ? "selftest" : !UsingCloud ? "local" : Cloud.Auth?.Session?.UserId;
        // First call (startup) always initializes — signed out at startup must still get the empty knowledge base and an engine.
        if (_workspaceInitialized && owner == _workspaceOwner && (Workspace != null || owner == null)) return;
        if (SessionActive)
        {
            // A token that expires mid-interview (owner null) keeps the interview running. A DIFFERENT account signing
            // in must not inherit this interview: stop it first (its report is saved to the current account), then switch.
            if (owner != null && _workspaceOwner != null && owner != _workspaceOwner && !_switchPending)
            {
                _switchPending = true;
                Post(async () =>
                {
                    try { await StopAsync(); } finally { _switchPending = false; }
                    OpenWorkspaceForCurrentAccount();
                });
            }
            return;
        }
        _workspaceInitialized = true;
        var baseDir = Path.Combine(AppPaths.Roaming, "workspace");
        Workspace?.ClearViewerCopies();
        ClearUserState();
        if (owner == null)
        {
            Workspace = null; _workspaceOwner = null; ActiveProfileId = ActiveTargetId = null;
            UseKnowledge(KnowledgeBase.Empty(), Loc.T("signed_out_no_data"));
            AppLog.Info("Workspace closed (signed out)");
            WorkspaceChanged?.Invoke();
            return;
        }
        string root;
        if (owner == "selftest") root = Path.Combine(AppPaths.Local, "selftest-workspace-live");
        else if (owner == "local") root = WorkspaceLocator.RootFor(baseDir, null);
        else
        {
            if (WorkspaceLocator.AdoptLegacy(baseDir, owner)) AppLog.Info("Pre-RC2 local workspace adopted by the signed-in account");
            root = WorkspaceLocator.RootFor(baseDir, owner);
        }
        Workspace = new Workspace(root, new DpapiProtector()) { Owner = owner == "local" || owner == "selftest" ? owner : WorkspaceLocator.AccountKey(owner) };
        Workspace.ClearViewerCopies();   // leftovers from a previous run
        Workspace.Store.FileQuarantined += folder => Post(() => Toast(Loc.T("toast_file_quarantined"), Views.ToastKind.Warning, Loc.T("toast_file_quarantined_detail")));
        var (p, t) = Workspace.Store.GetActive();
        if (p == null && Settings.ActiveProfileId is { } legacyP && Workspace.Store.GetProfile(legacyP) != null)
        {
            p = legacyP; t = Settings.ActiveTargetId;     // pre-RC2 selection lived in settings.json
            Workspace.Store.SetActive(p, t);
            Settings.ActiveProfileId = Settings.ActiveTargetId = null; Settings.Save();
        }
        ActiveProfileId = p; ActiveTargetId = t;
        _workspaceOwner = owner;
        var (kb, label) = Workspace.LoadActive(p, t);
        UseKnowledge(kb, label);
        AppLog.Info("Workspace opened for the current account");
        WorkspaceChanged?.Invoke();
    }

    /// <summary>Removes everything user-specific from the screen and memory (answers, history, quota figures).</summary>
    private void ClearUserState()
    {
        Bullets.Clear();
        Question = ""; ContextSummary = ""; Note = null; AnswerMeta = ""; LiveTranscript = ""; Pending = "";
        CoachKeywords = ""; CoachStructure = ""; CoachReminder = null; IsCoachView = false;
        LastReportPath = null; HistoryPosition = -1; _displayedAnswerId = -1;
        _periodRemainingSeconds = _todayRemainingSeconds = null;
        _freshSessionOnStart = false;
        RaiseMinutes(); Raise(nameof(HistoryLabel));
    }

    /// <summary>
    /// Sign out: the interview (if any) is stopped and its report saved to THIS account's workspace, the backend lease
    /// ends, tokens are revoked/cleared, and the account's data leaves the screen and memory.
    /// </summary>
    public async Task SignOutAsync()
    {
        if (SessionActive) await StopAsync();
        await EndLeaseAsync();
        await Cloud.SignOutAsync();
        OpenWorkspaceForCurrentAccount();
        Toast(Loc.T("toast_signed_out"), Views.ToastKind.Info, Loc.T("toast_signed_out_detail"));
    }

    /// <summary>A live interview session exists (listening or paused) — it continues until Stop.</summary>
    public bool SessionActive => IsListening || IsPaused;

    /// <summary>
    /// (Re)creates providers + engine from current settings. Refused while a session is active: rebuilding would end
    /// the interview (RC1 bug: changing a setting while paused replaced the session, history went 4/4 → 1/1).
    /// </summary>
    public void BuildEngine()
    {
        if (Knowledge == null) return;
        if (SessionActive && _engine != null) { AppLog.Warn("Engine rebuild refused: an interview session is active"); return; }
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
        _engine.AnswerStarted += a => Post(() => OnAnswerStarted(a));
        _engine.BulletAdded += (a, _) => Post(() => { if (a.Id == _displayedAnswerId) { SyncBullets(a); Pending = ""; Raise(nameof(IsThinking)); } });
        _engine.PendingTextChanged += (a, p) => Post(() => { if (a.Id == _displayedAnswerId) Pending = p; });
        _engine.AnswerCompleted += a => Post(() => { if (a.Id == _displayedAnswerId) { SyncBullets(a); Pending = ""; Note = a.Note; AnswerMeta = Meta(a); Raise(nameof(IsThinking)); } if (a.Kind != AnswerKind.Variant) LiveTranscript = ""; CommandManager.InvalidateRequerySuggested(); });
        _engine.LatencyMeasured += s => AppLog.Info($"Latency [{s.Source}] finalize {s.SpeechEndToFinalizedMs} ms, first bullet {s.FinalizedToFirstBulletMs} ms, total {s.FinalizedToCompleteMs} ms");
        _engine.InterruptionDetected += _ => AppLog.Info("Pace: likely interruption (answers shortened)");
        _engine.Log += m => AppLog.Info(m);
        ApplyStatus(EngineStatus.Ready, null);
        Raise(nameof(Presentation)); Raise(nameof(PresentationLabel));
    }

    /// <summary>Applies the settings that may change during an interview without ending it (sensitivity, fast cache, coach default).</summary>
    public void ApplyLiveSettings()
    {
        if (_engine == null) return;
        _engine.Turn.Sensitivity = Settings.VadSensitivity;
        _engine.Options.UseFastCache = Settings.UseFastCache;
        _engine.AnswerLanguageOverride = Settings.AnswerLanguage != "same" && LanguageRegistry.IsSupported(Settings.AnswerLanguage) ? Settings.AnswerLanguage : null;
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

    private bool _captureTrouble;

    /// <summary>Device unplugged / Bluetooth reconnect / default-device change: one calm toast per transition, never a crash.</summary>
    private void OnCaptureStatus(string status)
    {
        AudioStatus = _capture.StatusLabel;
        var trouble = status is "DEVICE ERROR" or "NO PLAYBACK DEVICE" or "RECONNECTING DEVICE";
        if (trouble == _captureTrouble) return;
        _captureTrouble = trouble;
        if (!SessionActive) return;
        if (trouble) Toast(Loc.T("toast_audio_device_lost"), Views.ToastKind.Warning, Loc.T("toast_audio_device_lost_detail"));
        else Toast(Loc.T("toast_audio_device_back"), Views.ToastKind.Success, _capture.CurrentDeviceName);
    }
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
    public async Task ToggleListeningAsync(string? startToast = null)
    {
        if (_engine == null || _toggleBusy) return;
        _toggleBusy = true;
        try { await ToggleListeningCoreAsync(startToast); }
        finally { _toggleBusy = false; }
    }

    private async Task ToggleListeningCoreAsync(string? startToast)
    {
        if (_engine == null) return;
        if (IsListening)
        {
            _engine.Pause(); IsListening = false; IsPaused = true;
            await EndLeaseAsync();   // paused time is never billed
            if (UsingCloud) Toast(Loc.T("toast_paused"), Views.ToastKind.Info, Loc.T("toast_paused_detail"));
            else Toast(Loc.T("toast_paused_dev"), Views.ToastKind.Info);
            return;
        }
        if (_freshSessionOnStart) { _freshSessionOnStart = false; BuildEngine(); if (_engine == null) return; }
        if (UsingCloud)
        {
            if (!Cloud.IsConfigured) { ShowLocalNote(Loc.T("note_not_configured_listen")); return; }
            if (!Cloud.IsSignedIn) { ShowLocalNote(Loc.T("note_sign_in_to_listen")); return; }
            if (!await BeginLeaseAsync()) return;
        }
        else if (!SecretStore.HasKey) { ShowLocalNote(Loc.T("note_dev_key")); return; }
        StartAudioPreview();
        if (_sessionStartedUtc == default) _sessionStartedUtc = DateTime.UtcNow;
        await _engine.StartListeningAsync();
        IsListening = true; IsPaused = false;
        Toast(Loc.T(startToast ?? "toast_listening"), Views.ToastKind.Success, Loc.T("toast_listening_detail"));
    }

    public async Task StopAsync()
    {
        if (_engine == null) return;
        var hadSession = SessionActive;
        await _engine.StopAsync();
        await EndLeaseAsync();
        IsListening = false; IsPaused = false;
        LiveTranscript = "";
        if (Settings.SaveSessionTranscript) SaveSession();
        var count = _engine.Memory.Questions.Count;
        LastReportPath = SaveReport();
        if (hadSession) Toast(LastReportPath != null ? Loc.F("toast_report_saved", count) : Loc.T("toast_no_report"), LastReportPath != null ? Views.ToastKind.Success : Views.ToastKind.Info);
        // The next Start begins a NEW interview (fresh history, memory and numbering); until then the finished
        // interview stays browsable on screen.
        _freshSessionOnStart = count > 0;
    }

    private bool _freshSessionOnStart;

    /// <summary>Starts the server lease for live listening and wires its events. False (with a readable note) when refused.</summary>
    private async Task<bool> BeginLeaseAsync()
    {
        try
        {
            _cloudSession = await StartLeaseAsync(CancellationToken.None);
            _transcriberOptions!.Endpoint = _cloudSession.Credential.WebSocketUrl;
            var session = _cloudSession;
            void Sync() { _periodRemainingSeconds = session.PeriodRemainingSeconds >= 0 ? session.PeriodRemainingSeconds : null; _todayRemainingSeconds = session.TodayRemainingSeconds >= 0 ? session.TodayRemainingSeconds : null; RaiseMinutes(); }
            session.RemainingChanged += _ => Post(Sync);
            session.Warning += w => Post(() => Note = w);
            session.Ended += reason => Post(async () => { if (session == _cloudSession) await OnLeaseEndedAsync(reason); });
            Sync();
            return true;
        }
        catch (BackendException ex)
        {
            ShowLocalNote(ex.Code switch
            {
                "allowance_exhausted" => Loc.T("lease_allowance"),
                "daily_live_cap" => Loc.T("lease_daily_cap"),
                "concurrent_limit" => Loc.T("lease_concurrent"),
                "device_revoked" or "device_limit" => Loc.T("lease_device"),
                "maintenance" => ex.Message,
                _ => Loc.F("lease_typed_still_work", CloudService.Describe(ex)),
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
        if (Knowledge == null || !Guid.TryParse(ActiveTargetId, out var targetId)) return null;
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
        if (s != null)
        {
            await s.DisposeAsync();
            _periodRemainingSeconds = _todayRemainingSeconds = null;   // the account summary is authoritative again
            _ = Cloud.RefreshAsync(CancellationToken.None);
            RaiseMinutes();
        }
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
        Note = Loc.T(reason switch
        {
            "allowance_exhausted" => "lease_end_allowance",
            "max_duration" => "lease_end_max_duration",
            "daily_cap" => "lease_end_daily_cap",
            "account_disabled" => "lease_end_disabled",
            "device_revoked" => "lease_end_device",
            _ => "lease_end_generic",
        });
        await Cloud.RefreshAsync(CancellationToken.None);
    }

    private string? _lastReportPath; public string? LastReportPath { get => _lastReportPath; private set => Set(ref _lastReportPath, value); }

    private bool _switchPending;
    private bool _workspaceInitialized;

    /// <summary>
    /// Post-interview report (honesty-labelled), saved encrypted in the account's workspace; a plain HTML viewer copy is
    /// written to the account's private view folder so "open last report" can show it in the browser.
    /// </summary>
    public string? SaveReport()
    {
        if (_engine == null || Knowledge == null || _engine.Memory.Questions.Count == 0) { _sessionStartedUtc = default; return null; }
        try
        {
            var id = Guid.NewGuid().ToString("n");
            var lang = LanguageRegistry.Find(Settings.ReportLanguage)?.Code ?? UiLanguage;
            var started = _sessionStartedUtc == default ? DateTime.UtcNow : _sessionStartedUtc;
            var pack = ActiveTargetId != null ? Workspace?.Store.GetPack(ActiveTargetId) : null;
            var report = ReportBuilder.Build(_engine.Memory, Knowledge, _engine.Metrics, id, started, DateTime.UtcNow, lang, pack,
                _engine.Metrics.Reconnects, _engine.Metrics.DuplicatesSuppressed, Knowledge.Profile.PreferredName is { Length: > 0 } pn ? pn : Knowledge.Context.CandidateName, ActiveLabel);
            if (Workspace == null) return null;   // signed out: nothing is persisted anywhere
            Workspace.Store.SaveSession(new StoredSession { Id = id, ProfileId = ActiveProfileId ?? "", TargetId = ActiveTargetId ?? "", StartedUtc = started, EndedUtc = DateTime.UtcNow, ReportJson = report.ToJson() });
            var path = Workspace.WriteViewerCopy(id, HtmlReportRenderer.Render(report, Branding.ProductName));
            _sessionStartedUtc = default;
            AppLog.Info($"Report saved ({report.Questions.Count} questions, {report.ExcludedTurns} non-interview turns excluded)");
            ReportSaved?.Invoke(id);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
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

    /// <summary>Reworks the answer that is ON SCREEN (e.g. item 5 of 8): same history position, same question, no new entry.</summary>
    private void Variant(AnswerStyle style)
    {
        if (_engine == null || _displayedAnswerId < 0) return;
        _ = _engine.RequestVariantAsync(_displayedAnswerId, style);
    }

    private void Navigate(int delta)
    {
        if (_engine == null) return;
        var h = _engine.History;
        var idx = Math.Clamp(HistoryPosition + delta, 0, h.Count - 1);
        if (idx < 0 || idx >= h.Count) return;
        HistoryPosition = idx;
        ShowAnswer(h[idx], isNew: false);
    }

    /// <summary>
    /// New question → show it (latest position). Replacement of the item on screen (variant or continued question) →
    /// update in place without moving. A replacement of an item the user is NOT looking at never pulls the view away.
    /// </summary>
    private void OnAnswerStarted(AnswerView a)
    {
        if (a.ReplacesAnswerId is int replaced)
        {
            if (replaced == _displayedAnswerId) ShowAnswer(a, isNew: false);
            Raise(nameof(HistoryLabel));
            return;
        }
        ShowAnswer(a, isNew: true);
    }

    private void ShowAnswer(AnswerView a, bool isNew)
    {
        _displayedAnswerId = a.Id;
        Question = a.Question;
        ContextSummary = a.ContextSummary;
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
        var style = a.Style switch
        {
            AnswerStyle.VeryShort => L("shorter"), AnswerStyle.Technical => L("technical"), AnswerStyle.Example => L("example"), AnswerStyle.Full => L("full"),
            AnswerStyle.Concise => L("adaptive_concise"), AnswerStyle.Rapid => L("adaptive_rapid"), _ => "",
        };
        var src = a.Source switch { AnswerSource.Cache => L("meta_prepared"), AnswerSource.CacheFallback => L("meta_prepared_fallback"), AnswerSource.Llm => L("meta_ai"), AnswerSource.Guidance => L("meta_guidance"), _ => "" };
        return Settings.ShowDiagnostics
            ? $"{(style.Length > 0 ? style + " · " : "")}{src} · {a.Mode.ToString().ToLowerInvariant()} · {a.Category.ToLowerInvariant()} · {a.Confidence.ToString().ToLowerInvariant()} · match {a.MatchScore:0.00}"
            : style;
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
        sb.AppendLine("Not answered: " + string.Join("  ", m.Suppression.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value}")).TrimEnd() + $"  variants {m.Variants}  guidance {m.GuidanceAnswers}");
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
            // Opt-in (Settings): the user chose a plain transcript in Documents. Unique per session, never overwritten.
            File.WriteAllText(Path.Combine(AppPaths.Sessions, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}.md"), sb.ToString());
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
        // Closing the app during an interview still keeps its report (RC1 lost it). Saved locally FIRST: ending the
        // lease is a network call that may be slow or offline, and the window only waits a few seconds.
        if (SessionActive)
        {
            SaveReport();
            if (Settings.SaveSessionTranscript) SaveSession();
        }
        if (_engine != null) await _engine.DisposeAsync();
        await EndLeaseAsync();
        (_provider as IDisposable)?.Dispose();
    }
}
