using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace CentralMobilidade.Telemetry.Infrastructure.Persistence;

public sealed class PostgresOperationalRetentionCleanup(IDbContextFactory<TelemetryControlDbContext> factory,
    IQuarantineStore quarantine, IOptions<OperationalRetentionOptions> configured) : IOperationalRetentionCleanup
{
    // Database-local, transaction-owned lease: disconnect/rollback/crash releases ownership.
    private const long LeaseKey = 0x434D5F524554454E;
    public async ValueTask<OperationalRetentionResult> SweepAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (cutoff.Offset != TimeSpan.Zero)
            throw new ArgumentException("Retention cutoff must be UTC.", nameof(cutoff));
        cancellationToken.ThrowIfCancellationRequested();
        var options = configured.Value;
        if (!options.IsValid())
            throw new InvalidOperationException("Operational retention configuration is invalid.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(options.PassTimeout);
        var token = stop.Token;
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LeaseKey}) AS \"Value\"").SingleAsync(token);
            if (!acquired)
                return new(false, 0, 0, 0, 0, 0);
            // PostgreSQL also releases a stalled transaction if file IO ignores cancellation.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL idle_in_transaction_session_timeout = '120s'", token);
            int objects = 0, batches = 0, evidence = 0, guards = 0, failed = 0;
            var after = Guid.Empty;
            for (var page = 0; page < options.MaxPages; page++)
            {
                var rows = await db.QuarantinedRejections.FromSqlInterpolated($"""
                    SELECT * FROM quarantined_rejections
                    WHERE "DeleteAfter" <= {cutoff} AND "QuarantineId" > {after}
                    ORDER BY "QuarantineId" LIMIT {options.PageSize}
                    """).AsNoTracking().ToArrayAsync(token);
                if (rows.Length == 0)
                    break;
                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    after = row.QuarantineId;
                    bool removed;
                    try
                    {
                        removed = await quarantine.DeleteExpiredAsync(row.QuarantineId, cutoff, token);
                        // A previous pass may have deleted bytes before its database commit failed.
                        if (!removed)
                            removed = await quarantine.GetMetadataAsync(row.QuarantineId, token) is null;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is ProviderOperationException or IOException or UnauthorizedAccessException)
                    {
                        failed++;
                        continue;
                    }
                    if (!removed)
                    {
                        failed++;
                        continue;
                    }
                    objects += await db.QuarantinedRejections.Where(x => x.QuarantineId == row.QuarantineId && x.DeleteAfter <= cutoff).ExecuteDeleteAsync(token);
                }
                if (rows.Length < options.PageSize)
                    break;
            }
            after = Guid.Empty;
            for (var page = 0; page < options.MaxPages; page++)
            {
                // Lock the parent before checking remaining quarantine. This fences concurrent result recording.
                var rows = await db.Batches.FromSqlInterpolated($"""
                    SELECT * FROM ingestion_batches
                    WHERE "ExpiresAt" <= {cutoff} AND "BatchId" > {after}
                    ORDER BY "BatchId" LIMIT {options.PageSize} FOR UPDATE SKIP LOCKED
                    """).AsNoTracking().ToArrayAsync(token);
                if (rows.Length == 0)
                    break;
                foreach (var row in rows)
                {
                    after = row.BatchId;
                    // Failed/unexpired objects retain the parent and its recovery metadata.
                    batches += await db.Batches.Where(x => x.BatchId == row.BatchId && x.ExpiresAt <= cutoff
                        && !db.QuarantinedRejections.Any(q => q.BatchId == x.BatchId)).ExecuteDeleteAsync(token);
                }
                if (rows.Length < options.PageSize)
                    break;
            }
            for (var page = 0; page < options.MaxPages; page++)
            {
                var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM event_identity_evidence WHERE ("CompanyId", "VehicleId", "EventTimestamp") IN
                    (SELECT "CompanyId", "VehicleId", "EventTimestamp" FROM event_identity_evidence
                     WHERE "EvidenceExpiresAt" <= {cutoff}
                     ORDER BY "EvidenceExpiresAt", "CompanyId", "VehicleId", "EventTimestamp"
                     LIMIT {options.PageSize} FOR UPDATE SKIP LOCKED)
                    """, token);
                evidence += deleted;
                if (deleted < options.PageSize)
                    break;
            }
            for (var page = 0; page < options.MaxPages; page++)
            {
                var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM event_identity_guards WHERE ("CompanyId", "VehicleId", "EventTimestamp") IN
                    (SELECT "CompanyId", "VehicleId", "EventTimestamp" FROM event_identity_guards
                     WHERE "GuardExpiresAt" <= {cutoff} AND "TelemetryExpiresAt" <= {cutoff}
                       AND "GuardExpiresAt" <= clock_timestamp()
                       AND ("State" = 'Committed' OR "LeaseUntil" <= {cutoff} AND "LeaseUntil" <= clock_timestamp())
                     ORDER BY "GuardExpiresAt", "CompanyId", "VehicleId", "EventTimestamp"
                     LIMIT {options.PageSize} FOR UPDATE SKIP LOCKED)
                    """, token);
                guards += deleted;
                if (deleted < options.PageSize)
                    break;
            }
            await transaction.CommitAsync(token);
            return new(true, objects, batches, evidence, guards, failed);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
        catch (DbException) { throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Transient); }
    }
}
