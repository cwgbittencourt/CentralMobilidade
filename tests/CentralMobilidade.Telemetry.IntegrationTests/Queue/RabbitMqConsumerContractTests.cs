using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Queue;

[Trait("Category", "Integration")]
public sealed class RabbitMqConsumerContractTests(RabbitMqContractBroker broker) : IClassFixture<RabbitMqContractBroker>
{
    [Fact]
    public async Task Native_body_prefetch_and_manual_ack_are_preserved()
    {
        await using var test = await Create();
        var id = await test.Publish();
        await test.Publish();
        await using var iterator = test.Consumer.ConsumeAsync().GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await using var first = iterator.Current;
        first.Metadata.BatchId.ShouldBe(id);
        first.DeliveryAttempt.ShouldBe(1);
        using var copy = new MemoryStream();
        await first.Body.CopyToAsync(copy);
        copy.ToArray().ShouldBe(Test.Payload);
        (await test.Channel.QueueDeclarePassiveAsync(test.Topology.Options.Queue)).MessageCount.ShouldBe(1u);
        await first.CompleteAsync(default);
        await first.CompleteAsync(default);
        await Should.ThrowAsync<InvalidOperationException>(async () => await first.DeadLetterAsync("POISON", default));
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await using var second = iterator.Current;
        await second.CompleteAsync(default);
    }

    [Fact]
    public async Task Disposal_never_acknowledges_and_channel_close_redelivers()
    {
        await using var test = await Create();
        var id = await test.Publish();
        await using (var iterator = test.Consumer.ConsumeAsync().GetAsyncEnumerator())
        {
            (await iterator.MoveNextAsync()).ShouldBeTrue();
            await iterator.Current.DisposeAsync();
        }
        await using var retry = test.Consumer.ConsumeAsync().GetAsyncEnumerator();
        (await retry.MoveNextAsync()).ShouldBeTrue();
        await using var delivery = retry.Current;
        delivery.Metadata.BatchId.ShouldBe(id);
        await delivery.CompleteAsync(default);
    }

    [Fact]
    public async Task Retry_waits_and_preserves_identity_body_and_attempt()
    {
        await using var test = await Create();
        var id = await test.Publish();
        await using var iterator = test.Consumer.ConsumeAsync().GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await using var first = iterator.Current;
        var clock = Stopwatch.StartNew();
        await first.RetryAsync(TimeSpan.FromMilliseconds(180), default);
        clock.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(170));
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await using var second = iterator.Current;
        second.DeliveryAttempt.ShouldBe(2);
        second.Metadata.BatchId.ShouldBe(id);
        second.Metadata.ShouldBe(first.Metadata);
        using var copy = new MemoryStream();
        await second.Body.CopyToAsync(copy);
        copy.ToArray().ShouldBe(Test.Payload);
        await second.CompleteAsync(default);
    }

    [Fact]
    public async Task Exhausted_attempt_routes_persistent_native_body_to_dead_queue()
    {
        await using var test = await Create(maxAttempts: 1);
        await test.Publish();
        await using var iterator = test.Consumer.ConsumeAsync().GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await using var delivery = iterator.Current;
        await delivery.RetryAsync(TimeSpan.Zero, default);
        var dead = await test.GetDead();
        dead.Body.ToArray().ShouldBe(Test.Payload);
        dead.BasicProperties.Persistent.ShouldBeTrue();
        Text(dead.BasicProperties.Headers!["errorCode"]).ShouldBe("RETRY_EXHAUSTED");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("attempt")]
    public async Task Invalid_envelope_is_not_exposed_and_is_confirmed_to_dead_queue(string fault)
    {
        await using var test = await Create();
        await test.Publish();
        var original = (await test.Channel.BasicGetAsync(test.Topology.Options.Queue, true))!;
        var properties = new BasicProperties(original.BasicProperties);
        properties.Headers!["untrusted-secret"] = "must-not-forward";
        if (fault == "missing")
            properties.Headers.Remove("batchId");
        if (fault == "hash")
            properties.Headers["payloadSha256"] = new string('0', 64);
        if (fault == "attempt")
            properties.Headers["deliveryAttempt"] = -1;
        await test.Channel.BasicPublishAsync(test.Topology.Options.Exchange, test.Topology.Options.RoutingKey, true, properties, original.Body);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var iterator = test.Consumer.ConsumeAsync(cancel.Token).GetAsyncEnumerator();
        var next = iterator.MoveNextAsync().AsTask();
        var dead = await test.GetDead();
        dead.Body.ToArray().ShouldBe(Test.Payload);
        dead.BasicProperties.Headers!.ContainsKey("untrusted-secret").ShouldBeFalse();
        Text(dead.BasicProperties.Headers["errorCode"]).ShouldBe("INVALID_ENVELOPE");
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await next);
    }

    [Fact]
    public async Task Stop_drains_delivery_completed_by_inflight_handler()
    {
        await using var test = await Create(drain: "00:00:02");
        await test.Publish();
        using var cancel = new CancellationTokenSource();
        var iterator = test.Consumer.ConsumeAsync(cancel.Token).GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await using var delivery = iterator.Current;
        await cancel.CancelAsync();
        var closing = iterator.DisposeAsync().AsTask();
        await Task.Delay(80);
        closing.IsCompleted.ShouldBeFalse();
        await delivery.CompleteAsync(default);
        await closing.WaitAsync(TimeSpan.FromSeconds(3));
        (await test.Channel.BasicGetAsync(test.Topology.Options.Queue, true)).ShouldBeNull();
    }

    [Fact]
    public async Task Drain_deadline_requeues_unfinished_delivery()
    {
        await using var test = await Create(drain: "00:00:00.100");
        var id = await test.Publish();
        var iterator = test.Consumer.ConsumeAsync().GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await iterator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await using var next = test.Consumer.ConsumeAsync().GetAsyncEnumerator();
        (await next.MoveNextAsync()).ShouldBeTrue();
        await using var delivery = next.Current;
        delivery.Metadata.BatchId.ShouldBe(id);
        await delivery.CompleteAsync(default);
    }

    [Fact]
    public async Task Unroutable_retry_never_acknowledges_original()
    {
        await using var test = await Create();
        var id = await test.Publish();
        var iterator = test.Consumer.ConsumeAsync().GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await test.Channel.QueueUnbindAsync(test.Topology.Options.Queue, test.Topology.Options.Exchange, test.Topology.Options.RoutingKey);
        await Should.ThrowAsync<ProviderOperationException>(async () => await iterator.Current.RetryAsync(TimeSpan.Zero, default));
        await iterator.DisposeAsync();
        var original = await test.Channel.BasicGetAsync(test.Topology.Options.Queue, true);
        original.ShouldNotBeNull();
        original.BasicProperties.MessageId.ShouldBe(id.ToString("D"));
    }

    [Fact]
    public async Task Cancelled_retry_leaves_original_available_for_redelivery()
    {
        await using var test = await Create();
        var id = await test.Publish();
        var iterator = test.Consumer.ConsumeAsync().GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        using var cancellation = new CancellationTokenSource(30);
        await Should.ThrowAsync<OperationCanceledException>(async () => await iterator.Current.RetryAsync(TimeSpan.FromSeconds(1), cancellation.Token));
        await iterator.DisposeAsync();
        var original = await test.Channel.BasicGetAsync(test.Topology.Options.Queue, true);
        original.ShouldNotBeNull();
        original.BasicProperties.MessageId.ShouldBe(id.ToString("D"));
    }

    [Fact]
    public async Task Dead_letter_failure_preserves_original_and_sanitizes_error_code()
    {
        await using var test = await Create();
        await test.Publish();
        var iterator = test.Consumer.ConsumeAsync().GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        await test.Channel.QueueUnbindAsync(test.Topology.Options.DeadLetterQueue, test.Topology.Options.DeadLetterExchange, test.Topology.Options.RoutingKey);
        await Should.ThrowAsync<ProviderOperationException>(async () => await iterator.Current.DeadLetterAsync("raw secret value", default));
        await iterator.DisposeAsync();
        await test.Channel.QueueBindAsync(test.Topology.Options.DeadLetterQueue, test.Topology.Options.DeadLetterExchange, test.Topology.Options.RoutingKey);
        await using var next = test.Consumer.ConsumeAsync().GetAsyncEnumerator();
        (await next.MoveNextAsync()).ShouldBeTrue();
        await using var delivery = next.Current;
        await delivery.DeadLetterAsync("raw secret value", default);
        Text((await test.GetDead()).BasicProperties.Headers!["errorCode"]).ShouldBe("POISON_BATCH");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Expired_or_excess_attempt_envelope_bypasses_handler(bool expired)
    {
        await using var test = await Create(maxAttempts: 2);
        await test.Publish();
        var message = (await test.Channel.BasicGetAsync(test.Topology.Options.Queue, true))!;
        var properties = new BasicProperties(message.BasicProperties);
        if (expired)
            properties.Headers!["receivedAt"] = DateTimeOffset.UtcNow.AddDays(-8).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        else
            properties.Headers!["deliveryAttempt"] = 3;
        await test.Channel.BasicPublishAsync(test.Topology.Options.Exchange, test.Topology.Options.RoutingKey, true, properties, message.Body);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var iterator = test.Consumer.ConsumeAsync(cancel.Token).GetAsyncEnumerator();
        var next = iterator.MoveNextAsync().AsTask();
        Text((await test.GetDead()).BasicProperties.Headers!["errorCode"]).ShouldBe("RETRY_EXHAUSTED");
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await next);
    }

    [Theory]
    [InlineData("Prefetch", "0")]
    [InlineData("Prefetch", "65")]
    [InlineData("MaxAttempts", "0")]
    [InlineData("MaxRetryDelay", "00:11:00")]
    [InlineData("DrainTimeout", "-00:00:01")]
    public void Unsafe_limits_fail_before_connecting(string key, string value)
    {
        var settings = new Dictionary<string, string?> { ["Queue:RabbitMq:Host"] = "localhost", ["Queue:RabbitMq:Username"] = "guest", ["Queue:RabbitMq:Password"] = "guest", ["Queue:RabbitMq:" + key] = value };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var manager = new RabbitMqConnectionManager(config, new(config));
        Should.Throw<ArgumentException>(() => new RabbitMqBatchConsumer(manager, config));
    }

    private async Task<Test> Create(int maxAttempts = 10, string drain = "00:00:00")
    {
        var prefix = "consumer." + Guid.NewGuid().ToString("N");
        var endpoint = broker.Endpoint;
        var credentials = endpoint.UserInfo.Split(':');
        var settings = new Dictionary<string, string?>
        {
            ["Queue:RabbitMq:Host"] = endpoint.Host,
            ["Queue:RabbitMq:Port"] = endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Queue:RabbitMq:Username"] = Uri.UnescapeDataString(credentials[0]),
            ["Queue:RabbitMq:Password"] = Uri.UnescapeDataString(credentials[1]),
            ["Queue:RabbitMq:Prefetch"] = "1",
            ["Queue:RabbitMq:MaxAttempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Queue:RabbitMq:RetryDelay"] = "00:00:00.050",
            ["Queue:RabbitMq:MaxRetryDelay"] = "00:00:01",
            ["Queue:RabbitMq:DrainTimeout"] = drain
        };
        foreach (var key in new[] { "Exchange", "Queue", "RetryExchange", "RetryQueue", "DeadLetterExchange", "DeadLetterQueue" })
            settings["Queue:RabbitMq:" + key] = prefix + "." + key;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var topology = new RabbitMqTopology(config);
        var connections = new RabbitMqConnectionManager(config, topology);
        var channel = await connections.CreatePublishChannelAsync();
        return new(connections, channel, topology, new(channel, config), new(connections, config));
    }
    private static string Text(object? value) => value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : (string)value!;

    private sealed record Test(RabbitMqConnectionManager Connections, IChannel Channel, RabbitMqTopology Topology, RabbitMqBatchPublisher Publisher, RabbitMqBatchConsumer Consumer) : IAsyncDisposable
    {
        internal static readonly byte[] Payload = Gzip();
        private static byte[] Gzip()
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
                gzip.Write(Encoding.UTF8.GetBytes("position,company_id=1 value=1i 123\n"));
            return buffer.ToArray();
        }
        internal async Task<Guid> Publish()
        {
            var id = Guid.NewGuid();
            var envelope = new TelemetryBatchEnvelope("1", id, new string('a', 32), "consumer-test", DateTimeOffset.UtcNow,
                "text/plain; charset=utf-8", "gzip", Payload.Length, Convert.ToHexStringLower(SHA256.HashData(Payload)), "v1");
            using var stream = new MemoryStream(Payload);
            await Publisher.PublishAsync(envelope, stream, default);
            return id;
        }
        internal async Task<BasicGetResult> GetDead()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var result = await Channel.BasicGetAsync(Topology.Options.DeadLetterQueue, true, timeout.Token);
                if (result is not null)
                    return result;
                await Task.Delay(20, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            await Publisher.DisposeAsync();
            await Channel.DisposeAsync();
            await Connections.DisposeAsync();
        }
    }
}
