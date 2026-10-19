namespace InterviewAssistant.App.UiState;

/// <summary>The eight pre-interview checks, in the order the page shows them.</summary>
public enum ReadinessCheckId { PlaybackDevice, AudioSignal, AccountPlan, Transcription, AnswerEngine, InterviewPrepared, PreparedAnswers, WindowOnTop }

/// <summary>The result of one check: outcome, a plain detail line (already localized) and the fix it offers, if any.</summary>
public sealed record ReadinessCheckResult(ReadinessCheckId Id, CheckOutcome Outcome, string Detail = "", ReadinessFix Fix = ReadinessFix.None);

/// <summary>What the action button of a check does (the page maps it to a localized label and a route).</summary>
public enum ReadinessFix { None, OpenAudioSettings, TestAudio, SignIn, OpenAccount, TestTranscription, TestAi, OpenInterviews, PrepareInterview, OpenWindowSettings }

/// <summary>Overall state shown by every readiness surface (page summary, Get started, Interviews, the global indicator).</summary>
public enum ReadinessState { NotChecked, Checking, Ready, ReadyWithWarnings, NotReady, Outdated }

/// <summary>
/// One authoritative readiness snapshot. Every surface renders this record — none of them computes readiness on its
/// own, so they can never disagree. <see cref="Context"/> is what the checks were run for (account, interview, audio
/// device); when that changes the snapshot is <see cref="ReadinessState.Outdated"/> until checked again.
/// </summary>
public sealed record ReadinessSnapshot(IReadOnlyList<ReadinessCheckResult> Checks, string Context, DateTimeOffset? CheckedAt)
{
    public static ReadinessSnapshot Empty { get; } = new(Array.Empty<ReadinessCheckResult>(), "", null);

    public int Total => Enum.GetValues<ReadinessCheckId>().Length;
    public int Passed => Checks.Count(c => c.Outcome == CheckOutcome.Pass);
    public int Warnings => Checks.Count(c => c.Outcome == CheckOutcome.Warning);
    public int Failed => Checks.Count(c => c.Outcome == CheckOutcome.Fail);
    /// <summary>Optional tests the user has not run yet (neither passed nor warning).</summary>
    public int NotTested => Checks.Count(c => c.Outcome == CheckOutcome.NotTested);
    /// <summary>The checks that count for "n of m checks passed" (all of them minus the optional tests not run).</summary>
    public int Required => Total - NotTested;

    /// <summary>A result older than this is "Out of date": the meeting, the network or the plan may have changed since.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    public bool IsRunning => Checks.Any(c => c.Outcome == CheckOutcome.Pending);

    public ReadinessCheckResult? Get(ReadinessCheckId id) => Checks.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// The state for <paramref name="currentContext"/> (what is selected now). With <paramref name="now"/>, a finished
    /// result older than <see cref="MaxAge"/> is <see cref="ReadinessState.Outdated"/>.
    /// </summary>
    public ReadinessState StateFor(string currentContext, DateTimeOffset? now = null)
    {
        if (Checks.Count == 0) return ReadinessState.NotChecked;
        if (IsRunning) return ReadinessState.Checking;
        if (!string.Equals(Context, currentContext, StringComparison.Ordinal)) return ReadinessState.Outdated;
        if (now is { } t && CheckedAt is { } at && t - at > MaxAge) return ReadinessState.Outdated;
        if (Checks.Count < Total) return ReadinessState.NotChecked;
        return ReadinessVerdict.Evaluate(Checks.Select(c => c.Outcome)) switch
        {
            VerdictKind.Ready => ReadinessState.Ready,
            VerdictKind.ReadyWithWarnings => ReadinessState.ReadyWithWarnings,
            VerdictKind.NotReady => ReadinessState.NotReady,
            _ => ReadinessState.Checking,
        };
    }

    /// <summary>Localization key of the state's label ("Ready", "Ready with warnings", …).</summary>
    public static string StateKey(ReadinessState s) => s switch
    {
        ReadinessState.Ready => "rd_state_ready",
        ReadinessState.ReadyWithWarnings => "rd_state_warnings",
        ReadinessState.NotReady => "rd_state_not_ready",
        ReadinessState.Checking => "rd_state_checking",
        ReadinessState.Outdated => "rd_state_outdated",
        _ => "rd_state_not_checked",
    };

    /// <summary>The design-token brush key that colours the state (badge, indicator dot).</summary>
    public static string BrushKey(ReadinessState s) => s switch
    {
        ReadinessState.Ready => "B.Listening",
        ReadinessState.ReadyWithWarnings => "B.Thinking",
        ReadinessState.NotReady => "B.Error",
        ReadinessState.Checking => "B.Accent",
        _ => "B.TextFaint",
    };
}

/// <summary>
/// Holds the latest readiness snapshot for the whole app (owned by the view model, one instance). The pre-interview
/// page writes check results; everything else only reads <see cref="Snapshot"/> and listens to <see cref="Changed"/>.
/// </summary>
public sealed class ReadinessStore
{
    private readonly object _gate = new();
    private ReadinessSnapshot _snapshot = ReadinessSnapshot.Empty;
    private readonly Func<DateTimeOffset> _now;

    public ReadinessStore(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.Now);

    public ReadinessSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    /// <summary>Raised on the writer's thread after every change, with the new snapshot.</summary>
    public event Action<ReadinessSnapshot>? Changed;

    /// <summary>A run begins for <paramref name="context"/>: every check is pending.</summary>
    public void Begin(string context)
    {
        var checks = Enum.GetValues<ReadinessCheckId>().Select(id => new ReadinessCheckResult(id, CheckOutcome.Pending)).ToList();
        Publish(new ReadinessSnapshot(checks, context, null));
    }

    /// <summary>Records one check result (a later result for the same check replaces the earlier one).</summary>
    public void Report(ReadinessCheckResult result)
    {
        lock (_publishGate)   // read-modify-write as one step: two writers never lose each other's result
        {
            ReadinessSnapshot next;
            lock (_gate)
            {
                var list = _snapshot.Checks.Where(c => c.Id != result.Id).Append(result).OrderBy(c => c.Id).ToList();
                var done = list.Count == _snapshot.Total && list.All(c => c.Outcome != CheckOutcome.Pending);
                next = _snapshot with { Checks = list, CheckedAt = done ? _now() : _snapshot.CheckedAt };
            }
            Publish(next);
        }
    }

    /// <summary>Forgets every result (sign-out: readiness belonged to the previous account).</summary>
    public void Clear() => Publish(ReadinessSnapshot.Empty);

    /// <summary>
    /// What the checks depend on changed (settings saved, interview re-prepared, profile saved): every surface
    /// re-renders, and a result for the old revision now reads "Out of date".
    /// </summary>
    public void Touch() => Publish(Snapshot);

    private readonly object _publishGate = new();

    private void Publish(ReadinessSnapshot s)
    {
        // Writers are serialized and listeners receive the snapshots in the order they were stored.
        lock (_publishGate)
        {
            lock (_gate) _snapshot = s;
            Changed?.Invoke(s);
        }
    }

    /// <summary>
    /// The context string the checks are bound to: who, which interview, which audio device, and the revision of what
    /// else they depend on (prepared answers, settings) — a change of any part makes a result "Out of date".
    /// </summary>
    public static string ContextOf(string? accountId, string? targetId, string? deviceId, int revision = 0) => $"{accountId}|{targetId}|{deviceId}|{revision}";
}

/// <summary>Steps of the Get started dashboard.</summary>
public enum SetupStep { Account, Profile, InterviewSetup, PreInterviewCheck }

public enum StepStatus { Done, Current, Upcoming }

/// <summary>The one primary action Get started offers, from the first unfinished step.</summary>
public enum SetupAction { SignIn, CompleteProfile, CreateInterview, PrepareInterview, RunPreInterviewCheck, StartInterview }

/// <summary>What the dashboard knows about the user's setup (no UI types: unit-tested).</summary>
public sealed record SetupFacts(
    bool AccountReady,
    bool HasProfile,
    bool HasConfirmedFacts,
    bool HasInterview,
    bool InterviewPrepared,
    ReadinessState Readiness);

public sealed record SetupStepView(SetupStep Step, StepStatus Status);

/// <summary>
/// Get started as a readiness dashboard: four steps, each Done / Current / Upcoming, and exactly one primary action —
/// the next thing to do. A step is Done only when everything before it is usable (a profile without an account is not
/// "done": nothing can be prepared without signing in).
/// </summary>
public static class SetupPlanner
{
    public static bool IsDone(SetupStep step, SetupFacts f) => step switch
    {
        SetupStep.Account => f.AccountReady,
        SetupStep.Profile => f.AccountReady && f.HasProfile && ProfileUsable(f),
        SetupStep.InterviewSetup => f.AccountReady && f.HasInterview && f.InterviewPrepared,
        // a check result counts only for a usable setup (signed in, an interview prepared)
        SetupStep.PreInterviewCheck => IsDone(SetupStep.Account, f) && IsDone(SetupStep.InterviewSetup, f)
                                       && f.Readiness is ReadinessState.Ready or ReadinessState.ReadyWithWarnings,
        _ => false,
    };

    /// <summary>
    /// Confirmed facts are asked for until the user knowingly prepares an interview without them (Prepare allows it
    /// after a warning); from then on the step detail keeps the reminder, but the next action moves on (rc.5 UX audit M10).
    /// </summary>
    private static bool ProfileUsable(SetupFacts f) => f.HasConfirmedFacts || f.InterviewPrepared;

    public static IReadOnlyList<SetupStepView> Steps(SetupFacts f)
    {
        var current = Enum.GetValues<SetupStep>().Cast<SetupStep?>().FirstOrDefault(s => !IsDone(s!.Value, f));
        return Enum.GetValues<SetupStep>().Select(s => new SetupStepView(s,
            IsDone(s, f) ? StepStatus.Done : s == current ? StepStatus.Current : StepStatus.Upcoming)).ToList();
    }

    public static SetupAction PrimaryAction(SetupFacts f)
    {
        if (!f.AccountReady) return SetupAction.SignIn;
        if (!f.HasProfile || !ProfileUsable(f)) return SetupAction.CompleteProfile;
        if (!f.HasInterview) return SetupAction.CreateInterview;
        if (!f.InterviewPrepared) return SetupAction.PrepareInterview;
        if (f.Readiness is not (ReadinessState.Ready or ReadinessState.ReadyWithWarnings)) return SetupAction.RunPreInterviewCheck;
        return SetupAction.StartInterview;
    }

    /// <summary>Localization key of the primary button.</summary>
    public static string ActionKey(SetupAction a) => a switch
    {
        SetupAction.SignIn => "gs_action_sign_in",
        SetupAction.CompleteProfile => "gs_action_profile",
        SetupAction.CreateInterview => "gs_action_create_interview",
        SetupAction.PrepareInterview => "gs_action_prepare",
        SetupAction.RunPreInterviewCheck => "gs_action_check",
        _ => "gs_action_start",
    };
}
