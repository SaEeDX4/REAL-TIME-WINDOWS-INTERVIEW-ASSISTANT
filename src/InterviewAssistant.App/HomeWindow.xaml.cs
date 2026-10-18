using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InterviewAssistant.App.UiState;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.Views;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.Client;
using InterviewAssistant.Contracts;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Persistence;
using InterviewAssistant.Core.Preparation;
using InterviewAssistant.Core.Reports;
using Microsoft.Win32;

namespace InterviewAssistant.App;

/// <summary>
/// Home: onboarding, the Candidate Knowledge Profile (facts with provenance + interview profile), interviews
/// (targets + preparation), reports, account (plan, quota, devices, security, privacy), settings (updates included) and help/about.
/// Everything shown belongs to the signed-in account's workspace; signed out, no user data is loaded or shown.
/// All text comes from the localization tables and refreshes live when the UI language changes — a language switch
/// only re-renders texts: drafts, selections, unsaved-changes state and scroll position stay exactly as they were.
/// Unsaved edits (profile, interview, settings) are protected by one <see cref="NavigationGuard"/> that asks only before
/// real data-loss actions (leaving the page, switching item, closing, signing out), through the in-app dialog.
/// </summary>
public partial class HomeWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    /// <summary>The signed-in account's store — null while a workspace switch is pending, so nothing of another account is shown or changed.</summary>
    private WorkspaceStore? Store => _vm.WorkspaceMatchesAccount ? _vm.Workspace!.Store : null;
    private CandidateProfileRecord? _profile;
    private InterviewTarget? _target;
    private readonly ObservableCollection<FactRow> _facts = new();
    private readonly Dictionary<string, TextBox> _personalBoxes = new();
    private readonly List<(TextBlock Block, Func<string> Text)> _personalTexts = new();
    private CancellationTokenSource? _prepCts;
    private string _page = "start";

    private readonly NavigationGuard _nav;
    private readonly DirtyStateGuard _profileGuard = new("profile");
    private readonly DirtyStateGuard _targetGuard = new("interview");
    private readonly AccountSections _sections = new();
    private int _selectionSuppressed;
    private bool _closeConfirmed;

    // Display records: ToString is the label so no picker can ever show "LangItem { Label = … }".
    private sealed record Item<T>(string Label, T Value) { public override string ToString() => Label; }
    private sealed record LangItem(string Label, string Code) { public override string ToString() => Label; }

    private static string T(string key) => Loc.T(key);
    private static string F(string key, params object?[] args) => Loc.F(key, args);
    /// <summary>Local storage failures (disk full, file locked, folder not writable, protected data unreadable): shown with contextual copy.</summary>
    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException;

    /// <summary>One row of the Reports list: when, which interview, how many questions (screen readers read all three).</summary>
    public sealed class ReportRow
    {
        public required StoredSession Session { get; init; }
        public InterviewReport? Report { get; init; }
        public required string When { get; init; }
        public required string Title { get; init; }
        public required string Questions { get; init; }
        public override string ToString() => string.Join(", ", new[] { When, Title, Questions }.Where(x => x.Length > 0));
    }

    /// <summary>One fact in the review list: confirmation is a pending edit until Save profile.</summary>
    public sealed class FactRow : INotifyPropertyChanged
    {
        public required ProfileFact Fact { get; init; }
        public required FactStatus Initial { get; init; }
        private bool _confirmed;
        public bool Confirmed { get => _confirmed; set { if (_confirmed == value) return; _confirmed = value; PropertyChanged?.Invoke(this, new(nameof(Confirmed))); } }
        public string Text => Fact.Kind == FactKind.Role ? $"{Fact.Title} — {Fact.Company} ({Fact.Period})".Replace(" ()", "").Trim(' ', '—') : Fact.Text;
        public string Meta
        {
            get
            {
                var kind = Loc.T("fact_kind_" + Fact.Kind.ToString().ToLowerInvariant());
                if (Fact.Kind is FactKind.Personal or FactKind.Boundary && PersonalCategories.Find(Fact.Category) is { } c) kind = Loc.T($"pc_{c.Key}_label");
                if (Fact.Status == FactStatus.AiInference) kind += " · " + Loc.T("fact_ai_inference");
                var source = Fact.Source switch
                {
                    FactSource.ResumeImport => Loc.F("fact_source_resume", Fact.SourceName.Length > 0 ? Fact.SourceName : Loc.T("fact_source_document")),
                    FactSource.Manual => Loc.T("fact_source_manual"),
                    FactSource.Questionnaire => Loc.T("fact_source_questionnaire"),
                    FactSource.Document => Loc.F("fact_source_resume", Fact.SourceName),
                    _ => Fact.Provenance != null ? Loc.T("fact_source_document") : Loc.T("fact_source_unknown"),
                };
                var line = Fact.Provenance is { } p && p.Snippet.Length > 0 && Fact.Source != FactSource.Manual ? " · " + Loc.F("fact_line", p.LineStart) : "";
                var when = (Fact.UpdatedUtc ?? Fact.CreatedUtc) is DateTime d ? " · " + d.ToLocalTime().ToString("d", Loc.Culture) : "";
                return $"{kind} · {source}{line}{when}";
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void RaiseAll() => PropertyChanged?.Invoke(this, new(""));
    }

    /// <param name="initial">Deep link to open on (Settings from the Live gear, the pre-interview check, Account): the
    /// window is built on that page, so no other page is ever shown first.</param>
    public HomeWindow(MainViewModel vm, MainWindow main, HomeRoute? initial = null)
    {
        // Building the window and filling its pickers raises change events: none of that is a user edit.
        using (_profileGuard.Suspend())
        using (_targetGuard.Suspend())
        {
            InitializeComponent();
            _vm = vm; _main = main;
            Title = Branding.ProductName;
            TitleText.Text = Branding.ProductName;
            FactList.ItemsSource = _facts;
            UiLanguageBox.ItemsSource = LanguageRegistry.All;
            UiLanguageBox.SelectedItem = LanguageRegistry.Find(_vm.UiLanguage);
            BuildLanguagePickers();
            AckBox.IsChecked = _vm.Settings.AuthorizedUseAcknowledged;
            BuildPersonalFields();
        }

        // One guard for every editor in Home: asks (in-app dialog) only before real data-loss actions.
        _nav = new NavigationGuard(AskUnsavedAsync);
        _nav.Register(_profileGuard);
        _nav.Register(_targetGuard);
        _nav.Register(PageSettings.Guard);
        _profileGuard.Save = SaveProfile;
        _profileGuard.Discard = () => { if (_profile != null) LoadProfile(Store?.GetProfile(_profile.Id)); };
        // A "Save" answer that fails validation shows the page with the problem (the user stays to fix it).
        _targetGuard.Save = () => { if (SaveTargetFields() != null) return true; ShowPage("interviews"); TargetTitle.Focus(); return false; };
        _targetGuard.Discard = () => LoadTarget(_target != null ? Store?.GetTarget(_target.Id) : null);
        foreach (var g in _nav.Guards) g.DirtyChanged += UpdateDirtyIndicators;
        PageSettings.Initialize(vm, main);
        PageSettings.Guard.Save = () => { if (PageSettings.Save()) return true; ShowPage("settings"); PageSettings.ShowSection("hotkeys"); return false; };
        PageSettings.Saved += () => Notify(T("toast_settings_saved"), ToastKind.Success);
        PageSettings.DefaultsRestored += category => Notify(category == null ? T("toast_defaults_restored")
            : F("toast_section_reset", T(SettingsCategories.All[SettingsCategories.IndexOf(category)].TitleKey)), ToastKind.Success);
        InitializeReadiness();
        foreach (var box in new Control[] { EmailBox, PasswordBox, ConfirmBox, TargetTitle }) Field.ClearOnEdit(box);

        _vm.Updates.Changed += OnUpdatesChanged;
        _vm.Cloud.Changed += OnCloudChanged;
        _vm.WorkspaceChanged += OnWorkspaceChanged;
        _vm.ReportSaved += OnReportSaved;
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _vm.Cloud.Changed -= OnCloudChanged; _vm.Updates.Changed -= OnUpdatesChanged; _vm.WorkspaceChanged -= OnWorkspaceChanged;
            _vm.ReportSaved -= OnReportSaved; Loc.LanguageChanged -= OnLanguageChanged; _prepCts?.Cancel();
            PageSettings.Guard.Discard?.Invoke();   // an unsaved audio-device preview never outlives the page
            DetachReadiness();   // probes and listeners of the pre-interview check stop with the window
        };
        WindowPlacement.HookMaximizeToWorkArea(this);
        WindowPlacement.Restore(this, _vm.Settings.HomeLeft, _vm.Settings.HomeTop, _vm.Settings.HomeWidth, _vm.Settings.HomeHeight, _vm.Settings.HomeMaximized);
        StateChanged += (_, _) => OnWindowStateChanged();
        Closing += OnClosing;
        _factView = System.Windows.Data.CollectionViewSource.GetDefaultView(_facts);
        _factView.Filter = o => o is FactRow r && (_factFilter == "all" || (_factFilter == "review") != r.Confirmed);
        _factView.SortDescriptions.Add(new SortDescription(nameof(FactRow.Confirmed), ListSortDirection.Ascending));
        SetProfileTab("facts");
        if (_vm.Cloud.IsSignedIn && _vm.Cloud.State == CloudState.SignedIn && _vm.Cloud.Account != null) _sections.Summary.Loaded(DateTimeOffset.Now);
        RefreshAll();
        UpdateDirtyIndicators();
        var route = initial ?? new HomeRoute(!_vm.HasWorkspace && _vm.Cloud.IsConfigured ? "account" : _vm.Settings.AuthorizedUseAcknowledged && _vm.Settings.FirstRunCompleted ? "interviews" : "start");
        ShowRoute(route);
    }

    private void OnUpdatesChanged() => Dispatcher.BeginInvoke(RefreshUpdates);
    private void OnCloudChanged() => Dispatcher.BeginInvoke(() =>
    {
        // Any successful refresh (sign-in, upgrade polling, live session) clears a stale summary error by itself.
        var c = _vm.Cloud;
        if (!c.IsSignedIn) _sections.ResetAll();
        else if (!_sections.Summary.IsLoading)
        {
            if (c.State == CloudState.SignedIn && c.Account != null) _sections.Summary.Loaded(DateTimeOffset.Now);
            else if (c.State == CloudState.ServiceUnavailable && c.LastRefreshError is { } ex) _sections.Summary.Failed(CloudService.Describe(ex, includeReference: false));
        }
        RefreshAccount(); RefreshChecklist();
    });
    private void OnWorkspaceChanged() => Dispatcher.BeginInvoke(() =>
    {
        // Another account's workspace: nothing of the previous one stays on screen. An explicit sign-out was confirmed
        // through the guard; when the account changed by itself (session expired or revoked) the edits cannot be saved to
        // it any more — the user is told instead of losing them silently.
        if (_profileGuard.IsDirty || _targetGuard.IsDirty) Notify(T("toast_edits_discarded_account"), ToastKind.Warning);
        _profileGuard.MarkClean(); _targetGuard.MarkClean();
        _profile = null; _target = null;
        RefreshAll(); ShowPage(_page);
    });
    private void OnReportSaved(string id) => Dispatcher.BeginInvoke(() => RefreshReports(id));
    private void OnLanguageChanged() => Dispatcher.BeginInvoke(Relocalize);

    /// <summary>
    /// The UI language changed: re-render every text composed in code (XAML texts refresh by binding). Never navigation:
    /// no record is reloaded, no list selection changes, pending edits and the unsaved-changes state stay, nothing is saved.
    /// </summary>
    private void Relocalize()
    {
        var scroll = ContentScroll.VerticalOffset;
        using (SuppressSelection())
        using (_profileGuard.Suspend())
        using (_targetGuard.Suspend())
        {
            BuildLanguagePickers();
            foreach (var (block, text) in _personalTexts) block.Text = text();
            foreach (var (key, box) in _personalBoxes) System.Windows.Automation.AutomationProperties.SetName(box, T($"pc_{key}_label"));
            foreach (var r in _facts) r.RaiseAll();
            UpdateFactSummary();
            RefreshProfiles();
            RefreshTargets();
            RefreshReports(SelectedReportId());
            RefreshChecklist();
            RefreshAccount();
            RefreshAbout();
            RefreshUpdates();
            SetCreateMode(_createMode);
            foreach (var m in new[] { AuthMessage, SecurityMessage, PrivacyMessage, TargetMessage }) { m.Text = Loc.Relocalize(m.Text) ?? m.Text; m.Detail = Loc.Relocalize(m.Detail) ?? m.Detail; }
            PageSettings.Relocalize();
            RelocalizeReadiness();
            UpdateDirtyIndicators();
        }
        ContentScroll.ScrollToVerticalOffset(scroll);
    }

    /// <summary>Programmatic list updates inside this scope are not user selections.</summary>
    private IDisposable SuppressSelection()
    {
        _selectionSuppressed++;
        return new Scope(() => _selectionSuppressed--);
    }

    private sealed class Scope(Action end) : IDisposable { public void Dispose() => end(); }

    // ================= unsaved changes =================

    private Task<LeaveDecision> AskUnsavedAsync(DirtyStateGuard guard, ChangeReason reason)
    {
        Activate();
        return AppDialog.AskUnsavedAsync(this, T(guard.Name switch
        {
            "profile" => "dlg_unsaved_profile",
            "interview" => "dlg_unsaved_interview",
            _ => "dlg_unsaved_settings",
        }));
    }

    private DirtyStateGuard[] GuardsOfPage(string page) => page switch
    {
        "profiles" => new[] { _profileGuard },
        "interviews" => new[] { _targetGuard },
        "settings" => new[] { PageSettings.Guard },
        _ => Array.Empty<DirtyStateGuard>(),
    };

    private void UpdateDirtyIndicators()
    {
        ProfileDirty.Text = _profileGuard.IsDirty ? T("profile_unsaved") : "";
        TargetDirty.Text = _targetGuard.IsDirty ? T("profile_unsaved") : "";
        var dirty = PageSettings.Guard.IsDirty;
        SettingsDirtyText.Text = dirty ? T("profile_unsaved") : T("set_all_saved");
        SettingsDirtyDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SettingsSaveBtn.IsEnabled = SettingsDiscardBtn.IsEnabled = dirty;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_closeConfirmed)
        {
            if (AppDialog.IsOpen(this)) { e.Cancel = true; return; }   // answer the open dialog first
            if (_nav.AnyDirty) { e.Cancel = true; _ = CloseAfterConfirmAsync(); return; }
        }
        SavePlacement();
    }

    private async Task CloseAfterConfirmAsync()
    {
        if (!await _nav.CanProceedAsync(ChangeReason.CloseWindow)) return;
        _closeConfirmed = true;
        Close();
    }

    /// <summary>True when any editor in Home holds unsaved edits (self-test: a fresh start must have none).</summary>
    internal bool HasUnsavedEdits => _nav.AnyDirty;

    /// <summary>The Live Assistant is closing the app: unsaved edits are asked about first. False = stay open.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (AppDialog.IsOpen(this)) { Activate(); return false; }
        if (!_nav.AnyDirty) return true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!await _nav.CanProceedAsync(ChangeReason.CloseWindow)) return false;
        _closeConfirmed = true;
        return true;
    }

    // ================= navigation =================

    /// <summary>User navigation: asks about unsaved edits of the page being left; refused = nothing changes.</summary>
    public async void Navigate(string tag, string? section = null) => await NavigateAsync(new HomeRoute(tag, section));

    /// <summary>
    /// Deep-link navigation (Live gear, pre-interview check, account banner). The route is resolved first and the page
    /// switched in one step; only then does the caller bring the window forward. False = the user chose to stay.
    /// </summary>
    public async Task<bool> NavigateAsync(HomeRoute route)
    {
        var target = route.Gate(SignedOutGate);
        if (target.Page != _page && !await _nav.CanProceedAsync(ChangeReason.Navigate, GuardsOfPage(_page))) return false;
        ShowRoute(target);
        return true;
    }

    /// <summary>Shows a page and its section (no unsaved-changes question).</summary>
    private void ShowRoute(HomeRoute route)
    {
        ShowPage(route.Page);
        if (_page == "settings") PageSettings.ShowSection(route.Section);
        if (_page == "profiles" && route.Section is "personal" or "facts") SetProfileTab(route.Section);
    }

    /// <summary>Opens the Settings page (deep link from the Live Assistant gear and the pre-interview check).</summary>
    public void ShowSettings(string? section = null) => Navigate("settings", section);

    private bool SignedOutGate => _vm.Cloud.IsConfigured && _vm.UsingCloud && !_vm.HasWorkspace;

    /// <summary>Switches the visible page (no unsaved-changes question: callers that leave edits use <see cref="Navigate"/>).</summary>
    internal void ShowPage(string tag)
    {
        var signedOutGate = SignedOutGate;
        tag = new HomeRoute(tag).Gate(signedOutGate).Page;
        var entering = tag != _page;
        var leftPreInterview = entering && _page == "preinterview";
        _page = tag;
        PageStart.Visibility = tag == "start" ? Visibility.Visible : Visibility.Collapsed;
        PageProfiles.Visibility = tag == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        PageInterviews.Visibility = tag == "interviews" ? Visibility.Visible : Visibility.Collapsed;
        PageReports.Visibility = tag == "reports" ? Visibility.Visible : Visibility.Collapsed;
        PageAccount.Visibility = tag == "account" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PagePreInterview.Visibility = PreInterviewFooter.Visibility = tag == "preinterview" ? Visibility.Visible : Visibility.Collapsed;
        PageHelp.Visibility = tag == "help" ? Visibility.Visible : Visibility.Collapsed;
        SettingsFooter.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var nav in new[] { NavProfiles, NavInterviews, NavReports }) nav.Visibility = signedOutGate ? Visibility.Collapsed : Visibility.Visible;
        if (tag == "account" && _vm.Cloud.IsSignedIn) _ = RefreshAccountSectionsAsync(summary: !_sections.Summary.HasData);
        if (tag == "help") RefreshAbout();
        if (tag == "start") RenderDashboard();
        if (tag == "reports") RefreshReports(SelectedReportId());
        if (tag == "settings") PageSettings.LoadFromSettings();   // stored values unless edits are pending
        if (leftPreInterview) PagePreInterview.OnHidden();       // open-ended probes stop when the page is left
        if (entering && tag == "preinterview") PagePreInterview.OnShown();   // runs the checks when there is no current result
        if (entering) ContentScroll.ScrollToTop();
        foreach (var nav in new[] { NavStart, NavProfiles, NavInterviews, NavPreInterview, NavReports, NavAccount, NavSettings, NavHelp })
        {
            var active = (string)nav.Tag == tag;
            nav.Background = active ? (Brush)FindResource("B.Surface2") : Brushes.Transparent;
            nav.Foreground = (Brush)FindResource(active ? "B.Text" : "B.TextDim");
            nav.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void RefreshAll()
    {
        HeaderSignOutBtn.Visibility = _vm.Cloud.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        RefreshUpdates();
        RefreshProfiles();
        RefreshReports(SelectedReportId());
        RefreshAccount();
        RefreshChecklist();
        RefreshAbout();
    }

    private void Nav_Click(object sender, RoutedEventArgs e) => Navigate((string)((Button)sender).Tag);
    private void AccountBadge_Click(object sender, RoutedEventArgs e) => Navigate("account");
    private void GoInterviews_Click(object sender, RoutedEventArgs e) => Navigate("interviews");
    private void GoInterviewProfile_Click(object sender, RoutedEventArgs e) => ShowInterviewProfile();

    /// <summary>Profiles page on the Interview profile tab (onboarding step 3 and the Live window's "Add to profile").</summary>
    public async void ShowInterviewProfile()
    {
        if (_page != "profiles" && !await _nav.CanProceedAsync(ChangeReason.Navigate, GuardsOfPage(_page))) return;
        ShowPage("profiles"); SetProfileTab("personal");
    }

    private void SettingsSave_Click(object sender, RoutedEventArgs e) => PageSettings.Save();
    private void SettingsDiscard_Click(object sender, RoutedEventArgs e) => PageSettings.Discard();

    private void UiLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (UiLanguageBox.SelectedItem is not LanguageInfo l || l.Code == _vm.UiLanguage) return;
        _vm.Settings.UiLanguage = l.Code;
        _vm.Settings.Save();
        _vm.RefreshLanguage();     // sets Loc.Language → every open window re-renders its texts (no restart, no reload)
    }

    private void BuildLanguagePickers()
    {
        var langs = LanguageRegistry.All.Select(l => new LangItem(l.DisplayName, l.Code)).ToList();
        var interview = (TargetLanguage.SelectedItem as LangItem)?.Code;
        var answer = (TargetAnswerLanguage.SelectedItem as LangItem)?.Code;
        using (_targetGuard.Suspend())
        {
            TargetLanguage.ItemsSource = new[] { new LangItem(T("lang_detect"), "auto") }.Concat(langs).ToList();
            TargetAnswerLanguage.ItemsSource = new[] { new LangItem(T("lang_same"), "same") }.Concat(langs).ToList();
            TargetLanguage.SelectedItem = ((IEnumerable<LangItem>)TargetLanguage.ItemsSource).FirstOrDefault(l => l.Code == (interview ?? _target?.ExpectedLanguage ?? "auto"));
            TargetAnswerLanguage.SelectedItem = ((IEnumerable<LangItem>)TargetAnswerLanguage.ItemsSource).FirstOrDefault(l => l.Code == (answer ?? _target?.AnswerLanguage ?? "same"));
        }
    }

    // ================= help & about =================

    /// <summary>About values (labels are bound in XAML and follow the UI language; versions and ids stay technical).</summary>
    private void RefreshAbout()
    {
        var signer = Environment.ProcessPath is { } exe ? Authenticode.TrustedSigner(exe) : null;
        AboutProduct.Text = Branding.ProductName;
        AboutVersion.Text = BuildInfo.Version;
        AboutChannel.Text = BuildInfo.ChannelLabel;
        AboutBuild.Text = BuildInfo.Commit;
        AboutInstall.Text = T(BuildInfo.IsInstalled ? "about_installed" : "about_portable");
        AboutSigning.Text = signer != null ? F("about_signed", signer) : T("about_unsigned");
        AboutService.Text = _vm.Cloud.Config?.BillingEnvironment is { Length: > 0 } b ? T(b == "sandbox" ? "about_env_sandbox" : "about_env_live") : T("about_env_offline");
    }

    private void CopyBuildInfo_Click(object sender, RoutedEventArgs e)
    {
        // Support text stays in English on purpose (read by the support team, not shown in the UI).
        var signer = Environment.ProcessPath is { } exe ? Authenticode.TrustedSigner(exe) : null;
        Clipboard.SetText($"{Branding.ProductName} {BuildInfo.Version} ({BuildInfo.Channel}) build {BuildInfo.Commit} · {(BuildInfo.IsInstalled ? "installed" : "portable")} · {(signer != null ? "signed" : "unsigned")} · {Environment.OSVersion}");
        Notify(T("toast_copied"), ToastKind.Success);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or IOException) { Notify(T("toast_logs_failed"), ToastKind.Error); }
    }

    // ================= onboarding: see HomeWindow.Start.cs (interview readiness dashboard) =================

    // ================= profiles =================

    /// <summary>
    /// Rebuilds the profile list (labels, counts) and keeps the selection. The edited profile is reloaded only when the
    /// selection really changes or <paramref name="reload"/> asks for it (after a save or an import into it).
    /// </summary>
    private void RefreshProfiles(string? selectId = null, bool reload = false)
    {
        var list = Store?.Profiles() ?? Array.Empty<CandidateProfileRecord>();
        var items = list.Select(p => new Item<string>(
            $"{(p.PreferredName.Length > 0 ? p.PreferredName : p.Name.Length > 0 ? p.Name : T("profile_unnamed"))}  ·  {F("profile_confirmed_count", p.Stats.ConfirmedExperience, p.Stats.ConfirmedProfileAnswers)}", p.Id)).ToList();
        var id = selectId ?? _profile?.Id ?? _vm.ActiveProfileId ?? list.FirstOrDefault()?.Id;
        using (SuppressSelection())
        {
            ProfileList.ItemsSource = items;
            ProfileList.SelectedItem = items.FirstOrDefault(i => i.Value == id);
        }
        ProfilesEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = (ProfileList.SelectedItem as Item<string>)?.Value;
        if (reload || selected != _profile?.Id) LoadProfile(selected != null ? Store?.GetProfile(selected) : null);
    }

    private void SelectProfileItem(string? id)
    {
        using (SuppressSelection())
            ProfileList.SelectedItem = (ProfileList.ItemsSource as IEnumerable<Item<string>>)?.FirstOrDefault(i => i.Value == id);
    }

    private async void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectionSuppressed > 0) return;
        var next = (ProfileList.SelectedItem as Item<string>)?.Value;
        if (DirtyStateGuard.ClassifySelection(_profile?.Id, next, programmatic: false) == ChangeReason.ListRefresh) return;
        var current = _profile?.Id;
        // Switching profile also reloads its interviews: both editors are asked about.
        if (!await _nav.CanProceedAsync(ChangeReason.SwitchItem, _profileGuard, _targetGuard)) { SelectProfileItem(current); return; }
        SelectProfileItem(next);
        LoadProfile(next != null ? Store?.GetProfile(next) : null);
    }

    private void LoadProfile(CandidateProfileRecord? p)
    {
        using (_profileGuard.Suspend())
        {
            _profile = p;
            ProfileDetail.IsEnabled = p != null;
            ExportProfileBtn.IsEnabled = DeleteProfileBtn.IsEnabled = p != null;
            ProfileName.Text = p?.Name ?? "";
            ProfilePreferred.Text = p?.PreferredName ?? "";
            ProfileHeadline.Text = p?.Headline ?? "";
            _facts.Clear();
            if (p != null)
                foreach (var f in p.Facts.Where(f => f.IsReviewable && f.Kind is not (FactKind.Personal or FactKind.Boundary)).OrderBy(f => f.Kind).ThenBy(f => f.Company))
                    AddRow(f);
            LoadPersonal(p);
            UpdateFactSummary();
        }
        _profileGuard.MarkClean();
        RefreshTargets();
    }

    private void AddRow(ProfileFact f)
    {
        var row = new FactRow { Fact = f, Initial = f.Status, Confirmed = f.Status == FactStatus.UserConfirmed };
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FactRow.Confirmed)) { UpdateFactSummary(); _profileGuard.MarkDirty(); } };
        _facts.Add(row);
    }

    private void ProfileField_Changed(object sender, TextChangedEventArgs e) => _profileGuard.MarkDirty();

    private string _profileTab = "facts";

    private void ProfileTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tab }) SetProfileTab(tab);
    }

    /// <summary>Segmented control keyboard: Left/Right move to the visually adjacent segment (mirrored in RTL) and select it.</summary>
    private void ProfileTabs_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        e.Handled = true;
        var tabs = new[] { TabFacts, TabPersonal };
        var current = Array.FindIndex(tabs, t => t.IsChecked == true);
        var next = SegmentedNavigation.Move(Math.Max(0, current), tabs.Length, e.Key == Key.Left, ProfileTabs.FlowDirection == FlowDirection.RightToLeft);
        if (next < 0 || next == current) return;
        tabs[next].IsChecked = true;   // → SetProfileTab
        tabs[next].Focus();
    }

    private void SetProfileTab(string tab)
    {
        _profileTab = tab;
        FactsPanel.Visibility = tab == "facts" ? Visibility.Visible : Visibility.Collapsed;
        PersonalPanel.Visibility = tab == "personal" ? Visibility.Visible : Visibility.Collapsed;
        TabFacts.IsChecked = tab == "facts";
        TabPersonal.IsChecked = tab == "personal";
        // Roving tab stop: Tab reaches the control once, on the selected segment; the arrows move inside it.
        TabFacts.IsTabStop = tab == "facts";
        TabPersonal.IsTabStop = tab == "personal";
    }

    /// <summary>The optional interview-profile questions, grouped by section (all localized; nothing required). Built once; a language switch only re-renders the texts.</summary>
    private void BuildPersonalFields()
    {
        PersonalFields.Children.Clear();
        _personalBoxes.Clear();
        _personalTexts.Clear();
        TextBlock Text(Func<string> text, Style? style = null)
        {
            var tb = new TextBlock { Text = text(), TextWrapping = TextWrapping.Wrap };
            if (style != null) tb.Style = style;
            _personalTexts.Add((tb, text));
            return tb;
        }
        foreach (var section in PersonalCategories.All.GroupBy(c => c.Section))
        {
            var key = section.Key;
            PersonalFields.Children.Add(Text(() => T("pcs_" + key), (Style)FindResource("Section")));
            foreach (var c in section)
            {
                var label = Text(() => T($"pc_{c.Key}_label"));
                label.Foreground = (Brush)FindResource("B.Text"); label.FontSize = 13; label.FontWeight = FontWeights.SemiBold; label.Margin = new Thickness(0, 10, 0, 2);
                var prompt = Text(() => T($"pc_{c.Key}_prompt"), (Style)FindResource("Hint"));
                prompt.Margin = new Thickness(0, 0, 0, 6);
                var box = new TextBox { AcceptsReturn = true, MinHeight = (double)FindResource("S.ControlHeight"), MaxHeight = 110, MaxLength = 600, Tag = c.Key };
                System.Windows.Automation.AutomationProperties.SetName(box, T($"pc_{c.Key}_label"));
                System.Windows.Automation.AutomationProperties.SetLabeledBy(box, label);
                box.TextChanged += (_, _) => _profileGuard.MarkDirty();
                _personalBoxes[c.Key] = box;
                PersonalFields.Children.Add(label);
                PersonalFields.Children.Add(prompt);
                PersonalFields.Children.Add(box);
            }
        }
    }

    private void LoadPersonal(CandidateProfileRecord? p)
    {
        using (_profileGuard.Suspend())
            foreach (var (key, box) in _personalBoxes)
            {
                var kind = PersonalCategories.Find(key)!.Boundary ? FactKind.Boundary : FactKind.Personal;
                box.Text = p?.Facts.FirstOrDefault(f => f.Kind == kind && f.Category == key && f.Status == FactStatus.UserConfirmed)?.Text ?? "";
            }
    }

    private async Task<(byte[]? Bytes, string FileName)> PickFileAsync(string title)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = T("file_filter_documents") + " (*.pdf;*.docx;*.txt;*.md)|*.pdf;*.docx;*.txt;*.md", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return (null, "");
        var info = new FileInfo(dlg.FileName);
        if (info.Length > 10 * 1024 * 1024) { await AlertAsync(T("warn_file_too_large"), DialogTone.Warning); return (null, ""); }
        try { return (File.ReadAllBytes(dlg.FileName), dlg.SafeFileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Document could not be read ({ex.GetType().Name})");
            await AlertAsync(T("err_file_read"), DialogTone.Warning);
            return (null, "");
        }
    }

    private async void NewProfile_Click(object sender, RoutedEventArgs e) => await ImportResumeAsync(newProfile: true);
    private async void AddResume_Click(object sender, RoutedEventArgs e) => await ImportResumeAsync(newProfile: _profile == null);

    private async Task<bool> ProfileLimitReachedAsync()
    {
        if (Store == null) return true;
        if (_vm.Cloud.Account?.Entitlements is { } ent && Store.Profiles().Count >= ent.MaxProfiles)
        {
            await AlertAsync(F("warn_profile_limit", ent.MaxProfiles), DialogTone.Warning);
            return true;
        }
        return false;
    }

    private async Task ImportResumeAsync(bool newProfile)
    {
        if (Store == null) return;
        // A new profile replaces the one being edited; a document added to it reloads it: both would drop pending edits.
        if (!await _nav.CanProceedAsync(newProfile ? ChangeReason.SwitchItem : ChangeReason.ReloadData, _profileGuard, _targetGuard)) return;
        if (newProfile && await ProfileLimitReachedAsync()) return;
        var (bytes, name) = await PickFileAsync(T("import_resume_title"));
        if (bytes == null || Store == null) return;
        var (doc, error) = ProfileService.Upload(name, bytes, DocumentKind.Resume);
        if (doc == null) { await AlertAsync(error ?? T("warn_import_failed"), DialogTone.Warning); return; }
        var profile = newProfile ? new CandidateProfileRecord() : Store.GetProfile(_profile!.Id) ?? _profile!;
        var parsed = ProfileService.AddResume(profile, doc);
        try { Store.SaveProfile(profile); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            AppLog.Error("Profile save after import failed", ex);
            await AlertAsync(T("err_profile_save"), DialogTone.Warning);
            return;
        }
        AppLog.Info($"Resume imported: {parsed.Facts.Count} facts, {parsed.Warnings.Count} warnings, flags {doc.SecurityFlags.Count}");
        RefreshProfiles(profile.Id, reload: true);
        ShowPage("profiles"); SetProfileTab("facts");
        RefreshChecklist();
        Notify(parsed.Facts.Count > 0 ? F("toast_resume_imported", parsed.Facts.Count) : T("toast_resume_no_facts"), parsed.Facts.Count > 0 ? ToastKind.Success : ToastKind.Warning);
        if (doc.SecurityFlags.Count > 0) await AlertAsync(T("warn_injection_in_document"), DialogTone.Warning);
    }

    private async void NewEmptyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Store == null || !await _nav.CanProceedAsync(ChangeReason.SwitchItem, _profileGuard, _targetGuard) || await ProfileLimitReachedAsync() || Store == null) return;
        var p = new CandidateProfileRecord { Name = T("profile_new_name") };
        try { Store.SaveProfile(p); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            AppLog.Error("New profile could not be saved", ex);
            Notify(T("err_profile_save"), ToastKind.Error);
            return;
        }
        RefreshProfiles(p.Id, reload: true);
        ProfileName.Focus(); ProfileName.SelectAll();
        Notify(T("toast_profile_created"), ToastKind.Success);
    }

    private System.ComponentModel.ICollectionView? _factView;
    private string _factFilter = "all";

    private void FactFilter_Click(object sender, RoutedEventArgs e)
    {
        _factFilter = (string)((FrameworkElement)sender).Tag;
        _factView?.Refresh();
        UpdateFactSummary();
    }

    /// <summary>Header, footer and filters all use the same numbers (pending ticks included) — they can never disagree.</summary>
    private void UpdateFactSummary()
    {
        var confirmed = _facts.Count(f => f.Confirmed);
        var toReview = _facts.Count - confirmed;
        FactsHeader.Text = _profile == null ? T("facts_header") : F("facts_header_count", toReview);
        FactSummary.Text = _facts.Count == 0 ? "" : F("facts_summary", confirmed, _facts.Count, toReview);
        FactsEmpty.Visibility = _profile != null && _facts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (btn, tag) in new[] { (FilterAll, "all"), (FilterReview, "review"), (FilterConfirmed, "confirmed") })
        {
            btn.Opacity = _factFilter == tag ? 1.0 : 0.65;
            btn.FontWeight = _factFilter == tag ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>
    /// Resume content becomes verified history only by explicit user action: Confirm all asks first, ticks the boxes,
    /// and nothing is stored until Save profile. Any fact can be unticked or edited later.
    /// </summary>
    private async void ConfirmAll_Click(object sender, RoutedEventArgs e)
    {
        var pending = _facts.Where(f => !f.Confirmed).ToList();
        if (pending.Count == 0) { Notify(T("toast_all_confirmed"), ToastKind.Info); return; }
        if (!await AppDialog.ConfirmAsync(this, F("confirm_all_question", pending.Count), T("dlg_confirm_facts"))) return;
        foreach (var f in pending) f.Confirmed = true;
        _factView?.Refresh();
        UpdateFactSummary();
        Notify(F("toast_facts_ticked", pending.Count), ToastKind.Info, T("toast_facts_ticked_detail"));
    }

    private async void AddFact_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;
        var result = await FactEditor.ShowAsync(this, null);
        if (result == null || _profile == null) return;
        var f = ProfileService.AddManualFact(_profile, result.Kind, result.Text, company: result.Company, title: result.Title, period: result.Period, confirmed: result.Confirmed);
        AddRow(f);
        _factView?.Refresh();
        UpdateFactSummary();
        _profileGuard.MarkDirty();
    }

    private async void EditFact_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || (sender as FrameworkElement)?.Tag is not FactRow row) return;
        var result = await FactEditor.ShowAsync(this, row);
        if (result == null || _profile == null) return;
        ProfileService.EditFact(_profile, row.Fact, result.Text, result.Confirmed, result.Company, result.Title, result.Period);
        row.Confirmed = result.Confirmed;
        row.RaiseAll();
        UpdateFactSummary();
        _profileGuard.MarkDirty();
    }

    private async void DeleteFact_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || (sender as FrameworkElement)?.Tag is not FactRow row) return;
        if (!await AppDialog.ConfirmAsync(this, F("confirm_delete_fact", Short(row.Text, 80)), T("dlg_delete"), destructive: true)) return;
        ProfileService.DeleteFact(_profile, row.Fact.Id);
        _facts.Remove(row);
        UpdateFactSummary();
        _profileGuard.MarkDirty();
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e) => SaveProfile();

    /// <summary>Stores every pending profile edit (also the "Save" choice of the unsaved-changes question).</summary>
    private bool SaveProfile()
    {
        if (_profile == null || Store == null) return false;
        ProfileMessage.Clear();
        _profile.Name = ProfileName.Text.Trim();
        _profile.PreferredName = ProfilePreferred.Text.Trim();
        _profile.Headline = ProfileHeadline.Text.Trim();
        var now = DateTime.UtcNow;
        foreach (var r in _facts)
        {
            if (r.Confirmed) r.Fact.SetConfirmed(true, now);
            else if (r.Fact.Status == FactStatus.UserConfirmed) r.Fact.SetConfirmed(false, now);   // un-confirmed: back to "to review", not deleted
        }
        foreach (var (key, box) in _personalBoxes) ProfileService.SetPersonal(_profile, key, box.Text);
        ProfileService.RebuildStories(_profile);
        try
        {
            Store.SaveProfile(_profile);
            foreach (var t in Store.Targets(_profile.Id).Where(t => t.Preparation == PreparationState.Prepared)) { t.Preparation = PreparationState.Stale; Store.SaveTarget(t); }
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            // The edits stay on screen (still unsaved): the user can free space and press Save again.
            AppLog.Error("Profile save failed", ex);
            ProfileMessage.Show(T("err_profile_save"), MessageKind.Error);
            return false;
        }
        // Personal answers apply to the Live Assistant immediately (no re-preparation needed).
        if (_vm.ActiveProfileId == _profile.Id && !_vm.SessionActive && _vm.Workspace != null)
        {
            var (kb, label) = _vm.Workspace.LoadActive(_vm.ActiveProfileId, _vm.ActiveTargetId);
            _vm.UseKnowledge(kb, label);
        }
        _profileGuard.MarkClean();
        var saved = _profile;
        RefreshProfiles(saved.Id);       // labels and counts; the saved record stays on screen
        RefreshTargets(_target?.Id);     // preparation states may have become stale
        RefreshChecklist();
        Notify(T("toast_profile_saved"), ToastKind.Success, F("toast_profile_saved_detail", saved.Stats.ConfirmedExperience, saved.Stats.ConfirmedProfileAnswers));
        return true;
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || Store == null) return;
        var dlg = new SaveFileDialog { FileName = "interview-assistant-profile.zip", Filter = T("file_filter_zip") + " (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            using (var fs = File.Create(dlg.FileName)) Store.ExportProfile(_profile.Id, fs);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            AppLog.Error("Profile export failed", ex);
            Notify(T("err_profile_export"), ToastKind.Error);
            return;
        }
        Notify(T("toast_profile_exported"), ToastKind.Success);
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || Store == null) return;
        if (_vm.SessionActive && _vm.ActiveProfileId == _profile.Id) { await AlertAsync(T("warn_stop_first"), DialogTone.Warning); return; }
        // Destructive confirmation in the in-app dialog: Cancel is the default button (Enter/Escape never delete).
        if (!await AppDialog.ConfirmAsync(this, F("confirm_delete_profile", _profile.Name), T("delete_profile"), destructive: true) || _profile == null || Store == null) return;
        var targetIds = Store.Targets(_profile.Id).Select(t => t.Id).ToList();
        try { Store.DeleteProfile(_profile.Id); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            AppLog.Error("Profile deletion failed", ex);
            Notify(T("err_profile_delete"), ToastKind.Error);
            return;   // nothing changed on screen: pending edits stay
        }
        if (_vm.ActiveProfileId == _profile.Id) _vm.SetActive(null, null);
        _ = _vm.ForgetTranscriptionContextAsync(targetIds);
        _profileGuard.MarkClean(); _targetGuard.MarkClean();   // the deletion was confirmed: its pending edits go with it
        _profile = null;
        RefreshProfiles(reload: true);
        RefreshChecklist();
        Notify(T("toast_profile_deleted"), ToastKind.Info);
    }

    // ================= interviews =================

    private void RefreshTargets(string? selectId = null, bool reload = false)
    {
        InterviewsProfile.Text = _profile == null ? T("interviews_need_profile") : F("interviews_for", _profile.PreferredName.Length > 0 ? _profile.PreferredName : _profile.Name);
        var list = _profile == null || Store == null ? new List<InterviewTarget>() : Store.Targets(_profile.Id).ToList();
        var items = list.Select(t => new Item<string>($"{t.DisplayName}  ·  {StateLabel(t.Preparation)}{(t.Id == _vm.ActiveTargetId ? "  ·  " + T("state_active") : "")}", t.Id)).ToList();
        InterviewsEmpty.Visibility = _profile != null && list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var id = selectId ?? (_target?.ProfileId == _profile?.Id ? _target?.Id : null) ?? _vm.ActiveTargetId;
        using (SuppressSelection())
        {
            TargetList.ItemsSource = items;
            TargetList.SelectedItem = items.FirstOrDefault(i => i.Value == id);
        }
        var selected = (TargetList.SelectedItem as Item<string>)?.Value;
        // A new interview that is being typed (not saved yet) stays on screen while the list refreshes.
        var editingNew = _target == null && _targetGuard.IsDirty && selected == null && _profile != null;
        if (reload || (selected != _target?.Id && !editingNew) || _target?.ProfileId != _profile?.Id && _target != null)
            LoadTarget(selected != null ? Store?.GetTarget(selected) : null);
        else RefreshTargetState();
    }

    private static string StateLabel(PreparationState s) => T(s switch
    {
        PreparationState.Prepared => "state_prepared", PreparationState.Preparing => "state_preparing", PreparationState.Stale => "state_stale",
        PreparationState.Failed => "state_failed", _ => "state_not_prepared",
    });

    private async void TargetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectionSuppressed > 0) return;
        var next = (TargetList.SelectedItem as Item<string>)?.Value;
        if (DirtyStateGuard.ClassifySelection(_target?.Id, next, programmatic: false) == ChangeReason.ListRefresh) return;
        var current = _target?.Id;
        if (!await _nav.CanProceedAsync(ChangeReason.SwitchItem, _targetGuard))
        {
            using (SuppressSelection()) TargetList.SelectedItem = (TargetList.ItemsSource as IEnumerable<Item<string>>)?.FirstOrDefault(i => i.Value == current);
            return;
        }
        using (SuppressSelection()) TargetList.SelectedItem = (TargetList.ItemsSource as IEnumerable<Item<string>>)?.FirstOrDefault(i => i.Value == next);
        LoadTarget(next != null ? Store?.GetTarget(next) : null);
    }

    private void LoadTarget(InterviewTarget? t)
    {
        using (_targetGuard.Suspend())
        {
            _target = t;
            TargetTitle.Text = t?.JobTitle ?? "";
            TargetCompany.Text = t?.Company ?? "";
            TargetJd.Text = t?.JobDescriptionText ?? "";
            TargetLanguage.SelectedItem = ((IEnumerable<LangItem>)TargetLanguage.ItemsSource).FirstOrDefault(l => l.Code == (t?.ExpectedLanguage ?? "auto"));
            TargetAnswerLanguage.SelectedItem = ((IEnumerable<LangItem>)TargetAnswerLanguage.ItemsSource).FirstOrDefault(l => l.Code == (t?.AnswerLanguage ?? "same"));
            Field.SetHasError(TargetTitle, false);
            TargetMessage.Clear();
        }
        _targetGuard.MarkClean();
        RefreshTargetState();
    }

    /// <summary>Preparation status of the shown interview (no field is touched).</summary>
    private void RefreshTargetState()
    {
        var t = _target;
        TargetDetail.IsEnabled = _profile != null;
        DeleteTargetBtn.IsEnabled = t != null;
        PrepProgress.Value = t?.Preparation == PreparationState.Prepared ? 1 : 0;
        PrepStatus.Text = t == null ? "" : StateLabel(t.Preparation) + (t.PreparationError != null ? " — " + Loc.Relocalize(t.PreparationError) : "");
        var pack = t != null ? Store?.GetPack(t.Id) : null;
        PrepSummary.Text = pack == null ? "" : F("prep_summary", pack.Questions.Count, pack.Stories.Count, pack.Gaps.Count, pack.CreatedUtc.ToLocalTime().ToString("g", Loc.Culture));
        UseBtn.IsEnabled = t?.Preparation is PreparationState.Prepared or PreparationState.Stale;
        RenderInterviewReadiness();
    }

    private void TargetField_Changed(object sender, RoutedEventArgs e) => _targetGuard.MarkDirty();

    private async void NewTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) { await AlertAsync(T("warn_create_profile_first"), DialogTone.Info); return; }
        if (!await _nav.CanProceedAsync(ChangeReason.SwitchItem, _targetGuard)) return;
        using (SuppressSelection()) TargetList.SelectedItem = null;
        LoadTarget(null);
        TargetTitle.Focus();
    }

    private async void ImportJd_Click(object sender, RoutedEventArgs e)
    {
        var (bytes, name) = await PickFileAsync(T("import_jd_title"));
        if (bytes == null) return;
        var (doc, error) = ProfileService.Upload(name, bytes, DocumentKind.JobDescription);
        if (doc == null) { await AlertAsync(error ?? T("warn_import_failed"), DialogTone.Warning); return; }
        TargetJd.Text = doc.Text;   // a pending edit until Save / Prepare
    }

    /// <summary>Stores the interview fields. A missing job title is shown at the field (no dialog); returns null then.</summary>
    private InterviewTarget? SaveTargetFields()
    {
        if (_profile == null || Store == null) return null;
        if (string.IsNullOrWhiteSpace(TargetTitle.Text))
        {
            Field.SetHasError(TargetTitle, true);
            TargetMessage.Show(T("warn_job_title"), MessageKind.Error);
            if (_page == "interviews") TargetTitle.Focus();
            return null;
        }
        TargetMessage.Clear();
        var t = _target ?? new InterviewTarget { ProfileId = _profile.Id };
        var changed = t.JobTitle != TargetTitle.Text.Trim() || t.Company != TargetCompany.Text.Trim() || t.JobDescriptionText != TargetJd.Text;
        t.JobTitle = TargetTitle.Text.Trim();
        t.Company = TargetCompany.Text.Trim();
        t.JobDescriptionText = TargetJd.Text;
        t.ExpectedLanguage = (TargetLanguage.SelectedItem as LangItem)?.Code ?? "auto";
        t.AnswerLanguage = (TargetAnswerLanguage.SelectedItem as LangItem)?.Code ?? "same";
        if (changed && t.Preparation == PreparationState.Prepared) t.Preparation = PreparationState.Stale;
        try { Store.SaveTarget(t); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            AppLog.Error("Interview save failed", ex);
            TargetMessage.Show(T("err_interview_save"), MessageKind.Error);
            return null;
        }
        _target = t;
        _targetGuard.MarkClean();
        RefreshTargets(t.Id);
        return t;
    }

    private void SaveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (SaveTargetFields() is { } t) Notify(T("toast_interview_saved"), ToastKind.Success, t.DisplayName);
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareBtn.IsEnabled) return;
        var t = SaveTargetFields();
        if (t == null || _profile == null || Store == null || _vm.Workspace == null) return;
        if (!_profile.ConfirmedFacts.Any() && !await AppDialog.ConfirmAsync(this, T("confirm_prepare_no_facts"), T("dlg_continue"))) return;
        PrepareBtn.IsEnabled = false;
        try
        {
            if (_vm.UsingCloud)
            {
                // Preparations are a plan entitlement metered by the service (the work itself runs on this PC).
                if (!_vm.Cloud.IsSignedIn) { await AlertAsync(T("warn_sign_in_to_prepare"), DialogTone.Info); Navigate("account"); return; }
                try
                {
                    var job = await _vm.Cloud.Api!.StartPreparationAsync(CancellationToken.None);
                    AppLog.Info($"Preparation job granted ({job.PrepJobsRemaining} left this month)");
                }
                catch (BackendException ex)
                {
                    await AlertAsync(ex.Code == "prep_limit" ? T("warn_prep_limit") : CloudService.Describe(ex), DialogTone.Warning);
                    return;
                }
            }
            await RunPreparationAsync(t);
        }
        finally { PrepareBtn.IsEnabled = true; }
    }

    private async Task RunPreparationAsync(InterviewTarget t)
    {
        if (_profile == null || Store == null || _vm.Workspace == null) return;
        _prepCts?.Cancel();
        _prepCts = new CancellationTokenSource();
        t.Preparation = PreparationState.Preparing; t.PreparationError = null; Store.SaveTarget(t);
        var progress = new Progress<PreparationProgress>(p => { PrepProgress.Value = (double)p.Stage / p.TotalStages; PrepStatus.Text = F("prep_progress", p.Stage, p.TotalStages); });
        var store = Store;
        try
        {
            var profile = _profile;
            var pack = await Task.Run(() => new PreparationPipeline(_vm.Workspace.Assets).RunAsync(profile, t, null, progress, _prepCts.Token));
            store.SavePack(pack);
            t.Preparation = PreparationState.Prepared; t.PreparedUtc = DateTime.UtcNow;
            AppLog.Info($"Prepared target: {pack.Questions.Count} questions");
            Notify(T("toast_prep_complete"), ToastKind.Success, F("toast_prep_complete_detail", pack.Questions.Count));
        }
        catch (OperationCanceledException) { t.Preparation = PreparationState.NotPrepared; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any failure (I/O, encryption, network, service) ends as "failed — try again", never stuck in "preparing".
            t.Preparation = PreparationState.Failed; t.PreparationError = ex is BackendException be ? CloudService.Describe(be) : T("prep_failed_short");
            AppLog.Error("Preparation failed", ex);
            Notify(T("toast_prep_failed"), ToastKind.Error);
        }
        finally
        {
            store.SaveTarget(t);
            if (_target?.Id == t.Id) _target = t;
            RefreshTargets(t.Id);
            RefreshChecklist();
        }
    }

    private async void UseTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_target == null || _profile == null || _vm.Workspace == null || !_vm.WorkspaceMatchesAccount) return;
        if (_vm.SessionActive) { await AlertAsync(T("warn_stop_before_switch"), DialogTone.Warning); return; }
        // Starting uses the stored interview: pending edits are saved or discarded first.
        if (!await _nav.CanProceedAsync(ChangeReason.Navigate, _targetGuard) || _target == null || _profile == null || _vm.Workspace == null) return;
        _vm.SetActive(_profile.Id, _target.Id);
        var (kb, label) = _vm.Workspace.LoadActive(_profile.Id, _target.Id);
        _vm.UseKnowledge(kb, label);
        RefreshTargets(_target.Id);
        RefreshChecklist();
        _main.Show();
        _main.Activate();
        _main.ShowToast(F("toast_live_opened", _target.DisplayName), ToastKind.Success);
    }

    private async void DeleteTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_target == null || Store == null) return;
        if (_vm.SessionActive && _vm.ActiveTargetId == _target.Id) { await AlertAsync(T("warn_stop_first"), DialogTone.Warning); return; }
        if (!await AppDialog.ConfirmAsync(this, F("confirm_delete_interview", _target.DisplayName), T("dlg_delete"), destructive: true) || _target == null || Store == null) return;
        try { Store.DeleteTarget(_target.Id); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            AppLog.Error("Interview deletion failed", ex);
            Notify(T("err_interview_delete"), ToastKind.Error);
            return;
        }
        if (_vm.ActiveTargetId == _target.Id) _vm.SetActive(_vm.ActiveProfileId, null);
        _ = _vm.ForgetTranscriptionContextAsync(new[] { _target.Id });
        _targetGuard.MarkClean();
        _target = null;
        RefreshTargets(reload: true);
        Notify(T("toast_interview_deleted"), ToastKind.Info);
    }

    // ================= reports =================

    private ReportRow? SelectedRow => ReportList.SelectedItem as ReportRow;
    private string? SelectedReportId() => SelectedRow?.Session.Id;

    /// <summary>
    /// Newest first; called on page open and automatically right after Stop saves a report (which selects it). Keeps the
    /// selection. No reports: the empty state replaces both panes.
    /// </summary>
    private void RefreshReports(string? selectId = null)
    {
        var sessions = Store?.Sessions() ?? Array.Empty<StoredSession>();
        var rows = sessions.Where(s => s.ReportJson.Length > 0).OrderByDescending(s => s.EndedUtc ?? s.StartedUtc).Select(ReportRowFor).ToList();
        ReportList.ItemsSource = rows;
        ReportList.SelectedItem = rows.FirstOrDefault(r => r.Session.Id == selectId);
        var any = rows.Count > 0;
        ReportsBody.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ReportsEmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ShowSelectedReport();
    }

    private ReportRow ReportRowFor(StoredSession s)
    {
        var r = ReportSerializer.FromJson(s.ReportJson);
        var title = Store?.GetTarget(s.TargetId)?.DisplayName is { Length: > 0 } target ? target
            : r?.TargetLabel is { Length: > 0 } label ? label : T("report_default_label");
        return new ReportRow
        {
            Session = s, Report = r, Title = title,
            When = s.StartedUtc.ToLocalTime().ToString("g", Loc.Culture),
            Questions = r == null ? T("report_unreadable_short") : F("report_row_questions", r.Questions.Count),
        };
    }

    private void SetReportButtons(bool readable, bool selected)
    {
        OpenReportBtn.IsEnabled = CopyReportBtn.IsEnabled = ExportReportBtn.IsEnabled = readable;
        DeleteReportBtn.IsEnabled = selected;
    }

    private InterviewReport? SelectedReport(out StoredSession? session)
    {
        session = SelectedRow?.Session;
        return SelectedRow?.Report;
    }

    private void ReportList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelectedReport();

    /// <summary>Preview pane: "Select a report" without a selection; otherwise the toolbar and the summary of the selected report.</summary>
    private void ShowSelectedReport()
    {
        var row = SelectedRow;
        var r = row?.Report;
        SetReportButtons(r != null, row != null);
        ReportSelectState.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
        ReportToolbar.Visibility = ReportPreview.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        if (row == null) return;
        if (r == null)
        {
            // A damaged report can still be deleted; nothing else is offered.
            ReportPreviewTitle.Text = row.Title;
            ReportPreviewMeta.Text = row.When;
            ReportPreviewQuestions.Text = T("report_unreadable");
            return;
        }
        ReportPreviewTitle.Text = HtmlReportRenderer.Header(r.ProfileLabel, r.TargetLabel) is { Length: > 0 } h ? h : row.Title;
        ReportPreviewMeta.Text = F("report_preview_meta", r.StartedUtc.ToLocalTime().ToString("g", Loc.Culture), (int)(r.EndedUtc - r.StartedUtc).TotalMinutes, r.Questions.Count);
        ReportPreviewQuestions.Text = string.Join("\n", r.Questions.Select(q => F("report_q_prefix", q.Index) + " " + Short(q.OriginalWording, 160)));
        ReportPreview.ScrollToTop();
    }

    /// <summary>Keyboard: Up/Down select (list default), Enter opens, Delete asks to delete.</summary>
    private void ReportList_KeyDown(object sender, KeyEventArgs e)
    {
        if (SelectedRow == null) return;
        if (e.Key == Key.Enter) { e.Handled = true; OpenReport_Click(sender, e); }
        else if (e.Key == Key.Delete) { e.Handled = true; DeleteReport_Click(sender, e); }
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedReport(out var s);
        if (r == null || s == null) return;
        if (_vm.Workspace == null || !_vm.WorkspaceMatchesAccount) return;
        try
        {
            // Viewer copy in this account's private folder (not Documents); removed when the workspace closes.
            var path = _vm.Workspace.WriteViewerCopy(s.Id, HtmlReportRenderer.Render(r, Branding.ProductName));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Report could not be opened", ex);
            Notify(T("err_report_open"), ToastKind.Error);
        }
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedReport(out _);
        if (r == null) return;
        try { Clipboard.SetText(HtmlReportRenderer.RenderText(r, Branding.ProductName)); }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            AppLog.Warn($"Clipboard busy: {ex.GetType().Name}");
            Notify(T("err_report_copy"), ToastKind.Error);
            return;
        }
        Notify(T("toast_report_copied"), ToastKind.Success);
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedReport(out _);
        if (r == null) return;
        var dlg = new SaveFileDialog { FileName = "interview-report.html", Filter = T("file_filter_html") + " (*.html)|*.html" };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, HtmlReportRenderer.Render(r, Branding.ProductName)); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            AppLog.Error("Report export failed", ex);
            Notify(T("err_report_save"), ToastKind.Error);
            return;
        }
        Notify(T("toast_report_exported"), ToastKind.Success);
    }

    private async void DeleteReport_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row || Store == null) return;
        var index = ReportList.SelectedIndex;
        // Destructive confirmation in the in-app dialog: Cancel is the default button.
        if (!await AppDialog.ConfirmAsync(this, T("confirm_delete_report"), T("dlg_delete"), destructive: true) || Store == null) return;
        try { Store.DeleteSession(row.Session.Id); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            AppLog.Error("Report deletion failed", ex);
            Notify(T("err_report_delete"), ToastKind.Error);
            return;
        }
        _vm.Workspace?.DeleteViewerCopy(row.Session.Id);
        RefreshReports();
        // Keyboard flow: the neighbouring report is selected and the list keeps focus.
        if (ReportList.Items.Count > 0)
        {
            ReportList.SelectedIndex = Math.Clamp(index, 0, ReportList.Items.Count - 1);
            (ReportList.ItemContainerGenerator.ContainerFromIndex(ReportList.SelectedIndex) as ListBoxItem)?.Focus();
        }
        Notify(T("toast_report_deleted"), ToastKind.Info);
    }

    // ================= account =================

    private bool _createMode;
    private bool _authBusy;

    private void RefreshAccount()
    {
        var c = _vm.Cloud;
        AccountBadgeText.Text = _vm.AccountLabel;
        HeaderSignOutBtn.Visibility = c.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        NotConfiguredPanel.Visibility = c.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        SignedOutPanel.Visibility = c.IsConfigured && !c.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = c.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        // Page level: only the account-wide state. A request that failed (refresh, devices, billing) is reported in its
        // own section, next to the data it concerns — never as a page-wide error over data that is fine.
        var email = c.Account?.Email ?? c.Auth?.Session?.Email;
        AccountStatus.Text = c.IsSignedIn && c.State is CloudState.SignedIn or CloudState.ServiceUnavailable or CloudState.Connecting && email is { Length: > 0 }
            ? F("acct_signed_in_email", email)
            : c.State == CloudState.Connecting ? T("acct_connecting_service") : Loc.Relocalize(c.Message) ?? "";
        AccountStatus.Foreground = (Brush)FindResource(c.State is CloudState.AccountDisabled or CloudState.UpdateRequired or CloudState.SessionExpired
            || c.State == CloudState.ServiceUnavailable && !c.IsSignedIn ? "B.Thinking" : "B.TextDim");
        SetCreateMode(_createMode);

        // Sign-in options come from the service (publishable configuration only).
        ForgotBtn.Visibility = c.Options.EmailPassword ? Visibility.Visible : Visibility.Collapsed;
        ProviderButtons.Children.Clear();
        foreach (var p in c.Options.OAuthProviders)
        {
            var b = new Button { Content = F("continue_with", ProviderName(p)), Style = (Style)FindResource("Btn.Form"), Margin = (Thickness)FindResource("S.ButtonGap"), Tag = p, IsEnabled = !_authBusy };
            b.Click += async (_, _) => await RunAuth(() => _vm.Cloud.SignInWithProviderAsync(p), T("acct_continue_in_browser"));
            ProviderButtons.Children.Add(b);
        }
        SignUpLegal.Text = LegalSentence();

        if (c.Account is { } a && c.IsSignedIn)
        {
            PlanText.Text = MainViewModel.BillingLabel(a);
            var e = a.Entitlements;
            var trial = a.BillingState is BillingStates.Trial or BillingStates.TrialExhausted;
            var plan = trial ? e.TrialMinutes : e.LiveMinutesPerPeriod;
            var used = a.Usage.LiveSecondsUsed / 60;
            var remaining = a.Usage.LiveSecondsRemaining / 60;
            var rows = new List<(string, string)>
            {
                (T("usage_minutes_used"), F("usage_minutes_used_value", used, plan)),
                (T("usage_remaining_period"), F("usage_remaining_period_value", remaining, a.Usage.PeriodEndUtc.ToLocalTime().ToString("d", Loc.Culture))),
            };
            if (a.Usage.LiveSecondsAvailableToday >= 0)
                rows.Add((T("usage_available_today"), F("usage_available_today_value", Math.Min(a.Usage.LiveSecondsAvailableToday / 60, remaining), a.Usage.DailyLiveMinutesCap)));
            rows.Add((T("usage_preparations"), F("usage_preparations_value", a.Usage.PrepJobsRemaining)));
            rows.Add((T("usage_limits"), F("usage_limits_value", e.MaxDevices, e.MaxProfiles)));
            FillGrid(UsageGrid, rows);
            BillingNotice.Text = a.BillingState switch
            {
                BillingStates.PastDue => F("bill_notice_past_due", a.Subscription?.GraceUntilUtc?.ToLocalTime().ToString("d", Loc.Culture)),
                BillingStates.Canceling => F("bill_notice_canceling", a.Subscription?.CurrentPeriodEndUtc?.ToLocalTime().ToString("d", Loc.Culture)),
                BillingStates.Paused => T("bill_notice_paused"),
                BillingStates.Expired => T("bill_notice_expired"),
                BillingStates.Revoked => T("bill_notice_revoked"),
                BillingStates.TrialExhausted => T("bill_notice_trial_used"),
                _ => "",
            };
            var pro = c.Plans.FirstOrDefault(x => x.Purchasable);
            var paid = CloudService.IsPaidState(a.BillingState) || a.BillingState == BillingStates.Paused;
            UpgradeBtn.Visibility = pro != null && !paid ? Visibility.Visible : Visibility.Collapsed;
            UpgradeBtn.Content = pro?.PriceDisplay is { } price ? F("upgrade_to_price", pro.Name, price) : F("upgrade_to", pro?.Name);
            UpgradeBtn.IsEnabled = !c.UpgradePending && !_sections.Billing.IsLoading;
            ManageBillingBtn.Visibility = a.Subscription != null ? Visibility.Visible : Visibility.Collapsed;
            ManageBillingBtn.IsEnabled = !_sections.Billing.IsLoading;
            UpgradeStatus.Text = c.UpgradePending ? T("upgrade_pending") : "";
            BillingTerms.Text = pro == null ? "" : F("billing_terms", pro.Name, pro.PriceDisplay ?? "");
        }
        else
        {
            // No account data (signed out, or this account's data is not loaded yet): never leave the previous
            // account's plan, minutes or devices on screen.
            PlanText.Text = ""; BillingNotice.Text = ""; UpgradeStatus.Text = ""; BillingTerms.Text = "";
            UsageGrid.Children.Clear(); UsageGrid.RowDefinitions.Clear();
            UpgradeBtn.Visibility = ManageBillingBtn.Visibility = Visibility.Collapsed;
            if (!c.IsSignedIn) { DeviceList.ItemsSource = null; SecurityMessage.Clear(); PrivacyMessage.Clear(); }
        }
        RenderSections();
        RefreshLegal();
        RefreshUpdates();
    }

    /// <summary>Inline status of each Account section (loading, failed with retry, saved data from …).</summary>
    private void RenderSections()
    {
        string Time(DateTimeOffset t) => t.ToLocalTime().ToString("t", Loc.Culture);
        void Render(SectionState s, InlineMessage m, string failedKey, bool retry = true)
        {
            if (s.Status == SectionStatus.Failed)
            {
                var detail = string.Join(" ", new[] { Loc.Relocalize(s.Error), s.StaleNote(Time) }.Where(x => !string.IsNullOrEmpty(x)));
                m.Show(T(failedKey), s.IsStale ? MessageKind.Warning : MessageKind.Error, detail, retry ? T("dlg_retry") : null);
            }
            else if (s.IsLoading && !s.HasData) m.Show(T("acct_section_loading"), MessageKind.Info);
            else m.Clear();
        }
        Render(_sections.Summary, SummaryMessage, "acct_summary_failed");
        Render(_sections.Devices, DevicesMessage, "acct_devices_failed");
        Render(_sections.Billing, BillingMessage, "acct_billing_failed", retry: _lastBillingAction != null);
        RefreshAccountBtn.IsEnabled = !_sections.Summary.IsLoading && !_sections.Devices.IsLoading;
    }

    private void FillGrid(Grid g, IReadOnlyList<(string Label, string Value)> rows)
    {
        g.Children.Clear(); g.RowDefinitions.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = rows[i].Label, Foreground = (Brush)FindResource("B.TextDim"), FontSize = 12.5, Margin = new Thickness(0, 2, 16, 2), TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
            var v = new TextBlock { Text = rows[i].Value, Foreground = (Brush)FindResource("B.Text"), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            g.Children.Add(k); g.Children.Add(v);
        }
    }

    private string LegalSentence()
    {
        var l = _vm.Cloud.Config?.Links;
        if (l?.Terms != null && l.PrivacyPolicy != null) return T("legal_agree_both");
        if (l?.Terms != null) return T("legal_agree_terms");
        if (l?.PrivacyPolicy != null) return T("legal_agree_privacy");
        return "";
    }

    private void RefreshLegal()
    {
        LegalLinks.Children.Clear();
        var l = _vm.Cloud.Config?.Links;
        foreach (var (label, url) in new[] { (T("legal_privacy"), l?.PrivacyPolicy), (T("legal_terms"), l?.Terms), (T("legal_billing_terms"), l?.BillingTerms), (T("legal_support_link"), l?.Support) })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            var b = new Button { Content = label, Style = (Style)FindResource("Btn.Ghost"), Margin = new Thickness(0, 0, 8, 4), ToolTip = url };
            b.Click += (_, _) => CloudService.OpenExternal(url);
            LegalLinks.Children.Add(b);
        }
        SupportEmailText.Text = l?.SupportEmail is { Length: > 0 } mail ? F("legal_support_email", mail) : "";
        LegalHeader.Visibility = LegalLinks.Children.Count > 0 || SupportEmailText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string ProviderName(string p) => p switch { "google" => "Google", "azure" => "Microsoft", "github" => "GitHub", "apple" => "Apple", _ => p };

    private void ModeSignIn_Click(object sender, RoutedEventArgs e) { SetCreateMode(false); AuthMessage.Clear(); EmailBox.Focus(); }
    private void ModeCreate_Click(object sender, RoutedEventArgs e) { SetCreateMode(true); AuthMessage.Clear(); EmailBox.Focus(); }

    private void SetCreateMode(bool create)
    {
        _createMode = create;
        ConfirmPanel.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
        if (!_authBusy) AuthSubmitBtn.Content = T(create ? "create_account" : "sign_in");
        ModeSignInBtn.FontWeight = create ? FontWeights.Normal : FontWeights.SemiBold;
        ModeCreateBtn.FontWeight = create ? FontWeights.SemiBold : FontWeights.Normal;
        ModeSignInBtn.Opacity = create ? 0.65 : 1; ModeCreateBtn.Opacity = create ? 1 : 0.65;
        // The chosen mode is announced and part of the tab order before the fields.
        System.Windows.Automation.AutomationProperties.SetItemStatus(ModeSignInBtn, create ? "" : T("auth_mode_selected"));
        System.Windows.Automation.AutomationProperties.SetItemStatus(ModeCreateBtn, create ? T("auth_mode_selected") : "");
    }

    /// <summary>Busy state of the sign-in form: the submit button shows the progress text and every submit path is disabled (no double submit).</summary>
    private void SetAuthBusy(bool busy, string? busyText = null)
    {
        _authBusy = busy;
        AuthSubmitBtn.IsEnabled = ModeSignInBtn.IsEnabled = ModeCreateBtn.IsEnabled = !busy;
        ForgotBtn.IsEnabled = !busy;
        EmailBox.IsReadOnly = busy;
        foreach (var b in ProviderButtons.Children.OfType<Button>()) b.IsEnabled = !busy;
        AuthSubmitBtn.Content = busy && busyText != null ? busyText : T(_createMode ? "create_account" : "sign_in");
    }

    private async void Email_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (PasswordBox.SecurePassword.Length == 0) { PasswordBox.Focus(); return; }
        await SubmitAuthAsync();
    }

    private async void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_createMode && sender == PasswordBox && ConfirmBox.SecurePassword.Length == 0) { ConfirmBox.Focus(); return; }
        await SubmitAuthAsync();
    }

    private async void AuthSubmit_Click(object sender, RoutedEventArgs e) => await SubmitAuthAsync();

    private static bool ValidEmail(string email) => System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    private void FieldError(Control field, string message)
    {
        Field.SetHasError(field, true);
        AuthMessage.Show(message, MessageKind.Error);
        field.Focus();
    }

    private async Task SubmitAuthAsync()
    {
        if (_authBusy) return;
        var email = EmailBox.Text.Trim();
        var pw = PasswordBox.Password;
        AuthMessage.Clear();
        if (!ValidEmail(email)) { FieldError(EmailBox, T("err_email_invalid")); return; }
        if (pw.Length == 0) { FieldError(PasswordBox, T("err_password_empty")); return; }
        if (_createMode)
        {
            if (pw.Length < 8) { FieldError(PasswordBox, T("err_password_short")); return; }
            if (pw != ConfirmBox.Password) { FieldError(ConfirmBox, T("err_password_mismatch")); return; }
            await RunAuth(async () =>
            {
                var (ok, confirm, error) = await _vm.Cloud.SignUpAsync(email, pw);
                if (ok && confirm) Dispatcher.Invoke(() => { SetCreateMode(false); AuthMessage.Show(F("acct_confirm_email_sent", email), MessageKind.Success); });
                return (ok, error);
            }, T("acct_creating"));
        }
        else await RunAuth(() => _vm.Cloud.SignInAsync(email, pw), T("acct_signing_in"));
    }

    private async Task RunAuth(Func<Task<(bool Ok, string? Error)>> action, string busyText)
    {
        if (_authBusy) return;
        SetAuthBusy(true, busyText);
        try
        {
            var (ok, error) = await action();
            if (!ok) AuthMessage.Show(error ?? T("err_sign_in_failed"), MessageKind.Error);
            else
            {
                PasswordBox.Clear(); ConfirmBox.Clear();
                if (_vm.Cloud.IsSignedIn)
                {
                    _vm.OpenWorkspaceForCurrentAccount();   // this account's data only
                    if (!_vm.SessionActive) _vm.BuildEngine();
                    _sections.Summary.Loaded(DateTimeOffset.Now);
                    await RefreshAccountSectionsAsync(summary: false);
                    Notify(T("toast_signed_in"), ToastKind.Success, _vm.Cloud.Account?.Email);
                }
            }
        }
        finally { SetAuthBusy(false); RefreshAll(); }
    }

    // The pending reset waits up to 15 minutes for the link. A new request (e.g. a corrected address) replaces it.
    private CancellationTokenSource? _resetCts;
    private string? _resetEmail;

    /// <summary>
    /// Password reset. Enumeration-safe: the same neutral notice appears for every syntactically valid address, whether
    /// or not an account exists, and it never claims that an e-mail was delivered. Rate limits are still reported.
    /// </summary>
    private async void Forgot_Click(object sender, RoutedEventArgs e)
    {
        if (_authBusy) return;
        var email = EmailBox.Text.Trim();
        AuthMessage.Clear();
        if (!ValidEmail(email)) { FieldError(EmailBox, T("err_email_first")); return; }
        void Requested() => AuthMessage.Show(PasswordResetCopy.Requested, MessageKind.Info, T("acct_reset_requested_hint"));
        // Same address again while its link is awaited: nothing new to send. Another address: the old wait ends.
        if (_resetCts != null && string.Equals(_resetEmail, email, StringComparison.OrdinalIgnoreCase)) { Requested(); return; }
        _resetCts?.Cancel();
        var cts = _resetCts = new CancellationTokenSource();
        _resetEmail = email;
        try
        {
            var (ok, error) = await _vm.Cloud.ResetPasswordAsync(email, () => Prompt.NewPasswordAsync(this),
                requested: () => Dispatcher.Invoke(Requested), ct: cts.Token);
            if (cts.IsCancellationRequested) return;   // replaced by a newer request
            if (ok) { AuthMessage.Show(T("acct_password_reset_done"), MessageKind.Success); _vm.OpenWorkspaceForCurrentAccount(); }
            else AuthMessage.Show(error ?? T("err_reset_failed"), MessageKind.Error);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_resetCts, cts)) { _resetCts = null; _resetEmail = null; }
            cts.Dispose();
            RefreshAll();
        }
    }

    /// <summary>
    /// Sensitive actions ask for the current password again (or a fresh provider sign-in for OAuth accounts). A wrong
    /// password is reported inside the prompt, which stays open for another try.
    /// </summary>
    private async Task<bool> ReauthenticateAsync(string reasonKey, InlineMessage messages)
    {
        messages.Clear();
        // Accounts that sign in with Google/Microsoft/GitHub/Apple have no password: confirm with that provider instead.
        if (_vm.Cloud.SignInProvider is { Length: > 0 } provider && provider != "email")
        {
            if (!await AppDialog.ConfirmAsync(this, F("reauth_provider_confirm", ProviderLabel(provider)), T("dlg_continue"), title: T("reauth_title"))) return false;
            var (pok, perror) = await _vm.Cloud.ReauthenticateWithProviderAsync(provider);
            if (!pok) messages.Show(perror ?? T("err_reauth_failed"), MessageKind.Error);
            return pok;
        }
        return await Prompt.CurrentPasswordAsync(this, T(reasonKey), _vm.Cloud.Account?.Email ?? "", async password =>
        {
            var (ok, error) = await _vm.Cloud.ReauthenticateAsync(password);
            return ok ? null : error ?? T("err_reauth_failed");
        });
    }

    private static string ProviderLabel(string provider) => provider switch
    {
        "google" => "Google", "azure" => "Microsoft", "github" => "GitHub", "apple" => "Apple",
        _ => provider.Length > 0 ? char.ToUpperInvariant(provider[0]) + provider[1..] : provider,
    };

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (!await ReauthenticateAsync("reauth_reason_password", SecurityMessage)) return;
        var pw = await Prompt.NewPasswordAsync(this);
        if (pw == null) return;
        var (ok, error) = await _vm.Cloud.ChangePasswordAsync(pw);
        if (ok) { SecurityMessage.Show(T("acct_password_changed"), MessageKind.Success); Notify(T("acct_password_changed"), ToastKind.Success); }
        else SecurityMessage.Show(error ?? T("err_generic"), MessageKind.Error);
    }

    /// <summary>Loads the Account sections independently: one failed or slow request never fails the others.</summary>
    private async Task RefreshAccountSectionsAsync(bool summary)
    {
        if (!_vm.Cloud.IsSignedIn) { _sections.ResetAll(); RenderSections(); return; }
        var work = new List<Task> { LoadDevicesAsync() };
        if (summary) work.Add(LoadSummaryAsync());
        await Task.WhenAll(work);
        ApplyAccountLimits();
    }

    private async Task LoadSummaryAsync()
    {
        var ticket = _sections.Summary.Begin();
        RenderSections();
        var ok = await _vm.Cloud.RefreshAsync(CancellationToken.None);
        if (!_vm.Cloud.IsSignedIn) _sections.ResetAll();
        else if (ok && _vm.Cloud.State == CloudState.SignedIn) _sections.Summary.Succeed(ticket, DateTimeOffset.Now);
        else _sections.Summary.Fail(ticket, _vm.Cloud.LastRefreshError is { } ex ? CloudService.Describe(ex, includeReference: false) : T("svc_unavailable"));
        RefreshAccount();
    }

    private async Task LoadDevicesAsync()
    {
        var api = _vm.Cloud.Api;
        if (api == null || !_vm.Cloud.IsSignedIn) { DeviceList.ItemsSource = null; return; }
        var requestedFor = _vm.Cloud.Auth?.Session?.UserId;
        var ticket = _sections.Devices.Begin();
        RenderSections();
        try
        {
            var devices = await api.DevicesAsync(CancellationToken.None);
            if (_vm.Cloud.Auth?.Session?.UserId != requestedFor) return;   // the account changed while loading
            if (!_sections.Devices.Succeed(ticket, DateTimeOffset.Now)) return;
            var selected = (DeviceList.SelectedItem as Item<DeviceDto>)?.Value.Id;
            var items = devices.Where(d => !d.Revoked)
                .Select(d => new Item<DeviceDto>(F("device_row", d.Name, d.ClientVersion, d.LastSeenUtc.ToLocalTime().ToString("g", Loc.Culture))
                    + (d.Id == _vm.Settings.CloudDeviceId ? " · " + T("device_this_pc") : ""), d)).ToList();
            DeviceList.ItemsSource = items;
            DeviceList.SelectedItem = items.FirstOrDefault(i => i.Value.Id == selected);
            RemoveDeviceBtn.IsEnabled = DeviceList.SelectedItem != null;
        }
        catch (Exception ex) when (ex is BackendException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // The list already on screen stays (marked "saved data from …"); the correlation id goes to the log only.
            AppLog.Warn($"Devices request failed: {(ex as BackendException)?.Code ?? ex.GetType().Name} (ref {(ex as BackendException)?.CorrelationId})");
            if (_vm.Cloud.Auth?.Session?.UserId == requestedFor) _sections.Devices.Fail(ticket, CloudService.Describe(ex, includeReference: false));
        }
        finally { RenderSections(); }
    }

    private async void SummaryRetry_Click(object sender, RoutedEventArgs e) { await LoadSummaryAsync(); ApplyAccountLimits(); }
    private async void DevicesRetry_Click(object sender, RoutedEventArgs e) => await LoadDevicesAsync();
    private async void BillingRetry_Click(object sender, RoutedEventArgs e) { if (_lastBillingAction != null) await RunBillingAsync(_lastBillingAction); }

    /// <summary>Plan limits for data that never leaves this PC: report retention (server value, applied locally).</summary>
    private void ApplyAccountLimits()
    {
        if (_vm.Cloud.Account?.Entitlements is { } e && Store != null)
        {
            var removed = Store.ApplyRetention(TimeSpan.FromDays(e.ReportRetentionDays), DateTime.UtcNow);
            if (removed > 0) { AppLog.Info($"Retention removed {removed} report(s)"); RefreshReports(SelectedReportId()); }
        }
    }

    private Func<Task<(bool Ok, string? Error)>>? _lastBillingAction;

    /// <summary>Billing actions (checkout, portal) report in the billing section; a retry repeats the failed action.</summary>
    private async Task RunBillingAsync(Func<Task<(bool Ok, string? Error)>> action)
    {
        if (_sections.Billing.IsLoading) return;
        _lastBillingAction = action;
        var ticket = _sections.Billing.Begin();
        RefreshAccount();
        var (ok, error) = await action();
        if (ok) _sections.Billing.Succeed(ticket, DateTimeOffset.Now);
        else _sections.Billing.Fail(ticket, error ?? T("err_generic"));
        RefreshAccount();
    }

    private async void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        var pro = _vm.Cloud.Plans.FirstOrDefault(p => p.Purchasable);
        if (pro == null) return;
        await RunBillingAsync(() => _vm.Cloud.StartUpgradeAsync(pro.Code));
    }

    private async void Portal_Click(object sender, RoutedEventArgs e) => await RunBillingAsync(() => _vm.Cloud.OpenPortalAsync());

    private async void RefreshAccount_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAccountSectionsAsync(summary: true);
        if (_vm.Cloud.IsSignedIn && _sections.All.All(s => s.Status != SectionStatus.Failed)) Notify(T("toast_billing_refreshed"), ToastKind.Info);
        else if (!_vm.Cloud.IsSignedIn && _vm.Cloud.Message is { Length: > 0 } m) Notify(m, ToastKind.Warning);
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RemoveDeviceBtn.IsEnabled = DeviceList.SelectedItem != null;

    private async void RevokeDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((DeviceList.SelectedItem as Item<DeviceDto>)?.Value is not { } d || _vm.Cloud.Api == null) return;
        if (!await AppDialog.ConfirmAsync(this, F("confirm_remove_device", d.Name), T("dlg_remove"), destructive: true) || _vm.Cloud.Api == null) return;
        RemoveDeviceBtn.IsEnabled = false;
        try { await _vm.Cloud.Api.RevokeDeviceAsync(d.Id, CancellationToken.None); Notify(T("toast_device_removed"), ToastKind.Success); }
        catch (BackendException ex)
        {
            AppLog.Warn($"Device removal failed: {ex.Code} (ref {ex.CorrelationId})");
            DevicesMessage.Show(T("acct_device_remove_failed"), MessageKind.Error, CloudService.Describe(ex, includeReference: false));
            RemoveDeviceBtn.IsEnabled = DeviceList.SelectedItem != null;
            return;
        }
        await LoadDevicesAsync();
    }

    private async void ExportAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Cloud.Api == null) return;
        if (!await ReauthenticateAsync("reauth_reason_export", PrivacyMessage)) return;
        var dlg = new SaveFileDialog { FileName = "interview-assistant-account-export.json", Filter = T("file_filter_json") + " (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true || _vm.Cloud.Api == null) return;
        try
        {
            var json = await _vm.Cloud.Api.ExportAsync(CancellationToken.None);
            await File.WriteAllTextAsync(dlg.FileName, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
            PrivacyMessage.Clear();
            Notify(T("toast_account_exported"), ToastKind.Success);
        }
        catch (BackendException ex) { AppLog.Warn($"Export failed: {ex.Code} (ref {ex.CorrelationId})"); PrivacyMessage.Show(CloudService.Describe(ex, includeReference: false), MessageKind.Error); }
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Cloud.Api == null) return;
        if (!await AppDialog.ConfirmAsync(this, T("confirm_delete_account"), T("dlg_delete_account"), destructive: true)) return;
        if (!await ReauthenticateAsync("reauth_reason_delete", PrivacyMessage) || _vm.Cloud.Api == null) return;
        try
        {
            if (_vm.SessionActive) await _vm.StopAsync();
            var deleted = await _vm.Cloud.Api.DeleteAccountAsync(CancellationToken.None);
            var localRoot = _vm.Workspace?.Store.Root;
            var viewer = _vm.Workspace?.ViewerDir;
            _profileGuard.MarkClean(); _targetGuard.MarkClean();   // the account and its data are gone
            await _vm.SignOutAsync();
            // The service removes the sign-in too; if the auth provider was unreachable it finishes that automatically.
            RefreshAll();
            AuthMessage.Show(T(deleted?.Identity == "pending" ? "acct_deleted_identity_pending" : "acct_deleted"), MessageKind.Success);
            // The service data is gone; this PC's copy of the account's profiles, interviews and reports is the
            // user's to keep or remove.
            if (localRoot != null && await AppDialog.ConfirmAsync(this, T("confirm_delete_local_data"), T("dlg_delete"), destructive: true))
            {
                foreach (var dir in new[] { localRoot, viewer })
                    try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Error("Local data removal failed", ex); }
                Notify(T("toast_local_data_deleted"), ToastKind.Info);
            }
        }
        catch (BackendException ex) { AppLog.Warn($"Account deletion failed: {ex.Code} (ref {ex.CorrelationId})"); PrivacyMessage.Show(CloudService.Describe(ex, includeReference: false), MessageKind.Error); }
        RefreshAll();
    }

    /// <summary>Sign-out closes this account's workspace: pending profile/interview edits are asked about first.</summary>
    private async Task<bool> ConfirmSignOutAsync()
    {
        if (!await _nav.CanProceedAsync(ChangeReason.SignOut, _profileGuard, _targetGuard)) return false;
        return !_vm.SessionActive || await AppDialog.ConfirmAsync(this, T("confirm_sign_out_session"), T("dlg_sign_out"), destructive: true);
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmSignOutAsync()) return;
        await _vm.SignOutAsync();
        EmailBox.Text = "";
        AuthMessage.Clear();
        RefreshAll();
        ShowPage("account");
    }

    private async void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmSignOutAsync()) return;
        await _vm.SignOutAsync();
        EmailBox.Text = "";
        AuthMessage.Clear();
        SetCreateMode(false);
        RefreshAll();
        ShowPage("account");
        EmailBox.Focus();
    }

    // ================= updates =================

    /// <summary>Updates live in Settings → Updates (rc.5; formerly on the Account page).</summary>
    private void RefreshUpdates() => PageSettings.RefreshUpdates();

    // ================= helpers =================

    private static string Short(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    private Task AlertAsync(string message, DialogTone tone) => AppDialog.AlertAsync(this, message, tone);
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnWindowStateChanged()
    {
        var max = WindowState == WindowState.Maximized;
        HomeRoot.Margin = new Thickness(max ? 0 : 8);
        HomeRoot.CornerRadius = new CornerRadius(max ? 0 : 14);
        MaxIcon.Text = max ? "" : "";
    }

    private void SavePlacement()
    {
        var s = _vm.Settings;
        s.HomeMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { s.HomeLeft = Left; s.HomeTop = Top; s.HomeWidth = Width; s.HomeHeight = Height; }
        s.Save();
    }

    /// <summary>Notification inside Home (bottom-right).</summary>
    public void Notify(string text, ToastKind kind = ToastKind.Success, string? detail = null) => Toasts.Show(text, detail, kind);

    // ----- self-test hooks (the in-app self-test drives the real window) -----
    internal DirtyStateGuard ProfileGuardForTest => _profileGuard;
    internal SettingsPage SettingsPageForTest => PageSettings;
    internal string CurrentPage => _page;
    internal string? EditedProfileId => _profile?.Id;
    internal string ProfileTab => _profileTab;
}
