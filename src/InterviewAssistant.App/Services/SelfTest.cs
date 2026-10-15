using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Turn;

namespace InterviewAssistant.App.Services;

/// <summary>
/// `InterviewAssistant.exe --selftest result.json` — runs inside the real WPF process on Windows (CI or user PC),
/// with NO API key and NO credits: loads every window's XAML, drives the manual-question UI, checks error states,
/// DPAPI, settings persistence, hotkeys, audio enumeration and pause/stop. Exit code 0 = all passed.
/// </summary>
public static class SelfTest
{
    public sealed record CheckResult(string Name, bool Ok, string Detail, long Ms);

    public static async Task<int> RunAsync(MainWindow w, MainViewModel vm, string outPath)
    {
        var results = new List<CheckResult>();
        async Task Check(string name, Func<Task<(bool, string)>> f)
        {
            var sw = Stopwatch.StartNew();
            try { var (ok, d) = await f(); results.Add(new(name, ok, d, sw.ElapsedMilliseconds)); }
            catch (Exception ex) { results.Add(new(name, false, ex.GetType().Name + ": " + ex.Message, sw.ElapsedMilliseconds)); }
            AppLog.Info($"SELFTEST {(results[^1].Ok ? "PASS" : "FAIL")} {name}: {results[^1].Detail}");
        }
        async Task Idle() => await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        async Task<bool> WaitFor(Func<bool> cond, int ms) { var until = Environment.TickCount64 + ms; while (!cond() && Environment.TickCount64 < until) await Task.Delay(50); return cond(); }

        await Check("Live Assistant introduction: shown on first use, dismissible", async () =>
        {
            await Idle();
            var shown = vm.ShowLiveIntro && w.FindName("LiveIntro") is FrameworkElement f && f.Visibility == Visibility.Visible;
            vm.DismissLiveIntro(dontShowAgain: false);
            await Idle();
            return (shown && !vm.ShowLiveIntro && !vm.Settings.LiveIntroDismissed, shown ? "shown, then dismissed for this session" : "not shown");
        });
        await Check("Build information: version, channel and commit", () =>
        {
            var v = BuildInfo.Version;
            var ok = v.Length > 0 && !v.Contains('+') && BuildInfo.Channel.Length > 0 && BuildInfo.Commit.Length > 0
                     && (!v.Contains("-rc") || BuildInfo.Channel == "Release Candidate");
            return Task.FromResult((ok, BuildInfo.Summary));
        });
        await Check("Notifications: toast appears, duplicates suppressed", async () =>
        {
            vm.Toast("Self-test notification", Views.ToastKind.Success);
            vm.Toast("Self-test notification", Views.ToastKind.Success);
            await Idle();
            var host = (Views.ToastHost)w.FindName("Toasts");
            var n = host.VisibleTexts.Count(t => t == "Self-test notification");
            return (n == 1, $"{n} visible");
        });

        await Check("Generic start: no hard-coded candidate", () => Task.FromResult((vm.Knowledge!.Questions.Count == 0 || vm.ActiveLabel.Length > 0, vm.ActiveLabel)));
        await Check("Fixture pack loads on request (--fixture)", () => Task.FromResult((vm.LoadSample(App.FixtureDir), vm.ActiveLabel)));
        await Check("Main window created + XAML loaded", () => Task.FromResult((w.IsLoaded && w.IsVisible && new System.Windows.Interop.WindowInteropHelper(w).Handle != IntPtr.Zero, $"{w.ActualWidth:0}x{w.ActualHeight:0}")));
        await Check("Window topmost", () => Task.FromResult((w.Topmost, w.Topmost ? "on" : "off")));
        await Check("Knowledge pack loaded", () => Task.FromResult((vm.Knowledge != null && vm.Knowledge.Profile.Experience.Count == 5, $"{vm.ActiveLabel}, {vm.Knowledge?.Stories.Count} stories, {vm.Knowledge?.Snippets.Count} notes")));
        await Check("Question bank loaded (>=80)", () => Task.FromResult((vm.Knowledge?.Questions.Count >= 80, $"{vm.Knowledge?.Questions.Count} questions")));
        await Check("Prepared answers pass fact validator", () =>
        {
            var v = new FactValidator(vm.Knowledge!.Profile, vm.Knowledge.Context);
            var bad = vm.Knowledge.Questions.Where(q => v.Validate(q.ShortBullets, q.Mode).HasFactualIssues).Select(q => q.QuestionId).ToList();
            return Task.FromResult((bad.Count == 0, bad.Count == 0 ? "all clean" : string.Join(",", bad)));
        });
        await Check("Manual question → 3 prepared bullets in UI (no API key needed)", async () =>
        {
            var sw = Stopwatch.StartNew();
            await vm.SubmitManualAsync("How would you increase XAB adoption?");
            var expected = vm.Engine!.Current!.SnapshotBullets().Count;
            await WaitFor(() => vm.Bullets.Count >= expected && vm.Question.Contains("XAB"), 3000);
            await Task.Delay(300); await Idle(); // let any late notifications land: count must stay exact (no duplicates)
            var distinct = vm.Bullets.Select(b => b.Text).Distinct().Count();
            return (vm.Bullets.Count == 3 && distinct == 3 && vm.Question.Contains("XAB"), $"{vm.Bullets.Count} bullets ({distinct} distinct) in {sw.ElapsedMilliseconds} ms: \"{vm.Bullets.FirstOrDefault()?.Text}\"");
        });
        await Check("Spoken-style paraphrase matches", async () =>
        {
            await vm.SubmitManualAsync("so um tell me a little bit about yourself");
            var ok = await WaitFor(() => vm.Bullets.Count >= 3 && vm.Question.Contains("yourself", StringComparison.OrdinalIgnoreCase), 3000);
            await Task.Delay(300); await Idle();
            return (ok && vm.Bullets.Count == 3, $"{vm.Bullets.Count} bullets (default view shows exactly 3): {vm.Bullets.FirstOrDefault()?.Text}");
        });
        await Check("Customer build: service-only mode (no own-API-key path)", () =>
            Task.FromResult((BuildFlavor.Developer || vm.UsingCloud, BuildFlavor.Developer ? "developer build" : "release build, cloud only")));
        await Check("Offline service: account state and overlay banner explain it, no crash", async () =>
        {
            await vm.Cloud.RefreshAsync(CancellationToken.None);
            await Idle();
            var ok = vm.Cloud.State is CloudState.ServiceUnavailable or CloudState.SignedOut && vm.Cloud.Message is { Length: > 0 };
            return (ok, $"{vm.Cloud.State}: {vm.Cloud.Message} | banner '{vm.ServiceBanner}'");
        });
        await Check("Signed out: Start explains how to sign in and does not listen", async () =>
        {
            vm.Note = null;
            await vm.ToggleListeningAsync();
            var note = vm.Note ?? "";
            return (!vm.IsListening && (BuildFlavor.Developer ? note.Contains("API key") : note.Contains("Sign in")), note);
        });
        await Check("Unknown question without the AI service: readable message, prepared answers still work, no crash", async () =>
        {
            vm.Note = null;
            await vm.SubmitManualAsync("Zebra quantum basketball orchestra symphony?");
            var ok = await WaitFor(() => vm.Question.Contains("Zebra") && !string.IsNullOrEmpty(vm.Note), 8000);
            var note = vm.Note ?? "";
            return (ok && !note.Contains("Exception") && !note.Contains(" at ") && (BuildFlavor.Developer || !note.Contains("API key")), note);
        });
        await Check("Previous / next answer navigation", async () =>
        {
            await Idle();
            var before = vm.Question;
            vm.PreviousCommand.Execute(null); await Idle();
            var moved = vm.Question != before;
            vm.NextCommand.Execute(null); await Idle();
            return (moved && vm.Question == before, vm.HistoryLabel);
        });
        await Check("Rework buttons act on the displayed item and never add a history entry", async () =>
        {
            await Idle();
            vm.PreviousCommand.Execute(null); await Idle();
            var label = vm.HistoryLabel; var q = vm.Question;
            var can = vm.ShorterCommand.CanExecute(null);
            if (can) { vm.ShorterCommand.Execute(null); await Task.Delay(1500); await Idle(); }
            var ok = vm.HistoryLabel == label && vm.Question == q;
            vm.NextCommand.Execute(null); await Idle();
            return (ok, $"{label} → {vm.HistoryLabel}; rework {(can ? "ran" : "disabled without the answer service")}");
        });
        await Check("Coach mode (Ctrl+Alt+C): 3 keywords + structure, then back to bullets", async () =>
        {
            vm.ToggleCoach();
            await vm.SubmitManualAsync("How would you increase XAB adoption?");
            var ok = await WaitFor(() => vm.IsCoachView && vm.CoachStructure.Contains('→'), 3000);
            await Idle();
            var kw = vm.CoachKeywords.Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var shown = $"[{vm.CoachKeywords}] [{vm.CoachStructure}]";
            vm.ToggleCoach();
            await vm.SubmitManualAsync("How would you increase XAB adoption?");
            var back = await WaitFor(() => !vm.IsCoachView && vm.Bullets.Count == 3, 3000);
            return (ok && kw.Length == 3 && back, $"coach {shown} → back to {vm.Bullets.Count} bullets");
        });
        await Check("Auto-fit: every bullet visible without scrolling at the default size; 3rd bullet visible when smaller", async () =>
        {
            await vm.SubmitManualAsync("Tell me about yourself.");
            await WaitFor(() => vm.Bullets.Count >= 3, 3000);
            await Task.Delay(300); await Idle(); await Idle();
            var n = vm.Bullets.Count;
            var atDefault = w.AnswerScroll.ScrollableHeight;
            var fontDefault = vm.AnswerFontSize;
            var h = w.Height;
            w.Height = 480;
            await Task.Delay(300); await Idle(); await Idle();
            var third = w.BulletBottom(2);
            var viewport = w.AnswerScroll.ViewportHeight;
            var fontSmall = vm.AnswerFontSize;
            w.Height = h; await Task.Delay(200); await Idle();
            var restored = vm.AnswerFontSize;
            var ok = atDefault <= 1 && third <= viewport + 1 && restored >= fontDefault;
            return (ok, $"{n} bullets; default {h:0}px: overflow {atDefault:0}px, font {fontDefault}; 480px: 3rd bullet bottom {third:0}/{viewport:0}px, font {fontSmall} (min {vm.MinAnswerFontSize}); restored font {restored}");
        });
        await Check("UI localisation: Persian switches to right-to-left with translated labels", async () =>
        {
            vm.Settings.UiLanguage = "fa"; w.ApplyAppearance(); await Idle();
            var rtl = w.FlowDirection == FlowDirection.RightToLeft && vm.LQuestion == Core.Languages.UiStrings.Get("fa", "question");
            var label = vm.LQuestion;
            vm.Settings.UiLanguage = "en"; w.ApplyAppearance(); await Idle();
            return (rtl && w.FlowDirection == FlowDirection.LeftToRight, $"fa label '{label}', restored to LTR");
        });
        foreach (var lang in new[] { "fa", "hi" })
            await Check($"UI localisation ({lang}): no English text left in the live, home, settings and pre-interview windows", async () =>
            {
                var leftovers = new SortedSet<string>(StringComparer.Ordinal);
                vm.Settings.UiLanguage = lang; w.ApplyAppearance(); await Idle();
                try
                {
                    var english = EnglishOnlyTexts(lang);
                    ScanTexts(w, english, leftovers);
                    var home = new HomeWindow(vm, w) { Owner = w };
                    home.Show(); await Idle();
                    foreach (var page in new[] { "start", "profiles", "interviews", "reports", "account", "help" }) { home.ShowPage(page); await Idle(); ScanTexts(home, english, leftovers); }
                    var rtlOk = lang != "fa" || home.FlowDirection == FlowDirection.RightToLeft;
                    home.Close();
                    var s = new SettingsWindow(vm) { Owner = w }; s.Show(); await Idle(); ScanTexts(s, english, leftovers); s.Close();
                    var r = new ReadinessWindow(vm, w, autoRun: false) { Owner = w }; r.Show(); await Idle(); r.CheckLocal(); await Idle(); ScanTexts(r, english, leftovers); r.Close();
                    var mainRtl = lang != "fa" || w.FlowDirection == FlowDirection.RightToLeft;
                    return (leftovers.Count == 0 && rtlOk && mainRtl, leftovers.Count == 0 ? $"4 windows, 6 home pages scanned; {(lang == "fa" ? "right-to-left" : "left-to-right")}" : "English left: " + string.Join(" | ", leftovers.Take(12)));
                }
                finally { vm.Settings.UiLanguage = "en"; w.ApplyAppearance(); await Idle(); }
            });
        await Check("Home window: onboarding, profiles, interviews, reports, account pages load", async () =>
        {
            var home = new HomeWindow(vm, w) { Owner = w };
            home.Show(); await Idle();
            foreach (var page in new[] { "start", "profiles", "interviews", "reports", "account", "help" }) { home.ShowPage(page); await Idle(); }
            home.ShowPage("account"); await Idle();
            var signInForm = home.SignedOutPanel.Visibility == Visibility.Visible || !vm.Cloud.IsConfigured;
            var noDevButton = BuildFlavor.Developer || home.DeveloperModeBtn.Visibility != Visibility.Visible;
            var ok = home.IsLoaded && signInForm && noDevButton; home.Close();
            return (ok, $"5 pages rendered; sign-in form {(signInForm ? "shown" : "MISSING")}; developer button {(noDevButton ? "hidden" : "VISIBLE")}");
        });
        await Check("Profile → confirm facts → prepare → load (isolated DPAPI workspace)", async () =>
        {
            var root = Path.Combine(AppPaths.Local, "selftest-workspace");
            var ws = new Workspace(root, new DpapiProtector());
            var profile = new Core.Domain.CandidateProfileRecord();
            var doc = Core.Domain.ProfileService.CreateDocument("cv.txt", Core.Domain.DocumentKind.Resume,
                "Jane Example\nProduct Manager\nBerlin\n\nEXPERIENCE\nSenior Product Manager — Contoso Payments (2019 – 2024)\n- Launched instant payouts used by 40,000 merchants\n- Reduced checkout drop-off by 18% through A/B testing\n\nSKILLS\nRoadmapping, SQL, Stakeholder management");
            Core.Domain.ProfileService.AddResume(profile, doc);
            profile.ConfirmAll();
            Core.Domain.ProfileService.RebuildStories(profile);
            ws.Store.SaveProfile(profile);
            var target = new Core.Domain.InterviewTarget { ProfileId = profile.Id, JobTitle = "Product Manager", Company = "Fabrikam", JobDescriptionText = "We need a product manager to own payments roadmap, run experiments, work with engineering and stakeholders. SQL a plus." };
            ws.Store.SaveTarget(target);
            var pack = await Task.Run(() => new Core.Preparation.PreparationPipeline(ws.Assets).RunAsync(profile, target));
            ws.Store.SavePack(pack);
            var (kb, label) = ws.LoadActive(profile.Id, target.Id);
            var ok = pack.Questions.Count >= 50 && kb.Questions.Count == pack.Questions.Count && label.Contains("Fabrikam");
            try { Directory.Delete(root, true); } catch (IOException) { }
            return (ok, $"{pack.Questions.Count} questions, {pack.Stories.Count} stories, label '{label}', {ws.Assets.Library.Count} library items");
        });
        await Check("Shipped cloud.json: public API endpoint only, no secrets", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cloud.json");
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            var secretLike = System.Text.RegularExpressions.Regex.IsMatch(text, @"sk-|pdl_|secret|service_role|password", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return Task.FromResult((text.Contains("https://") && !secretLike, text.Length == 0 ? "missing" : text.Replace("\n", " ").Trim()));
        });
        await Check("Settings window XAML", async () =>
        {
            var s = new SettingsWindow(vm) { Owner = w };
            s.Show(); await Idle();
            var ok = s.IsLoaded; s.Close();
            return (ok, "opened and closed");
        });
        await Check("Pre-interview window XAML + local checks", async () =>
        {
            var r = new ReadinessWindow(vm, w, autoRun: false) { Owner = w };
            r.Show(); await Idle();
            r.CheckLocal(); await Idle();
            var ok = r.IsLoaded; r.Close();
            return (ok, "opened, local checks ran, closed");
        });
        await Check("Compact mode toggle", async () => { w.SetCompact(true); await Idle(); w.SetCompact(false); await Idle(); return (true, "on/off"); });
        await Check("Diagnostics panel", async () => { vm.Settings.ShowDiagnostics = true; w.ApplyAppearance(); await Task.Delay(1300); var d = vm.Diagnostics; vm.Settings.ShowDiagnostics = false; w.ApplyAppearance(); return (d.Contains("Questions"), d.Split('\n')[0]); });
        await Check("DPAPI credential protection round-trip", () => Task.FromResult((SecretStore.SelfTestRoundTrip(), "protect/unprotect ok, wrong entropy rejected")));
        await Check("Settings persistence", () =>
        {
            var path = AppSettings.OverridePath!;
            var s = new AppSettings { AnswerModel = "selftest-model", WindowOpacity = 0.85 };
            s.Save();
            var back = AppSettings.Load();
            return Task.FromResult((back.AnswerModel == "selftest-model" && Math.Abs(back.WindowOpacity - 0.85) < 1e-9, path));
        });
        await Check("Unsigned build never self-updates", () =>
        {
            var signed = Environment.ProcessPath is { } exe && Authenticode.TrustedSigner(exe) != null;
            var u = new UpdateService(vm.Settings, "https://updates.example.invalid/feed");
            return Task.FromResult((signed || (!u.IsInstalled && u.Status.Contains("not code-signed")), signed ? "signed build" : u.Status));
        });
        await Check("Hotkey parsing", () => Task.FromResult((HotkeyService.TryParse("Ctrl+Alt+Space", out _, out _) && !HotkeyService.TryParse("Space", out _, out _), "Ctrl+Alt+Space ok, bare key rejected")));
        await Check("Audio endpoint enumeration (no crash without devices)", () =>
        {
            var devs = AudioCaptureService.ListDevices();
            return Task.FromResult((devs.Count >= 1, $"{devs.Count - 1} playback device(s); capture status {vm.Capture.Status}"));
        });
        await Check("Turn detector + duplicate guard at runtime", () =>
        {
            var clock = new Core.Diagnostics.ManualClock();
            var td = new TurnDetector(clock); string? got = null; td.TurnFinalized += t => got = t.Text; td.Start();
            td.OnSpeechStarted(); clock.Advance(800); td.OnSpeechStopped(); td.OnSegmentCompleted("a", "How would you prioritize the backlog?");
            for (int i = 0; i < 50; i++) { clock.Advance(20); td.Tick(); }
            var g = new DuplicateGuard(); g.Register(got ?? "", 0);
            return Task.FromResult((got != null && g.IsDuplicate("how would you prioritize the backlog", 100), got ?? "not finalized"));
        });
        await Check("Stop returns to idle + honesty-labelled report saved", async () =>
        {
            await vm.StopAsync();
            var path = vm.LastReportPath;
            var html = path != null && File.Exists(path) ? File.ReadAllText(path) : "";
            var stored = vm.Workspace?.Store.Sessions().FirstOrDefault();
            var ok = !vm.IsListening && !vm.IsPaused && html.Contains("microphone was OFF") && html.Contains("Content-Security-Policy") && stored?.ReportJson.Length > 0;
            return (ok, $"{vm.StatusText}; report {(path == null ? "missing" : Path.GetFileName(path))} ({html.Length} chars)");
        });

        var passed = results.All(r => r.Ok);
        var json = JsonSerializer.Serialize(new { passed, version = typeof(App).Assembly.GetName().Version?.ToString(), os = Environment.OSVersion.ToString(), checks = results }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outPath, json);
        AppLog.Info($"SELFTEST {(passed ? "PASSED" : "FAILED")} ({results.Count(r => r.Ok)}/{results.Count})");
        return passed ? 0 : 1;
    }

    /// <summary>English UI strings whose translation differs: seeing one of them on screen means a surface was not localised.</summary>
    private static HashSet<string> EnglishOnlyTexts(string lang)
    {
        var en = Core.Languages.Loc.TableFor("en");
        var t = Core.Languages.Loc.TableFor(lang);
        return en.Where(kv => t.TryGetValue(kv.Key, out var v) && v != kv.Value && !kv.Value.Contains('{') && System.Text.RegularExpressions.Regex.IsMatch(kv.Value, "[A-Za-z]{3,}"))
                 .Select(kv => kv.Value.Trim()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Walks the logical tree (collapsed pages and tool tips included) and records every English UI string found.</summary>
    private static void ScanTexts(DependencyObject root, HashSet<string> english, SortedSet<string> found)
    {
        void Hit(object? o) { if (o is string str && english.Contains(str.Trim())) found.Add(str.Trim()); }
        var stack = new Stack<object>(); stack.Push(root);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            if (!seen.Add(o)) continue;
            switch (o)
            {
                case System.Windows.Controls.TextBlock tb: Hit(tb.Text); break;
                case System.Windows.Controls.TextBox: break;   // user content
                case System.Windows.Controls.HeaderedContentControl hc: Hit(hc.Header); Hit(hc.Content); break;
                case System.Windows.Controls.ContentControl cc: Hit(cc.Content); break;
            }
            if (o is FrameworkElement fe) { Hit(fe.ToolTip); Hit(System.Windows.Automation.AutomationProperties.GetName(fe)); }
            if (o is DependencyObject d)
            {
                foreach (var c in LogicalTreeHelper.GetChildren(d)) stack.Push(c);
                if (d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                    for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
        }
    }
}
