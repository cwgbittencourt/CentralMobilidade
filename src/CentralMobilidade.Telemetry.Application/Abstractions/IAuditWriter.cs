using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Application.Abstractions;

public interface IAuditWriter
{
    // Append-only and idempotent by EventId; return only after durable commit.
    // Sensitive disclosure fails closed if this operation fails. Denials and missing/expired reads are audited too.
    ValueTask<AuditReceipt> AppendAsync(AuditRecord record, CancellationToken cancellationToken);
}
public enum AuditAction
{
    ReadBatch, ReadRejections, ReadQuarantineContent, ReadTelemetry, ResolveDriver, ReadAcrossCompanies
}
public enum AuditResourceCategory
{
    Batch, Quarantine, Telemetry, Driver
}
public enum AuditOutcome
{
    Allowed, Denied, Missing, Expired
}
public sealed record AuditRecord(Guid EventId, OperatorActor Actor, AuditAction Action, AuditResourceCategory ResourceCategory,
    string? OpaqueResourceId, IReadOnlyCollection<CompanyId> CompanyScope, DateTimeOffset OccurredAt, AuditOutcome Outcome, string TraceId);
public sealed record AuditReceipt(Guid EventId, DateTimeOffset CommittedAt);
