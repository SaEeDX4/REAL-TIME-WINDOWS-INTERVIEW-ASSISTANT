using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using InterviewAssistant.App.UiState;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App.Views;

/// <summary>
/// Settings as a page of the Home window (sidebar stays visible). One category at a time (Audio, Answers, Listening,
/// Window, Hotkeys, Privacy, Updates — <see cref="SettingsCategories"/>), chosen in a list on the reading-order start side;
/// deep links ("settings/audio") select a category. Explicit Save model: edits are pending until Save (the sticky footer
/// shows "Unsaved changes" with Save / Discard changes); switching category is not navigation and keeps pending edits;
/// leaving the page, closing Home or signing out with pending edits asks first (shared <see cref="DirtyStateGuard"/>).
/// "Reset this section" / "Restore all defaults" ask in an in-app dialog and then apply like a save
/// (<see cref="SettingsDefaults"/>). A UI-language switch re-renders texts without touching the pending values. Saving
/// never ends an interview: live-safe settings apply immediately, model changes apply after Stop
/// (<see cref="MainWindow.ApplySettings"/>). The update preference is saved right away (it applies after a restart).
/// </summary>
public partial class SettingsPage : UserControl
{
    private MainViewModel? _vm;
    private MainWindow? _main;
    private AppSettings S => _vm!.Settings;
    /// <summary>Audio device saved in settings (the picker previews a device live; Discard returns to this one).</summary>
    private string? _savedDeviceId;
    private bool _keyRemoved;
    private string _category = SettingsCategories.Audio;
    /// <summary>Last category shown in this app session: the Settings button reopens it.</summary>
    private static string? s_lastCategory;
    private int _selectingCategory;

    public DirtyStateGuard Guard { get; } = new("settings");
    /// <summary>Raised after a successful save (Home shows the confirmation).</summary>
    public event Action? Saved;
    /// <summary>Raised after defaults were restored: the category, or null for all (Home shows the confirmation).</summary>
    public event Action<string?>? DefaultsRestored;

    /// <summary>The category shown now (self-test).</summary>
    internal string CurrentCategory => _category;

    /// <summary>Secondary help shown as ⓘ next to the category heading.</summary>
    private static string? CategoryHintKey(string id) => id switch
    {
        SettingsCategories.Audio => "set_audio_hint",
        SettingsCategories.Hotkeys => "set_hotkeys_hint",
        _ => null,
    };

    public SettingsPage()
    {
        // Building the controls raises change events (a slider with Minimum 0.6 coerces its value, check boxes and
        // text boxes get their defaults): none of that is a user edit.
        using (Guard.Suspend()) InitializeComponent();
        // Editable combo boxes get their inner text when the template is applied (first layout, theme change): only
        // typing by the user (keyboard focus inside) is an edit.
        ModelBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => { if (ModelBox.IsKeyboardFocusWithin) Guard.MarkDirty(); }));
        SttBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => { if (SttBox.IsKeyboardFocusWithin) Guard.MarkDirty(); }));
        foreach (var box in new Control[] { HkToggle, HkStart, HkManual }) Field.ClearOnEdit(box);
        Guard.Save = Save;
        Guard.Discard = RevertPreview;
        BuildCategoryList();
        SelectCategory(SettingsCategories.Audio, focusField: false);
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
        using (Guard.Suspend())
        {
            foreach (var m in new[] { "gpt-5.4-mini", "gpt-4.1-mini", "gpt-5-mini", "gpt-4.1" }) ModelBox.Items.Add(m);
            foreach (var m in new[] { "gpt-4o-transcribe", "gpt-4o-mini-transcribe" }) SttBox.Items.Add(m);
            foreach (var p in new[] { "auto", "ga", "beta" }) ProtocolBox.Items.Add(p);
            foreach (var f in Enum.GetValues<FontPreset>()) FontBox.Items.Add(new ComboBoxItem { Content = Loc.T("font_" + f.ToString().ToLowerInvariant()), Tag = f });
        }
        LoadFromSettings();
        LoadUpdatePreference();
        RefreshUpdates();
    }

    // ================= categories =================

    private void BuildCategoryList()
    {
        foreach (var c in SettingsCategories.All)
        {
            var title = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            title.SetBinding(TextBlock.TextProperty, LocBinding(c.TitleKey));
            var description = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("B.TextFaint") };
            description.SetBinding(TextBlock.TextProperty, LocBinding(c.DescriptionKey));
            var item = new ListBoxItem { Tag = c.Id, Content = new StackPanel { Children = { title, description } } };
            item.SetBinding(AutomationProperties.NameProperty, LocBinding(c.TitleKey));
            item.SetBinding(AutomationProperties.HelpTextProperty, LocBinding(c.DescriptionKey));
            CategoryList.Items.Add(item);
        }
    }

    private static Binding LocBinding(string key) => new($"[{key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay };

    private IEnumerable<ListBoxItem> CategoryItems => CategoryList.Items.Cast<ListBoxItem>();
    private IEnumerable<FrameworkElement> CategoryPanels => new FrameworkElement[] { CatAudio, CatAnswers, CatListening, CatWindow, CatHotkeys, CatPrivacy, CatUpdates };

    /// <summary>
    /// Deep link: selects the category ("audio", "answers", "listening", "window", "hotkeys", "privacy", "updates"); an
    /// unknown or missing section shows the last used category, or Audio. Never navigation: pending edits stay.
    /// </summary>
    public void ShowSection(string? section) =>
        SelectCategory(SettingsCategories.Parse(section, s_lastCategory), focusField: SettingsCategories.IsKnown(section?.Trim().ToLowerInvariant()));

    private void SelectCategory(string id, bool focusField)
    {
        _category = id;
        s_lastCategory = id;
        _selectingCategory++;
        try { CategoryList.SelectedItem = CategoryItems.First(i => (string)i.Tag == id); }
        finally { _selectingCategory--; }
        foreach (var panel in CategoryPanels) panel.Visibility = (string)panel.Tag == id ? Visibility.Visible : Visibility.Collapsed;
        var c = SettingsCategories.All[SettingsCategories.IndexOf(id)];
        CategoryTitle.SetBinding(TextBlock.TextProperty, LocBinding(c.TitleKey));
        CategoryDescription.SetBinding(TextBlock.TextProperty, LocBinding(c.DescriptionKey));
        if (CategoryHintKey(id) is { } hint) CategoryInfo.SetBinding(InfoTip.TextProperty, LocBinding(hint));
        else { BindingOperations.ClearBinding(CategoryInfo, InfoTip.TextProperty); CategoryInfo.Text = ""; }
        UpdateItemStatus();
        if (id == SettingsCategories.Updates && _vm != null) RefreshUpdates();
        if (!focusField) return;
        Dispatcher.BeginInvoke(() =>
        {
            CategoryTitle.BringIntoView();
            (id switch { SettingsCategories.Audio => DeviceBox, SettingsCategories.Hotkeys => HkToggle, _ => (Control?)null })?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Screen readers: the selected row also says "current section" (selection itself is exposed by the list).</summary>
    private void UpdateItemStatus()
    {
        foreach (var item in CategoryItems) AutomationProperties.SetItemStatus(item, (string)item.Tag == _category ? Loc.T("set_cat_current") : "");
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingCategory > 0 || CategoryList.SelectedItem is not ListBoxItem { Tag: string id } || id == _category) return;
        SelectCategory(id, focusField: false);
    }

    /// <summary>Up/Down (and Home/End) move within the list and show that category; focus stays in the list.</summary>
    private void CategoryList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        string? next = e.Key switch
        {
            Key.Up => SettingsCategories.Move(_category, -1),
            Key.Down => SettingsCategories.Move(_category, +1),
            Key.Home => SettingsCategories.Ids[0],
            Key.End => SettingsCategories.Ids[^1],
            _ => null,
        };
        if (next == null) return;
        e.Handled = true;
        if (next != _category) SelectCategory(next, focusField: false);
        CategoryItems.First(i => (string)i.Tag == next).Focus();
    }

    // ================= values =================

    /// <summary>Shows the stored values (only when nothing is pending: a pending edit is never overwritten).</summary>
    public void LoadFromSettings()
    {
        if (_vm == null || Guard.IsDirty) return;
        using (Guard.Suspend())
        {
            _savedDeviceId = S.PlaybackDeviceId;
            _keyRemoved = false;
            foreach (var id in SettingsCategories.Ids) LoadCategory(id);
            ClearErrors();
        }
    }

    /// <summary>Shows the stored values of one category (the others keep what is on screen).</summary>
    private void LoadCategory(string id)
    {
        using (Guard.Suspend())
        {
            switch (id)
            {
                case SettingsCategories.Audio:
                    LoadDevices(S.PlaybackDeviceId);
                    break;
                case SettingsCategories.Answers:
                    KeyBox.Clear();
                    KeyStatus.Text = SecretStore.HasKey ? Loc.F("set_key_saved", SecretStore.Mask(SecretStore.GetApiKey())) : Loc.T("set_key_none");
                    ModelBox.Text = S.AnswerModel;
                    SttBox.Text = S.TranscriptionModel;
                    ProtocolBox.SelectedItem = S.RealtimeProtocol;
                    FontBox.SelectedItem = FontBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (FontPreset)i.Tag == S.FontPreset);
                    FastCacheBox.IsChecked = S.UseFastCache;
                    break;
                case SettingsCategories.Listening:
                    VadSlider.Value = S.VadSensitivity;
                    break;
                case SettingsCategories.Window:
                    OpacitySlider.Value = S.WindowOpacity;
                    TopmostBox.IsChecked = S.AlwaysOnTop;
                    break;
                case SettingsCategories.Hotkeys:
                    HkToggle.Text = S.HotkeyToggleWindow;
                    HkStart.Text = S.HotkeyStartPause;
                    HkManual.Text = S.HotkeyManualInput;
                    ClearErrors();
                    break;
                case SettingsCategories.Privacy:
                    SaveTranscriptBox.IsChecked = S.SaveSessionTranscript;
                    break;
                case SettingsCategories.Updates:
                    LoadUpdatePreference();
                    break;
            }
        }
    }

    /// <summary>True when anything on the page differs from the stored settings (a pending edit).</summary>
    private bool DiffersFromStored()
    {
        if ((DeviceBox.SelectedItem as PlaybackDevice)?.Id != _savedDeviceId) return true;
        if (BuildFlavor.Developer && (KeyBox.Password.Length > 0 || ModelBox.Text.Trim() is { Length: > 0 } m && m != S.AnswerModel
            || SttBox.Text.Trim() is { Length: > 0 } t && t != S.TranscriptionModel || (ProtocolBox.SelectedItem as string ?? "auto") != S.RealtimeProtocol)) return true;
        return (FontBox.SelectedItem is ComboBoxItem { Tag: FontPreset f } && f != S.FontPreset)
               || FastCacheBox.IsChecked != S.UseFastCache
               || Math.Abs(VadSlider.Value - S.VadSensitivity) > 0.001
               || Math.Abs(OpacitySlider.Value - S.WindowOpacity) > 0.001
               || TopmostBox.IsChecked != S.AlwaysOnTop
               || HkToggle.Text.Trim() != S.HotkeyToggleWindow || HkStart.Text.Trim() != S.HotkeyStartPause || HkManual.Text.Trim() != S.HotkeyManualInput
               || SaveTranscriptBox.IsChecked != S.SaveSessionTranscript;
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
            UpdateItemStatus();
            RefreshUpdates();
        }
    }

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
        if (firstBad != null)
        {
            // the problem is shown in its category (made visible first, so the field can take focus after layout)
            SelectCategory(SettingsCategories.Hotkeys, focusField: false);
            Dispatcher.BeginInvoke(() => firstBad.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
            return false;
        }

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

    // ================= reset to defaults =================

    private async void ResetSection_Click(object sender, RoutedEventArgs e) => await ResetAsync(_category);
    private async void RestoreAll_Click(object sender, RoutedEventArgs e) => await ResetAsync(null);

    /// <summary>
    /// "Reset this section" (<paramref name="category"/>) or "Restore all defaults" (null): asks in the in-app dialog
    /// (Cancel is the default), then restores. False = cancelled, nothing changed.
    /// </summary>
    internal async Task<bool> ResetAsync(string? category)
    {
        if (_vm == null || Window.GetWindow(this) is not { } owner) return false;
        var message = category == null
            ? Loc.T("set_restore_body")
            : Loc.F("set_restore_section_body", Loc.T(SettingsCategories.All[SettingsCategories.IndexOf(category)].TitleKey));
        if (!await AppDialog.ConfirmAsync(owner, message, Loc.T("set_restore_confirm"), destructive: true, title: Loc.T("set_restore_title"))) return false;
        ApplyReset(category);
        return true;
    }

    /// <summary>
    /// Restores the defaults of one category (or all) like a save: stored, applied (<see cref="MainWindow.ApplySettings"/>)
    /// and confirmed. Only that category's fields change (<see cref="SettingsDefaults"/>); the account, profiles, interviews,
    /// reports, window placement and UI language never do. Pending edits of other categories stay pending.
    /// </summary>
    internal void ApplyReset(string? category)
    {
        if (_vm == null) return;
        var preview = S.PlaybackDeviceId;   // an unsaved device preview is not a stored value
        S.PlaybackDeviceId = _savedDeviceId;
        var changed = SettingsDefaults.Reset(S, new AppSettings(), category);
        S.Save();
        _savedDeviceId = S.PlaybackDeviceId;
        if (category == null)
        {
            Guard.MarkClean();
            LoadFromSettings();
        }
        else
        {
            if (category != SettingsCategories.Audio) S.PlaybackDeviceId = preview;   // a device preview elsewhere stays previewed
            LoadCategory(category);
            if (!DiffersFromStored()) Guard.MarkClean();
        }
        if (changed.Contains("UpdateChannel")) UpdateStatus.Text = Loc.T("updates_channel_restart");
        var (engineChanged, modelChanged) = SettingsDefaults.EffectOf(changed);
        _main?.ApplySettings(engineChanged, modelChanged);
        DefaultsRestored?.Invoke(category);
    }

    // ================= updates =================

    private void LoadUpdatePreference()
    {
        if (_vm == null) return;
        using (Guard.Suspend()) ChannelBox.SelectedIndex = UpdatePolicyChannel(S.UpdateChannel) == "beta" ? 1 : 0;
    }

    private static string UpdatePolicyChannel(string? channel) => InterviewAssistant.Client.UpdatePolicy.NormalizeChannel(channel);

    /// <summary>Version, channel, build and the update service's state (called when the service reports a change).</summary>
    public void RefreshUpdates()
    {
        if (_vm == null) return;
        var u = _vm.Updates;
        UpdateVersion.Text = BuildInfo.Version;
        UpdateChannelText.Text = BuildInfo.ChannelLabel;
        UpdateCommit.Text = BuildInfo.Commit;
        UpdateStatus.Text = Loc.Relocalize(u.StatusText) ?? "";
        RestartUpdateBtn.Visibility = u.UpdateReady ? Visibility.Visible : Visibility.Collapsed;
        // Builds that cannot update (unsigned, portable, no feed) keep the button disabled; the status says why.
        CheckUpdatesBtn.Visibility = u.UpdateReady ? Visibility.Collapsed : Visibility.Visible;
        CheckUpdatesBtn.IsEnabled = u.CanCheck && !u.IsChecking;
    }

    private void Channel_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Saved right away, not through the Save footer: the preference only takes effect after a restart.
        if (_vm == null || Guard.IsSuspended || ChannelBox.SelectedItem is not ComboBoxItem { Tag: string ch } || ch == S.UpdateChannel) return;
        S.UpdateChannel = ch;
        S.Save();
        UpdateStatus.Text = Loc.T("updates_channel_restart");
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.Updates.CheckNowAsync(_vm.SessionActive);
        RefreshUpdates();
    }

    private async void RestartUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (!_vm.Updates.TryApplyAndRestart(_vm.SessionActive) && Window.GetWindow(this) is { } owner)
            await AppDialog.AlertAsync(owner, Loc.T("warn_update_session"), DialogTone.Warning);
    }
}
