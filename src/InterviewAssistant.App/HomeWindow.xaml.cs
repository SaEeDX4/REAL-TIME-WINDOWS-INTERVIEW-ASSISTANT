using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
/// (targets + preparation), reports, account (plan, quota, devices, security, privacy, updates) and help/about.
/// Everything shown belongs to the signed-in account's workspace; signed out, no user data is loaded or shown.
/// All text comes from the localization tables and refreshes live when the UI language changes.
/// </summary>
public partial class HomeWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    private WorkspaceStore? Store => _vm.Workspace?.Store;
    private CandidateProfileRecord? _profile;
    private InterviewTarget? _target;
    private readonly ObservableCollection<FactRow> _facts = new();
    private readonly Dictionary<string, TextBox> _personalBoxes = new();
    private CancellationTokenSource? _prepCts;
    private string _page = "start";

    // Display records: ToString is the label so no picker can ever show "LangItem { Label = … }".
    private sealed record Item<T>(string Label, T Value) { public override string ToString() => Label; }
    private sealed record LangItem(string Label, string Code) { public override string ToString() => Label; }

    private static string T(string key) => Loc.T(key);
    private static string F(string key, params object?[] args) => Loc.F(key, args);

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

    public HomeWindow(MainViewModel vm, MainWindow main)
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
        ChannelBox.SelectedIndex = _vm.Updates.Channel == "beta" ? 1 : 0;
        BuildPersonalFields();
        _vm.Updates.Changed += OnUpdatesChanged;
        _vm.Cloud.Changed += OnCloudChanged;
        _vm.WorkspaceChanged += OnWorkspaceChanged;
        _vm.ReportSaved += OnReportSaved;
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _vm.Cloud.Changed -= OnCloudChanged; _vm.Updates.Changed -= OnUpdatesChanged; _vm.WorkspaceChanged -= OnWorkspaceChanged;
            _vm.ReportSaved -= OnReportSaved; Loc.LanguageChanged -= OnLanguageChanged; _prepCts?.Cancel();
        };
        WindowPlacement.HookMaximizeToWorkArea(this);
        WindowPlacement.Restore(this, _vm.Settings.HomeLeft, _vm.Settings.HomeTop, _vm.Settings.HomeWidth, _vm.Settings.HomeHeight, _vm.Settings.HomeMaximized);
        StateChanged += (_, _) => OnWindowStateChanged();
        Closing += (_, e) =>
        {
            if (ProfileHasUnsavedChanges && !Confirm(T("confirm_discard_profile_changes"))) { e.Cancel = true; return; }
            SavePlacement();
        };
        _factView = System.Windows.Data.CollectionViewSource.GetDefaultView(_facts);
        _factView.Filter = o => o is FactRow r && (_factFilter == "all" || (_factFilter == "review") != r.Confirmed);
        _factView.SortDescriptions.Add(new SortDescription(nameof(FactRow.Confirmed), ListSortDirection.Ascending));
        SetProfileTab("facts");
        RefreshAll();
        ShowPage(!_vm.HasWorkspace && _vm.Cloud.IsConfigured ? "account" : _vm.Settings.AuthorizedUseAcknowledged && _vm.Settings.FirstRunCompleted ? "interviews" : "start");
    }

    private void OnUpdatesChanged() => Dispatcher.BeginInvoke(RefreshUpdates);
    private void OnCloudChanged() => Dispatcher.BeginInvoke(() => { RefreshAccount(); RefreshChecklist(); });
    private void OnWorkspaceChanged() => Dispatcher.BeginInvoke(() => { _profile = null; _target = null; RefreshAll(); ShowPage(_page); });
    private void OnReportSaved(string id) => Dispatcher.BeginInvoke(() => RefreshReports(id));
    private void OnLanguageChanged() => Dispatcher.BeginInvoke(() =>
    {
        // XAML texts refresh by binding; texts composed in code are rebuilt here.
        BuildLanguagePickers();
        BuildPersonalFields();
        if (_profile != null) LoadPersonal(_profile);
        foreach (var r in _facts) r.RaiseAll();
        RefreshAll();
        ShowPage(_page);
    });

    // ================= navigation =================

    internal void ShowPage(string tag)
    {
        var signedOutGate = _vm.Cloud.IsConfigured && _vm.UsingCloud && !_vm.HasWorkspace;
        if (signedOutGate && tag is "profiles" or "interviews" or "reports") tag = "account";
        _page = tag;
        PageStart.Visibility = tag == "start" ? Visibility.Visible : Visibility.Collapsed;
        PageProfiles.Visibility = tag == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        PageInterviews.Visibility = tag == "interviews" ? Visibility.Visible : Visibility.Collapsed;
        PageReports.Visibility = tag == "reports" ? Visibility.Visible : Visibility.Collapsed;
        PageAccount.Visibility = tag == "account" ? Visibility.Visible : Visibility.Collapsed;
        PageHelp.Visibility = tag == "help" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var nav in new[] { NavProfiles, NavInterviews, NavReports }) nav.Visibility = signedOutGate ? Visibility.Collapsed : Visibility.Visible;
        if (tag == "account" && _vm.Cloud.IsSignedIn) _ = LoadAccountExtrasAsync();
        if (tag == "help") RefreshAbout();
        if (tag == "reports") RefreshReports();
        foreach (var nav in new[] { NavStart, NavProfiles, NavInterviews, NavReports, NavAccount, NavHelp })
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
        RefreshReports();
        RefreshAccount();
        RefreshChecklist();
    }

    private void Nav_Click(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);
    private void AccountBadge_Click(object sender, RoutedEventArgs e) => ShowPage("account");
    private void GoInterviews_Click(object sender, RoutedEventArgs e) => ShowPage("interviews");
    private void GoInterviewProfile_Click(object sender, RoutedEventArgs e) { ShowPage("profiles"); SetProfileTab("personal"); }
    private void Readiness_Click(object sender, RoutedEventArgs e) => _main.OpenReadiness(firstRun: false);
    private void Settings_Click(object sender, RoutedEventArgs e) { _main.OpenSettings(); RefreshChecklist(); }

    private void UiLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (UiLanguageBox.SelectedItem is not LanguageInfo l || l.Code == _vm.UiLanguage) return;
        _vm.Settings.UiLanguage = l.Code;
        _vm.Settings.Save();
        _vm.RefreshLanguage();     // sets Loc.Language → every open window refreshes (no restart)
    }

    private void BuildLanguagePickers()
    {
        var langs = LanguageRegistry.All.Select(l => new LangItem(l.DisplayName, l.Code)).ToList();
        var interview = (TargetLanguage.SelectedItem as LangItem)?.Code;
        var answer = (TargetAnswerLanguage.SelectedItem as LangItem)?.Code;
        TargetLanguage.ItemsSource = new[] { new LangItem(T("lang_detect"), "auto") }.Concat(langs).ToList();
        TargetAnswerLanguage.ItemsSource = new[] { new LangItem(T("lang_same"), "same") }.Concat(langs).ToList();
        TargetLanguage.SelectedItem = ((IEnumerable<LangItem>)TargetLanguage.ItemsSource).FirstOrDefault(l => l.Code == (interview ?? _target?.ExpectedLanguage ?? "auto"));
        TargetAnswerLanguage.SelectedItem = ((IEnumerable<LangItem>)TargetAnswerLanguage.ItemsSource).FirstOrDefault(l => l.Code == (answer ?? _target?.AnswerLanguage ?? "same"));
    }

    // ================= help & about =================

    private void RefreshAbout()
    {
        var signer = Environment.ProcessPath is { } exe ? Authenticode.TrustedSigner(exe) : null;
        var env = _vm.Cloud.Config?.BillingEnvironment is { Length: > 0 } b ? T(b == "sandbox" ? "about_env_sandbox" : "about_env_live") : T("about_env_offline");
        var rows = new (string Key, string Value)[]
        {
            ("about_product", Branding.ProductName),
            ("about_version", BuildInfo.Version),
            ("about_channel", BuildInfo.ChannelLabel),
            ("about_build", BuildInfo.Commit),
            ("about_install", T(BuildInfo.IsInstalled ? "about_installed" : "about_portable")),
            ("about_signing", signer != null ? F("about_signed", signer) : T("about_unsigned")),
            ("about_service", env),
        };
        AboutGrid.Children.Clear(); AboutGrid.RowDefinitions.Clear();
        for (int i = 0; i < rows.Length; i++)
        {
            AboutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = T(rows[i].Key), Foreground = (Brush)FindResource("B.TextDim"), FontSize = 12.5, Margin = new Thickness(0, 3, 12, 3) };
            var v = new TextBlock { Text = rows[i].Value, Foreground = (Brush)FindResource("B.Text"), FontSize = 12.5, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
            if (rows[i].Key is "about_version" or "about_build") v.FlowDirection = FlowDirection.LeftToRight;   // numbers/ids read left-to-right in RTL too
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            AboutGrid.Children.Add(k); AboutGrid.Children.Add(v);
        }
    }

    private void CopyBuildInfo_Click(object sender, RoutedEventArgs e)
    {
        var signer = Environment.ProcessPath is { } exe ? Authenticode.TrustedSigner(exe) : null;
        Clipboard.SetText($"{Branding.ProductName} {BuildInfo.Version} ({BuildInfo.Channel}) build {BuildInfo.Commit} · {(BuildInfo.IsInstalled ? "installed" : "portable")} · {(signer != null ? "signed" : "unsigned")} · {Environment.OSVersion}");
        Notify(T("toast_copied"), ToastKind.Success);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or IOException) { Notify(T("toast_logs_failed"), ToastKind.Error); }
    }

    // ================= onboarding =================

    private void RefreshChecklist()
    {
        var profiles = Store?.Profiles() ?? Array.Empty<CandidateProfileRecord>();
        var prepared = Store == null ? 0 : profiles.SelectMany(p => Store.Targets(p.Id)).Count(t => t.Preparation == PreparationState.Prepared);
        Step1Title.Text = (_vm.Settings.AuthorizedUseAcknowledged ? "✓ " : "1 · ") + T("start_step1");
        var devMode = BuildFlavor.Developer && !_vm.UsingCloud;
        var accountOk = _vm.Cloud.IsSignedIn || (devMode && SecretStore.HasKey);
        Step2Title.Text = (accountOk ? "✓ " : "2 · ") + T("account");
        Step2Status.Text = devMode ? T(SecretStore.HasKey ? "start_dev_key" : "start_dev_no_key")
            : !_vm.Cloud.IsConfigured ? T("acct_not_configured_long")
            : _vm.Cloud.IsSignedIn ? F("start_signed_in", _vm.AccountLabel)
            : _vm.Cloud.Message ?? T("start_account_hint");
        DeveloperModeBtn.Visibility = BuildFlavor.Developer ? Visibility.Visible : Visibility.Collapsed;
        GoAccountBtn.Visibility = _vm.Cloud.IsConfigured && !_vm.Cloud.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        var stats = profiles.Select(p => p.Stats).ToList();
        Step3Title.Text = (profiles.Any(p => p.ConfirmedFacts.Any()) ? "✓ " : "3 · ") + T("start_step3");
        Step3Status.Text = !_vm.HasWorkspace ? T("start_sign_in_first")
            : profiles.Count == 0 ? T("start_profile_hint")
            : F("start_profile_status", profiles.Count, stats.Sum(s => s.Confirmed), stats.Sum(s => s.ToReview));
        StartImportBtn.IsEnabled = StartInterviewProfileBtn.IsEnabled = StartAddInterviewBtn.IsEnabled = _vm.HasWorkspace;
        Step4Title.Text = (prepared > 0 ? "✓ " : "4 · ") + T("start_step4");
        Step4Status.Text = prepared == 0 ? T("start_interview_hint") : F("start_interview_status", prepared, _vm.ActiveLabel);
        DoneBtn.IsEnabled = _vm.Settings.AuthorizedUseAcknowledged;
    }

    private void Ack_Changed(object sender, RoutedEventArgs e)
    {
        _vm.Settings.AuthorizedUseAcknowledged = AckBox.IsChecked == true;
        _vm.Settings.Save();
        RefreshChecklist();
    }

    private void DeveloperMode_Click(object sender, RoutedEventArgs e)
    {
        if (!BuildFlavor.Developer || _vm.SessionActive) return;
        _vm.Settings.Mode = "developer";
        _vm.Settings.Save();
        _vm.OpenWorkspaceForCurrentAccount();
        _vm.BuildEngine();
        _main.OpenSettings();
        RefreshAll();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.FirstRunCompleted = true;
        _vm.Settings.Save();
        _main.Activate();
        Close();
    }

    private void GoAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Settings.Mode != "cloud" && !_vm.SessionActive) { _vm.Settings.Mode = "cloud"; _vm.Settings.Save(); _vm.OpenWorkspaceForCurrentAccount(); _vm.BuildEngine(); }
        ShowPage("account");
    }

    // ================= profiles =================

    private void RefreshProfiles(string? selectId = null)
    {
        var list = Store?.Profiles() ?? Array.Empty<CandidateProfileRecord>();
        ProfileList.ItemsSource = list.Select(p => new Item<string>(
            $"{(p.PreferredName.Length > 0 ? p.PreferredName : p.Name.Length > 0 ? p.Name : T("profile_unnamed"))}  ·  {F("profile_confirmed_count", p.Stats.Confirmed)}", p.Id)).ToList();
        ProfilesEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var id = selectId ?? _profile?.Id ?? _vm.ActiveProfileId ?? list.FirstOrDefault()?.Id;
        ProfileList.SelectedItem = ((IEnumerable<Item<string>>)ProfileList.ItemsSource).FirstOrDefault(i => i.Value == id);
        if (ProfileList.SelectedItem == null) LoadProfile(null);
    }

    private bool ProfileHasUnsavedChanges => _profile != null && ProfileDirty.Text.Length > 0;
    private bool _revertingSelection;

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_revertingSelection) return;
        var next = ProfileList.SelectedItem as Item<string>;
        if (ProfileHasUnsavedChanges)
        {
            // A refresh that re-selects the same profile (language switch, list refresh) keeps the edits on screen.
            if (next?.Value == _profile!.Id) return;
            if (!Confirm(T("confirm_discard_profile_changes")))
            {
                _revertingSelection = true;
                ProfileList.SelectedItem = ((IEnumerable<Item<string>>)ProfileList.ItemsSource).FirstOrDefault(i => i.Value == _profile.Id);
                _revertingSelection = false;
                return;
            }
        }
        LoadProfile(next != null ? Store?.GetProfile(next.Value) : null);
    }

    private void LoadProfile(CandidateProfileRecord? p)
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
        RefreshTargets();
    }

    private void AddRow(ProfileFact f)
    {
        var row = new FactRow { Fact = f, Initial = f.Status, Confirmed = f.Status == FactStatus.UserConfirmed };
        row.PropertyChanged += (_, _) => { UpdateFactSummary(); ProfileDirty.Text = T("profile_unsaved"); };
        _facts.Add(row);
    }

    private void ProfileTab_Click(object sender, RoutedEventArgs e) => SetProfileTab((string)((Button)sender).Tag);

    private void SetProfileTab(string tab)
    {
        FactsPanel.Visibility = tab == "facts" ? Visibility.Visible : Visibility.Collapsed;
        PersonalPanel.Visibility = tab == "personal" ? Visibility.Visible : Visibility.Collapsed;
        TabFacts.Opacity = tab == "facts" ? 1 : 0.65;
        TabPersonal.Opacity = tab == "personal" ? 1 : 0.65;
        TabFacts.FontWeight = tab == "facts" ? FontWeights.SemiBold : FontWeights.Normal;
        TabPersonal.FontWeight = tab == "personal" ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>The optional interview-profile questions, grouped by section (all localized; nothing required).</summary>
    private void BuildPersonalFields()
    {
        var current = _personalBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text);
        PersonalFields.Children.Clear();
        _personalBoxes.Clear();
        foreach (var section in PersonalCategories.All.GroupBy(c => c.Section))
        {
            PersonalFields.Children.Add(new TextBlock { Text = T("pcs_" + section.Key), Style = (Style)FindResource("Section") });
            foreach (var c in section)
            {
                PersonalFields.Children.Add(new TextBlock { Text = T($"pc_{c.Key}_label"), Foreground = (Brush)FindResource("B.Text"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
                PersonalFields.Children.Add(new TextBlock { Text = T($"pc_{c.Key}_prompt"), Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 4) });
                var box = new TextBox
                {
                    Style = (Style)FindResource("Input"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 34, MaxHeight = 110,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 600, Tag = c.Key,
                };
                System.Windows.Automation.AutomationProperties.SetName(box, T($"pc_{c.Key}_label"));
                box.TextChanged += (_, _) => ProfileDirty.Text = T("profile_unsaved");
                if (current.TryGetValue(c.Key, out var text)) box.Text = text;
                _personalBoxes[c.Key] = box;
                PersonalFields.Children.Add(box);
            }
        }
    }

    private void LoadPersonal(CandidateProfileRecord? p)
    {
        foreach (var (key, box) in _personalBoxes)
        {
            var kind = PersonalCategories.Find(key)!.Boundary ? FactKind.Boundary : FactKind.Personal;
            box.Text = p?.Facts.FirstOrDefault(f => f.Kind == kind && f.Category == key && f.Status == FactStatus.UserConfirmed)?.Text ?? "";
        }
        ProfileDirty.Text = "";
    }

    private byte[]? PickFile(string title, out string fileName)
    {
        fileName = "";
        var dlg = new OpenFileDialog { Title = title, Filter = T("file_filter_documents") + " (*.pdf;*.docx;*.txt;*.md)|*.pdf;*.docx;*.txt;*.md", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return null;
        var info = new FileInfo(dlg.FileName);
        if (info.Length > 10 * 1024 * 1024) { Warn(T("warn_file_too_large")); return null; }
        fileName = dlg.SafeFileName;
        return File.ReadAllBytes(dlg.FileName);
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e) => ImportResume(newProfile: true);
    private void AddResume_Click(object sender, RoutedEventArgs e) => ImportResume(newProfile: _profile == null);

    private bool ProfileLimitReached()
    {
        if (Store == null) return true;
        if (_vm.Cloud.Account?.Entitlements is { } ent && Store.Profiles().Count >= ent.MaxProfiles)
        {
            Warn(F("warn_profile_limit", ent.MaxProfiles));
            return true;
        }
        return false;
    }

    private void ImportResume(bool newProfile)
    {
        if (Store == null) return;
        if (newProfile && ProfileLimitReached()) return;
        var bytes = PickFile(T("import_resume_title"), out var name);
        if (bytes == null) return;
        var (doc, error) = ProfileService.Upload(name, bytes, DocumentKind.Resume);
        if (doc == null) { Warn(error ?? T("warn_import_failed")); return; }
        var profile = newProfile ? new CandidateProfileRecord() : _profile!;
        var parsed = ProfileService.AddResume(profile, doc);
        Store.SaveProfile(profile);
        AppLog.Info($"Résumé imported: {parsed.Facts.Count} facts, {parsed.Warnings.Count} warnings, flags {doc.SecurityFlags.Count}");
        if (doc.SecurityFlags.Count > 0) Warn(T("warn_injection_in_document"));
        RefreshProfiles(profile.Id);
        ShowPage("profiles"); SetProfileTab("facts");
        RefreshChecklist();
        Notify(parsed.Facts.Count > 0 ? F("toast_resume_imported", parsed.Facts.Count) : T("toast_resume_no_facts"), parsed.Facts.Count > 0 ? ToastKind.Success : ToastKind.Warning);
    }

    private void NewEmptyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Store == null || ProfileLimitReached()) return;
        var p = new CandidateProfileRecord { Name = T("profile_new_name") };
        Store.SaveProfile(p);
        RefreshProfiles(p.Id);
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
    /// Résumé content becomes verified history only by explicit user action: Confirm all asks first, ticks the boxes,
    /// and nothing is stored until Save profile. Any fact can be unticked or edited later.
    /// </summary>
    private void ConfirmAll_Click(object sender, RoutedEventArgs e)
    {
        var pending = _facts.Where(f => !f.Confirmed).ToList();
        if (pending.Count == 0) { Notify(T("toast_all_confirmed"), ToastKind.Info); return; }
        if (!Confirm(F("confirm_all_question", pending.Count))) return;
        foreach (var f in pending) f.Confirmed = true;
        _factView?.Refresh();
        UpdateFactSummary();
        Notify(F("toast_facts_ticked", pending.Count), ToastKind.Info, T("toast_facts_ticked_detail"));
    }

    private void AddFact_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;
        var result = FactEditor.Show(this, null);
        if (result == null) return;
        var f = ProfileService.AddManualFact(_profile, result.Kind, result.Text, company: result.Company, title: result.Title, period: result.Period, confirmed: result.Confirmed);
        AddRow(f);
        _factView?.Refresh();
        UpdateFactSummary();
        ProfileDirty.Text = T("profile_unsaved");
    }

    private void EditFact_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || (sender as FrameworkElement)?.Tag is not FactRow row) return;
        var result = FactEditor.Show(this, row);
        if (result == null) return;
        ProfileService.EditFact(_profile, row.Fact, result.Text, result.Confirmed, result.Company, result.Title, result.Period);
        row.Confirmed = result.Confirmed;
        row.RaiseAll();
        UpdateFactSummary();
        ProfileDirty.Text = T("profile_unsaved");
    }

    private void DeleteFact_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || (sender as FrameworkElement)?.Tag is not FactRow row) return;
        if (!Confirm(F("confirm_delete_fact", Short(row.Text, 80)))) return;
        ProfileService.DeleteFact(_profile, row.Fact.Id);
        _facts.Remove(row);
        UpdateFactSummary();
        ProfileDirty.Text = T("profile_unsaved");
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || Store == null) return;
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
        Store.SaveProfile(_profile);
        foreach (var t in Store.Targets(_profile.Id).Where(t => t.Preparation == PreparationState.Prepared)) { t.Preparation = PreparationState.Stale; Store.SaveTarget(t); }
        // Personal answers apply to the Live Assistant immediately (no re-preparation needed).
        if (_vm.ActiveProfileId == _profile.Id && !_vm.SessionActive && _vm.Workspace != null)
        {
            var (kb, label) = _vm.Workspace.LoadActive(_vm.ActiveProfileId, _vm.ActiveTargetId);
            _vm.UseKnowledge(kb, label);
        }
        RefreshProfiles(_profile.Id);
        RefreshChecklist();
        ProfileDirty.Text = "";
        Notify(T("toast_profile_saved"), ToastKind.Success, F("toast_profile_saved_detail", _profile.Stats.Confirmed));
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || Store == null) return;
        var dlg = new SaveFileDialog { FileName = "interview-assistant-profile.zip", Filter = T("file_filter_zip") + " (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        using (var fs = File.Create(dlg.FileName)) Store.ExportProfile(_profile.Id, fs);
        Notify(T("toast_profile_exported"), ToastKind.Success);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || Store == null) return;
        if (_vm.SessionActive && _vm.ActiveProfileId == _profile.Id) { Warn(T("warn_stop_first")); return; }
        if (!Confirm(F("confirm_delete_profile", _profile.Name))) return;
        if (_vm.ActiveProfileId == _profile.Id) _vm.SetActive(null, null);
        Store.DeleteProfile(_profile.Id);
        _profile = null;
        RefreshProfiles();
        RefreshChecklist();
        Notify(T("toast_profile_deleted"), ToastKind.Info);
    }

    // ================= interviews =================

    private void RefreshTargets(string? selectId = null)
    {
        InterviewsProfile.Text = _profile == null ? T("interviews_need_profile") : F("interviews_for", _profile.PreferredName.Length > 0 ? _profile.PreferredName : _profile.Name);
        var list = _profile == null || Store == null ? new List<InterviewTarget>() : Store.Targets(_profile.Id).ToList();
        TargetList.ItemsSource = list.Select(t => new Item<string>($"{t.DisplayName}  ·  {StateLabel(t.Preparation)}{(t.Id == _vm.ActiveTargetId ? "  ·  " + T("state_active") : "")}", t.Id)).ToList();
        InterviewsEmpty.Visibility = _profile != null && list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var id = selectId ?? _target?.Id ?? _vm.ActiveTargetId;
        TargetList.SelectedItem = ((IEnumerable<Item<string>>)TargetList.ItemsSource).FirstOrDefault(i => i.Value == id);
        if (TargetList.SelectedItem == null) LoadTarget(null);
    }

    private static string StateLabel(PreparationState s) => T(s switch
    {
        PreparationState.Prepared => "state_prepared", PreparationState.Preparing => "state_preparing", PreparationState.Stale => "state_stale",
        PreparationState.Failed => "state_failed", _ => "state_not_prepared",
    });

    private void TargetList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadTarget(TargetList.SelectedItem is Item<string> i ? Store?.GetTarget(i.Value) : null);

    private void LoadTarget(InterviewTarget? t)
    {
        _target = t;
        TargetDetail.IsEnabled = _profile != null;
        DeleteTargetBtn.IsEnabled = t != null;
        TargetTitle.Text = t?.JobTitle ?? "";
        TargetCompany.Text = t?.Company ?? "";
        TargetJd.Text = t?.JobDescriptionText ?? "";
        TargetLanguage.SelectedItem = ((IEnumerable<LangItem>)TargetLanguage.ItemsSource).FirstOrDefault(l => l.Code == (t?.ExpectedLanguage ?? "auto"));
        TargetAnswerLanguage.SelectedItem = ((IEnumerable<LangItem>)TargetAnswerLanguage.ItemsSource).FirstOrDefault(l => l.Code == (t?.AnswerLanguage ?? "same"));
        PrepProgress.Value = t?.Preparation == PreparationState.Prepared ? 1 : 0;
        PrepStatus.Text = t == null ? "" : StateLabel(t.Preparation) + (t.PreparationError != null ? " — " + t.PreparationError : "");
        var pack = t != null ? Store?.GetPack(t.Id) : null;
        PrepSummary.Text = pack == null ? "" : F("prep_summary", pack.Questions.Count, pack.Stories.Count, pack.Gaps.Count, pack.CreatedUtc.ToLocalTime().ToString("g", Loc.Culture));
        UseBtn.IsEnabled = t?.Preparation is PreparationState.Prepared or PreparationState.Stale;
    }

    private void NewTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) { Warn(T("warn_create_profile_first")); return; }
        TargetList.SelectedItem = null;
        LoadTarget(null);
        TargetTitle.Focus();
    }

    private void ImportJd_Click(object sender, RoutedEventArgs e)
    {
        var bytes = PickFile(T("import_jd_title"), out var name);
        if (bytes == null) return;
        var (doc, error) = ProfileService.Upload(name, bytes, DocumentKind.JobDescription);
        if (doc == null) { Warn(error ?? T("warn_import_failed")); return; }
        TargetJd.Text = doc.Text;
    }

    private InterviewTarget? SaveTargetFields()
    {
        if (_profile == null || Store == null) return null;
        if (string.IsNullOrWhiteSpace(TargetTitle.Text)) { Warn(T("warn_job_title")); return null; }
        var t = _target ?? new InterviewTarget { ProfileId = _profile.Id };
        var changed = t.JobTitle != TargetTitle.Text.Trim() || t.Company != TargetCompany.Text.Trim() || t.JobDescriptionText != TargetJd.Text;
        t.JobTitle = TargetTitle.Text.Trim();
        t.Company = TargetCompany.Text.Trim();
        t.JobDescriptionText = TargetJd.Text;
        t.ExpectedLanguage = (TargetLanguage.SelectedItem as LangItem)?.Code ?? "auto";
        t.AnswerLanguage = (TargetAnswerLanguage.SelectedItem as LangItem)?.Code ?? "same";
        if (changed && t.Preparation == PreparationState.Prepared) t.Preparation = PreparationState.Stale;
        Store.SaveTarget(t);
        _target = t;
        return t;
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        var t = SaveTargetFields();
        if (t == null || _profile == null || Store == null || _vm.Workspace == null) return;
        if (!_profile.ConfirmedFacts.Any() && !Confirm(T("confirm_prepare_no_facts"))) return;
        if (_vm.UsingCloud)
        {
            // Preparations are a plan entitlement metered by the service (the work itself runs on this PC).
            if (!_vm.Cloud.IsSignedIn) { Warn(T("warn_sign_in_to_prepare")); ShowPage("account"); return; }
            try
            {
                var job = await _vm.Cloud.Api!.StartPreparationAsync(CancellationToken.None);
                AppLog.Info($"Preparation job granted ({job.PrepJobsRemaining} left this month)");
            }
            catch (BackendException ex)
            {
                Warn(ex.Code == "prep_limit" ? T("warn_prep_limit") : CloudService.Describe(ex));
                return;
            }
        }
        _prepCts?.Cancel();
        _prepCts = new CancellationTokenSource();
        PrepareBtn.IsEnabled = false;
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
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            t.Preparation = PreparationState.Failed; t.PreparationError = T("prep_failed_short");
            AppLog.Error("Preparation failed", ex);
            Notify(T("toast_prep_failed"), ToastKind.Error);
        }
        finally
        {
            store.SaveTarget(t);
            PrepareBtn.IsEnabled = true;
            RefreshTargets(t.Id);
            RefreshChecklist();
        }
    }

    private void UseTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_target == null || _profile == null || _vm.Workspace == null) return;
        if (_vm.SessionActive) { Warn(T("warn_stop_before_switch")); return; }
        _vm.SetActive(_profile.Id, _target.Id);
        var (kb, label) = _vm.Workspace.LoadActive(_profile.Id, _target.Id);
        _vm.UseKnowledge(kb, label);
        RefreshTargets(_target.Id);
        RefreshChecklist();
        _main.Show();
        _main.Activate();
        _main.ShowToast(F("toast_live_opened", _target.DisplayName), ToastKind.Success);
    }

    private void DeleteTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_target == null || Store == null) return;
        if (_vm.SessionActive && _vm.ActiveTargetId == _target.Id) { Warn(T("warn_stop_first")); return; }
        if (!Confirm(F("confirm_delete_interview", _target.DisplayName))) return;
        if (_vm.ActiveTargetId == _target.Id) _vm.SetActive(_vm.ActiveProfileId, null);
        Store.DeleteTarget(_target.Id);
        _target = null;
        RefreshTargets();
        Notify(T("toast_interview_deleted"), ToastKind.Info);
    }

    // ================= reports =================

    /// <summary>Newest first; called on page open and automatically right after Stop saves a report.</summary>
    private void RefreshReports(string? selectId = null)
    {
        var sessions = Store?.Sessions() ?? Array.Empty<StoredSession>();
        var items = sessions.Where(s => s.ReportJson.Length > 0).OrderByDescending(s => s.EndedUtc ?? s.StartedUtc)
            .Select(s => new Item<StoredSession>($"{s.StartedUtc.ToLocalTime().ToString("g", Loc.Culture)}  ·  {LabelFor(s)}", s)).ToList();
        ReportList.ItemsSource = items;
        ReportsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReportList.SelectedItem = items.FirstOrDefault(i => i.Value.Id == selectId) ?? (selectId != null ? items.FirstOrDefault() : ReportList.SelectedItem);
        if (ReportList.SelectedItem == null) ReportPreview.Visibility = Visibility.Collapsed;
        SetReportButtons(ReportList.SelectedItem != null);
    }

    private void SetReportButtons(bool selected) =>
        OpenReportBtn.IsEnabled = CopyReportBtn.IsEnabled = ExportReportBtn.IsEnabled = DeleteReportBtn.IsEnabled = selected;

    private string LabelFor(StoredSession s) => Store?.GetTarget(s.TargetId)?.DisplayName ?? T("report_default_label");

    private InterviewReport? SelectedReport(out StoredSession? session)
    {
        session = (ReportList.SelectedItem as Item<StoredSession>)?.Value;
        return session == null ? null : ReportSerializer.FromJson(session.ReportJson);
    }

    private void ReportList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var r = SelectedReport(out var s);
        SetReportButtons(r != null && s != null);
        if (r == null || s == null) { ReportPreview.Visibility = Visibility.Collapsed; return; }
        ReportPreview.Visibility = Visibility.Visible;
        ReportPreviewTitle.Text = HtmlReportRenderer.Header(r.ProfileLabel, r.TargetLabel);
        ReportPreviewMeta.Text = F("report_preview_meta", r.StartedUtc.ToLocalTime().ToString("g", Loc.Culture), (int)(r.EndedUtc - r.StartedUtc).TotalMinutes, r.Questions.Count);
        ReportPreviewQuestions.Text = string.Join("\n", r.Questions.Take(8).Select(q => F("report_q_prefix", q.Index) + " " + Short(q.OriginalWording, 110))) + (r.Questions.Count > 8 ? "\n…" : "");
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedReport(out var s);
        if (r == null || s == null) return;
        if (_vm.Workspace == null) return;
        // Viewer copy in this account's private folder (not Documents); removed when the workspace closes.
        var path = _vm.Workspace.WriteViewerCopy(s.Id, HtmlReportRenderer.Render(r, Branding.ProductName));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedReport(out _);
        if (r == null) return;
        Clipboard.SetText(HtmlReportRenderer.RenderText(r, Branding.ProductName));
        Notify(T("toast_report_copied"), ToastKind.Success);
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedReport(out _);
        if (r == null) return;
        var dlg = new SaveFileDialog { FileName = "interview-report.html", Filter = T("file_filter_html") + " (*.html)|*.html" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, HtmlReportRenderer.Render(r, Branding.ProductName));
        Notify(T("toast_report_exported"), ToastKind.Success);
    }

    private void DeleteReport_Click(object sender, RoutedEventArgs e)
    {
        if ((ReportList.SelectedItem as Item<StoredSession>)?.Value is not { } s || Store == null) return;
        if (!Confirm(T("confirm_delete_report"))) return;
        Store.DeleteSession(s.Id);
        _vm.Workspace?.DeleteViewerCopy(s.Id);
        RefreshReports();
        Notify(T("toast_report_deleted"), ToastKind.Info);
    }

    // ================= account =================

    private bool _createMode;

    private void RefreshAccount()
    {
        var c = _vm.Cloud;
        AccountBadgeText.Text = _vm.AccountLabel;
        HeaderSignOutBtn.Visibility = c.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        NotConfiguredPanel.Visibility = c.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        SignedOutPanel.Visibility = c.IsConfigured && !c.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = c.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        AccountStatus.Text = c.State switch
        {
            CloudState.SignedIn => F("acct_signed_in_email", c.Account?.Email),
            CloudState.Connecting => T("acct_connecting_service"),
            _ => c.Message ?? "",
        };
        AccountStatus.Foreground = (Brush)FindResource(c.State is CloudState.ServiceUnavailable or CloudState.AccountDisabled or CloudState.UpdateRequired or CloudState.SessionExpired ? "B.Thinking" : "B.TextDim");
        SetCreateMode(_createMode);

        // Sign-in options come from the service (publishable configuration only).
        ForgotBtn.Visibility = c.Options.EmailPassword ? Visibility.Visible : Visibility.Collapsed;
        ProviderButtons.Children.Clear();
        foreach (var p in c.Options.OAuthProviders)
        {
            var b = new Button { Content = F("continue_with", ProviderName(p)), Style = (Style)FindResource("Btn.Base"), Margin = new Thickness(0, 0, 8, 8), Tag = p };
            b.Click += async (_, _) => await RunAuth(() => _vm.Cloud.SignInWithProviderAsync(p), T("acct_continue_in_browser"));
            ProviderButtons.Children.Add(b);
        }
        SignUpLegal.Text = LegalSentence();

        if (c.Account is { } a)
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
                BillingStates.TrialExhausted => T("bill_notice_trial_used"),
                _ => "",
            };
            var pro = c.Plans.FirstOrDefault(x => x.Purchasable);
            var paid = CloudService.IsPaidState(a.BillingState) || a.BillingState == BillingStates.Paused;
            UpgradeBtn.Visibility = pro != null && !paid ? Visibility.Visible : Visibility.Collapsed;
            UpgradeBtn.Content = pro?.PriceDisplay is { } price ? F("upgrade_to_price", pro.Name, price) : F("upgrade_to", pro?.Name);
            UpgradeBtn.IsEnabled = !c.UpgradePending;
            ManageBillingBtn.Visibility = a.Subscription != null ? Visibility.Visible : Visibility.Collapsed;
            UpgradeStatus.Text = c.UpgradePending ? T("upgrade_pending") : "";
            BillingTerms.Text = pro == null ? "" : F("billing_terms", pro.Name, pro.PriceDisplay ?? "");
        }
        RefreshLegal();
        RefreshUpdates();
    }

    private void FillGrid(Grid g, IReadOnlyList<(string Label, string Value)> rows)
    {
        g.Children.Clear(); g.RowDefinitions.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = rows[i].Label, Foreground = (Brush)FindResource("B.TextDim"), FontSize = 12.5, Margin = new Thickness(0, 2, 16, 2) };
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

    private void ModeSignIn_Click(object sender, RoutedEventArgs e) => SetCreateMode(false);
    private void ModeCreate_Click(object sender, RoutedEventArgs e) => SetCreateMode(true);

    private void SetCreateMode(bool create)
    {
        _createMode = create;
        ConfirmPanel.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
        AuthSubmitBtn.Content = T(create ? "create_account" : "sign_in");
        ModeSignInBtn.FontWeight = create ? FontWeights.Normal : FontWeights.SemiBold;
        ModeCreateBtn.FontWeight = create ? FontWeights.SemiBold : FontWeights.Normal;
        ModeSignInBtn.Opacity = create ? 0.65 : 1; ModeCreateBtn.Opacity = create ? 1 : 0.65;
    }

    private async void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SubmitAuthAsync();
    }

    private async void AuthSubmit_Click(object sender, RoutedEventArgs e) => await SubmitAuthAsync();

    private static bool ValidEmail(string email) => System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    private async Task SubmitAuthAsync()
    {
        var email = EmailBox.Text.Trim();
        var pw = PasswordBox.Password;
        AccountError.Text = ""; AccountInfo.Text = "";
        if (!ValidEmail(email)) { AccountError.Text = T("err_email_invalid"); return; }
        if (pw.Length == 0) { AccountError.Text = T("err_password_empty"); return; }
        if (_createMode)
        {
            if (pw.Length < 8) { AccountError.Text = T("err_password_short"); return; }
            if (pw != ConfirmBox.Password) { AccountError.Text = T("err_password_mismatch"); return; }
            await RunAuth(async () =>
            {
                var (ok, confirm, error) = await _vm.Cloud.SignUpAsync(email, pw);
                if (ok && confirm) Dispatcher.Invoke(() => { SetCreateMode(false); AccountInfo.Text = F("acct_confirm_email_sent", email); });
                return (ok, error);
            }, T("acct_creating"));
        }
        else await RunAuth(() => _vm.Cloud.SignInAsync(email, pw), T("acct_signing_in"));
    }

    private async Task RunAuth(Func<Task<(bool Ok, string? Error)>> action, string busyText)
    {
        AuthSubmitBtn.IsEnabled = false;
        AccountStatus.Text = busyText;
        try
        {
            var (ok, error) = await action();
            if (!ok) AccountError.Text = error ?? T("err_sign_in_failed");
            else
            {
                PasswordBox.Clear(); ConfirmBox.Clear();
                if (_vm.Cloud.IsSignedIn)
                {
                    _vm.OpenWorkspaceForCurrentAccount();   // this account's data only
                    if (!_vm.SessionActive) _vm.BuildEngine();
                    await LoadAccountExtrasAsync();
                    ApplyAccountLimits();
                    Notify(T("toast_signed_in"), ToastKind.Success, _vm.Cloud.Account?.Email);
                }
            }
        }
        finally { AuthSubmitBtn.IsEnabled = true; RefreshAll(); }
    }

    private async void Forgot_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        AccountError.Text = ""; AccountInfo.Text = "";
        if (!ValidEmail(email)) { AccountError.Text = T("err_email_first"); return; }
        AccountInfo.Text = F("acct_reset_sent", email);
        var (ok, error) = await _vm.Cloud.ResetPasswordAsync(email, () => Task.FromResult(Prompt.NewPassword(this)));
        AccountInfo.Text = ok ? T("acct_password_reset_done") : "";
        if (!ok) AccountError.Text = error ?? T("err_reset_failed");
        if (ok) _vm.OpenWorkspaceForCurrentAccount();
        RefreshAll();
    }

    /// <summary>Sensitive actions ask for the current password again (or a fresh provider sign-in for OAuth accounts).</summary>
    private async Task<bool> ReauthenticateAsync(string reasonKey)
    {
        AccountError.Text = "";
        var password = Prompt.CurrentPassword(this, T(reasonKey), _vm.Cloud.Account?.Email ?? "");
        if (password == null) return false;
        var (ok, error) = await _vm.Cloud.ReauthenticateAsync(password);
        if (!ok) { AccountError.Text = error ?? T("err_reauth_failed"); Notify(AccountError.Text, ToastKind.Error); }
        return ok;
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        AccountError.Text = ""; AccountInfo.Text = "";
        if (!await ReauthenticateAsync("reauth_reason_password")) return;
        var pw = Prompt.NewPassword(this);
        if (pw == null) return;
        var (ok, error) = await _vm.Cloud.ChangePasswordAsync(pw);
        if (ok) { AccountInfo.Text = T("acct_password_changed"); Notify(T("acct_password_changed"), ToastKind.Success); }
        else AccountError.Text = error;
    }

    private async Task LoadAccountExtrasAsync()
    {
        var api = _vm.Cloud.Api;
        if (api == null || !_vm.Cloud.IsSignedIn) return;
        try
        {
            var devices = await api.DevicesAsync(CancellationToken.None);
            DeviceList.ItemsSource = devices.Where(d => !d.Revoked)
                .Select(d => new Item<DeviceDto>(F("device_row", d.Name, d.ClientVersion, d.LastSeenUtc.ToLocalTime().ToString("g", Loc.Culture))
                    + (d.Id == _vm.Settings.CloudDeviceId ? " · " + T("device_this_pc") : ""), d)).ToList();
            RemoveDeviceBtn.IsEnabled = DeviceList.SelectedItem != null;
        }
        catch (BackendException ex) { AccountError.Text = CloudService.Describe(ex); }
    }

    /// <summary>Plan limits for data that never leaves this PC: report retention (server value, applied locally).</summary>
    private void ApplyAccountLimits()
    {
        if (_vm.Cloud.Account?.Entitlements is { } e && Store != null)
        {
            var removed = Store.ApplyRetention(TimeSpan.FromDays(e.ReportRetentionDays), DateTime.UtcNow);
            if (removed > 0) { AppLog.Info($"Retention removed {removed} report(s)"); RefreshReports(); }
        }
    }

    private async Task Guard(Func<Task<(bool Ok, string? Error)>> action)
    {
        AccountError.Text = "";
        var (ok, error) = await action();
        if (!ok) { AccountError.Text = error ?? T("err_generic"); Notify(AccountError.Text, ToastKind.Error); }
        RefreshAccount();
    }

    private async void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        var pro = _vm.Cloud.Plans.FirstOrDefault(p => p.Purchasable);
        if (pro == null) return;
        UpgradeBtn.IsEnabled = false;
        await Guard(() => _vm.Cloud.StartUpgradeAsync(pro.Code));
    }

    private async void Portal_Click(object sender, RoutedEventArgs e) => await Guard(() => _vm.Cloud.OpenPortalAsync());

    private async void RefreshAccount_Click(object sender, RoutedEventArgs e)
    {
        await _vm.Cloud.RefreshAsync(CancellationToken.None);
        await LoadAccountExtrasAsync();
        ApplyAccountLimits();
        RefreshAccount();
        if (_vm.Cloud.IsSignedIn) Notify(T("toast_billing_refreshed"), ToastKind.Info);
        else if (_vm.Cloud.Message is { Length: > 0 } m) Notify(m, ToastKind.Warning);
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RemoveDeviceBtn.IsEnabled = DeviceList.SelectedItem != null;

    private async void RevokeDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((DeviceList.SelectedItem as Item<DeviceDto>)?.Value is not { } d || _vm.Cloud.Api == null) return;
        if (!Confirm(F("confirm_remove_device", d.Name))) return;
        try { await _vm.Cloud.Api.RevokeDeviceAsync(d.Id, CancellationToken.None); Notify(T("toast_device_removed"), ToastKind.Success); }
        catch (BackendException ex) { AccountError.Text = CloudService.Describe(ex); Notify(CloudService.Describe(ex), ToastKind.Error); }
        await LoadAccountExtrasAsync();
    }

    private async void ExportAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Cloud.Api == null) return;
        if (!await ReauthenticateAsync("reauth_reason_export")) return;
        var dlg = new SaveFileDialog { FileName = "interview-assistant-account-export.json", Filter = T("file_filter_json") + " (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var json = await _vm.Cloud.Api.ExportAsync(CancellationToken.None);
            await File.WriteAllTextAsync(dlg.FileName, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
            Notify(T("toast_account_exported"), ToastKind.Success);
        }
        catch (BackendException ex) { AccountError.Text = CloudService.Describe(ex); }
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Cloud.Api == null) return;
        if (!Confirm(T("confirm_delete_account"))) return;
        if (!await ReauthenticateAsync("reauth_reason_delete")) return;
        try
        {
            if (_vm.SessionActive) await _vm.StopAsync();
            await _vm.Cloud.Api.DeleteAccountAsync(CancellationToken.None);
            var localRoot = _vm.Workspace?.Store.Root;
            var viewer = _vm.Workspace?.ViewerDir;
            await _vm.SignOutAsync();
            AccountInfo.Text = T("acct_deleted");
            // The service data is gone; this PC's copy of the account's profiles, interviews and reports is the
            // user's to keep or remove.
            if (localRoot != null && Confirm(T("confirm_delete_local_data")))
            {
                foreach (var dir in new[] { localRoot, viewer })
                    try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Error("Local data removal failed", ex); }
                Notify(T("toast_local_data_deleted"), ToastKind.Info);
            }
        }
        catch (BackendException ex) { AccountError.Text = CloudService.Describe(ex); }
        RefreshAll();
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SessionActive && !Confirm(T("confirm_sign_out_session"))) return;
        await _vm.SignOutAsync();
        EmailBox.Text = "";
        RefreshAll();
        ShowPage("account");
    }

    private async void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SessionActive && !Confirm(T("confirm_sign_out_session"))) return;
        await _vm.SignOutAsync();
        EmailBox.Text = "";
        SetCreateMode(false);
        RefreshAll();
        ShowPage("account");
        EmailBox.Focus();
    }

    // ================= updates =================

    private void RefreshUpdates()
    {
        BuildChannelText.Text = F("updates_this_build", BuildInfo.Version, BuildInfo.ChannelLabel);
        UpdateStatus.Text = _vm.Updates.StatusText;
        RestartUpdateBtn.Visibility = _vm.Updates.UpdateReady ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Channel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelBox.SelectedItem is not ComboBoxItem { Tag: string ch } || ch == _vm.Settings.UpdateChannel) return;
        _vm.Settings.UpdateChannel = ch;
        _vm.Settings.Save();
        UpdateStatus.Text = T("updates_channel_restart");
    }

    private void RestartUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.Updates.TryApplyAndRestart(_vm.SessionActive)) Warn(T("warn_update_session"));
    }

    // ================= helpers =================

    private static string Short(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    /// <summary>Right-to-left reading order for the system message box in Arabic/Persian.</summary>
    private static MessageBoxOptions BoxOptions => Loc.IsRtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
    private void Warn(string message) => MessageBox.Show(this, message, Branding.ProductName, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, BoxOptions);
    private bool Confirm(string message) => MessageBox.Show(this, message, Branding.ProductName, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK, BoxOptions) == MessageBoxResult.OK;
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
}
