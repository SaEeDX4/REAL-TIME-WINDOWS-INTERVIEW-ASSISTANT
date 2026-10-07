using Microsoft.IdentityModel.Tokens;

namespace InterviewAssistant.Backend.Infrastructure;

/// <summary>Caches the identity provider's JSON Web Key Set (refresh hourly, or immediately on unknown kid, max once/min).</summary>
public static class JwksCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static IList<SecurityKey> _keys = new List<SecurityKey>();
    private static DateTime _fetched = DateTime.MinValue;
    private static readonly object Gate = new();

    public static IEnumerable<SecurityKey> GetKeys(string url, string? kid)
    {
        lock (Gate)
        {
            var stale = DateTime.UtcNow - _fetched > TimeSpan.FromHours(1);
            var unknownKid = kid != null && _keys.All(k => k.KeyId != kid) && DateTime.UtcNow - _fetched > TimeSpan.FromMinutes(1);
            if (stale || unknownKid)
            {
                try { _keys = new JsonWebKeySet(Http.GetStringAsync(url).GetAwaiter().GetResult()).GetSigningKeys(); _fetched = DateTime.UtcNow; }
                catch (HttpRequestException) { /* keep previous keys; validation fails closed if none */ }
            }
            return kid == null ? _keys : _keys.Where(k => k.KeyId == kid);
        }
    }
}
