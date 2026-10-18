using System.Text.RegularExpressions;
using InterviewAssistant.App.UiState;
using InterviewAssistant.Core.Languages;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC5 readiness UI: the pre-interview check is a Home page (no separate window) that writes the one readiness store;
/// Get started, the Interviews card and the title-bar indicator render it; every label the model refers to exists in
/// every language. WPF cannot run here: wiring is checked at source level, the logic is unit-tested, and the Windows
/// self-test drives the real page.
/// </summary>
public class Rc5ReadinessPageTests
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

    private static string AppDir => Path.Combine(RepoRoot, "src", "InterviewAssistant.App");
    private static string App(string relative) => File.ReadAllText(Path.Combine(AppDir, relative)).Replace("\r\n", "\n");
    private static string Between(string s, string from, string to)
    {
        var a = s.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, from);
        var b = s.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b > a, to);
        return s[a..b];
    }
    private static int PrimaryButtons(string xaml) => Regex.Matches(xaml, @"<Button\b[^>]*Style=""\{StaticResource Btn\.(Primary|FormPrimary)\}""").Count;

    private static IEnumerable<string> Languages => LanguageRegistry.All.Select(l => l.Code);

    // ---------------- keys ----------------

    [Fact]
    public void EveryStateAndActionKey_ExistsInEnglishAndEveryLanguage()
    {
        var keys = Enum.GetValues<ReadinessState>().Select(ReadinessSnapshot.StateKey)
            .Concat(Enum.GetValues<SetupAction>().Select(SetupPlanner.ActionKey)).Distinct().ToList();
        Assert.Equal(12, keys.Count);
        foreach (var k in keys) Assert.True(Loc.Has(k), $"en.json lacks {k}");
        foreach (var lang in Languages)
        {
            var table = Loc.TableFor(lang);
            foreach (var k in keys) Assert.True(table.TryGetValue(k, out var v) && v.Trim().Length > 0, $"{lang}: {k}");
        }
    }

    [Fact]
    public void EveryPresentationKey_ExistsInEveryLanguage()
    {
        var keys = new List<string>();
        foreach (var o in Enum.GetValues<CheckOutcome>().Cast<CheckOutcome?>().Append(null)) keys.Add(ReadinessPresentation.StatusKey(o));
        foreach (var id in Enum.GetValues<ReadinessCheckId>())
            foreach (var cloud in new[] { true, false }) { keys.Add(ReadinessPresentation.TitleKey(id, cloud)); keys.Add(ReadinessPresentation.FixHintKey(id, cloud)); }
        foreach (var f in Enum.GetValues<ReadinessFix>()) if (ReadinessPresentation.FixLabelKey(f) is { } k) keys.Add(k);
        foreach (var s in Enum.GetValues<ReadinessState>()) keys.Add(ReadinessPresentation.HintKey(s));
        foreach (var lang in Languages)
        {
            var table = Loc.TableFor(lang);
            foreach (var k in keys.Distinct()) Assert.True(table.ContainsKey(k), $"{lang}: {k}");
        }
    }

    // ---------------- presentation logic ----------------

    [Fact]
    public void EveryFix_HasALabel_AndALegalRouteOrRunsOnThePage()
    {
        foreach (var f in Enum.GetValues<ReadinessFix>().Where(f => f != ReadinessFix.None))
        {
            Assert.NotNull(ReadinessPresentation.FixLabelKey(f));
            var route = ReadinessPresentation.FixRoute(f);
            if (f is ReadinessFix.TestAudio or ReadinessFix.TestTranscription or ReadinessFix.TestAi) Assert.Null(route);   // test tools run on the page
            else Assert.Equal(route!.Split('/')[0], HomeRoute.Parse(route).Page);   // never falls back to Get started
        }
        Assert.Equal("settings/audio", ReadinessPresentation.FixRoute(ReadinessFix.OpenAudioSettings));
        Assert.Equal("settings/window", ReadinessPresentation.FixRoute(ReadinessFix.OpenWindowSettings));
        Assert.Equal("account", ReadinessPresentation.FixRoute(ReadinessFix.SignIn));
        Assert.Null(ReadinessPresentation.FixLabelKey(ReadinessFix.None));
    }

    [Theory]
    [InlineData(ReadinessState.Ready, true, false)]
    [InlineData(ReadinessState.ReadyWithWarnings, true, false)]
    [InlineData(ReadinessState.NotReady, false, false)]
    [InlineData(ReadinessState.Checking, false, false)]
    [InlineData(ReadinessState.Outdated, false, true)]
    [InlineData(ReadinessState.NotChecked, false, true)]
    public void StartAndAutoRun_FollowTheState(ReadinessState s, bool canStart, bool autoRun)
    {
        Assert.Equal(canStart, ReadinessPresentation.CanStart(s));
        Assert.Equal(autoRun, ReadinessPresentation.ShouldAutoRun(s));
    }

    [Fact]
    public void OutcomeBadges_ShareTheStateColours_AndAlwaysHaveAnIcon()
    {
        Assert.Equal("B.Listening", ReadinessSnapshot.BrushKey(ReadinessPresentation.StateOf(CheckOutcome.Pass)));
        Assert.Equal("B.Thinking", ReadinessSnapshot.BrushKey(ReadinessPresentation.StateOf(CheckOutcome.Warning)));
        Assert.Equal("B.Error", ReadinessSnapshot.BrushKey(ReadinessPresentation.StateOf(CheckOutcome.Fail)));
        Assert.Equal(ReadinessState.NotChecked, ReadinessPresentation.StateOf(null));
        var icons = Enum.GetValues<ReadinessState>().Select(ReadinessPresentation.Icon).ToList();
        Assert.All(icons, i => Assert.False(string.IsNullOrEmpty(i)));
        Assert.Equal(icons.Count, icons.Distinct().Count());   // the icon tells the states apart without colour
    }

    private static ReadinessStore Run(string ctx, params (ReadinessCheckId Id, CheckOutcome Outcome)[] results)
    {
        var store = new ReadinessStore();
        store.Begin(ctx);
        foreach (var id in Enum.GetValues<ReadinessCheckId>())
            store.Report(new ReadinessCheckResult(id, results.Any(r => r.Id == id) ? results.First(r => r.Id == id).Outcome : CheckOutcome.Pass));
        return store;
    }

    [Fact]
    public void SummaryNumbers_BelongToWhatIsSelectedNow()
    {
        var s = Run("a|t|d", (ReadinessCheckId.WindowOnTop, CheckOutcome.Warning)).Snapshot;
        Assert.Equal(7, ReadinessPresentation.ShownPassed(s, "a|t|d"));
        Assert.Equal(0, ReadinessPresentation.ShownPassed(s, "a|other|d"));   // out of date: never another interview's numbers
        Assert.Equal(0, ReadinessPresentation.ShownPassed(ReadinessSnapshot.Empty, "a|t|d"));
    }

    [Fact]
    public void Blockers_AreTheFailedChecks_AndTypedQuestionsStillWorkWhenOnlyConnectionChecksFail()
    {
        var s = Run("c", (ReadinessCheckId.AccountPlan, CheckOutcome.Fail), (ReadinessCheckId.Transcription, CheckOutcome.Fail), (ReadinessCheckId.WindowOnTop, CheckOutcome.Warning)).Snapshot;
        Assert.Equal(new[] { ReadinessCheckId.AccountPlan, ReadinessCheckId.Transcription }, ReadinessPresentation.Blockers(s).Select(c => c.Id));
        Assert.Equal(new[] { ReadinessCheckId.WindowOnTop }, ReadinessPresentation.Warnings(s).Select(c => c.Id));
        Assert.True(ReadinessPresentation.TypedQuestionsStillWork(s));
        var noPack = Run("c", (ReadinessCheckId.PreparedAnswers, CheckOutcome.Fail)).Snapshot;
        Assert.False(ReadinessPresentation.TypedQuestionsStillWork(noPack));
    }

    [Fact]
    public void TransitionLog_StatesAndCountsOnly()
    {
        var s = Run("user@example.com|target|Headset", (ReadinessCheckId.AudioSignal, CheckOutcome.Warning)).Snapshot;
        var line = ReadinessPresentation.TransitionLog(ReadinessState.Checking, ReadinessState.ReadyWithWarnings, s);
        Assert.Equal("Readiness: Checking -> ReadyWithWarnings (7/8 passed)", line);
        Assert.DoesNotContain("example.com", line);   // no personal data
        Assert.Null(ReadinessPresentation.TransitionLog(ReadinessState.Ready, ReadinessState.Ready, s));
    }

    // ---------------- wiring (source level) ----------------

    [Fact]
    public void PreInterviewCheck_IsAHomePage_AndTheSeparateWindowIsGone()
    {
        Assert.True(File.Exists(Path.Combine(AppDir, "Views", "PreInterviewPage.xaml")));
        Assert.True(File.Exists(Path.Combine(AppDir, "Views", "PreInterviewPage.xaml.cs")));
        Assert.False(File.Exists(Path.Combine(AppDir, "ReadinessWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(AppDir, "ReadinessWindow.xaml.cs")));
        var sources = Directory.EnumerateFiles(AppDir, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        Assert.DoesNotContain(sources, f => File.ReadAllText(f).Contains("ReadinessWindow"));

        var xaml = App("HomeWindow.xaml");
        Assert.Contains("<v:PreInterviewPage x:Name=\"PagePreInterview\"", xaml);
        // sidebar: Interviews → Pre-interview check → Reports
        int At(string s) => xaml.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("x:Name=\"NavInterviews\"") < At("x:Name=\"NavPreInterview\"") && At("x:Name=\"NavPreInterview\"") < At("x:Name=\"NavReports\""));
        Assert.Contains("Tag=\"preinterview\" Content=\"{v:Loc preflight}\"", xaml);
        var home = App("HomeWindow.xaml.cs");
        Assert.Contains("PagePreInterview.Visibility", home);
        Assert.Contains("PagePreInterview.OnShown()", home);
        Assert.Contains("PagePreInterview.OnHidden()", home);
        Assert.Contains("DetachReadiness()", home);   // closing Home stops the probes
        Assert.Contains("preinterview", string.Join(",", HomeRoute.Pages));
    }

    [Fact]
    public void OpenReadiness_DeepLinksHomeToThePage()
    {
        var main = App("MainWindow.xaml.cs");
        var start = main.IndexOf("public void OpenReadiness(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = main[start..main.IndexOf("\n    }", start, StringComparison.Ordinal)];
        Assert.Contains("OpenHome(\"preinterview\")", body);
        Assert.DoesNotContain("new ", body);   // no window is built
    }

    [Fact]
    public void ThePage_WritesTheStore_AndRendersFromIt()
    {
        var cs = App(Path.Combine("Views", "PreInterviewPage.xaml.cs"));
        Assert.Contains("_vm.Readiness.Begin(", cs);
        Assert.Contains("_vm.Readiness.Report(new ReadinessCheckResult(", cs);
        Assert.Contains("_vm.Readiness.Changed += OnStoreChanged", cs);
        Assert.Contains("ReadinessPresentation.TransitionLog(", cs);
        Assert.Contains("AppLog.Info(line)", cs);
        // production probes kept
        foreach (var probe in new[] { "OpenAiAuthProbe.CheckAsync", "AnswerServiceProbe.RunAsync", "_vm.Cloud.RefreshAsync", "_vm.Cloud.EnsureDeviceAsync", "_vm.TestAiAsync", "_vm.TestTranscriptionAsync", "SignalGrace", "SignalFollowUp", "_main.SettingsApplied += OnSettingsApplied" })
            Assert.Contains(probe, cs);
        var xaml = App(Path.Combine("Views", "PreInterviewPage.xaml"));
        Assert.Contains("x:Name=\"ToolDiagnostics\"", xaml);
        Assert.Contains("{v:Loc pf_technical_details}", xaml);
        Assert.Contains("Click=\"Fix_Click\"", xaml);
        Assert.Equal(0, PrimaryButtons(xaml));   // the primary action (Start interview) lives in the sticky action bar
    }

    [Fact]
    public void StickyActionBar_HasRunSettingsAndOnePrimaryStart()
    {
        var bar = Between(App("HomeWindow.xaml"), "x:Name=\"PreInterviewFooter\"", "x:Name=\"ContentScroll\"");
        Assert.Equal(1, PrimaryButtons(bar));
        Assert.Contains("{v:Loc pf_run_all}", bar);
        Assert.Contains("{v:Loc rd_open_settings}", bar);
        Assert.Matches(@"x:Name=""PfStartBtn"" Style=""\{StaticResource Btn\.FormPrimary\}"" Content=""\{v:Loc pf_start_interview\}""", bar);
        Assert.Contains("PfStartBtn.IsEnabled = PagePreInterview.CanStart", App("HomeWindow.Readiness.cs"));
    }

    [Fact]
    public void GetStarted_HasExactlyOnePrimaryAction_FromTheSetupPlanner()
    {
        var page = Between(App("HomeWindow.xaml"), "x:Name=\"PageStart\"", "<!-- ===== Profiles");
        Assert.Equal(1, PrimaryButtons(page));
        Assert.Contains("x:Name=\"PrimaryActionBtn\"", page);
        Assert.DoesNotContain("Btn.Base", page);   // secondary actions are links / ghost buttons only
        foreach (var n in new[] { "StepCard0", "StepCard1", "StepCard2", "StepCard3", "AckCard", "AckBox", "AckNote", "DeveloperModeBtn" }) Assert.Contains($"x:Name=\"{n}\"", page);
        var cs = App("HomeWindow.Start.cs");
        Assert.Contains("SetupPlanner.PrimaryAction(", cs);
        Assert.Contains("SetupPlanner.ActionKey(_primaryAction)", cs);
        Assert.Contains("SetupPlanner.Steps(", cs);
        Assert.Contains("PrimaryActionBtn.IsEnabled = ack", cs);   // first run: waits for the acknowledgement
        foreach (var a in Enum.GetValues<SetupAction>()) Assert.Contains($"case SetupAction.{a}:", cs);
        Assert.DoesNotContain("private void RefreshChecklist(", App("HomeWindow.xaml.cs"));   // the dashboard lives in its own partial file
    }

    [Fact]
    public void InterviewsCard_AndTitleBarIndicator_OpenTheCheck()
    {
        var xaml = App("HomeWindow.xaml");
        var interviews = Between(xaml, "x:Name=\"PageInterviews\"", "<!-- ===== Reports");
        Assert.Contains("x:Name=\"InterviewReadinessCard\"", interviews);
        Assert.True(interviews.IndexOf("InterviewReadinessCard", StringComparison.Ordinal) < interviews.IndexOf("x:Name=\"TargetFieldGrid\"", StringComparison.Ordinal), "card at the top of the detail");
        Assert.Matches(@"x:Name=""InterviewCheckBtn""[^>]*Click=""Readiness_Click""", interviews);
        var titleBar = Between(xaml, "<!-- title bar -->", "<!-- navigation -->");
        Assert.True(titleBar.IndexOf("x:Name=\"AccountBadge\"", StringComparison.Ordinal) < titleBar.IndexOf("x:Name=\"ReadinessPill\"", StringComparison.Ordinal));
        Assert.Contains("Click=\"ReadinessPill_Click\"", titleBar);
        Assert.Contains("x:Name=\"ReadinessDot\"", titleBar);
        Assert.Contains("x:Name=\"ReadinessPillText\"", titleBar);
        var cs = App("HomeWindow.Readiness.cs");
        Assert.Contains("ReadinessPill.ToolTip = summary", cs);
        Assert.Contains("Navigate(\"preinterview\")", cs);
    }

    [Fact]
    public void SelfTest_DrivesThePage_AndChecksGetStarted()
    {
        var st = App(Path.Combine("Services", "SelfTest.cs"));
        Assert.Matches(@"HomePages = \{[^}]*""preinterview""", st);
        Assert.Contains("w.OpenReadiness(", st);
        Assert.Contains("CheckLocalAsync()", st);
        Assert.Contains("StartPrimaryButtonsForTest", st);
    }
}
