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
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _closingHandled;

    public MainWindow()
    {
        InitializeComponent();
        Title = Branding.ProductName;
        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsListening))
                StartIcon.Text = _vm.IsListening ? "" : "";
        };
        _vm.BulletsChanged += ScheduleFit;
        AnswerScroll.SizeChanged += (_, _) => ScheduleFit();
        AnswerContent.SizeChanged += (_, _) => { if (!_fitting) ScheduleFit(); };   // note/pending/thinking rows too
        RestorePlacement();
        ApplyAppearance();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var error = _vm.Initialize();
        if (error != null) { ShowTransientError(error); return; }
        RegisterHotkeys();
        _vm.StartAudioPreview(); // meter works before the interview starts
        AppLog.Info("Main window loaded");
        if (App.SelfTestOutput is { } outPath)
        {
            Dispatcher.BeginInvoke(async () =>
            {
                App.RequestedExitCode = await SelfTest.RunAsync(this, _vm, outPath);
                Close();
            }, DispatcherPriority.ApplicationIdle);
            return;
        }
        // First run: onboarding (authorized-use acknowledgement, account/developer mode, profile, interview) in Home.
        if (!_vm.Settings.AuthorizedUseAcknowledged || !_vm.Settings.FirstRunCompleted)
            Dispatcher.BeginInvoke(() => OpenHome(), DispatcherPriority.ApplicationIdle);
    }

    // ---------------- appearance & placement ----------------

    public void ApplyAppearance()
    {
        _vm.RefreshLanguage();
        Topmost = _vm.Settings.AlwaysOnTop;
        Root.Opacity = ShadowLayer.Opacity = Math.Clamp(_vm.Settings.WindowOpacity, 0.6, 1.0);
        DiagPanel.Visibility = _vm.Settings.ShowDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        ApplyCompact(_vm.Settings.CompactMode);
        _vm.RefreshFonts();
    }

    public void SetCompact(bool compact) { _vm.Settings.CompactMode = compact; ApplyCompact(compact); }

    private void ApplyCompact(bool compact)
    {
        // Compact = question + bullets + status only. Controls stay reachable via hotkeys and by toggling back.
        var v = compact ? Visibility.Collapsed : Visibility.Visible;
        ActionBar.Visibility = v;
        ManualBar.Visibility = v;
        QuestionLabel.Visibility = v;
        AnswerHeader.Visibility = v;
        CompactIcon.Text = compact ? "" : "";
    }

    private void RestorePlacement()
    {
        var s = _vm.Settings;
        Width = Math.Max(MinWidth, s.Width);
        Height = Math.Max(MinHeight, s.Height);
        if (s.Left is double l && s.Top is double t)
        {
            // Only restore if the saved position is still on a connected monitor (multi-monitor safe).
            var vsLeft = SystemParameters.VirtualScreenLeft; var vsTop = SystemParameters.VirtualScreenTop;
            if (l >= vsLeft - 50 && t >= vsTop - 20 && l < vsLeft + SystemParameters.VirtualScreenWidth - 80 && t < vsTop + SystemParameters.VirtualScreenHeight - 60)
            { Left = l; Top = t; WindowStartupLocation = WindowStartupLocation.Manual; return; }
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Top + 80;
    }

    private void SavePlacement()
    {
        var s = _vm.Settings;
        if (WindowState == WindowState.Normal) { s.Left = Left; s.Top = Top; s.Width = Width; s.Height = Height; }
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

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.CompactMode = !_vm.Settings.CompactMode;
        ApplyCompact(_vm.Settings.CompactMode);
        _vm.Settings.Save();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.ShowDiagnostics = !_vm.Settings.ShowDiagnostics;
        DiagPanel.Visibility = _vm.Settings.ShowDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        _vm.Settings.Save();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void Home_Click(object sender, RoutedEventArgs e) => OpenHome();
    private void Presentation_Click(object sender, MouseButtonEventArgs e) => _vm.ToggleCoach();

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.LastReportPath is { } p && File.Exists(p))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
    }

    private HomeWindow? _home;

    /// <summary>Dashboard: onboarding, profiles, interviews (targets + preparation), reports, account. Non-modal.</summary>
    public void OpenHome()
    {
        if (_home is { IsLoaded: true }) { _home.Activate(); return; }
        _home = new HomeWindow(_vm, this) { Owner = this };
        _home.Closed += (_, _) => _home = null;
        _home.Show();
    }

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
    private void Readiness_Click(object sender, RoutedEventArgs e) => OpenReadiness(firstRun: false);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public void OpenSettings()
    {
        var wasListening = _vm.IsListening;
        var dlg = new SettingsWindow(_vm) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            ApplyAppearance();
            RegisterHotkeys();
            _vm.RestartAudio();
            if (dlg.EngineSettingsChanged)
            {
                _ = RebuildAsync(wasListening);
            }
        }
    }

    private async Task RebuildAsync(bool resume)
    {
        await _vm.StopAsync();
        _vm.BuildEngine();
        if (resume) await _vm.ToggleListeningAsync();
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

    public void ShowTransientError(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop(); _toastTimer.Start();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingHandled) return;
        e.Cancel = true; // finish async cleanup (stop audio, close WebSocket) then close for real
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
