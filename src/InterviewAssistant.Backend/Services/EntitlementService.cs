using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

/// <param name="TrialForfeited">A trial for an e-mail whose earlier account was deleted: no live minutes.</param>
public sealed record EffectivePlan(PlanDefinition Plan, string Status, DateTime PeriodStartUtc, DateTime PeriodEndUtc, bool IsTrial, bool TrialForfeited = false);

/// <summary>
/// Server-authoritative entitlements. Paid plan applies while the subscription is trialing/active, canceled-at-period-end
/// before the period ends, or past_due within the grace period; otherwise the default (trial) plan applies.
/// The client never decides entitlements; it only renders <see cref="BillingState"/>.
/// </summary>
public sealed class EntitlementService
{
    private readonly AppDbContext _db;
    private readonly PlanCatalog _plans;
    private readonly TimeProvider _time;
    private readonly ILogger<EntitlementService> _log;
    public EntitlementService(AppDbContext db, IOptions<PlanCatalog> plans, TimeProvider time, ILogger<EntitlementService> log) { _db = db; _plans = plans.Value; _time = time; _log = log; }

    /// <summary>Trial allowance has no period end; a fixed UTC horizon keeps Npgsql timestamptz parameters valid.</summary>
    public static readonly DateTime TrialHorizonUtc = new(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// An active/trialing subscription whose period ended more than this long ago is treated as lapsed: Paddle renews
    /// (or retries a failed renewal) around the period end and sends a webhook; if none arrives, access must not continue
    /// indefinitely on a stale row (late/missing webhook, sandbox rows left behind after a Live cutover).
    /// </summary>
    public static readonly TimeSpan RenewalGrace = TimeSpan.FromDays(3);

    public static bool CurrentPeriodValid(Subscription sub, DateTime now) => sub.CurrentPeriodEndUtc is not { } end || end + RenewalGrace > now;

    public static bool PaidAccess(Subscription sub, DateTime now) => sub.AccessRevokedUtc == null && (
        (sub.Status is "active" or "trialing" && CurrentPeriodValid(sub, now))
        || (sub.Status == "past_due" && sub.GraceUntilUtc > now)
        || (sub.Status == "canceled" && sub.CancelAtPeriodEnd && sub.CurrentPeriodEndUtc > now));

    public async Task<(EffectivePlan Plan, Subscription? Sub)> ResolveAsync(UserAccount user, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
        if (sub != null && PaidAccess(sub, now) && sub.CurrentPeriodStartUtc != null && sub.CurrentPeriodEndUtc != null)
        {
            var plan = _plans.Find(sub.PlanCode);
            if (plan == null)
                _log.LogError("Subscription {SubscriptionId} has unmapped plan code {PlanCode}; paid access falls back to the default plan until the price is mapped", sub.ProviderSubscriptionId, sub.PlanCode);
            else
                return (new EffectivePlan(plan, sub.Status, sub.CurrentPeriodStartUtc.Value, sub.CurrentPeriodEndUtc.Value, false), sub);
        }
        // Default/trial plan: lifetime allowance counted from account creation (paid usage counts too, so a lapsed
        // subscriber does not regain trial minutes).
        return (new EffectivePlan(_plans.Get(_plans.DefaultPlanCode), sub?.Status ?? "none", user.CreatedUtc, TrialHorizonUtc, true, user.TrialForfeited), sub);
    }

    /// <summary>Customer-facing state (see <see cref="BillingStates"/>).</summary>
    public static string BillingState(EffectivePlan plan, Subscription? sub, int liveSecondsRemaining, DateTime now)
    {
        if (sub != null && !string.IsNullOrEmpty(sub.Status) && sub.Status != "none")
        {
            if (sub.AccessRevokedUtc != null) return BillingStates.Revoked;
            if (sub.Status is "active" or "trialing")
                return !CurrentPeriodValid(sub, now) ? BillingStates.Expired : sub.CancelAtPeriodEnd ? BillingStates.Canceling : BillingStates.Active;
            if (sub.Status == "past_due") return sub.GraceUntilUtc > now ? BillingStates.PastDue : BillingStates.Expired;
            if (sub.Status == "paused") return BillingStates.Paused;
            if (sub.Status == "canceled") return sub.CancelAtPeriodEnd && sub.CurrentPeriodEndUtc > now ? BillingStates.Canceling : BillingStates.Expired;
        }
        if (!plan.IsTrial) return BillingStates.Active;
        return liveSecondsRemaining > 0 ? BillingStates.Trial : BillingStates.TrialExhausted;
    }

    public static Entitlements ToDto(EffectivePlan e) => new(e.Plan.Code, e.Plan.Name, e.Status, e.Plan.MaxProfiles, e.Plan.PrepJobsPerMonth,
        e.IsTrial ? e.Plan.TrialMinutes : e.Plan.LiveMinutesPerPeriod, e.Plan.MaxConcurrentSessions, e.Plan.MaxDevices, e.Plan.ReportRetentionDays,
        e.Plan.CoachMode, e.Plan.TrialMinutes);

    public static Entitlements CatalogDto(PlanDefinition p) => new(p.Code, p.Name, "catalog", p.MaxProfiles, p.PrepJobsPerMonth,
        p.Purchasable ? p.LiveMinutesPerPeriod : p.TrialMinutes, p.MaxConcurrentSessions, p.MaxDevices, p.ReportRetentionDays, p.CoachMode, p.TrialMinutes);

    public static int LiveSecondsAllowance(EffectivePlan e) => e.IsTrial && e.TrialForfeited ? 0 : (e.IsTrial ? e.Plan.TrialMinutes : e.Plan.LiveMinutesPerPeriod) * 60;
}
