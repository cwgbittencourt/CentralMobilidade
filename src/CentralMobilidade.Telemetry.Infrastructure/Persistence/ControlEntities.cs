using System;
using CentralMobilidade.Telemetry.Application.Abstractions;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence;

// Persistence rows only. Lifecycle rules and the benchmark-approved identity adapter are separate tasks.
public sealed class IngestionBatch
{
    public Guid BatchId
    {
        get; set;
    }
    public string TraceId { get; set; } = "";
    public string ProducerId { get; set; } = "";
    public string ContractVersion { get; set; } = "v1";
    public BatchLifecycle State
    {
        get; set;
    }
    public DateTimeOffset ReceivedAt
    {
        get; set;
    }
    public DateTimeOffset? DurableAt
    {
        get; set;
    }
    public DateTimeOffset? ProcessingStartedAt
    {
        get; set;
    }
    public DateTimeOffset? CompletedAt
    {
        get; set;
    }
    public DateTimeOffset ExpiresAt
    {
        get; set;
    }
    public long CompressedBytes
    {
        get; set;
    }
    public long? DecompressedBytes
    {
        get; set;
    }
    public long? TotalLines
    {
        get; set;
    }
    public long Accepted
    {
        get; set;
    }
    public long Rejected
    {
        get; set;
    }
    public long Replayed
    {
        get; set;
    }
    public int RetryCount
    {
        get; set;
    }
    public string? TerminalErrorCode
    {
        get; set;
    }
    public string[] CompanyScope { get; set; } = [];
    public bool CompanyScopeComplete
    {
        get; set;
    }
    public long RowVersion
    {
        get; set;
    }
}
public sealed class LineProcessingResult
{
    public Guid BatchId
    {
        get; set;
    }
    public long LineNumber
    {
        get; set;
    }
    public LineOutcome Outcome
    {
        get; set;
    }
    public string? ErrorCode
    {
        get; set;
    }
    public string? CompanyId
    {
        get; set;
    }
    public string? VehicleId
    {
        get; set;
    }
    public string SafeDigest { get; set; } = "";
    public Guid? QuarantineId
    {
        get; set;
    }
    public DateTimeOffset OccurredAt
    {
        get; set;
    }
}
public sealed class QuarantinedRejection
{
    public Guid QuarantineId
    {
        get; set;
    }
    public Guid BatchId
    {
        get; set;
    }
    public long LineNumber
    {
        get; set;
    }
    public string ErrorCode { get; set; } = "";
    public string EncryptedContentLocation { get; set; } = "";
    public string ContentDigest { get; set; } = "";
    public DateTimeOffset CreatedAt
    {
        get; set;
    }
    public DateTimeOffset DeleteAfter
    {
        get; set;
    }
    public DateTimeOffset? DeletedAt
    {
        get; set;
    }
}
public sealed class EventIdentityGuard
{
    public string CompanyId { get; set; } = "";
    public string VehicleId { get; set; } = "";
    public DateTimeOffset EventTimestamp
    {
        get; set;
    }
    public IdentityGuardState State
    {
        get; set;
    }
    public Guid OwnerBatchId
    {
        get; set;
    }
    public long OwnerLineNumber
    {
        get; set;
    }
    public DateTimeOffset? LeaseUntil
    {
        get; set;
    }
    public long FencingVersion
    {
        get; set;
    }
    public PersistedTelemetryDestination Destination
    {
        get; set;
    }
    public PersistedIdentityStorageLocation StorageLocation
    {
        get; set;
    }
    public DateTimeOffset GuardExpiresAt
    {
        get; set;
    }
    public DateTimeOffset TelemetryExpiresAt
    {
        get; set;
    }
    public Guid? PromotionOwner
    {
        get; set;
    }
    public DateTimeOffset? PromotionLeaseUntil
    {
        get; set;
    }
}
public sealed class EventIdentityEvidence
{
    public string CompanyId { get; set; } = "";
    public string VehicleId { get; set; } = "";
    public DateTimeOffset EventTimestamp
    {
        get; set;
    }
    public byte[] CanonicalFingerprint { get; set; } = [];
    public Guid FirstBatchId
    {
        get; set;
    }
    public DateTimeOffset FirstSeenAt
    {
        get; set;
    }
    public DateTimeOffset EvidenceExpiresAt
    {
        get; set;
    }
}
public sealed class OperatorAccessProfile
{
    public string ActorId { get; set; } = "";
    public OperatorActorKind Kind
    {
        get; set;
    }
    public string[] CredentialIds { get; set; } = [];
    public bool Enabled
    {
        get; set;
    }
    public string[] Permissions { get; set; } = [];
    public string[] CompanyIds { get; set; } = [];
    public string? GrafanaOrganizationId
    {
        get; set;
    }
    public long RowVersion
    {
        get; set;
    }
}
public enum PromotionState
{
    Discovered, Copying, Copied, SourceDeleted, Completed, Retrying
}
public sealed class PendingTelemetryPromotion
{
    public Guid PromotionId
    {
        get; set;
    }
    public string CompanyId { get; set; } = "";
    public Guid SourceBatchId
    {
        get; set;
    }
    public DateTimeOffset RangeStart
    {
        get; set;
    }
    public DateTimeOffset RangeEnd
    {
        get; set;
    }
    public PromotionState State
    {
        get; set;
    }
    public int Attempts
    {
        get; set;
    }
    public long ExpectedPointCount
    {
        get; set;
    }
    public long? ConfirmedPointCount
    {
        get; set;
    }
    public DateTimeOffset CreatedAt
    {
        get; set;
    }
    public DateTimeOffset UpdatedAt
    {
        get; set;
    }
    public DateTimeOffset? CompletedAt
    {
        get; set;
    }
    public string? LastErrorCode
    {
        get; set;
    }
    public long RowVersion
    {
        get; set;
    }
}
public sealed class AuditEvent
{
    public Guid EventId
    {
        get; set;
    }
    public string ActorId { get; set; } = "";
    public OperatorActorKind ActorKind
    {
        get; set;
    }
    public string? GrafanaOrganizationId
    {
        get; set;
    }
    public AuditAction Action
    {
        get; set;
    }
    public AuditResourceCategory ResourceCategory
    {
        get; set;
    }
    public string? OpaqueResourceId
    {
        get; set;
    }
    public string[] CompanyScope { get; set; } = [];
    public DateTimeOffset OccurredAt
    {
        get; set;
    }
    public AuditOutcome Outcome
    {
        get; set;
    }
    public string TraceId { get; set; } = "";
    public DateTimeOffset CommittedAt
    {
        get; set;
    }
}

// Legacy storage vocabulary stays inside Infrastructure until the T106 forward migration.
// These values are not application routing choices and must continue decoding existing rows.
public enum PersistedTelemetryDestination
{
    Queryable, Pending
}
public enum PersistedIdentityStorageLocation
{
    PendingCommitted, Promoting, QueryableCommitted
}
