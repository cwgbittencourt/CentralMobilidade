using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.LineProtocol;
using CentralMobilidade.Telemetry.Application.Processing;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Idempotency;
using CentralMobilidade.Telemetry.Infrastructure.Influx;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Infrastructure.Quarantine;
using CentralMobilidade.Telemetry.IntegrationTests.Influx;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Recovery;

public sealed class ProcessingRealStorageTests(InfluxContractServer influx) : IClassFixture<InfluxContractServer>
{
    [Fact]
    public async Task Mixed_delivery_uses_real_guard_writer_quarantine_results_and_terminal_redelivery()
    {
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        var directory = Path.Combine(Path.GetTempPath(), "centralmobilidade-t051-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Processing:DiagnosticKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x51, 32).ToArray()),
            ["Quarantine:Path"] = directory,
            ["InfluxDb:Queryable:Url"] = influx.Url,
            ["InfluxDb:Queryable:Database"] = "queryable",
            ["InfluxDb:Queryable:Token"] = "test-only-token",
            ["Cadastral:CompanyVisibilityUrl"] = "http://127.0.0.1:1/must-never-be-called"
        }).Build();
        try
        {
            await using var services = new ServiceCollection().AddTelemetryControlPersistence(postgres.Connection).BuildServiceProvider();
            var store = services.GetRequiredService<IBatchStateStore>();
            var clock = new Clock();
            var options = Options.Create(new TelemetryOptions { WriteBatchMaxPoints = 2 });
            var guard = new SelectedEventIdentityGuard(services.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>(), clock, options);
            var quarantine = new FileSystemQuarantineStore(configuration, NullLogger<FileSystemQuarantineStore>.Instance);
            using var client = new HttpClient();
            var writer = new CountingWriter(new InfluxV3TelemetryWriter(client, configuration));
            var handler = new ProcessTelemetryBatchHandler(store, guard, writer, quarantine, new TelemetryLineProtocolV1Parser(options), clock, options, TimeProvider.System, configuration);
            var now = clock.UtcNow;
            var body = $"telemetria,empresa_id=t051-a,veiculo_id=real-a latitude=1.0,longitude=2.0,velocidade=0.0,rpm=1i,ignicao=true {now.ToUnixTimeMilliseconds()}\n" +
                $"telemetria,empresa_id=t051-b,veiculo_id=real-b latitude=1.0,longitude=2.0,velocidade=0.0,rpm=1i,ignicao=true {now.ToUnixTimeMilliseconds()}\n" +
                "SEEDED_PRIVATE_REJECTED_LINE\n";
            var bytes = Compress(body);
            var metadata = new TelemetryBatchEnvelope("1", Guid.NewGuid(), "0123456789abcdef0123456789abcdef", "real-test-producer", now,
                "text/plain; charset=utf-8", "gzip", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "v1");
            var created = await store.CreateAsync(metadata, now.AddDays(30), default);
            (await store.TryTransitionAsync(created.BatchId, created.Version, new(BatchLifecycle.Publishing, now), default)).ShouldBeTrue();
            (await store.TryTransitionAsync(created.BatchId, created.Version + 1, new(BatchLifecycle.Accepted, now), default)).ShouldBeTrue();
            await using var delivery = new Delivery(metadata, bytes, store);
            await handler.HandleAsync(delivery, default);
            delivery.Completed.ShouldBeTrue();
            var snapshot = (await store.GetAsync(created.BatchId, default)).ShouldNotBeNull();
            snapshot.Counters.ShouldBe(new BatchCounters(2, 1, 0, 3));
            snapshot.State.ShouldBe(BatchLifecycle.CompletedWithRejections);
            snapshot.ErrorSummary.Values.Sum().ShouldBe(1);
            writer.Calls.ShouldBe(1);
            await using var db = postgres.Create();
            (await db.IdentityGuards.CountAsync(r => r.State == IdentityGuardState.Committed)).ShouldBe(2);
            var rejected = await db.QuarantinedRejections.SingleAsync();
            var protectedMetadata = (await quarantine.GetMetadataAsync(rejected.QuarantineId, default)).ShouldNotBeNull();
            protectedMetadata.ContentSha256.ShouldBe(rejected.ContentDigest);
            rejected.EncryptedContentLocation.ShouldNotContain(directory);
            await using (var raw = (await quarantine.OpenReadAsync(rejected.QuarantineId, default)).ShouldNotBeNull())
            {
                using var recovered = new MemoryStream();
                await raw.Content.CopyToAsync(recovered);
                Encoding.UTF8.GetString(recovered.ToArray()).ShouldBe("SEEDED_PRIVATE_REJECTED_LINE");
            }
            using var document = JsonDocument.Parse(await client.GetStringAsync(influx.Url + "/api/v3/query_sql?db=queryable&format=json&q=" +
                Uri.EscapeDataString("SELECT empresa_id,event_fingerprint FROM telemetria WHERE empresa_id IN ('t051-a','t051-b') ORDER BY empresa_id")));
            document.RootElement.GetArrayLength().ShouldBe(2);
            document.RootElement.EnumerateArray().ShouldAllBe(row => row.GetProperty("event_fingerprint").GetString()!.Length == 64);
            await using var redelivery = new Delivery(metadata, bytes, store);
            await handler.HandleAsync(redelivery, default);
            redelivery.Completed.ShouldBeTrue();
            writer.Calls.ShouldBe(1);
            (await db.QuarantinedRejections.CountAsync()).ShouldBe(1);
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            Path.GetDirectoryName(resolved).ShouldBe(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
            Path.GetFileName(resolved).ShouldStartWith("centralmobilidade-t051-");
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }
    private static byte[] Compress(string raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
            gzip.Write(Encoding.UTF8.GetBytes(raw));
        return output.ToArray();
    }
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
    private sealed class CountingWriter(ITelemetryWriter inner) : ITelemetryWriter
    {
        internal int Calls;
        public ValueTask<TelemetryWriteResult> WriteAsync(TelemetryWriteRequest request, Stream body, CancellationToken token)
        {
            Calls++;
            return inner.WriteAsync(request, body, token);
        }
    }
    private sealed class Delivery(TelemetryBatchEnvelope metadata, byte[] bytes, IBatchStateStore store) : IQueueDelivery
    {
        public TelemetryBatchEnvelope Metadata => metadata;
        public Stream Body { get; } = new MemoryStream(bytes);
        public int DeliveryAttempt => 1;
        internal bool Completed;
        public async ValueTask CompleteAsync(CancellationToken token)
        {
            var snapshot = (await store.GetAsync(new(metadata.BatchId), token)).ShouldNotBeNull();
            snapshot.State.ShouldBeOneOf(BatchLifecycle.Completed, BatchLifecycle.CompletedWithRejections);
            Completed = true;
        }
        public ValueTask RetryAsync(TimeSpan delay, CancellationToken token) => throw new InvalidOperationException("Unexpected retry in real-storage proof.");
        public ValueTask DeadLetterAsync(string errorCode, CancellationToken token) => throw new InvalidOperationException("Unexpected dead letter.");
        public ValueTask DisposeAsync() => Body.DisposeAsync();
    }
}
