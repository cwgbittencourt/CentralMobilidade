using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Infrastructure.Queue.PubSub;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Queue;

[Collection("PubSub emulator")]
public sealed class PubSubAdapterSafetyTests(PubSubEmulatorFixture emulator)
{
    [Theory]
    [InlineData("schema", "INVALID_ENVELOPE")]
    [InlineData("digest", "INVALID_ENVELOPE")]
    [InlineData("size", "INVALID_ENVELOPE")]
    [InlineData("missing", "INVALID_ENVELOPE")]
    [InlineData("attempt", "INVALID_ENVELOPE")]
    [InlineData("attempt-zero", "INVALID_ENVELOPE")]
    [InlineData("future", "INVALID_ENVELOPE")]
    [InlineData("expired", "RETRY_EXHAUSTED")]
    [InlineData("exhausted", "RETRY_EXHAUSTED")]
    public async Task Poison_or_expired_native_messages_are_confirmed_to_dead_topic_before_removal(string fault, string code)
    {
        await using var resources = await emulator.CreateResourcesAsync();
        var message = Message();
        switch (fault)
        {
            case "schema":
                message.Attributes["schemaVersion"] = "2";
                break;
            case "digest":
                message.Attributes["payloadSha256"] = new string('0', 64);
                break;
            case "size":
                message.Attributes["compressedBytes"] = "1";
                break;
            case "missing":
                message.Attributes.Remove("batchId");
                break;
            case "attempt":
                message.Attributes["deliveryAttempt"] = "synthetic-private-diagnostic";
                break;
            case "attempt-zero":
                message.Attributes["deliveryAttempt"] = "0";
                break;
            case "future":
                message.Attributes["receivedAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
                break;
            case "expired":
                message.Attributes["receivedAt"] = DateTimeOffset.UtcNow.AddDays(-8).ToString("O");
                break;
            case "exhausted":
                message.Attributes["deliveryAttempt"] = "11";
                break;
        }
        message.Attributes["untrusted-name"] = "synthetic-private-diagnostic";
        await PublishNative(resources, message);
        var subscriber = new TrackingSubscriber(emulator.Subscriber);
        await using var queue = Create(resources, subscriber: subscriber);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var iterator = queue.ConsumeAsync(deadline.Token).GetAsyncEnumerator();
        var pending = iterator.MoveNextAsync().AsTask();
        var dead = (await resources.PullAsync(resources.DeadSubscription, true)).ShouldNotBeNull();
        dead.Message.Data.ShouldBe(message.Data);
        dead.Message.Attributes["errorCode"].ShouldBe(code);
        dead.Message.Attributes.ContainsKey("untrusted-name").ShouldBeFalse();
        await resources.AcknowledgeAsync(resources.DeadSubscription, dead.AckId);
        await subscriber.Acknowledged.Task.WaitAsync(deadline.Token);
        await deadline.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        (await resources.PullAsync(resources.Subscription, false)).ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_retry_or_dead_letter_copy_leaves_original_available(bool deadLetter)
    {
        await using var resources = await emulator.CreateResourcesAsync();
        var message = Message();
        await PublishNative(resources, message);
        await using var queue = Create(resources, new FailedPublisher());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var iterator = queue.ConsumeAsync(deadline.Token).GetAsyncEnumerator())
        {
            (await iterator.MoveNextAsync()).ShouldBeTrue();
            await using var delivery = iterator.Current;
            var error = await Should.ThrowAsync<ProviderOperationException>(() => (deadLetter
                ? delivery.DeadLetterAsync("LP_INVALID_SYNTAX", deadline.Token)
                : delivery.RetryAsync(TimeSpan.Zero, deadline.Token)).AsTask());
            error.Kind.ShouldBe(ProviderFailureKind.Transient);
            error.ToString().ShouldNotContain("synthetic-private-diagnostic");
        }
        var original = (await resources.PullAsync(resources.Subscription, true)).ShouldNotBeNull();
        original.Message.Data.ShouldBe(message.Data);
        original.Message.Attributes.ShouldBe(message.Attributes);
        await resources.AcknowledgeAsync(resources.Subscription, original.AckId);
        (await resources.PullAsync(resources.DeadSubscription, false)).ShouldBeNull();
    }

    [Fact]
    public async Task Lease_is_renewed_beyond_subscription_deadline_and_released_without_ack_on_disposal()
    {
        await using var resources = await emulator.CreateResourcesAsync();
        var message = Message();
        await PublishNative(resources, message);
        await using var queue = Create(resources);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await using (var iterator = queue.ConsumeAsync(deadline.Token).GetAsyncEnumerator())
        {
            (await iterator.MoveNextAsync()).ShouldBeTrue();
            await Task.Delay(TimeSpan.FromSeconds(11), deadline.Token); // Subscription's real ACK deadline is 10s.
            (await resources.PullAsync(resources.Subscription, false)).ShouldBeNull();
            await iterator.Current.DisposeAsync();
            await Should.ThrowAsync<ObjectDisposedException>(() => iterator.Current.CompleteAsync(deadline.Token).AsTask());
        }
        var original = (await resources.PullAsync(resources.Subscription, true)).ShouldNotBeNull();
        original.Message.Data.ShouldBe(message.Data);
        await resources.AcknowledgeAsync(resources.Subscription, original.AckId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_settlement_bounds_retries_and_replaces_unknown_error_text(bool exhausted)
    {
        await using var resources = await emulator.CreateResourcesAsync();
        var message = Message();
        if (exhausted)
            message.Attributes["deliveryAttempt"] = "10";
        await PublishNative(resources, message);
        await using var queue = Create(resources);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var iterator = queue.ConsumeAsync(deadline.Token).GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await using var delivery = iterator.Current;
        if (exhausted)
            await delivery.RetryAsync(TimeSpan.Zero, deadline.Token);
        else
            await delivery.DeadLetterAsync("QZX9J83", deadline.Token); // Syntax alone must not admit a plate as an error code.
        var dead = (await resources.PullAsync(resources.DeadSubscription, true)).ShouldNotBeNull();
        dead.Message.Data.ShouldBe(message.Data);
        dead.Message.Attributes["batchId"].ShouldBe(message.Attributes["batchId"]);
        dead.Message.Attributes["errorCode"].ShouldBe(exhausted ? "RETRY_EXHAUSTED" : "POISON_BATCH");
        await resources.AcknowledgeAsync(resources.DeadSubscription, dead.AckId);
        (await resources.PullAsync(resources.Subscription, false)).ShouldBeNull();
    }

    private PubSubBatchQueue Create(PubSubResources resources, PublisherServiceApiClient? publisher = null, SubscriberServiceApiClient? subscriber = null) => new(
        publisher ?? emulator.Publisher, subscriber ?? emulator.Subscriber, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Queue:PubSub:ProjectId"] = emulator.Project,
            ["Queue:PubSub:Topic"] = resources.Topic,
            ["Queue:PubSub:Subscription"] = resources.Subscription,
            ["Queue:PubSub:DeadLetterTopic"] = resources.DeadTopic,
            ["Queue:PubSub:PublishTimeout"] = "00:00:05",
            ["Queue:PubSub:PullTimeout"] = "00:00:01",
            ["Queue:PubSub:DrainTimeout"] = "00:00:00",
            ["Queue:PubSub:RetryDelay"] = "00:00:00.010",
            ["Queue:PubSub:AckDeadlineSeconds"] = "10",
            ["Queue:PubSub:LeaseRenewalInterval"] = "00:00:01"
        }).Build());

    private Task<PublishResponse> PublishNative(PubSubResources resources, PubsubMessage message) => emulator.Publisher.PublishAsync(
        new PublishRequest { Topic = resources.Topic, Messages = { message } }, PubSubEmulatorFixture.RpcDeadline());

    private static PubsubMessage Message() => new()
    {
        Data = ByteString.CopyFrom(PubSubEmulatorFixture.Gzip),
        Attributes =
        {
            ["schemaVersion"] = "1", ["batchId"] = Guid.NewGuid().ToString("D"), ["traceId"] = new string('a', 32),
            ["producerId"] = "t080", ["receivedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["contentType"] = "text/plain; charset=utf-8", ["contentEncoding"] = "gzip",
            ["compressedBytes"] = PubSubEmulatorFixture.Gzip.Length.ToString(CultureInfo.InvariantCulture),
            ["payloadSha256"] = Convert.ToHexStringLower(SHA256.HashData(PubSubEmulatorFixture.Gzip))
        }
    };

    private sealed class FailedPublisher : PublisherServiceApiClient
    {
        public override Task<PublishResponse> PublishAsync(PublishRequest request, CallSettings? callSettings = null)
            => throw new RpcException(new Status(StatusCode.Unavailable, "synthetic-private-diagnostic"));
    }

    private sealed class TrackingSubscriber(SubscriberServiceApiClient inner) : SubscriberServiceApiClient
    {
        internal TaskCompletionSource Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<PullResponse> PullAsync(PullRequest request, CallSettings? callSettings = null) => inner.PullAsync(request, callSettings);
        public override Task ModifyAckDeadlineAsync(ModifyAckDeadlineRequest request, CallSettings? callSettings = null)
            => inner.ModifyAckDeadlineAsync(request, callSettings);
        public override async Task AcknowledgeAsync(AcknowledgeRequest request, CallSettings? callSettings = null)
        {
            await inner.AcknowledgeAsync(request, callSettings);
            Acknowledged.TrySetResult();
        }
    }
}
