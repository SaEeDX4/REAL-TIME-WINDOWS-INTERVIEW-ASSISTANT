namespace InterviewAssistant.Backend.Infrastructure;

public sealed class AuthOptions
{
    /// <summary>Supabase issuer, e.g. https://PROJECT.supabase.co/auth/v1</summary>
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "authenticated";
    /// <summary>JWKS (asymmetric signing keys, recommended). If empty, HS256 with JwtSecret.</summary>
    public string JwksUrl { get; set; } = "";
    public string JwtSecret { get; set; } = "";
    public List<Guid> AdminUserIds { get; set; } = new();
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
    public int SignatureToleranceSeconds { get; set; } = 300;
    public int PastDueGraceDays { get; set; } = 7;
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
    public int MaxAnswerTokens { get; set; } = 600;
    public int MaxDocumentBytes { get; set; } = 2 * 1024 * 1024;
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
    public int MaxProfiles { get; set; } = 1;
    public int PrepJobsPerMonth { get; set; } = 3;
    public int LiveMinutesPerPeriod { get; set; } = 0;
    public int MaxConcurrentSessions { get; set; } = 1;
    public int MaxDevices { get; set; } = 1;
    public int ReportRetentionDays { get; set; } = 30;
    public bool CoachMode { get; set; }
    public bool AdvancedReports { get; set; }
    public bool CustomAnswerModes { get; set; }
    public string ModelTier { get; set; } = "standard";
    public int TrialMinutes { get; set; }
    public int MaxLanguages { get; set; } = 10;
}

public sealed class PlanCatalog
{
    public string DefaultPlanCode { get; set; } = "trial";
    public List<PlanDefinition> Plans { get; set; } = new();
    public PlanDefinition Get(string? code) => Plans.FirstOrDefault(p => p.Code == code) ?? Plans.First(p => p.Code == DefaultPlanCode);
    public PlanDefinition? ByPaddlePrice(string? priceId) => priceId == null ? null : Plans.FirstOrDefault(p => p.PaddlePriceIds.Contains(priceId));
}
