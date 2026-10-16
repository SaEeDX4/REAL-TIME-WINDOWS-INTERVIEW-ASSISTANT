using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using InterviewAssistant.App.UiState;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App.Views;

/// <summary>
/// Settings as a page of the Home window (sidebar stays visible). Explicit Save model: edits are pending until Save
/// (the sticky footer shows "Unsaved changes" with Save / Discard changes); leaving the page, closing Home or signing
/// out with pending edits asks first (shared <see cref="DirtyStateGuard"/>). A UI-language switch re-renders texts
/// without touching the pending values. Saving never ends an interview: live-safe settings apply immediately, model
/// changes apply after Stop (<see cref="MainWindow.ApplySettings"/>).
/// </summary>
public partial class SettingsPage : UserControl
{
    private MainViewModel? _vm;
    private MainWindow? _main;
    private AppSettings S => _vm!.Settings;
    /// <summary>Audio device saved in settings (the picker previews a device live; Discard returns to this one).</summary>
    private string? _savedDeviceId;
    private bool _keyRemoved;

    public DirtyStateGuard Guard { get; } = new("settings");
    /// <summary>Raised after a successful save (Home shows the confirmation).</summary>
    public event Action? Saved;

    public SettingsPage()
    {
        InitializeComponent();
        ModelBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Guard.MarkDirty()));
        SttBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Guard.MarkDirty()));
        foreach (var box in new Control[] { HkToggle, HkStart, HkManual }) Field.ClearOnEdit(box);
        Guard.Save = Save;
        Guard.Discard = RevertPreview;
    }

    public void Initialize(MainViewModel vm, MainWindow main)
    {
        _vm = vm; _main = main;
        DataContext = vm;   // live level meter
        if (!BuildFlavor.Developer)
        {
            // Customer builds: AI runs through the Interview Assistant service; models are chosen server-side.
            DeveloperKeyPanel.Visibility = Visibility.Collapsed;
            foreach (var el in new FrameworkElement[] { ModelLabel, ModelBox, SttLabel, SttBox, ProtocolLabel, ProtocolBox }) el.Visibility = Visibility.Collapsed;
        }
        foreach (var m in new[] { "gpt-5.4-mini", "gpt-4.1-mini", "gpt-5-mini", "gpt-4.1" }) ModelBox.Items.Add(m);
        foreach (var m in new[] { "gpt-4o-transcribe", "gpt-4o-mini-transcribe" }) SttBox.Items.Add(m);
        foreach (var p in new[] { "auto", "ga", "beta" }) ProtocolBox.Items.Add(p);
        foreach (var f in Enum.GetValues<FontPreset>()) FontBox.Items.Add(new ComboBoxItem { Content = Loc.T("font_" + f.ToString().ToLowerInvariant()), Tag = f });
        LoadFromSettings();
    }

    /// <summary>Shows the stored values (only when nothing is pending: a pending edit is never overwritten).</summary>
    public void LoadFromSettings()
    {
        if (_vm == null || Guard.IsDirty) return;
        using (Guard.Suspend())
        {
            _savedDeviceId = S.PlaybackDeviceId;
            _keyRemoved = false;
            KeyBox.Clear();
            KeyStatus.Text = SecretStore.HasKey ? Loc.F("set_key_saved", SecretStore.Mask(SecretStore.GetApiKey())) : Loc.T("set_key_none");
            LoadDevices(S.PlaybackDeviceId);
            ModelBox.Text = S.AnswerModel;
            SttBox.Text = S.TranscriptionModel;
            ProtocolBox.SelectedItem = S.RealtimeProtocol;
            FontBox.SelectedItem = FontBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (FontPreset)i.Tag == S.FontPreset);
            FastCacheBox.IsChecked = S.UseFastCache;
            VadSlider.Value = S.VadSensitivity;
            OpacitySlider.Value = S.WindowOpacity;
            TopmostBox.IsChecked = S.AlwaysOnTop;
            HkToggle.Text = S.HotkeyToggleWindow;
            HkStart.Text = S.HotkeyStartPause;
            HkManual.Text = S.HotkeyManualInput;
            SaveTranscriptBox.IsChecked = S.SaveSessionTranscript;
            ClearErrors();
        }
    }

    /// <summary>UI language changed: re-render code-built texts in place; selections and pending values stay.</summary>
    public void Relocalize()
    {
        if (_vm == null) return;
        using (Guard.Suspend())
        {
            foreach (var item in FontBox.Items.Cast<ComboBoxItem>()) item.Content = Loc.T("font_" + ((FontPreset)item.Tag).ToString().ToLowerInvariant());
            // The "System default" device label is localized: rebuild the list, keep the chosen device.
            LoadDevices((DeviceBox.SelectedItem as PlaybackDevice)?.Id);
            KeyStatus.Text = Loc.Relocalize(KeyStatus.Text) ?? KeyStatus.Text;
            foreach (var m in new[] { HkToggleError, HkStartError, HkManualError }) m.Text = Loc.Relocalize(m.Text) ?? m.Text;
        }
    }

    /// <summary>Deep link: scrolls the section ("audio", "answers", "listening", "window", "hotkeys", "privacy") into view.</summary>
    public void ShowSection(string? section)
    {
        if (SectionHeader(section) is not { } header) return;
        Dispatcher.BeginInvoke(() =>
        {
            header.BringIntoView(new Rect(0, 0, header.ActualWidth, 400));
            (section == "audio" ? DeviceBox : section == "hotkeys" ? HkToggle : (Control?)null)?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private FrameworkElement? SectionHeader(string? section) => section switch
    {
        "audio" => SecAudio, "answers" => SecAnswers, "listening" => SecListening, "window" => SecWindow, "hotkeys" => SecHotkeys, "privacy" => SecPrivacy,
        _ => null,
    };

    private void SectionChip_Click(object sender, RoutedEventArgs e) => ShowSection((string)((FrameworkElement)sender).Tag);

    private void Changed(object sender, RoutedEventArgs e) => Guard.MarkDirty();
    private void SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Guard.MarkDirty();

    private void LoadDevices(string? selectId)
    {
        using (Guard.Suspend())
        {
            DeviceBox.Items.Clear();
            foreach (var d in AudioCaptureService.ListDevices()) DeviceBox.Items.Add(d);
            DeviceBox.SelectedItem = DeviceBox.Items.Cast<PlaybackDevice>().FirstOrDefault(d => d.Id == selectId) ?? DeviceBox.Items[0];
        }
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => LoadDevices((DeviceBox.SelectedItem as PlaybackDevice)?.Id);

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || DeviceBox.SelectedItem is not PlaybackDevice d) return;
        if (Guard.IsSuspended) return;   // list rebuilt by code, the same device stays selected
        // Live preview: the level meter follows the chosen device immediately; Save keeps it, Discard returns to the saved one.
        S.PlaybackDeviceId = d.Id;
        _vm.RestartAudio();
        Guard.MarkDirty();
    }

    private void RevertPreview()
    {
        if (_vm == null) return;
        if (S.PlaybackDeviceId != _savedDeviceId) { S.PlaybackDeviceId = _savedDeviceId; _vm.RestartAudio(); }
    }

    /// <summary>Discard changes: back to the stored values.</summary>
    public void Discard()
    {
        RevertPreview();
        Guard.MarkClean();
        LoadFromSettings();
    }

    private async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        var key = string.IsNullOrWhiteSpace(KeyBox.Password) ? SecretStore.GetApiKey() : KeyBox.Password.Trim();
        if (string.IsNullOrEmpty(key)) { KeyStatus.Text = Loc.T("set_key_enter"); return; }
        KeyStatus.Text = Loc.T("set_key_testing");
        var result = await AnswerServiceProbe.RunAsync(() => key, ModelBox.Text);
        KeyStatus.Text = result.Ok ? "✓ " + Loc.F("set_key_ok", result.FirstTokenMs, ModelBox.Text) : "✗ " + result.Error;
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        // Removing the key is immediate (a security action, not a pending edit): the engine is rebuilt right away.
        SecretStore.Clear();
        using (Guard.Suspend()) KeyBox.Clear();
        KeyStatus.Text = Loc.T("set_key_removed");
        _keyRemoved = true;
        _main?.ApplySettings(engineChanged: true, modelChanged: true);
    }

    private void ClearErrors()
    {
        foreach (var m in new[] { HkToggleError, HkStartError, HkManualError }) m.Clear();
        foreach (var b in new[] { HkToggle, HkStart, HkManual }) Field.SetHasError(b, false);
    }

    /// <summary>
    /// Validates and stores every pending value. Returns false (and shows the problem next to the field) when a hotkey
    /// is invalid or used twice — also by the fixed coach-mode and reset hotkeys.
    /// </summary>
    public bool Save()
    {
        if (_vm == null) return false;
        ClearErrors();
        var fields = new (TextBox Box, InlineMessage Error)[] { (HkToggle, HkToggleError), (HkStart, HkStartError), (HkManual, HkManualError) };
        var taken = new Dictionary<(uint, uint), string>
        {
            [Combo(S.HotkeyCoachToggle)] = S.HotkeyCoachToggle,
            [Combo(S.HotkeyResetAdaptive)] = S.HotkeyResetAdaptive,
        };
        TextBox? firstBad = null;
        foreach (var (box, error) in fields)
        {
            var text = box.Text.Trim();
            if (!HotkeyService.TryParse(text, out var mods, out var vk)) error.Show(Loc.F("set_hotkey_invalid", text), MessageKind.Error);
            else if (!taken.TryAdd((mods, vk), text)) error.Show(Loc.F("set_hotkey_conflict", text), MessageKind.Error);
            else continue;
            Field.SetHasError(box, true);
            firstBad ??= box;
        }
        if (firstBad != null) { ShowSection("hotkeys"); firstBad.Focus(); return false; }

        bool engineChanged = _keyRemoved, modelChanged = _keyRemoved;
        if (BuildFlavor.Developer && !string.IsNullOrWhiteSpace(KeyBox.Password)) { SecretStore.SetApiKey(KeyBox.Password); engineChanged = modelChanged = true; }
        var model = ModelBox.Text.Trim(); var stt = SttBox.Text.Trim(); var proto = ProtocolBox.SelectedItem as string ?? "auto";
        if (BuildFlavor.Developer && (model.Length > 0 && model != S.AnswerModel || stt.Length > 0 && stt != S.TranscriptionModel || proto != S.RealtimeProtocol))
            engineChanged = modelChanged = true;
        if (Math.Abs(VadSlider.Value - S.VadSensitivity) > 0.001 || FastCacheBox.IsChecked != S.UseFastCache) engineChanged = true;
        S.AnswerModel = model.Length > 0 ? model : S.AnswerModel;
        S.TranscriptionModel = stt.Length > 0 ? stt : S.TranscriptionModel;
        S.RealtimeProtocol = proto;
        S.FontPreset = FontBox.SelectedItem is ComboBoxItem { Tag: FontPreset f } ? f : S.FontPreset;
        S.UseFastCache = FastCacheBox.IsChecked == true;
        S.VadSensitivity = VadSlider.Value;
        S.WindowOpacity = OpacitySlider.Value;
        S.AlwaysOnTop = TopmostBox.IsChecked == true;
        S.HotkeyToggleWindow = HkToggle.Text.Trim();
        S.HotkeyStartPause = HkStart.Text.Trim();
        S.HotkeyManualInput = HkManual.Text.Trim();
        S.SaveSessionTranscript = SaveTranscriptBox.IsChecked == true;
        S.PlaybackDeviceId = (DeviceBox.SelectedItem as PlaybackDevice)?.Id;
        S.Save();
        _savedDeviceId = S.PlaybackDeviceId;
        _keyRemoved = false;
        using (Guard.Suspend()) KeyBox.Clear();
        KeyStatus.Text = SecretStore.HasKey ? Loc.F("set_key_saved", SecretStore.Mask(SecretStore.GetApiKey())) : Loc.T("set_key_none");
        Guard.MarkClean();
        _main?.ApplySettings(engineChanged, modelChanged);
        Saved?.Invoke();
        return true;

        static (uint, uint) Combo(string text) => HotkeyService.TryParse(text, out var m, out var k) ? (m, k) : (0u, 0u);
    }
}
