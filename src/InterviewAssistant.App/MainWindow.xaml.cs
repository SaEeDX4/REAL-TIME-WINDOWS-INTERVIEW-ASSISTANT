using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.ViewModels;

namespace InterviewAssistant.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private HotkeyService? _hotkeys;
    private bool _closingHandled, _closeRequested;

    public MainWindow()
    {
        InitializeComponent();
        Title = Branding.ProductName;
        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;
        _vm.Notify += (text, kind, detail) => Toasts.Show(text, detail, kind);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsListening))
                StartIcon.Text = _vm.IsListening ? "" : "";
        };
        _vm.BulletsChanged += ScheduleFit;
        AnswerScroll.SizeChanged += (_, _) => ScheduleFit();
        AnswerContent.SizeChanged += (_, _) => { if (!_fitting) ScheduleFit(); };   // note/pending/thinking rows too
        InitChrome();
        RestorePlacement();
        ApplyAppearance();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var error = _vm.Initialize();
        if (error != null) { ShowTransientError(error); return; }
        _firstUseHelpPending = !_vm.Settings.LiveIntroDismissed;   // Quick help opens by itself once (first use)
        RegisterHotkeys();
        _vm.StartAudioPreview(); // meter works before the interview starts
        AppLog.Info("Main window loaded");
        if (App.SelfTestOutput is { } outPath)
        {
            Dispatcher.BeginInvoke(ShowFirstUseHelp, DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(async () =>
            {
                App.RequestedExitCode = await SelfTest.RunAsync(this, _vm, outPath);
                Close();
            }, DispatcherPriority.ApplicationIdle);
            return;
        }
        // First run: onboarding (authorized-use acknowledgement, account, profile, interview) in Home.
        // Quick help then opens when the user comes back to the Live window (never over the onboarding).
        if (!_vm.Settings.AuthorizedUseAcknowledged || !_vm.Settings.FirstRunCompleted)
            Dispatcher.BeginInvoke(() => OpenHome(), DispatcherPriority.ApplicationIdle);
        else Dispatcher.BeginInvoke(ShowFirstUseHelp, DispatcherPriority.ApplicationIdle);
    }

    // ---------------- appearance & placement ----------------

    public void ApplyAppearance()
    {
        _vm.RefreshLanguage();
        Topmost = _vm.Settings.AlwaysOnTop;
        Root.Opacity = ShadowLayer.Opacity = Math.Clamp(_vm.Settings.WindowOpacity, 0.75, 1.0);
        ApplyCompact(_vm.Settings.CompactMode);
        _vm.RefreshFonts();
        RefreshReadiness();   // texts composed in code follow the UI language
    }

    public void SetCompact(bool compact) { _vm.Settings.CompactMode = compact; ApplyCompact(compact); }

    private void ApplyCompact(bool compact)
    {
        // Compact = question + bullets + status only. Controls stay reachable via hotkeys and by toggling back.
        var v = compact ? Visibility.Collapsed : Visibility.Visible;
        ActionBar.Visibility = v;
        ManualBar.Visibility = v;
        QuestionHeader.Visibility = v;
        AnswerHeader.Visibility = v;
        UpdateChrome();
    }

    private void RestorePlacement()
    {
        var s = _vm.Settings;
        Width = Math.Clamp(s.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Clamp(s.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        if (s.Left is double l && s.Top is double t)
        {
            // Only restore if the title area is still on a connected monitor (monitor unplugged, resolution/DPI changed).
            if (WindowPlacement.IsVisibleOnAnyMonitor(l, t, Width))
            { Left = l; Top = t; WindowStartupLocation = WindowStartupLocation.Manual; return; }
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Top + 80;
    }

    private void SavePlacement()
    {
        var s = _vm.Settings;
        // Maximized or minimized: the placement to come back to is the restore bounds, never the work area.
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!r.IsEmpty && r.Width > 0) { s.Left = r.Left; s.Top = r.Top; s.Width = r.Width; s.Height = r.Height; }
        s.Save();
    }

    // ---------------- hotkeys ----------------

    private void RegisterHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyService(new WindowInteropHelper(this).Handle);
        var s = _vm.Settings;
        var problems = new[]
        {
            _hotkeys.Register(s.HotkeyToggleWindow, ToggleVisibility),
            _hotkeys.Register(s.HotkeyStartPause, () => _ = _vm.ToggleListeningAsync()),
            _hotkeys.Register(s.HotkeyManualInput, FocusManualInput),
            _hotkeys.Register(s.HotkeyCoachToggle, _vm.ToggleCoach),
            _hotkeys.Register(s.HotkeyResetAdaptive, _vm.ResetAdaptive),
        }.Where(p => p != null).ToList();
        foreach (var p in problems) AppLog.Warn(p!);
        if (problems.Count > 0) ShowTransientError(string.Join("\n", problems));
    }

    private void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else { Show(); WindowState = WindowState.Normal; }
    }

    private void FocusManualInput()
    {
        if (!IsVisible) Show();
        if (_vm.Settings.CompactMode) { _vm.Settings.CompactMode = false; ApplyCompact(false); }
        Activate(); // explicit user request: the only time we take focus
        ManualInput.Focus();
    }

    // ---------------- handlers ----------------

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }   // like a native title bar
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private async void ManualInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SubmitManualAsync();
    }

    private async void ManualSend_Click(object sender, RoutedEventArgs e) => await SubmitManualAsync();

    private async Task SubmitManualAsync()
    {
        var text = ManualInput.Text;
        ManualInput.Clear();
        await _vm.SubmitManualAsync(text);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) { CloseMore(); OpenSettings(); }
    private void Home_Click(object sender, RoutedEventArgs e) { CloseMore(); OpenHome(); }
    private void Banner_Click(object sender, RoutedEventArgs e) => OpenHome("account");
    private void Presentation_Click(object sender, RoutedEventArgs e) { _vm.ToggleCoach(); UpdateChrome(); }

    /// <summary>Profile-gap card: opens Home on Profiles → Interview profile, where the missing personal answer can be added.</summary>
    private void AddToProfile_Click(object sender, RoutedEventArgs e)
    {
        OpenHome("profiles/personal");
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        CloseMore();
        if (_vm.LastReportPath is { } p && File.Exists(p))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
    }

    private HomeWindow? _home;

    /// <summary>
    /// Dashboard: onboarding, profiles, interviews (targets + preparation), reports, account. Non-modal.
    /// <paramref name="route"/> ("settings/audio", "preinterview", "account") is resolved before the window shows or is
    /// brought forward: a new window is built on that page; an open one switches first and is activated after.
    /// </summary>
    public async void OpenHome(string? route = null)
    {
        var target = route == null ? null : UiState.HomeRoute.Parse(route);
        if (_home is { IsLoaded: true } home)
        {
            if (target != null && !await home.NavigateAsync(target)) return;   // the user stayed with unsaved edits (asked in Home)
            if (home.WindowState == WindowState.Minimized) home.WindowState = WindowState.Normal;
            home.Activate();
            return;
        }
        _home = new HomeWindow(_vm, this, target) { Owner = this };
        _home.Closed += (_, _) => _home = null;
        _home.Show();
    }

    /// <summary>The open Home window, if any (self-test).</summary>
    internal HomeWindow? Home => _home;

    // ---------------- auto-fit: all bullets visible without scrolling ----------------

    private bool _fitScheduled, _fitting;
    private const double FitSafetyPx = 4;
    private void ScheduleFit()
    {
        if (_fitScheduled || !_vm.Settings.AutoFitAnswer) return;
        _fitScheduled = true;
        Dispatcher.BeginInvoke(FitAnswer, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Recomputed from the preferred layout every time (no drift): 1) preferred size and spacing, 2) tighter spacing,
    /// 3) smaller text — wrapped text height scales ≈ with font size², so one proportional step plus a short loop converges.
    /// Never below the readable minimum. All passes run inside one dispatcher operation, so nothing flickers.
    /// </summary>
    private void FitAnswer()
    {
        _fitScheduled = false;
        var viewport = AnswerScroll.ViewportHeight - FitSafetyPx;
        if (viewport <= 0 || !IsVisible) return;
        _fitting = true;
        try
        {
            bool Fits() { AnswerContent.UpdateLayout(); return AnswerContent.ActualHeight <= viewport; }
            _vm.CompactSpacing = false;
            _vm.SetFitFontSize(null);
            if (Fits()) return;
            _vm.CompactSpacing = true;
            if (Fits()) return;
            _vm.SetFitFontSize(_vm.AnswerFontSize * Math.Sqrt(viewport / AnswerContent.ActualHeight) * 0.98);
            for (int i = 0; i < 10 && !Fits() && _vm.AnswerFontSize > _vm.MinAnswerFontSize; i++)
                _vm.SetFitFontSize(_vm.AnswerFontSize - 0.5);
        }
        finally { _fitting = false; }
    }

    /// <summary>Bottom of the n-th bullet relative to the answer viewport (self-test: the critical third bullet must be visible).</summary>
    internal double BulletBottom(int index)
    {
        if (BulletList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement c) return double.NaN;
        return c.TransformToAncestor(AnswerScroll).Transform(new Point(0, c.ActualHeight)).Y;
    }
    private void Adaptive_Click(object sender, RoutedEventArgs e) { _vm.ResetAdaptive(); Toasts.Show(Core.Languages.Loc.T("toast_adaptive_reset"), Views.ToastKind.Info); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Settings live in Home (a page, not a separate window): brings Home forward on the Settings page, optionally
    /// scrolled to a section ("audio", "answers", "listening", "window", "hotkeys", "privacy").
    /// </summary>
    public void OpenSettings(string? section = null) => OpenHome(section == null ? "settings" : "settings/" + section);

    /// <summary>Raised after saved settings were applied (the pre-interview check re-runs its checks).</summary>
    public event Action? SettingsApplied;

    /// <summary>
    /// Applies saved settings. Settings never end an interview: during a session the live-changeable settings apply
    /// immediately and the rest (developer model choices) applies after Stop. Outside a session the engine is rebuilt.
    /// </summary>
    public void ApplySettings(bool engineChanged, bool modelChanged)
    {
        ApplyAppearance();
        RegisterHotkeys();
        _vm.RestartAudio();
        if (engineChanged)
        {
            if (_vm.SessionActive)
            {
                _vm.ApplyLiveSettings();
                if (modelChanged) _vm.RebuildAfterSession();   // applied on the next Start even if this session had no questions
                Toasts.Show(Core.Languages.Loc.T(modelChanged ? "toast_settings_after_stop" : "toast_settings_applied_live"), Views.ToastKind.Info);
            }
            else _vm.BuildEngine();
        }
        SettingsApplied?.Invoke();
    }

    private ReadinessWindow? _readiness;

    /// <summary>Non-modal: the main window (manual questions, close button) stays usable while checks run.</summary>
    public void OpenReadiness(bool firstRun)
    {
        if (_readiness is { IsLoaded: true }) { _readiness.Activate(); return; }
        _readiness = new ReadinessWindow(_vm, this) { Owner = this };
        _readiness.Closed += (_, _) =>
        {
            _readiness = null;
            if (firstRun) { _vm.Settings.FirstRunCompleted = true; _vm.Settings.Save(); }
        };
        _readiness.Show();
    }

    public void ShowTransientError(string message) => Toasts.Show(message, Views.ToastKind.Error);
    public void ShowToast(string message, Views.ToastKind kind = Views.ToastKind.Info) => Toasts.Show(message, kind);

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingHandled) return;
        e.Cancel = true; // finish async cleanup (stop audio, close WebSocket) then close for real
        if (_closeRequested) return;
        _closeRequested = true;
        // Unsaved edits in Home (profile, interview, settings) are asked about before anything closes.
        if (_home is { IsLoaded: true } home && !await home.ConfirmCloseAsync()) { _closeRequested = false; return; }
        _closingHandled = true;
        AppLog.Info("Close requested");
        foreach (var w in OwnedWindows.Cast<Window>().ToList()) w.Close();
        SavePlacement();
        _hotkeys?.Dispose();
        try
        {
            var cleanup = _vm.DisposeAsync().AsTask();
            if (await Task.WhenAny(cleanup, Task.Delay(5000)) != cleanup) AppLog.Warn("Cleanup exceeded 5 s; exiting anyway");
            else await cleanup;
        }
        catch (Exception ex) { AppLog.Error("Shutdown cleanup", ex); }
        _vm.Updates.ApplyOnExit();   // a downloaded update installs after this process exits — never mid-session
        AppLog.Info("Clean shutdown");
        Application.Current.Shutdown(App.RequestedExitCode);
    }
}
