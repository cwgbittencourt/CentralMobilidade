using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Application.Abstractions;

public interface IBatchStateStore
{
    // Creation and line recording are replay-safe by BatchId and (BatchId, LineNumber), respectively.
    ValueTask<BatchSnapshot> CreateAsync(TelemetryBatchEnvelope metadata, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    ValueTask<BatchSnapshot?> GetAsync(BatchId batchId, CancellationToken cancellationToken);
    // Compare-and-swap and legal lifecycle transition checks are atomic; false means no mutation.
    ValueTask<bool> TryTransitionAsync(BatchId batchId, long expectedVersion, BatchTransition transition, CancellationToken cancellationToken);
    ValueTask RecordLineResultsAsync(BatchId batchId, IReadOnlyCollection<LineProcessingOutcome> results, CancellationToken cancellationToken);
    ValueTask<RejectionResultPage> GetRejectionsAsync(BatchId batchId, string? cursor, int limit, CancellationToken cancellationToken);
}

public enum BatchLifecycle
{
    Receiving, Publishing, Accepted, Processing, Retrying, Completed, CompletedWithRejections, Rejected, PublishFailed, Failed, Expired
}
public enum LineOutcome
{
    Accepted, IdenticalReplay, Rejected
}
public sealed record BatchCounters(long Accepted, long Rejected, long Replayed, long? Total);
public sealed record BatchSnapshot(BatchId BatchId, string ProducerId, BatchLifecycle State, DateTimeOffset ReceivedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? ProcessingStartedAt, DateTimeOffset? CompletedAt, BatchCounters Counters,
    IReadOnlyDictionary<string, long> ErrorSummary, IReadOnlyCollection<CompanyId> Companies, bool CompanyScopeComplete, long Version);
public sealed record BatchTransition(BatchLifecycle TargetState, DateTimeOffset OccurredAt, BatchCounters? Counters = null, string? ErrorCode = null);
public sealed record LineProcessingOutcome(int LineNumber, LineOutcome Outcome, string? ErrorCode, CompanyId? CompanyId,
    VehicleId? VehicleId, string SafeDigest, DateTimeOffset OccurredAt, Guid? QuarantineId = null);
public sealed record RejectionResultPage(IReadOnlyList<LineProcessingOutcome> Items, string? NextCursor);
