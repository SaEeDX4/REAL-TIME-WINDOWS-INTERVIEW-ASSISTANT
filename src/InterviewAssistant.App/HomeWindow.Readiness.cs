using System.Windows;
using System.Windows.Automation;
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
        // a disabled Start says why (the tool tip also shows on the disabled button)
        PfStartBtn.ToolTip = T(PagePreInterview.CanStart ? "tt_pf_start" : state switch
        {
            ReadinessState.NotReady => "tt_pf_start_blocked",
            ReadinessState.Checking => "rd_hint_checking",
            _ => "tt_pf_start_run_first",
        });
        RenderInterviewReadiness();
        RenderReadinessPill();
        // Get started reads the workspace (profiles, interviews): only rendered while it is the visible page.
        if (_page == "start") RenderDashboard();
    }

    /// <summary>
    /// Interviews page: compact readiness of the active interview (state badge, n of 8, Run check / Open check). When
    /// another interview is selected, the card says which interview the checks are for.
    /// </summary>
    private void RenderInterviewReadiness()
    {
        var state = _vm.ReadinessState;
        var brush = (Brush)FindResource(ReadinessSnapshot.BrushKey(state));
        IvBadge.BorderBrush = IvBadgeIcon.Foreground = IvBadgeText.Foreground = brush;
        IvBadgeIcon.Text = ReadinessPresentation.Icon(state);
        IvBadgeText.Text = T(ReadinessSnapshot.StateKey(state));
        IvSummary.Text = PreInterviewPage.SummaryText(_vm);
        var otherSelected = _target != null && _vm.ActiveTargetId != null && _target.Id != _vm.ActiveTargetId && _vm.ActiveLabel.Length > 0;
        IvApplies.Text = otherSelected ? F("rd_card_applies", _vm.ActiveLabel) : "";
        IvApplies.Visibility = otherSelected ? Visibility.Visible : Visibility.Collapsed;
        InterviewCheckBtn.Content = T(ReadinessPresentation.ShouldAutoRun(state) ? "rd_run_check" : "rd_open_check");
        AutomationProperties.SetName(InterviewReadinessCard, string.Join(". ", new[] { T("preflight"), IvBadgeText.Text, IvSummary.Text, IvApplies.Text }.Where(x => x.Length > 0)));
    }

    /// <summary>
    /// Title-bar indicator: state dot + label, opens the pre-interview page; the tool tip carries the summary line.
    /// </summary>
    private void RenderReadinessPill()
    {
        var state = _vm.ReadinessState;
        ReadinessDot.Fill = (Brush)FindResource(ReadinessSnapshot.BrushKey(state));
        ReadinessPillText.Text = T(ReadinessSnapshot.StateKey(state));
        var summary = state == ReadinessState.NotChecked ? T("rd_hint_not_checked") : PreInterviewPage.SummaryText(_vm);
        ReadinessPill.ToolTip = summary;
        AutomationProperties.SetName(ReadinessPill, F("rd_pill_name", ReadinessPillText.Text));
        AutomationProperties.SetHelpText(ReadinessPill, summary);
    }

    private void ReadinessPill_Click(object sender, RoutedEventArgs e) => Navigate("preinterview");

    private void PfRun_Click(object sender, RoutedEventArgs e) => _ = PagePreInterview.RunAllAsync();
    private void PfSettings_Click(object sender, RoutedEventArgs e) => Navigate("settings", "audio");
    private void PfStart_Click(object sender, RoutedEventArgs e) => PagePreInterview.RequestStart();

    /// <summary>
    /// Start interview (pre-interview page, Get started): the Live Assistant comes forward and starts listening right
    /// away — no second Start button. Home closes (it would otherwise stay above its owner); pending edits are asked about.
    /// </summary>
    private async Task StartInterviewAsync()
    {
        // unsaved edits first (rc.5 UX audit L15): the interview never starts behind an open "Save changes?" question
        if (!await ConfirmCloseAsync()) return;
        if (_vm.Settings.AuthorizedUseAcknowledged && !_vm.Settings.FirstRunCompleted) { _vm.Settings.FirstRunCompleted = true; _vm.Settings.Save(); }
        _main.Show();
        _main.Activate();
        Close();
        if (!_vm.IsListening) await _vm.ToggleListeningAsync(startToast: "toast_interview_started");
    }

    // ----- self-test hooks -----
    internal PreInterviewPage PreInterviewPageForTest => PagePreInterview;
}
