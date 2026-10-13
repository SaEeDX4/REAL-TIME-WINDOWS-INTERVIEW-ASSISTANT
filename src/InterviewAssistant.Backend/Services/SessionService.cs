using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

/// <summary>
/// Live interview session leases. Start: entitlement + remaining allowance + concurrency + device + maintenance checks,
/// then an ephemeral transcription credential. Heartbeat: meters elapsed time (bounded to 2× interval so a paused client
/// can't be over-billed, and a hacked client can't under-report beyond one interval), requires increasing sequence
/// numbers (replay protection), ends the lease when allowance is exhausted. Stop/timeout releases the lease.
/// </summary>
public sealed class SessionService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    private readonly SafetyOptions _safety;
    private readonly UsageService _usage;
    private readonly IRealtimeSecretIssuer _issuer;
    private readonly ConfigService _config;
    private readonly DeviceService _devices;
    private readonly ILogger<SessionService> _log;

    public SessionService(AppDbContext db, TimeProvider time, IOptions<SafetyOptions> safety, UsageService usage, IRealtimeSecretIssuer issuer, ConfigService config, DeviceService devices, ILogger<SessionService> log)
    { _db = db; _time = time; _safety = safety.Value; _usage = usage; _issuer = issuer; _config = config; _devices = devices; _log = log; }

    /// <summary>
    /// Serialises all lease operations of one user (start, heartbeat, renew, stop) with a transaction-scoped Postgres
    /// advisory lock, so concurrent requests can neither exceed the concurrent-session limit nor bill a heartbeat twice.
    /// </summary>
    private async Task<T> UserLockedAsync<T>(Guid userId, Func<Task<T>> body, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var key = BitConverter.ToInt64(userId.ToByteArray(), 0);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        var result = await body();
        await tx.CommitAsync(ct);
        return result;
    }

    private static IReadOnlyList<string> TranscriptionModels(RuntimeConfig cfg) => new[] { cfg.TranscriptionModel }.Concat(cfg.TranscriptionFallbacks).ToList();

    public Task<StartSessionResponse> StartAsync(UserAccount user, EffectivePlan plan, StartSessionRequest req, string transcriptionPrompt, CancellationToken ct) =>
        UserLockedAsync(user.Id, () => StartLockedAsync(user, plan, req, transcriptionPrompt, ct), ct);

    private async Task<StartSessionResponse> StartLockedAsync(UserAccount user, EffectivePlan plan, StartSessionRequest req, string transcriptionPrompt, CancellationToken ct)
    {
        var cfg = await _config.GetAsync(ct);
        if (cfg.Maintenance) throw new ApiException(503, "maintenance", cfg.MaintenanceMessage ?? "The service is under maintenance. Prepared answers still work offline.");
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey) || req.IdempotencyKey.Length > 80) throw ApiException.BadRequest("idempotency_key", "IdempotencyKey is required.");
        await _devices.RequireActiveAsync(user.Id, req.DeviceId, ct);

        // Idempotent create: same key returns the same active lease (network retries never double-start).
        var existing = await _db.Sessions.FirstOrDefaultAsync(s => s.UserId == user.Id && s.IdempotencyKey == req.IdempotencyKey, ct);
        var now = _time.GetUtcNow().UtcDateTime;
        if (existing != null && existing.Status != "active") throw ApiException.Conflict("session_ended", "This session already ended. Start a new one.");

        var live = await _usage.LiveRemainingAsync(user.Id, plan, ct);
        var remaining = live.Effective;
        if (live.Period <= 0) throw ApiException.Payment("allowance_exhausted", "No live interview minutes remain on your plan for this period.");
        if (live.Today <= 0) throw ApiException.TooMany("daily_live_cap", "Today's live-minute limit for this account is reached. It resets at 00:00 UTC.");
        if (remaining <= 0) throw new ApiException(503, "global_cap", "The live service is at capacity right now. Prepared answers still work.");
        if (existing == null)
        {
            // The same installation starting again (app crash/restart) replaces its own stale lease instead of being blocked.
            foreach (var old in await _db.Sessions.Where(s => s.UserId == user.Id && s.Status == "active" && s.DeviceId == req.DeviceId).ToListAsync(ct))
            {
                await BillTailAsync(user, plan, old, now, ct);
                End(old, "stopped", "replaced_on_device", now);
            }
            var active = await _db.Sessions.CountAsync(s => s.UserId == user.Id && s.Status == "active" && s.DeviceId != req.DeviceId, ct);
            if (active >= plan.Plan.MaxConcurrentSessions) throw ApiException.Conflict("concurrent_limit", "Another live session is already running for this account.");
            existing = new InterviewSession { Id = Guid.NewGuid(), UserId = user.Id, DeviceId = req.DeviceId, TargetId = req.TargetId, IdempotencyKey = req.IdempotencyKey, StartedUtc = now, LastHeartbeatUtc = now };
            _db.Sessions.Add(existing);
        }
        var cred = await _issuer.IssueAsync(TranscriptionModels(cfg), transcriptionPrompt, req.InterviewLanguage, ct);
        existing.SecretsIssued++;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Session {SessionId} started for user {UserId} on device {DeviceId} ({Plan}, {Remaining}s left, model {Model})", existing.Id, user.Id, req.DeviceId, plan.Plan.Code, remaining, cred.Model);
        var maxDuration = Math.Min(_safety.MaxSessionMinutes * 60, remaining);
        return new StartSessionResponse(existing.Id, cred, _safety.HeartbeatIntervalSeconds, maxDuration, remaining, live.Period, live.Today);
    }

    public Task<HeartbeatResponse> HeartbeatAsync(UserAccount user, EffectivePlan plan, Guid sessionId, long seq, CancellationToken ct) =>
        UserLockedAsync(user.Id, () => HeartbeatLockedAsync(user, plan, sessionId, seq, ct), ct);

    private async Task<HeartbeatResponse> HeartbeatLockedAsync(UserAccount user, EffectivePlan plan, Guid sessionId, long seq, CancellationToken ct)
    {
        var s = await _db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == user.Id, ct) ?? throw ApiException.NotFound();
        if (s.Status != "active") return new HeartbeatResponse(0, false, s.EndReason ?? s.Status);
        if (seq <= s.HeartbeatSequence) throw ApiException.Conflict("replayed_heartbeat", "Heartbeat sequence must increase.");
        var now = _time.GetUtcNow().UtcDateTime;
        var elapsed = (int)Math.Clamp((now - s.LastHeartbeatUtc).TotalSeconds, 0, _safety.HeartbeatIntervalSeconds * 2);
        var before = await _usage.LiveRemainingAsync(user.Id, plan, ct);
        var remainingBefore = before.Effective;
        var billable = Math.Min(elapsed, remainingBefore);
        if (billable > 0) _usage.Record(user.Id, UsageService.LiveSeconds, billable, s.Id);
        s.MeteredSeconds += billable; s.LastHeartbeatUtc = now; s.HeartbeatSequence = seq;
        var remaining = remainingBefore - billable;
        if (remaining <= 0) End(s, "exhausted", before.Period - billable <= 0 ? "allowance_exhausted" : before.Today - billable <= 0 ? "daily_cap" : "global_cap", now);
        else if ((now - s.StartedUtc).TotalMinutes >= _safety.MaxSessionMinutes) End(s, "stopped", "max_duration", now);
        await _db.SaveChangesAsync(ct);
        if (s.Status != "active") _log.LogInformation("Session {SessionId} ended: {Reason} ({Metered}s metered)", s.Id, s.EndReason, s.MeteredSeconds);
        return new HeartbeatResponse(Math.Max(0, remaining), s.Status == "active", s.EndReason, Math.Max(0, before.Period - billable), Math.Max(0, before.Today - billable));
    }

    public Task<RealtimeCredential> RenewSecretAsync(UserAccount user, EffectivePlan plan, Guid sessionId, string prompt, string? language, CancellationToken ct) =>
        UserLockedAsync(user.Id, () => RenewLockedAsync(user, plan, sessionId, prompt, language, ct), ct);

    private async Task<RealtimeCredential> RenewLockedAsync(UserAccount user, EffectivePlan plan, Guid sessionId, string prompt, string? language, CancellationToken ct)
    {
        var s = await _db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == user.Id, ct) ?? throw ApiException.NotFound();
        if (s.Status != "active") throw ApiException.Conflict("session_ended", "Session is not active.");
        if (await _usage.RemainingLiveSecondsAsync(user.Id, plan, ct) <= 0) throw ApiException.Payment("allowance_exhausted", "No live minutes remain.");
        if (s.SecretsIssued > (_safety.MaxSessionMinutes * 60 / 60) + 5) throw ApiException.TooMany("secret_rate", "Too many credential renewals for this session.");
        var cfg = await _config.GetAsync(ct);
        var cred = await _issuer.IssueAsync(TranscriptionModels(cfg), prompt, language, ct);
        s.SecretsIssued++;
        await _db.SaveChangesAsync(ct);
        return cred;
    }

    public Task StopAsync(UserAccount user, EffectivePlan plan, Guid sessionId, CancellationToken ct) =>
        UserLockedAsync(user.Id, async () =>
        {
            var s = await _db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == user.Id, ct) ?? throw ApiException.NotFound();
            if (s.Status != "active") return true;
            var now = _time.GetUtcNow().UtcDateTime;
            await BillTailAsync(user, plan, s, now, ct);
            End(s, "stopped", "client_stop", now);
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Session {SessionId} stopped by client ({Metered}s metered)", s.Id, s.MeteredSeconds);
            return true;
        }, ct);

    /// <summary>Bills the time since the last heartbeat (bounded to 2 intervals) when a lease ends.</summary>
    private async Task BillTailAsync(UserAccount user, EffectivePlan plan, InterviewSession s, DateTime now, CancellationToken ct)
    {
        var tail = (int)Math.Clamp((now - s.LastHeartbeatUtc).TotalSeconds, 0, _safety.HeartbeatIntervalSeconds * 2);
        var billable = Math.Min(tail, await _usage.RemainingLiveSecondsAsync(user.Id, plan, ct));
        if (billable > 0) { _usage.Record(user.Id, UsageService.LiveSeconds, billable, s.Id); s.MeteredSeconds += billable; }
    }

    /// <summary>Expires leases without heartbeats (crash, network loss). Bills at most one interval after the last heartbeat.</summary>
    public async Task<int> ExpireStaleAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var cutoff = now.AddSeconds(-_safety.HeartbeatTimeoutSeconds);
        var stale = await _db.Sessions.Where(s => s.Status == "active" && s.LastHeartbeatUtc < cutoff).ToListAsync(ct);
        foreach (var s in stale) End(s, "expired", "heartbeat_timeout", now);
        await _db.SaveChangesAsync(ct);
        foreach (var s in stale) _log.LogInformation("Session {SessionId} expired: no heartbeat for {Timeout}s", s.Id, _safety.HeartbeatTimeoutSeconds);
        return stale.Count;
    }

    public async Task<bool> IsActiveAsync(Guid userId, Guid sessionId, CancellationToken ct) =>
        await _db.Sessions.AnyAsync(s => s.Id == sessionId && s.UserId == userId && s.Status == "active", ct);

    private static void End(InterviewSession s, string status, string reason, DateTime now) { s.Status = status; s.EndReason = reason; s.EndedUtc = now; }
}

/// <summary>Background sweeper: stale leases every 30 s, report retention hourly.</summary>
public sealed class SessionSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SessionSweeper> _log;
    public SessionSweeper(IServiceScopeFactory scopes, ILogger<SessionSweeper> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        long tick = 0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var n = await scope.ServiceProvider.GetRequiredService<SessionService>().ExpireStaleAsync(ct);
                if (n > 0) _log.LogInformation("Expired {Count} stale sessions", n);
                if (tick++ % 120 == 0) // hourly: enforce report retention
                {
                    var purged = await scope.ServiceProvider.GetRequiredService<DocumentService>().PurgeExpiredReportsAsync(ct);
                    if (purged > 0) _log.LogInformation("Purged {Count} reports past retention", purged);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Session sweep failed"); }
        }
    }
}
