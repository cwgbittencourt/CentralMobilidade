using System;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CentralMobilidade.Telemetry.Infrastructure.Idempotency;

/// <summary>ADR-001: unique identity, transactionally pinned evidence and row-locked fenced leases.</summary>
public sealed class SelectedEventIdentityGuard(IDbContextFactory<TelemetryControlDbContext> factory,
    IClock clock, IOptions<TelemetryOptions>? options = null) : IEventIdentityGuard
{
    private readonly TimeSpan telemetryRetention = options?.Value.LocalTelemetryRetention ?? TimeSpan.FromDays(60);

    public ValueTask<CommittedIdentitySnapshot?> GetCommittedAsync(EventIdentity id, CancellationToken token) => Execute(() => GetCommittedCore(id, token));
    public ValueTask<IdentityReservationResult> AcquireOrResumeAsync(IdentityReservationRequest request, CancellationToken token) => Execute(() => AcquireCore(request, token));
    public ValueTask<IdentityLease?> RenewAsync(IdentityLease lease, DateTimeOffset now, DateTimeOffset until, CancellationToken token) => Execute(() => RenewCore(lease, now, until, token));
    public ValueTask<bool> TryMarkWritingAsync(IdentityLease lease, DateTimeOffset now, CancellationToken token) => Execute(() => MarkWritingCore(lease, now, token));
    public ValueTask<bool> TryCommitAsync(IdentityLease lease, DateTimeOffset now, CancellationToken token) => Execute(() => CommitCore(lease, now, token));
    private static async ValueTask<T> Execute<T>(Func<ValueTask<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception error) when (error is DbException or DbUpdateException)
        {
            throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Ambiguous);
        }
    }

    private async ValueTask<CommittedIdentitySnapshot?> GetCommittedCore(EventIdentity identity, CancellationToken token)
    {
        ValidateIdentity(identity);
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await Query(db, identity).AsNoTracking().SingleOrDefaultAsync(token);
        if (row?.State != IdentityGuardState.Committed)
            return null;
        var evidence = await Evidence(db, identity).AsNoTracking().SingleOrDefaultAsync(token);
        return new(identity, row.GuardExpiresAt,
            evidence?.EvidenceExpiresAt > clock.UtcNow ? evidence.EvidenceExpiresAt : null);
    }

    private async ValueTask<IdentityReservationResult> AcquireCore(IdentityReservationRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.Identity);
        ValidateOwner(request.Owner);
        Utc(request.Now);
        Utc(request.LeaseUntil);
        Utc(request.GuardExpiresAt);
        Utc(request.EvidenceExpiresAt);
        if (request.LeaseUntil <= request.Now || request.EvidenceExpiresAt <= request.Now || request.EvidenceExpiresAt - request.Now > TimeSpan.FromDays(30))
            throw new ArgumentException("Invalid identity reservation limits.", nameof(request));
        byte[] fingerprint;
        try
        {
            fingerprint = Convert.FromHexString(request.Fingerprint);
        }
        catch (Exception error) when (error is ArgumentException or FormatException) { throw new ArgumentException("Invalid canonical fingerprint.", nameof(request)); }
        if (fingerprint.Length != 32)
            throw new ArgumentException("Invalid canonical fingerprint.", nameof(request));
        var telemetryExpires = request.Identity.Timestamp.Value.Add(telemetryRetention);
        if (request.GuardExpiresAt < telemetryExpires || request.GuardExpiresAt <= request.Now)
            throw new ArgumentException("Guard retention must cover telemetry lifetime.", nameof(request));
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var id = request.Identity;
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO event_identity_guards
            ("CompanyId","VehicleId","EventTimestamp","State","OwnerBatchId","OwnerLineNumber","LeaseUntil","FencingVersion",
             "GuardExpiresAt","TelemetryExpiresAt")
            VALUES ({id.CompanyId.Value},{id.VehicleId.Value},{id.Timestamp.Value},'Reserved',{request.Owner.BatchId.Value},
             {request.Owner.LineNumber},{Normalize(request.LeaseUntil)},1,{Normalize(request.GuardExpiresAt)},{Normalize(telemetryExpires)})
            ON CONFLICT ("CompanyId","VehicleId","EventTimestamp") DO NOTHING
            """, token);
        var row = await Locked(db, id).SingleAsync(token);
        var evidence = await Evidence(db, id).SingleOrDefaultAsync(token);
        if (inserted == 1)
        {
            if (request.EvidenceExpiresAt - request.Now != TimeSpan.FromDays(30))
                throw new ArgumentException("New evidence requires an exact 30-day lifetime.", nameof(request));
            if (evidence is not null && evidence.EvidenceExpiresAt > request.Now)
            {
                await transaction.RollbackAsync(token);
                return new IdentityReservationResult.Existing(ExistingIdentityOutcome.AlreadyExistsUnclassified);
            }
            if (evidence is not null)
            {
                db.IdentityEvidence.Remove(evidence);
                await db.SaveChangesAsync(token);
            }
            db.IdentityEvidence.Add(new EventIdentityEvidence
            {
                CompanyId = id.CompanyId.Value,
                VehicleId = id.VehicleId.Value,
                EventTimestamp = id.Timestamp.Value,
                FirstBatchId = request.Owner.BatchId.Value,
                FirstSeenAt = Normalize(request.Now),
                EvidenceExpiresAt = Normalize(request.EvidenceExpiresAt),
                CanonicalFingerprint = fingerprint
            });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new IdentityReservationResult.Granted(Lease(row, id), false);
        }
        IdentityReservationResult result;
        if (evidence is null || evidence.EvidenceExpiresAt <= request.Now)
            result = new IdentityReservationResult.Existing(ExistingIdentityOutcome.AlreadyExistsUnclassified);
        else if (!CryptographicOperations.FixedTimeEquals(evidence.CanonicalFingerprint, fingerprint))
            result = new IdentityReservationResult.Existing(ExistingIdentityOutcome.Conflict);
        else if (row.State == IdentityGuardState.Committed)
            result = new IdentityReservationResult.Existing(ExistingIdentityOutcome.IdenticalReplay);
        else
        {
            var sameOwner = row.OwnerBatchId == request.Owner.BatchId.Value && row.OwnerLineNumber == request.Owner.LineNumber;
            // PostgreSQL clock is an additional authority: client time cannot force an early takeover.
            var databaseNow = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
            if (!sameOwner && (row.LeaseUntil > request.Now || row.LeaseUntil > databaseNow))
                result = new IdentityReservationResult.Contended(row.LeaseUntil!.Value);
            else
            {
                row.FencingVersion = checked(row.FencingVersion + 1); // Fence even a same-owner expired renewal.
                if (sameOwner && row.LeaseUntil > request.Now && row.LeaseUntil > databaseNow)
                    row.FencingVersion--;
                row.OwnerBatchId = request.Owner.BatchId.Value;
                row.OwnerLineNumber = request.Owner.LineNumber;
                if (!sameOwner)
                    row.State = IdentityGuardState.Reserved;
                row.LeaseUntil = Normalize(sameOwner && row.LeaseUntil > request.LeaseUntil ? row.LeaseUntil.Value : request.LeaseUntil);
                await db.SaveChangesAsync(token);
                result = new IdentityReservationResult.Granted(Lease(row, id), sameOwner);
            }
        }
        await transaction.CommitAsync(token);
        return result;
    }

    private async ValueTask<IdentityLease?> RenewCore(IdentityLease lease, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken token)
    {
        Utc(now);
        Utc(leaseUntil);
        ValidateLease(lease);
        if (leaseUntil <= now)
            throw new ArgumentException("Renewal must end after now.", nameof(leaseUntil));
        await using var db = await factory.CreateDbContextAsync(token);
        var changed = await Active(db, lease, now).Where(row => row.LeaseUntil <= leaseUntil)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.LeaseUntil, Normalize(leaseUntil)), token);
        return changed == 1 ? lease with
        {
            LeaseUntil = Normalize(leaseUntil)
        } : null;
    }

    private async ValueTask<bool> MarkWritingCore(IdentityLease lease, DateTimeOffset now, CancellationToken token)
    {
        Utc(now);
        ValidateLease(lease);
        await using var db = await factory.CreateDbContextAsync(token);
        return await Active(db, lease, now).Where(row => row.State == IdentityGuardState.Reserved)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.State, IdentityGuardState.Writing), token) == 1;
    }

    private async ValueTask<bool> CommitCore(IdentityLease lease, DateTimeOffset now, CancellationToken token)
    {
        Utc(now);
        ValidateLease(lease);
        await using var db = await factory.CreateDbContextAsync(token);
        return await Active(db, lease, now).Where(row => row.State == IdentityGuardState.Writing)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.State, IdentityGuardState.Committed)
                .SetProperty(row => row.LeaseUntil, (DateTimeOffset?)null), token) == 1;
    }

    private static IQueryable<EventIdentityGuard> Query(TelemetryControlDbContext db, EventIdentity id) => db.IdentityGuards
        .Where(row => row.CompanyId == id.CompanyId.Value && row.VehicleId == id.VehicleId.Value && row.EventTimestamp == id.Timestamp.Value);
    private static IQueryable<EventIdentityEvidence> Evidence(TelemetryControlDbContext db, EventIdentity id) => db.IdentityEvidence
        .Where(row => row.CompanyId == id.CompanyId.Value && row.VehicleId == id.VehicleId.Value && row.EventTimestamp == id.Timestamp.Value);
    private static IQueryable<EventIdentityGuard> Locked(TelemetryControlDbContext db, EventIdentity id) => db.IdentityGuards.FromSqlInterpolated($"""
        SELECT * FROM event_identity_guards WHERE "CompanyId"={id.CompanyId.Value} AND "VehicleId"={id.VehicleId.Value} AND "EventTimestamp"={id.Timestamp.Value} FOR UPDATE
        """);
    private static IQueryable<EventIdentityGuard> Active(TelemetryControlDbContext db, IdentityLease lease, DateTimeOffset now) => Query(db, lease.Identity)
        .Where(row => row.OwnerBatchId == lease.Owner.BatchId.Value && row.OwnerLineNumber == lease.Owner.LineNumber && row.FencingVersion == lease.FencingVersion
            && row.State != IdentityGuardState.Committed && row.LeaseUntil > now && row.LeaseUntil > DateTimeOffset.UtcNow);
    private static IdentityLease Lease(EventIdentityGuard row, EventIdentity id) => new(id, new(new(row.OwnerBatchId), checked((int)row.OwnerLineNumber)), row.FencingVersion, row.LeaseUntil!.Value, row.State);
    private static DateTimeOffset Normalize(DateTimeOffset time) => new(time.Ticks - time.Ticks % 10, TimeSpan.Zero);
    private static void Utc(DateTimeOffset time)
    {
        if (time.Offset != TimeSpan.Zero)
            throw new ArgumentException("Identity guard timestamps must be UTC.");
    }
    private static void ValidateIdentity(EventIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(identity.CompanyId);
        ArgumentNullException.ThrowIfNull(identity.VehicleId);
        ArgumentNullException.ThrowIfNull(identity.Timestamp);
    }
    private static void ValidateOwner(LineOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(owner.BatchId);
        if (owner.LineNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(owner));
    }
    private static void ValidateLease(IdentityLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateIdentity(lease.Identity);
        ValidateOwner(lease.Owner);
        if (lease.FencingVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(lease));
    }
}
