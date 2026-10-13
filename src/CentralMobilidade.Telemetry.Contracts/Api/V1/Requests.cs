using System;
using System.ComponentModel.DataAnnotations;

namespace CentralMobilidade.Telemetry.Contracts.Api.V1;

// These records describe route/query/header metadata; the intake payload remains a gzip stream.
// Endpoints bind the documented HTTP parameter names explicitly; these are not JSON request bodies.
public sealed record IngestBatchHeaders(
    [property: Required, RegularExpression("v1")] string ContractVersion,
    [property: Required] string ContentType,
    [property: Required] string ContentEncoding,
    string? TraceParent = null);

public sealed record GetBatchStatusRequest(Guid BatchId);

public sealed record ListBatchRejectionsRequest(
    Guid BatchId,
    string? Cursor = null,
    [property: Range(1, 200)] int Limit = 50);

public sealed record GetQuarantinedContentRequest(Guid BatchId, Guid QuarantineId);

public sealed record DriverPresentationRequest(
    [property: Required, StringLength(128)] string DriverId,
    [property: Required, StringLength(128)] string EmpresaId);

public sealed record LatestPositionsRequest(
    [property: Required, StringLength(128)] string EmpresaId,
    [property: StringLength(512)] string? Cursor = null,
    [property: Range(1, 500)] int Limit = 500,
    [property: StringLength(128)] string? VehicleId = null);

public sealed record VehicleTripRequest(
    [property: Required, StringLength(128)] string EmpresaId,
    [property: Required, StringLength(128)] string VehicleId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    [property: Range(1, 10000)] int Limit = 10000);

public sealed record IngestionHealthRequest(
    [property: Required, StringLength(128)] string EmpresaId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    [property: Range(60, int.MaxValue)] int GranularitySeconds = 60,
    [property: Range(1, 10000)] int Limit = 10000);
