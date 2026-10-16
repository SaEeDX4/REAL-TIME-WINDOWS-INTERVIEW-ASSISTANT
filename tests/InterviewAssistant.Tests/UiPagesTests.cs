using System.Text.RegularExpressions;
using InterviewAssistant.App.UiState;
using InterviewAssistant.Core.Languages;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// Desktop UI pass 2 (2.1.0-rc.4): Profiles / Interviews / Reports page composition, the Live window's history counter,
/// manual-question bar and profile-gap card, and the debounced connection notifications. WPF cannot run here, so the
/// pages are checked at source level and the logic (UiState) is unit-tested; the Windows self-test drives the real UI.
/// </summary>
public class UiPagesTests
{
    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "InterviewAssistant.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
        }
    }

    private static string App(string relative) => File.ReadAllText(Path.Combine(RepoRoot, "src", "InterviewAssistant.App", relative));

    /// <summary>The opening tag of the element with this x:Name.</summary>
    private static string Element(string xaml, string name)
    {
        var m = Regex.Match(xaml, $@"<[\w:.]+\s[^>]*x:Name=""{Regex.Escape(name)}""[^>]*>", RegexOptions.Singleline);
        Assert.True(m.Success, $"element {name} not found");
        return m.Value;
    }

    /// <summary>Body of a C# method (from its signature to the next member at the same indentation).</summary>
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = Regex.Match(source[(start + signature.Length)..], @"\n    (private|public|internal|protected) ");
        return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
    }

    // ---------------- Reports ----------------

    [Fact]
    public void ReportsPage_HasTwoResponsivePanes_FourActions_EmptyAndSelectStates()
    {
        var xaml = App("HomeWindow.xaml");
        var page = xaml[xaml.IndexOf("x:Name=\"PageReports\"", StringComparison.Ordinal)..xaml.IndexOf("<!-- ===== Help & about", StringComparison.Ordinal)];
        // list ≈ 32 % (min width token), preview ≈ 68 %: star columns, mirrored automatically in RTL
        Assert.Matches(@"ColumnDefinition Width=""32\*"" MinWidth=""\{StaticResource S\.PaneMinWidth\}""", page);
        Assert.Matches(@"ColumnDefinition Width=""68\*""", page);
        // the four actions, as a toolbar above the preview: Open / Copy / Save secondary, Delete destructive and set apart
        foreach (var (name, key) in new[] { ("OpenReportBtn", "open"), ("CopyReportBtn", "copy_as_text"), ("ExportReportBtn", "save_as_html"), ("DeleteReportBtn", "delete") })
        {
            var el = Element(page, name);
            Assert.Contains($"{{v:Loc {key}}}", el);
            Assert.Contains("ToolTip=\"{v:Loc", el);
        }
        foreach (var secondary in new[] { "OpenReportBtn", "CopyReportBtn", "ExportReportBtn" }) Assert.Contains("Btn.Form}", Element(page, secondary));
        Assert.Contains("Btn.FormDanger", Element(page, "DeleteReportBtn"));
        Assert.Contains("DockPanel.Dock=\"Right\"", Element(page, "DeleteReportBtn"));
        var toolbar = page.IndexOf("x:Name=\"ReportToolbar\"", StringComparison.Ordinal);
        Assert.True(toolbar > 0 && toolbar < page.IndexOf("x:Name=\"ReportPreview\"", StringComparison.Ordinal), "toolbar sits above the preview");
        // empty state (icon + message + hint) and the "Select a report" state
        var empty = page[page.IndexOf("x:Name=\"ReportsEmptyState\"", StringComparison.Ordinal)..];
        Assert.Contains("EmptyState.Icon", empty);
        Assert.Contains("{v:Loc reports_empty_title}", empty);
        Assert.Contains("{v:Loc reports_empty_hint}", empty);
        Assert.Equal("Reports are created when you stop an interview.", Loc.TableFor("en")["reports_empty_hint"]);
        Assert.Contains("{v:Loc reports_select_title}", page);
        // list rows: date/time, interview, question count
        Assert.Contains("{Binding When}", page);
        Assert.Contains("{Binding Title}", page);
        Assert.Contains("{Binding Questions}", page);
        Assert.Contains("KeyDown=\"ReportList_KeyDown\"", Element(page, "ReportList"));
    }

    [Fact]
    public void ReportsPage_Keyboard_EnterOpens_DeleteAsksDestructively()
    {
        var cs = App("HomeWindow.xaml.cs");
        var keys = Method(cs, "private void ReportList_KeyDown(");
        Assert.Contains("Key.Enter", keys);
        Assert.Contains("OpenReport_Click", keys);
        Assert.Contains("Key.Delete", keys);
        Assert.Contains("DeleteReport_Click", keys);
        var delete = Method(cs, "private async void DeleteReport_Click(");
        var confirm = delete.IndexOf("AppDialog.ConfirmAsync(", StringComparison.Ordinal);
        Assert.True(confirm > 0 && delete.IndexOf("destructive: true", confirm, StringComparison.Ordinal) > 0);
        Assert.True(confirm < delete.IndexOf("Store.DeleteSession", StringComparison.Ordinal), "confirmed before deleting");
        // newest first, and the report saved by Stop is selected
        Assert.Contains("OrderByDescending(s => s.EndedUtc ?? s.StartedUtc)", Method(cs, "private void RefreshReports("));
        Assert.Contains("RefreshReports(id)", cs);
    }

    // ---------------- Profiles / Interviews ----------------

    [Fact]
    public void ProfileDelete_IsDestructive_SetApart_AndConfirmedWithCancelAsDefault()
    {
        var xaml = App("HomeWindow.xaml");
        var actions = xaml[xaml.IndexOf("x:Name=\"ProfileActions\"", StringComparison.Ordinal)..xaml.IndexOf("x:Name=\"ProfileDetail\"", StringComparison.Ordinal)];
        Assert.Contains("Btn.FormPrimary", Element(actions, "ImportResumeBtn"));
        Assert.Contains("{v:Loc import_resume}", Element(actions, "ImportResumeBtn"));
        foreach (var n in new[] { "AddDocumentBtn", "NewEmptyProfileBtn", "ExportProfileBtn" }) Assert.Contains("Btn.Form}", Element(actions, n));
        var delete = Element(actions, "DeleteProfileBtn");
        Assert.Contains("Btn.FormDanger", delete);
        Assert.Contains("{v:Loc delete_profile}", delete);
        Assert.Contains("Click=\"DeleteProfile_Click\"", delete);
        // order: primary, secondary group, divider, destructive last
        int At(string s) => actions.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("ImportResumeBtn") < At("AddDocumentBtn") && At("ExportProfileBtn") < At("Style=\"{StaticResource Divider}\"") && At("Style=\"{StaticResource Divider}\"") < At("DeleteProfileBtn"));
        // existing handlers kept
        foreach (var h in new[] { "NewProfile_Click", "AddResume_Click", "NewEmptyProfile_Click", "ExportProfile_Click" }) Assert.Contains($"Click=\"{h}\"", actions);

        var cs = Method(App("HomeWindow.xaml.cs"), "private async void DeleteProfile_Click(");
        var confirm = cs.IndexOf("AppDialog.ConfirmAsync(", StringComparison.Ordinal);
        Assert.True(confirm > 0, "asks in the in-app dialog");
        Assert.True(cs.IndexOf("destructive: true", confirm, StringComparison.Ordinal) > 0, "destructive confirmation");
        Assert.True(confirm < cs.IndexOf("Store.DeleteProfile", StringComparison.Ordinal), "confirmed before deleting");
        // the in-app destructive confirmation makes Cancel the default button
        var dialog = App(Path.Combine("Views", "AppDialog.cs"));
        Assert.Contains("IsCancel = true, IsDefault = destructive", dialog);
        Assert.DoesNotContain("MessageBox.Show", string.Join("\n", Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "InterviewAssistant.App"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).Select(File.ReadAllText)));
    }

    [Fact]
    public void ProfileSections_AreASegmentedControl_WithAutomationNames_AndKeyboard()
    {
        var xaml = App("HomeWindow.xaml");
        var tabs = Element(xaml, "ProfileTabs");
        Assert.StartsWith("<UniformGrid", tabs);   // equal segments across the content width
        Assert.Contains("Rows=\"1\"", tabs);
        Assert.Contains("PreviewKeyDown=\"ProfileTabs_PreviewKeyDown\"", tabs);
        Assert.Contains("AutomationProperties.Name=\"{v:Loc profile_sections}\"", tabs);
        foreach (var (name, key) in new[] { ("TabFacts", "profile_tab_facts"), ("TabPersonal", "profile_tab_personal") })
        {
            var el = Element(xaml, name);
            Assert.StartsWith("<RadioButton", el);
            Assert.Contains("Style=\"{StaticResource Seg.Item}\"", el);
            Assert.Contains($"AutomationProperties.Name=\"{{v:Loc {key}}}\"", el);
            Assert.Contains("GroupName=\"ProfileSection\"", el);
        }
        var theme = App(Path.Combine("Themes", "Theme.xaml"));
        var seg = theme[theme.IndexOf("x:Key=\"Seg.Item\"", StringComparison.Ordinal)..];
        seg = seg[..seg.IndexOf("</Style>", StringComparison.Ordinal)];
        Assert.Matches(@"Property=""IsChecked"" Value=""True"">\s*<Setter TargetName=""Bd"" Property=""Background"" Value=""\{StaticResource B\.AccentStrong\}""", seg);
        Assert.Contains("IsKeyboardFocused", seg);
        var cs = App("HomeWindow.xaml.cs");
        Assert.Contains("SegmentedNavigation.Move(", Method(cs, "private void ProfileTabs_PreviewKeyDown("));
        Assert.Contains("IsTabStop", Method(cs, "private void SetProfileTab("));
    }

    [Theory]
    [InlineData(0, false, false, 1)]   // LTR: Right → next
    [InlineData(1, true, false, 0)]    // LTR: Left → previous
    [InlineData(0, true, true, 1)]     // RTL: segments mirrored, Left → next
    [InlineData(1, false, true, 0)]    // RTL: Right → previous
    [InlineData(1, false, false, 1)]   // stops at the end
    [InlineData(0, true, false, 0)]    // stops at the start
    public void SegmentedNavigation_FollowsTheVisualDirection(int current, bool left, bool rtl, int expected) =>
        Assert.Equal(expected, SegmentedNavigation.Move(current, 2, left, rtl));

    [Fact]
    public void ProfileAndInterviewForms_UseTheSameGridTokens()
    {
        var xaml = App("HomeWindow.xaml");
        foreach (var (page, next) in new[] { ("PageProfiles", "<!-- ===== Interviews"), ("PageInterviews", "<!-- ===== Reports") })
        {
            var body = xaml[xaml.IndexOf($"x:Name=\"{page}\"", StringComparison.Ordinal)..xaml.IndexOf(next, StringComparison.Ordinal)];
            Assert.Contains("Width=\"{StaticResource S.SidePaneWidth}\"", body);
            Assert.Contains("Width=\"{StaticResource S.PaneGap}\"", body);
            Assert.Contains("Width=\"{StaticResource S.ColumnGap}\"", body);
            Assert.Contains("Style=\"{StaticResource T.FieldLabel}\"", body);
            Assert.DoesNotContain("Btn.Base", body);   // only the form button family (one height) on these pages
        }
        foreach (var name in new[] { "ProfileHeaderGrid", "TargetFieldGrid" }) Element(xaml, name);
        Assert.Contains("Btn.FormDanger", Element(xaml, "DeleteTargetBtn"));
        Assert.Contains("{v:Loc delete_interview}", Element(xaml, "DeleteTargetBtn"));
    }

    // ---------------- Live window ----------------

    [Fact]
    public void HistoryCounter_NeverShowsZeroOfN()
    {
        var before = Loc.Language;
        try
        {
            Loc.Language = "en";
            for (int count = 0; count <= 12; count++)
                for (int position = -3; position <= 14; position++)
                {
                    var text = HistoryCounter.Text(position, count);
                    Assert.DoesNotMatch(@"^0\s*/", text);
                    Assert.DoesNotContain("0 / 0", text);
                    if (HistoryCounter.Numbers(position, count) is { } n)
                    {
                        Assert.InRange(n.Shown, 1, count);
                        Assert.Equal(count, n.Total);
                        Assert.Equal($"{n.Shown} / {n.Total}", text);
                    }
                    else Assert.Equal("", text);
                }
            Assert.Equal("", HistoryCounter.Text(-1, 1));   // the reported "0 / 1": nothing on screen yet → hidden
            Assert.Equal("", HistoryCounter.Text(-1, 0));
            Assert.Equal("1 / 1", HistoryCounter.Text(0, 1));
            Assert.Equal("", HistoryCounter.Text(3, 2));    // stale position after an engine rebuild
        }
        finally { Loc.Language = before; }
    }

    [Fact]
    public void LiveWindow_CounterHiddenWhenEmpty_HistoryResetOnRebuildAndSignOut()
    {
        var vm = App(Path.Combine("ViewModels", "MainViewModel.cs"));
        Assert.Contains("public string HistoryLabel => HistoryCounter.Text(HistoryPosition, _engine?.History.Count ?? 0);", vm);
        var build = Method(vm, "public void BuildEngine(");
        Assert.Contains("HistoryPosition = -1", build);
        Assert.Contains("Raise(nameof(HistoryLabel))", build);
        Assert.Contains("if (h.Count == 0) return;", Method(vm, "private void Navigate(int delta)"));
        // signed out after an interview that kept running: the history leaves the screen once Stop saved the report
        Assert.Contains("if (_workspaceOwner != null && CurrentOwner == null) OpenWorkspaceForCurrentAccount();", Method(vm, "public async Task StopAsync("));
        var xaml = App("MainWindow.xaml");
        var counter = Element(xaml, "HistoryCounterText");
        Assert.Contains("Visibility=\"{Binding HistoryLabel, Converter={StaticResource StrVis}}\"", counter);
    }

    [Fact]
    public void ManualQuestionBar_FieldAndSendButton_ShareTheHeightTokenOnOneRow()
    {
        var xaml = App("MainWindow.xaml");
        var bar = xaml[xaml.IndexOf("x:Name=\"ManualBar\"", StringComparison.Ordinal)..];
        bar = bar[..bar.IndexOf("</Grid>", StringComparison.Ordinal)];
        Assert.Contains("<ColumnDefinition Width=\"{StaticResource S.ColumnGap}\"/>", bar);
        var input = Element(bar, "ManualInput");
        var send = Element(bar, "ManualSendBtn");
        Assert.Contains("Height=\"{StaticResource S.ControlHeight}\"", input);
        Assert.Contains("Height=\"{StaticResource S.ControlHeight}\"", send);
        Assert.Contains("VerticalAlignment=\"Center\"", input);
        Assert.Contains("VerticalAlignment=\"Center\"", send);
        Assert.Contains("Grid.Column=\"2\"", send);
        Assert.Contains("AutomationProperties.Name=\"{v:Loc tt_send}\"", send);
        Assert.DoesNotContain("HorizontalAlignment=\"Right\"", send);   // no longer overlaid on the field
    }

    [Fact]
    public void ProfileGapGuidance_IsADistinctCard_NeverTheCandidatesAnswer()
    {
        var vm = App(Path.Combine("ViewModels", "MainViewModel.cs"));
        Assert.Contains("IsProfileGap = a.Source == AnswerSource.Guidance;", vm);
        Assert.Contains("IsCoachView = a.Presentation == Presentation.Coach && !IsProfileGap;", vm);
        Assert.Contains("IsProfileGap ? L(\"live_gap_header\")", vm);   // the header no longer says "Answer"
        var xaml = App("MainWindow.xaml");
        var card = xaml[xaml.IndexOf("x:Name=\"AnswerCard\"", StringComparison.Ordinal)..];
        card = card[..card.IndexOf("</Border>\n", card.IndexOf("x:Name=\"AddToProfileBtn\"", StringComparison.Ordinal), StringComparison.Ordinal)];
        Assert.Contains("Binding IsProfileGap", card);
        Assert.Contains("{StaticResource B.Thinking}", card);         // amber/info accent, not the error colour
        Assert.DoesNotContain("B.Error", card);
        Assert.Contains("{v:Loc live_gap_title}", card);
        Assert.Contains("{v:Loc live_gap_subtitle}", card);
        Assert.Contains("x:Name=\"BulletList\"", card);               // the guidance bullets are inside the card
        Assert.Contains("Click=\"AddToProfile_Click\"", Element(card, "AddToProfileBtn"));
        Assert.Contains("_home?.ShowInterviewProfile()", Method(App("MainWindow.xaml.cs"), "private void AddToProfile_Click("));
        Assert.Contains("SetProfileTab(\"personal\")", Method(App("HomeWindow.xaml.cs"), "public async void ShowInterviewProfile("));
    }

    public static IEnumerable<object[]> Languages => LanguageRegistry.All.Select(l => new object[] { l.Code });

    [Theory]
    [MemberData(nameof(Languages))]
    public void ProfileGapCard_ReportsStates_AndContextualErrors_AreTranslated(string lang)
    {
        var keys = new[]
        {
            "live_gap_header", "live_gap_title", "live_gap_subtitle", "live_gap_add", "tt_live_gap_add",
            "reports_empty_title", "reports_empty_hint", "reports_select_title", "reports_select_hint", "report_row_questions",
            "profile_sections", "delete_profile", "delete_interview", "toast_connection_lost_reconnecting",
            "err_profile_save", "err_profile_export", "err_profile_delete", "err_file_read", "err_interview_save", "err_interview_delete",
            "err_report_open", "err_report_copy", "err_report_save", "err_report_delete",
        };
        var en = Loc.TableFor("en");
        var t = Loc.TableFor(lang);
        foreach (var k in keys)
        {
            Assert.True(t.TryGetValue(k, out var v) && v.Trim().Length > 0, $"{lang}: {k} missing");
            if (lang != "en") Assert.NotEqual(en[k], v);
        }
        Assert.Equal("Not in your profile yet", en["live_gap_title"]);
        Assert.Equal("Speak from your own experience — here is a structure:", en["live_gap_subtitle"]);
        Assert.Equal("Connection lost — reconnecting…", en["toast_connection_lost_reconnecting"]);
        Assert.StartsWith("Couldn't save the profile.", en["err_profile_save"]);
        Assert.DoesNotContain("résumé", string.Join(" ", keys.Select(k => en[k])));
    }

    // ---------------- connection notifications ----------------

    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    /// <summary>Polls every 500 ms for <paramref name="seconds"/> and collects what would be shown.</summary>
    private static List<ConnectionToast> Run(ConnectionToastGate gate, FakeClock clock, double seconds)
    {
        var shown = new List<ConnectionToast>();
        for (double t = 0; t < seconds; t += 0.5)
        {
            clock.Advance(0.5);
            if (gate.Poll() is var r and not ConnectionToast.None) shown.Add(r);
        }
        return shown;
    }

    [Fact]
    public void ConnectionToasts_ShortBlip_ShowsNothing()
    {
        var clock = new FakeClock();
        var gate = new ConnectionToastGate(() => clock.Now);
        Assert.Equal(ConnectionToast.None, gate.Report(false));
        Assert.Empty(Run(gate, clock, 2.5));
        Assert.Equal(ConnectionToast.None, gate.Report(true));   // no "restored" without a shown "lost"
        Assert.Empty(Run(gate, clock, 20));
    }

    [Fact]
    public void ConnectionToasts_RealOutage_LostAfterGrace_ThenRestored()
    {
        var clock = new FakeClock();
        var gate = new ConnectionToastGate(() => clock.Now);
        gate.Report(false);
        Assert.Empty(Run(gate, clock, 2.5));
        Assert.Equal(new[] { ConnectionToast.Lost }, Run(gate, clock, 10));   // exactly once, however long it lasts
        Assert.False(gate.NeedsPolling);
        Assert.Equal(ConnectionToast.Restored, gate.Report(true));
        Assert.Equal(ConnectionToast.None, gate.Report(true));            // re-confirmations are silent
    }

    [Fact]
    public void ConnectionToasts_AtMostOnePairPerTenSeconds()
    {
        var clock = new FakeClock();
        var gate = new ConnectionToastGate(() => clock.Now);
        gate.Report(false);
        Assert.Equal(new[] { ConnectionToast.Lost }, Run(gate, clock, 3.5));   // lost shown at ~3 s
        clock.Advance(1);
        Assert.Equal(ConnectionToast.Restored, gate.Report(true));
        // flapping again right away: the next "lost" waits until 10 s after the previous one…
        clock.Advance(0.5);
        gate.Report(false);
        var during = Run(gate, clock, 4);    // down 4 s, but only ~9 s since the last "lost"
        Assert.Empty(during);
        // …and an outage that ends inside that window is never announced (nor its recovery)
        Assert.Equal(ConnectionToast.None, gate.Report(true));
        // a later, real outage is announced again
        clock.Advance(10);
        gate.Report(false);
        Assert.Equal(new[] { ConnectionToast.Lost }, Run(gate, clock, 4));
        Assert.Equal(ConnectionToast.Restored, gate.Report(true));
    }

    [Fact]
    public void ConnectionToasts_DelayedLost_WhenTheOutageOutlastsTheRateLimit()
    {
        var clock = new FakeClock();
        var gate = new ConnectionToastGate(() => clock.Now);
        gate.Report(false);
        Run(gate, clock, 3.5);
        gate.Report(true);
        gate.Report(false);
        var shown = Run(gate, clock, 12);
        Assert.Equal(new[] { ConnectionToast.Lost }, shown);   // shown once the 10 s interval has passed
    }

    [Fact]
    public void ConnectionToasts_SilentReset_NeverAnnouncesARecoveryThatDidNotHappen()
    {
        var clock = new FakeClock();
        var gate = new ConnectionToastGate(() => clock.Now);
        gate.Report(false);
        Assert.Equal(new[] { ConnectionToast.Lost }, Run(gate, clock, 4));
        Assert.Equal(ConnectionToast.None, gate.Report(true, silent: true));   // interview stopped / signed out
        Assert.True(gate.IsConnected);
        Assert.Empty(Run(gate, clock, 15));
    }

    [Fact]
    public void LiveWindow_UsesTheGateForEverySource()
    {
        var vm = App(Path.Combine("ViewModels", "MainViewModel.cs"));
        Assert.Contains("ConnectionToastGate _connectionGate = new(", vm);
        Assert.Contains("_relayUp", Method(vm, "private void ApplyStatus("));
        Assert.Contains("_networkUp = e.IsAvailable", vm);
        Assert.Contains("PollConnection()", vm);
        // the lost/restored toasts are only raised from the gate's decisions
        Assert.Single(Regex.Matches(vm, @"toast_connection_lost_reconnecting"));
        Assert.Single(Regex.Matches(vm, @"""toast_connection_restored"""));
        Assert.Contains("ConnectionToast.Restored", Method(vm, "private void ShowConnectionToast("));
    }
}
