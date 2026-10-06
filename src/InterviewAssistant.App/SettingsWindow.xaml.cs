using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.App;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _s;
    public bool EngineSettingsChanged { get; private set; }

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _s = vm.Settings;
        DataContext = vm; // for the live level meter
        KeyStatus.Text = SecretStore.HasKey ? "Saved key: " + SecretStore.Mask(SecretStore.GetApiKey()) : "No key saved yet.";
        LoadDevices();
        foreach (var m in new[] { "gpt-4.1-mini", "gpt-4.1", "gpt-4o-mini", "gpt-4o", "gpt-5-mini", "gpt-5" }) ModelBox.Items.Add(m);
        ModelBox.Text = _s.AnswerModel;
        foreach (var m in new[] { "gpt-4o-transcribe", "gpt-4o-mini-transcribe" }) SttBox.Items.Add(m);
        SttBox.Text = _s.TranscriptionModel;
        foreach (var p in new[] { "auto", "ga", "beta" }) ProtocolBox.Items.Add(p);
        ProtocolBox.SelectedItem = _s.RealtimeProtocol;
        foreach (var f in Enum.GetValues<FontPreset>()) FontBox.Items.Add(f);
        FontBox.SelectedItem = _s.FontPreset;
        FastCacheBox.IsChecked = _s.UseFastCache;
        VadSlider.Value = _s.VadSensitivity;
        OpacitySlider.Value = _s.WindowOpacity;
        TopmostBox.IsChecked = _s.AlwaysOnTop;
        HkToggle.Text = _s.HotkeyToggleWindow;
        HkStart.Text = _s.HotkeyStartPause;
        HkManual.Text = _s.HotkeyManualInput;
        SaveTranscriptBox.IsChecked = _s.SaveSessionTranscript;
    }

    private void LoadDevices()
    {
        DeviceBox.Items.Clear();
        foreach (var d in AudioCaptureService.ListDevices()) DeviceBox.Items.Add(d);
        DeviceBox.SelectedItem = DeviceBox.Items.Cast<PlaybackDevice>().FirstOrDefault(d => d.Id == _s.PlaybackDeviceId) ?? DeviceBox.Items[0];
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || DeviceBox.SelectedItem is not PlaybackDevice d) return;
        _s.PlaybackDeviceId = d.Id; // live preview: meter follows the chosen device immediately
        _vm.RestartAudio();
    }

    private async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        var key = string.IsNullOrWhiteSpace(KeyBox.Password) ? SecretStore.GetApiKey() : KeyBox.Password.Trim();
        if (string.IsNullOrEmpty(key)) { KeyStatus.Text = "Enter a key first."; return; }
        KeyStatus.Text = "Testing…";
        var result = await AnswerServiceProbe.RunAsync(() => key, ModelBox.Text);
        KeyStatus.Text = result.Ok ? $"✓ Answer service OK — first token in {result.FirstTokenMs} ms ({ModelBox.Text})" : "✗ " + result.Error;
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        SecretStore.Clear();
        KeyBox.Clear();
        KeyStatus.Text = "Key removed.";
        EngineSettingsChanged = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var hk in new[] { HkToggle.Text, HkStart.Text, HkManual.Text })
            if (!HotkeyService.TryParse(hk, out _, out _)) { KeyStatus.Text = $"Invalid hotkey: {hk} (example: Ctrl+Alt+Space)"; return; }

        if (!string.IsNullOrWhiteSpace(KeyBox.Password)) { SecretStore.SetApiKey(KeyBox.Password); EngineSettingsChanged = true; }
        var model = ModelBox.Text.Trim(); var stt = SttBox.Text.Trim(); var proto = ProtocolBox.SelectedItem as string ?? "auto";
        if (model != _s.AnswerModel || stt != _s.TranscriptionModel || proto != _s.RealtimeProtocol ||
            Math.Abs(VadSlider.Value - _s.VadSensitivity) > 0.001 || FastCacheBox.IsChecked != _s.UseFastCache) EngineSettingsChanged = true;
        _s.AnswerModel = model.Length > 0 ? model : _s.AnswerModel;
        _s.TranscriptionModel = stt.Length > 0 ? stt : _s.TranscriptionModel;
        _s.RealtimeProtocol = proto;
        _s.FontPreset = FontBox.SelectedItem is FontPreset f ? f : _s.FontPreset;
        _s.UseFastCache = FastCacheBox.IsChecked == true;
        _s.VadSensitivity = VadSlider.Value;
        _s.WindowOpacity = OpacitySlider.Value;
        _s.AlwaysOnTop = TopmostBox.IsChecked == true;
        _s.HotkeyToggleWindow = HkToggle.Text.Trim();
        _s.HotkeyStartPause = HkStart.Text.Trim();
        _s.HotkeyManualInput = HkManual.Text.Trim();
        _s.SaveSessionTranscript = SaveTranscriptBox.IsChecked == true;
        _s.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}

/// <summary>Minimal round-trip to the answer model to validate key + measure first-token latency.</summary>
public static class AnswerServiceProbe
{
    public sealed record Result(bool Ok, long FirstTokenMs, string? Error);

    public static async Task<Result> RunAsync(Func<string?> key, string model)
    {
        using var p = new OpenAiChatAnswerProvider(key, new ChatProviderOptions { Model = string.IsNullOrWhiteSpace(model) ? "gpt-4.1-mini" : model });
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await foreach (var _ in p.StreamAsync(new[] { new ChatMessage("user", "Reply with the single word: ready") }, 5, cts.Token))
                return new Result(true, sw.ElapsedMilliseconds, null);
            return new Result(false, -1, "No content returned");
        }
        catch (ProviderException ex) { return new Result(false, -1, ex.Message); }
        catch (OperationCanceledException) { return new Result(false, -1, "Timed out"); }
    }
}
