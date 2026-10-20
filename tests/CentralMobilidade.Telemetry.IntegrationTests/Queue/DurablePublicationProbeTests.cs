using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Infrastructure.DependencyInjection;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Shouldly;
using Testcontainers.RabbitMq;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Queue;

public sealed class DurablePublicationProbeTests : IAsyncLifetime
{
    private readonly RabbitMqContainer broker = new RabbitMqBuilder("rabbitmq:4.3.6-management-alpine").Build();
    public Task InitializeAsync() => broker.StartAsync();
    public Task DisposeAsync() => broker.DisposeAsync().AsTask();

    private IConfigurationRoot Config()
    {
        var endpoint = new ConnectionFactory { Uri = new Uri(broker.GetConnectionString()) };
        var prefix = "probe." + Guid.NewGuid().ToString("N");
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Queue:Provider"] = "RabbitMq",
            ["Queue:RabbitMq:Host"] = endpoint.HostName,
            ["Queue:RabbitMq:Port"] = endpoint.Port.ToString(),
            ["Queue:RabbitMq:Username"] = endpoint.UserName,
            ["Queue:RabbitMq:Password"] = endpoint.Password,
            ["Queue:RabbitMq:VirtualHost"] = endpoint.VirtualHost,
            ["Queue:RabbitMq:Exchange"] = prefix + ".exchange",
            ["Queue:RabbitMq:Queue"] = prefix + ".queue",
            ["Queue:RabbitMq:RetryExchange"] = prefix + ".retry.exchange",
            ["Queue:RabbitMq:RetryQueue"] = prefix + ".retry.queue",
            ["Queue:RabbitMq:DeadLetterExchange"] = prefix + ".dead.exchange",
            ["Queue:RabbitMq:DeadLetterQueue"] = prefix + ".dead.queue",
            ["Queue:RabbitMq:RoutingKey"] = "batch",
            ["Queue:RabbitMq:DrainTimeout"] = "00:00:00",
            ["Health:ProbeTimeout"] = "00:00:02"
        }).Build();
    }

    [Fact]
    public async Task Runtime_registration_publishes_a_persistent_control_to_the_actual_durable_queue()
    {
        var config = Config();
        var services = new ServiceCollection();
        services.AddTelemetryQueue(QueueRuntimeOptions.Read(config, "Testing"));
        await using var provider = services.BuildServiceProvider();
        var probe = provider.GetRequiredService<IDurablePublicationProbe>();
        (await probe.ProbeAsync(default)).Ready.ShouldBeTrue();
        await using var connection = await new ConnectionFactory { Uri = new Uri(broker.GetConnectionString()) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var topology = new RabbitMqTopology(config);
        // This would fail if the probe had only pinged management or published elsewhere.
        var control = await channel.BasicGetAsync(topology.Options.Queue, false);
        control.ShouldNotBeNull();
        PublicationProbeControl.IsControl(control.BasicProperties, control.Body.Span).ShouldBeTrue();
        control.BasicProperties.Persistent.ShouldBeTrue();
        control.BasicProperties.Headers!.Keys.ShouldAllBe(k => k == Constants.PublishSequenceNumberHeader);
        await channel.BasicAckAsync(control.DeliveryTag, false);
        (await channel.QueueDeclarePassiveAsync(topology.Options.Queue)).MessageCount.ShouldBe(0u);
    }

    [Fact]
    public async Task Concurrent_health_requests_share_one_five_second_observation()
    {
        var config = Config();
        var topology = new RabbitMqTopology(config);
        await using var manager = new RabbitMqConnectionManager(config, topology);
        var probe = new RabbitMqDurablePublicationProbe(manager, config);
        var states = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => probe.ProbeAsync(default).AsTask()));
        states.ShouldAllBe(s => s.Ready);
        await using var channel = await manager.CreatePublishChannelAsync();
        (await channel.QueueDeclarePassiveAsync(topology.Options.Queue)).MessageCount.ShouldBe(1u);
    }

    [Fact]
    public async Task Write_denial_fails_even_when_connection_and_topology_are_permitted()
    {
        var config = Config();
        var topology = new RabbitMqTopology(config);
        await using (var owner = new RabbitMqConnectionManager(config, topology))
        await using (var channel = await owner.CreatePublishChannelAsync())
        {
        }
        var user = "p" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        (await broker.ExecAsync(["rabbitmqctl", "add_user", user, password])).ExitCode.ShouldBe(0);
        var prefix = config["Queue:RabbitMq:Queue"]![..^6];
        var write = "^" + Regex.Escape(prefix) + "\\.(queue|retry\\.queue|dead\\.queue)$";
        (await broker.ExecAsync(["rabbitmqctl", "set_permissions", "-p", "/", user, ".*", write, ".*"])).ExitCode.ShouldBe(0);
        config["Queue:RabbitMq:Username"] = user;
        config["Queue:RabbitMq:Password"] = password;
        await using var manager = new RabbitMqConnectionManager(config, topology);
        await using (var channel = await manager.CreatePublishChannelAsync())
        {
        }
        (await new RabbitMqDurablePublicationProbe(manager, config).ProbeAsync(default)).Ready.ShouldBeFalse();
    }

    [Fact]
    public async Task Incompatible_exclusive_nondurable_queue_fails_and_recovers_after_correction()
    {
        var config = Config();
        await using var connection = await new ConnectionFactory { Uri = new Uri(broker.GetConnectionString()) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(config["Queue:RabbitMq:Queue"]!, false, true, false);
        await using var manager = new RabbitMqConnectionManager(config, new(config));
        var probe = new RabbitMqDurablePublicationProbe(manager, config);
        (await probe.ProbeAsync(default)).Ready.ShouldBeFalse();
        await channel.QueueDeleteAsync(config["Queue:RabbitMq:Queue"]!);
        await Task.Delay(5200);
        (await probe.ProbeAsync(default)).Ready.ShouldBeTrue();
    }

    [Fact]
    public async Task Unreachable_broker_and_cancellation_do_not_report_success()
    {
        var config = Config();
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        config["Queue:RabbitMq:Port"] = port.ToString();
        await using var manager = new RabbitMqConnectionManager(config, new(config));
        var probe = new RabbitMqDurablePublicationProbe(manager, config);
        (await probe.ProbeAsync(default)).Ready.ShouldBeFalse();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await probe.ProbeAsync(cancelled.Token));
    }

    [Fact]
    public async Task Resource_alarm_withholding_confirmation_fails_within_health_deadline()
    {
        var config = Config();
        await using var manager = new RabbitMqConnectionManager(config, new(config));
        await using (var channel = await manager.CreatePublishChannelAsync())
        {
        }
        try
        {
            (await broker.ExecAsync(["rabbitmqctl", "set_vm_memory_high_watermark", "0.000001"])).ExitCode.ShouldBe(0);
            await Task.Delay(1000);
            var elapsed = Stopwatch.StartNew();
            (await new RabbitMqDurablePublicationProbe(manager, config).ProbeAsync(default)).Ready.ShouldBeFalse();
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            (await broker.ExecAsync(["rabbitmqctl", "set_vm_memory_high_watermark", "0.4"])).ExitCode.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Consumer_ACKs_controls_without_business_delivery_and_preserves_following_batch()
    {
        var config = Config();
        var topology = new RabbitMqTopology(config);
        await using var manager = new RabbitMqConnectionManager(config, topology);
        (await new RabbitMqDurablePublicationProbe(manager, config).ProbeAsync(default)).Ready.ShouldBeTrue();
        using var payload = new MemoryStream();
        using (var gzip = new GZipStream(payload, CompressionMode.Compress, true))
            gzip.Write("telemetria,empresa_id=x,veiculo_id=y latitude=1,longitude=2,velocidade=3,rpm=4i,ignicao=true 1791686400000\n"u8);
        var bytes = payload.ToArray();
        var id = Guid.NewGuid();
        var envelope = new TelemetryBatchEnvelope("1", id, new string('a', 32), "test", DateTimeOffset.UtcNow,
            "text/plain; charset=utf-8", "gzip", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "v1");
        await using var publisher = await RabbitMqBatchPublisher.CreateAsync(manager, config);
        await publisher.PublishAsync(envelope, new MemoryStream(bytes), default);
        var consumer = new RabbitMqBatchConsumer(manager, config);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var iterator = consumer.ConsumeAsync(timeout.Token).GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).ShouldBeTrue();
        iterator.Current.Metadata.BatchId.ShouldBe(id);
        await iterator.Current.CompleteAsync(default);
        await iterator.Current.DisposeAsync();
        await using var channel = await manager.CreatePublishChannelAsync();
        (await channel.QueueDeclarePassiveAsync(topology.Options.Queue)).MessageCount.ShouldBe(0u);
        (await channel.QueueDeclarePassiveAsync(topology.Options.DeadLetterQueue)).MessageCount.ShouldBe(0u);
    }

    [Fact]
    public void Business_or_malformed_messages_are_never_misclassified_as_controls()
    {
        var props = PublicationProbeControl.Properties();
        PublicationProbeControl.IsControl(props, "not a probe"u8).ShouldBeFalse();
        props.Headers = new Dictionary<string, object?> { ["batchId"] = Guid.NewGuid().ToString() };
        PublicationProbeControl.IsControl(props, PublicationProbeControl.Body.Span).ShouldBeFalse();
        props = PublicationProbeControl.Properties();
        props.Persistent = false;
        PublicationProbeControl.IsControl(props, PublicationProbeControl.Body.Span).ShouldBeFalse();
    }
}
