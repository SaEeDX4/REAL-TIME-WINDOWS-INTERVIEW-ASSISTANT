using System.Text.Json;
using System.Text.RegularExpressions;
using InterviewAssistant.App.UiState;
using InterviewAssistant.Core.Languages;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// Desktop UI foundation (2.1.0-rc.4): the unsaved-changes guard (a language switch is never navigation), independently
/// loaded Account sections, the pre-interview verdict rule, and source/copy rules for the desktop app.
/// The logic classes live in src/InterviewAssistant.App/UiState and are compiled into this test project (no WPF).
/// </summary>
public class UiFoundationTests
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

    // ---------------- dirty-state guard ----------------

    private sealed class Asker
    {
        public LeaveDecision Answer { get; set; } = LeaveDecision.Stay;
        public List<(string Guard, ChangeReason Reason)> Questions { get; } = new();
        public Task<LeaveDecision> Ask(DirtyStateGuard g, ChangeReason r) { Questions.Add((g.Name, r)); return Task.FromResult(Answer); }
    }

    [Theory]
    [InlineData(ChangeReason.LanguageChange)]
    [InlineData(ChangeReason.Resize)]
    [InlineData(ChangeReason.ThemeChange)]
    [InlineData(ChangeReason.ResourceRefresh)]
    [InlineData(ChangeReason.ListRefresh)]
    public async Task PresentationRefreshes_NeverAsk_AndKeepTheEdits(ChangeReason reason)
    {
        var asker = new Asker();
        var nav = new NavigationGuard(asker.Ask);
        var profile = nav.Register(new DirtyStateGuard("profile"));
        profile.MarkDirty();
        Assert.True(await nav.CanProceedAsync(reason));
        Assert.Empty(asker.Questions);
        Assert.True(profile.IsDirty);   // nothing saved, nothing discarded
        Assert.False(profile.RequiresConfirmation(reason));
    }

    [Theory]
    [InlineData(ChangeReason.Navigate)]
    [InlineData(ChangeReason.SwitchItem)]
    [InlineData(ChangeReason.CloseWindow)]
    [InlineData(ChangeReason.SignOut)]
    [InlineData(ChangeReason.ReloadData)]
    public async Task RealDataLossActions_AskAndStayWhenTheUserCancels(ChangeReason reason)
    {
        var asker = new Asker { Answer = LeaveDecision.Stay };
        var nav = new NavigationGuard(asker.Ask);
        var profile = nav.Register(new DirtyStateGuard("profile"));
        profile.MarkDirty();
        Assert.False(await nav.CanProceedAsync(reason));
        Assert.Equal(new[] { ("profile", reason) }, asker.Questions);
        Assert.True(profile.IsDirty);
    }

    [Fact]
    public async Task CleanEditors_NeverAsk()
    {
        var asker = new Asker();
        var nav = new NavigationGuard(asker.Ask);
        nav.Register(new DirtyStateGuard("profile"));
        Assert.True(await nav.CanProceedAsync(ChangeReason.Navigate));
        Assert.Empty(asker.Questions);
    }

    [Fact]
    public async Task SaveChoice_SavesThenProceeds_FailedValidationStays()
    {
        var asker = new Asker { Answer = LeaveDecision.Save };
        var nav = new NavigationGuard(asker.Ask);
        var saves = 0;
        var settings = nav.Register(new DirtyStateGuard("settings") { Save = () => { saves++; return saves > 1; } });
        settings.MarkDirty();
        Assert.False(await nav.CanProceedAsync(ChangeReason.Navigate));   // validation failed: stay, still dirty
        Assert.True(settings.IsDirty);
        Assert.True(await nav.CanProceedAsync(ChangeReason.Navigate));
        Assert.False(settings.IsDirty);
        Assert.Equal(2, saves);
    }

    [Fact]
    public async Task DiscardChoice_RevertsSideEffectsAndProceeds()
    {
        var asker = new Asker { Answer = LeaveDecision.Discard };
        var nav = new NavigationGuard(asker.Ask);
        var reverted = false;
        var settings = nav.Register(new DirtyStateGuard("settings") { Discard = () => reverted = true });
        settings.MarkDirty();
        Assert.True(await nav.CanProceedAsync(ChangeReason.CloseWindow));
        Assert.True(reverted);
        Assert.False(settings.IsDirty);
    }

    [Fact]
    public async Task ScopedQuestion_OnlyConcernsTheAffectedEditors()
    {
        var asker = new Asker { Answer = LeaveDecision.Stay };
        var nav = new NavigationGuard(asker.Ask);
        var profile = nav.Register(new DirtyStateGuard("profile"));
        var settings = nav.Register(new DirtyStateGuard("settings"));
        settings.MarkDirty();
        Assert.True(await nav.CanProceedAsync(ChangeReason.SwitchItem, profile));   // switching profile: settings edits are unaffected
        Assert.Empty(asker.Questions);
        Assert.False(await nav.CanProceedAsync(ChangeReason.CloseWindow));          // closing: everything is affected
        Assert.Single(asker.Questions);
    }

    [Fact]
    public void ProgrammaticUpdates_NeitherMarkNorClearDirty()
    {
        var g = new DirtyStateGuard("profile");
        var flips = 0;
        g.DirtyChanged += () => flips++;
        using (g.Suspend()) g.MarkDirty();   // loading a record fills its fields
        Assert.False(g.IsDirty);
        g.MarkDirty();                       // the user types
        using (g.Suspend()) g.MarkDirty();   // re-rendering after a language switch rewrites labels and pickers
        Assert.True(g.IsDirty);
        Assert.Equal(1, flips);
        Assert.False(g.IsSuspended);
    }

    [Fact]
    public async Task LanguageSwitchWhileEditingAProfile_DoesNotAskAndKeepsTheDraft()
    {
        // The confirmed rc.3 bug: switching the UI language with unsaved profile edits showed "Discard them?".
        var asker = new Asker();
        var nav = new NavigationGuard(asker.Ask);
        var profile = nav.Register(new DirtyStateGuard("profile"));
        var draft = "Senior PM";
        profile.MarkDirty();
        // Home re-renders: the list is rebuilt and re-selects the profile being edited, field labels change.
        using (profile.Suspend())
        {
            Assert.Equal(ChangeReason.ListRefresh, DirtyStateGuard.ClassifySelection("p1", "p1", programmatic: false));
            Assert.Equal(ChangeReason.ListRefresh, DirtyStateGuard.ClassifySelection("p1", null, programmatic: true));
            Assert.True(await nav.CanProceedAsync(ChangeReason.LanguageChange));
        }
        Assert.Empty(asker.Questions);
        Assert.True(profile.IsDirty);
        Assert.Equal("Senior PM", draft);
        // Picking another profile afterwards is a real switch and asks.
        Assert.Equal(ChangeReason.SwitchItem, DirtyStateGuard.ClassifySelection("p1", "p2", programmatic: false));
        Assert.False(await nav.CanProceedAsync(ChangeReason.SwitchItem, profile));
        Assert.Single(asker.Questions);
    }

    [Fact]
    public async Task OnlyOneQuestionAtATime()
    {
        var gate = new TaskCompletionSource<LeaveDecision>();
        var asked = 0;
        var nav = new NavigationGuard((_, _) => { asked++; return gate.Task; });
        nav.Register(new DirtyStateGuard("profile")).MarkDirty();
        var first = nav.CanProceedAsync(ChangeReason.CloseWindow);
        Assert.False(await nav.CanProceedAsync(ChangeReason.Navigate));   // a second request while the question is open is refused
        gate.SetResult(LeaveDecision.Discard);
        Assert.True(await first);
        Assert.Equal(1, asked);
    }

    // ---------------- account sections ----------------

    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 14, 5, 0, TimeSpan.Zero);
    private static string Hm(DateTimeOffset t) => t.ToString("HH:mm");

    [Fact]
    public void OneSectionTimingOut_DoesNotFailTheOthers()
    {
        var s = new AccountSections();
        s.Summary.Loaded(T0);                      // plan and quota loaded at sign-in
        var d = s.Devices.Begin();
        Assert.True(s.Devices.Fail(d, "That took too long. Please try again."));
        Assert.Equal(SectionStatus.Ready, s.Summary.Status);
        Assert.Null(s.Summary.Error);
        Assert.Equal(SectionStatus.Failed, s.Devices.Status);
        Assert.Equal(SectionStatus.NotLoaded, s.Billing.Status);
        Assert.False(s.Devices.IsStale);           // never loaded: an error, not "saved data"
    }

    [Fact]
    public void RetrySuccess_ClearsTheErrorAutomatically()
    {
        var devices = new SectionState("devices");
        devices.Succeed(devices.Begin(), T0);
        devices.Fail(devices.Begin(), "timeout");
        Assert.True(devices.IsStale);
        var retry = devices.Begin();
        Assert.True(devices.IsLoading);
        Assert.True(devices.Succeed(retry, T0.AddMinutes(1)));
        Assert.Equal(SectionStatus.Ready, devices.Status);
        Assert.Null(devices.Error);
        Assert.False(devices.IsStale);
        Assert.Equal(T0.AddMinutes(1), devices.LoadedAt);
    }

    [Fact]
    public void StaleData_IsLabelledWithTheTimeItWasLoaded()
    {
        var before = Loc.Language;
        try
        {
            Loc.Language = "en";
            var summary = new SectionState("summary");
            summary.Succeed(summary.Begin(), T0);
            Assert.Null(summary.StaleNote(Hm));
            summary.Fail(summary.Begin(), "timeout");
            Assert.Equal("Showing saved data from 14:05.", summary.StaleNote(Hm));
            Loc.Language = "de";
            Assert.Contains("14:05", summary.StaleNote(Hm));
        }
        finally { Loc.Language = before; }
    }

    [Fact]
    public void LateResponses_AreIgnored_AndSignOutDropsEverything()
    {
        var devices = new SectionState("devices");
        var slow = devices.Begin();
        var fast = devices.Begin();
        Assert.True(devices.Succeed(fast, T0));
        Assert.False(devices.Fail(slow, "timeout"));     // an older request that times out later cannot mark fresh data as failed
        Assert.Equal(SectionStatus.Ready, devices.Status);
        var pending = devices.Begin();
        devices.Reset();                                  // signed out meanwhile
        Assert.False(devices.Succeed(pending, T0));
        Assert.False(devices.HasData);
        Assert.Equal(SectionStatus.NotLoaded, devices.Status);
    }

    // ---------------- pre-interview verdict ----------------

    [Fact]
    public void Verdict_OnlyFailBlocks()
    {
        Assert.Equal(VerdictKind.Ready, ReadinessVerdict.Evaluate(new[] { CheckOutcome.Pass, CheckOutcome.Pass }));
        Assert.Equal(VerdictKind.ReadyWithWarnings, ReadinessVerdict.Evaluate(new[] { CheckOutcome.Pass, CheckOutcome.Warning, CheckOutcome.Warning }));
        Assert.Equal(VerdictKind.NotReady, ReadinessVerdict.Evaluate(new[] { CheckOutcome.Pass, CheckOutcome.Warning, CheckOutcome.Fail }));
        Assert.Equal(VerdictKind.Checking, ReadinessVerdict.Evaluate(new[] { CheckOutcome.Pending, CheckOutcome.Fail }));
    }

    [Fact]
    public void PreflightCopy_MatchesTheVerdictRule()
    {
        var en = Loc.TableFor("en");
        // rc.5 UX audit M6/M12 copy: sentence-case badges; a warning does not block, a failed check must be fixed.
        Assert.Contains("a warning does not stop you from starting", en["pf_subtitle"]);
        Assert.Contains("Fix anything marked " + en["pf_fail"], en["pf_subtitle"]);
        Assert.StartsWith("READY", en["pf_ready_warnings"]);
    }

    // ---------------- source and copy rules ----------------

    private static IEnumerable<string> AppSources(params string[] patterns) =>
        patterns.SelectMany(p => Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "InterviewAssistant.App"), p, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void DesktopApp_NeverUsesTheNativeMessageBox()
    {
        var offenders = AppSources("*.cs", "*.xaml")
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, l, i)))
            .Where(x => Regex.IsMatch(x.l, @"\bMessageBox\s*\.\s*Show\b|\bMessageBoxButton\b|\bMessageBoxResult\b"))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.l.Trim()}").ToList();
        Assert.True(offenders.Count == 0, "Use the in-app dialog (Views/AppDialog.cs) instead of MessageBox:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void SettingsIsAPage_NotASeparateWindow()
    {
        Assert.Empty(AppSources("SettingsWindow*"));
        Assert.DoesNotContain(AppSources("*.cs"), f => File.ReadAllText(f).Contains("new SettingsWindow("));
    }

    [Fact]
    public void FormControls_UseTheSharedStyles_NoAdHocWatermarkOverlays()
    {
        foreach (var f in AppSources("*.xaml").Where(f => !f.EndsWith("Theme.xaml")))
        {
            var xaml = File.ReadAllText(f);
            // the old overlay pattern: a TextBlock whose visibility follows a TextBox's Text
            Assert.DoesNotMatch(@"Binding\s+Text,\s*ElementName=", xaml);
            // inputs take the implicit shared style (or a named member of the family), never a private template
            Assert.DoesNotMatch(@"<(TextBox|PasswordBox|ComboBox)\b[^>]*\bTemplate=", xaml);
        }
    }

    [Fact]
    public void EnglishCopy_SaysResume_WithoutAccents()
    {
        var en = File.ReadAllText(Path.Combine(RepoRoot, "src", "InterviewAssistant.Core", "Languages", "i18n", "en.json"));
        Assert.DoesNotContain("résumé", en, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resumé", en, StringComparison.OrdinalIgnoreCase);
        var table = JsonSerializer.Deserialize<Dictionary<string, string>>(en)!;
        Assert.DoesNotContain(table, kv => Regex.IsMatch(kv.Value, @"\bCVs?\b"));
        Assert.Contains("resume", table["import_resume"], StringComparison.OrdinalIgnoreCase);
        // hard-coded English strings in the desktop app (tooltips, accessibility names, logs) follow the same rule
        var literal = new Regex(@"""[^""\n]*résumé[^""\n]*""", RegexOptions.IgnoreCase);
        Assert.DoesNotContain(AppSources("*.cs", "*.xaml"), f => literal.IsMatch(File.ReadAllText(f)));
    }

    [Fact]
    public void PasswordResetCopy_IsNeutral_InEveryLanguage()
    {
        Assert.Equal("If an account exists for this e-mail, a password reset link has been sent.", Loc.TableFor("en")["acct_reset_requested"]);
        Assert.False(Loc.Has("acct_reset_sent"));   // the old copy claimed delivery to a specific address
        foreach (var lang in LanguageRegistry.All.Select(l => l.Code))
            Assert.DoesNotContain("{0}", Loc.TableFor(lang)["acct_reset_requested"]);   // never echoes the address
    }

    [Fact]
    public void AboutLabels_AreTranslated()
    {
        var keys = new[] { "about_product", "about_version", "about_channel", "about_build", "about_install", "about_signing", "about_service" };
        foreach (var lang in new[] { "fa", "ar", "zh", "hi" })
            foreach (var k in keys)
                Assert.NotEqual(Loc.TableFor("en")[k], Loc.TableFor(lang)[k]);
        var home = File.ReadAllText(Path.Combine(RepoRoot, "src", "InterviewAssistant.App", "HomeWindow.xaml"));
        foreach (var k in keys) Assert.Contains($"{{v:Loc {k}}}", home);   // bound in XAML: follows a live language switch
    }
}
