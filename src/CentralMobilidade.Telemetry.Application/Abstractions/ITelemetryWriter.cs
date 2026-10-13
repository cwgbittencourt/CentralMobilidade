using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Application.Abstractions;

public interface ITelemetryWriter
{
    // Caller closes the bounded batch on the first count/byte/time limit and owns its gzip stream.
    // Adapter uses millisecond precision and durable sync, and maps partial errors to original line owners.
    // Ambiguous/transient durability failures throw; a network send alone is not a committed result.
    ValueTask<TelemetryWriteResult> WriteAsync(TelemetryWriteRequest request, Stream gzipLineProtocol, CancellationToken cancellationToken);
}
public sealed record TelemetryWritePoint(BatchId BatchId, int LineNumber, EventIdentity Identity);
public sealed record TelemetryWriteRequest(TelemetryDestination Destination, string TraceId,
    IReadOnlyList<TelemetryWritePoint> Points, long UncompressedBytes);
public sealed record TelemetryWriteRejection(BatchId BatchId, int LineNumber, string ErrorCode);
public sealed record TelemetryWriteResult(int CommittedPoints, IReadOnlyList<TelemetryWriteRejection> Rejections);
