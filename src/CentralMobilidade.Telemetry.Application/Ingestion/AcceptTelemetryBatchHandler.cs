using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CentralMobilidade.Telemetry.Application.Ingestion;

public sealed record AcceptTelemetryBatchCommand(TelemetryBatchEnvelope Metadata, Stream GzipBody);

public sealed record AcceptedTelemetryBatch(BatchId BatchId, DateTimeOffset ReceivedAt);

public sealed class AcceptTelemetryBatchHandler(
    IBatchStateStore batches,
    IDurableBatchQueue queue,
    IClock clock)
{
    public async ValueTask<AcceptedTelemetryBatch> HandleAsync(
        AcceptTelemetryBatchCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Metadata);
        ArgumentNullException.ThrowIfNull(command.GzipBody);
        if (!command.GzipBody.CanRead)
            throw new ArgumentException("The gzip body must be readable.", nameof(command));

        var metadata = command.Metadata;
        metadata.Validate();
        var snapshot = await batches.CreateAsync(metadata, metadata.ReceivedAt.ToUniversalTime().AddDays(30),
            cancellationToken).ConfigureAwait(false);

        snapshot = await EnterPublishingAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (snapshot.State == BatchLifecycle.Accepted)
            return new(snapshot.BatchId, snapshot.ReceivedAt);

        try
        {
            var receipt = await queue.PublishAsync(metadata, command.GzipBody, cancellationToken).ConfigureAwait(false);
            ValidateReceipt(snapshot, receipt);

            var accepted = await batches.TryTransitionAsync(snapshot.BatchId, snapshot.Version,
                new BatchTransition(BatchLifecycle.Accepted, receipt.ConfirmedAt), cancellationToken)
                .ConfigureAwait(false);
            if (!accepted)
            {
                var current = await batches.GetAsync(snapshot.BatchId, cancellationToken).ConfigureAwait(false);
                if (current?.State != BatchLifecycle.Accepted)
                    throw DurabilityUnconfirmed();
                snapshot = current;
            }

            return new(snapshot.BatchId, snapshot.ReceivedAt);
        }
        catch (ProviderOperationException error)
        {
            await TryRecordPublicationFailureAsync(snapshot, error.ErrorCode).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryRecordPublicationFailureAsync(snapshot, TelemetryErrorCodes.DurabilityUnconfirmed)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<BatchSnapshot> EnterPublishingAsync(
        BatchSnapshot snapshot, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (snapshot.State == BatchLifecycle.Accepted)
                return snapshot;
            if (snapshot.State == BatchLifecycle.Publishing)
                return snapshot;
            if (snapshot.State != BatchLifecycle.Receiving)
                throw DurabilityUnconfirmed();

            var occurredAt = LaterOf(clock.UtcNow, snapshot.ReceivedAt);
            if (await batches.TryTransitionAsync(snapshot.BatchId, snapshot.Version,
                new BatchTransition(BatchLifecycle.Publishing, occurredAt), cancellationToken).ConfigureAwait(false))
                return snapshot with
                {
                    State = BatchLifecycle.Publishing,
                    Version = snapshot.Version + 1
                };

            snapshot = await batches.GetAsync(snapshot.BatchId, cancellationToken).ConfigureAwait(false)
                ?? throw DurabilityUnconfirmed();
        }

        throw DurabilityUnconfirmed();
    }

    private static void ValidateReceipt(BatchSnapshot snapshot, DurablePublishReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.BatchId != snapshot.BatchId || receipt.ConfirmedAt.Offset != TimeSpan.Zero ||
            receipt.ConfirmedAt < snapshot.ReceivedAt)
            throw DurabilityUnconfirmed();
    }

    private async ValueTask TryRecordPublicationFailureAsync(BatchSnapshot snapshot, string errorCode)
    {
        try
        {
            await batches.TryTransitionAsync(snapshot.BatchId, snapshot.Version,
                new BatchTransition(BatchLifecycle.PublishFailed,
                    LaterOf(clock.UtcNow, snapshot.ReceivedAt), ErrorCode: errorCode), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original publication failure remains authoritative. A concurrent acceptance or an
            // unavailable state store must not be turned into a false success or mask that failure.
        }
    }

    private static DateTimeOffset LaterOf(DateTimeOffset left, DateTimeOffset right)
    {
        var utc = left.ToUniversalTime();
        return utc >= right ? utc : right;
    }

    private static ProviderOperationException DurabilityUnconfirmed() =>
        new(TelemetryErrorCodes.DurabilityUnconfirmed, ProviderFailureKind.Ambiguous);
}

public static class TelemetryIngestionRegistration
{
    public static IServiceCollection AddTelemetryIngestion(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped(provider => new AcceptTelemetryBatchHandler(
            provider.GetRequiredService<IBatchStateStore>(),
            provider.GetRequiredService<IDurableBatchQueue>(),
            provider.GetRequiredService<IClock>()));
        return services;
    }
}
