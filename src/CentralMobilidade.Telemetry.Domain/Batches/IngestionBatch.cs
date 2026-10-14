using System;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Domain.Batches;

public enum IngestionBatchState
{
    Receiving, Publishing, Accepted, Rejected, PublishFailed
}

public enum IntakeFailure
{
    InvalidRequest, UploadTimeout, CompressedLimit, UnsupportedMediaType, Backpressure,
    RequestCancelled, DurabilityUnconfirmed
}

/// <summary>Evidence supplied only after the application obtains a durable provider confirmation.</summary>
public sealed record DurableBatchConfirmation
{
    public DurableBatchConfirmation(BatchId batchId, DateTimeOffset confirmedAt)
    {
        ArgumentNullException.ThrowIfNull(batchId);
        BatchId = batchId;
        ConfirmedAt = confirmedAt.ToUniversalTime();
    }

    public BatchId BatchId
    {
        get;
    }
    public DateTimeOffset ConfirmedAt
    {
        get;
    }
}

/// <summary>
/// Immutable intake lifecycle. Application orchestration supplies trustworthy confirmation evidence;
/// this aggregate does not perform provider operations or infer that a failed publication sent nothing.
/// </summary>
public sealed class IngestionBatch
{
    private IngestionBatch(BatchId id, string producerId, DateTimeOffset receivedAt,
        DateTimeOffset expiresAt, IngestionBatchState state, DateTimeOffset changedAt,
        long version, DateTimeOffset? durableAt = null, IntakeFailure? failure = null)
    {
        Id = id;
        ProducerId = producerId;
        ReceivedAt = receivedAt;
        ExpiresAt = expiresAt;
        State = state;
        ChangedAt = changedAt;
        Version = version;
        DurableAt = durableAt;
        Failure = failure;
    }

    public BatchId Id
    {
        get;
    }
    public string ProducerId
    {
        get;
    }
    public DateTimeOffset ReceivedAt
    {
        get;
    }
    public DateTimeOffset ExpiresAt
    {
        get;
    }
    public IngestionBatchState State
    {
        get;
    }
    public DateTimeOffset ChangedAt
    {
        get;
    }
    public long Version
    {
        get;
    }
    public DateTimeOffset? DurableAt
    {
        get;
    }
    public IntakeFailure? Failure
    {
        get;
    }

    public static IngestionBatch Receive(BatchId id, string producerId, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        IdentifierValidation.Validate(producerId);
        if (producerId.Length > 128)
            throw new ArgumentException("Producer identifier exceeds 128 characters.", nameof(producerId));
        var utc = receivedAt.ToUniversalTime();
        var expiresAt = utc.AddDays(30);
        return new(id, producerId, utc, expiresAt, IngestionBatchState.Receiving, utc, 0);
    }

    public IngestionBatch BeginPublishing(DateTimeOffset occurredAt) => Transition(
        IngestionBatchState.Receiving, IngestionBatchState.Publishing, occurredAt);

    public IngestionBatch ConfirmAcceptance(DurableBatchConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (confirmation.BatchId != Id)
            throw new ArgumentException("Confirmation belongs to another batch.", nameof(confirmation));
        return Transition(IngestionBatchState.Publishing, IngestionBatchState.Accepted,
            confirmation.ConfirmedAt, durableAt: confirmation.ConfirmedAt);
    }

    public IngestionBatch Reject(IntakeFailure reason, DateTimeOffset occurredAt)
    {
        ValidateFailure(reason);
        if (reason == IntakeFailure.DurabilityUnconfirmed)
            throw new ArgumentException("Durability failure belongs to publication.", nameof(reason));
        return Transition(IngestionBatchState.Receiving, IngestionBatchState.Rejected, occurredAt, failure: reason);
    }

    public IngestionBatch FailPublication(IntakeFailure reason, DateTimeOffset occurredAt)
    {
        ValidateFailure(reason);
        if (reason is not (IntakeFailure.DurabilityUnconfirmed or IntakeFailure.RequestCancelled))
            throw new ArgumentException("Only durability or cancellation failures belong to publication.", nameof(reason));
        return Transition(IngestionBatchState.Publishing, IngestionBatchState.PublishFailed, occurredAt, failure: reason);
    }

    private IngestionBatch Transition(IngestionBatchState source, IngestionBatchState target,
        DateTimeOffset occurredAt, DateTimeOffset? durableAt = null, IntakeFailure? failure = null)
    {
        var utc = occurredAt.ToUniversalTime();
        if (utc < ChangedAt)
            throw new ArgumentOutOfRangeException(nameof(occurredAt), "Transition time cannot move backwards.");
        if (State == target && utc == ChangedAt && DurableAt == durableAt && Failure == failure)
            return this;
        if (State != source)
            throw new InvalidOperationException("The batch cannot perform this intake transition.");
        return new(Id, ProducerId, ReceivedAt, ExpiresAt, target, utc, checked(Version + 1), durableAt, failure);
    }

    private static void ValidateFailure(IntakeFailure reason)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), "Unknown intake failure.");
    }

    public override string ToString() => "[IngestionBatch]";
}
