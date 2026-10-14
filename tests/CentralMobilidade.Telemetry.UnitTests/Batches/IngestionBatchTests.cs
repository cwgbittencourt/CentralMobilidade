using System;
using CentralMobilidade.Telemetry.Domain.Batches;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Batches;

public sealed class IngestionBatchTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 14, 1, 0, 0, TimeSpan.FromHours(-3));
    private static readonly BatchId Id = BatchId.New();
    private static IngestionBatch Receive() => IngestionBatch.Receive(Id, "producer-1", Received);

    [Fact]
    public void Receipt_starts_unconfirmed_with_utc_timestamps_and_30_day_retention()
    {
        var batch = Receive();
        batch.Id.ShouldBe(Id);
        batch.ProducerId.ShouldBe("producer-1");
        batch.State.ShouldBe(IngestionBatchState.Receiving);
        batch.ReceivedAt.Offset.ShouldBe(TimeSpan.Zero);
        batch.ReceivedAt.ShouldBe(Received);
        batch.ExpiresAt.ShouldBe(Received.AddDays(30));
        batch.DurableAt.ShouldBeNull();
        batch.Version.ShouldBe(0);
    }

    [Fact]
    public void Only_matching_durable_confirmation_accepts_a_publishing_batch()
    {
        var receiving = Receive();
        var publishing = receiving.BeginPublishing(Received.AddSeconds(1));
        var confirmation = new DurableBatchConfirmation(Id, Received.AddSeconds(2));
        var accepted = publishing.ConfirmAcceptance(confirmation);
        receiving.State.ShouldBe(IngestionBatchState.Receiving);
        publishing.State.ShouldBe(IngestionBatchState.Publishing);
        publishing.DurableAt.ShouldBeNull();
        accepted.State.ShouldBe(IngestionBatchState.Accepted);
        accepted.DurableAt.ShouldBe(confirmation.ConfirmedAt);
        accepted.DurableAt!.Value.Offset.ShouldBe(TimeSpan.Zero);
        accepted.Version.ShouldBe(2);
        accepted.Failure.ShouldBeNull();
    }

    [Fact]
    public void Confirmation_cannot_skip_publication_or_accept_another_batch()
    {
        Should.Throw<InvalidOperationException>(() => Receive().ConfirmAcceptance(new(Id, Received)));
        var publishing = Receive().BeginPublishing(Received);
        Should.Throw<ArgumentException>(() => publishing.ConfirmAcceptance(new(BatchId.New(), Received)));
        publishing.State.ShouldBe(IngestionBatchState.Publishing);
        publishing.DurableAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(IntakeFailure.InvalidRequest)]
    [InlineData(IntakeFailure.UploadTimeout)]
    [InlineData(IntakeFailure.CompressedLimit)]
    [InlineData(IntakeFailure.UnsupportedMediaType)]
    [InlineData(IntakeFailure.Backpressure)]
    [InlineData(IntakeFailure.RequestCancelled)]
    public void Rejection_before_publication_is_terminal_and_never_durable(IntakeFailure reason)
    {
        var rejected = Receive().Reject(reason, Received.AddSeconds(1));
        rejected.State.ShouldBe(IngestionBatchState.Rejected);
        rejected.Failure.ShouldBe(reason);
        rejected.DurableAt.ShouldBeNull();
        rejected.Version.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => rejected.BeginPublishing(Received.AddSeconds(2)));
        Should.Throw<InvalidOperationException>(() => rejected.ConfirmAcceptance(new(Id, Received.AddSeconds(2))));
    }

    [Theory]
    [InlineData(IntakeFailure.DurabilityUnconfirmed)]
    [InlineData(IntakeFailure.RequestCancelled)]
    public void Publication_failure_does_not_claim_acceptance_or_absence_of_a_message(IntakeFailure reason)
    {
        var failed = Receive().BeginPublishing(Received).FailPublication(reason, Received.AddSeconds(1));
        failed.State.ShouldBe(IngestionBatchState.PublishFailed);
        failed.Failure.ShouldBe(reason);
        failed.DurableAt.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => failed.ConfirmAcceptance(new(Id, Received.AddSeconds(2))));
        Should.Throw<InvalidOperationException>(() => failed.BeginPublishing(Received.AddSeconds(2)));
    }

    [Fact]
    public void Accepted_batch_cannot_be_rejected_failed_or_republished()
    {
        var accepted = Receive().BeginPublishing(Received).ConfirmAcceptance(new(Id, Received));
        Should.Throw<InvalidOperationException>(() => accepted.Reject(IntakeFailure.InvalidRequest, Received));
        Should.Throw<InvalidOperationException>(() => accepted.FailPublication(IntakeFailure.DurabilityUnconfirmed, Received));
        Should.Throw<InvalidOperationException>(() => accepted.BeginPublishing(Received));
    }

    [Fact]
    public void Exact_repeated_transitions_are_noops_but_conflicting_repeats_fail()
    {
        var publishing = Receive().BeginPublishing(Received);
        publishing.BeginPublishing(Received).ShouldBeSameAs(publishing);
        Should.Throw<InvalidOperationException>(() => publishing.BeginPublishing(Received.AddSeconds(1)));
        var confirmation = new DurableBatchConfirmation(Id, Received.AddSeconds(1));
        var accepted = publishing.ConfirmAcceptance(confirmation);
        accepted.ConfirmAcceptance(confirmation).ShouldBeSameAs(accepted);
        Should.Throw<InvalidOperationException>(() => accepted.ConfirmAcceptance(new(Id, Received.AddSeconds(2))));
        var rejected = Receive().Reject(IntakeFailure.InvalidRequest, Received);
        rejected.Reject(IntakeFailure.InvalidRequest, Received).ShouldBeSameAs(rejected);
        Should.Throw<InvalidOperationException>(() => rejected.Reject(IntakeFailure.UploadTimeout, Received));
        var failed = publishing.FailPublication(IntakeFailure.DurabilityUnconfirmed, Received);
        failed.FailPublication(IntakeFailure.DurabilityUnconfirmed, Received).ShouldBeSameAs(failed);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("confirm")]
    [InlineData("reject")]
    [InlineData("fail")]
    public void Transition_times_cannot_move_backwards(string action)
    {
        var batch = Receive();
        var past = Received.AddTicks(-1);
        Should.Throw<ArgumentOutOfRangeException>(() => action switch
        {
            "publish" => batch.BeginPublishing(past),
            "confirm" => batch.BeginPublishing(Received).ConfirmAcceptance(new(Id, past)),
            "reject" => batch.Reject(IntakeFailure.InvalidRequest, past),
            _ => batch.BeginPublishing(Received).FailPublication(IntakeFailure.DurabilityUnconfirmed, past)
        });
        batch.Version.ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("producer\n")]
    public void Producer_identity_must_be_valid(string producer)
    {
        Should.Throw<ArgumentException>(() => IngestionBatch.Receive(Id, producer, Received));
    }

    [Fact]
    public void Malformed_unicode_is_rejected_before_test_runner_serialization()
    {
        Should.Throw<ArgumentException>(() => IngestionBatch.Receive(Id, "\ud800", Received));
    }

    [Fact]
    public void Nulls_oversized_identity_and_unrepresentable_retention_are_rejected()
    {
        Should.Throw<ArgumentNullException>(() => IngestionBatch.Receive(null!, "producer", Received));
        Should.Throw<ArgumentNullException>(() => IngestionBatch.Receive(Id, null!, Received));
        Should.Throw<ArgumentException>(() => IngestionBatch.Receive(Id, new string('p', 129), Received));
        Should.Throw<ArgumentOutOfRangeException>(() => IngestionBatch.Receive(Id, "producer", DateTimeOffset.MaxValue));
        Should.Throw<ArgumentNullException>(() => Receive().BeginPublishing(Received).ConfirmAcceptance(null!));
    }

    [Fact]
    public void Failures_must_match_the_phase_and_never_accept_freeform_diagnostics()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Receive().Reject((IntakeFailure)999, Received));
        Should.Throw<ArgumentException>(() => Receive().Reject(IntakeFailure.DurabilityUnconfirmed, Received));
        Should.Throw<ArgumentException>(() => Receive().BeginPublishing(Received).FailPublication(IntakeFailure.InvalidRequest, Received));
        Should.Throw<InvalidOperationException>(() => Receive().FailPublication(IntakeFailure.DurabilityUnconfirmed, Received));
        Should.Throw<InvalidOperationException>(() => Receive().BeginPublishing(Received).Reject(IntakeFailure.InvalidRequest, Received));
        Receive().ToString().ShouldNotContain("producer-1");
    }
}
