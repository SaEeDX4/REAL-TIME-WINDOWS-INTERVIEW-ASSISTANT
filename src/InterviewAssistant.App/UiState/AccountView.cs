using InterviewAssistant.Contracts;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App.UiState;

/// <summary>Responsive layout of the Account dashboard (two columns on wide content, one column below the breakpoint).</summary>
public static class AccountLayout
{
    /// <summary>Content width (device-independent pixels) from which Plan/Devices and Security/Privacy sit side by side.</summary>
    public const double TwoColumnMinWidth = 900;
    /// <summary>Content width from which the signed-out page shows the product column next to the form.</summary>
    public const double AuthTwoColumnMinWidth = 760;
    /// <summary>Maximum width of the signed-out page (product column + form), and of its single centred column.</summary>
    public const double AuthMaxWidth = 940, AuthNarrowMaxWidth = 460;

    public static int DashboardColumns(double contentWidth) => contentWidth >= TwoColumnMinWidth ? 2 : 1;
    public static int AuthColumns(double contentWidth) => contentWidth >= AuthTwoColumnMinWidth ? 2 : 1;
}

public enum PlanTone { Neutral, Accent, Positive, Warning, Problem }

/// <summary>One metric card of "Plan and usage": label, value, optional detail line and optional usage bar (0..1).</summary>
public sealed record UsageMetric(string Id, string Label, string Value, string Detail, double? Progress);

/// <summary>
/// What the "Plan and usage" card shows for an account summary. Plain class (no WPF): the server computes the billing
/// state and the numbers, this only maps them to words, badges and bars.
/// </summary>
public static class PlanView
{
    /// <summary>Badge text key of the server's billing state.</summary>
    public static string BadgeKey(string? billingState) => billingState switch
    {
        BillingStates.Trial => "acct_state_trial",
        BillingStates.TrialExhausted => "acct_state_trial_used",
        BillingStates.Active => "acct_state_active",
        BillingStates.Canceling => "acct_state_canceling",
        BillingStates.PastDue => "acct_state_past_due",
        BillingStates.Paused => "acct_state_paused",
        BillingStates.Expired => "acct_state_expired",
        BillingStates.Revoked => "acct_state_revoked",
        _ => "acct_state_active",
    };

    public static PlanTone BadgeTone(string? billingState) => billingState switch
    {
        BillingStates.Active => PlanTone.Positive,
        BillingStates.Trial => PlanTone.Accent,
        BillingStates.Canceling or BillingStates.PastDue or BillingStates.Paused => PlanTone.Warning,
        BillingStates.TrialExhausted or BillingStates.Expired or BillingStates.Revoked => PlanTone.Problem,
        _ => PlanTone.Neutral,
    };

    /// <summary>A paid plan (or a paused one that can be resumed): Upgrade is not offered as the primary action.</summary>
    public static bool IsPaid(string? billingState) =>
        billingState is BillingStates.Active or BillingStates.Canceling or BillingStates.PastDue or BillingStates.Paused;

    /// <summary>Metric cards: minutes used and remaining this period, available today, preparations left, plan limits.</summary>
    public static IReadOnlyList<UsageMetric> Metrics(AccountDto a, Func<DateTime, string> formatDate)
    {
        var e = a.Entitlements;
        var u = a.Usage;
        var trial = a.BillingState is BillingStates.Trial or BillingStates.TrialExhausted;
        var plan = Math.Max(0, trial ? e.TrialMinutes : e.LiveMinutesPerPeriod);
        var used = Math.Max(0, u.LiveSecondsUsed / 60);
        var remaining = Math.Max(0, u.LiveSecondsRemaining / 60);
        var list = new List<UsageMetric>
        {
            new("minutes_used", Loc.T("usage_minutes_used"), Loc.F("usage_minutes_used_value", used, plan), "", Fraction(used, plan)),
            new("minutes_remaining", Loc.T("usage_remaining_period"), Loc.F("acct_metric_minutes", remaining), Loc.F("acct_metric_until", formatDate(u.PeriodEndUtc)), null),
        };
        if (u.LiveSecondsAvailableToday >= 0)
            list.Add(new("available_today", Loc.T("usage_available_today"), Loc.F("acct_metric_minutes", Math.Min(u.LiveSecondsAvailableToday / 60, remaining)),
                Loc.F("acct_metric_daily_cap", u.DailyLiveMinutesCap), null));
        var prepTotal = u.PrepJobsUsed + u.PrepJobsRemaining;
        list.Add(new("preparations", Loc.T("usage_preparations"), Loc.F("usage_preparations_value", Math.Max(0, u.PrepJobsRemaining)),
            prepTotal > 0 ? Loc.F("acct_metric_prep_used", u.PrepJobsUsed, prepTotal) : "", prepTotal > 0 ? Fraction(u.PrepJobsUsed, prepTotal) : null));
        list.Add(new("limits", Loc.T("usage_limits"), Loc.F("usage_limits_value", e.MaxDevices, e.MaxProfiles), "", null));
        return list;
    }

    /// <summary>0..1, never NaN (a plan without minutes shows an empty bar).</summary>
    public static double Fraction(int part, int total) => total <= 0 ? 0 : Math.Clamp((double)part / total, 0, 1);
}

/// <summary>Account deletion rules the dialog explains (mirrors the service: a subscription that can still charge blocks deletion).</summary>
public static class AccountDeletion
{
    /// <summary>
    /// True when the service refuses deletion because the subscription can still charge or resume: active without a
    /// scheduled cancellation, trialing, past due or paused. A subscription already set to end does not block.
    /// </summary>
    public static bool SubscriptionBlocks(SubscriptionDto? s) => s != null &&
        ((s.Status == "active" && !s.CancelAtPeriodEnd) || s.Status is "trialing" or "past_due" or "paused");

    /// <summary>Every text of the delete dialog and the follow-up local-data question (checked in all languages).</summary>
    public static readonly IReadOnlyList<string> DialogKeys = new[]
    {
        "del_title", "del_irreversible", "del_will_be_deleted", "del_item_account", "del_item_plan", "del_item_devices", "del_item_service_data",
        "del_note_subscription", "del_note_subscription_blocks", "del_note_paddle", "del_note_providers", "del_note_local", "del_keep", "del_confirm",
        "del_local_title", "del_local_message", "del_local_keep", "del_local_delete",
    };
}
