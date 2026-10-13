using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Shouldly;
using Testcontainers.RabbitMq;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Queue;

[Trait("Category", "Integration")]
[Trait("Feature", "US1RabbitMq")]
public sealed class RabbitMqPublisherContractTests : IClassFixture<RabbitMqContractBroker>
{
    private readonly RabbitMqContractBroker broker;
    public RabbitMqPublisherContractTests(RabbitMqContractBroker broker) => this.broker = broker;
    private static readonly byte[] Payload = CreateGzip();

    [Fact]
    public async Task Broker_fixture_supports_native_bytes_persistence_and_tracked_confirms()
    {
        await using var connection = await broker.Connect();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        var queue = await channel.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true);
        await channel.BasicPublishAsync("", queue.QueueName, true, new BasicProperties { Persistent = true }, Payload);
        var delivery = await channel.BasicGetAsync(queue.QueueName, autoAck: true);
        delivery.ShouldNotBeNull();
        delivery.Body.ToArray().ShouldBe(Payload);
        delivery.BasicProperties.Persistent.ShouldBeTrue();
    }

    [Fact]
    public async Task Publisher_preserves_native_gzip_and_all_envelope_attributes_after_confirm()
    {
        await using var test = await CreatePublisher();
        var metadata = Envelope();
        using var body = new MemoryStream(Payload);
        var receipt = await test.Publisher.PublishAsync(metadata, body, CancellationToken.None);
        receipt.BatchId.Value.ShouldBe(metadata.BatchId);
        var delivery = await test.Channel.BasicGetAsync(test.Queue, autoAck: true);
        delivery.ShouldNotBeNull("The confirmed batch must be available in the configured durable queue");
        delivery.Body.ToArray().ShouldBe(Payload);
        delivery.BasicProperties.Persistent.ShouldBeTrue();
        delivery.BasicProperties.ContentType.ShouldBe("text/plain; charset=utf-8");
        delivery.BasicProperties.ContentEncoding.ShouldBe("gzip");
        var headers = delivery.BasicProperties.Headers!;
        foreach (var (key, expected) in new Dictionary<string, string>
        {
            ["schemaVersion"] = metadata.SchemaVersion,
            ["batchId"] = metadata.BatchId.ToString("D"),
            ["traceId"] = metadata.TraceId,
            ["producerId"] = metadata.ProducerId,
            ["contractVersion"] = "v1",
            ["contentType"] = metadata.ContentType,
            ["contentEncoding"] = metadata.ContentEncoding,
            ["payloadSha256"] = metadata.PayloadSha256
        })
        {
            HeaderText(headers[key]).ShouldBe(expected);
        }
        Convert.ToInt64(headers["compressedBytes"], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(Payload.Length);
        DateTimeOffset.Parse(HeaderText(headers["receivedAt"]), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(metadata.ReceivedAt);
        body.CanRead.ShouldBeTrue("The caller owns the spool stream");
    }

    [Fact]
    public async Task Production_topology_is_durable_nonexclusive_and_survives_publisher_connection_close()
    {
        await using var test = await CreatePublisher();
        using var body = new MemoryStream(Payload);
        await test.Publisher.PublishAsync(Envelope(), body, CancellationToken.None);
        foreach (var exchange in new[] { test.Exchange, test.RetryExchange, test.DeadExchange })
            await test.Channel.ExchangeDeclarePassiveAsync(exchange);
        foreach (var queue in new[] { test.Queue, test.RetryQueue, test.DeadQueue })
            await test.Channel.QueueDeclarePassiveAsync(queue);
        // Redeclaration is a broker-side equivalence check, not a test-created topology.
        await test.Channel.ExchangeDeclareAsync(test.Exchange, ExchangeType.Direct, durable: true, autoDelete: false);
        await test.Channel.QueueDeclareAsync(test.Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "classic",
                ["x-message-ttl"] = 604800000,
                ["x-dead-letter-exchange"] = test.DeadExchange
            });
        await test.Channel.ExchangeDeclareAsync(test.RetryExchange, ExchangeType.Direct, durable: true, autoDelete: false);
        await test.Channel.ExchangeDeclareAsync(test.DeadExchange, ExchangeType.Direct, durable: true, autoDelete: false);
        await test.Channel.QueueDeclareAsync(test.RetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "classic",
                ["x-message-ttl"] = 1000,
                ["x-dead-letter-exchange"] = test.Exchange,
                ["x-dead-letter-routing-key"] = test.RoutingKey
            });
        await test.Channel.QueueDeclareAsync(test.DeadQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "classic" });
        await test.Connection.CloseAsync();
        await using var verification = await broker.Connect();
        await using var channel = await verification.CreateChannelAsync();
        var delivery = await channel.BasicGetAsync(test.Queue, autoAck: true);
        delivery.ShouldNotBeNull();
        delivery.Body.ToArray().ShouldBe(Payload);
    }

    [Fact]
    public async Task Retry_and_dead_letter_bindings_preserve_original_body_bytes()
    {
        await using var test = await CreatePublisher();
        using (var initial = new MemoryStream(Payload))
            await test.Publisher.PublishAsync(Envelope(), initial, CancellationToken.None);
        (await test.Channel.BasicGetAsync(test.Queue, autoAck: true)).ShouldNotBeNull();
        await test.Channel.BasicPublishAsync(test.RetryExchange, test.RoutingKey, true,
            new BasicProperties { Persistent = true }, Payload);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var retried = await GetEventually(test.Channel, test.Queue, deadline.Token);
        retried.Body.ToArray().ShouldBe(Payload);
        await test.Channel.BasicNackAsync(retried.DeliveryTag, multiple: false, requeue: false);
        var dead = await GetEventually(test.Channel, test.DeadQueue, deadline.Token);
        dead.Body.ToArray().ShouldBe(Payload);
        await test.Channel.BasicAckAsync(dead.DeliveryTag, multiple: false);
    }

    private static async Task<BasicGetResult> GetEventually(IChannel channel, string queue, CancellationToken cancellationToken)
    {
        while (true)
        {
            var delivery = await channel.BasicGetAsync(queue, autoAck: false, cancellationToken);
            if (delivery is not null)
                return delivery;
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    [Fact]
    public async Task Unroutable_mandatory_publication_never_returns_a_durable_receipt()
    {
        await using var test = await CreatePublisher();
        using (var first = new MemoryStream(Payload))
            await test.Publisher.PublishAsync(Envelope(), first, CancellationToken.None);
        await test.Channel.QueueUnbindAsync(test.Queue, test.Exchange, test.RoutingKey);
        using var body = new MemoryStream(Payload);
        var failure = await Should.ThrowAsync<ProviderOperationException>(() => test.Publisher.PublishAsync(Envelope(), body, CancellationToken.None).AsTask());
        failure.ErrorCode.ShouldBe(TelemetryErrorCodes.DurabilityUnconfirmed);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("digest")]
    [InlineData("version")]
    public async Task Envelope_integrity_failures_are_rejected_without_a_provider_publish(string variant)
    {
        await using var test = await CreatePublisher();
        var metadata = Envelope();
        metadata = variant switch
        {
            "size" => metadata with { CompressedBytes = Payload.Length + 1 },
            "digest" => metadata with { PayloadSha256 = new string('0', 64) },
            _ => metadata with { SchemaVersion = "2" }
        };
        using var body = new MemoryStream(Payload);
        var failure = await Should.ThrowAsync<Exception>(() => test.Publisher.PublishAsync(metadata, body, CancellationToken.None).AsTask());
        (failure is ArgumentException or System.ComponentModel.DataAnnotations.ValidationException or ProviderOperationException)
            .ShouldBeTrue("Invalid envelopes must cause a validation/provider failure, not an unrelated programming exception");
        test.Interceptor.PublishCalls.ShouldBe(0, "Integrity must be checked before any AMQP publication");
    }

    [Theory]
    [InlineData("nack")]
    [InlineData("timeout")]
    public async Task Negative_or_missing_confirmation_maps_to_a_safe_failure(string fault)
    {
        await using var test = await CreatePublisher(fault);
        using var body = new MemoryStream(Payload);
        var failure = await Should.ThrowAsync<ProviderOperationException>(() => test.Publisher.PublishAsync(Envelope(), body, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        failure.ErrorCode.ShouldBe(TelemetryErrorCodes.DurabilityUnconfirmed);
        failure.Message.ShouldBe("A provider operation failed.");
        failure.InnerException.ShouldBeNull();
        if (fault == "timeout")
            failure.Kind.ShouldBe(ProviderFailureKind.Ambiguous);
    }

    [Fact]
    public async Task Receipt_waits_for_the_correlated_confirmation()
    {
        await using var test = await CreatePublisher("gate");
        using var body = new MemoryStream(Payload);
        var publication = test.Publisher.PublishAsync(Envelope(), body, CancellationToken.None).AsTask();
        await test.Interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        publication.IsCompleted.ShouldBeFalse("Sending bytes does not establish durable acceptance");
        test.Interceptor.Release.TrySetResult();
        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        test.Interceptor.Mandatory.ShouldBeTrue();
        test.Interceptor.Persistent.ShouldBeTrue();
    }

    private async Task<PublisherSession> CreatePublisher(string? fault = null)
    {
        var assembly = typeof(TelemetryControlDbContext).Assembly;
        var type = assembly.GetType("CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq.RabbitMqBatchPublisher");
        type.ShouldNotBeNull("T031 must provide RabbitMqBatchPublisher implementing IDurableBatchQueue");
        var connection = await broker.Connect();
        var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        var proxy = DispatchProxy.Create<IChannel, PublicationInterceptor>();
        var interceptor = (PublicationInterceptor)(object)proxy;
        interceptor.Inner = channel;
        interceptor.Fault = fault;
        var name = "t026." + Guid.NewGuid().ToString("N");
        var values = new Dictionary<string, string?>
        {
            ["Queue:RabbitMq:Exchange"] = name + ".exchange",
            ["Queue:RabbitMq:Queue"] = name + ".queue",
            ["Queue:RabbitMq:RoutingKey"] = name + ".route",
            ["Queue:RabbitMq:RetryExchange"] = name + ".retry.exchange",
            ["Queue:RabbitMq:RetryQueue"] = name + ".retry.queue",
            ["Queue:RabbitMq:DeadLetterExchange"] = name + ".dead.exchange",
            ["Queue:RabbitMq:DeadLetterQueue"] = name + ".dead.queue",
            ["Queue:RabbitMq:PublishTimeout"] = "00:00:00.500",
            ["Queue:RabbitMq:RetryDelay"] = "00:00:01",
            ["Telemetry:QueueRetention"] = "7.00:00:00"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration)
            .AddSingleton(connection).AddSingleton(proxy).BuildServiceProvider();
        try
        {
            var publisher = ActivatorUtilities.CreateInstance(services, type).ShouldBeAssignableTo<IDurableBatchQueue>();
            return new(broker, connection, channel, services, publisher, interceptor, values);
        }
        catch
        {
            await services.DisposeAsync();
            await channel.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string HeaderText(object? value) => value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
    private static TelemetryBatchEnvelope Envelope() => new("1", Guid.NewGuid(), "0123456789abcdef0123456789abcdef", "publisher-test", DateTimeOffset.UtcNow, "text/plain; charset=utf-8", "gzip", Payload.Length, Convert.ToHexStringLower(SHA256.HashData(Payload)), "v1");
    private static byte[] CreateGzip()
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes("telemetria,empresa_id=company,veiculo_id=vehicle latitude=1.0,longitude=2.0,velocidade=0.0,rpm=0i,ignicao=false 1791849600000\n"));
        return bytes.ToArray();
    }

    private sealed class PublisherSession(RabbitMqContractBroker broker, IConnection connection, IChannel channel, ServiceProvider services, IDurableBatchQueue publisher, PublicationInterceptor interceptor, Dictionary<string, string?> values) : IAsyncDisposable
    {
        internal IConnection Connection => connection;
        internal IChannel Channel => channel;
        internal IDurableBatchQueue Publisher => publisher;
        internal PublicationInterceptor Interceptor => interceptor;
        internal string Exchange => values["Queue:RabbitMq:Exchange"]!;
        internal string Queue => values["Queue:RabbitMq:Queue"]!;
        internal string RoutingKey => values["Queue:RabbitMq:RoutingKey"]!;
        internal string RetryExchange => values["Queue:RabbitMq:RetryExchange"]!;
        internal string RetryQueue => values["Queue:RabbitMq:RetryQueue"]!;
        internal string DeadExchange => values["Queue:RabbitMq:DeadLetterExchange"]!;
        internal string DeadQueue => values["Queue:RabbitMq:DeadLetterQueue"]!;
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cleanupConnection = await broker.Connect();
                foreach (var name in new[] { Queue, RetryQueue, DeadQueue, Exchange, RetryExchange, DeadExchange })
                {
                    await using var cleanup = await cleanupConnection.CreateChannelAsync();
                    try
                    {
                        if (name.EndsWith(".queue", StringComparison.Ordinal))
                            await cleanup.QueueDeleteAsync(name);
                        else
                            await cleanup.ExchangeDeleteAsync(name);
                    }
                    catch (RabbitMQ.Client.Exceptions.OperationInterruptedException error) when (error.ShutdownReason?.ReplyCode == 404)
                    {
                        // A failed topology test may leave only part of its isolated resources declared.
                    }
                }
            }
            finally
            {
                if (publisher is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
                else if (publisher is IDisposable disposable)
                    disposable.Dispose();
                await services.DisposeAsync();
                await channel.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }

    // Forward real channel calls; only the SDK confirmation completion is controlled in fault cases.
    public class PublicationInterceptor : DispatchProxy
    {
        internal IChannel Inner { get; set; } = null!;
        internal string? Fault
        {
            get; set;
        }
        internal int PublishCalls
        {
            get; private set;
        }
        internal bool Mandatory
        {
            get; private set;
        }
        internal bool Persistent
        {
            get; private set;
        }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "BasicPublishAsync")
            {
                PublishCalls++;
                Mandatory = (bool)args![2]!;
                Persistent = ((IReadOnlyBasicProperties)args[3]!).Persistent;
                if (Fault is not null)
                    return new ValueTask(ControlledPublish(targetMethod, args));
            }
            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
        private async Task ControlledPublish(MethodInfo method, object?[] args)
        {
            Entered.TrySetResult();
            if (Fault == "nack")
                throw new RabbitMQ.Client.Exceptions.PublishException(1, false);
            if (Fault == "timeout")
            {
                // The adapter must bound the confirm wait with its own cancellation token.
                await Task.Delay(Timeout.InfiniteTimeSpan, (CancellationToken)args[5]!).WaitAsync(TimeSpan.FromSeconds(5));
                return;
            }
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await (ValueTask)method.Invoke(Inner, args)!;
        }
    }
}

public sealed class RabbitMqContractBroker : IAsyncLifetime
{
    private RabbitMqContainer? container;
    private string? ConnectionUri
    {
        get; set;
    }
    public async Task InitializeAsync()
    {
        ConnectionUri = Environment.GetEnvironmentVariable("TELEMETRY_TEST_RABBITMQ");
        if (ConnectionUri is null)
        {
            container = new RabbitMqBuilder("rabbitmq:4.3.6-management-alpine").Build();
            await container.StartAsync();
            ConnectionUri = container.GetConnectionString();
        }
    }
    internal Task<IConnection> Connect() => new ConnectionFactory { Uri = new Uri(ConnectionUri!), AutomaticRecoveryEnabled = true }.CreateConnectionAsync();
    public async Task DisposeAsync()
    {
        if (container is not null)
            await container.DisposeAsync();
    }
}
