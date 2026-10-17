using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Encoding = System.Text.Encoding;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.DependencyInjection;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Infrastructure.Queue;
using CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;
using CentralMobilidade.Telemetry.IntegrationTests.Influx;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using CentralMobilidade.Telemetry.Worker;
using Google.Cloud.PubSub.V1;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Queue;

[Collection("PubSub emulator")]
public sealed class QueueProviderHostTests(PubSubEmulatorFixture emulator, InfluxContractServer influx) : IClassFixture<InfluxContractServer>
{
    [Fact]
    public async Task Configured_api_and_worker_process_a_mixed_batch_through_pubsub_and_real_storage()
    {
        await using var resources = await emulator.CreateResourcesAsync();
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        var settings = Settings(resources, postgres.Connection);
        var token = OpaqueBearerTokens.Generate(BearerCredentialKind.Producer);
        var hash = BearerTokenHash.Create(token);
        settings["Authentication:Producers:0:CredentialId"] = "t081";
        settings["Authentication:Producers:0:ProducerId"] = "provider-host-test";
        settings["Authentication:Producers:0:Enabled"] = "true";
        settings["Authentication:Producers:0:Hash:LookupDigest"] = hash.LookupDigest;
        settings["Authentication:Producers:0:Hash:Salt"] = hash.Salt;
        settings["Authentication:Producers:0:Hash:Verifier"] = hash.Verifier;
        settings["Authentication:Producers:0:Hash:Iterations"] = hash.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var worker = await StartWorker(settings);
        await using var api = new Fixtures.ConfiguredApiFactory(settings);
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Contract-Version", "v1");
        var company = "t081-" + Guid.NewGuid().ToString("N");
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var raw = $"telemetria,empresa_id={company},veiculo_id=host-a latitude=1.0,longitude=2.0,velocidade=0.0,rpm=1i,ignicao=true {time}\n" +
            $"telemetria,empresa_id={company}-b,veiculo_id=host-b latitude=1.0,longitude=2.0,velocidade=0.0,rpm=1i,ignicao=true {time}\nPRIVATE_REJECTED_CONTENT\n";
        using var content = new ByteArrayContent(Compress(raw));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain; charset=utf-8");
        content.Headers.ContentEncoding.Add("gzip");
        using var response = await client.PostAsync("/api/v1/telemetry/batches", content);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var batch = new BatchId(accepted.RootElement.GetProperty("batchId").GetGuid());
        var store = worker.Services.GetRequiredService<IBatchStateStore>();
        await Eventually(async () => (await store.GetAsync(batch, default))?.State == BatchLifecycle.CompletedWithRejections);
        (await store.GetAsync(batch, default))!.Counters.ShouldBe(new BatchCounters(2, 1, 0, 3));
        (await client.GetAsync("/api/v1/telemetry/batches/" + batch)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var sql = new HttpClient();
        var query = Uri.EscapeDataString($"SELECT count(*) AS total FROM telemetria WHERE empresa_id IN ('{company}','{company}-b')");
        using var stored = JsonDocument.Parse(await sql.GetStringAsync(influx.Url + "/api/v3/query_sql?db=queryable&format=json&q=" + query));
        stored.RootElement[0].GetProperty("total").GetInt32().ShouldBe(2);
        await Eventually(() => Task.FromResult(worker.Services.GetRequiredService<WorkerProgress>().IsFresh(TimeSpan.FromSeconds(1))));
        var endpoint = worker.Services.GetServices<IHostedService>().OfType<WorkerMetricsEndpoint>().Single();
        (await sql.GetAsync(endpoint.ListenUrl + "/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        api.Services.GetService<RabbitMqConnectionManager>().ShouldBeNull();
        worker.Services.GetService<RabbitMqConnectionManager>().ShouldBeNull();
        await worker.StopAsync();
        (await resources.PullAsync(resources.Subscription, false)).ShouldBeNull();
    }

    [Theory]
    [InlineData("missing-topic")]
    [InlineData("wrong-binding")]
    [InlineData("missing-dead-subscription")]
    public async Task Pubsub_startup_rejects_missing_or_misbound_resources_without_fallback(string fault)
    {
        await using var resources = await emulator.CreateResourcesAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = "Testing";
        var settings = Settings(resources, "unused");
        if (fault == "missing-topic")
            settings["Queue:PubSub:Topic"] = $"projects/{emulator.Project}/topics/missing-{Guid.NewGuid():N}";
        if (fault == "wrong-binding")
            settings["Queue:PubSub:Subscription"] = resources.DeadSubscription;
        if (fault == "wrong-binding")
            settings["Queue:PubSub:DeadLetterSubscription"] = resources.Subscription;
        if (fault == "missing-dead-subscription")
            settings["Queue:PubSub:DeadLetterSubscription"] = $"projects/{emulator.Project}/subscriptions/missing-{Guid.NewGuid():N}";
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddTelemetryQueue(builder.Configuration, builder.Environment);
        await using var host = (IAsyncDisposable)builder.Build();
        var error = await Should.ThrowAsync<InvalidOperationException>(() => ((IHost)host).StartAsync());
        error.Message.ShouldBe("Queue provider startup validation failed.");
        error.InnerException.ShouldBeNull();
        ((IHost)host).Services.GetService<RabbitMqConnectionManager>().ShouldBeNull();
    }

    [Fact]
    public async Task Worker_pubsub_concurrency_is_bounded_and_idle_and_backpressured_consumer_remains_fresh()
    {
        await using var resources = await emulator.CreateResourcesAsync();
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var two = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var count = 0;
        var max = 0;
        var processor = new ProbeProcessor(async (delivery, token) =>
        {
            var current = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref max, Math.Max(max, current));
            if (current == 2)
                two.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                await delivery.CompleteAsync(token);
                Interlocked.Increment(ref count);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        await using var host = await StartWorker(Settings(resources, postgres.Connection), processor);
        var progress = host.Services.GetRequiredService<WorkerProgress>();
        await Eventually(() => Task.FromResult(progress.IsFresh(TimeSpan.FromSeconds(1))));
        for (var i = 0; i < 5; i++)
            await Publish(host.Services);
        await two.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Task.Delay(1250);
            max.ShouldBe(2);
            progress.IsFresh(TimeSpan.FromSeconds(1)).ShouldBeTrue();
        }
        finally { release.TrySetResult(); }
        await Eventually(() => Task.FromResult(Volatile.Read(ref count) == 5));
        await host.StopAsync();
        progress.IsFresh(TimeSpan.FromSeconds(1)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Worker_shutdown_drains_or_releases_original_without_early_ack(bool complete)
    {
        await using var resources = await emulator.CreateResourcesAsync();
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = Settings(resources, postgres.Connection);
        settings["Queue:PubSub:DrainTimeout"] = "00:00:01";
        var cancelled = false;
        var processor = new ProbeProcessor(async (delivery, token) =>
        {
            entered.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                await delivery.CompleteAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; throw; }
        });
        await using var host = await StartWorker(settings, processor);
        await Publish(host.Services);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = host.StopAsync();
        if (complete)
            release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.ShouldBe(!complete);
        var native = await resources.PullAsync(resources.Subscription, !complete);
        if (complete)
            native.ShouldBeNull();
        else
        {
            native.ShouldNotBeNull();
            native.Message.Data.ToByteArray().ShouldBe(PubSubEmulatorFixture.Gzip);
            await resources.AcknowledgeAsync(resources.Subscription, native.AckId);
        }
    }

    [Fact]
    public async Task Worker_loses_freshness_when_subscription_disappears_and_recovers_after_recreation()
    {
        await using var resources = await emulator.CreateResourcesAsync();
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        await using var host = await StartWorker(Settings(resources, postgres.Connection), new ProbeProcessor((delivery, token) => delivery.CompleteAsync(token)));
        var progress = host.Services.GetRequiredService<WorkerProgress>();
        await Eventually(() => Task.FromResult(progress.IsFresh(TimeSpan.FromSeconds(1))));
        await emulator.Subscriber.DeleteSubscriptionAsync(new DeleteSubscriptionRequest { Subscription = resources.Subscription }, PubSubEmulatorFixture.RpcDeadline());
        await Eventually(() => Task.FromResult(!progress.IsFresh(TimeSpan.FromSeconds(1))));
        await emulator.Subscriber.CreateSubscriptionAsync(new Subscription { Name = resources.Subscription, Topic = resources.Topic, AckDeadlineSeconds = 10 }, PubSubEmulatorFixture.RpcDeadline());
        await Eventually(() => Task.FromResult(progress.IsFresh(TimeSpan.FromSeconds(1))));
        await host.StopAsync();
    }

    [Fact]
    public async Task Registered_transport_delivers_native_gzip_above_the_grpc_four_mib_default()
    {
        await using var resources = await emulator.CreateResourcesAsync();
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true))
            gzip.Write(RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
        var bytes = compressed.ToArray();
        bytes.Length.ShouldBeGreaterThan(4 * 1024 * 1024);
        var expected = SHA256.HashData(bytes);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new ProbeProcessor(async (delivery, token) =>
        {
            using var received = new MemoryStream();
            await delivery.Body.CopyToAsync(received, token);
            SHA256.HashData(received.ToArray()).ShouldBe(expected);
            await delivery.CompleteAsync(token);
            done.TrySetResult();
        });
        await using var host = await StartWorker(Settings(resources, postgres.Connection), processor);
        using var body = new MemoryStream(bytes);
        var metadata = new TelemetryBatchEnvelope("1", Guid.NewGuid(), new string('a', 32), "large-transport-probe", DateTimeOffset.UtcNow,
            "text/plain; charset=utf-8", "gzip", bytes.Length, Convert.ToHexStringLower(expected));
        await host.Services.GetRequiredService<IDurableBatchQueue>().PublishAsync(metadata, body, default);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await host.StopAsync();
        (await resources.PullAsync(resources.Subscription, false)).ShouldBeNull();
    }

    private async Task<OwnedHost> StartWorker(Dictionary<string, string?> settings, IWorkerBatchProcessor? processor = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = "Testing";
        builder.Configuration.AddInMemoryCollection(settings);
        CentralMobilidade.Telemetry.Worker.Program.ConfigureServices(builder.Services, builder.Configuration, builder.Environment);
        if (processor is not null)
        {
            builder.Services.RemoveAll<IWorkerBatchProcessor>();
            builder.Services.AddSingleton(processor);
        }
        var host = new OwnedHost(builder.Build());
        try
        {
            await host.StartAsync();
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }

    private Dictionary<string, string?> Settings(PubSubResources r, string connection) => new()
    {
        ["AllowedHosts"] = "localhost",
        ["Queue:Provider"] = "PubSub",
        ["Queue:PubSub:ProjectId"] = emulator.Project,
        ["Queue:PubSub:Endpoint"] = emulator.Endpoint,
        ["Queue:PubSub:UseEmulator"] = "true",
        ["Queue:PubSub:Topic"] = r.Topic,
        ["Queue:PubSub:Subscription"] = r.Subscription,
        ["Queue:PubSub:DeadLetterTopic"] = r.DeadTopic,
        ["Queue:PubSub:DeadLetterSubscription"] = r.DeadSubscription,
        ["Queue:PubSub:ConcurrentDeliveries"] = "2",
        ["Queue:PubSub:PullTimeout"] = "00:00:01",
        ["Queue:PubSub:DrainTimeout"] = "00:00:02",
        ["Queue:PubSub:PublishTimeout"] = "00:00:05",
        ["Worker:Concurrency"] = "2",
        ["Worker:HeartbeatInterval"] = "00:00:00.100",
        ["Worker:RecoveryDelay"] = "00:00:00.100",
        ["Health:WorkerMaxAge"] = "00:00:01",
        ["ConnectionStrings:TelemetryControl"] = connection,
        ["Observability:WorkerMetricsUrl"] = "http://127.0.0.1:0",
        ["InfluxDb:Queryable:Url"] = influx.Url,
        ["InfluxDb:Queryable:Database"] = "queryable",
        ["InfluxDb:Queryable:Token"] = "test-only-token",
        ["Processing:DiagnosticKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["BatchInspection:CursorKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["Quarantine:Path"] = Path.GetFullPath(Path.Combine("artifacts", "validation", "T081", "quarantine", Guid.NewGuid().ToString("N")))
    };

    private static async Task Publish(IServiceProvider services)
    {
        using var body = new MemoryStream(PubSubEmulatorFixture.Gzip);
        var envelope = new TelemetryBatchEnvelope("1", Guid.NewGuid(), new string('a', 32), "t081", DateTimeOffset.UtcNow,
            "text/plain; charset=utf-8", "gzip", body.Length, Convert.ToHexStringLower(SHA256.HashData(PubSubEmulatorFixture.Gzip)));
        await services.GetRequiredService<IDurableBatchQueue>().PublishAsync(envelope, body, default);
    }
    private static byte[] Compress(string raw)
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, true))
            gzip.Write(Encoding.UTF8.GetBytes(raw));
        return bytes.ToArray();
    }
    private static async Task Eventually(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await predicate())
            await Task.Delay(20, timeout.Token);
    }
    private sealed class ProbeProcessor(Func<IQueueDelivery, CancellationToken, ValueTask> process) : IWorkerBatchProcessor
    {
        public ValueTask ProcessAsync(IQueueDelivery delivery, CancellationToken token) => process(delivery, token);
    }
    private sealed class OwnedHost(IHost inner) : IAsyncDisposable
    {
        internal IServiceProvider Services => inner.Services;
        internal Task StartAsync() => inner.StartAsync();
        internal Task StopAsync() => inner.StopAsync();
        public async ValueTask DisposeAsync()
        {
            await inner.StopAsync();
            await ((IAsyncDisposable)inner).DisposeAsync();
        }
    }
}
