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
        cancellationToken.ThrowIfCancellationRequested();
        if (record.EventId == Guid.Empty || record.Actor is null ||
            string.IsNullOrWhiteSpace(record.Actor.ActorId) || record.Actor.ActorId.Length > 256 ||
            string.IsNullOrWhiteSpace(record.TraceId) || record.TraceId.Length > 128 ||
            record.OpaqueResourceId?.Length > 256 || record.Actor.GrafanaOrganizationId?.Length > 256 ||
            record.OccurredAt == default || record.OccurredAt.Offset != TimeSpan.Zero || !Enum.IsDefined(record.Action) ||
            !Enum.IsDefined(record.ResourceCategory) || !Enum.IsDefined(record.Outcome) || !Enum.IsDefined(record.Actor.Kind))
            throw new ArgumentException("Invalid audit metadata.", nameof(record));
        if (record.CompanyScope is null || record.CompanyScope.Any(company => company is null)
            || record.Actor.ActorId.Any(char.IsControl) || record.TraceId.Any(char.IsControl)
            || record.Actor.GrafanaOrganizationId?.Any(char.IsControl) == true
            || record.OpaqueResourceId is { } resource && (!Guid.TryParseExact(resource, "D", out var opaque) || opaque == Guid.Empty))
            throw new ArgumentException("Invalid audit metadata.", nameof(record));
        var scope = record.CompanyScope.Select(c => c.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var categoryMatches = record.Action switch
        {
            AuditAction.ReadBatch or AuditAction.ReadRejections => record.ResourceCategory == AuditResourceCategory.Batch,
            AuditAction.ReadQuarantineContent => record.ResourceCategory == AuditResourceCategory.Quarantine,
            AuditAction.ReadTelemetry => record.ResourceCategory == AuditResourceCategory.Telemetry,
            AuditAction.ResolveDriver => record.ResourceCategory == AuditResourceCategory.Driver,
            // Cross-company access can accompany any of the concrete read operations.
            AuditAction.ReadAcrossCompanies => true,
            _ => false
        };
        var actorMatches = record.Actor.Kind switch
        {
            OperatorActorKind.Human => record.Actor.GrafanaOrganizationId is null,
            OperatorActorKind.ViewerPresentationService => !string.IsNullOrWhiteSpace(record.Actor.GrafanaOrganizationId)
                && (record.Outcome == AuditOutcome.Denied || record.Action is AuditAction.ReadTelemetry or AuditAction.ResolveDriver),
            OperatorActorKind.Unidentified => record.Actor.ActorId == "unauthenticated" && record.Actor.GrafanaOrganizationId is null
                && record.Outcome == AuditOutcome.Denied && scope.Length == 0,
            _ => false
        };
        if (!categoryMatches || !actorMatches || record.Outcome == AuditOutcome.Allowed &&
            (record.Action == AuditAction.ResolveDriver && scope.Length != 1
                || record.Action is AuditAction.ReadTelemetry or AuditAction.ReadAcrossCompanies && scope.Length == 0))
            throw new ArgumentException("Invalid audit metadata.", nameof(record));
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
