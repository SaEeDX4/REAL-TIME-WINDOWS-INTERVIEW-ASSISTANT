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
        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsListening))
                StartIcon.Text = _vm.IsListening ? "" : "";
        };
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
        if (!_vm.Settings.FirstRunCompleted || !SecretStore.HasKey)
            Dispatcher.BeginInvoke(() => OpenReadiness(firstRun: true), DispatcherPriority.ApplicationIdle);
    }

    // ---------------- appearance & placement ----------------

    public void ApplyAppearance()
    {
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
        AppLog.Info("Clean shutdown");
        Application.Current.Shutdown(App.RequestedExitCode);
    }
}
