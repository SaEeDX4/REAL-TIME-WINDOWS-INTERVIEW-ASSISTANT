using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.App;

public sealed class CheckItem : ObservableObject
{
    public required string Title { get; init; }
    private string _detail = ""; public string Detail { get => _detail; set => Set(ref _detail, value); }
    private string _state = "—"; public string State { get => _state; set => Set(ref _state, value); }
    private string _icon = ""; public string Icon { get => _icon; set => Set(ref _icon, value); }
    private Brush _brush = Brushes.Gray; public Brush Brush { get => _brush; set => Set(ref _brush, value); }

    public void Pass(string state, string detail) => Apply(state, detail, "", "B.Listening");
    public void Fail(string state, string detail) => Apply(state, detail, "", "B.Error");
    public void Warn(string state, string detail) => Apply(state, detail, "", "B.Thinking");
    public void Busy(string detail) => Apply("CHECKING", detail, "", "B.Accent");
    private void Apply(string s, string d, string i, string b) { State = s; Detail = d; Icon = i; Brush = (Brush)Application.Current.Resources[b]; }
}

/// <summary>First-run guide and pre-interview readiness: key → device → audio → transcription → answers → latency.</summary>
public partial class ReadinessWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    private readonly CheckItem _kb = new() { Title = "Knowledge" }, _key = new() { Title = "API key" }, _audio = new() { Title = "Audio" },
        _stt = new() { Title = "Transcription" }, _answer = new() { Title = "Answer service" }, _latency = new() { Title = "Latency test" };

    public ReadinessWindow(MainViewModel vm, MainWindow main)
    {
        InitializeComponent();
        _vm = vm; _main = main;
        DataContext = vm;
        Checks.ItemsSource = new ObservableCollection<CheckItem> { _kb, _key, _audio, _stt, _answer, _latency };
        Loaded += (_, _) => _ = RunAsync();
    }

    private async Task RunAsync()
    {
        RunBtn.IsEnabled = false;
        try
        {
            if (_vm.Knowledge is { } k) _kb.Pass("TEROXX / XAB LOADED", $"{k.Questions.Count} prepared answers · {k.Stories.Count} verified stories · {k.Snippets.Count} research notes");
            else _kb.Fail("MISSING", "Knowledge folder not found next to the app.");

            var key = SecretStore.GetApiKey();
            if (string.IsNullOrEmpty(key)) _key.Fail("MISSING", "Open Settings and paste your OpenAI API key. Manual questions still work with prepared answers.");
            else _key.Pass("SET", "Stored encrypted (DPAPI): " + SecretStore.Mask(key));

            var audioTask = CheckAudioAsync();
            if (!string.IsNullOrEmpty(key))
            {
                _answer.Busy("Contacting " + _vm.Settings.AnswerModel + "…");
                _latency.Busy("Measuring first-token latency…");
                var sttTask = CheckTranscriptionAsync();
                var r = await AnswerServiceProbe.RunAsync(SecretStore.GetApiKey, _vm.Settings.AnswerModel);
                if (r.Ok)
                {
                    _answer.Pass("READY", _vm.Settings.AnswerModel);
                    if (r.FirstTokenMs <= 2000) _latency.Pass("PASS", $"First token in {r.FirstTokenMs} ms");
                    else _latency.Warn("SLOW", $"First token in {r.FirstTokenMs} ms — prepared answers will still be instant. Check network.");
                }
                else { _answer.Fail("ERROR", r.Error ?? "Unknown error"); _latency.Fail("—", "Answer service unavailable"); }
                await sttTask;
            }
            else
            {
                _answer.Warn("SKIPPED", "Needs API key"); _stt.Warn("SKIPPED", "Needs API key"); _latency.Warn("SKIPPED", "Needs API key");
            }
            await audioTask;
        }
        finally { RunBtn.IsEnabled = true; }
    }

    private async Task CheckAudioAsync()
    {
        _vm.StartAudioPreview();
        _audio.Busy("Play something in Chrome now (Meet audio or any video)…");
        double peak = 0;
        for (int i = 0; i < 100 && peak < 0.3; i++) { await Task.Delay(100); peak = Math.Max(peak, _vm.AudioLevel); }
        var dev = string.IsNullOrEmpty(_vm.CurrentDeviceName) ? "No device" : _vm.CurrentDeviceName;
        if (peak >= 0.3) _audio.Pass("ACTIVE", dev + " — audio detected");
        else _audio.Warn("NO SIGNAL", dev + " — no sound heard in 10 s. Play audio in Chrome, or pick the headset in Settings.");
    }

    private async Task CheckTranscriptionAsync()
    {
        _stt.Busy("Connecting to realtime transcription (" + _vm.Settings.TranscriptionModel + ")…");
        var t = new OpenAiRealtimeTranscriber(SecretStore.GetApiKey, new RealtimeTranscriberOptions { Model = _vm.Settings.TranscriptionModel, Protocol = _vm.Settings.RealtimeProtocol });
        var tcs = new TaskCompletionSource<(ConnectionStatus, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? lastError = null;
        t.Diagnostic += m => lastError = m;
        t.StatusChanged += (s, d) => { if (s is ConnectionStatus.Connected or ConnectionStatus.Failed) tcs.TrySetResult((s, d)); };
        await t.StartAsync(CancellationToken.None);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(10_000));
        if (done == tcs.Task && tcs.Task.Result.Item1 == ConnectionStatus.Connected)
        {
            await Task.Delay(1500); // allow the server to reject the session configuration, if it will
            if (t.Status == ConnectionStatus.Connected) _stt.Pass("READY", "Realtime session accepted" + (tcs.Task.Result.Item2 is { } d ? $" ({d})" : ""));
            else _stt.Warn("RETRYING", lastError ?? "Session reconnecting");
        }
        else _stt.Fail("ERROR", done == tcs.Task ? tcs.Task.Result.Item2 ?? "Connection failed" : "Timed out connecting");
        await t.DisposeAsync();
    }

    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunAsync();
    private void Settings_Click(object sender, RoutedEventArgs e) { _main.OpenSettings(); _ = RunAsync(); }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (!_vm.IsListening) await _vm.ToggleListeningAsync();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
