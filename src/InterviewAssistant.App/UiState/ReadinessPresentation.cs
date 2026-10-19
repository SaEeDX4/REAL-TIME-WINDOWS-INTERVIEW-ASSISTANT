namespace InterviewAssistant.App.UiState;

/// <summary>
/// How every readiness surface presents the one <see cref="ReadinessSnapshot"/>: labels, icons, fixes and their routes,
/// the summary line and the log line of a state change. No WPF types (unit-tested); the pages only map keys to text.
/// </summary>
public static class ReadinessPresentation
{
    /// <summary>The state whose colour and icon a single check outcome shares (no result yet = not checked).</summary>
    public static ReadinessState StateOf(CheckOutcome? outcome) => outcome switch
    {
        CheckOutcome.Pass => ReadinessState.Ready,
        CheckOutcome.Warning => ReadinessState.ReadyWithWarnings,
        CheckOutcome.Fail => ReadinessState.NotReady,
        CheckOutcome.Pending => ReadinessState.Checking,
        _ => ReadinessState.NotChecked,   // no result yet, or an optional test not run (neutral grey)
    };

    /// <summary>Badge text of one check (PASS / WARNING / FAIL are the words the page subtitle explains).</summary>
    public static string StatusKey(CheckOutcome? outcome) => outcome switch
    {
        CheckOutcome.Pass => "pf_pass",
        CheckOutcome.Warning => "pf_warning",
        CheckOutcome.Fail => "pf_fail",
        CheckOutcome.Pending => "pf_checking",
        CheckOutcome.NotTested => "rd_status_not_tested",
        _ => "rd_state_not_checked",
    };

    /// <summary>Segoe Fluent glyph that goes with a state: the badge is never colour alone.</summary>
    public static string Icon(ReadinessState s) => s switch
    {
        ReadinessState.Ready => "",              // check mark
        ReadinessState.ReadyWithWarnings => "",  // warning
        ReadinessState.NotReady => "",           // cross
        ReadinessState.Checking => "",           // sync
        ReadinessState.Outdated => "",           // clock
        _ => "",                                  // unknown
    };

    /// <summary>Row label. Developer builds without the service check the own OpenAI key instead of the account.</summary>
    public static string TitleKey(ReadinessCheckId id, bool usingCloud) => id switch
    {
        ReadinessCheckId.PlaybackDevice => "pf_check_device",
        ReadinessCheckId.AudioSignal => "pf_check_signal",
        ReadinessCheckId.AccountPlan => usingCloud ? "pf_check_account" : "pf_check_auth_dev",
        ReadinessCheckId.Transcription => "pf_check_stt",
        ReadinessCheckId.AnswerEngine => "pf_check_answer",
        ReadinessCheckId.InterviewPrepared => "pf_check_prepared",
        ReadinessCheckId.PreparedAnswers => "pf_check_bank",
        _ => "pf_check_topmost",
    };

    /// <summary>The plain "how to fix it" sentence listed under "What blocks the interview".</summary>
    public static string FixHintKey(ReadinessCheckId id, bool usingCloud) => id switch
    {
        ReadinessCheckId.PlaybackDevice => "pf_fix_device",
        ReadinessCheckId.AudioSignal => "pf_fix_signal",
        ReadinessCheckId.AccountPlan => usingCloud ? "pf_fix_account" : "pf_fix_auth_dev",
        ReadinessCheckId.Transcription => usingCloud ? "pf_fix_stt" : "pf_fix_stt_dev",
        ReadinessCheckId.AnswerEngine => usingCloud ? "pf_fix_answer" : "pf_fix_answer_dev",
        ReadinessCheckId.InterviewPrepared => "pf_fix_prepared",
        ReadinessCheckId.PreparedAnswers => "pf_fix_bank",
        _ => "pf_fix_topmost",
    };

    /// <summary>Label of a check's action button; null = the check offers no action.</summary>
    public static string? FixLabelKey(ReadinessFix fix) => fix switch
    {
        ReadinessFix.OpenAudioSettings => "rd_fix_audio_settings",
        ReadinessFix.TestAudio => "pf_test_audio",
        ReadinessFix.SignIn => "gs_action_sign_in",
        ReadinessFix.OpenAccount => "rd_fix_open_account",
        ReadinessFix.TestTranscription => "pf_test_stt",
        ReadinessFix.TestAi => "pf_test_ai",
        ReadinessFix.OpenInterviews => "rd_fix_open_interviews",
        ReadinessFix.PrepareInterview => "gs_action_prepare",
        ReadinessFix.OpenWindowSettings => "rd_fix_window_settings",
        _ => null,
    };

    /// <summary>Home deep link of a fix; null for the test tools, which run on the page itself.</summary>
    public static string? FixRoute(ReadinessFix fix) => fix switch
    {
        ReadinessFix.OpenAudioSettings => "settings/audio",
        ReadinessFix.SignIn or ReadinessFix.OpenAccount => "account",
        ReadinessFix.OpenInterviews or ReadinessFix.PrepareInterview => "interviews",
        ReadinessFix.OpenWindowSettings => "settings/window",
        _ => null,
    };

    /// <summary>Start interview is offered only when nothing blocks.</summary>
    public static bool CanStart(ReadinessState s) => s is ReadinessState.Ready or ReadinessState.ReadyWithWarnings;

    /// <summary>Opening the page runs the checks when there is no current result.</summary>
    public static bool ShouldAutoRun(ReadinessState s) => s is ReadinessState.NotChecked or ReadinessState.Outdated;

    /// <summary>
    /// Passed checks to show for <paramref name="currentContext"/>: a result for another account, interview or device
    /// (out of date) or no result at all shows 0 of 8 — never numbers that belong to something else.
    /// </summary>
    public static int ShownPassed(ReadinessSnapshot s, string currentContext) =>
        s.Checks.Count > 0 && (s.IsRunning || string.Equals(s.Context, currentContext, StringComparison.Ordinal)) ? s.Passed : 0;

    /// <summary>
    /// The summary line every surface shows ("7 of 8 checks passed"; with an optional test not run yet:
    /// "7 of 7 checks passed · 1 optional test to run"): key and arguments, one helper for the page, Home and Live.
    /// </summary>
    public static (string Key, object[] Args) Summary(ReadinessSnapshot s, string currentContext)
    {
        var passed = ShownPassed(s, currentContext);
        var current = s.Checks.Count > 0 && (s.IsRunning || string.Equals(s.Context, currentContext, StringComparison.Ordinal));
        return current && s.NotTested > 0
            ? ("rd_summary_passed_untested", new object[] { passed, s.Required, s.NotTested })
            : ("rd_summary_passed", new object[] { passed, s.Total });
    }

    /// <summary>Optional tests still to run (shown under "Ready" as a gentle to-do, never as a warning).</summary>
    public static IReadOnlyList<ReadinessCheckResult> NotTestedYet(ReadinessSnapshot s) =>
        s.Checks.Where(c => c.Outcome == CheckOutcome.NotTested).OrderBy(c => c.Id).ToList();

    /// <summary>The checks that block the interview (FAIL), in page order.</summary>
    public static IReadOnlyList<ReadinessCheckResult> Blockers(ReadinessSnapshot s) =>
        s.Checks.Where(c => c.Outcome == CheckOutcome.Fail).OrderBy(c => c.Id).ToList();

    /// <summary>Optional improvements (WARNING), in page order.</summary>
    public static IReadOnlyList<ReadinessCheckResult> Warnings(ReadinessSnapshot s) =>
        s.Checks.Where(c => c.Outcome == CheckOutcome.Warning).OrderBy(c => c.Id).ToList();

    /// <summary>
    /// True when only connection-dependent checks fail: typed questions with prepared answers still work then
    /// (the page says so under the blockers).
    /// </summary>
    public static bool TypedQuestionsStillWork(ReadinessSnapshot s)
    {
        var failed = Blockers(s);
        return failed.Count > 0 && failed.All(c => c.Id is ReadinessCheckId.AudioSignal or ReadinessCheckId.Transcription
            or ReadinessCheckId.AnswerEngine or ReadinessCheckId.AccountPlan);
    }

    /// <summary>Hint under the summary for a state (Not ready lists its blockers separately).</summary>
    public static string HintKey(ReadinessState s) => s switch
    {
        ReadinessState.Ready => "rd_hint_ready",
        ReadinessState.ReadyWithWarnings => "rd_hint_warnings",
        ReadinessState.NotReady => "rd_hint_not_ready",
        ReadinessState.Checking => "rd_hint_checking",
        ReadinessState.Outdated => "rd_hint_outdated",
        _ => "rd_hint_not_checked",
    };

    /// <summary>
    /// The log line of a readiness transition, or null when the state did not change. Only states and counts — never
    /// device names, e-mail addresses or interview names.
    /// </summary>
    public static string? TransitionLog(ReadinessState from, ReadinessState to, ReadinessSnapshot s) =>
        from == to ? null : $"Readiness: {from} -> {to} ({s.Passed}/{s.Total} passed)";
}
