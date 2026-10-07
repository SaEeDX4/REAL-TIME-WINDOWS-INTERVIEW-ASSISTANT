using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

/// <summary>Append-only usage ledger + safety caps (per account and global operator caps).</summary>
public sealed class UsageService
{
    public const string LiveSeconds = "live_seconds", AnswerRequest = "answer_request", PrepJob = "prep_job";
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    private readonly SafetyOptions _safety;
    public UsageService(AppDbContext db, TimeProvider time, IOptions<SafetyOptions> safety) { _db = db; _time = time; _safety = safety.Value; }

    public async Task<int> SumAsync(Guid userId, string kind, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        await _db.Usage.Where(u => u.UserId == userId && u.Kind == kind && u.CreatedUtc >= fromUtc && u.CreatedUtc < toUtc).SumAsync(u => (int?)u.Quantity, ct) ?? 0;

    public async Task<int> GlobalSumAsync(string kind, DateTime fromUtc, CancellationToken ct) =>
        await _db.Usage.Where(u => u.Kind == kind && u.CreatedUtc >= fromUtc).SumAsync(u => (int?)u.Quantity, ct) ?? 0;

    public void Record(Guid userId, string kind, int qty, Guid? sessionId = null, string? idempotencyKey = null) =>
        _db.Usage.Add(new UsageEntry { UserId = userId, Kind = kind, Quantity = qty, SessionId = sessionId, CreatedUtc = _time.GetUtcNow().UtcDateTime, IdempotencyKey = idempotencyKey });

    public DateTime TodayUtc => _time.GetUtcNow().UtcDateTime.Date;

    public async Task<UsageSummary> SummaryAsync(Guid userId, EffectivePlan plan, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var used = await SumAsync(userId, LiveSeconds, plan.PeriodStartUtc, plan.PeriodEndUtc, ct);
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var prep = await SumAsync(userId, PrepJob, monthStart, monthStart.AddMonths(1), ct);
        var answers = await SumAsync(userId, AnswerRequest, now.Date, now.Date.AddDays(1), ct);
        var allowance = EntitlementService.LiveSecondsAllowance(plan);
        return new UsageSummary(plan.PeriodStartUtc, plan.PeriodEndUtc, used, Math.Max(0, allowance - used), prep, Math.Max(0, plan.Plan.PrepJobsPerMonth - prep), answers);
    }

    /// <summary>Remaining live seconds after plan allowance, per-account daily cap and global daily operator cap.</summary>
    public async Task<int> RemainingLiveSecondsAsync(Guid userId, EffectivePlan plan, CancellationToken ct)
    {
        var periodUsed = await SumAsync(userId, LiveSeconds, plan.PeriodStartUtc, plan.PeriodEndUtc, ct);
        var todayUsed = await SumAsync(userId, LiveSeconds, TodayUtc, TodayUtc.AddDays(1), ct);
        var globalToday = await GlobalSumAsync(LiveSeconds, TodayUtc, ct);
        var byPlan = EntitlementService.LiveSecondsAllowance(plan) - periodUsed;
        var byDaily = _safety.PerAccountDailyLiveMinutes * 60 - todayUsed;
        var byGlobal = _safety.GlobalDailyLiveMinutes * 60 - globalToday;
        return Math.Max(0, Math.Min(byPlan, Math.Min(byDaily, byGlobal)));
    }
}
