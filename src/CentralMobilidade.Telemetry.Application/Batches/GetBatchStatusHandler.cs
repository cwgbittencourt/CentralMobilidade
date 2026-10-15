using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
namespace CentralMobilidade.Telemetry.Application.Batches;

public sealed class GetBatchStatusHandler(BatchVisibility visibility)
{
    public async ValueTask<BatchStatus?> HandleAsync(BatchId batchId, BatchReadIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batchId);
        ArgumentNullException.ThrowIfNull(identity);
        var batch = await visibility.GetVisibleAsync(batchId, identity, cancellationToken);
        if (batch is null)
            return null;
        // Pre-confirmation states must never imply durable acceptance.
        BatchState? state = batch.State switch
        {
            BatchLifecycle.Accepted => BatchState.Accepted,
            BatchLifecycle.Processing => BatchState.Processing,
            BatchLifecycle.Retrying => BatchState.Retrying,
            BatchLifecycle.Completed => BatchState.Completed,
            BatchLifecycle.CompletedWithRejections => BatchState.CompletedWithRejections,
            BatchLifecycle.Failed => BatchState.Failed,
            _ => null
        };
        if (state is null)
            return null;
        return new(batch.BatchId.Value, state.Value, batch.ReceivedAt, batch.Counters.Accepted,
            batch.Counters.Rejected, batch.Counters.Replayed, batch.ProcessingStartedAt, batch.CompletedAt,
            batch.Counters.Total, batch.ErrorSummary.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new ErrorCount(x.Key, x.Value)).ToArray());
    }
}
