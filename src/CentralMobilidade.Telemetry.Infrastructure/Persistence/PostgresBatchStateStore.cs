using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Data.Common;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.Processing;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace CentralMobilidade.Telemetry.Infrastructure.Persistence;

public sealed class PostgresBatchStateStore(IDbContextFactory<TelemetryControlDbContext> contextFactory, Microsoft.Extensions.Configuration.IConfiguration? configuration = null, IClock? clock = null) : IBatchStateStore
{
    public async ValueTask<BatchSnapshot> CreateAsync(TelemetryBatchEnvelope metadata,
        DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Validate();
        if (metadata.ReceivedAt.Offset != TimeSpan.Zero || expiresAt.Offset != TimeSpan.Zero)
            throw new ValidationException("Batch timestamps must be UTC.");

        var receivedAt = NormalizeForPostgres(metadata.ReceivedAt);
        var normalizedExpiresAt = NormalizeForPostgres(expiresAt);
        if (normalizedExpiresAt != receivedAt.AddDays(30))
            throw new ValidationException("Batch retention must be exactly 30 days.");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ingestion_batches
                ("BatchId", "TraceId", "ProducerId", "ContractVersion", "State", "ReceivedAt",
                 "ExpiresAt", "CompressedBytes", "Accepted", "Rejected", "Replayed", "RetryCount",
                 "CompanyScope", "CompanyScopeComplete", "RowVersion")
            VALUES
                ({metadata.BatchId}, {metadata.TraceId}, {metadata.ProducerId}, {metadata.ContractVersion ?? "v1"},
                 {BatchLifecycle.Receiving.ToString()}, {receivedAt}, {normalizedExpiresAt}, {metadata.CompressedBytes},
                 0, 0, 0, 0, {Array.Empty<string>()}, FALSE, 1)
            ON CONFLICT ("BatchId") DO NOTHING
            """, cancellationToken).ConfigureAwait(false);

        var row = await db.Batches.AsNoTracking().SingleAsync(e => e.BatchId == metadata.BatchId,
            cancellationToken).ConfigureAwait(false);
        EnsureSameCreation(row, metadata, receivedAt, normalizedExpiresAt);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ToSnapshotAsync(db, row, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<BatchSnapshot?> GetAsync(BatchId batchId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batchId);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Batches.AsNoTracking().SingleOrDefaultAsync(e => e.BatchId == batchId.Value,
            cancellationToken).ConfigureAwait(false);
        return row is null ? null : await ToSnapshotAsync(db, row, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> TryTransitionAsync(BatchId batchId, long expectedVersion,
        BatchTransition transition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batchId);
        ArgumentNullException.ThrowIfNull(transition);
        if (expectedVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));

        var occurredAt = NormalizeForPostgres(transition.OccurredAt);
        ValidateTransition(transition, occurredAt);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var sources = SourcesFor(transition.TargetState);
        if (sources.Length == 0)
            return false;

        var updated = await db.Batches
            .Where(e => e.BatchId == batchId.Value && e.RowVersion == expectedVersion &&
                sources.Contains(e.State) && e.ReceivedAt <= occurredAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.State, transition.TargetState)
                .SetProperty(e => e.DurableAt, e => transition.TargetState == BatchLifecycle.Accepted ? occurredAt : e.DurableAt)
                .SetProperty(e => e.ProcessingStartedAt, e => transition.TargetState == BatchLifecycle.Processing && e.ProcessingStartedAt == null ? occurredAt : e.ProcessingStartedAt)
                .SetProperty(e => e.CompletedAt, e => IsTerminal(transition.TargetState) ? occurredAt : e.CompletedAt)
                .SetProperty(e => e.Accepted, e => transition.Counters == null ? e.Accepted : transition.Counters.Accepted)
                .SetProperty(e => e.Rejected, e => transition.Counters == null ? e.Rejected : transition.Counters.Rejected)
                .SetProperty(e => e.Replayed, e => transition.Counters == null ? e.Replayed : transition.Counters.Replayed)
                .SetProperty(e => e.TotalLines, e => transition.Counters == null ? e.TotalLines : transition.Counters.Total)
                .SetProperty(e => e.CompanyScopeComplete, e =>
                    (transition.TargetState == BatchLifecycle.Completed || transition.TargetState == BatchLifecycle.CompletedWithRejections)
                    ? db.LineResults.Any(r => r.BatchId == e.BatchId) && !db.LineResults.Any(r => r.BatchId == e.BatchId && r.CompanyId == null) : e.CompanyScopeComplete)
                .SetProperty(e => e.TerminalErrorCode, e => transition.ErrorCode ?? e.TerminalErrorCode)
                .SetProperty(e => e.RetryCount, e => transition.TargetState == BatchLifecycle.Retrying ? e.RetryCount + 1 : e.RetryCount)
                .SetProperty(e => e.RowVersion, e => e.RowVersion + 1), cancellationToken)
            .ConfigureAwait(false);

        if (updated == 1)
            return true;

        var existing = await db.Batches.AsNoTracking().SingleOrDefaultAsync(e => e.BatchId == batchId.Value,
            cancellationToken).ConfigureAwait(false);
        return existing is not null && IsExactReplay(existing, expectedVersion, transition, occurredAt);
    }

    public async ValueTask RecordLineResultsAsync(BatchId batchId, IReadOnlyCollection<LineProcessingOutcome> results,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batchId);
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count > 5000)
            throw new ArgumentException("Result page is too large.", nameof(results));
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var batch = await db.Batches.FromSqlInterpolated($"SELECT * FROM ingestion_batches WHERE \"BatchId\"={batchId.Value} FOR UPDATE").SingleAsync(cancellationToken);
            foreach (var result in results)
            {
                if (result.LineNumber <= 0 || !Enum.IsDefined(result.Outcome) || result.OccurredAt.Offset != TimeSpan.Zero ||
                    (result.Outcome == LineOutcome.Rejected ? !TelemetryErrorCodes.IsRejectionCode(result.ErrorCode!) || result.QuarantineId is null :
                        result.ErrorCode is not null || result.QuarantineId is not null || result.ProtectedContent is not null || result.CompanyId is null || result.VehicleId is null))
                    throw new ArgumentException("Invalid line result.", nameof(results));
                _ = SafeLineDigest.FromStoredValue(result.SafeDigest);
                if (result.Outcome == LineOutcome.Rejected)
                {
                    var content = result.ProtectedContent ?? throw new ArgumentException("Protected quarantine metadata is required.");
                    if (content.QuarantineId != result.QuarantineId || content.BatchId != batchId || content.LineNumber != result.LineNumber ||
                        content.ErrorCode != result.ErrorCode || content.CreatedAt.Offset != TimeSpan.Zero || content.DeleteAfter.Offset != TimeSpan.Zero ||
                        content.DeleteAfter < content.CreatedAt || content.DeleteAfter - content.CreatedAt > TimeSpan.FromDays(30) ||
                        content.ContentSha256.Length != 64 || !content.ContentSha256.All(Uri.IsHexDigit))
                        throw new ArgumentException("Invalid protected metadata.");
                    var stored = await db.QuarantinedRejections.SingleOrDefaultAsync(q => q.QuarantineId == content.QuarantineId, cancellationToken);
                    if (stored is null)
                        db.QuarantinedRejections.Add(new QuarantinedRejection
                        {
                            QuarantineId = content.QuarantineId,
                            BatchId = batchId.Value,
                            LineNumber = content.LineNumber,
                            ErrorCode = content.ErrorCode,
                            ContentDigest = content.ContentSha256,
                            EncryptedContentLocation = "quarantine:" + content.QuarantineId.ToString("N"),
                            CreatedAt = NormalizeForPostgres(content.CreatedAt),
                            DeleteAfter = NormalizeForPostgres(content.DeleteAfter)
                        });
                    else if (stored.BatchId != batchId.Value || stored.LineNumber != content.LineNumber || stored.ErrorCode != content.ErrorCode ||
                        stored.ContentDigest != content.ContentSha256 || stored.CreatedAt != NormalizeForPostgres(content.CreatedAt) || stored.DeleteAfter != NormalizeForPostgres(content.DeleteAfter))
                        throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Permanent);
                }
                var existing = await db.LineResults.SingleOrDefaultAsync(r => r.BatchId == batchId.Value && r.LineNumber == result.LineNumber, cancellationToken);
                if (existing is not null)
                {
                    if (existing.Outcome != result.Outcome || existing.ErrorCode != result.ErrorCode || existing.SafeDigest != result.SafeDigest ||
                        existing.CompanyId != result.CompanyId?.Value || existing.VehicleId != result.VehicleId?.Value || existing.QuarantineId != result.QuarantineId)
                        throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Permanent);
                    continue;
                }
                if (batch.State != BatchLifecycle.Processing)
                    throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Ambiguous);
                db.LineResults.Add(new LineProcessingResult
                {
                    BatchId = batchId.Value,
                    LineNumber = result.LineNumber,
                    Outcome = result.Outcome,
                    ErrorCode = result.ErrorCode,
                    SafeDigest = result.SafeDigest,
                    CompanyId = result.CompanyId?.Value,
                    VehicleId = result.VehicleId?.Value,
                    QuarantineId = result.QuarantineId,
                    OccurredAt = NormalizeForPostgres(result.OccurredAt)
                });
            }
            await db.SaveChangesAsync(cancellationToken);
            var outcomes = db.LineResults.Where(r => r.BatchId == batchId.Value);
            batch.Accepted = await outcomes.LongCountAsync(r => r.Outcome == LineOutcome.Accepted, cancellationToken);
            batch.Rejected = await outcomes.LongCountAsync(r => r.Outcome == LineOutcome.Rejected, cancellationToken);
            batch.Replayed = await outcomes.LongCountAsync(r => r.Outcome == LineOutcome.IdenticalReplay, cancellationToken);
            batch.CompanyScope = await outcomes.Where(r => r.CompanyId != null).Select(r => r.CompanyId!).Distinct().ToArrayAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception error) when (error is DbException or DbUpdateException)
        {
            throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Ambiguous);
        }
    }

    public async ValueTask<LineProcessingOutcome?> GetLineResultAsync(BatchId batchId, int lineNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batchId);
        if (lineNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineNumber));
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.LineResults.AsNoTracking().SingleOrDefaultAsync(r => r.BatchId == batchId.Value && r.LineNumber == lineNumber, cancellationToken);
            return row is null ? null : new((int)row.LineNumber, row.Outcome, row.ErrorCode,
                row.CompanyId is null ? null : new(row.CompanyId), row.VehicleId is null ? null : new(row.VehicleId),
                row.SafeDigest, row.OccurredAt, row.QuarantineId);
        }
        catch (DbException) { throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Ambiguous); }
    }

    public ValueTask<RejectionResultPage> GetRejectionsAsync(BatchId batchId, string? cursor, int limit,
        CancellationToken cancellationToken) =>
        new PostgresRejectionQuery(contextFactory, configuration, clock).GetRejectionsAsync(batchId, cursor, limit, cancellationToken);

    private static void EnsureSameCreation(IngestionBatch row, TelemetryBatchEnvelope metadata,
        DateTimeOffset receivedAt, DateTimeOffset expiresAt)
    {
        if (!StringComparer.Ordinal.Equals(row.TraceId, metadata.TraceId) ||
            !StringComparer.Ordinal.Equals(row.ProducerId, metadata.ProducerId) ||
            !StringComparer.Ordinal.Equals(row.ContractVersion, metadata.ContractVersion ?? "v1") ||
            row.ReceivedAt != receivedAt || row.ExpiresAt != expiresAt ||
            row.CompressedBytes != metadata.CompressedBytes)
            throw new InvalidOperationException("Batch identifier is already associated with different metadata.");
    }

    private static void ValidateTransition(BatchTransition transition, DateTimeOffset occurredAt)
    {
        if (transition.OccurredAt.Offset != TimeSpan.Zero)
            throw new ValidationException("Transition timestamps must be UTC.");
        if (transition.Counters is { } counters &&
            (counters.Accepted < 0 || counters.Rejected < 0 || counters.Replayed < 0 || counters.Total < 0 ||
             counters.Total is { } total && counters.Accepted + counters.Rejected + counters.Replayed > total))
            throw new ValidationException("Batch counters are invalid.");
        if (transition.ErrorCode is { Length: > 64 } || string.IsNullOrWhiteSpace(transition.ErrorCode) && transition.ErrorCode is not null)
            throw new ValidationException("Transition error code is invalid.");
        _ = occurredAt;
    }

    private static DateTimeOffset NormalizeForPostgres(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);

    private static BatchLifecycle[] SourcesFor(BatchLifecycle target) => target switch
    {
        BatchLifecycle.Publishing => [BatchLifecycle.Receiving],
        BatchLifecycle.Accepted => [BatchLifecycle.Publishing],
        BatchLifecycle.Rejected => [BatchLifecycle.Receiving],
        BatchLifecycle.PublishFailed => [BatchLifecycle.Publishing],
        BatchLifecycle.Processing => [BatchLifecycle.Accepted, BatchLifecycle.Retrying],
        BatchLifecycle.Retrying => [BatchLifecycle.Processing],
        BatchLifecycle.Completed or BatchLifecycle.CompletedWithRejections => [BatchLifecycle.Processing],
        BatchLifecycle.Failed => [BatchLifecycle.Processing, BatchLifecycle.Retrying],
        BatchLifecycle.Expired => [BatchLifecycle.Accepted],
        _ => []
    };

    private static bool IsTerminal(BatchLifecycle state) => state is BatchLifecycle.Completed or
        BatchLifecycle.CompletedWithRejections or BatchLifecycle.Rejected or BatchLifecycle.PublishFailed or
        BatchLifecycle.Failed or BatchLifecycle.Expired;

    private static bool IsExactReplay(IngestionBatch row, long expectedVersion, BatchTransition transition,
        DateTimeOffset occurredAt)
    {
        if (row.RowVersion != expectedVersion + 1 || row.State != transition.TargetState)
            return false;
        var timestampMatches = transition.TargetState switch
        {
            BatchLifecycle.Accepted => row.DurableAt == occurredAt,
            BatchLifecycle.Processing => row.ProcessingStartedAt == occurredAt,
            _ when IsTerminal(transition.TargetState) => row.CompletedAt == occurredAt,
            _ => true
        };
        var countersMatch = transition.Counters is null ||
            row.Accepted == transition.Counters.Accepted && row.Rejected == transition.Counters.Rejected &&
            row.Replayed == transition.Counters.Replayed && row.TotalLines == transition.Counters.Total;
        var errorMatches = transition.ErrorCode is null || StringComparer.Ordinal.Equals(row.TerminalErrorCode, transition.ErrorCode);
        return timestampMatches && countersMatch && errorMatches;
    }

    private static async ValueTask<BatchSnapshot> ToSnapshotAsync(TelemetryControlDbContext db,
        IngestionBatch row, CancellationToken cancellationToken)
    {
        var errors = await db.LineResults.AsNoTracking()
            .Where(e => e.BatchId == row.BatchId && e.ErrorCode != null)
            .GroupBy(e => e.ErrorCode!)
            .Select(group => new { Code = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(e => e.Code, e => e.Count, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        return new BatchSnapshot(new BatchId(row.BatchId), row.ProducerId, row.State, row.ReceivedAt,
            row.ExpiresAt, row.ProcessingStartedAt, row.CompletedAt,
            new BatchCounters(row.Accepted, row.Rejected, row.Replayed, row.TotalLines), errors,
            row.CompanyScope.Select(value => new CompanyId(value)).ToArray(), row.CompanyScopeComplete, row.RowVersion);
    }
}
