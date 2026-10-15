using System;
using System.Threading;
using System.Threading.Tasks;
namespace CentralMobilidade.Telemetry.Application.Abstractions;

public interface IOperationalRetentionCleanup
{
    // A database-owned lease serializes bounded sweeps across worker instances.
    ValueTask<OperationalRetentionResult> SweepAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
public sealed record OperationalRetentionResult(bool LeaseAcquired, int QuarantineObjects, int Batches,
    int Evidence, int Guards, int FailedObjects);
