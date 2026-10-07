using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InterviewAssistant.App.Services;
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

/// <summary>Dashboard: onboarding checklist, profiles (with fact review), interviews (targets + preparation), reports, account.</summary>
public partial class HomeWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    private WorkspaceStore? Store => _vm.Workspace?.Store;
    private CandidateProfileRecord? _profile;
    private InterviewTarget? _target;
    private readonly ObservableCollection<FactRow> _facts = new();
    private CancellationTokenSource? _prepCts;

    private sealed record Item<T>(string Label, T Value);
    private sealed record LangItem(string Label, string Code);

    public sealed class FactRow : INotifyPropertyChanged
    {
        public required ProfileFact Fact { get; init; }
        public required FactStatus Initial { get; init; }
        private bool _confirmed;
        public bool Confirmed { get => _confirmed; set { _confirmed = value; PropertyChanged?.Invoke(this, new(nameof(Confirmed))); } }
        public string Text => Fact.Kind == FactKind.Role ? $"{Fact.Title} — {Fact.Company} ({Fact.Period})".Replace(" ()", "") : Fact.Text;
        public string KindLabel => Fact.Kind.ToString().ToLowerInvariant() + (Fact.Status == FactStatus.AiInference ? " · AI inference" : "");
        public string Source => Fact.Provenance is { } p ? $"Source: “{Trim(p.Snippet, 110)}” (line {p.LineStart})" : "Source: added manually";
        public event PropertyChangedEventHandler? PropertyChanged;
        private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
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
        var langs = LanguageRegistry.All.Select(l => new LangItem($"{l.NativeName} ({l.EnglishName})", l.Code)).ToList();
        TargetLanguage.ItemsSource = new[] { new LangItem("Detect automatically", "auto") }.Concat(langs).ToList();
        TargetAnswerLanguage.ItemsSource = new[] { new LangItem("Same as the interviewer", "same") }.Concat(langs).ToList();
        AckBox.IsChecked = _vm.Settings.AuthorizedUseAcknowledged;
        _vm.Cloud.Changed += OnCloudChanged;
        Closed += (_, _) => { _vm.Cloud.Changed -= OnCloudChanged; _prepCts?.Cancel(); };
        Localize();
        RefreshAll();
        ShowPage(_vm.Settings.AuthorizedUseAcknowledged && _vm.Settings.FirstRunCompleted ? "interviews" : "start");
    }

    private void OnCloudChanged() => Dispatcher.BeginInvoke(() => { RefreshAccount(); RefreshChecklist(); });

    private string L(string key) => _vm.L(key);

    private void Localize()
    {
        FlowDirection = _vm.UiFlowDirection;
        NavProfiles.Content = ProfilesTitle.Text = L("profiles");
        NavInterviews.Content = InterviewsTitle.Text = L("interviews");
        NavReports.Content = ReportsTitle.Text = L("reports");
        NavAccount.Content = AccountTitle.Text = L("account");
        NavSettings.Content = L("settings");
        AckText.Text = L("authorized_use");
        PrepareBtn.Content = L("prepare");
        UseBtn.Content = L("start_interview");
    }

    internal void ShowPage(string tag)
    {
        PageStart.Visibility = tag == "start" ? Visibility.Visible : Visibility.Collapsed;
        PageProfiles.Visibility = tag == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        PageInterviews.Visibility = tag == "interviews" ? Visibility.Visible : Visibility.Collapsed;
        PageReports.Visibility = tag == "reports" ? Visibility.Visible : Visibility.Collapsed;
        PageAccount.Visibility = tag == "account" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "account" && _vm.Cloud.IsSignedIn) _ = LoadAccountExtrasAsync();
    }

    private void RefreshAll()
    {
        RefreshProfiles();
        RefreshReports();
        RefreshAccount();
        RefreshChecklist();
    }

    // ================= onboarding =================

    private void RefreshChecklist()
    {
        var profiles = Store?.Profiles() ?? Array.Empty<CandidateProfileRecord>();
        var prepared = profiles.SelectMany(p => Store!.Targets(p.Id)).Count(t => t.Preparation == PreparationState.Prepared);
        Step1Title.Text = (_vm.Settings.AuthorizedUseAcknowledged ? "✓ " : "1 · ") + "Authorized use";
        var accountOk = _vm.Cloud.IsSignedIn || (_vm.Settings.Mode == "developer" && SecretStore.HasKey);
        Step2Title.Text = (accountOk ? "✓ " : "2 · ") + L("account");
        Step2Status.Text = !_vm.Cloud.IsConfigured ? "No service configured in this build — use developer mode with your own OpenAI key."
            : _vm.Cloud.IsSignedIn ? "Signed in: " + _vm.AccountLabel
            : _vm.Settings.Mode == "developer" ? "Developer mode (own OpenAI key)" + (SecretStore.HasKey ? "" : " — add your key in Settings.")
            : "Sign in to use live transcription and AI answers. No API key needed.";
        GoAccountBtn.Content = L("sign_in");
        GoAccountBtn.Visibility = _vm.Cloud.IsConfigured && !_vm.Cloud.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        Step3Title.Text = (profiles.Any(p => p.ConfirmedFacts.Any()) ? "✓ " : "3 · ") + L("profiles");
        Step3Status.Text = profiles.Count == 0 ? "Import your résumé (PDF, DOCX or TXT), then confirm the facts the assistant may use."
            : $"{profiles.Count} profile(s) · {profiles.Sum(p => p.ConfirmedFacts.Count())} confirmed facts · {profiles.Sum(p => p.PendingReviewCount)} awaiting review";
        Step4Title.Text = (prepared > 0 ? "✓ " : "4 · ") + L("interviews");
        Step4Status.Text = prepared == 0 ? "Add the job title, company and job description, then Prepare (builds 50–150 likely questions with answers from your confirmed facts)."
            : $"{prepared} prepared interview(s). Active: {_vm.ActiveLabel}";
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
        _vm.Settings.Mode = "developer";
        _vm.Settings.Save();
        _vm.BuildEngine();
        _main.OpenSettings();
        RefreshChecklist();
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
        if (_vm.Settings.Mode != "cloud") { _vm.Settings.Mode = "cloud"; _vm.Settings.Save(); _vm.BuildEngine(); }
        ShowPage("account");
    }
    private void GoInterviews_Click(object sender, RoutedEventArgs e) => ShowPage("interviews");
    private void Readiness_Click(object sender, RoutedEventArgs e) => _main.OpenReadiness(firstRun: false);
    private void Nav_Click(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);
    private void Settings_Click(object sender, RoutedEventArgs e) { _main.OpenSettings(); Localize(); RefreshChecklist(); }

    private void UiLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (UiLanguageBox.SelectedItem is not LanguageInfo l || l.Code == _vm.UiLanguage) return;
        _vm.Settings.UiLanguage = l.Code;
        _vm.Settings.Save();
        _vm.RefreshLanguage();
        Localize();
        RefreshChecklist();
    }

    // ================= profiles =================

    private void RefreshProfiles(string? selectId = null)
    {
        var list = Store?.Profiles() ?? Array.Empty<CandidateProfileRecord>();
        ProfileList.ItemsSource = list.Select(p => new Item<string>($"{(p.PreferredName.Length > 0 ? p.PreferredName : p.Name.Length > 0 ? p.Name : "Unnamed")}  ·  {p.ConfirmedFacts.Count()} confirmed", p.Id)).ToList();
        var id = selectId ?? _profile?.Id ?? _vm.Settings.ActiveProfileId ?? list.FirstOrDefault()?.Id;
        ProfileList.SelectedItem = ((IEnumerable<Item<string>>)ProfileList.ItemsSource).FirstOrDefault(i => i.Value == id);
        if (ProfileList.SelectedItem == null) LoadProfile(null);
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadProfile(ProfileList.SelectedItem is Item<string> i ? Store?.GetProfile(i.Value) : null);

    private void LoadProfile(CandidateProfileRecord? p)
    {
        _profile = p;
        ProfileDetail.IsEnabled = p != null;
        ProfileName.Text = p?.Name ?? "";
        ProfilePreferred.Text = p?.PreferredName ?? "";
        ProfileHeadline.Text = p?.Headline ?? "";
        _facts.Clear();
        if (p != null)
            foreach (var f in p.Facts.Where(f => f.Kind != FactKind.Summary).OrderBy(f => f.Kind).ThenBy(f => f.Company))
                _facts.Add(new FactRow { Fact = f, Initial = f.Status, Confirmed = f.Status == FactStatus.UserConfirmed });
        FactsHeader.Text = p == null ? "FACTS" : $"FACTS — {p.PendingReviewCount} AWAITING REVIEW";
        RefreshTargets();
    }

    private byte[]? PickFile(string title, out string fileName)
    {
        fileName = "";
        var dlg = new OpenFileDialog { Title = title, Filter = "Documents (*.pdf;*.docx;*.txt;*.md)|*.pdf;*.docx;*.txt;*.md", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return null;
        var info = new FileInfo(dlg.FileName);
        if (info.Length > 10 * 1024 * 1024) { Warn("That file is larger than 10 MB."); return null; }
        fileName = dlg.SafeFileName;
        return File.ReadAllBytes(dlg.FileName);
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e) => ImportResume(newProfile: true);
    private void AddResume_Click(object sender, RoutedEventArgs e) => ImportResume(newProfile: _profile == null);

    private void ImportResume(bool newProfile)
    {
        if (Store == null) return;
        var bytes = PickFile("Import résumé", out var name);
        if (bytes == null) return;
        var (doc, error) = ProfileService.Upload(name, bytes, DocumentKind.Resume);
        if (doc == null) { Warn(error ?? "The document could not be imported."); return; }
        var profile = newProfile ? new CandidateProfileRecord() : _profile!;
        var parsed = ProfileService.AddResume(profile, doc);
        Store.SaveProfile(profile);
        AppLog.Info($"Résumé imported: {parsed.Facts.Count} facts, {parsed.Warnings.Count} warnings, flags {doc.SecurityFlags.Count}");
        if (doc.SecurityFlags.Count > 0) Warn("Some text in this document looked like instructions to an AI. It was kept as plain data and will not change how the assistant behaves.");
        RefreshProfiles(profile.Id);
        ShowPage("profiles");
        RefreshChecklist();
    }

    private void ConfirmAll_Click(object sender, RoutedEventArgs e) { foreach (var f in _facts) f.Confirmed = true; }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || Store == null) return;
        _profile.Name = ProfileName.Text.Trim();
        _profile.PreferredName = ProfilePreferred.Text.Trim();
        _profile.Headline = ProfileHeadline.Text.Trim();
        foreach (var r in _facts)
            r.Fact.Status = r.Confirmed ? FactStatus.UserConfirmed : r.Initial == FactStatus.UserConfirmed ? FactStatus.Rejected : r.Initial;
        ProfileService.RebuildStories(_profile);
        Store.SaveProfile(_profile);
        foreach (var t in Store.Targets(_profile.Id).Where(t => t.Preparation == PreparationState.Prepared)) { t.Preparation = PreparationState.Stale; Store.SaveTarget(t); }
        RefreshProfiles(_profile.Id);
        RefreshChecklist();
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || Store == null) return;
        var dlg = new SaveFileDialog { FileName = "interview-assistant-profile.zip", Filter = "ZIP archive (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        using var fs = File.Create(dlg.FileName);
        Store.ExportProfile(_profile.Id, fs);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || Store == null) return;
        if (!Confirm($"Delete the profile “{_profile.Name}”, its interviews, prepared packs and documents from this computer?")) return;
        if (_vm.Settings.ActiveProfileId == _profile.Id) { _vm.Settings.ActiveProfileId = null; _vm.Settings.ActiveTargetId = null; _vm.Settings.Save(); }
        Store.DeleteProfile(_profile.Id);
        _profile = null;
        RefreshProfiles();
        RefreshChecklist();
    }

    // ================= interviews =================

    private void RefreshTargets(string? selectId = null)
    {
        InterviewsProfile.Text = _profile == null ? "Create a profile first." : "For " + (_profile.PreferredName.Length > 0 ? _profile.PreferredName : _profile.Name);
        var list = _profile == null || Store == null ? new List<InterviewTarget>() : Store.Targets(_profile.Id).ToList();
        TargetList.ItemsSource = list.Select(t => new Item<string>($"{t.DisplayName}  ·  {StateLabel(t.Preparation)}{(t.Id == _vm.Settings.ActiveTargetId ? "  ·  active" : "")}", t.Id)).ToList();
        var id = selectId ?? _target?.Id ?? _vm.Settings.ActiveTargetId;
        TargetList.SelectedItem = ((IEnumerable<Item<string>>)TargetList.ItemsSource).FirstOrDefault(i => i.Value == id);
        if (TargetList.SelectedItem == null) LoadTarget(null);
    }

    private static string StateLabel(PreparationState s) => s switch
    {
        PreparationState.Prepared => "prepared", PreparationState.Preparing => "preparing…", PreparationState.Stale => "needs re-prepare",
        PreparationState.Failed => "failed", _ => "not prepared",
    };

    private void TargetList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadTarget(TargetList.SelectedItem is Item<string> i ? Store?.GetTarget(i.Value) : null);

    private void LoadTarget(InterviewTarget? t)
    {
        _target = t;
        TargetDetail.IsEnabled = _profile != null;
        TargetTitle.Text = t?.JobTitle ?? "";
        TargetCompany.Text = t?.Company ?? "";
        TargetJd.Text = t?.JobDescriptionText ?? "";
        TargetLanguage.SelectedItem = ((IEnumerable<LangItem>)TargetLanguage.ItemsSource).FirstOrDefault(l => l.Code == (t?.ExpectedLanguage ?? "auto"));
        TargetAnswerLanguage.SelectedItem = ((IEnumerable<LangItem>)TargetAnswerLanguage.ItemsSource).FirstOrDefault(l => l.Code == (t?.AnswerLanguage ?? "same"));
        PrepProgress.Value = t?.Preparation == PreparationState.Prepared ? 1 : 0;
        PrepStatus.Text = t == null ? "" : StateLabel(t.Preparation) + (t.PreparationError != null ? " — " + t.PreparationError : "");
        var pack = t != null ? Store?.GetPack(t.Id) : null;
        PrepSummary.Text = pack == null ? "" : $"{pack.Questions.Count} prepared questions · {pack.Stories.Count} stories · {pack.Gaps.Count} gaps to handle honestly · prepared {pack.CreatedUtc.ToLocalTime():g}";
        UseBtn.IsEnabled = t?.Preparation is PreparationState.Prepared or PreparationState.Stale;
    }

    private void NewTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) { Warn("Create a profile first (import your résumé)."); return; }
        TargetList.SelectedItem = null;
        LoadTarget(null);
        TargetTitle.Focus();
    }

    private void ImportJd_Click(object sender, RoutedEventArgs e)
    {
        var bytes = PickFile("Import job description", out var name);
        if (bytes == null) return;
        var (doc, error) = ProfileService.Upload(name, bytes, DocumentKind.JobDescription);
        if (doc == null) { Warn(error ?? "The document could not be imported."); return; }
        TargetJd.Text = doc.Text;
    }

    private InterviewTarget? SaveTargetFields()
    {
        if (_profile == null || Store == null) return null;
        if (string.IsNullOrWhiteSpace(TargetTitle.Text)) { Warn("Enter the job title."); return null; }
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
        if (!_profile.ConfirmedFacts.Any() && !Confirm("No facts are confirmed in this profile yet, so answers will be generic (no personal claims). Continue?")) return;
        _prepCts?.Cancel();
        _prepCts = new CancellationTokenSource();
        PrepareBtn.IsEnabled = false;
        t.Preparation = PreparationState.Preparing; t.PreparationError = null; Store.SaveTarget(t);
        var progress = new Progress<PreparationProgress>(p => { PrepProgress.Value = (double)p.Stage / p.TotalStages; PrepStatus.Text = $"{p.Stage}/{p.TotalStages} · {p.StageName}: {p.Detail}"; });
        try
        {
            var profile = _profile;
            var pack = await Task.Run(() => new PreparationPipeline(_vm.Workspace.Assets).RunAsync(profile, t, null, progress, _prepCts.Token));
            Store.SavePack(pack);
            t.Preparation = PreparationState.Prepared; t.PreparedUtc = DateTime.UtcNow;
            AppLog.Info($"Prepared target: {pack.Questions.Count} questions");
        }
        catch (OperationCanceledException) { t.Preparation = PreparationState.NotPrepared; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            t.Preparation = PreparationState.Failed; t.PreparationError = ex.Message;
            AppLog.Error("Preparation failed", ex);
        }
        finally
        {
            Store.SaveTarget(t);
            PrepareBtn.IsEnabled = true;
            RefreshTargets(t.Id);
            RefreshChecklist();
        }
    }

    private void UseTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_target == null || _profile == null || _vm.Workspace == null) return;
        if (_vm.IsListening || _vm.IsPaused) { Warn("Stop the current session before switching interviews."); return; }
        _vm.Settings.ActiveProfileId = _profile.Id;
        _vm.Settings.ActiveTargetId = _target.Id;
        _vm.Settings.Save();
        var (kb, label) = _vm.Workspace.LoadActive(_profile.Id, _target.Id);
        _vm.UseKnowledge(kb, label);
        RefreshTargets(_target.Id);
        RefreshChecklist();
        _main.Activate();
    }

    private void DeleteTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_target == null || Store == null) return;
        if (!Confirm($"Delete “{_target.DisplayName}” and its prepared answers?")) return;
        if (_vm.Settings.ActiveTargetId == _target.Id) { _vm.Settings.ActiveTargetId = null; _vm.Settings.Save(); }
        Store.DeleteTarget(_target.Id);
        _target = null;
        RefreshTargets();
    }

    // ================= reports =================

    private void RefreshReports()
    {
        var sessions = Store?.Sessions() ?? Array.Empty<StoredSession>();
        ReportList.ItemsSource = sessions.Where(s => s.ReportJson.Length > 0)
            .Select(s => new Item<StoredSession>($"{s.StartedUtc.ToLocalTime():g}  ·  {LabelFor(s)}", s)).ToList();
    }

    private string LabelFor(StoredSession s) => Store?.GetTarget(s.TargetId)?.DisplayName ?? "Interview";

    private InterviewReport? SelectedReport(out StoredSession? session)
    {
        session = (ReportList.SelectedItem as Item<StoredSession>)?.Value;
        return session == null ? null : ReportSerializer.FromJson(session.ReportJson);
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedReport(out var s);
        if (r == null || s == null) return;
        var path = Path.Combine(AppPaths.Sessions, $"report-{s.Id[..8]}.html");
        File.WriteAllText(path, HtmlReportRenderer.Render(r, Branding.ProductName));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedReport(out _);
        if (r == null) return;
        var dlg = new SaveFileDialog { FileName = "interview-report.html", Filter = "HTML (*.html)|*.html" };
        if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, HtmlReportRenderer.Render(r, Branding.ProductName));
    }

    private void DeleteReport_Click(object sender, RoutedEventArgs e)
    {
        if ((ReportList.SelectedItem as Item<StoredSession>)?.Value is not { } s || Store == null) return;
        if (!Confirm("Delete this report?")) return;
        Store.DeleteSession(s.Id);
        RefreshReports();
    }

    // ================= account =================

    private void RefreshAccount()
    {
        var c = _vm.Cloud;
        AccountBadge.Text = _vm.AccountLabel;
        NotConfiguredPanel.Visibility = c.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        SignedOutPanel.Visibility = c.IsConfigured && !c.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = c.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        AccountError.Text = c.LastError ?? "";
        AccountStatus.Text = c.IsSignedIn ? _vm.AccountLabel : c.IsConfigured ? "Not signed in." : "";
        if (ProviderButtons.Children.Count == 0 && c.IsConfigured)
            foreach (var p in c.Options.OAuthProviders)
            {
                var b = new Button { Content = $"{L("sign_in")} · {ProviderName(p)}", Style = (Style)FindResource("Btn.Primary"), Margin = new Thickness(0, 0, 8, 8), Tag = p };
                b.Click += async (_, _) => await SignInAsync(p, null);
                ProviderButtons.Children.Add(b);
            }
        if (c.Account is { } a)
        {
            var e = a.Entitlements;
            PlanText.Text = $"{e.PlanName} — {a.Subscription?.Status ?? (e.PlanCode == "trial" ? "trial" : e.Status)}";
            var renew = a.Subscription switch
            {
                { CancelAtPeriodEnd: true, CurrentPeriodEndUtc: { } end } => $" · ends {end.ToLocalTime():d}",
                { GraceUntilUtc: { } g } => $" · payment issue — access until {g.ToLocalTime():d}",
                { CurrentPeriodEndUtc: { } end } => $" · renews {end.ToLocalTime():d}",
                _ => "",
            };
            UsageText.Text = $"{a.Usage.LiveSecondsUsed / 60} min used · {a.Usage.LiveSecondsRemaining / 60} min left · {a.Usage.PrepJobsRemaining} preparations left this month{renew}";
        }
    }

    private static string ProviderName(string p) => p switch { "google" => "Google", "azure" => "Microsoft", "github" => "GitHub", "apple" => "Apple", _ => p };

    private async Task SignInAsync(string? provider, string? email)
    {
        AccountError.Text = "";
        AccountStatus.Text = email != null ? "Check your e-mail and open the sign-in link on this computer…" : "Continue in your browser…";
        var ok = await _vm.Cloud.SignInAsync(provider, email, CancellationToken.None);
        if (ok)
        {
            _vm.Settings.Mode = "cloud"; _vm.Settings.Save();
            if (!_vm.IsListening && !_vm.IsPaused) _vm.BuildEngine();
            await LoadAccountExtrasAsync();
        }
        RefreshAccount();
        RefreshChecklist();
    }

    private async void MagicLink_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) { AccountError.Text = "Enter a valid e-mail address."; return; }
        await SignInAsync(null, email);
    }

    private async Task LoadAccountExtrasAsync()
    {
        var api = _vm.Cloud.Api;
        if (api == null) return;
        try
        {
            var plans = await api.PlansAsync(CancellationToken.None);
            PlanButtons.Children.Clear();
            foreach (var p in plans.Where(p => p.Purchasable && p.Code != _vm.Cloud.Account?.Entitlements.PlanCode))
            {
                var b = new Button { Content = $"{L("upgrade")}: {p.Name} ({p.Entitlements.LiveMinutesPerPeriod} min)", Style = (Style)FindResource("Btn.Primary"), Margin = new Thickness(0, 0, 8, 8) };
                b.Click += async (_, _) => await Guard(() => _vm.Cloud.OpenCheckoutAsync(p.Code, CancellationToken.None));
                PlanButtons.Children.Add(b);
            }
            var devices = await api.DevicesAsync(CancellationToken.None);
            DeviceList.ItemsSource = devices.Where(d => !d.Revoked)
                .Select(d => new Item<DeviceDto>($"{d.Name} · {d.Platform} · v{d.ClientVersion} · last seen {d.LastSeenUtc.ToLocalTime():g}{(d.IsCurrent ? " · this device" : "")}", d)).ToList();
        }
        catch (BackendException ex) { AccountError.Text = ex.Message; }
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); AccountError.Text = ""; }
        catch (BackendException ex) { AccountError.Text = ex.Message + (ex.CorrelationId != null ? $" (ref {ex.CorrelationId[..8]})" : ""); }
    }

    private async void Portal_Click(object sender, RoutedEventArgs e) => await Guard(() => _vm.Cloud.OpenPortalAsync(CancellationToken.None));
    private async void RefreshAccount_Click(object sender, RoutedEventArgs e) { await _vm.Cloud.RefreshAsync(CancellationToken.None); await LoadAccountExtrasAsync(); RefreshAccount(); }

    private async void RevokeDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((DeviceList.SelectedItem as Item<DeviceDto>)?.Value is not { } d || _vm.Cloud.Api == null) return;
        if (!Confirm($"Remove “{d.Name}” from your account? Any live session on it ends.")) return;
        await Guard(() => _vm.Cloud.Api.RevokeDeviceAsync(d.Id, CancellationToken.None));
        await LoadAccountExtrasAsync();
    }

    private async void ExportAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Cloud.Api == null) return;
        var dlg = new SaveFileDialog { FileName = "interview-assistant-account-export.json", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        await Guard(async () =>
        {
            var json = await _vm.Cloud.Api.ExportAsync(CancellationToken.None);
            await File.WriteAllTextAsync(dlg.FileName, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
        });
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Cloud.Api == null) return;
        if (!Confirm("Permanently delete your account and all data stored on the service? Cancel an active subscription in “Manage billing” first. Local data on this computer is not affected.")) return;
        await Guard(async () => { await _vm.Cloud.Api.DeleteAccountAsync(CancellationToken.None); await _vm.Cloud.SignOutAsync(); });
        RefreshAccount();
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e) { await _vm.Cloud.SignOutAsync(); RefreshAccount(); RefreshChecklist(); }

    // ================= helpers =================

    private void Warn(string message) => MessageBox.Show(this, message, Branding.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
    private bool Confirm(string message) => MessageBox.Show(this, message, Branding.ProductName, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
