using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Ingestion;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Ingestion;

public sealed class AcceptTelemetryBatchHandlerTests
{
    [Fact]
    public async Task Acceptance_is_not_recorded_or_returned_before_the_matching_durable_confirmation()
    {
        var fixture = new Fixture();
        var handling = fixture.Handle();

        await fixture.Queue.PublishStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        handling.IsCompleted.ShouldBeFalse();
        fixture.Store.States.ShouldBe([BatchLifecycle.Receiving, BatchLifecycle.Publishing]);

        fixture.Queue.Confirm();
        var accepted = await handling;

        accepted.BatchId.ShouldBe(fixture.BatchId);
        fixture.Store.States.ShouldBe([BatchLifecycle.Receiving, BatchLifecycle.Publishing, BatchLifecycle.Accepted]);
        fixture.Store.AcceptedAfterQueueConfirmation.ShouldBeTrue();
    }

    [Fact]
    public async Task A_confirmation_for_another_batch_is_never_accepted()
    {
        var fixture = new Fixture();
        fixture.Queue.ReceiptBatchId = BatchId.New();
        var handling = fixture.Handle();
        await fixture.Queue.PublishStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Queue.Confirm();

        var error = await Should.ThrowAsync<ProviderOperationException>(() => handling);

        error.ErrorCode.ShouldBe(TelemetryErrorCodes.DurabilityUnconfirmed);
        fixture.Store.States.ShouldBe([BatchLifecycle.Receiving, BatchLifecycle.Publishing, BatchLifecycle.PublishFailed]);
    }

    [Fact]
    public async Task Provider_failure_is_preserved_and_cannot_produce_acceptance()
    {
        var fixture = new Fixture();
        fixture.Queue.Failure = new ProviderOperationException(
            TelemetryErrorCodes.DurabilityUnconfirmed, ProviderFailureKind.Transient);

        var error = await Should.ThrowAsync<ProviderOperationException>(() => fixture.Handle());

        error.ShouldBeSameAs(fixture.Queue.Failure);
        fixture.Store.States.ShouldBe([BatchLifecycle.Receiving, BatchLifecycle.Publishing, BatchLifecycle.PublishFailed]);
    }

    [Fact]
    public async Task Cancellation_during_publication_is_preserved_and_cannot_produce_acceptance()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var handling = fixture.Handle(cancellation.Token);
        await fixture.Queue.PublishStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => handling);
        fixture.Store.States.ShouldBe([BatchLifecycle.Receiving, BatchLifecycle.Publishing, BatchLifecycle.PublishFailed]);
    }

    [Fact]
    public async Task An_already_accepted_exact_replay_does_not_publish_again()
    {
        var fixture = new Fixture(BatchLifecycle.Accepted, version: 3);

        var accepted = await fixture.Handle();

        accepted.BatchId.ShouldBe(fixture.BatchId);
        fixture.Queue.PublishCount.ShouldBe(0);
        fixture.Store.States.ShouldBe([BatchLifecycle.Accepted]);
    }

    [Fact]
    public async Task State_failure_after_confirmation_returns_durability_failure_instead_of_success()
    {
        var fixture = new Fixture();
        fixture.Store.RejectAcceptance = true;
        var handling = fixture.Handle();
        await fixture.Queue.PublishStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Queue.Confirm();

        var error = await Should.ThrowAsync<ProviderOperationException>(() => handling);

        error.Kind.ShouldBe(ProviderFailureKind.Ambiguous);
        fixture.Store.States.ShouldNotContain(BatchLifecycle.Accepted);
    }

    private sealed class Fixture
    {
        private readonly TelemetryBatchEnvelope metadata;
        internal BatchId BatchId
        {
            get;
        }
        internal QueueStub Queue
        {
            get;
        }
        internal StateStoreStub Store
        {
            get;
        }

        internal Fixture(BatchLifecycle initialState = BatchLifecycle.Receiving, long version = 1)
        {
            BatchId = BatchId.New();
            var receivedAt = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
            metadata = new("1", BatchId.Value, "0123456789abcdef0123456789abcdef", "producer-test",
                receivedAt, "text/plain; charset=utf-8", "gzip", 10, new string('a', 64), "v1");
            Queue = new(BatchId, receivedAt.AddSeconds(2));
            Store = new(BatchId, receivedAt, initialState, version, Queue);
        }

        internal Task<AcceptedTelemetryBatch> Handle(CancellationToken cancellationToken = default)
        {
            var handler = new AcceptTelemetryBatchHandler(Store, Queue, new FixedClock(metadata.ReceivedAt.AddSeconds(1)));
            return handler.HandleAsync(new(metadata, new MemoryStream(new byte[10])), cancellationToken).AsTask();
        }
    }

    private sealed class QueueStub(BatchId batchId, DateTimeOffset confirmedAt) : IDurableBatchQueue
    {
        private readonly TaskCompletionSource confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource PublishStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal BatchId ReceiptBatchId { get; set; } = batchId;
        internal ProviderOperationException? Failure
        {
            get; set;
        }
        internal int PublishCount
        {
            get; private set;
        }
        internal bool IsConfirmed => confirmation.Task.IsCompletedSuccessfully;

        internal void Confirm() => confirmation.TrySetResult();

        public async ValueTask<DurablePublishReceipt> PublishAsync(
            TelemetryBatchEnvelope metadata, Stream gzipBody, CancellationToken cancellationToken)
        {
            PublishCount++;
            PublishStarted.TrySetResult();
            if (Failure is not null)
                throw Failure;
            await confirmation.Task.WaitAsync(cancellationToken);
            return new(ReceiptBatchId, confirmedAt);
        }

        public IAsyncEnumerable<IQueueDelivery> ConsumeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StateStoreStub(
        BatchId batchId,
        DateTimeOffset receivedAt,
        BatchLifecycle initialState,
        long version,
        QueueStub queue) : IBatchStateStore
    {
        private BatchSnapshot current = Snapshot(batchId, receivedAt, initialState, version);
        internal List<BatchLifecycle> States { get; } = [initialState];
        internal bool RejectAcceptance
        {
            get; set;
        }
        internal bool AcceptedAfterQueueConfirmation
        {
            get; private set;
        }

        public ValueTask<BatchSnapshot> CreateAsync(
            TelemetryBatchEnvelope metadata, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
            ValueTask.FromResult(current);

        public ValueTask<BatchSnapshot?> GetAsync(BatchId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult<BatchSnapshot?>(current);

        public ValueTask<bool> TryTransitionAsync(
            BatchId id, long expectedVersion, BatchTransition transition, CancellationToken cancellationToken)
        {
            if (current.Version != expectedVersion || transition.TargetState == BatchLifecycle.Accepted && RejectAcceptance)
                return ValueTask.FromResult(false);
            if (transition.TargetState == BatchLifecycle.Accepted)
                AcceptedAfterQueueConfirmation = queue.IsConfirmed;
            current = current with
            {
                State = transition.TargetState,
                Version = current.Version + 1
            };
            States.Add(transition.TargetState);
            return ValueTask.FromResult(true);
        }

        public ValueTask RecordLineResultsAsync(BatchId id, IReadOnlyCollection<LineProcessingOutcome> results,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<RejectionResultPage> GetRejectionsAsync(
            BatchId id, string? cursor, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static BatchSnapshot Snapshot(
            BatchId id, DateTimeOffset receivedAt, BatchLifecycle state, long version) =>
            new(id, "producer-test", state, receivedAt, receivedAt.AddDays(30), null, null,
                new(0, 0, 0, null), new Dictionary<string, long>(), [], false, version);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}
