using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CentralMobilidade.Telemetry.Infrastructure.Presentation;

public sealed class PostgresIngestionHealthReader(IDbContextFactory<TelemetryControlDbContext>? factory) : IIngestionHealthReader
{
    public async ValueTask<IReadOnlyList<IngestionHealthPoint>> ReadAsync(CompanyId company, DateTimeOffset from, DateTimeOffset to, int granularitySeconds, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (factory is null || granularitySeconds < 60 || limit is < 1 or > 10000 || to <= from || to - from > TimeSpan.FromDays(7))
                throw new InvalidOperationException();
            // PostgreSQL stores microseconds. Round lower bounds up, preserving a sub-microsecond half-open upper bound.
            var start = from.AddTicks((10 - from.Ticks % 10) % 10);
            var end = to.AddTicks(-(to.Ticks % 10));
            var includeEnd = to.Ticks % 10 != 0;
            var interval = TimeSpan.FromSeconds(granularitySeconds);
            await using var db = await factory.CreateDbContextAsync(deadline.Token);
            var rows = await db.Database.SqlQuery<HealthRow>($"""
                SELECT date_bin({interval}, "OccurredAt", {start}) AS "Time",
                    count(*) FILTER (WHERE "Outcome" = 'Accepted') AS "Accepted",
                    count(*) FILTER (WHERE "Outcome" = 'Rejected') AS "Rejected"
                FROM line_processing_results
                WHERE "CompanyId" = {company.Value} AND "OccurredAt" >= {start}
                    AND ("OccurredAt" < {end} OR ({includeEnd} AND "OccurredAt" = {end}))
                    AND "Outcome" IN ('Accepted', 'Rejected')
                GROUP BY "Time" ORDER BY "Time" LIMIT {limit}
                """).ToListAsync(deadline.Token);
            return rows.Select(row => new IngestionHealthPoint(company.Value, row.Time, row.Accepted, row.Rejected)).ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { throw new ProviderOperationException(TelemetryErrorCodes.InternalError, ProviderFailureKind.Transient); }
    }

    public sealed class HealthRow
    {
        public DateTimeOffset Time
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
    }
}
