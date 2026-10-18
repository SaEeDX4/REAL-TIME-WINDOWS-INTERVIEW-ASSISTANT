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
/// with NO API key and NO credits: loads every window's XAML and every Home page (Settings included), drives the manual-question UI, checks error states,
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
        await Check("History counter hidden without history (never \"0 / 1\")", async () =>
        {
            await Idle();
            var counter = (FrameworkElement)w.FindName("HistoryCounterText");
            var hidden = vm.HistoryLabel.Length == 0 && counter.Visibility != Visibility.Visible;
            return (hidden && !vm.PreviousCommand.CanExecute(null), hidden ? "hidden" : $"shows '{vm.HistoryLabel}'");
        });
        await Check("Main window created + XAML loaded",() => Task.FromResult((w.IsLoaded && w.IsVisible && new System.Windows.Interop.WindowInteropHelper(w).Handle != IntPtr.Zero, $"{w.ActualWidth:0}x{w.ActualHeight:0}")));
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
                    foreach (var page in HomePages) { home.ShowPage(page); await Idle(); ScanTexts(home, english, leftovers); }
                    // an in-app dialog (localized buttons, RTL)
                    var dialog = Views.AppDialog.ConfirmAsync(home, Core.Languages.Loc.T("confirm_delete_report"), Core.Languages.Loc.T("dlg_delete"), destructive: true);
                    await Idle(); ScanTexts(home, english, leftovers);
                    PressKey(home, System.Windows.Input.Key.Escape); await Task.WhenAny(dialog, Task.Delay(2000));
                    var rtlOk = lang != "fa" || home.FlowDirection == FlowDirection.RightToLeft;
                    home.Close();
                    var r = new ReadinessWindow(vm, w, autoRun: false) { Owner = w }; r.Show(); await Idle(); r.CheckLocal(); await Idle(); ScanTexts(r, english, leftovers); r.Close();
                    var mainRtl = lang != "fa" || w.FlowDirection == FlowDirection.RightToLeft;
                    return (leftovers.Count == 0 && rtlOk && mainRtl && dialog.IsCompleted, leftovers.Count == 0 ? $"3 windows, {HomePages.Length} home pages and a dialog scanned; {(lang == "fa" ? "right-to-left" : "left-to-right")}" : "English left: " + string.Join(" | ", leftovers.Take(12)));
                }
                finally { vm.Settings.UiLanguage = "en"; w.ApplyAppearance(); await Idle(); }
            });
        await Check("Home window: onboarding, profiles, interviews, reports, account pages load", async () =>
        {
            var home = new HomeWindow(vm, w) { Owner = w };
            home.Show(); await Idle();
            // A fresh window has no edits: building controls (slider coercion, default selections) is not a change, and
            // closing the app must never stop on an "unsaved changes" question nobody caused.
            var cleanAtStart = !home.HasUnsavedEdits;
            foreach (var page in HomePages) { home.ShowPage(page); await Idle(); }
            home.ShowPage("account"); await Idle();
            cleanAtStart &= !home.HasUnsavedEdits;
            var signInForm = home.SignedOutPanel.Visibility == Visibility.Visible || !vm.Cloud.IsConfigured;
            var noDevButton = BuildFlavor.Developer || home.DeveloperModeBtn.Visibility != Visibility.Visible;
            // PasswordBox: same template family as TextBox, placeholder state tracked without reading the password
            home.PasswordBox.Password = "x"; await Idle();
            var tracksFilled = !Views.Watermark.GetIsEmpty(home.PasswordBox);
            home.PasswordBox.Clear(); await Idle();
            var tracksEmpty = Views.Watermark.GetIsEmpty(home.PasswordBox);
            var sameHeight = Math.Abs(home.PasswordBox.ActualHeight - home.EmailBox.ActualHeight) < 0.5;
            var ok = home.IsLoaded && signInForm && noDevButton && tracksFilled && tracksEmpty && sameHeight && cleanAtStart; home.Close();
            return (ok, $"{HomePages.Length} pages rendered; no unsaved edits after opening and paging: {cleanAtStart}; sign-in form {(signInForm ? "shown" : "MISSING")}; developer button {(noDevButton ? "hidden" : "VISIBLE")}; "
                        + $"password field {home.PasswordBox.ActualHeight:0}px = e-mail field {home.EmailBox.ActualHeight:0}px: {sameHeight}; placeholder tracking {tracksFilled && tracksEmpty}");
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
        await Check("Settings: a page in Home (gear deep-links to a section), explicit Save, validation, Discard, no separate window", async () =>
        {
            w.OpenSettings("hotkeys"); await Idle(); await Idle();
            var home = w.Home;
            if (home == null) return (false, "Home did not open");
            var page = home.SettingsPageForTest;
            var onPage = home.CurrentPage == "settings" && page.IsVisible;
            var noWindow = !Application.Current.Windows.OfType<Window>().Any(x => x is not MainWindow && x is not HomeWindow && x.GetType().Name.Contains("Settings"));
            var original = page.HkToggle.Text;
            page.HkToggle.Text = vm.Settings.HotkeyCoachToggle;   // conflicts with the fixed coach-mode hotkey
            var dirty = page.Guard.IsDirty;
            var rejected = !page.Save() && Views.Field.GetHasError(page.HkToggle) && page.HkToggleError.Visibility == Visibility.Visible;
            page.Discard(); await Idle();
            var discarded = !page.Guard.IsDirty && page.HkToggle.Text == original;
            home.Close(); await Idle();
            return (onPage && noWindow && dirty && rejected && discarded, $"page {home.CurrentPage}; dirty {dirty}; conflict rejected {rejected}; discard restored {discarded}");
        });
        await Check("Language switch with unsaved edits: no question, edits and page kept (not navigation)", async () =>
        {
            w.OpenSettings("hotkeys"); await Idle();
            var home = w.Home!;
            var page = home.SettingsPageForTest;
            page.HkManual.Text = "Ctrl+Alt+M";
            vm.Settings.UiLanguage = "fa"; w.ApplyAppearance(); await Idle(); await Idle();
            var noQuestion = !Views.AppDialog.IsOpen(home);
            var kept = page.Guard.IsDirty && page.HkManual.Text == "Ctrl+Alt+M" && home.CurrentPage == "settings";
            vm.Settings.UiLanguage = "en"; w.ApplyAppearance(); await Idle(); await Idle();
            kept &= page.Guard.IsDirty && page.HkManual.Text == "Ctrl+Alt+M";
            page.Discard(); await Idle();
            home.Close(); await Idle();
            return (noQuestion && kept, $"question shown: {!noQuestion}; edit kept through fa → en: {kept}");
        });
        await Check("In-app dialog: overlay inside the window, destructive defaults to Cancel, Escape cancels, unsaved-changes question on close", async () =>
        {
            w.OpenHome(); await Idle();
            var home = w.Home!;
            var confirm = Views.AppDialog.ConfirmAsync(home, "selftest", Core.Languages.Loc.T("dlg_delete"), destructive: true);
            await Idle(); await Idle();
            var open = Views.AppDialog.IsOpen(home);
            var focused = System.Windows.Input.FocusManager.GetFocusedElement(home) as System.Windows.Controls.Button;
            var defaultCancel = focused?.Content as string == Core.Languages.Loc.T("dlg_cancel");
            PressKey(home, System.Windows.Input.Key.Escape);
            var cancelled = await Task.WhenAny(confirm, Task.Delay(2000)) == confirm && !confirm.Result && !Views.AppDialog.IsOpen(home);
            // closing with unsaved settings asks first (the window stays open until answered)
            home.ShowSettings("hotkeys"); await Idle();
            home.SettingsPageForTest.HkStart.Text = "Ctrl+Alt+K";
            home.Close(); await Idle(); await Idle();
            var asked = home.IsLoaded && Views.AppDialog.IsOpen(home);
            PressKey(home, System.Windows.Input.Key.Escape); await Idle();   // Cancel = stay
            var stayed = home.IsLoaded && home.SettingsPageForTest.Guard.IsDirty;
            home.SettingsPageForTest.Discard(); home.Close(); await Idle();
            return (open && defaultCancel && cancelled && asked && stayed && !home.IsLoaded, $"overlay {open}; default Cancel {defaultCancel}; Escape → cancel {cancelled}; close asked {asked}, stayed {stayed}");
        });
        await Check("Data export: format choice in the in-app dialog (options with descriptions, first preselected), Escape and Cancel close it", async () =>
        {
            w.OpenHome(); await Idle();
            var home = w.Home!;
            var account = Views.ExportDialog.ChooseAccountFormatAsync(home);
            await Idle(); await Idle();
            var open = Views.AppDialog.IsOpen(home);
            var list = Descendants<Views.ExportChoiceList>(Views.DialogHost.Find(home)?.TopCard).FirstOrDefault();
            var twoOptions = list?.Buttons.Count == 2 && list.SelectedKey == Views.ExportDialog.AccountHtml
                             && list.Buttons.All(b => System.Windows.Automation.AutomationProperties.GetHelpText(b).Length > 0);
            PressKey(home, System.Windows.Input.Key.Escape);
            var escaped = await Task.WhenAny(account, Task.Delay(2000)) == account && account.Result == null && !Views.AppDialog.IsOpen(home);
            var profile = Views.ExportDialog.ChooseProfileFormatAsync(home, "Jane Example");
            await Idle(); await Idle();
            var card = Views.DialogHost.Find(home)?.TopCard;
            var threeOptions = Descendants<Views.ExportChoiceList>(card).FirstOrDefault()?.Buttons.Count == 3;
            var cancel = Descendants<System.Windows.Controls.Button>(card).FirstOrDefault(b => b.Content as string == Core.Languages.Loc.T("dlg_cancel"));
            cancel?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var cancelled = await Task.WhenAny(profile, Task.Delay(2000)) == profile && profile.Result == null && !Views.AppDialog.IsOpen(home);
            home.Close(); await Idle();
            return (open && twoOptions && escaped && threeOptions && cancelled, $"overlay {open}; account options {twoOptions}; Escape closes {escaped}; profile options {threeOptions}; Cancel closes {cancelled}");
        });
        await Check("Data export: readable profile HTML renders; .iaprofile backup round trip imports a new profile (isolated DPAPI workspace)", async () =>
        {
            var root = Path.Combine(AppPaths.Local, "selftest-export-workspace");
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
            var ws = new Workspace(root, new DpapiProtector());
            var profile = new Core.Domain.CandidateProfileRecord { Name = "Jane Example" };
            Core.Domain.ProfileService.AddResume(profile, Core.Domain.ProfileService.CreateDocument("cv.txt", Core.Domain.DocumentKind.Resume,
                "Jane Example\nProduct Manager\nBerlin\n\nEXPERIENCE\nSenior Product Manager — Contoso Payments (2019 – 2024)\n- Launched instant payouts used by 40,000 merchants\n- Reduced checkout drop-off by 18% through A/B testing\n\nSKILLS\nRoadmapping, SQL, Stakeholder management"));
            profile.ConfirmAll();
            Core.Domain.ProfileService.SetPersonal(profile, "hobby", "Sailing");
            ws.Store.SaveProfile(profile);
            var target = new Core.Domain.InterviewTarget { ProfileId = profile.Id, JobTitle = "Product Manager", Company = "Fabrikam" };
            ws.Store.SaveTarget(target);
            ws.Store.SaveSession(new Core.Persistence.StoredSession { ProfileId = profile.Id, TargetId = target.Id, EndedUtc = DateTime.UtcNow });
            var html = Core.Reports.ProfileExportRenderer.Render(profile, "en", Branding.ProductName);
            var htmlOk = html.Contains("Jane Example") && html.Contains("Contoso Payments") && html.Contains("Content-Security-Policy") && html.Contains("Hobbies");
            var bundle = new MemoryStream();
            await Task.Run(() => Core.Persistence.ProfileBundle.Write(ws.Store, profile.Id, bundle, BuildInfo.Version));
            var content = Core.Persistence.ProfileBundle.Read(new MemoryStream(bundle.ToArray()));
            var imported = Core.Persistence.ProfileBundle.Import(ws.Store, content, "Jane Example (imported)");
            var ok = htmlOk && imported.Id != profile.Id && ws.Store.Profiles().Count == 2 && ws.Store.Targets(imported.Id).Count == 1
                     && ws.Store.Targets(profile.Id).Count == 1 && ws.Store.Sessions().Count(x => x.ProfileId == imported.Id) == 1 && content.Preview.Interviews == 1;
            try { Directory.Delete(root, true); } catch (IOException) { }
            return (ok, $"profile HTML {html.Length} chars ({htmlOk}); bundle {bundle.Length} bytes, {content.Manifest.Entries.Count} entries; imported as new profile {imported.Id != profile.Id}");
        });
        await Check("Placeholder: inside the field, never hit-testable, hidden while focused or filled", async () =>
        {
            var box = w.ManualInput;
            box.Text = "";
            if (box.IsKeyboardFocused) System.Windows.Input.Keyboard.ClearFocus();
            await Idle();
            if (box.Template.FindName("Placeholder", box) is not System.Windows.Controls.TextBlock ph) return (false, "template has no placeholder");
            var shownEmpty = ph.Visibility == Visibility.Visible && ph.Text == vm.LTypeHint;
            box.Text = "typed"; await Idle();
            var hiddenFilled = ph.Visibility != Visibility.Visible;
            box.Text = ""; box.Focus(); await Idle();
            var hiddenFocused = !box.IsKeyboardFocused || ph.Visibility != Visibility.Visible;
            System.Windows.Input.Keyboard.ClearFocus();
            return (shownEmpty && hiddenFilled && hiddenFocused && !ph.IsHitTestVisible, $"empty {shownEmpty}; filled hides {hiddenFilled}; focus hides {hiddenFocused}");
        });
        await Check("Live window: question field and send button share one row and one height; prepared answer is not a profile-gap card", async () =>
        {
            await Idle();
            var box = w.ManualInput; var send = w.ManualSendBtn;
            var sameHeight = Math.Abs(box.ActualHeight - send.ActualHeight) < 0.5;
            var boxMid = box.TranslatePoint(new Point(0, box.ActualHeight / 2), w).Y;
            var sendMid = send.TranslatePoint(new Point(0, send.ActualHeight / 2), w).Y;
            var aligned = Math.Abs(boxMid - sendMid) < 1;
            var noGapCard = !vm.IsProfileGap && w.ProfileGapHeader.Visibility != Visibility.Visible && w.AddToProfileBtn.Visibility != Visibility.Visible;
            return (sameHeight && aligned && noGapCard, $"field {box.ActualHeight:0}px, send {send.ActualHeight:0}px, centre offset {Math.Abs(boxMid - sendMid):0.0}px; gap card {(noGapCard ? "hidden" : "SHOWN")}");
        });
        await Check("Home: Profiles segmented sections (arrows switch, one tab stop); Reports two panes or empty state with four actions", async () =>
        {
            var home = new HomeWindow(vm, w) { Owner = w };
            home.Show(); await Idle();
            home.ShowPage("profiles"); await Idle();
            var named = System.Windows.Automation.AutomationProperties.GetName(home.TabFacts).Length > 0 && System.Windows.Automation.AutomationProperties.GetName(home.TabPersonal).Length > 0;
            var equal = Math.Abs(home.TabFacts.ActualWidth - home.TabPersonal.ActualWidth) < 1;
            void Arrow(System.Windows.Input.Key key)
            {
                if (PresentationSource.FromVisual(home) is not { } src) return;
                var target = home.TabFacts.IsChecked == true ? home.TabFacts : home.TabPersonal;
                target.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, src, 0, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            }
            Arrow(System.Windows.Input.Key.Right); await Idle();
            var movedRight = home.ProfileTab == "personal" && home.PersonalPanel.Visibility == Visibility.Visible && home.TabPersonal.IsTabStop && !home.TabFacts.IsTabStop;
            Arrow(System.Windows.Input.Key.Left); await Idle();
            var movedBack = home.ProfileTab == "facts" && home.FactsPanel.Visibility == Visibility.Visible;
            var deleteSeparated = home.DeleteProfileBtn.Style == (Style)home.FindResource("Btn.FormDanger");
            home.ShowPage("reports"); await Idle();
            var any = home.ReportList.Items.Count > 0;
            var states = any ? home.ReportsBody.Visibility == Visibility.Visible && home.ReportsEmptyState.Visibility != Visibility.Visible
                             : home.ReportsEmptyState.Visibility == Visibility.Visible && home.ReportsBody.Visibility != Visibility.Visible;
            var fourActions = new[] { home.OpenReportBtn, home.CopyReportBtn, home.ExportReportBtn, home.DeleteReportBtn }.All(b => b.Content is string s && s.Length > 0);
            var listShare = any ? home.ReportList.ActualWidth / Math.Max(1, home.ReportsBody.ActualWidth) : 0.32;
            home.Close(); await Idle();
            var ok = named && equal && movedRight && movedBack && deleteSeparated && states && fourActions && listShare is > 0.2 and < 0.5;
            return (ok, $"segments named {named}, equal {equal}, → {movedRight}, ← {movedBack}; delete destructive {deleteSeparated}; reports {(any ? "two panes" : "empty state")} {states}; actions {fourActions}; list share {listShare:P0}");
        });
        await Check("Pre-interview window XAML + local checks", async () =>
        {
            var r = new ReadinessWindow(vm, w, autoRun: false) { Owner = w };
            r.Show(); await Idle();
            r.CheckLocal(); await Idle();
            var ok = r.IsLoaded; var verdict = r.Verdict; r.Close();
            return (ok, $"opened, local checks ran ({verdict}), closed");
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

    private static readonly string[] HomePages = { "start", "profiles", "interviews", "reports", "account", "settings", "help" };

    /// <summary>Every element of type <typeparamref name="T"/> below <paramref name="root"/> (logical tree, e.g. inside a dialog card).</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) yield break;
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is T t) yield return t;
            foreach (var c in LogicalTreeHelper.GetChildren(d)) if (c is DependencyObject child) stack.Push(child);
        }
    }

    /// <summary>Sends a key to the focused element of <paramref name="window"/> (tunnelling, like a real key press).</summary>
    private static void PressKey(Window window, System.Windows.Input.Key key)
    {
        if (PresentationSource.FromVisual(window) is not { } source) return;
        var target = System.Windows.Input.FocusManager.GetFocusedElement(window) as UIElement ?? window;
        // An open dialog owns the keyboard: without OS focus (CI desktop) send the key through its card.
        if (Views.DialogHost.Find(window)?.TopCard is { } card && !card.IsAncestorOf(target)) target = card;
        target.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
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
