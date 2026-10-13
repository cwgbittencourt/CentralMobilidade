using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CentralMobilidade.Telemetry.Infrastructure.Auditing;

public sealed class PostgresAuditWriter(IDbContextFactory<TelemetryControlDbContext> factory) : IAuditWriter
{
    public async ValueTask<AuditReceipt> AppendAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.EventId == Guid.Empty || record.Actor is null ||
            string.IsNullOrWhiteSpace(record.Actor.ActorId) || record.Actor.ActorId.Length > 256 ||
            string.IsNullOrWhiteSpace(record.TraceId) || record.TraceId.Length > 128 ||
            record.OpaqueResourceId?.Length > 256 || record.Actor.GrafanaOrganizationId?.Length > 256 ||
            record.OccurredAt.Offset != TimeSpan.Zero || !Enum.IsDefined(record.Action) ||
            !Enum.IsDefined(record.ResourceCategory) || !Enum.IsDefined(record.Outcome) || !Enum.IsDefined(record.Actor.Kind))
            throw new ArgumentException("Invalid audit metadata.", nameof(record));
        ArgumentNullException.ThrowIfNull(record.CompanyScope);
        var scope = record.CompanyScope.Select(c => c.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // PostgreSQL has microsecond precision. Normalize before comparing idempotent requests.
        var occurredAt = new DateTimeOffset(record.OccurredAt.Ticks - record.OccurredAt.Ticks % 10, TimeSpan.Zero);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL synchronous_commit = on", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.audit_events ("EventId", "ActorId", "ActorKind", "GrafanaOrganizationId", "Action",
                "ResourceCategory", "OpaqueResourceId", "CompanyScope", "OccurredAt", "Outcome", "TraceId", "CommittedAt")
            VALUES ({record.EventId}, {record.Actor.ActorId}, {record.Actor.Kind.ToString()}, {record.Actor.GrafanaOrganizationId},
                {record.Action.ToString()}, {record.ResourceCategory.ToString()}, {record.OpaqueResourceId}, {scope},
                {occurredAt}, {record.Outcome.ToString()}, {record.TraceId}, clock_timestamp())
            ON CONFLICT ("EventId") DO NOTHING
            """, cancellationToken);
        var stored = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.EventId == record.EventId, cancellationToken);
        if (stored.ActorId != record.Actor.ActorId || stored.ActorKind != record.Actor.Kind ||
            stored.GrafanaOrganizationId != record.Actor.GrafanaOrganizationId || stored.Action != record.Action ||
            stored.ResourceCategory != record.ResourceCategory || stored.OpaqueResourceId != record.OpaqueResourceId ||
            !stored.CompanyScope.SequenceEqual(scope) || stored.OccurredAt != occurredAt ||
            stored.Outcome != record.Outcome || stored.TraceId != record.TraceId)
            throw new InvalidOperationException("Audit event ID already belongs to another record.");
        await transaction.CommitAsync(cancellationToken);
        return new AuditReceipt(stored.EventId, stored.CommittedAt);
    }
}
