using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Application.Abstractions;

public interface IDurableBatchQueue
{
    // Return only after durable provider confirmation. Failure can be ambiguous; never infer acceptance.
    // Caller owns the bounded gzip stream; the adapter does not dispose or JSON/Base64-wrap it.
    ValueTask<DurablePublishReceipt> PublishAsync(TelemetryBatchEnvelope metadata, Stream gzipBody, CancellationToken cancellationToken);
    IAsyncEnumerable<IQueueDelivery> ConsumeAsync(CancellationToken cancellationToken);
}

public sealed record DurablePublishReceipt(BatchId BatchId, DateTimeOffset ConfirmedAt);

public interface IQueueDelivery : IAsyncDisposable
{
    TelemetryBatchEnvelope Metadata
    {
        get;
    }
    Stream Body
    {
        get;
    }
    int DeliveryAttempt
    {
        get;
    }

    // Complete only after every write, quarantine item, and batch outcome is durable.
    ValueTask CompleteAsync(CancellationToken cancellationToken);
    ValueTask RetryAsync(TimeSpan delay, CancellationToken cancellationToken);
    ValueTask DeadLetterAsync(string errorCode, CancellationToken cancellationToken);
    // Disposal releases resources but never implicitly acknowledges a delivery.
}
