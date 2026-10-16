using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Application.Abstractions;

// Closed, company-bound query operations: callers cannot submit SQL or a company allowlist.
public interface ITelemetryPresentationReader
{
    ValueTask<IReadOnlyList<TelemetryPosition>> LatestAsync(CompanyId company, VehicleId? vehicle, string? afterVehicle, int limit, CancellationToken token);
    ValueTask<IReadOnlyList<TelemetryPosition>> TripAsync(CompanyId company, VehicleId vehicle, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken token);
}
public interface IIngestionHealthReader
{
    ValueTask<IReadOnlyList<IngestionHealthPoint>> ReadAsync(CompanyId company, DateTimeOffset from, DateTimeOffset to, int granularitySeconds, int limit, CancellationToken token);
}
