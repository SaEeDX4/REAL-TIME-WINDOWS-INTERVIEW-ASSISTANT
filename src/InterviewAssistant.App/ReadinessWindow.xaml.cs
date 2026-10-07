using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.App;

public sealed class CheckItem : ObservableObject
{
    public required string Title { get; init; }
    public required string FixHint { get; init; }
    private string _detail = ""; public string Detail { get => _detail; set => Set(ref _detail, value); }
    private string _state = "—"; public string State { get => _state; set => Set(ref _state, value); }
    private string _icon = ""; public string Icon { get => _icon; set => Set(ref _icon, value); }
    private Brush _brush = Brushes.Gray; public Brush Brush { get => _brush; set => Set(ref _brush, value); }
    public bool? Passed { get; private set; }

    public void Pass(string state, string detail) { Passed = true; Apply(state, detail, "", "B.Listening"); }
    public void Fail(string state, string detail) { Passed = false; Apply(state, detail, "", "B.Error"); }
    public void Warn(string state, string detail) { Passed = false; Apply(state, detail, "", "B.Thinking"); }
    public void Busy(string detail) { Passed = null; Apply("CHECKING", detail, "", "B.Accent"); }
    private void Apply(string s, string d, string i, string b) { State = s; Detail = d; Icon = i; Brush = (Brush)Application.Current.Resources[b]; }
}

/// <summary>Pre-interview check + test tools. All tests use the production classes (capture, transcriber, engine).</summary>
public partial class ReadinessWindow : Window
{
    public const string TestQuestion = "How would you prioritise competing requests from two important stakeholders?";
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    private readonly bool _autoRun;
    private CancellationTokenSource? _toolCts;
    private readonly CheckItem _device = new() { Title = "AUDIO DEVICE", FixHint = "Settings → choose the headset Meet plays through." },
        _signal = new() { Title = "AUDIO SIGNAL", FixHint = "Play speech in Chrome and confirm the meter moves; if flat, pick the exact headset in Settings." },
        _auth = new() { Title = "OPENAI AUTH", FixHint = "Settings → paste a valid OpenAI API key (check billing)." },
        _stt = new() { Title = "TRANSCRIPTION", FixHint = "Check internet; Settings → try another transcription model or protocol." },
        _answer = new() { Title = "ANSWER ENGINE", FixHint = "Settings → try answer model gpt-4.1-mini; check internet." },
        _kb = new() { Title = "INTERVIEW PREPARED", FixHint = "Create a profile, add a job and press Prepare interview." },
        _bank = new() { Title = "QUESTION BANK", FixHint = "Prepare the interview (or re-run preparation)." },
        _topmost = new() { Title = "WINDOW TOPMOST", FixHint = "Settings → enable Always on top." };

    public ReadinessWindow(MainViewModel vm, MainWindow main, bool autoRun = true)
    {
        InitializeComponent();
        _vm = vm; _main = main; _autoRun = autoRun;
        DataContext = vm;
        Checks.ItemsSource = new ObservableCollection<CheckItem> { _device, _signal, _auth, _stt, _answer, _kb, _bank, _topmost };
        Loaded += (_, _) => { if (_autoRun) _ = RunAllAsync(); };
        Closed += (_, _) => _toolCts?.Cancel();
    }

    // ---------------- checks ----------------

    public async Task RunAllAsync()
    {
        RunBtn.IsEnabled = false;
        try
        {
            CheckLocal();
            var key = SecretStore.GetApiKey();
            var signalTask = CheckSignalAsync(TimeSpan.FromSeconds(10));
            _auth.Busy("Verifying key (no tokens used)…");
            var auth = await OpenAiAuthProbe.CheckAsync(key);
            if (auth.Ok)
            {
                _auth.Pass("OK", auth.Detail);
                _answer.Busy("Generating a test answer…");
                var sttTask = CheckTranscriptionConnectAsync();
                var r = await AnswerServiceProbe.RunAsync(SecretStore.GetApiKey, _vm.Settings.AnswerModel);
                if (r.Ok) _answer.Pass("READY", $"{_vm.Settings.AnswerModel} · first token {r.FirstTokenMs} ms" + (r.FirstTokenMs > 2500 ? " (slow network?)" : ""));
                else _answer.Fail("ERROR", r.Error ?? "Unknown error");
                await sttTask;
            }
            else
            {
                _auth.Fail(auth.Kind == ProviderErrorKind.Network ? "OFFLINE" : "FAILED", auth.Detail);
                _answer.Warn("SKIPPED", "Needs working API key — prepared answers still work offline.");
                _stt.Warn("SKIPPED", "Needs working API key.");
            }
            await signalTask;
        }
        finally { RunBtn.IsEnabled = true; UpdateVerdict(); }
    }

    /// <summary>Checks that need no network or audio (also used by --selftest).</summary>
    public void CheckLocal()
    {
        _vm.StartAudioPreview();
        var cap = _vm.Capture;
        if (cap.Status == "ACTIVE") _device.Pass("ACTIVE", $"{cap.CurrentDeviceName} · {cap.SampleRate} Hz · {cap.Channels} ch · {cap.Encoding}");
        else _device.Fail(cap.Status, string.IsNullOrEmpty(cap.CurrentDeviceName) ? "No playback device available" : cap.CurrentDeviceName);

        if (_vm.Knowledge is { } k)
        {
            if (k.Questions.Count > 0) _kb.Pass("LOADED", $"{_vm.ActiveLabel} · {k.Profile.Experience.Count} roles · {k.Stories.Count} verified stories");
            else _kb.Warn("NOT PREPARED", "No interview prepared yet — AI answers still work, but prepared answers and your verified facts need a prepared interview.");
            if (k.Questions.Count >= 50) _bank.Pass("LOADED", $"{k.Questions.Count} prepared answers");
            else if (k.Questions.Count == 0) _bank.Warn("NONE", "Prepare an interview to get instant answers");
            else _bank.Fail("INCOMPLETE", $"Only {k.Questions.Count} prepared answers");
        }
        else { _kb.Fail("MISSING", "Knowledge folder not found"); _bank.Fail("MISSING", "question_bank.json not loaded"); }

        if (_main.Topmost) _topmost.Pass("ON", "Window stays above Chrome");
        else _topmost.Warn("OFF", "Window can be hidden behind Chrome");
        UpdateVerdict();
    }

    private async Task CheckSignalAsync(TimeSpan window)
    {
        _signal.Busy("Play speech in Chrome now…");
        var start = _vm.Capture.FramesWithSignal;
        var until = DateTime.UtcNow + window;
        while (DateTime.UtcNow < until && _vm.Capture.FramesWithSignal - start < 5) await Task.Delay(100);
        if (_vm.Capture.FramesWithSignal - start >= 5) _signal.Pass("DETECTED", $"Peak {_vm.Capture.LastPeakDb:0} dBFS on {_vm.Capture.CurrentDeviceName}");
        else _signal.Warn("NO AUDIO", $"Nothing heard in {window.TotalSeconds:0} s on {_vm.Capture.CurrentDeviceName}");
        UpdateVerdict();
    }

    private async Task CheckTranscriptionConnectAsync()
    {
        _stt.Busy($"Connecting ({_vm.Settings.TranscriptionModel})…");
        await using var t = new OpenAiRealtimeTranscriber(SecretStore.GetApiKey, _vm.TranscriberOptions());
        var tcs = new TaskCompletionSource<(ConnectionStatus, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.StatusChanged += (s, d) => { if (s is ConnectionStatus.Connected or ConnectionStatus.Failed) tcs.TrySetResult((s, d)); };
        await t.StartAsync(CancellationToken.None);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        if (done == tcs.Task && tcs.Task.Result.Item1 == ConnectionStatus.Connected)
        {
            await Task.Delay(2000); // give the server time to reject the session config / trigger model fallback
            if (t.Status == ConnectionStatus.Connected) _stt.Pass("READY", $"Session accepted · {t.ActiveModel}");
            else _stt.Warn("RETRYING", $"Session not stable ({t.Status})");
        }
        else _stt.Fail("ERROR", done == tcs.Task ? tcs.Task.Result.Item2 ?? "Connection failed" : "Timed out connecting");
    }

    private void UpdateVerdict()
    {
        var all = new[] { _device, _signal, _auth, _stt, _answer, _kb, _bank, _topmost };
        var res = Application.Current.Resources;
        if (all.All(c => c.Passed == true))
        {
            VerdictTitle.Text = "✓ READY FOR INTERVIEW";
            VerdictTitle.Foreground = (Brush)res["B.Listening"];
            VerdictDetail.Text = "Press Start interview. Keep this window near your camera.";
        }
        else
        {
            var pending = all.Any(c => c.Passed == null);
            var failed = all.Where(c => c.Passed == false).ToList();
            VerdictTitle.Text = pending ? "CHECKING…" : "NOT READY — fix the items below";
            VerdictTitle.Foreground = (Brush)res[pending ? "B.Accent" : "B.Thinking"];
            VerdictDetail.Text = string.Join("\n", failed.Select(c => $"• {c.Title}: {c.FixHint}"));
            if (!pending && failed.All(c => c == _signal || c == _stt || c == _answer || c == _auth))
                VerdictDetail.Text += "\nManual typed questions with prepared answers still work.";
        }
    }

    // ---------------- test tools ----------------

    private void BeginTool(string title)
    {
        _toolCts?.Cancel();
        _toolCts = new CancellationTokenSource();
        ToolTitle.Text = title;
        ToolOutput.Text = "";
    }

    private async void TestAudio_Click(object sender, RoutedEventArgs e)
    {
        BeginTool("AUDIO TEST — play YouTube/music/speech in Chrome (15 s)");
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
                .AppendLine($"Endpoint:     {cap.CurrentDeviceName}")
                .AppendLine($"Format:       {cap.SampleRate} Hz · {cap.Channels} ch · {cap.Encoding} → 24000 Hz mono PCM16")
                .AppendLine($"Capture:      {cap.Status}")
                .AppendLine($"Peak / RMS:   {cap.LastPeakDb,6:0.0} / {cap.LastRmsDb,6:0.0} dBFS   (max peak {maxPeak:0.0})")
                .Append(detected ? "Result:       ✓ AUDIO DETECTED" : "Result:       … NO AUDIO DETECTED YET").ToString();
        };
        timer.Start();
        try { await Task.Delay(15_000, ct); } catch (OperationCanceledException) { }
        timer.Stop();
        var ok = cap.FramesWithSignal - start >= 5;
        ToolOutput.Text += ok ? "" : "\n\nNo sound reached the app. Check: Chrome is playing, the volume isn't muted, and Settings → device matches where Meet plays.";
        if (ok) _signal.Pass("DETECTED", $"Peak {maxPeak:0} dBFS on {cap.CurrentDeviceName}"); else _signal.Warn("NO AUDIO", "Audio test heard nothing");
        UpdateVerdict();
    }

    private async void TestStt_Click(object sender, RoutedEventArgs e)
    {
        if (!SecretStore.HasKey) { BeginTool("TRANSCRIPTION TEST"); ToolOutput.Text = "Add your API key in Settings first."; return; }
        BeginTool("TRANSCRIPTION TEST — play spoken English in Chrome (25 s)");
        var ct = _toolCts!.Token;
        string status = "connecting…", transcript = "";
        void Render() => ToolOutput.Text = $"[{status}]\n\n{(transcript.Length > 0 ? transcript : "(waiting for speech…)")}";
        Render();
        var result = await _vm.TestTranscriptionAsync(t => { transcript = t; Render(); }, s => { status = s; Render(); }, TimeSpan.FromSeconds(25), ct);
        var ok = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
        ToolOutput.Text += $"\n\n{(ok ? "✓ TRANSCRIPTION WORKS" : "✗ No transcript received")} ({result})";
        if (ok) _stt.Pass("WORKING", "Live transcript received · " + result); else if (_stt.Passed != true) _stt.Warn("NO TEXT", "No transcript — check audio test first");
        UpdateVerdict();
    }

    private async void TestAi_Click(object sender, RoutedEventArgs e)
    {
        if (!SecretStore.HasKey) { BeginTool("AI TEST"); ToolOutput.Text = "Add your API key in Settings first."; return; }
        BeginTool($"AI TEST — \"{TestQuestion}\"");
        var sb = new StringBuilder();
        ToolOutput.Text = "Requesting…";
        try
        {
            var (answer, sample, model) = await _vm.TestAiAsync(TestQuestion, b => { sb.AppendLine("• " + b); ToolOutput.Text = sb.ToString(); });
            sb.AppendLine();
            if (sample != null)
                sb.AppendLine($"Model {model} · first token {sample.FinalizedToFirstTokenMs} ms · first bullet {sample.FinalizedToFirstBulletMs} ms · complete {sample.FinalizedToCompleteMs} ms");
            var ok = answer.Bullets.Count is >= 2 and <= 4 && answer.Source == Core.Orchestration.AnswerSource.Llm;
            sb.AppendLine(ok ? "✓ ANSWER ENGINE WORKS" : "✗ " + (answer.Note ?? "Unexpected answer"));
            if (ok) _answer.Pass("READY", $"{model} · first bullet {sample?.FinalizedToFirstBulletMs} ms"); else _answer.Fail("ERROR", answer.Note ?? "No AI answer");
        }
        catch (Exception ex) when (ex is ProviderException or InvalidOperationException) { sb.AppendLine("✗ " + ex.Message); }
        ToolOutput.Text = sb.ToString();
        UpdateVerdict();
    }

    // ---------------- buttons ----------------
    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunAllAsync();
    private void Settings_Click(object sender, RoutedEventArgs e) { _main.OpenSettings(); _ = RunAllAsync(); }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (!_vm.IsListening) await _vm.ToggleListeningAsync();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
