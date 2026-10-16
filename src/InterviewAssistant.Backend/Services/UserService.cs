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

    /// <summary>
    /// Time of the most recent primary authentication (password or provider sign-in) from the auth provider's <c>amr</c>
    /// claim. A refreshed access token keeps the original authentication time, so this proves a recent sign-in.
    /// Null when the token carries no authentication-method timestamps.
    /// </summary>
    public static DateTimeOffset? LastAuthenticatedAt(ClaimsPrincipal p)
    {
        long best = 0;
        foreach (var c in p.FindAll("amr"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(c.Value);
                foreach (var e in doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : new[] { doc.RootElement })
                    if (e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var t)) best = Math.Max(best, t);
            }
            catch (System.Text.Json.JsonException) { }
        }
        return best > 0 ? DateTimeOffset.FromUnixTimeSeconds(best) : null;
    }

    /// <summary>Sensitive actions (data export, account deletion) require a sign-in within <paramref name="window"/>.</summary>
    public static void RequireRecentAuthentication(ClaimsPrincipal p, DateTimeOffset now, TimeSpan window)
    {
        if (LastAuthenticatedAt(p) is not { } at || now - at > window || at - now > TimeSpan.FromMinutes(2))
            throw ApiException.Forbidden("reauth_required", "Confirm your password again to continue.");
    }

    public static Guid UserId(ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub"), out var id) ? id : throw new ApiException(401, "invalid_token", "Token has no valid subject.");

    public bool IsAdmin(ClaimsPrincipal p) => _auth.AdminUserIds.Contains(UserId(p));

    public async Task<UserAccount> GetOrCreateAsync(ClaimsPrincipal p, CancellationToken ct)
    {
        var id = UserId(p);
        // Anonymous sign-ins (if ever enabled in the auth provider) must not create trial accounts.
        if (string.Equals(p.FindFirstValue("is_anonymous"), "true", StringComparison.OrdinalIgnoreCase))
            throw ApiException.Forbidden("anonymous_not_allowed", "Sign in with an e-mail address or a sign-in provider.");
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (u == null)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            u = new UserAccount { Id = id, Email = p.FindFirstValue(ClaimTypes.Email) ?? p.FindFirstValue("email") ?? "", CreatedUtc = now, UpdatedUtc = now };
            // Deleting an account and signing up again with the same e-mail does not grant a second trial.
            if (DeletedAccountMarkers.Hash(u.Email) is { } h && await _db.DeletedAccountMarkers.AnyAsync(m => m.EmailHash == h && m.DeletedUtc > now - DeletedAccountMarkers.Retention, ct))
                u.TrialForfeited = true;
            _db.Users.Add(u);
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { _db.ChangeTracker.Clear(); u = await _db.Users.FirstAsync(x => x.Id == id, ct); } // concurrent first requests
        }
        if (u.Status == "disabled") throw ApiException.Forbidden("account_disabled", "This account is disabled. Contact support.");
        if (u.Status == "deleted") throw ApiException.Forbidden("account_deleted", "This account was deleted.");
        return u;
    }
}
