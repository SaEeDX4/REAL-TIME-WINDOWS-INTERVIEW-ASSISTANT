using InterviewAssistant.App.UiState;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC5 readiness: one authoritative snapshot drives the pre-interview page, Get started, the Interviews summary and the
/// global indicator; Get started offers exactly one next action; deep links resolve before a page is shown.
/// </summary>
public class Rc5ReadinessTests
{
    private const string Ctx = "user|target|device";

    private static ReadinessStore Run(params (ReadinessCheckId Id, CheckOutcome Outcome)[] results)
    {
        var store = new ReadinessStore(() => new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
        store.Begin(Ctx);
        foreach (var id in Enum.GetValues<ReadinessCheckId>())
        {
            var r = results.FirstOrDefault(x => x.Id == id);
            store.Report(new ReadinessCheckResult(id, results.Any(x => x.Id == id) ? r.Outcome : CheckOutcome.Pass));
        }
        return store;
    }

    [Fact]
    public void AllPass_IsReady_EightOfEight()
    {
        var s = Run().Snapshot;
        Assert.Equal(ReadinessState.Ready, s.StateFor(Ctx));
        Assert.Equal(8, s.Passed);
        Assert.Equal(8, s.Total);
        Assert.NotNull(s.CheckedAt);
    }

    [Fact]
    public void OneWarning_IsReadyWithWarnings_SevenOfEight()
    {
        var s = Run((ReadinessCheckId.WindowOnTop, CheckOutcome.Warning)).Snapshot;
        Assert.Equal(ReadinessState.ReadyWithWarnings, s.StateFor(Ctx));
        Assert.Equal(7, s.Passed);
        Assert.Equal(1, s.Warnings);
    }

    [Fact]
    public void OneFailure_IsNotReady()
    {
        var s = Run((ReadinessCheckId.AccountPlan, CheckOutcome.Fail), (ReadinessCheckId.WindowOnTop, CheckOutcome.Warning)).Snapshot;
        Assert.Equal(ReadinessState.NotReady, s.StateFor(Ctx));
        Assert.Equal(6, s.Passed);
    }

    [Fact]
    public void Running_IsChecking_AndHasNoTimestamp()
    {
        var store = new ReadinessStore();
        store.Begin(Ctx);
        store.Report(new ReadinessCheckResult(ReadinessCheckId.PlaybackDevice, CheckOutcome.Pass));
        Assert.Equal(ReadinessState.Checking, store.Snapshot.StateFor(Ctx));
        Assert.Null(store.Snapshot.CheckedAt);
    }

    [Fact]
    public void AnotherInterviewOrDevice_MakesTheResultOutdated()
    {
        var s = Run().Snapshot;
        Assert.Equal(ReadinessState.Outdated, s.StateFor("user|other-target|device"));
        Assert.Equal(ReadinessState.Outdated, s.StateFor(ReadinessStore.ContextOf("user", "target", "headset")));
    }

    [Fact]
    public void NothingRun_IsNotChecked_AndClearForgetsTheResult()
    {
        Assert.Equal(ReadinessState.NotChecked, ReadinessSnapshot.Empty.StateFor(Ctx));
        var store = Run();
        store.Clear();
        Assert.Equal(ReadinessState.NotChecked, store.Snapshot.StateFor(Ctx));
    }

    [Fact]
    public void ALaterResult_ReplacesTheEarlierOne_AndEverySubscriberSeesTheSameSnapshot()
    {
        var store = Run((ReadinessCheckId.AudioSignal, CheckOutcome.Warning));
        ReadinessSnapshot? seenByPage = null, seenByIndicator = null;
        store.Changed += s => seenByPage = s;
        store.Changed += s => seenByIndicator = s;
        store.Report(new ReadinessCheckResult(ReadinessCheckId.AudioSignal, CheckOutcome.Pass, "heard -18 dB"));
        Assert.Same(seenByPage, seenByIndicator);
        Assert.Equal(ReadinessState.Ready, seenByPage!.StateFor(Ctx));
        Assert.Single(seenByPage.Checks, c => c.Id == ReadinessCheckId.AudioSignal);
    }

    [Fact]
    public void CloudTranscriptionNotTestedYet_IsStillReady_AndTheSummarySaysWhatIsOptional()
    {
        // rc.5 UX audit H1: transcription can only be proven with real meeting audio, so it is an optional test —
        // a fully working account setup reaches "Ready", never a permanent "Ready with warnings".
        var s = Run((ReadinessCheckId.Transcription, CheckOutcome.NotTested)).Snapshot;
        Assert.Equal(ReadinessState.Ready, s.StateFor(Ctx));
        Assert.Equal(7, s.Passed);
        Assert.Equal(7, s.Required);
        var (key, args) = ReadinessPresentation.Summary(s, Ctx);
        Assert.Equal("rd_summary_passed_untested", key);
        Assert.Equal(new object[] { 7, 7, 1 }, args);
        Assert.Single(ReadinessPresentation.NotTestedYet(s));
        Assert.Empty(ReadinessPresentation.Warnings(s));
        Assert.Equal("rd_status_not_tested", ReadinessPresentation.StatusKey(CheckOutcome.NotTested));
    }

    [Fact]
    public void NotTestedNextToAFailure_IsStillNotReady()
    {
        var s = Run((ReadinessCheckId.Transcription, CheckOutcome.NotTested), (ReadinessCheckId.AccountPlan, CheckOutcome.Fail)).Snapshot;
        Assert.Equal(ReadinessState.NotReady, s.StateFor(Ctx));
    }

    [Fact]
    public void AllPassed_SummaryIsNOfEight()
    {
        var (key, args) = ReadinessPresentation.Summary(Run().Snapshot, Ctx);
        Assert.Equal("rd_summary_passed", key);
        Assert.Equal(new object[] { 8, 8 }, args);
        // a result for something else never shows its numbers
        Assert.Equal(new object[] { 0, 8 }, ReadinessPresentation.Summary(Run().Snapshot, "other").Args);
    }

    [Fact]
    public void AnOldResult_IsOutOfDate()
    {
        // rc.5 UX audit H3: "Ready" from yesterday morning says nothing about this meeting.
        var s = Run().Snapshot;
        var checkedAt = s.CheckedAt!.Value;
        Assert.Equal(ReadinessState.Ready, s.StateFor(Ctx, checkedAt + TimeSpan.FromMinutes(29)));
        Assert.Equal(ReadinessState.Outdated, s.StateFor(Ctx, checkedAt + ReadinessSnapshot.MaxAge + TimeSpan.FromSeconds(1)));
        Assert.True(ReadinessPresentation.ShouldAutoRun(ReadinessState.Outdated));
    }

    [Fact]
    public void SavedSettingsOrProfile_MakeTheResultOutOfDate_ThroughTheRevision()
    {
        var before = ReadinessStore.ContextOf("user", "target", "device", revision: 3);
        var after = ReadinessStore.ContextOf("user", "target", "device", revision: 4);
        Assert.NotEqual(before, after);
        var store = new ReadinessStore();
        store.Begin(before);
        foreach (var id in Enum.GetValues<ReadinessCheckId>()) store.Report(new ReadinessCheckResult(id, CheckOutcome.Pass));
        Assert.Equal(ReadinessState.Ready, store.Snapshot.StateFor(before));
        Assert.Equal(ReadinessState.Outdated, store.Snapshot.StateFor(after));
        ReadinessSnapshot? seen = null;
        store.Changed += x => seen = x;
        store.Touch();   // surfaces re-render with the new context
        Assert.Same(store.Snapshot, seen);
    }

    [Fact]
    public void ConcurrentReports_AreNeverLost()
    {
        // error-state audit L1: probes report from thread-pool continuations
        var store = new ReadinessStore();
        store.Begin(Ctx);
        Parallel.ForEach(Enum.GetValues<ReadinessCheckId>(), id => store.Report(new ReadinessCheckResult(id, CheckOutcome.Pass)));
        Assert.Equal(8, store.Snapshot.Passed);
    }

    [Fact]
    public void GetStarted_CheckStepIsNeverDone_BeforeTheSetupItChecks()
    {
        // a "Ready" result from earlier must not tick step 4 while step 3 (prepare) is still open
        var f = Facts(prepared: false, readiness: ReadinessState.Ready);
        Assert.False(SetupPlanner.IsDone(SetupStep.PreInterviewCheck, f));
        Assert.Equal(StepStatus.Upcoming, SetupPlanner.Steps(f).Single(s => s.Step == SetupStep.PreInterviewCheck).Status);
    }

    [Theory]
    [InlineData(ReadinessState.Ready, "rd_state_ready")] [InlineData(ReadinessState.ReadyWithWarnings, "rd_state_warnings")]
    [InlineData(ReadinessState.NotReady, "rd_state_not_ready")] [InlineData(ReadinessState.NotChecked, "rd_state_not_checked")]
    [InlineData(ReadinessState.Outdated, "rd_state_outdated")] [InlineData(ReadinessState.Checking, "rd_state_checking")]
    public void EveryStateHasALabel(ReadinessState state, string key) => Assert.Equal(key, ReadinessSnapshot.StateKey(state));

    // ---------------- Get started ----------------

    private static SetupFacts Facts(bool account = true, bool profile = true, bool facts = true, bool interview = true, bool prepared = true,
        ReadinessState readiness = ReadinessState.Ready) => new(account, profile, facts, interview, prepared, readiness);

    [Theory]
    [InlineData(false, true, true, true, true, ReadinessState.Ready, SetupAction.SignIn)]
    [InlineData(true, false, false, false, false, ReadinessState.NotChecked, SetupAction.CompleteProfile)]
    [InlineData(true, true, false, false, false, ReadinessState.NotChecked, SetupAction.CompleteProfile)]
    [InlineData(true, true, true, false, false, ReadinessState.NotChecked, SetupAction.CreateInterview)]
    [InlineData(true, true, true, true, false, ReadinessState.NotChecked, SetupAction.PrepareInterview)]
    [InlineData(true, true, true, true, true, ReadinessState.NotChecked, SetupAction.RunPreInterviewCheck)]
    [InlineData(true, true, true, true, true, ReadinessState.Outdated, SetupAction.RunPreInterviewCheck)]
    [InlineData(true, true, true, true, true, ReadinessState.NotReady, SetupAction.RunPreInterviewCheck)]
    [InlineData(true, true, true, true, true, ReadinessState.ReadyWithWarnings, SetupAction.StartInterview)]
    [InlineData(true, true, true, true, true, ReadinessState.Ready, SetupAction.StartInterview)]
    // rc.5 UX audit M10: prepared knowingly without confirmed facts → the button no longer sticks on "Complete profile"
    [InlineData(true, true, false, true, true, ReadinessState.Ready, SetupAction.StartInterview)]
    [InlineData(true, true, false, true, false, ReadinessState.NotChecked, SetupAction.CompleteProfile)]
    public void GetStarted_OffersExactlyTheNextAction(bool account, bool profile, bool facts, bool interview, bool prepared, ReadinessState r, SetupAction expected) =>
        Assert.Equal(expected, SetupPlanner.PrimaryAction(Facts(account, profile, facts, interview, prepared, r)));

    [Fact]
    public void GetStarted_Steps_OneCurrent_DoneBeforeIt()
    {
        var steps = SetupPlanner.Steps(Facts(interview: false, prepared: false, readiness: ReadinessState.NotChecked));
        Assert.Equal(new[] { StepStatus.Done, StepStatus.Done, StepStatus.Current, StepStatus.Upcoming }, steps.Select(s => s.Status));
        Assert.Single(steps, s => s.Status == StepStatus.Current);
    }

    [Fact]
    public void GetStarted_SignedOut_NothingAfterAccountIsDone()
    {
        // Local data of a signed-out user is not shown, so it cannot count as a finished step either.
        var steps = SetupPlanner.Steps(Facts(account: false));
        Assert.Equal(StepStatus.Current, steps[0].Status);
        Assert.All(steps.Skip(1).Take(2), s => Assert.NotEqual(StepStatus.Done, s.Status));
    }

    [Fact]
    public void GetStarted_AllDone_NoCurrentStep_StartInterview()
    {
        var f = Facts();
        Assert.All(SetupPlanner.Steps(f), s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.Equal("gs_action_start", SetupPlanner.ActionKey(SetupPlanner.PrimaryAction(f)));
    }

    // ---------------- deep links ----------------

    [Theory]
    [InlineData("settings/audio", "settings", "audio")]
    [InlineData("Settings/Hotkeys", "settings", "hotkeys")]
    [InlineData("preinterview", "preinterview", null)]
    [InlineData("account", "account", null)]
    [InlineData("profiles/personal", "profiles", "personal")]
    [InlineData("nonsense/x", "start", null)]
    [InlineData("", "start", null)]
    [InlineData(null, "start", null)]
    public void Route_Parse(string? route, string page, string? section)
    {
        var r = HomeRoute.Parse(route);
        Assert.Equal(page, r.Page);
        Assert.Equal(section, r.Section);
    }

    [Theory]
    [InlineData("profiles", true, "account")] [InlineData("interviews", true, "account")] [InlineData("reports", true, "account")]
    [InlineData("settings", true, "settings")] [InlineData("preinterview", true, "preinterview")] [InlineData("profiles", false, "profiles")]
    public void Route_SignedOutGate(string page, bool signedOut, string expected) =>
        Assert.Equal(expected, new HomeRoute(page).Gate(signedOut).Page);
}
