using System;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Application.Abstractions;

public interface IEventIdentityGuard
{
    // Read-only committed retention evidence for recovery; never grants a write.
    ValueTask<CommittedIdentitySnapshot?> GetCommittedAsync(EventIdentity identity, CancellationToken cancellationToken);
    // Atomic first-writer reservation/resumption; takeover requires lease expiry and a new fencing version.
    ValueTask<IdentityReservationResult> AcquireOrResumeAsync(IdentityReservationRequest request, CancellationToken cancellationToken);
    ValueTask<IdentityLease?> RenewAsync(IdentityLease lease, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken);
    ValueTask<bool> TryMarkWritingAsync(IdentityLease lease, DateTimeOffset now, CancellationToken cancellationToken);
    // Commit only after confirmed telemetry persistence. Compare identity, owner, fence, and lease atomically.
    ValueTask<bool> TryCommitAsync(IdentityLease lease, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record EventIdentity(CompanyId CompanyId, VehicleId VehicleId, EventTimestamp Timestamp);
public sealed record LineOwner(BatchId BatchId, int LineNumber);
public enum IdentityGuardState
{
    Reserved, Writing, Committed
}
public enum ExistingIdentityOutcome
{
    IdenticalReplay, Conflict, AlreadyExistsUnclassified
}
public sealed record IdentityReservationRequest(EventIdentity Identity, LineOwner Owner, string Fingerprint,
    DateTimeOffset Now, DateTimeOffset LeaseUntil,
    DateTimeOffset GuardExpiresAt, DateTimeOffset EvidenceExpiresAt);
public sealed record IdentityLease(EventIdentity Identity, LineOwner Owner, long FencingVersion, DateTimeOffset LeaseUntil,
    IdentityGuardState State);
public abstract record IdentityReservationResult
{
    public sealed record Granted(IdentityLease Lease, bool IsResumption) : IdentityReservationResult;
    public sealed record Existing(ExistingIdentityOutcome Outcome) : IdentityReservationResult;
    public sealed record Contended(DateTimeOffset RetryAfter) : IdentityReservationResult;
}
public sealed record CommittedIdentitySnapshot(EventIdentity Identity,
    DateTimeOffset GuardExpiresAt, DateTimeOffset? EvidenceExpiresAt);
