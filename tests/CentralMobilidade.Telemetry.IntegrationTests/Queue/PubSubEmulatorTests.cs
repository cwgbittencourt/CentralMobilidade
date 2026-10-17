using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.ContractTests.Queue;
using CentralMobilidade.Telemetry.Infrastructure.Queue.PubSub;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Queue;

[CollectionDefinition("PubSub emulator")]
public sealed class PubSubEmulatorCollection : ICollectionFixture<PubSubEmulatorFixture>
{
}

[Collection("PubSub emulator")]
[Trait("Category", "Integration")]
public sealed class PubSubEmulatorTests(PubSubEmulatorFixture emulator) : DurableBatchQueueContractTests
{
    protected override async Task<DurableQueueContractHarness> CreateAsync(QueuePublishFault fault = QueuePublishFault.None)
        => await CreateAdapterAsync(fault);

    private async Task<PubSubAdapterHarness> CreateAdapterAsync(QueuePublishFault fault = QueuePublishFault.None, StatusCode? status = null)
    {
        var type = typeof(PubSubBatchQueue);
        var resources = await emulator.CreateResourcesAsync();
        try
        {
            var publisher = new ControlledPublisher(emulator.Publisher, fault, status);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Queue:Provider"] = "PubSub",
                ["Queue:PubSub:ProjectId"] = emulator.Project,
                ["Queue:PubSub:Endpoint"] = emulator.Endpoint,
                ["Queue:PubSub:UseEmulator"] = "true",
                ["Queue:PubSub:Topic"] = resources.Topic,
                ["Queue:PubSub:Subscription"] = resources.Subscription,
                ["Queue:PubSub:DeadLetterTopic"] = resources.DeadTopic,
                ["Queue:PubSub:DeadLetterSubscription"] = resources.DeadSubscription,
                ["Queue:PubSub:PublishTimeout"] = fault == QueuePublishFault.HoldConfirmation ? "00:00:05" : "00:00:00.500",
                ["Queue:PubSub:PullTimeout"] = "00:00:01",
                ["Queue:PubSub:MaxAttempts"] = "10",
                ["Queue:PubSub:DrainTimeout"] = "00:00:00"
            }).Build();
            // Constructor injection is a test seam, not T081 provider registration.
            var services = new ServiceCollection().AddSingleton<IConfiguration>(config)
                .AddSingleton<PublisherServiceApiClient>(publisher).AddSingleton(emulator.Subscriber).BuildServiceProvider();
            try
            {
                var adapter = (IDurableBatchQueue)ActivatorUtilities.CreateInstance(services, type);
                return new(adapter, resources, publisher, services);
            }
            catch { await services.DisposeAsync(); throw; }
        }
        catch { await resources.DisposeAsync(); throw; }
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied, ProviderFailureKind.Permanent)]
    [InlineData(StatusCode.InvalidArgument, ProviderFailureKind.Permanent)]
    [InlineData(StatusCode.NotFound, ProviderFailureKind.Permanent)]
    [InlineData(StatusCode.Unavailable, ProviderFailureKind.Transient)]
    [InlineData(StatusCode.ResourceExhausted, ProviderFailureKind.Transient)]
    [InlineData(StatusCode.DeadlineExceeded, ProviderFailureKind.Ambiguous)]
    public async Task Grpc_publish_failures_are_mapped_without_leaking_provider_diagnostics(StatusCode status, ProviderFailureKind expected)
    {
        await using var harness = await CreateAdapterAsync(status: status);
        using var body = new MemoryStream(PubSubEmulatorFixture.Gzip);
        var metadata = new CentralMobilidade.Telemetry.Contracts.Messaging.V1.TelemetryBatchEnvelope("1", Guid.NewGuid(), new string('a', 32), "t077", DateTimeOffset.UtcNow,
            "text/plain; charset=utf-8", "gzip", body.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(PubSubEmulatorFixture.Gzip)), "v1");
        var failure = await Should.ThrowAsync<ProviderOperationException>(() => harness.Publisher.PublishAsync(metadata, body, default).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        failure.Kind.ShouldBe(expected);
        failure.ErrorCode.ShouldBe(CentralMobilidade.Telemetry.Contracts.Errors.TelemetryErrorCodes.DurabilityUnconfirmed);
        failure.ToString().ShouldNotContain("synthetic-provider-secret");
    }

    private sealed class PubSubAdapterHarness(IDurableBatchQueue adapter, PubSubResources resources, ControlledPublisher publisher, ServiceProvider services) : DurableQueueContractHarness
    {
        public override IDurableBatchQueue Publisher => adapter;
        public override IAsyncEnumerable<IQueueDelivery> ConsumeAsync(CancellationToken token) => adapter.ConsumeAsync(token);
        public override Task PublishEntered => publisher.Entered.Task;
        public override Task ConfirmAsync()
        {
            publisher.Release.TrySetResult();
            return Task.CompletedTask;
        }
        public override async Task<NativeQueueContractMessage?> ReadNativeAsync(bool deadLetter, bool wait)
        {
            var subscription = deadLetter ? resources.DeadSubscription : resources.Subscription;
            var native = await resources.PullAsync(subscription, wait);
            if (native is null)
                return null;
            await resources.AcknowledgeAsync(subscription, native.AckId);
            return new(native.Message.Data.ToByteArray(), native.Message.Attributes.ToDictionary(p => p.Key, p => p.Value));
        }
        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (adapter is IAsyncDisposable disposable)
                    await disposable.DisposeAsync();
                else if (adapter is IDisposable sync)
                    sync.Dispose();
            }
            finally { await services.DisposeAsync(); await resources.DisposeAsync(); }
        }
    }

    private sealed class ControlledPublisher(PublisherServiceApiClient inner, QueuePublishFault fault, StatusCode? status) : PublisherServiceApiClient
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<PublishResponse> PublishAsync(PublishRequest request, CallSettings? callSettings = null)
        {
            Entered.TrySetResult();
            if (status is not null || fault == QueuePublishFault.NegativeConfirmation)
                throw new RpcException(new Status(status ?? StatusCode.Unavailable, "synthetic-provider-secret"));
            if (fault == QueuePublishFault.MissingConfirmation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, callSettings?.CancellationToken ?? default);
                throw new InvalidOperationException("Unreachable confirmation path.");
            }
            var response = await inner.PublishAsync(request, callSettings);
            if (fault == QueuePublishFault.HoldConfirmation)
                await Release.Task.WaitAsync(callSettings?.CancellationToken ?? default);
            return response;
        }
    }
}

[Collection("PubSub emulator")]
[Trait("Category", "Integration")]
public sealed class PubSubEmulatorControlTests(PubSubEmulatorFixture emulator)
{
    [Fact]
    public async Task Sdk_native_bytes_attributes_and_confirmation_work_in_this_session()
    {
        await using var resources = await emulator.CreateResourcesAsync();
        var message = new PubsubMessage { Data = ByteString.CopyFrom(PubSubEmulatorFixture.Gzip), Attributes = { ["traceId"] = new string('a', 32), ["batchId"] = Guid.NewGuid().ToString("D") } };
        var confirmation = await emulator.Publisher.PublishAsync(new PublishRequest { Topic = resources.Topic, Messages = { message } }, PubSubEmulatorFixture.RpcDeadline());
        confirmation.MessageIds.Count.ShouldBe(1);
        var delivery = (await resources.PullAsync(resources.Subscription, true)).ShouldNotBeNull();
        delivery.Message.MessageId.ShouldBe(confirmation.MessageIds[0]);
        delivery.Message.Data.ToByteArray().ShouldBe(PubSubEmulatorFixture.Gzip);
        delivery.Message.Attributes.ShouldBe(message.Attributes);
        await resources.AcknowledgeAsync(resources.Subscription, delivery.AckId);
    }

    [Fact]
    public async Task Sdk_nack_redelivers_until_explicit_acknowledgement()
    {
        await using var resources = await emulator.CreateResourcesAsync();
        await emulator.Publisher.PublishAsync(new PublishRequest { Topic = resources.Topic, Messages = { new PubsubMessage { Data = ByteString.CopyFrom(PubSubEmulatorFixture.Gzip) } } }, PubSubEmulatorFixture.RpcDeadline());
        var first = (await resources.PullAsync(resources.Subscription, true)).ShouldNotBeNull();
        await emulator.Subscriber.ModifyAckDeadlineAsync(new ModifyAckDeadlineRequest { Subscription = resources.Subscription, AckIds = { first.AckId }, AckDeadlineSeconds = 0 }, PubSubEmulatorFixture.RpcDeadline());
        var second = (await resources.PullAsync(resources.Subscription, true)).ShouldNotBeNull();
        second.Message.MessageId.ShouldBe(first.Message.MessageId);
        second.Message.Data.ShouldBe(first.Message.Data);
        await resources.AcknowledgeAsync(resources.Subscription, second.AckId);
        (await resources.PullAsync(resources.Subscription, false)).ShouldBeNull();
    }

    [Fact]
    public async Task Sdk_missing_topic_returns_a_real_not_found_status()
    {
        var error = await Should.ThrowAsync<RpcException>(() => emulator.Publisher.PublishAsync(new PublishRequest
        {
            Topic = $"projects/{emulator.Project}/topics/missing-{Guid.NewGuid():N}",
            Messages = { new PubsubMessage { Data = ByteString.CopyFromUtf8("probe") } }
        }, PubSubEmulatorFixture.RpcDeadline()));
        error.StatusCode.ShouldBe(StatusCode.NotFound);
    }

    [Fact]
    public async Task Restarting_an_emulator_session_loses_resources_and_cannot_prove_durability()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var topic = $"projects/{emulator.Project}/topics/session-{suffix}";
        var subscription = $"projects/{emulator.Project}/subscriptions/session-{suffix}";
        await emulator.Publisher.CreateTopicAsync(new Topic { Name = topic }, PubSubEmulatorFixture.RpcDeadline());
        await emulator.Subscriber.CreateSubscriptionAsync(new Subscription { Name = subscription, Topic = topic, AckDeadlineSeconds = 10 }, PubSubEmulatorFixture.RpcDeadline());
        await emulator.Publisher.PublishAsync(new PublishRequest { Topic = topic, Messages = { new PubsubMessage { Data = ByteString.CopyFromUtf8("session-only") } } }, PubSubEmulatorFixture.RpcDeadline());
        var pending = await emulator.Subscriber.PullAsync(new PullRequest { Subscription = subscription, MaxMessages = 1 }, PubSubEmulatorFixture.RpcDeadline());
        pending.ReceivedMessages.Single().Message.Data.ToStringUtf8().ShouldBe("session-only");
        await emulator.RestartAsync();
        var error = await Should.ThrowAsync<RpcException>(() => emulator.Publisher.GetTopicAsync(new GetTopicRequest { Topic = topic }, PubSubEmulatorFixture.RpcDeadline()));
        error.StatusCode.ShouldBe(StatusCode.NotFound);
        (await Should.ThrowAsync<RpcException>(() => emulator.Subscriber.GetSubscriptionAsync(new GetSubscriptionRequest { Subscription = subscription }, PubSubEmulatorFixture.RpcDeadline()))).StatusCode.ShouldBe(StatusCode.NotFound);
    }
}

public sealed class PubSubEmulatorFixture : IAsyncLifetime
{
    private IContainer? container;
    private GrpcChannel? channel;
    internal string Project { get; } = "t077-" + Guid.NewGuid().ToString("N")[..12];
    internal string Endpoint { get; private set; } = "";
    internal PublisherServiceApiClient Publisher { get; private set; } = null!;
    internal SubscriberServiceApiClient Subscriber { get; private set; } = null!;
    internal static readonly byte[] Gzip = Compress();
    internal static CallSettings RpcDeadline() => CallSettings.FromExpiration(Google.Api.Gax.Expiration.FromTimeout(TimeSpan.FromSeconds(10)));
    public async Task InitializeAsync()
    {
        container = new ContainerBuilder("gcr.io/google.com/cloudsdktool/google-cloud-cli:588.0.0-emulators")
            .WithCommand("gcloud", "beta", "emulators", "pubsub", "start", "--host-port=0.0.0.0:8085", "--project=" + Project, "--quiet")
            .WithPortBinding(8085, true).WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8085)).Build();
        await container.StartAsync();
        Connect();
    }
    internal async Task<PubSubResources> CreateResourcesAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var resources = new PubSubResources(this, $"projects/{Project}/topics/main-{suffix}", $"projects/{Project}/subscriptions/main-{suffix}",
            $"projects/{Project}/topics/dead-{suffix}", $"projects/{Project}/subscriptions/dead-{suffix}");
        try
        {
            foreach (var (topic, subscription) in new[] { (resources.Topic, resources.Subscription), (resources.DeadTopic, resources.DeadSubscription) })
            {
                await Publisher.CreateTopicAsync(new Topic { Name = topic }, RpcDeadline());
                await Subscriber.CreateSubscriptionAsync(new Subscription { Name = subscription, Topic = topic, AckDeadlineSeconds = 10 }, RpcDeadline());
            }
            return resources;
        }
        catch { await resources.DisposeAsync(); throw; }
    }
    private void Connect()
    {
        Endpoint = $"{container!.Hostname}:{container.GetMappedPublicPort(8085)}";
        // Own a local HTTP/2 channel: no ambient credentials, global pool or cloud fallback.
        channel = GrpcChannel.ForAddress("http://" + Endpoint);
        Publisher = new PublisherServiceApiClientBuilder { CallInvoker = channel.CreateCallInvoker() }.Build();
        Subscriber = new SubscriberServiceApiClientBuilder { CallInvoker = channel.CreateCallInvoker() }.Build();
    }
    internal async Task RestartAsync()
    {
        channel!.Dispose();
        await container!.StopAsync();
        await container.StartAsync();
        Connect(); // Docker can assign a new mapped host port after restart.
    }
    public async Task DisposeAsync()
    {
        channel?.Dispose();
        if (container is not null)
            await container.DisposeAsync();
    }
    private static byte[] Compress()
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, true))
            gzip.Write(System.Text.Encoding.UTF8.GetBytes("telemetria,empresa_id=company,veiculo_id=vehicle latitude=1.0,longitude=2.0,velocidade=3.0,rpm=4i,ignicao=true 1720000000000\n"));
        return bytes.ToArray();
    }
}

internal sealed record PubSubResources(PubSubEmulatorFixture Fixture, string Topic, string Subscription, string DeadTopic, string DeadSubscription) : IAsyncDisposable
{
    internal async Task<ReceivedMessage?> PullAsync(string subscription, bool wait)
    {
        using var deadline = new CancellationTokenSource(wait ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(500));
        try
        {
            while (true)
            {
                var response = await Fixture.Subscriber.PullAsync(new PullRequest { Subscription = subscription, MaxMessages = 1 }, CallSettings.FromCancellationToken(deadline.Token));
                if (response.ReceivedMessages.Count != 0)
                    return response.ReceivedMessages.Single();
                if (!wait)
                    return null;
                await Task.Delay(20, deadline.Token);
            }
        }
        catch (RpcException error) when (!wait && deadline.IsCancellationRequested && error.StatusCode is StatusCode.Cancelled or StatusCode.DeadlineExceeded) { return null; }
        catch (OperationCanceledException) when (!wait && deadline.IsCancellationRequested) { return null; }
    }
    internal Task AcknowledgeAsync(string subscription, string ack) => Fixture.Subscriber.AcknowledgeAsync(new AcknowledgeRequest { Subscription = subscription, AckIds = { ack } }, PubSubEmulatorFixture.RpcDeadline());
    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in new[] { Subscription, DeadSubscription })
            try
            {
                await Fixture.Subscriber.DeleteSubscriptionAsync(new DeleteSubscriptionRequest { Subscription = subscription }, PubSubEmulatorFixture.RpcDeadline());
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.NotFound) { }
        foreach (var topic in new[] { Topic, DeadTopic })
            try
            {
                await Fixture.Publisher.DeleteTopicAsync(new DeleteTopicRequest { Topic = topic }, PubSubEmulatorFixture.RpcDeadline());
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.NotFound) { }
    }
}
