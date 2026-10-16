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
    /// <summary>The e-mail belonged to an account deleted within the last year: no new trial allowance.</summary>
    public bool TrialForfeited { get; set; }
}

/// <summary>
/// Pseudonymous marker kept after an account deletion (SHA-256 of the normalised e-mail, no other data) so deleting and
/// signing up again does not grant a new trial. Purged after 365 days.
/// </summary>
public sealed class DeletedAccountMarker
{
    public string EmailHash { get; set; } = "";
    public DateTime DeletedUtc { get; set; }
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
    /// <summary>
    /// Paid access withdrawn because every payment covering the current time was fully refunded or charged back
    /// (derived from <see cref="BillingTransaction"/> and <see cref="BillingAdjustment"/> rows).
    /// </summary>
    public DateTime? AccessRevokedUtc { get; set; }
    public string? RevocationReason { get; set; }       // refund | chargeback
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>A completed Paddle payment: which subscription and which billing period it paid for.</summary>
public sealed class BillingTransaction
{
    public string Id { get; set; } = "";                 // txn_…
    public string? ProviderSubscriptionId { get; set; }
    public string Origin { get; set; } = "";             // web | subscription_recurring | subscription_payment_method_change | …
    /// <summary>Money was collected (grand total &gt; 0, and not a payment-method change).</summary>
    public bool Paid { get; set; }
    public DateTime BilledUtc { get; set; }
    public DateTime? PeriodStartUtc { get; set; }
    public DateTime? PeriodEndUtc { get; set; }
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
    /// <summary>The one relay connection allowed to carry this lease's audio (a newer connection, on any replica, wins).</summary>
    public Guid? RelayConnectionId { get; set; }
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
