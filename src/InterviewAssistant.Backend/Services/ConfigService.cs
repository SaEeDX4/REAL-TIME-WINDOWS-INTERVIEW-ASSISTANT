using System.Text.Json;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

/// <summary>
/// Server-managed runtime config (model routing, timeouts, feature flags, maintenance, minimum client version).
/// Operators change rows in runtime_config (admin API) — no desktop release needed when a model changes.
/// Disabled models are skipped in favour of the configured fallbacks (kill switch).
/// </summary>
public sealed class ConfigService
{
    private readonly AppDbContext _db;
    private readonly SafetyOptions _safety;
    private readonly AuthOptions _auth;
    private readonly ProductOptions _product;
    private readonly PaddleOptions _paddle;
    public ConfigService(AppDbContext db, IOptions<SafetyOptions> safety, IOptions<AuthOptions> auth, IOptions<ProductOptions> product, IOptions<PaddleOptions> paddle)
    { _db = db; _safety = safety.Value; _auth = auth.Value; _product = product.Value; _paddle = paddle.Value; }

    /// <summary>
    /// Defaults (operators override per key in runtime_config). Model ids verified against OpenAI's current model list:
    /// realtime transcription accepts gpt-4o-transcribe / gpt-4o-mini-transcribe; answers use gpt-5.4-mini (reasoning
    /// effort "none") with gpt-4.1-mini as fallback.
    /// </summary>
    public static readonly RuntimeConfig Defaults = new(
        "gpt-4o-transcribe", new[] { "gpt-4o-mini-transcribe" },
        "gpt-5.4-mini", new[] { "gpt-4.1-mini" }, "gpt-5.4-mini", 260, 8000,
        new Dictionary<string, bool> { ["coach_mode"] = true, ["adaptive_interruptions"] = true, ["report_export"] = true },
        false, null, "2.0.0", 72);

    private AuthClientConfig PublicAuth()
    {
        var url = _auth.EffectiveSupabaseUrl;
        var available = url.StartsWith("https://", StringComparison.Ordinal) && _auth.PublishableKey.Length > 0;
        var confirmed = _product.PublicBaseUrl.Length > 0 ? _product.PublicBaseUrl.TrimEnd('/') + "/auth/confirmed" : null;
        return new AuthClientConfig(available, url, available ? _auth.PublishableKey : "", _auth.EmailPassword, _auth.OAuthProviders.Where(p => p.Length > 0).ToList(), confirmed);
    }

    private ProductLinks PublicLinks() => new(Opt(_product.PrivacyPolicyUrl), Opt(_product.TermsUrl), Opt(_product.BillingTermsUrl), Opt(_product.SupportUrl), Opt(_product.SupportEmail));
    private static string? Opt(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public async Task<RuntimeConfig> GetAsync(CancellationToken ct)
    {
        var rows = await _db.Config.AsNoTracking().ToDictionaryAsync(r => r.Key, r => r.ValueJson, ct);
        T Get<T>(string key, T fallback) { try { return rows.TryGetValue(key, out var j) ? JsonSerializer.Deserialize<T>(j) ?? fallback : fallback; } catch (JsonException) { return fallback; } }
        var disabled = Get("disabled_models", new List<string>());
        string Pick(string primary, IReadOnlyList<string> fallbacks) => new[] { primary }.Concat(fallbacks).FirstOrDefault(m => !disabled.Contains(m)) ?? primary;
        var tModel = Get("transcription_model", Defaults.TranscriptionModel);
        var tFall = Get<List<string>>("transcription_fallbacks", Defaults.TranscriptionFallbacks.ToList());
        var aModel = Get("answer_model", Defaults.AnswerModel);
        var aFall = Get<List<string>>("answer_fallbacks", Defaults.AnswerFallbacks.ToList());
        var features = new Dictionary<string, bool>(Defaults.Features);
        foreach (var kv in Get("features", new Dictionary<string, bool>())) features[kv.Key] = kv.Value;
        return new RuntimeConfig(Pick(tModel, tFall), tFall.Where(m => !disabled.Contains(m)).ToList(), Pick(aModel, aFall), aFall.Where(m => !disabled.Contains(m)).ToList(),
            Get("preparation_model", Defaults.PreparationModel), Get("answer_max_tokens", Defaults.AnswerMaxTokens), Get("first_token_timeout_ms", Defaults.FirstTokenTimeoutMs),
            features, Get("maintenance", false), Get<string?>("maintenance_message", null), Get("minimum_client_version", _safety.MinimumClientVersion), _safety.OfflineGraceHours,
            PublicAuth(), PublicLinks(), _paddle.Environment);
    }

    public async Task SetAsync(string key, JsonElement value, string actor, TimeProvider time, CancellationToken ct)
    {
        var row = await _db.Config.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (row == null) _db.Config.Add(row = new ConfigEntry { Key = key });
        row.ValueJson = value.GetRawText(); row.UpdatedUtc = time.GetUtcNow().UtcDateTime; row.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// SemVer 2.0 precedence check: a pre-release sorts below its release (2.1.0-rc.2 &lt; 2.1.0), pre-release identifiers
    /// compare numerically or ordinally per spec (rc.2 &lt; rc.10), build metadata (+abc) is ignored. Unparseable client
    /// versions never pass; an unparseable minimum is a configuration error and does not lock clients out.
    /// </summary>
    public static bool VersionAtLeast(string? client, string minimum)
    {
        if (!SemVer.TryParse(client, out var c)) return false;
        if (!SemVer.TryParse(minimum, out var m)) return true;
        return SemVer.Compare(c, m) >= 0;
    }
}

/// <summary>Minimal SemVer 2.0 value (major.minor.patch[-pre][+build]) used for the minimum-client-version gate.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string[] Pre)
{
    public static bool TryParse(string? s, out SemVer v)
    {
        v = default;
        s = s?.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var pre = dash >= 0 ? s[(dash + 1)..].Split('.') : [];
        var parts = core.Split('.');
        if (parts.Length is < 2 or > 4) return false;   // tolerate "2.1" and a 4-part assembly version
        var n = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var x)) return false;
            if (i < 3) n[i] = x;
        }
        if (pre.Any(p => p.Length == 0 || !p.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'))) return false;
        v = new SemVer(n[0], n[1], n[2], pre);
        return true;
    }

    public static int Compare(SemVer a, SemVer b)
    {
        int c = a.Major.CompareTo(b.Major);
        if (c == 0) c = a.Minor.CompareTo(b.Minor);
        if (c == 0) c = a.Patch.CompareTo(b.Patch);
        if (c != 0) return c;
        if (a.Pre.Length == 0 || b.Pre.Length == 0) return b.Pre.Length.CompareTo(a.Pre.Length);   // release > pre-release
        for (int i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            bool an = long.TryParse(a.Pre[i], out var ai), bn = long.TryParse(b.Pre[i], out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a.Pre[i], b.Pre[i]);
            if (c != 0) return c;
        }
        return a.Pre.Length.CompareTo(b.Pre.Length);
    }
}
