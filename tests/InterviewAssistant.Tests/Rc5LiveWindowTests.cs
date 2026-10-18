using System.Text.RegularExpressions;
using InterviewAssistant.App.UiState;
using InterviewAssistant.Core.Languages;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC5 Live Assistant window pass: header chrome rule (setup vs interview mode, narrow widths), Interview Focus
/// geometry, context-aware Quick help, the in-app health check, and source-level wiring checks (no diagnostics panel
/// or button in the Live window, no "Don't show again", maximize/restore). The real UI runs in the Windows self-test.
/// </summary>
public class Rc5LiveWindowTests
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

    private static string App(string relative) => File.ReadAllText(Path.Combine(RepoRoot, "src", "InterviewAssistant.App", relative)).Replace("\r\n", "\n");

    private static string Element(string xaml, string name)
    {
        var m = Regex.Match(xaml, $@"<[\w:.]+\s[^>]*x:Name=""{Regex.Escape(name)}""[^>]*>", RegexOptions.Singleline);
        Assert.True(m.Success, $"element {name} not found");
        return m.Value;
    }

    /// <summary>The header grid of the Live window (from x:Name="Header" to the body).</summary>
    private static string Header()
    {
        var xaml = App("MainWindow.xaml");
        var start = xaml.IndexOf("x:Name=\"Header\"", StringComparison.Ordinal);
        return xaml[start..xaml.IndexOf("<!-- ===== Body ===== -->", start, StringComparison.Ordinal)];
    }

    // ---------------- LiveChrome ----------------

    [Fact]
    public void LiveChrome_SetupMode_ShowsSetupAffordances_InterviewMode_CollapsesToTheDot()
    {
        var setup = LiveChrome.For(sessionActive: false, compact: false, width: 760);
        Assert.Equal(ReadinessIndicator.Label, setup.Readiness);
        Assert.True(setup.SetupEmptyState);
        Assert.True(setup.HomeLink);

        var interview = LiveChrome.For(sessionActive: true, compact: false, width: 760);
        Assert.Equal(ReadinessIndicator.Dot, interview.Readiness);
        Assert.False(interview.SetupEmptyState);
        Assert.False(interview.HomeLink);
        Assert.True(interview.StartLabel);   // interview controls stay
    }

    [Fact]
    public void LiveChrome_MinimumWidth_KeepsOnlyPrimaryItems_OverflowTakesTheRest()
    {
        var narrow = LiveChrome.For(false, false, LiveChrome.MinWidth);
        Assert.Equal(ReadinessIndicator.Dot, narrow.Readiness);
        Assert.False(narrow.StartLabel);
        Assert.False(narrow.AdaptiveBadge);
        Assert.False(narrow.PresentationChip);
        Assert.False(narrow.MinutesLabel);
        Assert.True(narrow.PresentationInMore);
        Assert.True(narrow.MinutesInMore);

        var wide = LiveChrome.For(true, false, 1600);
        Assert.True(wide.PresentationChip && wide.MinutesLabel && wide.AdaptiveBadge && wide.StartLabel);
        Assert.False(wide.PresentationInMore || wide.MinutesInMore);
    }

    [Fact]
    public void LiveChrome_CompactView_IsMinimal()
    {
        var c = LiveChrome.For(false, compact: true, width: 1600);
        Assert.Equal(ReadinessIndicator.Dot, c.Readiness);
        Assert.False(c.PresentationChip || c.MinutesLabel || c.AdaptiveBadge || c.HomeLink);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void LiveChrome_GrowingWidth_NeverHidesAnItem(bool session, bool compact)
    {
        LiveChromeLayout? previous = null;
        for (double w = LiveChrome.MinWidth; w <= 2000; w += 10)
        {
            var c = LiveChrome.For(session, compact, w);
            if (previous != null)
            {
                Assert.True(!previous.StartLabel || c.StartLabel, $"start label at {w}");
                Assert.True(!previous.PresentationChip || c.PresentationChip, $"chip at {w}");
                Assert.True(!previous.MinutesLabel || c.MinutesLabel, $"minutes at {w}");
                Assert.True(previous.Readiness == ReadinessIndicator.Dot || c.Readiness == ReadinessIndicator.Label, $"readiness at {w}");
            }
            previous = c;
        }
    }

    // ---------------- Interview Focus ----------------

    public static IEnumerable<object[]> WorkAreas => new[]
    {
        new object[] { "1366x768", 0d, 0d, 1366d, 728d },
        new object[] { "1920x1080", 0d, 0d, 1920d, 1040d },
        new object[] { "4K at 100 %", 0d, 0d, 3840d, 2120d },
        new object[] { "4K at 150 %", 0d, 0d, 2560d, 1400d },
        new object[] { "left of the primary", -1920d, 0d, 1920d, 1040d },
        new object[] { "above, shifted", -640d, -1080d, 1920d, 1040d },
        new object[] { "small 1280x720 at 200 %", 0d, 0d, 640d, 340d },
    };

    [Theory]
    [MemberData(nameof(WorkAreas))]
    public void FocusPlacement_EveryPreset_InsideTheWorkArea_AtTheTop_NeverMoreThan40Percent(string name, double left, double top, double width, double height)
    {
        var work = new PlacementRect(left, top, width, height);
        foreach (var preset in Enum.GetValues<FocusPreset>())
        {
            var r = FocusPlacement.Compute(work, preset);
            Assert.True(work.Contains(r), $"{name} {preset}: {r} outside {work}");
            Assert.Equal(work.Top, r.Top);
            // 40 % cap, with the window's own minimum size as the floor
            Assert.True(r.Width <= Math.Max(width * FocusPlacement.MaxShare, LiveChrome.MinWidth) + 0.01, $"{name} {preset}: width {r.Width}");
            Assert.True(r.Height <= Math.Max(height * FocusPlacement.MaxShare, 260) + 0.01, $"{name} {preset}: height {r.Height}");
            Assert.True(r.Width >= Math.Min(LiveChrome.MinWidth, width) - 0.01 && r.Height >= Math.Min(260, height) - 0.01, $"{name} {preset}: below the minimum size");
        }
    }

    [Fact]
    public void FocusPlacement_1366x768_IsCappedAt40Percent_AndCentredUnderTheWebcam()
    {
        var work = new PlacementRect(0, 0, 1366, 728);
        var r = FocusPlacement.Compute(work, FocusPreset.TopCentre);
        Assert.True(r.Width <= 1366 * 0.4 && r.Height <= 728 * 0.4, r.ToString());
        Assert.True(Math.Abs((r.Left + r.Width / 2) - 683) <= 1, "centred");
        Assert.Equal(0, r.Top);
    }

    [Fact]
    public void FocusPlacement_LargeScreens_UseTheRecommendedSize_CornersKeepAGap()
    {
        var work = new PlacementRect(0, 0, 3840, 2120);
        var centre = FocusPlacement.Compute(work, FocusPreset.TopCentre);
        Assert.Equal(FocusPlacement.RecommendedWidth, centre.Width);
        Assert.Equal(FocusPlacement.RecommendedHeight, centre.Height);
        Assert.Equal(FocusPlacement.EdgeGap, FocusPlacement.Compute(work, FocusPreset.TopLeft).Left);
        Assert.Equal(work.Right - FocusPlacement.EdgeGap, FocusPlacement.Compute(work, FocusPreset.TopRight).Right);
    }

    [Fact]
    public void FocusPlacement_NegativeCoordinateMonitor_StaysOnThatMonitor()
    {
        var work = new PlacementRect(-1920, -200, 1920, 1040);
        var left = FocusPlacement.Compute(work, FocusPreset.TopLeft);
        var right = FocusPlacement.Compute(work, FocusPreset.TopRight);
        Assert.Equal(-1920 + FocusPlacement.EdgeGap, left.Left);
        Assert.Equal(-200, left.Top);
        Assert.True(right.Right <= 0 && right.Left > -1920, right.ToString());
        Assert.True((FocusPlacement.Compute(work, FocusPreset.TopCentre).Left + 280) is > -961 and < -959);
    }

    [Fact]
    public void FocusPreset_IsRememberedAsText_UnknownFallsBackToTopCentre()
    {
        foreach (var p in Enum.GetValues<FocusPreset>()) Assert.Equal(p, FocusPlacement.Parse(p.ToString()));
        Assert.Equal(FocusPreset.TopCentre, FocusPlacement.Parse(null));
        Assert.Equal(FocusPreset.TopCentre, FocusPlacement.Parse("Sideways"));
        Assert.Equal(FocusPreset.TopCentre, FocusPlacement.Parse("42"));
    }

    // ---------------- Quick help ----------------

    private static readonly QuickHelpHotkeys Hk = new("Ctrl+Alt+L", "Ctrl+Alt+Q", "Ctrl+Alt+Space", "Ctrl+Alt+C");

    [Fact]
    public void QuickHelp_StatePrecedence_ASessionWinsOverTheSignIn()
    {
        Assert.Equal(QuickHelpState.SignedOut, QuickHelp.StateFor(signInRequired: true, listening: false, paused: false));
        Assert.Equal(QuickHelpState.Setup, QuickHelp.StateFor(false, false, false));
        Assert.Equal(QuickHelpState.Listening, QuickHelp.StateFor(true, listening: true, paused: false));
        Assert.Equal(QuickHelpState.Paused, QuickHelp.StateFor(true, false, paused: true));
    }

    [Fact]
    public void QuickHelp_ContentFollowsTheContext_WithTheConfiguredHotkeys()
    {
        var signedOut = QuickHelp.For(QuickHelpState.SignedOut, answerShown: true, Hk);
        Assert.Equal("qh_title_signed_out", signedOut.Sections[0].TitleKey);
        Assert.Single(signedOut.Sections);                       // no answer tips without an account
        Assert.Equal(QuickHelpAction.SignIn, signedOut.Action);
        Assert.Equal("account", QuickHelp.ActionRoute(signedOut.Action));

        var setup = QuickHelp.For(QuickHelpState.Setup, false, Hk);
        Assert.Contains("qh_setup_1", setup.Sections[0].LineKeys);
        Assert.Equal("preinterview", QuickHelp.ActionRoute(setup.Action));
        Assert.Contains(setup.Hotkeys, h => h.LabelKey == "qh_hk_start" && h.Keys == "Ctrl+Alt+L");

        var custom = QuickHelp.For(QuickHelpState.Listening, false, Hk with { StartPause = "Ctrl+Shift+F9" });
        Assert.Contains(custom.Hotkeys, h => h.LabelKey == "qh_hk_start" && h.Keys == "Ctrl+Shift+F9");   // the user's own hotkey
        Assert.Equal(QuickHelpAction.None, custom.Action);

        var paused = QuickHelp.For(QuickHelpState.Paused, answerShown: true, Hk);
        Assert.Equal(new[] { "qh_title_paused", "qh_title_answer" }, paused.Sections.Select(s => s.TitleKey));
        Assert.DoesNotContain(QuickHelp.For(QuickHelpState.Listening, false, Hk).Sections, s => s.TitleKey == "qh_title_answer");
        Assert.DoesNotContain(QuickHelp.For(QuickHelpState.Setup, false, Hk with { ManualInput = "" }).Hotkeys, h => h.LabelKey == "qh_hk_type");
    }

    public static IEnumerable<object[]> Languages => LanguageRegistry.All.Select(l => new object[] { l.Code });

    [Theory]
    [MemberData(nameof(Languages))]
    public void QuickHelp_FocusAndHealthCheck_TextsExist_AndAreTranslated(string lang)
    {
        var keys = Enum.GetValues<QuickHelpState>().SelectMany(s => QuickHelp.For(s, true, Hk).Keys)
            .Concat(Enum.GetValues<FocusPreset>().Select(FocusPlacement.Key))
            .Concat(HealthCheck.Evaluate(new HealthFacts(1, ServiceReach.Reachable, true, true)).Concat(HealthCheck.Evaluate(new HealthFacts(0, ServiceReach.Unreachable, false, false)))
                .SelectMany(r => new[] { r.LabelKey, r.DetailKey }))
            .Concat(new[]
            {
                "live_rd_tt", "live_rd_tt_run", "tt_more", "more_compact", "more_coach", "more_focus", "more_home", "more_last_report",
                "help_diag", "help_diag_hint", "help_diag_copy", "help_diag_run", "help_diag_result", "hc_pass", "hc_warn", "qh_shortcuts", "qh_full_help",
                "focus_title_first", "focus_body_first", "focus_title", "focus_body", "focus_apply", "focus_restore", "live_empty_setup", "live_open_home",
            })
            .Concat(Enum.GetValues<ReadinessState>().Select(ReadinessSnapshot.StateKey))
            .Distinct().ToList();
        var en = Loc.TableFor("en");
        var t = Loc.TableFor(lang);
        foreach (var k in keys)
        {
            Assert.True(t.TryGetValue(k, out var v) && v.Trim().Length > 0, $"{lang}: {k} missing");
            if (lang != "en" && k is not ("settings" or "sign_in")) Assert.NotEqual(en[k], v);
        }
        // honest framing: Interview Focus never claims to hide the window
        Assert.Contains("does not hide it from screen sharing", en["focus_body_first"]);
        Assert.DoesNotContain("invisible", en["focus_body_first"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Ready when you are — start listening when the interview begins.", en["live_empty_setup"]);
    }

    // ---------------- health check ----------------

    [Fact]
    public void HealthCheck_PassAndWarnRows_NeverFail()
    {
        var ok = HealthCheck.Evaluate(new HealthFacts(2, ServiceReach.Reachable, true, true));
        Assert.Equal(4, ok.Count);
        Assert.Equal(4, HealthCheck.Passed(ok));
        var bad = HealthCheck.Evaluate(new HealthFacts(0, ServiceReach.Unreachable, false, false));
        Assert.All(bad, r => Assert.Equal(HealthOutcome.Warn, r.Outcome));
        Assert.Equal(new[] { HealthCheckId.AudioDevice, HealthCheckId.Service, HealthCheckId.Settings, HealthCheckId.Disk }, bad.Select(r => r.Id));
        Assert.Equal(HealthOutcome.Pass, HealthCheck.Evaluate(new HealthFacts(1, ServiceReach.DeveloperMode, true, true))[1].Outcome);
        Assert.Equal(HealthOutcome.Warn, HealthCheck.Evaluate(new HealthFacts(1, ServiceReach.NotConfigured, true, true))[1].Outcome);
        Assert.Equal(1.3, HealthCheck.AnswerSeconds(1260, 900));
        Assert.Equal(0.9, HealthCheck.AnswerSeconds(-1, 900));    // typed question: no speech end
        Assert.Null(HealthCheck.AnswerSeconds(0, 0));
    }

    // ---------------- source wiring ----------------

    [Fact]
    public void LiveToolbar_HasNoDiagnosticsButton_NoDiagPanel_NoOldPreInterviewIcon()
    {
        var xaml = App("MainWindow.xaml");
        var code = App("MainWindow.xaml.cs") + App("MainWindow.Chrome.cs");
        Assert.DoesNotContain("DiagPanel", xaml);
        Assert.DoesNotContain("Diagnostics_Click", xaml);
        Assert.DoesNotContain("tt_diagnostics", xaml);
        Assert.DoesNotContain("{Binding Diagnostics}", xaml);
        Assert.DoesNotContain("DiagPanel", code);
        Assert.DoesNotContain("tt_preflight", Header());           // the shield icon is gone; the readiness indicator replaces it
        Assert.DoesNotContain("Settings_Click", Header());         // settings live in the More menu
        Assert.DoesNotContain("Compact_Click", Header());
        // what the header keeps, in order: status, Start/Pause, Stop, readiness, quick help, More, minimize, maximize, close
        var header = Header();
        var order = new[] { "x:Name=\"StatusPill\"", "x:Name=\"StartBtn\"", "x:Name=\"StopBtn\"", "x:Name=\"ReadinessBtn\"", "x:Name=\"HelpBtn\"", "x:Name=\"MoreBtn\"", "Click=\"Minimize_Click\"", "x:Name=\"MaxBtn\"", "Click=\"Close_Click\"" }
            .Select(n => header.IndexOf(n, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.OrderBy(i => i), order);
        // readiness: opens the pre-interview page directly and is refreshed on the UI thread
        Assert.Contains("OpenHome(\"preinterview\")", App("MainWindow.Chrome.cs"));
        Assert.Contains("_vm.Readiness.Changed += _ => Dispatcher.BeginInvoke(RefreshReadiness);", App("MainWindow.Chrome.cs"));
        // More menu: compact, interview focus, home, last report, settings
        var more = xaml[xaml.IndexOf("x:Name=\"MorePopup\"", StringComparison.Ordinal)..];
        foreach (var item in new[] { "MoreCompact_Click", "MoreFocus_Click", "Home_Click", "Report_Click", "Settings_Click" }) Assert.Contains(item, more);
        Assert.Contains("Visibility=\"{Binding LastReportPath, Converter={StaticResource StrVis}}\"", Element(more, "MoreReport"));
    }

    [Fact]
    public void QuickHelp_IsACompactPopover_WithoutDontShowAgain()
    {
        var xaml = App("MainWindow.xaml");
        Assert.DoesNotContain("live_intro_dont_show", xaml);
        Assert.DoesNotContain("IntroDontShow", xaml);
        Assert.DoesNotContain("x:Name=\"LiveIntro\"", xaml);
        Assert.Matches(@"<Popup x:Name=""QuickHelpPopup"" PlacementTarget=""\{Binding ElementName=HelpBtn\}""", xaml);
        Assert.Contains("MaxWidth=\"340\"", Element(xaml, "QuickHelpCard"));
        var code = App("MainWindow.QuickHelp.cs");
        Assert.Contains("FlowDirection = FlowDirection.LeftToRight", code);    // hotkeys stay LTR in RTL layouts
        Assert.Contains("_vm.DismissLiveIntro(remember: true)", code);           // first-use flag
        var popover = File.ReadAllText(Path.Combine(RepoRoot, "src", "InterviewAssistant.App", "Views", "Popover.cs"));
        Assert.Contains("Key.Escape", popover);
        Assert.Contains("_anchor.Focus()", popover);                             // focus returns to "?"
        Assert.Contains("PreviewMouseDown", popover);                            // click outside closes
    }

    [Fact]
    public void LiveWindow_MaximizeRestore_WorkAreaHook_ReadableColumn()
    {
        var xaml = App("MainWindow.xaml");
        var max = Element(xaml, "MaxBtn");
        Assert.Contains("Click=\"Maximize_Click\"", max);
        Assert.Contains("AutomationProperties.Name=\"{v:Loc tt_maximize}\"", max);
        Assert.Contains("Text=\"&#xE922;\"", Element(xaml, "MaxIcon"));
        var chrome = App("MainWindow.Chrome.cs");
        Assert.Contains("WindowPlacement.HookMaximizeToWorkArea(this);", chrome);
        Assert.Contains("\uE923", chrome);                                         // restore glyph while maximized
        Assert.Contains("new Thickness(max ? 0 : 8)", chrome);
        Assert.Contains("if (e.ClickCount == 2) { ToggleMaximize(); return; }", App("MainWindow.xaml.cs"));
        foreach (var name in new[] { "LiveBody", "ActionBar", "ManualBar" })
            Assert.Contains("MaxWidth=\"{StaticResource LiveReadableWidth}\"", Element(xaml, name));
        Assert.Contains("RestoreBounds", App("MainWindow.xaml.cs"));               // closing while maximized saves the normal placement
    }

    [Fact]
    public void HelpPage_HasADiagnosticsSection_AndTheHealthCheckNeverLaunchesTheCiSelfTest()
    {
        var xaml = App("HomeWindow.xaml");
        var help = xaml[xaml.IndexOf("x:Name=\"PageHelp\"", StringComparison.Ordinal)..xaml.IndexOf("<!-- ===== Account ===== -->", StringComparison.Ordinal)];
        Assert.Contains("{v:Loc help_diag}", help);
        foreach (var handler in new[] { "CopyDiagnostics_Click", "OpenLogs_Click", "RunHealthCheck_Click" }) Assert.Contains(handler, help);
        var code = App("HomeWindow.Help.cs");
        Assert.DoesNotContain("--selftest", code);
        Assert.DoesNotContain("Process.Start", code);
        Assert.DoesNotContain("ex.Message", code);                                 // no raw exception text in the UI
        Assert.Contains("HealthCheck.Evaluate", code);
        var selfTest = App(Path.Combine("Services", "SelfTest.cs"));
        Assert.DoesNotContain("vm.Settings.ShowDiagnostics = true", selfTest);
        Assert.Contains("home.RunHealthCheckAsync()", selfTest);
        Assert.Contains("w.ApplyFocus(preset)", selfTest);
        Assert.Contains("w.ToggleMaximize()", selfTest);
    }
}
