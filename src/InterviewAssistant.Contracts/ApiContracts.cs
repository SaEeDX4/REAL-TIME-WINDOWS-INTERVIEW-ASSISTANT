using System.Text.Json;

namespace InterviewAssistant.Contracts;

// ---- shared: /api/v1 contracts used by the desktop client and the backend ----

public sealed record ApiError(string Code, string Message, string? CorrelationId = null);

public sealed record Entitlements(
    string PlanCode, string PlanName, string Status,
    int MaxProfiles, int PrepJobsPerMonth, int LiveMinutesPerPeriod, int MaxConcurrentSessions, int MaxDevices,
    int ReportRetentionDays, bool CoachMode, bool AdvancedReports, bool CustomAnswerModes, string ModelTier, int TrialMinutes,
    int MaxLanguages);

public sealed record UsageSummary(DateTime PeriodStartUtc, DateTime PeriodEndUtc, int LiveSecondsUsed, int LiveSecondsRemaining,
    int PrepJobsUsed, int PrepJobsRemaining, int AnswerRequestsToday);

public sealed record AccountDto(Guid UserId, string Email, string Status, Entitlements Entitlements, UsageSummary Usage,
    SubscriptionDto? Subscription, DateTime CreatedUtc);

public sealed record SubscriptionDto(string PlanCode, string Status, DateTime? CurrentPeriodEndUtc, bool CancelAtPeriodEnd, DateTime? GraceUntilUtc);

public sealed record RegisterDeviceRequest(string InstallationId, string Name, string Platform, string ClientVersion);
public sealed record DeviceDto(Guid Id, string Name, string Platform, string ClientVersion, DateTime LastSeenUtc, bool Revoked, bool IsCurrent);

/// <summary>Opaque client-owned JSON payload (profile/target/report) stored per user with optimistic concurrency.</summary>
public sealed record SyncDocument(Guid Id, string Kind, Guid? ParentId, JsonElement Data, long Version, DateTime UpdatedUtc);
public sealed record UpsertDocumentRequest(Guid? ParentId, JsonElement Data, long? ExpectedVersion);

public sealed record StartSessionRequest(Guid DeviceId, Guid? TargetId, string IdempotencyKey, string? InterviewLanguage);
public sealed record RealtimeCredential(string ClientSecret, DateTime ExpiresAtUtc, string Model, string WebSocketUrl, string TranscriptionPrompt);
public sealed record StartSessionResponse(Guid SessionId, RealtimeCredential Realtime, int HeartbeatIntervalSeconds, int MaxDurationSeconds, int RemainingSeconds);
public sealed record HeartbeatRequest(long Sequence);
public sealed record HeartbeatResponse(int RemainingSeconds, bool Active, string? EndReason);

public sealed record AnswerMessage(string Role, string Content);
public sealed record AnswerStreamRequest(Guid? SessionId, Guid? PrepJobId, IReadOnlyList<AnswerMessage> Messages, int MaxTokens, string Purpose);

public sealed record PrepJobResponse(Guid JobId, int PrepJobsRemaining);

public sealed record PlanDto(string Code, string Name, Entitlements Entitlements, bool Purchasable);
public sealed record CheckoutRequest(string PlanCode);
public sealed record UrlResponse(string Url);

public sealed record RuntimeConfig(
    string TranscriptionModel, IReadOnlyList<string> TranscriptionFallbacks, string AnswerModel, IReadOnlyList<string> AnswerFallbacks,
    string PreparationModel, int AnswerMaxTokens, int FirstTokenTimeoutMs, IReadOnlyDictionary<string, bool> Features,
    bool Maintenance, string? MaintenanceMessage, string MinimumClientVersion, int OfflineGraceHours);
