using System.Text.Json;

namespace InterviewAssistant.Contracts;

// ---- shared: /api/v1 contracts used by the desktop client and the backend ----

public sealed record ApiError(string Code, string Message, string? CorrelationId = null);

/// <summary>Server-authoritative entitlements. Every field is enforced (server-side, or locally for data that never leaves the PC).</summary>
public sealed record Entitlements(
    string PlanCode, string PlanName, string Status,
    int MaxProfiles, int PrepJobsPerMonth, int LiveMinutesPerPeriod, int MaxConcurrentSessions, int MaxDevices,
    int ReportRetentionDays, bool CoachMode, int TrialMinutes);

/// <summary>Customer-facing billing state, computed by the server (the desktop only maps it to text).</summary>
public static class BillingStates
{
    public const string Trial = "trial", TrialExhausted = "trial_exhausted", Active = "active", Canceling = "canceling",
        PastDue = "past_due", Paused = "paused", Expired = "expired";
}

/// <summary>
/// <see cref="LiveSecondsRemaining"/> is the plan allowance left in the billing period. <see cref="LiveSecondsAvailableToday"/>
/// additionally applies the per-account daily safety cap (what can actually be used today); -1 when unknown.
/// </summary>
public sealed record UsageSummary(DateTime PeriodStartUtc, DateTime PeriodEndUtc, int LiveSecondsUsed, int LiveSecondsRemaining,
    int PrepJobsUsed, int PrepJobsRemaining, int AnswerRequestsToday, int LiveSecondsAvailableToday = -1, int DailyLiveMinutesCap = 0);

public sealed record AccountDto(Guid UserId, string Email, string Status, Entitlements Entitlements, UsageSummary Usage,
    SubscriptionDto? Subscription, DateTime CreatedUtc, string BillingState);

public sealed record SubscriptionDto(string PlanCode, string Status, DateTime? CurrentPeriodEndUtc, bool CancelAtPeriodEnd, DateTime? GraceUntilUtc);

public sealed record RegisterDeviceRequest(string InstallationId, string Name, string Platform, string ClientVersion);
public sealed record DeviceDto(Guid Id, string Name, string Platform, string ClientVersion, DateTime LastSeenUtc, bool Revoked, bool IsCurrent);

/// <summary>Opaque client-owned JSON payload (profile/target/report) stored per user with optimistic concurrency.</summary>
public sealed record SyncDocument(Guid Id, string Kind, Guid? ParentId, JsonElement Data, long Version, DateTime UpdatedUtc);
public sealed record UpsertDocumentRequest(Guid? ParentId, JsonElement Data, long? ExpectedVersion);

public sealed record StartSessionRequest(Guid DeviceId, Guid? TargetId, string IdempotencyKey, string? InterviewLanguage);
public sealed record RealtimeCredential(string ClientSecret, DateTime ExpiresAtUtc, string Model, string WebSocketUrl, string TranscriptionPrompt);
/// <summary><see cref="RemainingSeconds"/> = what this session may still use (min of plan period, today's cap, operator cap).</summary>
public sealed record StartSessionResponse(Guid SessionId, RealtimeCredential Realtime, int HeartbeatIntervalSeconds, int MaxDurationSeconds, int RemainingSeconds,
    int PeriodRemainingSeconds = -1, int TodayRemainingSeconds = -1);
public sealed record HeartbeatRequest(long Sequence);
public sealed record HeartbeatResponse(int RemainingSeconds, bool Active, string? EndReason, int PeriodRemainingSeconds = -1, int TodayRemainingSeconds = -1);

public sealed record AnswerMessage(string Role, string Content);
/// <summary>
/// Purpose: "live" (requires an active session lease), "prep" (requires a preparation job) or "manual" (typed question;
/// no lease, never bills live minutes, entitlement- and rate-limited). DeviceId lets the server reject revoked devices.
/// </summary>
public sealed record AnswerStreamRequest(Guid? SessionId, Guid? PrepJobId, IReadOnlyList<AnswerMessage> Messages, int MaxTokens, string Purpose, Guid? DeviceId = null);

public static class AnswerPurposes { public const string Live = "live", Prep = "prep", Manual = "manual"; }

/// <summary>
/// Minimal per-interview transcription context synced as the user's "target" document (no résumé or job-description
/// text): it biases speech recognition toward the interview's names and terms.
/// </summary>
public sealed record TranscriptionContextDocument(string JobTitle, string Company, IReadOnlyList<string> Vocabulary, string Language);

public sealed record PrepJobResponse(Guid JobId, int PrepJobsRemaining);

/// <summary>Price comes from Paddle (authoritative); PriceDisplay is null when Paddle could not be reached.</summary>
public sealed record PlanDto(string Code, string Name, Entitlements Entitlements, bool Purchasable, string? PriceDisplay);
public sealed record CheckoutRequest(string PlanCode);
public sealed record UrlResponse(string Url);

public sealed record RuntimeConfig(
    string TranscriptionModel, IReadOnlyList<string> TranscriptionFallbacks, string AnswerModel, IReadOnlyList<string> AnswerFallbacks,
    string PreparationModel, int AnswerMaxTokens, int FirstTokenTimeoutMs, IReadOnlyDictionary<string, bool> Features,
    bool Maintenance, string? MaintenanceMessage, string MinimumClientVersion, int OfflineGraceHours,
    AuthClientConfig? Auth = null, ProductLinks? Links = null, string BillingEnvironment = "sandbox");

/// <summary>Public sign-in configuration for the desktop (publishable values only — never a secret).</summary>
public sealed record AuthClientConfig(bool Available, string SupabaseUrl, string PublishableKey, bool EmailPassword, IReadOnlyList<string> OAuthProviders, string? EmailConfirmedRedirectUrl);

/// <summary>Customer-facing links; null when the operator has not configured them (never invented).</summary>
public sealed record ProductLinks(string? PrivacyPolicy, string? Terms, string? BillingTerms, string? Support, string? SupportEmail);

