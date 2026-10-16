namespace InterviewAssistant.App.UiState;

public enum CheckOutcome { Pending, Pass, Warning, Fail }

public enum VerdictKind { Checking, Ready, ReadyWithWarnings, NotReady }

/// <summary>
/// Overall pre-interview verdict. The rule the window states is the rule it applies: only FAIL blocks (NOT READY);
/// warnings are optional improvements (READY with warnings); a check that truly blocks an interview reports FAIL.
/// </summary>
public static class ReadinessVerdict
{
    public static VerdictKind Evaluate(IEnumerable<CheckOutcome> outcomes)
    {
        var list = outcomes.ToList();
        if (list.Any(o => o == CheckOutcome.Pending)) return VerdictKind.Checking;
        if (list.Any(o => o == CheckOutcome.Fail)) return VerdictKind.NotReady;
        return list.Any(o => o == CheckOutcome.Warning) ? VerdictKind.ReadyWithWarnings : VerdictKind.Ready;
    }
}

/// <summary>Copy for the enumeration-safe password reset (same text whether or not an account exists).</summary>
public static class PasswordResetCopy
{
    /// <summary>Shown after the reset request was accepted — for existing and unknown e-mail addresses alike.</summary>
    public static string Requested => Core.Languages.Loc.T("acct_reset_requested");
}
