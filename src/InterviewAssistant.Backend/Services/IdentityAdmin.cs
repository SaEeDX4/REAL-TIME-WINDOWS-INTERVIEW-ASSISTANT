using System.Net;
using System.Net.Http.Headers;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

public enum IdentityDeletion { Deleted, Pending, NotConfigured }

/// <summary>Removes a user's sign-in identity from the auth provider.</summary>
public interface IIdentityAdmin
{
    bool Configured { get; }
    /// <summary>True when the identity is gone (deleted now, or it no longer existed).</summary>
    Task<bool> DeleteUserAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Supabase Auth admin API: <c>DELETE /auth/v1/admin/users/{id}</c> with the server-only secret key (hard delete:
/// the e-mail address, password hash and linked OAuth identities are removed; the same e-mail can sign up again as a
/// new, empty account).
/// </summary>
public sealed class SupabaseIdentityAdmin : IIdentityAdmin
{
    private readonly HttpClient _http;
    private readonly AuthOptions _opt;
    private readonly ILogger<SupabaseIdentityAdmin> _log;
    public SupabaseIdentityAdmin(HttpClient http, IOptions<AuthOptions> opt, ILogger<SupabaseIdentityAdmin> log) { _http = http; _opt = opt.Value; _log = log; }

    public bool Configured => _opt.ServiceRoleKey.Length > 0 && _opt.EffectiveSupabaseUrl.Length > 0;

    public async Task<bool> DeleteUserAsync(Guid userId, CancellationToken ct)
    {
        if (!Configured) return false;
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_opt.EffectiveSupabaseUrl}/auth/v1/admin/users/{userId}");
        req.Headers.Add("apikey", _opt.ServiceRoleKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ServiceRoleKey);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotFound) return true;
            _log.LogWarning("Sign-in identity deletion for user {UserId} failed with HTTP {Status}", userId, (int)resp.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Sign-in identity deletion for user {UserId} failed: {Error}", userId, ex.GetType().Name);
            return false;
        }
    }
}

/// <summary>Completes queued identity deletions (provider outage at deletion time, or the key configured later).</summary>
public sealed class IdentityDeletionService
{
    private readonly AppDbContext _db;
    private readonly IIdentityAdmin _admin;
    private readonly TimeProvider _time;
    private readonly ILogger<IdentityDeletionService> _log;
    public IdentityDeletionService(AppDbContext db, IIdentityAdmin admin, TimeProvider time, ILogger<IdentityDeletionService> log) { _db = db; _admin = admin; _time = time; _log = log; }

    /// <summary>Attempts the deletion for one account whose data was already deleted; records the outcome.</summary>
    public async Task<IdentityDeletion> CompleteAsync(UserAccount u, CancellationToken ct)
    {
        if (u.IdentityDeletedUtc != null) return IdentityDeletion.Deleted;
        if (!_admin.Configured) return IdentityDeletion.NotConfigured;
        u.IdentityDeletionAttempts++;
        var ok = await _admin.DeleteUserAsync(u.Id, ct);
        if (ok) { u.IdentityDeletedUtc = _time.GetUtcNow().UtcDateTime; u.IdentityDeletionPendingSinceUtc = null; }
        await _db.SaveChangesAsync(ct);
        if (ok) _log.LogInformation("Sign-in identity of deleted account {UserId} removed", u.Id);
        return ok ? IdentityDeletion.Deleted : IdentityDeletion.Pending;
    }

    /// <summary>Retries pending deletions (bounded batch; called by the background sweeper).</summary>
    public async Task<int> RetryPendingAsync(CancellationToken ct)
    {
        if (!_admin.Configured) return 0;
        var pending = await _db.Users.Where(u => u.Status == "deleted" && u.IdentityDeletionPendingSinceUtc != null && u.IdentityDeletedUtc == null)
            .OrderBy(u => u.IdentityDeletionPendingSinceUtc).Take(50).ToListAsync(ct);
        int done = 0;
        foreach (var u in pending) if (await CompleteAsync(u, ct) == IdentityDeletion.Deleted) done++;
        return done;
    }

    public Task<int> PendingCountAsync(CancellationToken ct) =>
        _db.Users.CountAsync(u => u.Status == "deleted" && u.IdentityDeletionPendingSinceUtc != null && u.IdentityDeletedUtc == null, ct);
}
