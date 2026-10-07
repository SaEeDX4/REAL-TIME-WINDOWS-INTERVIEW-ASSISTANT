using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

public sealed record EffectivePlan(PlanDefinition Plan, string Status, DateTime PeriodStartUtc, DateTime PeriodEndUtc, bool IsTrial);

/// <summary>
/// Server-authoritative entitlements. Paid plan applies while the subscription is trialing/active, or past_due within the
/// grace period; otherwise the default (trial) plan applies. The client never decides entitlements.
/// </summary>
public sealed class EntitlementService
{
    private readonly AppDbContext _db;
    private readonly PlanCatalog _plans;
    private readonly TimeProvider _time;
    public EntitlementService(AppDbContext db, IOptions<PlanCatalog> plans, TimeProvider time) { _db = db; _plans = plans.Value; _time = time; }

    public async Task<(EffectivePlan Plan, Subscription? Sub)> ResolveAsync(UserAccount user, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
        if (sub != null)
        {
            bool paidActive = sub.Status is "active" or "trialing"
                              || (sub.Status == "past_due" && sub.GraceUntilUtc > now)
                              || (sub.Status == "canceled" && sub.CurrentPeriodEndUtc > now && sub.CancelAtPeriodEnd);
            if (paidActive && sub.CurrentPeriodStartUtc != null && sub.CurrentPeriodEndUtc != null)
                return (new EffectivePlan(_plans.Get(sub.PlanCode), sub.Status, sub.CurrentPeriodStartUtc.Value, sub.CurrentPeriodEndUtc.Value, false), sub);
        }
        // Default/trial plan: lifetime allowance counted from account creation.
        return (new EffectivePlan(_plans.Get(_plans.DefaultPlanCode), sub?.Status ?? "none", user.CreatedUtc, TrialHorizonUtc, true), sub);
    }

    public static Entitlements ToDto(EffectivePlan e) => new(e.Plan.Code, e.Plan.Name, e.Status, e.Plan.MaxProfiles, e.Plan.PrepJobsPerMonth,
        e.IsTrial ? e.Plan.TrialMinutes : e.Plan.LiveMinutesPerPeriod, e.Plan.MaxConcurrentSessions, e.Plan.MaxDevices, e.Plan.ReportRetentionDays,
        e.Plan.CoachMode, e.Plan.AdvancedReports, e.Plan.CustomAnswerModes, e.Plan.ModelTier, e.Plan.TrialMinutes, e.Plan.MaxLanguages);

    /// <summary>Trial allowance has no period end; a fixed UTC horizon keeps Npgsql timestamptz parameters valid.</summary>
    public static readonly DateTime TrialHorizonUtc = new(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static int LiveSecondsAllowance(EffectivePlan e) => (e.IsTrial ? e.Plan.TrialMinutes : e.Plan.LiveMinutesPerPeriod) * 60;
}
