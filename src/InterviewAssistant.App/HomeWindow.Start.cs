using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.UiState;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App;

/// <summary>
/// Get started as the interview readiness dashboard: four steps (Account, Profile, Interview setup, Pre-interview
/// check) shown Done / Current / Upcoming with real data, and exactly one primary action — the next step, from
/// <see cref="SetupPlanner"/>. The authorized-use acknowledgement is a first-run card; afterwards only a note remains.
/// </summary>
public partial class HomeWindow
{
    private SetupAction _primaryAction = SetupAction.SignIn;

    /// <summary>The interview the dashboard talks about: the active one, else a prepared one, else the first.</summary>
    private InterviewTarget? FocusInterview(IReadOnlyList<CandidateProfileRecord> profiles)
    {
        if (Store is not { } store) return null;
        var targets = profiles.SelectMany(p => store.Targets(p.Id)).ToList();
        return targets.FirstOrDefault(t => t.Id == _vm.ActiveTargetId)
               ?? targets.FirstOrDefault(t => t.Preparation == PreparationState.Prepared)
               ?? targets.FirstOrDefault();
    }

    private bool DeveloperKeyMode => BuildFlavor.Developer && !_vm.UsingCloud;

    /// <summary>What the dashboard knows (signed out: nothing local counts — it is not shown either).</summary>
    private SetupFacts CurrentSetupFacts(IReadOnlyList<CandidateProfileRecord> profiles, InterviewTarget? focus) => new(
        AccountReady: _vm.HasWorkspace && (_vm.Cloud.IsSignedIn || (DeveloperKeyMode && SecretStore.HasKey)),
        HasProfile: profiles.Count > 0,
        HasConfirmedFacts: profiles.Any(p => p.ConfirmedFacts.Any()),
        HasInterview: focus != null,
        InterviewPrepared: focus?.Preparation == PreparationState.Prepared,
        Readiness: _vm.ReadinessState);

    /// <summary>
    /// Recomputes the dashboard. Also called whenever the account, profiles or interviews change; it refreshes the
    /// readiness surfaces too (the active interview or the account may have changed what the check result is for).
    /// </summary>
    private void RefreshChecklist() => PagePreInterview.Refresh();   // → StateChanged → RefreshReadinessSurfaces → RenderDashboard

    private void RenderDashboard()
    {
        var profiles = Store?.Profiles() ?? Array.Empty<CandidateProfileRecord>();
        var focus = FocusInterview(profiles);
        var facts = CurrentSetupFacts(profiles, focus);
        var steps = SetupPlanner.Steps(facts);
        _primaryAction = SetupPlanner.PrimaryAction(facts);
        var ack = _vm.Settings.AuthorizedUseAcknowledged;

        // first-run acknowledgement card, then only a note
        AckCard.Visibility = ack ? Visibility.Collapsed : Visibility.Visible;
        AckNote.Visibility = ack ? Visibility.Visible : Visibility.Collapsed;

        var details = new[] { AccountStepDetail(), ProfileStepDetail(profiles), InterviewStepDetail(focus), CheckStepDetail() };
        var cards = new[] { StepCard0, StepCard1, StepCard2, StepCard3 };
        var marks = new[] { StepMark0, StepMark1, StepMark2, StepMark3 };
        var nums = new[] { StepNum0, StepNum1, StepNum2, StepNum3 };
        var icons = new[] { StepIcon0, StepIcon1, StepIcon2, StepIcon3 };
        var states = new[] { StepState0, StepState1, StepState2, StepState3 };
        var titles = new[] { StepTitle0, StepTitle1, StepTitle2, StepTitle3 };
        var detailBlocks = new[] { StepDetail0, StepDetail1, StepDetail2, StepDetail3 };
        for (int i = 0; i < steps.Count && i < cards.Length; i++)
        {
            var status = steps[i].Status;
            var done = status == StepStatus.Done;
            var current = status == StepStatus.Current;
            marks[i].Background = current ? Res("B.AccentStrong") : done ? Res("B.Surface2") : Brushes.Transparent;
            marks[i].BorderBrush = current ? Res("B.AccentStrong") : done ? Res("B.Listening") : Res("B.Border");
            nums[i].Visibility = done ? Visibility.Collapsed : Visibility.Visible;
            nums[i].Foreground = current ? Brushes.White : Res("B.TextFaint");
            icons[i].Visibility = done ? Visibility.Visible : Visibility.Collapsed;
            states[i].Text = T(done ? "gs_status_done" : current ? "gs_status_current" : "gs_status_upcoming");
            states[i].Foreground = done ? Res("B.Listening") : current ? Res("B.Accent") : Res("B.TextFaint");
            cards[i].BorderBrush = current ? Res("B.Accent") : Res("B.Border");
            detailBlocks[i].Text = details[i];
            detailBlocks[i].Foreground = Res(status == StepStatus.Upcoming ? "B.TextFaint" : "B.TextDim");
            AutomationProperties.SetName(cards[i], $"{(i + 1).ToString(Loc.Culture)}. {F("fmt_label_value", titles[i].Text, states[i].Text)}. {details[i]}");
        }

        // secondary actions: links only
        var configured = _vm.Cloud.IsConfigured;
        StepAccountLink.Visibility = configured && _vm.Cloud.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        DeveloperModeBtn.Visibility = BuildFlavor.Developer ? Visibility.Visible : Visibility.Collapsed;
        StartImportBtn.Visibility = StartAddInterviewBtn.Visibility = _vm.HasWorkspace ? Visibility.Visible : Visibility.Collapsed;
        StartInterviewProfileBtn.Visibility = _vm.HasWorkspace && profiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // the one primary action
        PrimaryActionBtn.Content = T(SetupPlanner.ActionKey(_primaryAction));
        PrimaryActionBtn.IsEnabled = ack;
        PrimaryActionBtn.ToolTip = ack ? null : T("start_step1_hint");
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private string AccountStepDetail()
    {
        if (DeveloperKeyMode) return T(SecretStore.HasKey ? "start_dev_key" : "start_dev_no_key");
        if (!_vm.Cloud.IsConfigured) return T("acct_not_configured_long");
        if (_vm.Cloud.IsSignedIn) return F("gs_account_signed_in", _vm.AccountLabel);
        return Loc.Relocalize(_vm.Cloud.Message) ?? T("start_account_hint");
    }

    private string ProfileStepDetail(IReadOnlyList<CandidateProfileRecord> profiles)
    {
        if (!_vm.HasWorkspace) return T("start_sign_in_first");
        if (profiles.Count == 0) return T("start_profile_hint");
        var confirmed = profiles.Sum(p => p.ConfirmedFacts.Count());
        return confirmed > 0 ? F("gs_profile_status", profiles.Count, confirmed) : F("gs_profile_review", profiles.Count, profiles.Sum(p => p.Stats.ToReview));
    }

    private string InterviewStepDetail(InterviewTarget? focus)
    {
        if (!_vm.HasWorkspace) return T("start_sign_in_first");
        return focus == null ? T("start_interview_hint") : $"{focus.DisplayName} · {StateLabel(focus.Preparation)}";
    }

    private string CheckStepDetail()
    {
        var state = _vm.ReadinessState;
        return state == ReadinessState.NotChecked ? T("rd_hint_not_checked") : $"{T(ReadinessSnapshot.StateKey(state))} · {Views.PreInterviewPage.SummaryText(_vm)}";
    }

    // ================= actions =================

    /// <summary>The dashboard's one primary action: whatever the next step is.</summary>
    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.Settings.AuthorizedUseAcknowledged) return;
        switch (_primaryAction)
        {
            case SetupAction.SignIn:
                GoAccount();
                break;
            case SetupAction.CompleteProfile:
                // No profile: the Profiles page leads with Import resume. Otherwise its facts tab, where facts are confirmed.
                await NavigateAsync(new HomeRoute("profiles", "facts"));
                break;
            case SetupAction.CreateInterview:
                if (await NavigateAsync(new HomeRoute("interviews"))) NewTarget_Click(sender, e);
                break;
            case SetupAction.PrepareInterview:
                await PrepareFocusInterviewAsync(sender, e);
                break;
            case SetupAction.RunPreInterviewCheck:
                UseFocusInterview();
                CompleteFirstRun();
                await NavigateAsync(new HomeRoute("preinterview"));
                break;
            case SetupAction.StartInterview:
                await StartInterviewAsync();
                break;
        }
    }

    /// <summary>Opens the interview the dashboard means on the Interviews page and starts its preparation there.</summary>
    private async Task PrepareFocusInterviewAsync(object sender, RoutedEventArgs e)
    {
        var focus = FocusInterview(Store?.Profiles() ?? Array.Empty<CandidateProfileRecord>());
        if (!await NavigateAsync(new HomeRoute("interviews")) || focus == null) return;
        if (_target?.Id != focus.Id)
        {
            // Switching the shown interview (and maybe the profile) drops pending edits: asked first.
            if (!await _nav.CanProceedAsync(ChangeReason.SwitchItem, _profileGuard, _targetGuard)) return;
            if (_profile?.Id != focus.ProfileId) RefreshProfiles(focus.ProfileId);
            RefreshTargets(focus.Id, reload: true);
        }
        if (_target?.Id != focus.Id) return;
        PrepareBtn.Focus();
        Prepare_Click(PrepareBtn, e);
    }

    /// <summary>The check and the interview are for the interview the dashboard shows: it becomes the active one.</summary>
    private void UseFocusInterview()
    {
        var focus = FocusInterview(Store?.Profiles() ?? Array.Empty<CandidateProfileRecord>());
        if (focus == null || focus.Id == _vm.ActiveTargetId || _vm.SessionActive || _vm.Workspace == null || !_vm.WorkspaceMatchesAccount) return;
        _vm.SetActive(focus.ProfileId, focus.Id);
        var (kb, label) = _vm.Workspace.LoadActive(focus.ProfileId, focus.Id);
        _vm.UseKnowledge(kb, label);
        RefreshTargets(_target?.Id);
    }

    /// <summary>Onboarding is complete once the user reaches the check (or starts) after the acknowledgement.</summary>
    private void CompleteFirstRun()
    {
        if (!_vm.Settings.AuthorizedUseAcknowledged || _vm.Settings.FirstRunCompleted) return;
        _vm.Settings.FirstRunCompleted = true;
        _vm.Settings.Save();
    }

    private void Ack_Changed(object sender, RoutedEventArgs e)
    {
        _vm.Settings.AuthorizedUseAcknowledged = AckBox.IsChecked == true;
        _vm.Settings.Save();
        RefreshChecklist();
        if (_vm.Settings.AuthorizedUseAcknowledged) PrimaryActionBtn.Focus();   // keyboard: straight on to the next step
    }

    private void DeveloperMode_Click(object sender, RoutedEventArgs e)
    {
        if (!BuildFlavor.Developer || _vm.SessionActive) return;
        _vm.Settings.Mode = "developer";
        _vm.Settings.Save();
        _vm.OpenWorkspaceForCurrentAccount();
        _vm.BuildEngine();
        RefreshAll();
        Navigate("settings");
    }

    /// <summary>Sign in: the service account (a developer build switches back from its own-key mode first).</summary>
    private void StepAccountLink_Click(object sender, RoutedEventArgs e) => GoAccount();

    private void GoAccount()
    {
        if (_vm.Settings.Mode != "cloud" && !_vm.SessionActive) { _vm.Settings.Mode = "cloud"; _vm.Settings.Save(); _vm.OpenWorkspaceForCurrentAccount(); _vm.BuildEngine(); }
        Navigate("account");
    }

    private void Readiness_Click(object sender, RoutedEventArgs e) => Navigate("preinterview");

    // ----- self-test hooks -----
    /// <summary>Visible buttons of Get started styled as a primary action (must be exactly one).</summary>
    internal IReadOnlyList<Button> StartPrimaryButtonsForTest
    {
        get
        {
            var primary = new[] { (Style)FindResource("Btn.Primary"), (Style)FindResource("Btn.FormPrimary") };
            var found = new List<Button>();
            void Walk(DependencyObject d)
            {
                if (d is Button b && b.IsVisible && primary.Contains(b.Style)) found.Add(b);
                foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(c);
            }
            Walk(PageStart);
            return found;
        }
    }
    internal SetupAction PrimaryActionForTest => _primaryAction;
}
