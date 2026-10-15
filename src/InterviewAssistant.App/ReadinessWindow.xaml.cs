using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.App;

/// <summary>One pre-interview check: PASS / WARNING / FAIL with a plain explanation and a fix.</summary>
public sealed class CheckItem : ObservableObject
{
    public required string TitleKey { get; init; }
    public required string FixKey { get; set; }
    public string Title => Loc.T(TitleKey);
    public string FixHint => Loc.T(FixKey);
    private string _detail = ""; public string Detail { get => _detail; set => Set(ref _detail, value); }
    private string _state = "—"; public string State { get => _state; set => Set(ref _state, value); }
    private string _icon = ""; public string Icon { get => _icon; set => Set(ref _icon, value); }
    private Brush _brush = Brushes.Gray; public Brush Brush { get => _brush; set => Set(ref _brush, value); }
    public bool? Passed { get; private set; }
    public bool IsWarning { get; private set; }

    public void Pass(string detail) { Passed = true; IsWarning = false; Apply(Loc.T("pf_pass"), detail, "", "B.Listening"); }
    public void Fail(string detail) { Passed = false; IsWarning = false; Apply(Loc.T("pf_fail"), detail, "", "B.Error"); }
    public void Warn(string detail) { Passed = false; IsWarning = true; Apply(Loc.T("pf_warning"), detail, "", "B.Thinking"); }
    public void Busy(string detail) { Passed = null; IsWarning = false; Apply(Loc.T("pf_checking"), detail, "", "B.Accent"); }
    private void Apply(string s, string d, string i, string b) { State = s; Detail = d; Icon = i; Brush = (Brush)Application.Current.Resources[b]; }
    public void RefreshTitle() { Raise(nameof(Title)); Raise(nameof(FixHint)); }
}

/// <summary>Pre-interview check + test tools. All tests use the production classes (capture, transcriber, engine).</summary>
public partial class ReadinessWindow : Window
{
    /// <summary>Sample question for the AI test, in the UI language (it is sent to the AI as typed text).</summary>
    public static string TestQuestion => Loc.T("pf_ai_test_question");
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    private readonly bool _autoRun;
    private CancellationTokenSource? _toolCts;
    private readonly CheckItem _device = new() { TitleKey = "pf_check_device", FixKey = "pf_fix_device" },
        _signal = new() { TitleKey = "pf_check_signal", FixKey = "pf_fix_signal" },
        _auth = new() { TitleKey = "pf_check_auth_dev", FixKey = "pf_fix_auth_dev" },
        _stt = new() { TitleKey = "pf_check_stt", FixKey = "pf_fix_stt_dev" },
        _answer = new() { TitleKey = "pf_check_answer", FixKey = "pf_fix_answer_dev" },
        _kb = new() { TitleKey = "pf_check_prepared", FixKey = "pf_fix_prepared" },
        _bank = new() { TitleKey = "pf_check_bank", FixKey = "pf_fix_bank" },
        _topmost = new() { TitleKey = "pf_check_topmost", FixKey = "pf_fix_topmost" };
    private IEnumerable<CheckItem> All => new[] { _device, _signal, _auth, _stt, _answer, _kb, _bank, _topmost };

    public ReadinessWindow(MainViewModel vm, MainWindow main, bool autoRun = true)
    {
        InitializeComponent();
        _vm = vm; _main = main; _autoRun = autoRun;
        DataContext = vm;
        var cloudAuth = new CheckItem { TitleKey = "pf_check_account", FixKey = "pf_fix_account" };
        if (vm.UsingCloud)
        {
            _auth = cloudAuth;
            _answer.FixKey = "pf_fix_answer";
            _stt.FixKey = "pf_fix_stt";
        }
        Checks.ItemsSource = new ObservableCollection<CheckItem>(All);
        Loaded += (_, _) => { WindowPlacement.FitToWorkArea(this); if (_autoRun) _ = RunAllAsync(); };
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => { _toolCts?.Cancel(); Loc.LanguageChanged -= OnLanguageChanged; };
    }

    private void OnLanguageChanged() => Dispatcher.BeginInvoke(() => { foreach (var c in All) c.RefreshTitle(); UpdateVerdict(); });

    private static string T(string key) => Loc.T(key);
    private static string F(string key, params object?[] args) => Loc.F(key, args);

    // ---------------- checks ----------------

    public async Task RunAllAsync()
    {
        RunBtn.IsEnabled = false;
        try
        {
            CheckLocal();
            if (_vm.UsingCloud) { await RunCloudChecksAsync(); return; }
            var key = SecretStore.GetApiKey();
            var signalTask = CheckSignalAsync(TimeSpan.FromSeconds(10));
            _auth.Busy(T("pf_busy_key"));
            var auth = await OpenAiAuthProbe.CheckAsync(key);
            if (auth.Ok)
            {
                _auth.Pass(auth.Detail);
                _answer.Busy(T("pf_busy_answer"));
                var sttTask = CheckTranscriptionConnectAsync();
                var r = await AnswerServiceProbe.RunAsync(SecretStore.GetApiKey, _vm.Settings.AnswerModel);
                if (r.Ok) _answer.Pass(F("pf_answer_ok", _vm.Settings.AnswerModel, r.FirstTokenMs));
                else _answer.Fail(r.Error ?? T("err_unknown"));
                await sttTask;
            }
            else
            {
                if (auth.Kind == ProviderErrorKind.Network) _auth.Fail(T("svc_unreachable")); else _auth.Fail(auth.Detail);
                _answer.Warn(T("pf_skipped_key"));
                _stt.Warn(T("pf_skipped_key"));
            }
            await signalTask;
        }
        finally { RunBtn.IsEnabled = true; UpdateVerdict(); }
    }

    /// <summary>Account mode: entitlements from the server, answer test through the server proxy (typed-question path, no live minutes).</summary>
    private async Task RunCloudChecksAsync()
    {
        var signalTask = CheckSignalAsync(TimeSpan.FromSeconds(10));
        _auth.Busy(T("pf_busy_account"));
        var refreshed = _vm.Cloud.IsSignedIn && await _vm.Cloud.RefreshAsync(CancellationToken.None);
        if (!_vm.Cloud.IsConfigured) { _auth.Fail(T("acct_not_configured_long")); _answer.Warn(T("pf_skipped_service")); _stt.Warn(T("pf_skipped_service")); }
        else if (!_vm.Cloud.IsSignedIn) { _auth.Fail(Loc.Relocalize(_vm.Cloud.Message) ?? T("note_sign_in_to_listen")); _answer.Warn(T("pf_skipped_sign_in")); _stt.Warn(T("pf_skipped_sign_in")); }
        else if (!refreshed || _vm.Cloud.Account is not { } a) { _auth.Fail(Loc.Relocalize(_vm.Cloud.Message) ?? T("svc_unreachable")); _answer.Warn(T("pf_skipped_unreachable")); _stt.Warn(T("pf_skipped_unreachable")); }
        else
        {
            var mins = a.Usage.LiveSecondsRemaining / 60;
            var today = a.Usage.LiveSecondsAvailableToday >= 0 ? Math.Min(mins, a.Usage.LiveSecondsAvailableToday / 60) : mins;
            if (_vm.Cloud.Config?.Maintenance == true) _auth.Fail(_vm.Cloud.Config.MaintenanceMessage ?? T("svc_maintenance"));
            else if (mins <= 0) _auth.Fail(F("pf_no_minutes", MainViewModel.BillingLabel(a)));
            else
            {
                // The device must be registered (and not removed) for live sessions.
                string device;
                try { await _vm.Cloud.EnsureDeviceAsync(CancellationToken.None); device = T("pf_device_registered"); }
                catch (Client.BackendException ex) { device = ""; _auth.Fail(CloudService.Describe(ex)); }
                if (device.Length > 0)
                {
                    if (today <= 0) _auth.Warn(F("pf_account_today_used", a.Email, MainViewModel.BillingLabel(a)));
                    else _auth.Pass(F("pf_account_ok", a.Email, MainViewModel.BillingLabel(a), mins, today, device));
                }
            }
            if (mins > 0)
            {
                _answer.Busy(T("pf_busy_answer_service"));
                try
                {
                    var (answer, sample, model) = await _vm.TestAiAsync(TestQuestion, _ => { });
                    if (answer.Source == Core.Orchestration.AnswerSource.Llm && answer.Bullets.Count >= 2) _answer.Pass(F("pf_answer_ok_service", sample?.FinalizedToFirstBulletMs));
                    else _answer.Fail(Loc.Relocalize(answer.Note) ?? T("err_unknown"));
                }
                catch (Exception ex) when (ex is ProviderException or InvalidOperationException or Client.BackendException) { _answer.Fail(CloudService.Describe(ex)); }
                _stt.Warn(T("pf_stt_run_test"));
            }
        }
        await signalTask;
    }

    private bool ToolReady(string titleKey)
    {
        if (_vm.UsingCloud ? _vm.Cloud.IsSignedIn : SecretStore.HasKey) return true;
        BeginTool(T(titleKey));
        ToolOutput.Text = T(_vm.UsingCloud ? "pf_sign_in_first" : "pf_key_first");
        return false;
    }

    /// <summary>Checks that need no network or audio (also used by --selftest).</summary>
    public void CheckLocal()
    {
        _vm.StartAudioPreview();
        var cap = _vm.Capture;
        if (cap.Status == "ACTIVE") _device.Pass($"{cap.CurrentDeviceName} · {cap.SampleRate} Hz · {cap.Channels} ch");
        else _device.Fail(string.IsNullOrEmpty(cap.CurrentDeviceName) ? T("pf_no_device") : $"{cap.CurrentDeviceName} · {cap.StatusLabel}");

        if (_vm.Knowledge is { } k)
        {
            if (k.Questions.Count > 0) _kb.Pass(F("pf_prepared_ok", _vm.ActiveLabel, k.Profile.Experience.Count, k.Stories.Count, k.Context.TranscriptionTerms().Count));
            else _kb.Warn(T("pf_prepared_none"));
            if (k.Questions.Count >= 50) _bank.Pass(F("pf_bank_ok", k.Questions.Count));
            else if (k.Questions.Count == 0) _bank.Warn(T("pf_bank_none"));
            else _bank.Fail(F("pf_bank_incomplete", k.Questions.Count));
        }
        else { _kb.Fail(T("pf_prepared_none")); _bank.Fail(T("pf_bank_none")); }

        if (_main.Topmost) _topmost.Pass(T("pf_topmost_on"));
        else _topmost.Warn(T("pf_topmost_off"));
        UpdateVerdict();
    }

    private async Task CheckSignalAsync(TimeSpan window)
    {
        _signal.Busy(T("pf_busy_signal"));
        var start = _vm.Capture.FramesWithSignal;
        var until = DateTime.UtcNow + window;
        while (DateTime.UtcNow < until && _vm.Capture.FramesWithSignal - start < 5) await Task.Delay(100);
        if (_vm.Capture.FramesWithSignal - start >= 5) _signal.Pass(F("pf_signal_ok", Math.Round(_vm.Capture.LastPeakDb), _vm.Capture.CurrentDeviceName));
        else _signal.Warn(F("pf_signal_none", window.TotalSeconds, _vm.Capture.CurrentDeviceName));
        UpdateVerdict();
    }

    private async Task CheckTranscriptionConnectAsync()
    {
        _stt.Busy(F("pf_busy_stt", _vm.Settings.TranscriptionModel));
        await using var t = new OpenAiRealtimeTranscriber(SecretStore.GetApiKey, _vm.TranscriberOptions());
        var tcs = new TaskCompletionSource<(ConnectionStatus, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.StatusChanged += (s, d) => { if (s is ConnectionStatus.Connected or ConnectionStatus.Failed) tcs.TrySetResult((s, d)); };
        await t.StartAsync(CancellationToken.None);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        if (done == tcs.Task && tcs.Task.Result.Item1 == ConnectionStatus.Connected)
        {
            await Task.Delay(2000); // give the server time to reject the session config / trigger model fallback
            if (t.Status == ConnectionStatus.Connected) _stt.Pass(F("pf_stt_accepted", t.ActiveModel));
            else _stt.Warn(T("pf_stt_unstable"));
        }
        else _stt.Fail(done == tcs.Task ? tcs.Task.Result.Item2 ?? T("pf_stt_failed") : T("svc_timeout"));
    }

    private void UpdateVerdict()
    {
        var all = All.ToList();
        var res = Application.Current.Resources;
        var pending = all.Any(c => c.Passed == null);
        var failed = all.Where(c => c.Passed == false && !c.IsWarning).ToList();
        var warnings = all.Where(c => c.IsWarning).ToList();
        if (all.All(c => c.Passed == true) || !pending && failed.Count == 0 && warnings.All(w => w == _stt || w == _topmost || w == _bank || w == _kb))
        {
            VerdictTitle.Text = "✓ " + T("ready");
            VerdictTitle.Foreground = (Brush)res["B.Listening"];
            VerdictDetail.Text = warnings.Count == 0 ? T("pf_verdict_ready") : T("pf_verdict_ready") + "\n" + string.Join("\n", warnings.Select(c => $"• {c.Title}: {c.FixHint}"));
        }
        else
        {
            VerdictTitle.Text = pending ? T("pf_checking_all") : T("pf_not_ready");
            VerdictTitle.Foreground = (Brush)res[pending ? "B.Accent" : "B.Thinking"];
            VerdictDetail.Text = string.Join("\n", failed.Concat(warnings).Select(c => $"• {c.Title}: {c.FixHint}"));
            if (!pending && failed.All(c => c == _signal || c == _stt || c == _answer || c == _auth))
                VerdictDetail.Text += "\n" + T("pf_typed_still_work");
        }
    }

    // ---------------- test tools ----------------

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
        ToolResult.Foreground = (Brush)Application.Current.Resources[ok ? "B.Listening" : "B.Thinking"];
        ToolResult.Visibility = Visibility.Visible;
    }

    private void Diagnostic(string line)
    {
        ToolDiagnostics.Visibility = Visibility.Visible;
        ToolDiagnosticsText.Text = (ToolDiagnosticsText.Text.Length > 0 ? ToolDiagnosticsText.Text + "\n" : "") + line;
    }

    private async void TestAudio_Click(object sender, RoutedEventArgs e)
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
        var ok = cap.FramesWithSignal - start >= 5;
        ShowResult(ok, T(ok ? "pf_audio_passed" : "pf_audio_failed"));
        Diagnostic($"{cap.SampleRate} Hz · {cap.Channels} ch · {cap.Encoding} → 24000 Hz mono PCM16 · {cap.Status} · peak {maxPeak:0.0} dBFS");
        if (ok) _signal.Pass(F("pf_signal_ok", Math.Round(maxPeak), cap.CurrentDeviceName)); else _signal.Warn(T("pf_audio_failed"));
        UpdateVerdict();
    }

    /// <summary>
    /// Transcription test on the production path. The test connection is closed on purpose when it ends, so the
    /// result reads "Test complete · Transcription test passed" — the technical connection log is under Technical details.
    /// </summary>
    private async void TestStt_Click(object sender, RoutedEventArgs e)
    {
        if (!ToolReady("pf_stt_title")) return;
        BeginTool(T("pf_stt_title"));
        var ct = _toolCts!.Token;
        string transcript = "";
        void Render() => ToolOutput.Text = transcript.Length > 0 ? transcript : T("pf_stt_waiting");
        Render();
        var result = await _vm.TestTranscriptionAsync(t => { transcript = t; Render(); }, s => Diagnostic(s), TimeSpan.FromSeconds(25), ct);
        var ok = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
        ShowResult(ok, ok ? T("pf_stt_passed") : T("pf_stt_no_text"));
        Diagnostic(result);
        if (ok) _stt.Pass(T("pf_stt_passed")); else if (_stt.Passed != true) _stt.Warn(T("pf_stt_no_text"));
        UpdateVerdict();
    }

    private async void TestAi_Click(object sender, RoutedEventArgs e)
    {
        if (!ToolReady("pf_ai_title")) return;
        BeginTool(F("pf_ai_title_q", TestQuestion));
        var sb = new StringBuilder();
        ToolOutput.Text = T("pf_ai_requesting");
        try
        {
            var (answer, sample, model) = await _vm.TestAiAsync(TestQuestion, b => { sb.AppendLine("• " + b); ToolOutput.Text = sb.ToString(); });
            if (sample != null) Diagnostic($"{model} · first token {sample.FinalizedToFirstTokenMs} ms · first bullet {sample.FinalizedToFirstBulletMs} ms · complete {sample.FinalizedToCompleteMs} ms");
            var ok = answer.Bullets.Count is >= 2 and <= 4 && answer.Source == Core.Orchestration.AnswerSource.Llm;
            ShowResult(ok, ok ? T("pf_ai_passed") : Loc.Relocalize(answer.Note) ?? T("err_unknown"));
            if (ok) _answer.Pass(F("pf_answer_ok_service", sample?.FinalizedToFirstBulletMs)); else _answer.Fail(Loc.Relocalize(answer.Note) ?? T("err_unknown"));
        }
        catch (Exception ex) when (ex is ProviderException or InvalidOperationException or Client.BackendException) { ShowResult(false, CloudService.Describe(ex)); }
        UpdateVerdict();
    }

    // ---------------- buttons ----------------
    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunAllAsync();
    private void Settings_Click(object sender, RoutedEventArgs e) { _main.OpenSettings(); _ = RunAllAsync(); }

    /// <summary>Starts listening immediately (no second Start button) and confirms it.</summary>
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (!_vm.IsListening) await _vm.ToggleListeningAsync(startToast: "toast_interview_started");
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }
}
