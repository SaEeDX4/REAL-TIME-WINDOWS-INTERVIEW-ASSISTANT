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

        await Check("Generic start: no hard-coded candidate", () => Task.FromResult((vm.Knowledge!.Questions.Count == 0 || vm.ActiveLabel.Length > 0, vm.ActiveLabel)));
        await Check("Sample profile loads on request (test fixture)", () => Task.FromResult((vm.LoadSample(), vm.ActiveLabel)));
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
            return (ok && vm.Bullets.Count == 4, $"{vm.Bullets.Count} bullets: {vm.Bullets.FirstOrDefault()?.Text}");
        });
        if (!SecretStore.HasKey)
        {
            await Check("Missing API key: Start shows guidance, does not listen", async () =>
            {
                await vm.ToggleListeningAsync();
                return (!vm.IsListening && (vm.Note ?? "").Contains("API key"), vm.Note ?? "(no note)");
            });
            await Check("Missing API key: unknown question shows actionable message, no crash", async () =>
            {
                vm.Note = null;
                await vm.SubmitManualAsync("Zebra quantum basketball orchestra symphony?");
                var ok = await WaitFor(() => vm.Question.Contains("Zebra") && !string.IsNullOrEmpty(vm.Note), 5000);
                return (ok && (vm.Note ?? "").Contains("Settings"), vm.Note ?? "");
            });
        }
        await Check("Previous / next answer navigation", async () =>
        {
            await Idle();
            var before = vm.Question;
            vm.PreviousCommand.Execute(null); await Idle();
            var moved = vm.Question != before;
            vm.NextCommand.Execute(null); await Idle();
            return (moved && vm.Question == before, vm.HistoryLabel);
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
        await Check("Stop returns to idle", async () => { await vm.StopAsync(); return (!vm.IsListening && !vm.IsPaused, vm.StatusText); });

        var passed = results.All(r => r.Ok);
        var json = JsonSerializer.Serialize(new { passed, version = typeof(App).Assembly.GetName().Version?.ToString(), os = Environment.OSVersion.ToString(), checks = results }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outPath, json);
        AppLog.Info($"SELFTEST {(passed ? "PASSED" : "FAILED")} ({results.Count(r => r.Ok)}/{results.Count})");
        return passed ? 0 : 1;
    }
}
