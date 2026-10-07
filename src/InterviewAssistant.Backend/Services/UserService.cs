using System.Security.Claims;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

/// <summary>Resolves the authenticated user (JWT "sub") and provisions the account on first use.</summary>
public sealed class UserService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    private readonly AuthOptions _auth;
    public UserService(AppDbContext db, TimeProvider time, IOptions<AuthOptions> auth) { _db = db; _time = time; _auth = auth.Value; }

    public static Guid UserId(ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub"), out var id) ? id : throw new ApiException(401, "invalid_token", "Token has no valid subject.");

    public bool IsAdmin(ClaimsPrincipal p) => _auth.AdminUserIds.Contains(UserId(p));

    public async Task<UserAccount> GetOrCreateAsync(ClaimsPrincipal p, CancellationToken ct)
    {
        var id = UserId(p);
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (u == null)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            u = new UserAccount { Id = id, Email = p.FindFirstValue(ClaimTypes.Email) ?? p.FindFirstValue("email") ?? "", CreatedUtc = now, UpdatedUtc = now };
            _db.Users.Add(u);
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { _db.ChangeTracker.Clear(); u = await _db.Users.FirstAsync(x => x.Id == id, ct); } // concurrent first requests
        }
        if (u.Status == "disabled") throw ApiException.Forbidden("account_disabled", "This account is disabled. Contact support.");
        if (u.Status == "deleted") throw ApiException.Forbidden("account_deleted", "This account was deleted.");
        return u;
    }
}
