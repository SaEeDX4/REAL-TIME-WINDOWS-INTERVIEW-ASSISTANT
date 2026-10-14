namespace InterviewAssistant.Backend.Infrastructure;

public sealed class AuthOptions
{
    /// <summary>Supabase issuer, e.g. https://PROJECT.supabase.co/auth/v1</summary>
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "authenticated";
    /// <summary>JWKS (asymmetric signing keys, required in production). HS256 JwtSecret is for local development/tests only.</summary>
    public string JwksUrl { get; set; } = "";
    public string JwtSecret { get; set; } = "";
    public List<Guid> AdminUserIds { get; set; } = new();
    /// <summary>Supabase project URL for the desktop; derived from <see cref="Issuer"/> when empty.</summary>
    public string SupabaseUrl { get; set; } = "";
    /// <summary>Supabase publishable (anon) key — public by design, served to the desktop via /api/v1/config.</summary>
    public string PublishableKey { get; set; } = "";
    /// <summary>OAuth providers enabled in Supabase that the desktop may offer (e.g. "google"). Empty = e-mail only.</summary>
    public List<string> OAuthProviders { get; set; } = new();
    public bool EmailPassword { get; set; } = true;

    public string EffectiveSupabaseUrl =>
        SupabaseUrl.Length > 0 ? SupabaseUrl.TrimEnd('/') : Issuer.EndsWith("/auth/v1", StringComparison.Ordinal) ? Issuer[..^"/auth/v1".Length] : "";
}

public sealed class OpenAiOptions
{
    public string ApiKey { get; set; } = "";              // server-side only (secret store / env var)
    public string BaseUrl { get; set; } = "https://api.openai.com";
    public int ClientSecretTtlSeconds { get; set; } = 600;
}

public sealed class PaddleOptions
{
    public string ApiBaseUrl { get; set; } = "https://sandbox-api.paddle.com"; // live: https://api.paddle.com
    public string ApiKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    /// <summary>Client-side token (public, "test_…" / "live_…") used by the /pay checkout page.</summary>
    public string ClientToken { get; set; } = "";
    public int SignatureToleranceSeconds { get; set; } = 300;
    public int PastDueGraceDays { get; set; } = 7;

    public bool IsSandbox => ApiBaseUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase);
    public string Environment => IsSandbox ? "sandbox" : "live";
}

/// <summary>Public product surfaces. Unset values stay unset (the app hides the link) — nothing is invented.</summary>
public sealed class ProductOptions
{
    /// <summary>Public base URL of this API (used for the e-mail-confirmed page and checkout links), e.g. https://api.example.com</summary>
    public string PublicBaseUrl { get; set; } = "";
    public string PrivacyPolicyUrl { get; set; } = "";
    public string TermsUrl { get; set; } = "";
    public string BillingTermsUrl { get; set; } = "";
    public string SupportUrl { get; set; } = "";
    public string SupportEmail { get; set; } = "";
}

public sealed class SafetyOptions
{
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int HeartbeatTimeoutSeconds { get; set; } = 90;
    public int MaxSessionMinutes { get; set; } = 120;
    public int PerAccountDailyLiveMinutes { get; set; } = 240;
    public int GlobalDailyLiveMinutes { get; set; } = 100_000;
    public int AnswersPerMinute { get; set; } = 30;
    public int AnswersPerDayPerAccount { get; set; } = 2000;
    public int GlobalAnswersPerDay { get; set; } = 1_000_000;
    /// <summary>Typed (manual) answers per account per UTC day — a subset of AnswersPerDayPerAccount, never billed as live minutes.</summary>
    public int ManualAnswersPerDayPerAccount { get; set; } = 300;
    /// <summary>Upper bound for the transcription bias prompt built from the user's target document.</summary>
    public int MaxTranscriptionPromptChars { get; set; } = 1500;
    public int MaxAnswerTokens { get; set; } = 600;
    public int MaxDocumentBytes { get; set; } = 2 * 1024 * 1024;
    /// <summary>Upper bound on stored targets/reports per account (profiles are limited by the plan).</summary>
    public int MaxDocumentsPerKind { get; set; } = 500;
    public int MaxRequestBodyBytes { get; set; } = 4 * 1024 * 1024;
    public int OfflineGraceHours { get; set; } = 72;
    public string MinimumClientVersion { get; set; } = "2.0.0";
}

/// <summary>Plan catalog — DATA, not code. Prices live in Paddle; only price ids are referenced here.</summary>
public sealed class PlanDefinition
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Purchasable { get; set; }
    public List<string> PaddlePriceIds { get; set; } = new();
    /// <summary>Retired codes still stored in old rows (e.g. "pro-dev") that resolve to this plan.</summary>
    public List<string> LegacyCodes { get; set; } = new();
    public int MaxProfiles { get; set; } = 1;
    public int PrepJobsPerMonth { get; set; } = 3;
    public int LiveMinutesPerPeriod { get; set; }
    public int MaxConcurrentSessions { get; set; } = 1;
    public int MaxDevices { get; set; } = 1;
    public int ReportRetentionDays { get; set; } = 30;
    public bool CoachMode { get; set; }
    public int TrialMinutes { get; set; }
}

public sealed class PlanCatalog
{
    public string DefaultPlanCode { get; set; } = "trial";
    public List<PlanDefinition> Plans { get; set; } = new();
    public PlanDefinition? Find(string? code) =>
        code == null ? null : Plans.FirstOrDefault(p => p.Code == code) ?? Plans.FirstOrDefault(p => p.LegacyCodes.Contains(code));
    public PlanDefinition Get(string? code) => Find(code) ?? Plans.First(p => p.Code == DefaultPlanCode);
    public PlanDefinition? ByPaddlePrice(string? priceId) => priceId == null ? null : Plans.FirstOrDefault(p => p.PaddlePriceIds.Contains(priceId));
}

/// <summary>Startup validation: misconfiguration fails fast with a clear message instead of failing customer requests later.</summary>
public static class ConfigValidation
{
    public static IReadOnlyList<string> Validate(IConfiguration cfg, bool production, AuthOptions auth, OpenAiOptions ai, PaddleOptions paddle, PlanCatalog plans, ProductOptions product)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(cfg.GetConnectionString("Db"))) e.Add("ConnectionStrings__Db is required.");
        if (!Uri.TryCreate(auth.Issuer, UriKind.Absolute, out _)) e.Add("Auth__Issuer must be the Supabase issuer URL (https://<project>.supabase.co/auth/v1).");
        if (string.IsNullOrWhiteSpace(auth.Audience)) e.Add("Auth__Audience is required.");
        if (string.IsNullOrWhiteSpace(auth.JwksUrl) && string.IsNullOrWhiteSpace(auth.JwtSecret)) e.Add("Auth__JwksUrl (or Auth__JwtSecret outside production) is required.");
        if (production && string.IsNullOrWhiteSpace(auth.JwksUrl)) e.Add("Auth__JwksUrl is required in production (asymmetric JWT verification).");
        if (production && !string.IsNullOrWhiteSpace(auth.JwtSecret)) e.Add("Auth__JwtSecret must not be set in production; use Auth__JwksUrl.");
        if (production && string.IsNullOrWhiteSpace(ai.ApiKey)) e.Add("OpenAI__ApiKey is required.");
        if (production && string.IsNullOrWhiteSpace(paddle.ApiKey)) e.Add("Paddle__ApiKey is required.");
        if (production && string.IsNullOrWhiteSpace(paddle.WebhookSecret)) e.Add("Paddle__WebhookSecret is required.");
        if (paddle.ApiKey.StartsWith("pdl_live_", StringComparison.Ordinal) && paddle.IsSandbox) e.Add("Paddle__ApiKey is a LIVE key but Paddle__ApiBaseUrl points to the sandbox.");
        if (paddle.ApiKey.StartsWith("pdl_sdbx_", StringComparison.Ordinal) && !paddle.IsSandbox) e.Add("Paddle__ApiKey is a SANDBOX key but Paddle__ApiBaseUrl points to live.");
        if (paddle.ClientToken.StartsWith("live_", StringComparison.Ordinal) && paddle.IsSandbox) e.Add("Paddle__ClientToken is a live token but billing runs in the sandbox.");
        if (paddle.ClientToken.StartsWith("test_", StringComparison.Ordinal) && !paddle.IsSandbox) e.Add("Paddle__ClientToken is a test token but billing runs live.");

        if (plans.Plans.Count == 0) e.Add("PlanCatalog has no plans.");
        if (plans.Plans.Select(p => p.Code).Distinct().Count() != plans.Plans.Count) e.Add("PlanCatalog plan codes must be unique.");
        if (plans.Plans.All(p => p.Code != plans.DefaultPlanCode)) e.Add($"PlanCatalog__DefaultPlanCode '{plans.DefaultPlanCode}' is not a defined plan.");
        foreach (var p in plans.Plans)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) e.Add($"Plan '{p.Code}' needs a customer-facing name.");
            if (p.Name.Contains("seed", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("dev", StringComparison.OrdinalIgnoreCase)) e.Add($"Plan '{p.Code}' has a development name ('{p.Name}').");
            if (p.Purchasable && production && (p.PaddlePriceIds.Count == 0 || p.PaddlePriceIds.Any(id => !id.StartsWith("pri_", StringComparison.Ordinal) || id.Contains("SET_IN"))))
                e.Add($"Plan '{p.Code}' is purchasable but has no real Paddle price id (PlanCatalog__Plans__N__PaddlePriceIds__0).");
            if (p.LiveMinutesPerPeriod < 0 || p.TrialMinutes < 0 || p.MaxDevices < 1 || p.MaxConcurrentSessions < 1) e.Add($"Plan '{p.Code}' has invalid limits.");
        }
        foreach (var (name, url) in new[] { ("Product__PublicBaseUrl", product.PublicBaseUrl), ("Product__PrivacyPolicyUrl", product.PrivacyPolicyUrl), ("Product__TermsUrl", product.TermsUrl), ("Product__BillingTermsUrl", product.BillingTermsUrl), ("Product__SupportUrl", product.SupportUrl) })
            if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)) e.Add($"{name} must be an https URL.");
        return e;
    }
}
