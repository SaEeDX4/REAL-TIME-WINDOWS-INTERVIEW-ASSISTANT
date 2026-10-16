namespace InterviewAssistant.Backend.Data;

public sealed class UserAccount
{
    public Guid Id { get; set; }                       // = Supabase auth user id (JWT "sub")
    public string Email { get; set; } = "";
    public string Status { get; set; } = "active";     // active | disabled | deleted
    public string PrivacyJson { get; set; } = "{}";
    public string FeatureFlagsJson { get; set; } = "{}";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>Set when the account is deleted and its sign-in identity still has to be removed from the auth provider.</summary>
    public DateTime? IdentityDeletionPendingSinceUtc { get; set; }
    public DateTime? IdentityDeletedUtc { get; set; }
    public int IdentityDeletionAttempts { get; set; }
}

public sealed class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "paddle";
    public string? ProviderCustomerId { get; set; }
    public string? ProviderSubscriptionId { get; set; }
    public string PlanCode { get; set; } = "";
    public string Status { get; set; } = "none";       // trialing | active | past_due | paused | canceled | none
    public DateTime? CurrentPeriodStartUtc { get; set; }
    public DateTime? CurrentPeriodEndUtc { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTime? GraceUntilUtc { get; set; }
    public DateTime? LastEventOccurredUtc { get; set; } // ordering guard for out-of-order webhooks
    /// <summary>The latest completed payment (transaction) of this subscription and when it was billed.</summary>
    public string? CurrentTransactionId { get; set; }
    public DateTime? CurrentTransactionBilledUtc { get; set; }
    /// <summary>
    /// Paid access withdrawn because the current payment was fully refunded or charged back (derived from
    /// <see cref="BillingAdjustment"/> rows; cleared by a newer payment or a reversed chargeback).
    /// </summary>
    public DateTime? AccessRevokedUtc { get; set; }
    public string? RevocationReason { get; set; }       // refund | chargeback
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>A Paddle adjustment (refund, credit, chargeback, chargeback reversal), latest known state per adjustment id.</summary>
public sealed class BillingAdjustment
{
    public string Id { get; set; } = "";                 // adj_…
    public string? ProviderSubscriptionId { get; set; }
    public string? ProviderCustomerId { get; set; }
    public string TransactionId { get; set; } = "";
    public string Action { get; set; } = "";             // refund | credit | chargeback | chargeback_reverse | chargeback_warning | credit_reverse
    public string Type { get; set; } = "";               // full | partial
    public string Status { get; set; } = "";             // pending_approval | approved | rejected | reversed
    public DateTime OccurredUtc { get; set; }            // of the event that produced this state (ordering guard)
    public DateTime UpdatedUtc { get; set; }
}

public sealed class Device
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string InstallationIdHash { get; set; } = ""; // SHA-256 of the random installation id (no hardware fingerprinting)
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
}

public sealed class InterviewSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? TargetId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Status { get; set; } = "active";     // active | stopped | expired | exhausted
    public DateTime StartedUtc { get; set; }
    public DateTime LastHeartbeatUtc { get; set; }
    public long HeartbeatSequence { get; set; }
    public int MeteredSeconds { get; set; }
    public DateTime? EndedUtc { get; set; }
    public string? EndReason { get; set; }
    public int SecretsIssued { get; set; }
    /// <summary>Live seconds the account had left after the last heartbeat; the relay closes when they are used up.</summary>
    public int AllowanceAtHeartbeatSeconds { get; set; }
}

public sealed class UsageEntry
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? SessionId { get; set; }
    public string Kind { get; set; } = "";             // live_seconds | answer_request | prep_job
    public int Quantity { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? IdempotencyKey { get; set; }
}

public sealed class WebhookEvent
{
    public string EventId { get; set; } = "";          // provider event id (PK → idempotency)
    public string Provider { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTime OccurredUtc { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public DateTime? ProcessedUtc { get; set; }
    public string PayloadSha256 { get; set; } = "";
    public string Status { get; set; } = "received";   // received | processed | ignored | failed
    public string? Error { get; set; }
}

/// <summary>Client-owned documents (profiles, targets, reports) synced per user. Content is opaque JSON; ownership enforced.</summary>
public sealed class UserDocument
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = "";             // profile | target | report
    public Guid? ParentId { get; set; }                // target → profile, report → target
    public string DataJson { get; set; } = "{}";
    public long Version { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? RetainUntilUtc { get; set; }      // reports: plan retention
}

public sealed class PreparationJob
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? TargetId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int AiRequests { get; set; }
}

public sealed class ConfigEntry
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public Guid? SubjectUserId { get; set; }
    public string Detail { get; set; } = "";
    public DateTime AtUtc { get; set; }
}
