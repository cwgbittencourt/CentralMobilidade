using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Infrastructure.Queue.PubSub;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Queue;

public sealed class PubSubBatchQueueTests
{
    [Theory]
    [InlineData("Topic", "synthetic-secret")]
    [InlineData("Subscription", "projects/other/subscriptions/main")]
    [InlineData("DeadLetterTopic", "projects/project/topics/main")]
    [InlineData("PublishTimeout", "00:00:00")]
    [InlineData("PublishTimeout", "synthetic-secret")]
    [InlineData("PullTimeout", "00:02:00")]
    [InlineData("DrainTimeout", "-00:00:01")]
    [InlineData("MaxAttempts", "0")]
    [InlineData("MaxAttempts", "1001")]
    [InlineData("AckDeadlineSeconds", "9")]
    [InlineData("AckDeadlineSeconds", "601")]
    [InlineData("LeaseRenewalInterval", "00:01:00")]
    [InlineData("MaxLeaseDuration", "00:00:01")]
    [InlineData("RetryDelay", "00:00:00")]
    [InlineData("MaxRetryDelay", "00:11:00")]
    public void Invalid_adapter_settings_fail_without_echoing_configuration(string key, string value)
    {
        var settings = Settings();
        settings["Queue:PubSub:" + key] = value;
        var error = Should.Throw<ArgumentException>(() => new PubSubBatchQueue(new PublisherProbe(), new SubscriberProbe(), Config(settings)));
        error.Message.ShouldBe("Invalid Pub/Sub queue configuration.");
        error.InnerException.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task Missing_or_mismatched_message_ids_never_confirm_a_publish(int ids)
    {
        var publisher = new PublisherProbe { Ids = ids };
        await using var queue = new PubSubBatchQueue(publisher, new SubscriberProbe(), Config());
        using var body = new MemoryStream(Gzip());
        var error = await Should.ThrowAsync<ProviderOperationException>(() => queue.PublishAsync(Envelope(body.ToArray()), body, default).AsTask());
        error.Kind.ShouldBe(ProviderFailureKind.Ambiguous);
        publisher.Calls.ShouldBe(1);
        publisher.Call!.Retry!.MaxAttempts.ShouldBe(1);
        body.CanRead.ShouldBeTrue();
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("truncated")]
    [InlineData("header")]
    public async Task Invalid_stream_bytes_are_rejected_before_the_sdk(string fault)
    {
        var bytes = Gzip();
        var metadata = Envelope(bytes);
        if (fault == "extra")
            Array.Resize(ref bytes, bytes.Length + 1);
        if (fault == "truncated")
            Array.Resize(ref bytes, bytes.Length - 1);
        if (fault == "header")
        {
            bytes[0] = 0;
            metadata = Envelope(bytes); // Matching SHA is insufficient for a non-gzip body.
        }
        var publisher = new PublisherProbe();
        await using var queue = new PubSubBatchQueue(publisher, new SubscriberProbe(), Config());
        using var body = new MemoryStream(bytes);
        var error = await Should.ThrowAsync<ProviderOperationException>(() => queue.PublishAsync(metadata, body, default).AsTask());
        error.ErrorCode.ShouldBe("REQUEST_INVALID");
        error.Kind.ShouldBe(ProviderFailureKind.Permanent);
        publisher.Calls.ShouldBe(0);
        body.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task In_flight_caller_cancellation_is_not_a_provider_failure()
    {
        var publisher = new PublisherProbe { Hold = true };
        await using var queue = new PubSubBatchQueue(publisher, new SubscriberProbe(), Config());
        using var body = new MemoryStream(Gzip());
        using var stop = new CancellationTokenSource();
        var pending = queue.PublishAsync(Envelope(body.ToArray()), body, stop.Token).AsTask();
        await publisher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        body.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task Failed_lease_renewal_prevents_ack_and_surfaces_safe_failure()
    {
        var publisher = new PublisherProbe();
        var subscriber = new SubscriberProbe { FailRenewal = true };
        await using var queue = new PubSubBatchQueue(publisher, subscriber, Config());
        using var body = new MemoryStream(Gzip());
        await queue.PublishAsync(Envelope(body.ToArray()), body, default);
        subscriber.Next = new ReceivedMessage { AckId = "test-ack", Message = publisher.Message };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var iterator = queue.ConsumeAsync(timeout.Token).GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        var delivery = iterator.Current;
        await subscriber.RenewalFailed.Task.WaitAsync(timeout.Token);
        // MoveNext waits for the renewal loop to record the failure, without an arbitrary sleep.
        var failure = await Should.ThrowAsync<ProviderOperationException>(() => iterator.MoveNextAsync().AsTask());
        failure.ToString().ShouldNotContain("synthetic-secret");
        subscriber.Acks.ShouldBe(0);
        await Should.ThrowAsync<ObjectDisposedException>(() => delivery.CompleteAsync(default).AsTask());
    }

    [Fact]
    public async Task Ack_failure_is_sanitized_and_does_not_mark_delivery_complete()
    {
        var publisher = new PublisherProbe();
        var subscriber = new SubscriberProbe { FailAck = true };
        await using var queue = new PubSubBatchQueue(publisher, subscriber, Config());
        using var body = new MemoryStream(Gzip());
        await queue.PublishAsync(Envelope(body.ToArray()), body, default);
        subscriber.Next = new ReceivedMessage { AckId = "test-ack", Message = publisher.Message };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var iterator = queue.ConsumeAsync(timeout.Token).GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        var delivery = iterator.Current;
        for (var i = 0; i < 2; i++)
        {
            var error = await Should.ThrowAsync<ProviderOperationException>(() => delivery.CompleteAsync(timeout.Token).AsTask());
            error.Kind.ShouldBe(ProviderFailureKind.Permanent);
            error.ToString().ShouldNotContain("synthetic-secret");
        }
        subscriber.Acks.ShouldBe(2);
        await delivery.DisposeAsync();
        subscriber.Nacks.ShouldBe(1);
    }

    [Fact]
    public async Task Conflicting_settlement_and_use_after_disposal_are_rejected()
    {
        var publisher = new PublisherProbe();
        var subscriber = new SubscriberProbe();
        await using var queue = new PubSubBatchQueue(publisher, subscriber, Config());
        using var body = new MemoryStream(Gzip());
        await queue.PublishAsync(Envelope(body.ToArray()), body, default);
        subscriber.Next = new ReceivedMessage { AckId = "test-ack", Message = publisher.Message };
        await using var iterator = queue.ConsumeAsync(default).GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await iterator.Current.CompleteAsync(default);
        await Should.ThrowAsync<InvalidOperationException>(() => iterator.Current.RetryAsync(TimeSpan.Zero, default).AsTask());
        subscriber.Acks.ShouldBe(1);
        await queue.DisposeAsync();
        using var next = new MemoryStream(Gzip());
        await Should.ThrowAsync<ObjectDisposedException>(() => queue.PublishAsync(Envelope(next.ToArray()), next, default).AsTask());
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied, ProviderFailureKind.Permanent)]
    [InlineData(StatusCode.NotFound, ProviderFailureKind.Permanent)]
    [InlineData(StatusCode.Unavailable, ProviderFailureKind.Transient)]
    [InlineData(StatusCode.ResourceExhausted, ProviderFailureKind.Transient)]
    public async Task Pull_errors_are_sanitized_and_not_treated_as_empty_poll(StatusCode status, ProviderFailureKind kind)
    {
        await using var queue = new PubSubBatchQueue(new PublisherProbe(), new SubscriberProbe { PullFailure = status }, Config());
        await using var iterator = queue.ConsumeAsync(default).GetAsyncEnumerator();
        var error = await Should.ThrowAsync<ProviderOperationException>(() => iterator.MoveNextAsync().AsTask());
        error.Kind.ShouldBe(kind);
        error.ToString().ShouldNotContain("synthetic-secret");
    }

    [Fact]
    public async Task Empty_pull_deadlines_remain_polling_until_caller_cancels()
    {
        await using var queue = new PubSubBatchQueue(new PublisherProbe(), new SubscriberProbe { PullFailure = StatusCode.DeadlineExceeded }, Config());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await using var iterator = queue.ConsumeAsync(timeout.Token).GetAsyncEnumerator();
        await Should.ThrowAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        ["Queue:PubSub:ProjectId"] = "project",
        ["Queue:PubSub:Topic"] = "projects/project/topics/main",
        ["Queue:PubSub:Subscription"] = "projects/project/subscriptions/main",
        ["Queue:PubSub:DeadLetterTopic"] = "projects/project/topics/dead",
        ["Queue:PubSub:DrainTimeout"] = "00:00:00",
        ["Queue:PubSub:LeaseRenewalInterval"] = "00:00:00.100"
    };
    private static IConfiguration Config(Dictionary<string, string?>? settings = null) => new ConfigurationBuilder().AddInMemoryCollection(settings ?? Settings()).Build();
    private static TelemetryBatchEnvelope Envelope(byte[] bytes) => new("1", Guid.NewGuid(), new string('a', 32), "unit", DateTimeOffset.UtcNow,
        "text/plain; charset=utf-8", "gzip", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    private static byte[] Gzip()
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, true))
            gzip.Write("synthetic compressed bytes"u8);
        return bytes.ToArray();
    }

    private sealed class PublisherProbe : PublisherServiceApiClient
    {
        internal int Ids = 1, Calls;
        internal bool Hold;
        internal PubsubMessage Message = null!;
        internal CallSettings? Call;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<PublishResponse> PublishAsync(PublishRequest request, CallSettings? callSettings = null)
        {
            Calls++;
            Message = request.Messages[0];
            Call = callSettings;
            Entered.TrySetResult();
            if (Hold)
                await Task.Delay(Timeout.InfiniteTimeSpan, callSettings!.CancellationToken!.Value);
            var result = new PublishResponse();
            for (var i = 0; i < Ids; i++)
                result.MessageIds.Add("id-" + i);
            if (Ids == -1)
                result.MessageIds.Add(" ");
            return result;
        }
    }

    private sealed class SubscriberProbe : SubscriberServiceApiClient
    {
        internal ReceivedMessage? Next;
        internal bool FailRenewal, FailAck;
        internal StatusCode? PullFailure;
        internal int Acks, Nacks, leases;
        internal TaskCompletionSource RenewalFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<PullResponse> PullAsync(PullRequest request, CallSettings? callSettings = null)
        {
            if (PullFailure is StatusCode status)
                throw new RpcException(new Status(status, "synthetic-secret"));
            var response = new PullResponse();
            var message = Interlocked.Exchange(ref Next, null);
            if (message is not null)
                response.ReceivedMessages.Add(message);
            return Task.FromResult(response);
        }
        public override Task AcknowledgeAsync(AcknowledgeRequest request, CallSettings? callSettings = null)
        {
            Acks++;
            if (FailAck)
                throw new RpcException(new Status(StatusCode.PermissionDenied, "synthetic-secret"));
            return Task.CompletedTask;
        }
        public override Task ModifyAckDeadlineAsync(ModifyAckDeadlineRequest request, CallSettings? callSettings = null)
        {
            if (request.AckDeadlineSeconds == 0)
                Nacks++;
            else if (++leases > 1 && FailRenewal)
            {
                RenewalFailed.TrySetResult();
                throw new RpcException(new Status(StatusCode.Unavailable, "synthetic-secret"));
            }
            return Task.CompletedTask;
        }
    }
}
