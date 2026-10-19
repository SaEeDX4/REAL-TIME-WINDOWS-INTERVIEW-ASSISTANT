using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.UiState;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.App.Views;

/// <summary>One check row. Everything it shows comes from the readiness snapshot; the row only maps it to text and brushes.</summary>
public sealed class ReadinessRow : ObservableObject
{
    public ReadinessRow(ReadinessCheckId id, bool first) { Id = id; Separator = new Thickness(0, first ? 0 : 1, 0, 0); }
    public ReadinessCheckId Id { get; }
    /// <summary>Hairline between rows (none above the first).</summary>
    public Thickness Separator { get; }
    private string _title = "", _detail = "", _status = "", _icon = "", _fixLabel = "";
    private Brush _brush = Brushes.Gray;
    private ReadinessFix _fix;
    public string Title { get => _title; set { if (Set(ref _title, value)) Raise(nameof(AccessibleName)); } }
    public string Detail { get => _detail; set { if (Set(ref _detail, value)) { Raise(nameof(DetailVisibility)); Raise(nameof(AccessibleName)); } } }
    public string Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(AccessibleName)); } }
    public string Icon { get => _icon; set => Set(ref _icon, value); }
    public Brush Brush { get => _brush; set => Set(ref _brush, value); }
    public ReadinessFix Fix { get => _fix; set { if (Set(ref _fix, value)) Raise(nameof(FixVisibility)); } }
    public string FixLabel { get => _fixLabel; set { if (Set(ref _fixLabel, value)) Raise(nameof(FixAccessibleName)); } }
    public Visibility DetailVisibility => _detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FixVisibility => _fix == ReadinessFix.None ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>Screen readers hear the whole row: "Audio signal, PASS, Peak -18 dBFS on Headset".</summary>
    public string AccessibleName => string.Join(", ", new[] { _title, _status, _detail }.Where(s => s.Length > 0));
    public string FixAccessibleName => _fixLabel.Length == 0 ? "" : $"{_fixLabel}: {_title}";
}

/// <summary>A line under "What blocks the interview" (or the optional improvements).</summary>
public sealed record ReadinessLine(string Icon, Brush Brush, string Text);

/// <summary>
/// The pre-interview check as a Home page. Runs the production probes (capture, account, transcription, answer
/// engine) and writes every result to the one <see cref="ReadinessStore"/> (vm.Readiness); the page renders FROM the
/// store, so Get started, the Interviews summary and the indicators always show the same thing. A run is bounded
/// (about 15 s) and finishes even when the user leaves the page (e.g. through a row's own fix button); closing Home
/// cancels it, and a run cut short is forgotten (Not checked) rather than left "Checking". Every failure — offline,
/// an expired sign-in that cannot be renewed, a captive Wi-Fi page — ends the pending checks with a reason. No check
/// runs while an interview is live (the AI and transcription tests would compete with the session).
/// </summary>
public partial class PreInterviewPage : UserControl
{
    /// <summary>Sample question for the AI test, in the UI language (it is sent to the AI as typed text).</summary>
    public static string TestQuestion => Loc.T("pf_ai_test_question");

    private MainViewModel _vm = null!;
    private MainWindow _main = null!;
    private readonly List<ReadinessRow> _rows;
    private CancellationTokenSource? _toolCts, _signalCts, _runCts;
    private int _generation;
    private string _runContext = "";
    private bool _running, _shown;
    private ReadinessState _loggedState = ReadinessState.NotChecked;

    /// <summary>A fix needs another Home page ("settings/audio", "account", "interviews").</summary>
    public event Action<string>? NavigateRequested;
    /// <summary>Start interview was pressed (Home brings the Live Assistant forward and starts listening).</summary>
    public event Action? StartRequested;
    /// <summary>The readiness state or the running flag changed (the sticky action bar follows it).</summary>
    public event Action? StateChanged;

    public PreInterviewPage()
    {
        InitializeComponent();
        _rows = Enum.GetValues<ReadinessCheckId>().Select((id, i) => new ReadinessRow(id, i == 0)).ToList();
        CheckRows.ItemsSource = _rows;
    }

    public void Initialize(MainViewModel vm, MainWindow main)
    {
        _vm = vm; _main = main;
        DataContext = vm;   // the level meter
        _loggedState = vm.ReadinessState;
        _vm.Readiness.Changed += OnStoreChanged;
        _main.SettingsApplied += OnSettingsApplied;
        Render();
    }

    /// <summary>Home is closing: stop every probe and listener.</summary>
    public void Detach()
    {
        Cancel();
        _vm.Readiness.Changed -= OnStoreChanged;
        _main.SettingsApplied -= OnSettingsApplied;
    }

    private static string T(string key) => Loc.T(key);
    private static string F(string key, params object?[] args) => Loc.F(key, args);
    private Brush BrushFor(ReadinessState s) => (Brush)FindResource(ReadinessSnapshot.BrushKey(s));

    // ================= page lifecycle =================

    /// <summary>The page became visible: run the checks when there is no current result (none yet, or out of date).</summary>
    public void OnShown()
    {
        _shown = true;
        Render();
        if (!_running && !_vm.SessionActive && ReadinessPresentation.ShouldAutoRun(State)) _ = RunAllAsync();
    }

    /// <summary>
    /// The user went to another page (often through a row's own fix button): a running test tool stops, the bounded run
    /// finishes into the store so every surface still gets its result.
    /// </summary>
    public void OnHidden()
    {
        _shown = false;
        if (_toolCts is { IsCancellationRequested: false }) { _toolCts.Cancel(); ShowResult(false, T("rd_tool_stopped")); }
    }

    /// <summary>Saved settings make the last result "Out of date" (see MainWindow.ApplySettings); an open page checks again at once.</summary>
    private void OnSettingsApplied() => Dispatcher.BeginInvoke(() =>
    {
        if (_shown && !_running && !_vm.SessionActive) _ = RunAllAsync();
    });

    /// <summary>Home is closing: cancels tools, the signal listener and an unfinished run (whose results are dropped).</summary>
    private void Cancel()
    {
        _toolCts?.Cancel(); _signalCts?.Cancel(); _runCts?.Cancel();
        if (_running)
        {
            _generation++;
            _running = false;
            var s = _vm.Readiness.Snapshot;
            if (s.IsRunning && s.Context == _runContext) _vm.Readiness.Clear();   // unknown, not "checking" forever
        }
        Render();
    }

    /// <summary>The state every surface shows right now.</summary>
    public ReadinessState State => _vm.ReadinessState;
    public bool IsRunning => _running;
    public bool CanStart => ReadinessPresentation.CanStart(State);

    // ================= store =================

    private void OnStoreChanged(ReadinessSnapshot s)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnStoreChanged(s)); return; }
        var now = _vm.ReadinessState;
        if (ReadinessPresentation.TransitionLog(_loggedState, now, s) is { } line) AppLog.Info(line);
        _loggedState = now;
        Render();
    }

    /// <summary>Writes one result of the run <paramref name="gen"/> (a cancelled or replaced run writes nothing).</summary>
    private void Report(int gen, ReadinessCheckId id, CheckOutcome outcome, string detail, ReadinessFix fix = ReadinessFix.None)
    {
        // A cancelled or replaced run, or a result the store has forgotten meanwhile (sign-out), writes nothing.
        var s = _vm.Readiness.Snapshot;
        if (gen != _generation || s.Checks.Count == 0 || s.Context != _runContext) return;
        _vm.Readiness.Report(new ReadinessCheckResult(id, outcome, detail, fix));
    }

    private void Pass(int gen, ReadinessCheckId id, string detail) => Report(gen, id, CheckOutcome.Pass, detail);
    private void Warn(int gen, ReadinessCheckId id, string detail, ReadinessFix fix) => Report(gen, id, CheckOutcome.Warning, detail, fix);
    private void Fail(int gen, ReadinessCheckId id, string detail, ReadinessFix fix) => Report(gen, id, CheckOutcome.Fail, detail, fix);
    private void Busy(int gen, ReadinessCheckId id, string detail) => Report(gen, id, CheckOutcome.Pending, detail);

    /// <summary>
    /// A test tool's result updates the matching check only when a result for the current account, interview and device
    /// exists (otherwise the tool output alone shows it — a lone result is not a readiness check).
    /// </summary>
    private bool HasCurrentResult
    {
        get { var s = _vm.Readiness.Snapshot; return s.Checks.Count > 0 && s.Context == _vm.ReadinessContext; }
    }

    private CheckOutcome? OutcomeOf(ReadinessCheckId id) => _vm.Readiness.Snapshot.Get(id)?.Outcome;

    // ================= rendering (from the snapshot only) =================

    /// <summary>Re-renders texts from the store (also after a language switch: nothing is re-run).</summary>
    public void Relocalize() => Render();

    /// <summary>Re-renders after something the result depends on changed (account, active interview, audio device).</summary>
    public void Refresh() => Render();

    /// <summary>"7 of 8 checks passed" for what is selected now (shared by every readiness surface).</summary>
    public static string SummaryText(MainViewModel vm)
    {
        var (key, args) = ReadinessPresentation.Summary(vm.Readiness.Snapshot, vm.ReadinessContext);
        return Loc.F(key, args);
    }

    private void Render()
    {
        if (_vm == null) return;
        var snap = _vm.Readiness.Snapshot;
        var state = _vm.ReadinessState;
        var cloud = _vm.UsingCloud;
        var current = snap.Checks.Count > 0 && snap.Context == _vm.ReadinessContext;
        foreach (var row in _rows)
        {
            var r = current || snap.IsRunning ? snap.Get(row.Id) : null;
            var rs = ReadinessPresentation.StateOf(r?.Outcome);
            row.Title = T(ReadinessPresentation.TitleKey(row.Id, cloud));
            row.Detail = r == null ? "" : Loc.Relocalize(r.Detail) ?? r.Detail;
            row.Status = T(ReadinessPresentation.StatusKey(r?.Outcome));
            row.Icon = ReadinessPresentation.Icon(rs);
            row.Brush = BrushFor(rs);
            row.Fix = r?.Outcome is CheckOutcome.Warning or CheckOutcome.Fail or CheckOutcome.NotTested ? r.Fix : ReadinessFix.None;
            row.FixLabel = ReadinessPresentation.FixLabelKey(row.Fix) is { } k ? T(k) : "";
        }

        var brush = BrushFor(state);
        StateIcon.Text = ReadinessPresentation.Icon(state);
        StateIcon.Foreground = StateText.Foreground = StateBadge.BorderBrush = brush;
        StateText.Text = T(ReadinessSnapshot.StateKey(state));
        SummaryLine.Text = SummaryText(_vm);
        CheckedAtText.Text = current && snap.CheckedAt is { } at ? F("rd_last_checked", at.ToLocalTime().ToString("t", Loc.Culture)) : "";
        CheckedAtText.Visibility = CheckedAtText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryHint.Text = T(ReadinessPresentation.HintKey(state));

        // Not ready: what blocks (and how to fix it). Ready with warnings: the optional improvements.
        // Ready (all required checks passed): an optional test not run yet is a gentle to-do, never a warning.
        var lines = state switch
        {
            ReadinessState.NotReady => ReadinessPresentation.Blockers(snap),
            ReadinessState.ReadyWithWarnings => ReadinessPresentation.Warnings(snap).Concat(ReadinessPresentation.NotTestedYet(snap)).ToList(),
            ReadinessState.Ready => ReadinessPresentation.NotTestedYet(snap),
            _ => Array.Empty<ReadinessCheckResult>(),
        };
        BlockersPanel.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BlockersTitle.Text = T(state == ReadinessState.NotReady ? "rd_blockers_title" : "pf_verdict_optional");
        BlockerList.ItemsSource = lines.Select(c =>
        {
            var s = ReadinessPresentation.StateOf(c.Outcome);
            return new ReadinessLine(ReadinessPresentation.Icon(s), BrushFor(s), $"{T(ReadinessPresentation.TitleKey(c.Id, cloud))}: {T(ReadinessPresentation.FixHintKey(c.Id, cloud))}");
        }).ToList();
        TypedStillWork.Visibility = state == ReadinessState.NotReady && ReadinessPresentation.TypedQuestionsStillWork(snap) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(SummaryCard, string.Join(". ", new[] { StateText.Text, SummaryLine.Text, CheckedAtText.Text, SummaryHint.Text }.Where(x => x.Length > 0)));

        // Tool output already on screen follows a language switch too.
        foreach (var tb in new[] { ToolTitle, ToolResult, ToolOutput }) tb.Text = Loc.Relocalize(tb.Text) ?? tb.Text;
        StateChanged?.Invoke();
    }

    // ================= checks =================

    /// <summary>Runs all eight checks for the current account, interview and audio device.</summary>
    public async Task RunAllAsync()
    {
        if (_running || _vm == null) return;
        if (_vm.SessionActive) { BeginTool(T("pf_title")); ToolOutput.Text = T("rd_session_active"); return; }
        _running = true;
        var gen = ++_generation;
        _runCts?.Cancel();
        var cts = _runCts = new CancellationTokenSource();
        _runContext = _vm.ReadinessContext;
        _vm.Readiness.Begin(_runContext);
        try
        {
            await CheckLocalAsync(gen);
            if (gen != _generation) return;   // replaced or cancelled meanwhile: a stale run never touches the new one
            if (_vm.UsingCloud) { await RunCloudChecksAsync(gen, cts.Token); return; }
            var key = SecretStore.GetApiKey();
            var signalTask = CheckSignalAsync(gen, TimeSpan.FromSeconds(10), cts.Token);
            Busy(gen, ReadinessCheckId.AccountPlan, T("pf_busy_key"));
            var auth = await OpenAiAuthProbe.CheckAsync(key);
            if (gen != _generation) return;
            if (auth.Ok)
            {
                Pass(gen, ReadinessCheckId.AccountPlan, auth.Detail);
                Busy(gen, ReadinessCheckId.AnswerEngine, T("pf_busy_answer"));
                var sttTask = CheckTranscriptionConnectAsync(gen, cts.Token);
                var r = await AnswerServiceProbe.RunAsync(SecretStore.GetApiKey, _vm.Settings.AnswerModel);
                if (r.Ok) Pass(gen, ReadinessCheckId.AnswerEngine, F("pf_answer_ok", _vm.Settings.AnswerModel, r.FirstTokenMs));
                else Fail(gen, ReadinessCheckId.AnswerEngine, r.Error ?? T("err_unknown"), ReadinessFix.TestAi);
                await sttTask;
            }
            else
            {
                Fail(gen, ReadinessCheckId.AccountPlan, auth.Kind == ProviderErrorKind.Network ? T("svc_unreachable") : auth.Detail, ReadinessFix.None);
                Warn(gen, ReadinessCheckId.AnswerEngine, T("pf_skipped_key"), ReadinessFix.None);
                Warn(gen, ReadinessCheckId.Transcription, T("pf_skipped_key"), ReadinessFix.None);
            }
            await signalTask;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Offline, a sign-in that cannot be renewed, a captive Wi-Fi page, a provider error: every check this run
            // still owes ends as a failure with a reason (never "Checking…" forever). Details go to the log only.
            AppLog.Warn($"Pre-interview check stopped: {ex.GetType().Name}");
            FailPending(gen, UiState.ErrorCopy.Describe(ex, includeReference: false));
        }
        finally
        {
            if (gen == _generation) _running = false;
            Render();
        }
    }

    /// <summary>Ends every still-pending check of run <paramref name="gen"/> as a failure with <paramref name="reason"/>.</summary>
    private void FailPending(int gen, string reason)
    {
        foreach (var c in _vm.Readiness.Snapshot.Checks.Where(c => c.Outcome == CheckOutcome.Pending).ToList())
            Fail(gen, c.Id, reason, c.Id switch
            {
                ReadinessCheckId.AccountPlan => ReadinessFix.OpenAccount,
                ReadinessCheckId.Transcription => ReadinessFix.TestTranscription,
                ReadinessCheckId.AnswerEngine => ReadinessFix.TestAi,
                ReadinessCheckId.AudioSignal => ReadinessFix.TestAudio,
                _ => ReadinessFix.None,
            });
    }

    /// <summary>Account mode: entitlements from the server, answer test through the server proxy (typed-question path, no live minutes).</summary>
    private async Task RunCloudChecksAsync(int gen, CancellationToken ct)
    {
        var signalTask = CheckSignalAsync(gen, TimeSpan.FromSeconds(10), ct);
        const ReadinessCheckId Acct = ReadinessCheckId.AccountPlan, Answer = ReadinessCheckId.AnswerEngine, Stt = ReadinessCheckId.Transcription;
        Busy(gen, Acct, T("pf_busy_account"));
        var refreshed = _vm.Cloud.IsSignedIn && await _vm.Cloud.RefreshAsync(ct);
        if (gen != _generation) return;
        if (!_vm.Cloud.IsConfigured)
        {
            Fail(gen, Acct, T("acct_not_configured_long"), ReadinessFix.OpenAccount);
            Warn(gen, Answer, T("pf_skipped_service"), ReadinessFix.None); Warn(gen, Stt, T("pf_skipped_service"), ReadinessFix.None);
        }
        else if (!_vm.Cloud.IsSignedIn)
        {
            Fail(gen, Acct, Loc.Relocalize(_vm.Cloud.Message) ?? T("note_sign_in_to_listen"), ReadinessFix.SignIn);
            Warn(gen, Answer, T("pf_skipped_sign_in"), ReadinessFix.SignIn); Warn(gen, Stt, T("pf_skipped_sign_in"), ReadinessFix.SignIn);
        }
        else if (!refreshed || _vm.Cloud.Account is not { } a)
        {
            Fail(gen, Acct, Loc.Relocalize(_vm.Cloud.Message) ?? T("svc_unreachable"), ReadinessFix.OpenAccount);
            Warn(gen, Answer, T("pf_skipped_unreachable"), ReadinessFix.None); Warn(gen, Stt, T("pf_skipped_unreachable"), ReadinessFix.None);
        }
        else
        {
            var mins = a.Usage.LiveSecondsRemaining / 60;
            var today = a.Usage.LiveSecondsAvailableToday >= 0 ? Math.Min(mins, a.Usage.LiveSecondsAvailableToday / 60) : mins;
            if (_vm.Cloud.Config?.Maintenance == true) Fail(gen, Acct, _vm.Cloud.Config.MaintenanceMessage ?? T("svc_maintenance"), ReadinessFix.OpenAccount);
            else if (mins <= 0) Fail(gen, Acct, F("pf_no_minutes", MainViewModel.BillingLabel(a)), ReadinessFix.OpenAccount);
            else
            {
                // The device must be registered (and not removed) for live sessions.
                string device;
                try { await _vm.Cloud.EnsureDeviceAsync(ct); device = T("pf_device_registered"); }
                catch (Client.BackendException ex) { device = ""; Fail(gen, Acct, CloudService.Describe(ex), ReadinessFix.OpenAccount); }
                if (device.Length > 0)
                {
                    // Live listening is blocked for the rest of the day: that is a FAIL, not an optional warning.
                    if (today <= 0) Fail(gen, Acct, F("pf_account_today_used", a.Email, MainViewModel.BillingLabel(a)), ReadinessFix.OpenAccount);
                    else Pass(gen, Acct, F("pf_account_ok", a.Email, MainViewModel.BillingLabel(a), mins, today, device));
                }
            }
            if (mins > 0)
            {
                Busy(gen, Answer, T("pf_busy_answer_service"));
                try
                {
                    var (answer, sample, _) = await _vm.TestAiAsync(TestQuestion, _ => { });
                    if (answer.Source == Core.Orchestration.AnswerSource.Llm && answer.Bullets.Count >= 2) Pass(gen, Answer, F("pf_answer_ok_service", sample?.FinalizedToFirstBulletMs));
                    else Fail(gen, Answer, Loc.Relocalize(answer.Note) ?? T("err_unknown"), ReadinessFix.TestAi);
                }
                catch (Exception ex) when (ex is ProviderException or InvalidOperationException or Client.BackendException) { Fail(gen, Answer, CloudService.Describe(ex), ReadinessFix.TestAi); }
                // Live transcription is only proven with real meeting audio: an optional test the user runs (neither PASS nor WARNING).
                Report(gen, Stt, CheckOutcome.NotTested, T("pf_stt_run_test"), ReadinessFix.TestTranscription);
            }
            else
            {
                Warn(gen, Answer, T("pf_skipped_service"), ReadinessFix.OpenAccount); Warn(gen, Stt, T("pf_skipped_service"), ReadinessFix.OpenAccount);
            }
        }
        await signalTask;
    }

    /// <summary>
    /// Checks that need no network (device, prepared interview, prepared answers, window on top; also used by --selftest).
    /// Starts a run for the current context when none is under way.
    /// </summary>
    public async Task CheckLocalAsync()
    {
        if (!_running && !HasCurrentResult) _ = RunAllAsync();   // nothing to add to: a full run (its local part is below)
        if (!_running) { _generation++; _runContext = _vm.ReadinessContext; }   // re-check into the finished result
        await CheckLocalAsync(_generation);
    }

    private async Task CheckLocalAsync(int gen)
    {
        _vm.StartAudioPreview();
        var cap = _vm.Capture;
        // The capture was (re)started a moment ago (settings saved, page opened): give the device a moment to open.
        var until = DateTime.UtcNow.AddSeconds(2);
        while (cap.Status != "ACTIVE" && DateTime.UtcNow < until && gen == _generation) await Task.Delay(100);
        if (gen != _generation) return;
        if (cap.Status == "ACTIVE") Pass(gen, ReadinessCheckId.PlaybackDevice, $"{cap.CurrentDeviceName} · {cap.SampleRate} Hz · {cap.Channels} ch");
        else Fail(gen, ReadinessCheckId.PlaybackDevice, string.IsNullOrEmpty(cap.CurrentDeviceName) ? T("pf_no_device") : $"{cap.CurrentDeviceName} · {cap.StatusLabel}", ReadinessFix.OpenAudioSettings);

        if (_vm.Knowledge is { } k)
        {
            if (k.Questions.Count > 0) Pass(gen, ReadinessCheckId.InterviewPrepared, F("pf_prepared_ok", _vm.ActiveLabel, k.Profile.Experience.Count, k.Stories.Count, k.Context.TranscriptionTerms().Count));
            else Warn(gen, ReadinessCheckId.InterviewPrepared, T("pf_prepared_none"), ReadinessFix.OpenInterviews);
            if (k.Questions.Count >= 50) Pass(gen, ReadinessCheckId.PreparedAnswers, F("pf_bank_ok", k.Questions.Count));
            else if (k.Questions.Count == 0) Warn(gen, ReadinessCheckId.PreparedAnswers, T("pf_bank_none"), ReadinessFix.PrepareInterview);
            else Fail(gen, ReadinessCheckId.PreparedAnswers, F("pf_bank_incomplete", k.Questions.Count), ReadinessFix.PrepareInterview);
        }
        else
        {
            Fail(gen, ReadinessCheckId.InterviewPrepared, T("pf_prepared_none"), ReadinessFix.OpenInterviews);
            Fail(gen, ReadinessCheckId.PreparedAnswers, T("pf_bank_none"), ReadinessFix.PrepareInterview);
        }

        if (_main.Topmost) Pass(gen, ReadinessCheckId.WindowOnTop, T("pf_topmost_on"));
        else Warn(gen, ReadinessCheckId.WindowOnTop, T("pf_topmost_off"), ReadinessFix.OpenWindowSettings);
    }

    /// <summary>Time the capture gets to start (device open, first buffers) before the listening window counts.</summary>
    private static readonly TimeSpan SignalGrace = TimeSpan.FromSeconds(3);
    /// <summary>After "nothing heard yet", the check keeps listening this long and passes as soon as audio plays.</summary>
    private static readonly TimeSpan SignalFollowUp = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Meeting-audio check. A short grace period lets the capture start before anything is judged; "nothing heard" is
    /// a WARNING (the meeting may simply be silent yet), and the check keeps listening in the background so it turns
    /// PASS by itself once audio plays (or after a successful audio or transcription test).
    /// </summary>
    private async Task CheckSignalAsync(int gen, TimeSpan window, CancellationToken runToken)
    {
        // Linked to this run: a test tool that proves the signal stops it, and a cancelled run stops it too.
        _signalCts?.Cancel();
        var ct = (_signalCts = CancellationTokenSource.CreateLinkedTokenSource(runToken)).Token;
        Busy(gen, ReadinessCheckId.AudioSignal, T("pf_busy_signal"));
        try
        {
            var graceUntil = DateTime.UtcNow + SignalGrace;
            while (DateTime.UtcNow < graceUntil && _vm.Capture.Status != "ACTIVE") await Task.Delay(100, ct);
            var start = _vm.Capture.FramesWithSignal;
            bool Heard() => _vm.Capture.FramesWithSignal - start >= 5;
            var until = DateTime.UtcNow + window;
            while (DateTime.UtcNow < until && !Heard()) await Task.Delay(100, ct);
            if (Heard()) { Pass(gen, ReadinessCheckId.AudioSignal, F("pf_signal_ok", Math.Round(_vm.Capture.LastPeakDb), _vm.Capture.CurrentDeviceName)); return; }
            Warn(gen, ReadinessCheckId.AudioSignal, F("pf_signal_none", window.TotalSeconds, _vm.Capture.CurrentDeviceName), ReadinessFix.TestAudio);
            _ = FollowUpSignalAsync(start, ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task FollowUpSignalAsync(long start, CancellationToken ct)
    {
        try
        {
            var until = DateTime.UtcNow + SignalFollowUp;
            while (DateTime.UtcNow < until && OutcomeOf(ReadinessCheckId.AudioSignal) == CheckOutcome.Warning)
            {
                await Task.Delay(250, ct);
                if (_vm.Capture.FramesWithSignal - start < 5 || !HasCurrentResult) continue;
                _vm.Readiness.Report(new ReadinessCheckResult(ReadinessCheckId.AudioSignal, CheckOutcome.Pass, F("pf_signal_ok", Math.Round(_vm.Capture.LastPeakDb), _vm.Capture.CurrentDeviceName)));
                return;
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckTranscriptionConnectAsync(int gen, CancellationToken ct)
    {
        Busy(gen, ReadinessCheckId.Transcription, F("pf_busy_stt", _vm.Settings.TranscriptionModel));
        await using var t = new OpenAiRealtimeTranscriber(SecretStore.GetApiKey, _vm.TranscriberOptions());
        var tcs = new TaskCompletionSource<(ConnectionStatus, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.StatusChanged += (s, d) => { if (s is ConnectionStatus.Connected or ConnectionStatus.Failed) tcs.TrySetResult((s, d)); };
        await t.StartAsync(ct);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(10_000, ct));
        if (done == tcs.Task && tcs.Task.Result.Item1 == ConnectionStatus.Connected)
        {
            await Task.Delay(2000, ct); // give the server time to reject the session config / trigger model fallback
            if (t.Status == ConnectionStatus.Connected) Pass(gen, ReadinessCheckId.Transcription, F("pf_stt_accepted", t.ActiveModel));
            else Warn(gen, ReadinessCheckId.Transcription, T("pf_stt_unstable"), ReadinessFix.TestTranscription);
        }
        else Fail(gen, ReadinessCheckId.Transcription, done == tcs.Task ? tcs.Task.Result.Item2 ?? T("pf_stt_failed") : T("svc_timeout"), ReadinessFix.TestTranscription);
    }

    // ================= test tools =================

    private bool ToolReady(string titleKey)
    {
        if (_vm.UsingCloud ? _vm.Cloud.IsSignedIn : SecretStore.HasKey) return true;
        BeginTool(T(titleKey));
        ToolOutput.Text = T(_vm.UsingCloud ? "pf_sign_in_first" : "pf_key_first");
        return false;
    }

    private void BeginTool(string title)
    {
        _toolCts?.Cancel();
        _toolCts = new CancellationTokenSource();
        ToolTitle.Text = title;
        ToolOutput.Text = "";
        ToolResult.Visibility = Visibility.Collapsed;
        ToolDiagnostics.Visibility = Visibility.Collapsed;
        ToolDiagnosticsText.Text = "";
    }

    private void ShowResult(bool ok, string text)
    {
        ToolResult.Text = (ok ? "✓ " : "✗ ") + text;
        ToolResult.Foreground = (Brush)FindResource(ok ? "B.Listening" : "B.Thinking");
        ToolResult.Visibility = Visibility.Visible;
    }

    private void Diagnostic(string line)
    {
        ToolDiagnostics.Visibility = Visibility.Visible;
        ToolDiagnosticsText.Text = (ToolDiagnosticsText.Text.Length > 0 ? ToolDiagnosticsText.Text + "\n" : "") + line;
    }

    /// <summary>Records a tool's result on its check (only into a current result; see <see cref="HasCurrentResult"/>).</summary>
    private void ToolReport(ReadinessCheckId id, CheckOutcome outcome, string detail, ReadinessFix fix = ReadinessFix.None)
    {
        if (HasCurrentResult) _vm.Readiness.Report(new ReadinessCheckResult(id, outcome, detail, fix));
    }

    private void TestAudio_Click(object sender, RoutedEventArgs e) => _ = TestAudioAsync();
    private void TestStt_Click(object sender, RoutedEventArgs e) => _ = TestTranscriptionAsync();
    private void TestAi_Click(object sender, RoutedEventArgs e) => _ = TestAiAsync();

    private async Task TestAudioAsync()
    {
        BeginTool(T("pf_audio_title"));
        var ct = _toolCts!.Token;
        _vm.StartAudioPreview();
        var cap = _vm.Capture;
        var start = cap.FramesWithSignal;
        double maxPeak = -100;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            maxPeak = Math.Max(maxPeak, cap.LastPeakDb);
            var detected = cap.FramesWithSignal - start >= 5;
            ToolOutput.Text = new StringBuilder()
                .AppendLine(F("pf_audio_device", cap.CurrentDeviceName))
                .AppendLine(F("pf_audio_level", Math.Round(cap.LastPeakDb), Math.Round(maxPeak)))
                .Append(T(detected ? "pf_audio_detected" : "pf_audio_waiting")).ToString();
        };
        timer.Start();
        try { await Task.Delay(15_000, ct); } catch (OperationCanceledException) { }
        timer.Stop();
        if (ct.IsCancellationRequested) return;   // another test started, or the page was left
        var ok = cap.FramesWithSignal - start >= 5;
        ShowResult(ok, T(ok ? "pf_audio_passed" : "pf_audio_failed"));
        Diagnostic($"{cap.SampleRate} Hz · {cap.Channels} ch · {cap.Encoding} → 24000 Hz mono PCM16 · {cap.Status} · peak {maxPeak:0.0} dBFS");
        if (ok) { _signalCts?.Cancel(); ToolReport(ReadinessCheckId.AudioSignal, CheckOutcome.Pass, F("pf_signal_ok", Math.Round(maxPeak), cap.CurrentDeviceName)); }
        else if (OutcomeOf(ReadinessCheckId.AudioSignal) != CheckOutcome.Pass) ToolReport(ReadinessCheckId.AudioSignal, CheckOutcome.Warning, T("pf_audio_failed"), ReadinessFix.OpenAudioSettings);
    }

    /// <summary>
    /// Transcription test on the production path. The test connection is closed on purpose when it ends, so the
    /// result reads "Test complete · Transcription test passed" — the technical connection log is under Technical details.
    /// </summary>
    private async Task TestTranscriptionAsync()
    {
        if (!ToolReady("pf_stt_title")) return;
        BeginTool(T("pf_stt_title"));
        var ct = _toolCts!.Token;
        string transcript = "";
        void Render() => ToolOutput.Text = transcript.Length > 0 ? transcript : T("pf_stt_waiting");
        Render();
        string result;
        try { result = await _vm.TestTranscriptionAsync(t => { transcript = t; Render(); }, s => Diagnostic(s), TimeSpan.FromSeconds(25), ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is ProviderException or InvalidOperationException or Client.BackendException) { return; }
        catch (Exception ex) when (ex is ProviderException or InvalidOperationException or Client.BackendException)
        {
            AppLog.Warn($"Transcription test failed: {ex.GetType().Name}");
            ShowResult(false, CloudService.Describe(ex));
            if (OutcomeOf(ReadinessCheckId.Transcription) != CheckOutcome.Pass) ToolReport(ReadinessCheckId.Transcription, CheckOutcome.Fail, CloudService.Describe(ex), ReadinessFix.TestTranscription);
            return;
        }
        if (ct.IsCancellationRequested) return;
        var ok = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
        ShowResult(ok, ok ? T("pf_stt_passed") : T("pf_stt_no_text"));
        Diagnostic(result);
        if (ok)
        {
            ToolReport(ReadinessCheckId.Transcription, CheckOutcome.Pass, T("pf_stt_passed"));
            // Words were transcribed from the meeting audio, so the audio signal is proven too (re-evaluated at once).
            if (OutcomeOf(ReadinessCheckId.AudioSignal) != CheckOutcome.Pass) { _signalCts?.Cancel(); ToolReport(ReadinessCheckId.AudioSignal, CheckOutcome.Pass, F("pf_signal_from_stt", _vm.Capture.CurrentDeviceName)); }
        }
        else if (OutcomeOf(ReadinessCheckId.Transcription) != CheckOutcome.Pass) ToolReport(ReadinessCheckId.Transcription, CheckOutcome.Warning, T("pf_stt_no_text"), ReadinessFix.TestAudio);
    }

    private async Task TestAiAsync()
    {
        if (!ToolReady("pf_ai_title")) return;
        BeginTool(F("pf_ai_title_q", TestQuestion));
        var ct = _toolCts!.Token;
        var sb = new StringBuilder();
        ToolOutput.Text = T("pf_ai_requesting");
        try
        {
            var (answer, sample, model) = await _vm.TestAiAsync(TestQuestion, b => { if (ct.IsCancellationRequested) return; sb.AppendLine("• " + b); ToolOutput.Text = sb.ToString(); });
            if (ct.IsCancellationRequested) return;
            if (sample != null) Diagnostic($"{model} · first token {sample.FinalizedToFirstTokenMs} ms · first bullet {sample.FinalizedToFirstBulletMs} ms · complete {sample.FinalizedToCompleteMs} ms");
            var ok = answer.Bullets.Count is >= 2 and <= 4 && answer.Source == Core.Orchestration.AnswerSource.Llm;
            ShowResult(ok, ok ? T("pf_ai_passed") : Loc.Relocalize(answer.Note) ?? T("err_unknown"));
            if (ok) ToolReport(ReadinessCheckId.AnswerEngine, CheckOutcome.Pass, F("pf_answer_ok_service", sample?.FinalizedToFirstBulletMs));
            else ToolReport(ReadinessCheckId.AnswerEngine, CheckOutcome.Fail, Loc.Relocalize(answer.Note) ?? T("err_unknown"), ReadinessFix.TestAi);
        }
        catch (Exception ex) when (ex is ProviderException or InvalidOperationException or Client.BackendException) { if (!ct.IsCancellationRequested) ShowResult(false, CloudService.Describe(ex)); }
    }

    // ================= actions =================

    /// <summary>A row's action: test tools run here, everything else is a Home deep link.</summary>
    private void Fix_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ReadinessRow row) return;
        switch (row.Fix)
        {
            case ReadinessFix.TestAudio: _ = TestAudioAsync(); AudioBtn.BringIntoView(); break;
            case ReadinessFix.TestTranscription: _ = TestTranscriptionAsync(); SttBtn.BringIntoView(); break;
            case ReadinessFix.TestAi: _ = TestAiAsync(); AiBtn.BringIntoView(); break;
            default:
                if (ReadinessPresentation.FixRoute(row.Fix) is { } route) NavigateRequested?.Invoke(route);
                break;
        }
    }

    /// <summary>Start interview (sticky action bar, Get started): only when nothing blocks.</summary>
    public void RequestStart()
    {
        if (CanStart) StartRequested?.Invoke();
    }

    // ----- self-test hooks -----
    internal IReadOnlyList<ReadinessRow> RowsForTest => _rows;
    internal string StateTextForTest => StateText.Text;
}
