using System.Text.Json;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

/// <summary>Profiles/targets/reports sync (per-user ownership on every query), plan limits, export and deletion.</summary>
public sealed class DocumentService
{
    public static readonly string[] Kinds = { "profile", "target", "report" };
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    private readonly SafetyOptions _safety;
    public DocumentService(AppDbContext db, TimeProvider time, IOptions<SafetyOptions> safety) { _db = db; _time = time; _safety = safety.Value; }

    private static void CheckKind(string kind) { if (!Kinds.Contains(kind)) throw ApiException.NotFound(); }

    public async Task<List<SyncDocument>> ListAsync(Guid userId, string kind, Guid? parentId, CancellationToken ct)
    {
        CheckKind(kind);
        var now = _time.GetUtcNow().UtcDateTime;
        var q = _db.Documents.AsNoTracking().Where(d => d.UserId == userId && d.Kind == kind && (d.RetainUntilUtc == null || d.RetainUntilUtc > now));
        if (parentId != null) q = q.Where(d => d.ParentId == parentId);
        return (await q.OrderBy(d => d.CreatedUtc).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<SyncDocument> GetAsync(Guid userId, string kind, Guid id, CancellationToken ct)
    {
        CheckKind(kind);
        var d = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId && x.Kind == kind, ct) ?? throw ApiException.NotFound();
        return ToDto(d);
    }

    public async Task<SyncDocument> UpsertAsync(Guid userId, EffectivePlan plan, string kind, Guid id, UpsertDocumentRequest req, CancellationToken ct)
    {
        CheckKind(kind);
        var raw = req.Data.GetRawText();
        if (raw.Length > _safety.MaxDocumentBytes) throw new ApiException(413, "too_large", "Document is too large.");
        var now = _time.GetUtcNow().UtcDateTime;
        var d = await _db.Documents.FirstOrDefaultAsync(x => x.Id == id && x.Kind == kind, ct);
        if (d != null && d.UserId != userId) throw ApiException.NotFound(); // never reveal other users' ids
        if (req.ParentId != null)
        {
            var parentKind = kind == "target" ? "profile" : kind == "report" ? "target" : null;
            if (parentKind == null || !await _db.Documents.AnyAsync(p => p.Id == req.ParentId && p.UserId == userId && p.Kind == parentKind, ct)) throw ApiException.NotFound();
        }
        if (d == null)
        {
            if (kind == "profile" && await _db.Documents.CountAsync(x => x.UserId == userId && x.Kind == "profile", ct) >= plan.Plan.MaxProfiles)
                throw ApiException.Payment("profile_limit", $"Your plan allows {plan.Plan.MaxProfiles} candidate profile(s).");
            d = new UserDocument { Id = id, UserId = userId, Kind = kind, CreatedUtc = now };
            if (kind == "report") d.RetainUntilUtc = now.AddDays(plan.Plan.ReportRetentionDays);
            _db.Documents.Add(d);
        }
        else if (req.ExpectedVersion != null && req.ExpectedVersion != d.Version) throw ApiException.Conflict("version_conflict", "The document was changed on another device.");
        d.ParentId = req.ParentId ?? d.ParentId; d.DataJson = raw; d.Version++; d.UpdatedUtc = now;
        await _db.SaveChangesAsync(ct);
        return ToDto(d);
    }

    public async Task DeleteAsync(Guid userId, string kind, Guid id, CancellationToken ct)
    {
        CheckKind(kind);
        var d = await _db.Documents.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId && x.Kind == kind, ct) ?? throw ApiException.NotFound();
        // Cascade: profile → its targets → their reports.
        var ids = new List<Guid> { d.Id };
        var frontier = new List<Guid> { d.Id };
        while (frontier.Count > 0)
        {
            var children = await _db.Documents.Where(x => x.UserId == userId && x.ParentId != null && frontier.Contains(x.ParentId.Value)).Select(x => x.Id).ToListAsync(ct);
            ids.AddRange(children); frontier = children;
        }
        await _db.Documents.Where(x => x.UserId == userId && ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<int> PurgeExpiredReportsAsync(CancellationToken ct) =>
        await _db.Documents.Where(d => d.RetainUntilUtc != null && d.RetainUntilUtc <= _time.GetUtcNow().UtcDateTime).ExecuteDeleteAsync(ct);

    private static SyncDocument ToDto(UserDocument d) => new(d.Id, d.Kind, d.ParentId, JsonDocument.Parse(d.DataJson).RootElement.Clone(), d.Version, d.UpdatedUtc);
}

public sealed class AccountService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    public AccountService(AppDbContext db, TimeProvider time) { _db = db; _time = time; }

    /// <summary>GDPR-style export: account, subscription state, devices, documents, usage (JSON).</summary>
    public async Task<object> ExportAsync(UserAccount u, CancellationToken ct) => new
    {
        format = "interview-assistant-export/v1", exportedUtc = _time.GetUtcNow().UtcDateTime,
        account = new { u.Id, u.Email, u.Status, u.CreatedUtc },
        subscription = await _db.Subscriptions.AsNoTracking().Where(s => s.UserId == u.Id).Select(s => new { s.PlanCode, s.Status, s.CurrentPeriodEndUtc }).FirstOrDefaultAsync(ct),
        devices = await _db.Devices.AsNoTracking().Where(d => d.UserId == u.Id).Select(d => new { d.Name, d.Platform, d.CreatedUtc, d.LastSeenUtc, d.RevokedUtc }).ToListAsync(ct),
        documents = (await _db.Documents.AsNoTracking().Where(d => d.UserId == u.Id).ToListAsync(ct)).Select(d => new { d.Id, d.Kind, d.ParentId, data = JsonDocument.Parse(d.DataJson).RootElement.Clone(), d.UpdatedUtc }),
        usage = await _db.Usage.AsNoTracking().Where(x => x.UserId == u.Id).GroupBy(x => x.Kind).Select(g => new { kind = g.Key, total = g.Sum(x => x.Quantity) }).ToListAsync(ct),
        sessions = await _db.Sessions.AsNoTracking().Where(s => s.UserId == u.Id).Select(s => new { s.Id, s.StartedUtc, s.EndedUtc, s.MeteredSeconds, s.EndReason }).ToListAsync(ct),
    };

    /// <summary>
    /// Deletes personal data immediately (documents, devices, sessions, usage, subscription row) and tombstones the account
    /// id so the same JWT can't recreate it. Billing records held by Paddle (MoR) follow Paddle's retention; cancel first.
    /// </summary>
    public async Task DeleteAsync(UserAccount u, CancellationToken ct)
    {
        var active = await _db.Subscriptions.AnyAsync(s => s.UserId == u.Id && (s.Status == "active" || s.Status == "trialing" || s.Status == "past_due"), ct);
        if (active) throw ApiException.Conflict("subscription_active", "Cancel your subscription in the billing portal before deleting the account.");
        await _db.Documents.Where(x => x.UserId == u.Id).ExecuteDeleteAsync(ct);
        await _db.Sessions.Where(x => x.UserId == u.Id).ExecuteDeleteAsync(ct);
        await _db.Devices.Where(x => x.UserId == u.Id).ExecuteDeleteAsync(ct);
        await _db.Usage.Where(x => x.UserId == u.Id).ExecuteDeleteAsync(ct);
        await _db.PreparationJobs.Where(x => x.UserId == u.Id).ExecuteDeleteAsync(ct);
        await _db.Subscriptions.Where(x => x.UserId == u.Id).ExecuteDeleteAsync(ct);
        u.Status = "deleted"; u.Email = ""; u.PrivacyJson = "{}"; u.FeatureFlagsJson = "{}"; u.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
        _db.Audit.Add(new AuditEntry { ActorUserId = u.Id, SubjectUserId = u.Id, Action = "account_deleted", AtUtc = u.UpdatedUtc });
        await _db.SaveChangesAsync(ct);
    }
}
