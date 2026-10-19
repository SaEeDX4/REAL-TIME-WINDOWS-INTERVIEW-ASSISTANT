using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.UiState;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.App.Views;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App;

/// <summary>
/// Live Assistant header (rc.5): status, Start/Pause, Stop, the readiness indicator, Quick help, the More overflow menu
/// and the window buttons. Which optional items show is decided by <see cref="LiveChrome"/> (setup vs interview mode,
/// compact view, width); this file only applies that rule to the controls.
/// </summary>
public partial class MainWindow
{
    private Views.Popover _more = null!;

    /// <summary>The layout currently applied (self-test).</summary>
    internal LiveChromeLayout Chrome { get; private set; } = LiveChrome.For(false, false, LiveChrome.MinWidth);

    private void InitChrome()
    {
        _more = new Views.Popover(this, MorePopup, MoreBtn);
        InitQuickHelp();
        // The snapshot is written on the checks' threads: marshal to the UI thread before touching controls.
        _vm.Readiness.Changed += _ => Dispatcher.BeginInvoke(RefreshReadiness);
        _vm.WorkspaceChanged += () => Dispatcher.BeginInvoke(RefreshReadiness);   // other account / interview → "Check again"
        SettingsApplied += RefreshReadiness;                                       // other playback device → "Check again"
        Activated += (_, _) => RefreshReadiness();
        SizeChanged += (_, _) => UpdateChrome();
        _vm.PropertyChanged += OnChromeProperty;
        WindowPlacement.HookMaximizeToWorkArea(this);
        StateChanged += (_, _) => OnWindowStateChanged();
        RefreshReadiness();
    }

    private void OnChromeProperty(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsListening) or nameof(MainViewModel.IsPaused) or nameof(MainViewModel.Question)
            or nameof(MainViewModel.AdaptiveLabel) or nameof(MainViewModel.MinutesLeftLabel) or nameof(MainViewModel.PresentationLabel))
            UpdateChrome();
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Applies <see cref="LiveChrome"/> for the current session state, view and width.</summary>
    /// <summary>A UI language switch flips the reading direction: the chevrons follow at once.</summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FlowDirectionProperty && IsLoaded) UpdateChrome();
    }

    private void UpdateChrome()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var c = LiveChrome.For(_vm.SessionActive, _vm.Settings.CompactMode, double.IsNaN(width) ? LiveChrome.MinWidth : width);
        Chrome = c;
        StartLabel.Visibility = Vis(c.StartLabel);
        StartBtn.Padding = c.StartLabel ? new Thickness(12, 4, 12, 4) : new Thickness(9, 4, 9, 4);
        AdaptiveBadge.Visibility = Vis(c.AdaptiveBadge && _vm.AdaptiveLabel.Length > 0);
        // icon-font chevrons are not mirrored by WPF: in right-to-left layouts "previous" points right
        var rtl = FlowDirection == FlowDirection.RightToLeft;
        PrevGlyph.Text = rtl ? "\uE76C" : "\uE76B";
        NextGlyph.Text = rtl ? "\uE76B" : "\uE76C";
        PresentationChip.Visibility = Vis(c.PresentationChip);
        MinutesText.Visibility = Vis(c.MinutesLabel && _vm.MinutesLeftLabel.Length > 0);
        // the overflow takes what the header has no room for
        MoreCoach.Visibility = Vis(c.PresentationInMore);
        MoreCoachCheck.Visibility = Vis(_vm.Presentation == Core.Live.Presentation.Coach);
        // the check glyph is not the only cue: screen readers hear "on" / "off"
        AutomationProperties.SetItemStatus(MoreCoach, Loc.T(_vm.Presentation == Core.Live.Presentation.Coach ? "a11y_on" : "a11y_off"));
        AutomationProperties.SetItemStatus(MoreCompact, Loc.T(_vm.Settings.CompactMode ? "a11y_on" : "a11y_off"));
        MoreMinutes.Visibility = Vis(c.MinutesInMore && _vm.MinutesLeftLabel.Length > 0);
        MoreCompactCheck.Visibility = Vis(_vm.Settings.CompactMode);
        UpdateFocusMenu();
        ApplyReadinessIndicator(c.Readiness);
        UpdateSetupMode(c);
    }

    // ---------------- readiness indicator ----------------

    /// <summary>Re-renders the indicator from the one readiness snapshot (state, colour, tooltip with the counts).</summary>
    private void RefreshReadiness()
    {
        var state = _vm.ReadinessState;
        var label = Loc.T(ReadinessSnapshot.StateKey(state));
        ReadinessDot.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, ReadinessSnapshot.BrushKey(state));
        ReadinessDot.Text = ReadinessPresentation.Icon(state);
        ReadinessLabel.Text = label;
        var tip = state is ReadinessState.NotChecked or ReadinessState.Outdated
            ? Loc.F("live_rd_tt_run", label)
            : Loc.F("live_rd_tt", label, Views.PreInterviewPage.SummaryText(_vm));
        ReadinessBtn.ToolTip = tip;
        AutomationProperties.SetName(ReadinessBtn, tip);
    }

    private void ApplyReadinessIndicator(ReadinessIndicator mode)
    {
        var label = mode == ReadinessIndicator.Label;
        ReadinessLabel.Visibility = Vis(label);
        // setup mode: a visible pill; during the interview: just the dot
        if (label) { ReadinessBtn.SetResourceReference(BackgroundProperty, "B.Surface2"); ReadinessBtn.SetResourceReference(BorderBrushProperty, "B.Border"); }
        else { ReadinessBtn.Background = Brushes.Transparent; ReadinessBtn.BorderBrush = Brushes.Transparent; }
        ReadinessBtn.Padding = label ? new Thickness(9, 4, 10, 4) : new Thickness(8, 5, 8, 5);
    }

    /// <summary>Opens Home directly on the pre-interview check.</summary>
    private void Readiness_Click(object sender, RoutedEventArgs e) => OpenHome("preinterview");

    // ---------------- authorized use ----------------

    /// <summary>
    /// The authorized-use acknowledgement before the first interview, whichever way it is started (Get started shows
    /// the same statement as a check box). Confirming it is remembered; Cancel starts nothing.
    /// </summary>
    private async Task<bool> ConfirmAuthorizedUseAsync()
    {
        if (_vm.Settings.AuthorizedUseAcknowledged) return true;
        if (!IsVisible) { Show(); }
        Activate();
        var result = await AppDialog.ShowAsync(this, new DialogRequest
        {
            Title = Loc.T("start_step1"),
            Message = Loc.T("authorized_use") + "\n\n" + Loc.T("start_step1_hint"),
            Tone = DialogTone.Info,
            Actions = new List<DialogAction>
            {
                new() { Text = Loc.T("dlg_cancel"), Result = AppDialogResult.Cancel, IsCancel = true },
                new() { Text = Loc.T("dlg_continue"), Result = AppDialogResult.Primary, Role = DialogButtonRole.Primary, IsDefault = true },
            },
        });
        if (result != AppDialogResult.Primary) return false;
        _vm.Settings.AuthorizedUseAcknowledged = true;
        _vm.Settings.Save();
        return true;
    }

    // ---------------- More (overflow) ----------------

    private void More_Click(object sender, RoutedEventArgs e) { UpdateChrome(); _more.Toggle(); }
    /// <summary>Closes the menu. In-place actions (Compact, Coach, Restore size) return focus to the More button; navigation does not.</summary>
    private void CloseMore(bool restoreFocus = false) => _more.Close(restoreFocus);
    internal bool MoreOpen => _more.IsOpen;

    private void MoreCompact_Click(object sender, RoutedEventArgs e)
    {
        CloseMore(restoreFocus: true);
        _vm.Settings.CompactMode = !_vm.Settings.CompactMode;
        ApplyCompact(_vm.Settings.CompactMode);
        _vm.Settings.Save();
    }

    // ---------------- maximize / restore ----------------

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>Maximized = the monitor's work area (taskbar stays visible); Normal returns to the placement before.</summary>
    internal void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnWindowStateChanged()
    {
        var max = WindowState == WindowState.Maximized;
        // no transparent resize margin, rounded corners or shadow gap when the window fills the work area
        Root.Margin = ShadowLayer.Margin = new Thickness(max ? 0 : 8);
        Root.CornerRadius = ShadowLayer.CornerRadius = new CornerRadius(max ? 0 : 14);
        Dialogs.CornerRadius = new CornerRadius(max ? 0 : 13);
        MaxIcon.Text = max ? "" : "";
        UpdateChrome();
    }

    // ---------------- setup mode vs interview mode ----------------

    /// <summary>
    /// Setup mode (no session): a calm empty state and a link to Home while no question is on screen. Interview mode:
    /// only the interview controls; an empty question card says it is waiting for the interviewer.
    /// </summary>
    private void UpdateSetupMode(LiveChromeLayout c)
    {
        var noQuestion = _vm.Question.Length == 0;
        SetupEmpty.Visibility = Vis(c.SetupEmptyState && noQuestion);
        SetupHomeLink.Visibility = Vis(c.HomeLink);
        WaitingText.Visibility = Vis(!c.SetupEmptyState && noQuestion);
    }
}
