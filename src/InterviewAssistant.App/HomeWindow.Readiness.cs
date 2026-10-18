using System.Windows;
using System.Windows.Media;
using InterviewAssistant.App.UiState;
using InterviewAssistant.App.Views;

namespace InterviewAssistant.App;

/// <summary>
/// Home's readiness surfaces: the pre-interview page (hosted like Settings) and its sticky action bar. Every surface
/// renders the one snapshot in vm.Readiness — written only by the page — so none of them can disagree.
/// </summary>
public partial class HomeWindow
{
    private void InitializeReadiness()
    {
        PagePreInterview.Initialize(_vm, _main);
        PagePreInterview.NavigateRequested += route => { var r = HomeRoute.Parse(route); Navigate(r.Page, r.Section); };
        PagePreInterview.StartRequested += () => _ = StartInterviewAsync();
        // The page re-renders on every store change (and on Refresh); every other surface follows it.
        PagePreInterview.StateChanged += RefreshReadinessSurfaces;
        RefreshReadinessSurfaces();
    }

    private void DetachReadiness() => PagePreInterview.Detach();

    private void RelocalizeReadiness() => PagePreInterview.Relocalize();

    /// <summary>Footer (and the other readiness surfaces) from the current snapshot.</summary>
    private void RefreshReadinessSurfaces()
    {
        var state = _vm.ReadinessState;
        PfFooterDot.Fill = (Brush)FindResource(ReadinessSnapshot.BrushKey(state));
        PfFooterText.Text = $"{T(ReadinessSnapshot.StateKey(state))} · {PreInterviewPage.SummaryText(_vm)}";
        PfRunBtn.IsEnabled = !PagePreInterview.IsRunning;
        PfStartBtn.IsEnabled = PagePreInterview.CanStart;
        // Get started reads the workspace (profiles, interviews): only rendered while it is the visible page.
        if (_page == "start") RenderDashboard();
    }

    private void PfRun_Click(object sender, RoutedEventArgs e) => _ = PagePreInterview.RunAllAsync();
    private void PfSettings_Click(object sender, RoutedEventArgs e) => Navigate("settings", "audio");
    private void PfStart_Click(object sender, RoutedEventArgs e) => PagePreInterview.RequestStart();

    /// <summary>
    /// Start interview (pre-interview page, Get started): the Live Assistant comes forward and starts listening right
    /// away — no second Start button. Home closes (it would otherwise stay above its owner); pending edits are asked about.
    /// </summary>
    private async Task StartInterviewAsync()
    {
        if (_vm.Settings.AuthorizedUseAcknowledged && !_vm.Settings.FirstRunCompleted) { _vm.Settings.FirstRunCompleted = true; _vm.Settings.Save(); }
        _main.Show();
        _main.Activate();
        Close();
        if (!_vm.IsListening) await _vm.ToggleListeningAsync(startToast: "toast_interview_started");
    }

    // ----- self-test hooks -----
    internal PreInterviewPage PreInterviewPageForTest => PagePreInterview;
}
