using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;
using CentralMobilidade.Telemetry.IntegrationTests.Influx;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using CentralMobilidade.Telemetry.IntegrationTests.Queue;
using CentralMobilidade.Telemetry.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Recovery;

[Trait("Category", "Integration")]
public sealed class WorkerHostTests(RabbitMqContractBroker broker, InfluxContractServer influx)
    : IClassFixture<RabbitMqContractBroker>, IClassFixture<InfluxContractServer>
{
    private readonly Errors errors = new();
    [Fact]
    public async Task Host_composition_processes_mixed_broker_delivery_with_real_storage_and_freshness()
    {
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        await using var test = await Create(connection: postgres.Connection);
        var store = test.Host.Services.GetRequiredService<IBatchStateStore>();
        var now = DateTimeOffset.UtcNow;
        var company = "worker-" + Guid.NewGuid().ToString("N");
        var raw = $"telemetria,empresa_id={company},veiculo_id=host-a latitude=1.0,longitude=2.0,velocidade=0.0,rpm=1i,ignicao=true {now.ToUnixTimeMilliseconds()}\n" +
            $"telemetria,empresa_id={company}-b,veiculo_id=host-b latitude=1.0,longitude=2.0,velocidade=0.0,rpm=1i,ignicao=true {now.ToUnixTimeMilliseconds()}\nPRIVATE_REJECTED_CONTENT\n";
        var id = await test.Publish(raw, store);
        await Eventually(async () => (await store.GetAsync(new(id), default))?.State == BatchLifecycle.CompletedWithRejections);
        var result = (await store.GetAsync(new(id), default))!;
        result.Counters.ShouldBe(new BatchCounters(2, 1, 0, 3));
        await Eventually(() => Task.FromResult(test.Progress.IsFresh(TimeSpan.FromSeconds(1))));
        var endpoint = test.Host.Services.GetServices<IHostedService>().OfType<WorkerMetricsEndpoint>().Single();
        using var client = new HttpClient();
        (await client.GetStringAsync(endpoint.ListenUrl + "/health/ready")).ShouldContain("healthy");
        var metrics = await client.GetStringAsync(endpoint.ListenUrl + "/metrics");
        metrics.ShouldContain("telemetry_queue_delivery_age_seconds_count");
        metrics.ShouldContain("telemetry_processing_duration_seconds_count");
        metrics.ShouldContain("telemetry_storage_write_duration_seconds_count");
        var query = Uri.EscapeDataString($"SELECT empresa_id FROM telemetria WHERE empresa_id = '{company}'");
        (await client.GetStringAsync(influx.Url + "/api/v3/query_sql?db=queryable&format=json&q=" + query)).ShouldContain(company);
        await test.Host.StopAsync();
        test.Progress.IsFresh(TimeSpan.FromSeconds(1)).ShouldBeFalse();
        (await test.Channel.BasicGetAsync(test.Topology.Options.Queue, true)).ShouldBeNull();
    }

    [Fact]
    public async Task Idle_subscription_stays_fresh_and_concurrency_is_bounded_under_backpressure()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var active = 0;
        var observedConcurrency = new ConcurrentBag<int>();
        var processor = new Processor(async (delivery, token) =>
        {
            var current = Interlocked.Increment(ref active);
            observedConcurrency.Add(current);
            try
            {
                await release.Task.WaitAsync(token);
                await delivery.CompleteAsync(token);
                Interlocked.Increment(ref count);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        await using var test = await Create(processor);
        await Eventually(() => Task.FromResult(test.Progress.IsFresh(TimeSpan.FromSeconds(1))));
        await Task.Delay(1250);
        test.Progress.IsFresh(TimeSpan.FromSeconds(1)).ShouldBeTrue("Idle heartbeat must follow a live subscription probe");
        for (var n = 0; n < 5; n++)
            await test.Publish();
        await Eventually(() => Task.FromResult(Volatile.Read(ref active) == 2));
        await Task.Delay(1250);
        observedConcurrency.Max().ShouldBe(2);
        test.Progress.IsFresh(TimeSpan.FromSeconds(1)).ShouldBeTrue("Backpressure must not disable subscription checks");
        release.TrySetResult();
        await Eventually(() => Task.FromResult(Volatile.Read(ref count) == 5));
    }

    [Fact]
    public async Task Graceful_stop_allows_inflight_completion_without_cancelling_early()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var processor = new Processor(async (delivery, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            cancelled = token.IsCancellationRequested;
            await delivery.CompleteAsync(token);
        });
        await using var test = await Create(processor, drain: "00:00:02");
        await test.Publish();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = test.Host.StopAsync();
        await Task.Delay(100);
        stopping.IsCompleted.ShouldBeFalse();
        test.Progress.IsFresh(TimeSpan.FromSeconds(1)).ShouldBeFalse();
        release.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(4));
        cancelled.ShouldBeFalse();
        (await test.Channel.BasicGetAsync(test.Topology.Options.Queue, true)).ShouldBeNull();
    }

    [Fact]
    public async Task Drain_deadline_cancels_work_and_requeues_original()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new Processor(async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally { cancelled.TrySetResult(); }
        });
        await using var test = await Create(processor, drain: "00:00:00.150");
        var id = await test.Publish();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await test.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
        cancelled.Task.IsCompleted.ShouldBeTrue();
        var original = await test.Channel.BasicGetAsync(test.Topology.Options.Queue, true);
        original.ShouldNotBeNull();
        original.BasicProperties.MessageId.ShouldBe(id.ToString("D"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_failure_retries_without_stopping_healthy_consumption(bool providerFailure)
    {
        var attempts = new ConcurrentQueue<int>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new Processor(async (delivery, token) =>
        {
            attempts.Enqueue(delivery.DeliveryAttempt);
            if (delivery.DeliveryAttempt == 1)
            {
                if (providerFailure)
                    throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Permanent);
                throw new IOException("PRIVATE_PROVIDER_DETAIL");
            }
            await delivery.CompleteAsync(token);
            completed.TrySetResult();
        });
        await using var test = await Create(processor);
        await test.Publish();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        attempts.ToArray().ShouldBe(new[] { 1, 2 });
    }

    [Fact]
    public async Task Invalid_request_is_isolated_without_blocking_the_next_delivery()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var test = await Create(new Processor(async (delivery, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new ProviderOperationException(TelemetryErrorCodes.RequestInvalid, ProviderFailureKind.Permanent);
            await delivery.CompleteAsync(token);
            completed.TrySetResult();
        }));
        await test.Publish();
        await Eventually(async () => (await test.Channel.QueueDeclarePassiveAsync(test.Topology.Options.DeadLetterQueue)).MessageCount == 1);
        await test.Publish();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var dead = (await test.Channel.BasicGetAsync(test.Topology.Options.DeadLetterQueue, true))!;
        Encoding.UTF8.GetString((byte[])dead.BasicProperties.Headers!["errorCode"]!).ShouldBe(TelemetryErrorCodes.RequestInvalid);
    }

    [Fact]
    public async Task Cancelled_subscription_is_recreated_by_recovery_loop()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var test = await Create(new Processor(async (delivery, token) => { await delivery.CompleteAsync(token); completed.TrySetResult(); }));
        await Eventually(async () => (await test.Channel.QueueDeclarePassiveAsync(test.Topology.Options.Queue)).ConsumerCount == 1);
        await test.Channel.QueueDeleteAsync(test.Topology.Options.Queue);
        // Redeclare on an independent channel; the worker must subscribe again, not just refresh a timer.
        await test.Topology.EnsureAsync(test.Channel);
        await Eventually(async () => (await test.Channel.QueueDeclarePassiveAsync(test.Topology.Options.Queue)).ConsumerCount == 1);
        await test.Publish();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("Worker:Concurrency", "0")]
    [InlineData("Worker:Concurrency", "65")]
    [InlineData("Worker:Concurrency", "5")]
    [InlineData("Worker:HeartbeatInterval", "00:00:02")]
    [InlineData("Queue:Provider", "PubSub")]
    [InlineData("ConnectionStrings:TelemetryControl", "")]
    public void Invalid_configuration_fails_before_consumption(string key, string value)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Settings());
        builder.Configuration[key] = value;
        Should.Throw<InvalidOperationException>(() => CentralMobilidade.Telemetry.Worker.Program.ConfigureServices(builder.Services, builder.Configuration));
    }

    [Theory]
    [InlineData("Processing:DiagnosticKey", "")]
    [InlineData("Processing:DiagnosticKey", "not-base64")]
    [InlineData("Processing:DiagnosticKey", "AQID")]
    [InlineData("InfluxDb:Queryable:Token", "")]
    [InlineData("Quarantine:Path", "")]
    public async Task Missing_required_adapter_configuration_fails_host_start(string key, string value)
    {
        var builder = Host.CreateApplicationBuilder();
        var settings = Settings();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Configuration[key] = value;
        CentralMobilidade.Telemetry.Worker.Program.ConfigureServices(builder.Services, builder.Configuration);
        var host = builder.Build();
        try
        {
            await Should.ThrowAsync<ArgumentException>(() => host.StartAsync());
        }
        finally
        {
            await ((IAsyncDisposable)host).DisposeAsync();
            var root = Path.GetFullPath(settings["Quarantine:Path"]!);
            Path.GetDirectoryName(root).ShouldBe(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
            Path.GetFileName(root).ShouldStartWith("centralmobilidade-t053-");
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private Dictionary<string, string?> Settings()
    {
        var prefix = "worker." + Guid.NewGuid().ToString("N");
        var credentials = broker.Endpoint.UserInfo.Split(':');
        var settings = new Dictionary<string, string?>
        {
            ["Queue:Provider"] = "RabbitMq",
            ["Queue:RabbitMq:Host"] = broker.Endpoint.Host,
            ["Queue:RabbitMq:VirtualHost"] = "/",
            ["Queue:RabbitMq:Port"] = broker.Endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Queue:RabbitMq:Username"] = Uri.UnescapeDataString(credentials[0]),
            ["Queue:RabbitMq:Password"] = Uri.UnescapeDataString(credentials[1]),
            ["Queue:RabbitMq:Prefetch"] = "4",
            ["Queue:RabbitMq:RetryDelay"] = "00:00:00.050",
            ["Queue:RabbitMq:MaxRetryDelay"] = "00:00:00.100",
            ["Queue:RabbitMq:DrainTimeout"] = "00:00:01",
            ["Worker:Concurrency"] = "2",
            ["Worker:HeartbeatInterval"] = "00:00:00.100",
            ["Worker:RecoveryDelay"] = "00:00:00.100",
            ["Health:WorkerMaxAge"] = "00:00:01",
            ["Observability:WorkerMetricsUrl"] = "http://127.0.0.1:0",
            ["ConnectionStrings:TelemetryControl"] = "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
            ["Processing:DiagnosticKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["InfluxDb:Queryable:Url"] = influx.Url,
            ["InfluxDb:Queryable:Database"] = "queryable",
            ["InfluxDb:Queryable:Token"] = "test-only-token",
            ["Quarantine:Path"] = Path.Combine(Path.GetTempPath(), "centralmobilidade-t053-" + Guid.NewGuid().ToString("N"))
        };
        foreach (var key in new[] { "Exchange", "Queue", "RetryExchange", "RetryQueue", "DeadLetterExchange", "DeadLetterQueue" })
            settings["Queue:RabbitMq:" + key] = prefix + "." + key;
        return settings;
    }

    private async Task<TestHost> Create(Processor? processor = null, string drain = "00:00:01", string? connection = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Settings());
        builder.Configuration["Queue:RabbitMq:DrainTimeout"] = drain;
        if (connection is not null)
            builder.Configuration["ConnectionStrings:TelemetryControl"] = connection;
        CentralMobilidade.Telemetry.Worker.Program.ConfigureServices(builder.Services, builder.Configuration);
        builder.Services.AddSingleton<ILogger<TelemetryProcessingWorker>>(errors);
        if (processor is not null)
        {
            builder.Services.RemoveAll<IWorkerBatchProcessor>();
            builder.Services.AddSingleton<IWorkerBatchProcessor>(processor);
        }
        var host = builder.Build();
        var connectionToBroker = await broker.Connect();
        var channel = await connectionToBroker.CreateChannelAsync(new CreateChannelOptions(true, true));
        var topology = new RabbitMqTopology(builder.Configuration);
        await topology.EnsureAsync(channel);
        var test = new TestHost(host, connectionToBroker, channel, topology, new(channel, builder.Configuration), builder.Configuration["Quarantine:Path"]!);
        try
        {
            await host.StartAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try
            {
                while (!test.Progress.IsFresh(TimeSpan.FromSeconds(1)))
                    await Task.Delay(20, deadline.Token);
            }
            catch (OperationCanceledException) { throw new InvalidOperationException("Consumer failed to start: " + string.Join("; ", errors.Values.Select(e => e.ToString()))); }
            return test;
        }
        catch { await test.DisposeAsync(); throw; }
    }

    private static async Task Eventually(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await predicate())
            await Task.Delay(20, timeout.Token);
    }

    private sealed class Processor(Func<IQueueDelivery, CancellationToken, ValueTask> process) : IWorkerBatchProcessor
    {
        public ValueTask ProcessAsync(IQueueDelivery delivery, CancellationToken token) => process(delivery, token);
    }

    private sealed class Errors : ILogger<TelemetryProcessingWorker>
    {
        internal ConcurrentQueue<Exception> Values { get; } = new();
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (error is not null)
                Values.Enqueue(error);
        }
    }

    private sealed record TestHost(IHost Host, RabbitMQ.Client.IConnection Connection, RabbitMQ.Client.IChannel Channel, RabbitMqTopology Topology, RabbitMqBatchPublisher Publisher, string DirectoryPath) : IAsyncDisposable
    {
        internal WorkerProgress Progress => Host.Services.GetRequiredService<WorkerProgress>();
        internal async Task<Guid> Publish(string raw = "invalid-private-line\n", IBatchStateStore? store = null)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
                gzip.Write(Encoding.UTF8.GetBytes(raw));
            var bytes = output.ToArray();
            var now = DateTimeOffset.UtcNow;
            var envelope = new TelemetryBatchEnvelope("1", Guid.NewGuid(), "0123456789abcdef0123456789abcdef", "worker-host-test", now, "text/plain; charset=utf-8", "gzip", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "v1");
            if (store is not null)
            {
                var created = await store.CreateAsync(envelope, now.AddDays(30), default);
                (await store.TryTransitionAsync(created.BatchId, created.Version, new(BatchLifecycle.Publishing, now), default)).ShouldBeTrue();
                (await store.TryTransitionAsync(created.BatchId, created.Version + 1, new(BatchLifecycle.Accepted, now), default)).ShouldBeTrue();
            }
            using var body = new MemoryStream(bytes);
            await Publisher.PublishAsync(envelope, body, default);
            return envelope.BatchId;
        }
        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            if (Host is IAsyncDisposable asyncHost)
                await asyncHost.DisposeAsync();
            else
                Host.Dispose();
            await Publisher.DisposeAsync();
            await Channel.DisposeAsync();
            await Connection.DisposeAsync();
            var resolved = Path.GetFullPath(DirectoryPath);
            Path.GetDirectoryName(resolved).ShouldBe(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
            Path.GetFileName(resolved).ShouldStartWith("centralmobilidade-t053-");
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }
}
