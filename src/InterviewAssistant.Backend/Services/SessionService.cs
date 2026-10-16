using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

/// <summary>
/// Live interview session leases. Start: entitlement + remaining allowance + concurrency + device + maintenance checks,
/// then the relay endpoint for live transcription (the provider credential is minted per relay connection and never
/// leaves the server — see <see cref="RealtimeRelay"/>). Heartbeat: meters elapsed wall-clock time since the previous heartbeat,
/// bounded by the heartbeat timeout (a lease without a heartbeat for that long is expired anyway, so a modified client
/// cannot stretch heartbeats to under-report), requires increasing sequence numbers (replay protection) and ends the
/// lease when allowance is exhausted. Stop, replacement and expiry bill the time since the last heartbeat with the same
/// bound — a client that never heartbeats is still billed. Pause ends the lease on the client, so paused time is never
/// billed. Every way a lease ends closes its relay, and the relay also closes on its own when the time since the last
/// heartbeat reaches the remaining allowance or the heartbeat timeout, so transcription never outlives billable time.
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
    private readonly EntitlementService _entitlements;
    private readonly RealtimeRelayRegistry _relays;
    private readonly ILogger<SessionService> _log;
    private readonly List<Guid> _endedInTransaction = new();

    public SessionService(AppDbContext db, TimeProvider time, IOptions<SafetyOptions> safety, UsageService usage, IRealtimeSecretIssuer issuer, ConfigService config, DeviceService devices,
        EntitlementService entitlements, RealtimeRelayRegistry relays, ILogger<SessionService> log)
    { _db = db; _time = time; _safety = safety.Value; _usage = usage; _issuer = issuer; _config = config; _devices = devices; _entitlements = entitlements; _relays = relays; _log = log; }

    /// <summary>
    /// Serialises all lease operations of one user (start, heartbeat, renew, stop) with a transaction-scoped Postgres
    /// advisory lock, so concurrent requests can neither exceed the concurrent-session limit nor bill a heartbeat twice.
    /// </summary>
    private async Task<T> UserLockedAsync<T>(Guid userId, Func<Task<T>> body, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var key = BitConverter.ToInt64(userId.ToByteArray(), 0);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        _endedInTransaction.Clear();
        var result = await body();
        await tx.CommitAsync(ct);
        foreach (var id in _endedInTransaction) _relays.Close(id);   // relays of leases ended here stop now, not at their next check
        _endedInTransaction.Clear();
        return result;
    }

    private static IReadOnlyList<string> TranscriptionModels(RuntimeConfig cfg) => new[] { cfg.TranscriptionModel }.Concat(cfg.TranscriptionFallbacks).ToList();

    public Task<StartSessionResponse> StartAsync(UserAccount user, EffectivePlan plan, StartSessionRequest req, CancellationToken ct) =>
        UserLockedAsync(user.Id, () => StartLockedAsync(user, plan, req, ct), ct);

    private async Task<StartSessionResponse> StartLockedAsync(UserAccount user, EffectivePlan plan, StartSessionRequest req, CancellationToken ct)
    {
        var cfg = await _config.GetAsync(ct);
        if (cfg.Maintenance) throw new ApiException(503, "maintenance", cfg.MaintenanceMessage ?? "The service is under maintenance. Prepared answers still work offline.");
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey) || req.IdempotencyKey.Length > 80) throw ApiException.BadRequest("idempotency_key", "IdempotencyKey is required.");
        // Clients that would open the provider socket themselves cannot be metered server-side: they must update.
        if (!req.Relay) throw new ApiException(426, "client_update_required", "Please update the app to start live sessions.");
        await _devices.RequireActiveAsync(user.Id, req.DeviceId, ct);

        // Idempotent create: same key returns the same active lease (network retries never double-start).
        var existing = await _db.Sessions.FirstOrDefaultAsync(s => s.UserId == user.Id && s.IdempotencyKey == req.IdempotencyKey, ct);
        var now = _time.GetUtcNow().UtcDateTime;
        if (existing != null && existing.Status != "active") throw ApiException.Conflict("session_ended", "This session already ended. Start a new one.");
        if (existing != null && existing.SecretsIssued > MaxSecretsPerSession) throw ApiException.TooMany("secret_rate", "Too many credential requests for this session.");
        // Starting leases that are then abandoned without heartbeats must not become a way to mint transcription
        // credentials: each expiry is billed (see ExpireStaleAsync) and abandoned leases per hour are bounded. Normal
        // pause/resume (each resume is a new lease, ended by the client) only meets a high ceiling for runaway clients.
        var hourAgo = now.AddHours(-1);
        if (existing == null)
        {
            var recent = await _db.Sessions.Where(s => s.UserId == user.Id && s.StartedUtc > hourAgo)
                .GroupBy(s => s.EndReason == "heartbeat_timeout").Select(g => new { Abandoned = g.Key, Count = g.Count() }).ToListAsync(ct);
            var abandoned = recent.Where(r => r.Abandoned).Sum(r => r.Count);
            var started = recent.Sum(r => r.Count);
            if (abandoned >= _safety.MaxAbandonedSessionsPerHour || started >= _safety.MaxSessionStartsPerHour)
                throw ApiException.TooMany("session_start_rate", "Too many live sessions were started in the last hour. Please wait a few minutes.");
        }

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
            existing = new InterviewSession { Id = Guid.NewGuid(), UserId = user.Id, DeviceId = req.DeviceId, TargetId = req.TargetId, IdempotencyKey = req.IdempotencyKey, StartedUtc = now, LastHeartbeatUtc = now, AllowanceAtHeartbeatSeconds = remaining };
            _db.Sessions.Add(existing);
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Session {SessionId} started for user {UserId} on device {DeviceId} ({Plan}, {Remaining}s left)", existing.Id, user.Id, req.DeviceId, plan.Plan.Code, remaining);
        var maxDuration = Math.Min(_safety.MaxSessionMinutes * 60, remaining);
        var cred = RelayCredential.For(existing.Id, cfg.TranscriptionModel, existing.StartedUtc.AddSeconds(maxDuration));
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
        var elapsed = (int)Math.Clamp((now - s.LastHeartbeatUtc).TotalSeconds, 0, _safety.HeartbeatTimeoutSeconds);
        var before = await _usage.LiveRemainingAsync(user.Id, plan, ct);
        var remainingBefore = before.Effective;
        var billable = Math.Min(elapsed, remainingBefore);
        if (billable > 0) _usage.Record(user.Id, UsageService.LiveSeconds, billable, s.Id);
        s.MeteredSeconds += billable; s.LastHeartbeatUtc = now; s.HeartbeatSequence = seq;
        var remaining = remainingBefore - billable;
        s.AllowanceAtHeartbeatSeconds = Math.Max(0, remaining);
        if (remaining <= 0) End(s, "exhausted", before.Period - billable <= 0 ? "allowance_exhausted" : before.Today - billable <= 0 ? "daily_cap" : "global_cap", now);
        else if ((now - s.StartedUtc).TotalMinutes >= _safety.MaxSessionMinutes) End(s, "stopped", "max_duration", now);
        await _db.SaveChangesAsync(ct);
        if (s.Status != "active") _log.LogInformation("Session {SessionId} ended: {Reason} ({Metered}s metered)", s.Id, s.EndReason, s.MeteredSeconds);
        return new HeartbeatResponse(Math.Max(0, remaining), s.Status == "active", s.EndReason, Math.Max(0, before.Period - billable), Math.Max(0, before.Today - billable));
    }

    /// <summary>Kept for clients that ask for a fresh endpoint before reconnecting: returns the relay, never a provider secret.</summary>
    public async Task<RealtimeCredential> RenewSecretAsync(UserAccount user, EffectivePlan plan, Guid sessionId, CancellationToken ct)
    {
        var s = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == user.Id, ct) ?? throw ApiException.NotFound();
        if (s.Status != "active") throw ApiException.Conflict("session_ended", "Session is not active.");
        if (await _usage.RemainingLiveSecondsAsync(user.Id, plan, ct) <= 0) throw ApiException.Payment("allowance_exhausted", "No live minutes remain.");
        var cfg = await _config.GetAsync(ct);
        return RelayCredential.For(s.Id, cfg.TranscriptionModel, s.StartedUtc.AddMinutes(_safety.MaxSessionMinutes));
    }

    /// <summary>
    /// Authorises one relay connection: the lease must be this user's, active, recently heartbeaten, on an active device
    /// and have allowance left. Mints the upstream credential (counted against the per-lease cap); it is used only by
    /// the relay and is never sent to the client.
    /// </summary>
    public Task<RealtimeCredential> OpenRelayAsync(UserAccount user, EffectivePlan plan, Guid sessionId, string prompt, string? language, CancellationToken ct) =>
        UserLockedAsync(user.Id, async () =>
        {
            var s = await _db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == user.Id, ct) ?? throw ApiException.NotFound();
            if (!RelayMayRun(s, _time.GetUtcNow().UtcDateTime)) throw ApiException.Conflict("session_ended", "Your live session has ended. Press Resume.");
            await _devices.RequireActiveAsync(user.Id, s.DeviceId, ct);
            if (await _usage.RemainingLiveSecondsAsync(user.Id, plan, ct) <= 0) throw ApiException.Payment("allowance_exhausted", "No live minutes remain.");
            if (s.SecretsIssued > MaxSecretsPerSession) throw ApiException.TooMany("secret_rate", "Too many live connections for this session.");
            var cfg = await _config.GetAsync(ct);
            var cred = await _issuer.IssueAsync(TranscriptionModels(cfg), prompt, language, ct);
            s.SecretsIssued++;
            await _db.SaveChangesAsync(ct);
            return cred;
        }, ct);

    /// <summary>Re-read by a running relay every few seconds.</summary>
    public async Task<bool> RelayLeaseActiveAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var s = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, ct);
        return s != null && RelayMayRun(s, _time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// A relay may carry audio only inside time that will be billed: while the lease is active, and for no longer after
    /// the last heartbeat than both the remaining allowance and the heartbeat timeout (less a margin for the relay's
    /// check interval). Heartbeats and the expiry sweeper bill exactly that window.
    /// </summary>
    private bool RelayMayRun(InterviewSession s, DateTime now)
    {
        if (s.Status != "active") return false;
        var sinceHeartbeat = (now - s.LastHeartbeatUtc).TotalSeconds;
        var window = Math.Min(s.AllowanceAtHeartbeatSeconds, _safety.HeartbeatTimeoutSeconds - RelayCheckMarginSeconds);
        return sinceHeartbeat < window;
    }

    /// <summary>Twice the relay's lease check interval, so a relay always closes before unbilled time could start.</summary>
    public const int RelayCheckMarginSeconds = 5;

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

    private int MaxSecretsPerSession => _safety.MaxSessionMinutes + 5;

    /// <summary>Bills the time since the last heartbeat (bounded by the heartbeat timeout) when a lease ends.</summary>
    private async Task BillTailAsync(UserAccount user, EffectivePlan plan, InterviewSession s, DateTime now, CancellationToken ct)
    {
        var tail = (int)Math.Clamp((now - s.LastHeartbeatUtc).TotalSeconds, 0, _safety.HeartbeatTimeoutSeconds);
        var billable = Math.Min(tail, await _usage.RemainingLiveSecondsAsync(user.Id, plan, ct));
        if (billable > 0) { _usage.Record(user.Id, UsageService.LiveSeconds, billable, s.Id); s.MeteredSeconds += billable; }
    }

    /// <summary>
    /// Expires leases without heartbeats (crash, network loss, or a client that never heartbeats) and bills the time
    /// since the last heartbeat, bounded by the timeout. Runs under the same per-user lock as heartbeats, and re-checks
    /// the lease inside the lock so a heartbeat that just arrived wins.
    /// </summary>
    public async Task<int> ExpireStaleAsync(CancellationToken ct)
    {
        var cutoff = _time.GetUtcNow().UtcDateTime.AddSeconds(-_safety.HeartbeatTimeoutSeconds);
        var candidates = await _db.Sessions.AsNoTracking().Where(s => s.Status == "active" && s.LastHeartbeatUtc < cutoff).Select(s => new { s.Id, s.UserId }).ToListAsync(ct);
        int expired = 0;
        foreach (var c in candidates)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == c.UserId, ct);
            if (user == null) continue;
            var (plan, _) = await _entitlements.ResolveAsync(user, ct);
            var ended = await UserLockedAsync(c.UserId, async () =>
            {
                var s = await _db.Sessions.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
                var now = _time.GetUtcNow().UtcDateTime;
                if (s == null || s.Status != "active" || s.LastHeartbeatUtc >= now.AddSeconds(-_safety.HeartbeatTimeoutSeconds)) return false;
                await BillTailAsync(user, plan, s, now, ct);
                End(s, "expired", "heartbeat_timeout", now);
                await _db.SaveChangesAsync(ct);
                return true;
            }, ct);
            if (ended) { expired++; _log.LogInformation("Session {SessionId} expired: no heartbeat for {Timeout}s", c.Id, _safety.HeartbeatTimeoutSeconds); }
        }
        return expired;
    }

    public async Task<bool> IsActiveAsync(Guid userId, Guid sessionId, CancellationToken ct) =>
        await _db.Sessions.AnyAsync(s => s.Id == sessionId && s.UserId == userId && s.Status == "active", ct);

    private void End(InterviewSession s, string status, string reason, DateTime now) { s.Status = status; s.EndReason = reason; s.EndedUtc = now; _endedInTransaction.Add(s.Id); }
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
