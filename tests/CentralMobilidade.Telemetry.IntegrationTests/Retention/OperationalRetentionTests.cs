using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Infrastructure.Quarantine;
using CentralMobilidade.Telemetry.IntegrationTests.Influx;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using WorkerProgram = CentralMobilidade.Telemetry.Worker.Program;

namespace CentralMobilidade.Telemetry.IntegrationTests.Retention;

[Trait("Category", "Integration")]
public sealed class OperationalRetentionTests(OperationalRetentionDatabase database, OperationalRetentionInflux influx)
    : IClassFixture<OperationalRetentionDatabase>, IClassFixture<OperationalRetentionInflux>
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Protected_raw_content_obeys_the_inclusive_thirty_day_deadline(int microseconds, bool expired)
    {
        await using var test = await Create();
        var seed = await test.Seed(test.Now.AddDays(-30));
        var cutoff = test.Now.AddTicks(microseconds * 10L);
        (await test.Quarantine.DeleteExpiredAsync(seed.QuarantineId, cutoff, default)).ShouldBe(expired);
        var metadata = await test.Quarantine.GetMetadataAsync(seed.QuarantineId, default);
        if (expired)
        {
            metadata.ShouldBeNull();
            (await test.Quarantine.OpenReadAsync(seed.QuarantineId, default)).ShouldBeNull();
            (await test.Quarantine.DeleteExpiredAsync(seed.QuarantineId, cutoff, default)).ShouldBeFalse();
            Directory.GetFiles(test.Root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        else
            metadata.ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancelled_raw_deletion_keeps_the_protected_content_recoverable()
    {
        await using var test = await Create();
        var seed = await test.Seed(test.Now.AddDays(-31));
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => test.Quarantine.DeleteExpiredAsync(seed.QuarantineId, test.Now, stop.Token).AsTask());
        (await test.Quarantine.GetMetadataAsync(seed.QuarantineId, default)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Single_database_telemetry_remains_after_thirty_days_and_expires_after_sixty()
    {
        using var client = new HttpClient();
        const string name = "telemetry";
        var now = DateTimeOffset.UtcNow;
        var lines = new StringBuilder();
        foreach (var days in new[] { 29, 59, 61 })
            foreach (var company in new[] { "company-a", "company-b" })
                lines.Append(CultureInfo.InvariantCulture, $"retentionproof,empresa_id={company},age={days} value=1i {now.AddDays(-days).ToUnixTimeMilliseconds()}\n");
        using var content = new StringContent(lines.ToString(), Encoding.UTF8, "text/plain");
        using var written = await client.PostAsync(influx.Url + "/api/v3/write_lp?db=" + name + "&precision=millisecond&no_sync=false", content);
        written.EnsureSuccessStatusCode();
        foreach (var days in new[] { 29, 59, 61 })
        {
            var timestamp = now.AddDays(-days);
            var query = $"SELECT empresa_id FROM retentionproof WHERE time >= '{timestamp.AddHours(-1):O}' AND time < '{timestamp.AddHours(1):O}' ORDER BY empresa_id";
            using var rows = JsonDocument.Parse(await client.GetStringAsync(influx.Url + "/api/v3/query_sql?db=" + name + "&format=json&q=" + Uri.EscapeDataString(query)));
            rows.RootElement.GetArrayLength().ShouldBe(days == 61 ? 0 : 2);
            if (days != 61)
                rows.RootElement.EnumerateArray().Select(row => row.GetProperty("empresa_id").GetString()).ShouldBe(new[] { "company-a", "company-b" });
        }
    }

    [Theory]
    [Trait("PendingImplementation", "T061")]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Cleanup_removes_expired_batch_rejection_metadata_and_raw_content_together(int microseconds, bool expired)
    {
        await using var test = await Create();
        var seed = await test.Seed(test.Now.AddDays(-30));
        test.Clock.UtcNow = test.Now.AddTicks(microseconds * 10L);
        await test.Cleanup();
        await using var db = test.Database.Create();
        (await db.Batches.AnyAsync(row => row.BatchId == seed.BatchId.Value)).ShouldBe(!expired);
        (await db.LineResults.AnyAsync(row => row.BatchId == seed.BatchId.Value)).ShouldBe(!expired);
        (await db.QuarantinedRejections.AnyAsync(row => row.QuarantineId == seed.QuarantineId)).ShouldBe(!expired);
        ((await test.Quarantine.GetMetadataAsync(seed.QuarantineId, default)) is not null).ShouldBe(!expired);
    }

    [Fact]
    [Trait("PendingImplementation", "T061")]
    public async Task Repeated_and_concurrent_cleanup_preserves_fresh_data_and_is_idempotent()
    {
        await using var test = await Create();
        var old = await test.Seed(test.Now.AddDays(-31));
        var fresh = await test.Seed(test.Now.AddDays(-29));
        await using var otherWorker = test.AnotherWorker();
        await Task.WhenAll(test.Cleanup(), test.Cleanup(otherWorker)).WaitAsync(TimeSpan.FromSeconds(15));
        await test.Cleanup();
        await using var db = test.Database.Create();
        (await db.Batches.Select(row => row.BatchId).ToArrayAsync()).ShouldBe(new[] { fresh.BatchId.Value });
        (await db.LineResults.Select(row => row.BatchId).ToArrayAsync()).ShouldBe(new[] { fresh.BatchId.Value });
        (await test.Quarantine.GetMetadataAsync(old.QuarantineId, default)).ShouldBeNull();
        (await test.Quarantine.GetMetadataAsync(fresh.QuarantineId, default)).ShouldNotBeNull();
    }

    [Fact]
    [Trait("PendingImplementation", "T061")]
    public async Task Thirty_day_evidence_cleanup_does_not_delete_the_sixty_day_immutable_guard()
    {
        await using var test = await Create();
        var seed = await test.Seed(test.Now.AddDays(-31));
        await test.SeedGuard(seed.BatchId, test.Now.AddDays(-31));
        await test.Cleanup();
        await using var db = test.Database.Create();
        (await db.IdentityEvidence.CountAsync()).ShouldBe(0);
        var guard = (await db.IdentityGuards.SingleAsync()).ShouldNotBeNull();
        guard.State.ShouldBe(IdentityGuardState.Committed);
        guard.OwnerBatchId.ShouldBe(seed.BatchId.Value);
        guard.FencingVersion.ShouldBe(1);
        guard.GuardExpiresAt.ShouldBe(test.Now.AddDays(29));
        (await db.Batches.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [Trait("PendingImplementation", "T061")]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Committed_guard_is_deleted_only_when_its_telemetry_lifetime_has_expired(int microseconds, bool expired)
    {
        await using var test = await Create();
        var seed = await test.Seed(test.Now.AddDays(-61));
        await test.SeedGuard(seed.BatchId, test.Now.AddDays(-60));
        test.Clock.UtcNow = test.Now.AddTicks(microseconds * 10L);
        await test.Cleanup();
        await using var db = test.Database.Create();
        (await db.IdentityGuards.CountAsync()).ShouldBe(expired ? 0 : 1);
        (await db.IdentityEvidence.CountAsync()).ShouldBe(0);
    }

    [Fact]
    [Trait("PendingImplementation", "T061")]
    public async Task Failed_raw_deletion_preserves_metadata_for_a_later_cleanup_attempt()
    {
        await using var test = await Create(failDeletion: true);
        var seed = await test.Seed(test.Now.AddDays(-31));
        try
        {
            await test.Cleanup();
        }
        catch (ProviderOperationException) { }
        await using var db = test.Database.Create();
        (await db.QuarantinedRejections.AnyAsync(row => row.QuarantineId == seed.QuarantineId)).ShouldBeTrue();
        (await test.Quarantine.GetMetadataAsync(seed.QuarantineId, default)).ShouldNotBeNull();
    }

    [Fact]
    [Trait("PendingImplementation", "T061")]
    public async Task Removing_thirty_day_operational_rows_preserves_the_same_database_telemetry()
    {
        await using var test = await Create();
        var at = test.Now.AddDays(-31);
        await test.Seed(at);
        var table = "cleanup_" + Guid.NewGuid().ToString("N");
        using var client = new HttpClient();
        using var content = new StringContent($"{table},empresa_id=company-a value=1i {at.ToUnixTimeMilliseconds()}", Encoding.UTF8, "text/plain");
        using var written = await client.PostAsync(influx.Url + "/api/v3/write_lp?db=telemetry&precision=millisecond&no_sync=false", content);
        written.EnsureSuccessStatusCode();
        var query = Uri.EscapeDataString($"SELECT empresa_id FROM {table} WHERE time >= '{at.AddHours(-1):O}' AND time < '{at.AddHours(1):O}'");
        var url = influx.Url + "/api/v3/query_sql?db=telemetry&format=json&q=" + query;
        using var before = JsonDocument.Parse(await client.GetStringAsync(url));
        before.RootElement.GetArrayLength().ShouldBe(1);
        await test.Cleanup();
        await using var db = test.Database.Create();
        (await db.Batches.CountAsync()).ShouldBe(0);
        using var after = JsonDocument.Parse(await client.GetStringAsync(url));
        after.RootElement.GetArrayLength().ShouldBe(1);
    }

    [Fact]
    [Trait("PendingImplementation", "T061")]
    public async Task Aggregate_metrics_remain_exported_after_the_detailed_operational_rows_are_deleted()
    {
        await using var test = await Create();
        await test.Seed(test.Now.AddDays(-31));
        var endpoint = test.Services.GetServices<IHostedService>().OfType<WorkerMetricsEndpoint>().Single();
        await endpoint.StartAsync(default);
        try
        {
            test.Services.GetRequiredService<TelemetryObservability>().RecordReceived(512);
            using var client = new HttpClient();
            var before = await client.GetStringAsync(endpoint.ListenUrl + "/metrics");
            var baseline = Received(before);
            baseline.ShouldBeGreaterThanOrEqualTo(1);
            await test.Cleanup();
            await using var db = test.Database.Create();
            (await db.Batches.CountAsync()).ShouldBe(0);
            var after = await client.GetStringAsync(endpoint.ListenUrl + "/metrics");
            Received(after).ShouldBeGreaterThanOrEqualTo(baseline);
            after.ShouldNotContain("PRIVATE_REJECTED_RETENTION_CONTENT");
            after.ShouldNotContain("empresa_id=");
            after.ShouldNotContain("veiculo_id=");
        }
        finally { await endpoint.StopAsync(default); }
    }

    private static double Received(string exposition)
    {
        var line = exposition.Split('\n').Single(value => value.StartsWith("telemetry_batches_received", StringComparison.Ordinal));
        foreach (var label in new[] { "batchId", "empresa", "veiculo", "vehicle", "producer" })
            line.ShouldNotContain(label);
        return double.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
    }

    private async Task<Fixture> Create(bool failDeletion = false)
    {
        var postgres = await MigratedPostgresFixture.StartAsync(connectionOverride: database.Connection);
        var root = Path.Combine(Path.GetTempPath(), "centralmobilidade-t056-" + Guid.NewGuid().ToString("N"));
        var clock = new RetentionClock(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TelemetryControl"] = postgres.Connection,
            ["Queue:Provider"] = "RabbitMq",
            ["Queue:RabbitMq:Host"] = "127.0.0.1",
            ["Queue:RabbitMq:Username"] = "unused-test",
            ["Queue:RabbitMq:Password"] = "unused-test",
            ["InfluxDb:Queryable:Url"] = influx.Url,
            ["InfluxDb:Queryable:Database"] = "telemetry",
            ["InfluxDb:Queryable:Token"] = "test-only-token",
            ["Quarantine:Path"] = root,
            ["Processing:DiagnosticKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x56, 32).ToArray()),
            ["Observability:WorkerMetricsUrl"] = "http://127.0.0.1:0"
        }).Build();
        var collection = new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton<TimeProvider>(clock);
        WorkerProgram.ConfigureServices(collection, config);
        var quarantine = new FileSystemQuarantineStore(config, NullLogger<FileSystemQuarantineStore>.Instance);
        collection.RemoveAll<IQuarantineStore>();
        collection.AddSingleton<IQuarantineStore>(failDeletion ? new FailedDeletion(quarantine) : quarantine);
        return new(postgres, collection.BuildServiceProvider(), quarantine, clock, root);
    }

    private sealed class RetentionClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed record Fixture(MigratedPostgresFixture Database, ServiceProvider Services, FileSystemQuarantineStore Quarantine, RetentionClock Clock, string Root) : IAsyncDisposable
    {
        internal DateTimeOffset Now { get; } = Clock.UtcNow;
        internal async Task<(BatchId BatchId, Guid QuarantineId)> Seed(DateTimeOffset created)
        {
            var metadata = new TelemetryBatchEnvelope("1", Guid.NewGuid(), new string('a', 32), "retention-producer", created,
                "text/plain; charset=utf-8", "gzip", 128, new string('a', 64), "v1");
            var store = Services.GetRequiredService<IBatchStateStore>();
            var batch = await store.CreateAsync(metadata, created.AddDays(30), default);
            foreach (var state in new[] { BatchLifecycle.Publishing, BatchLifecycle.Accepted, BatchLifecycle.Processing })
            {
                (await store.TryTransitionAsync(batch.BatchId, batch.Version, new(state, created.AddSeconds(batch.Version)), default)).ShouldBeTrue();
                batch = (await store.GetAsync(batch.BatchId, default))!;
            }
            var id = Guid.NewGuid();
            using var raw = new MemoryStream(Encoding.UTF8.GetBytes("PRIVATE_REJECTED_RETENTION_CONTENT"));
            await Quarantine.WriteAsync(new(id, batch.BatchId, 1, TelemetryErrorCodes.LpInvalidSyntax, created, created.AddDays(30)), raw, default);
            var protectedMetadata = (await Quarantine.GetMetadataAsync(id, default))!;
            await store.RecordLineResultsAsync(batch.BatchId, [new(1, LineOutcome.Rejected, TelemetryErrorCodes.LpInvalidSyntax,
                new("company-a"), new("vehicle-a"), "hmac-sha256:v1:" + new string('b', 64), created, id, protectedMetadata)], default);
            batch = (await store.GetAsync(batch.BatchId, default))!;
            (await store.TryTransitionAsync(batch.BatchId, batch.Version, new(BatchLifecycle.CompletedWithRejections, created.AddSeconds(6), new(0, 1, 0, 1)), default)).ShouldBeTrue();
            return (batch.BatchId, id);
        }

        internal async Task SeedGuard(BatchId owner, DateTimeOffset timestamp)
        {
            await using var db = Database.Create();
            db.IdentityGuards.Add(new EventIdentityGuard
            {
                CompanyId = "company-a",
                VehicleId = "vehicle-a",
                EventTimestamp = timestamp,
                State = IdentityGuardState.Committed,
                OwnerBatchId = owner.Value,
                OwnerLineNumber = 1,
                FencingVersion = 1,
                TelemetryExpiresAt = timestamp.AddDays(60),
                GuardExpiresAt = timestamp.AddDays(60)
            });
            db.IdentityEvidence.Add(new EventIdentityEvidence
            {
                CompanyId = "company-a",
                VehicleId = "vehicle-a",
                EventTimestamp = timestamp,
                CanonicalFingerprint = new byte[32],
                FirstBatchId = owner.Value,
                FirstSeenAt = timestamp,
                EvidenceExpiresAt = timestamp.AddDays(30)
            });
            await db.SaveChangesAsync();
        }

        internal ServiceProvider AnotherWorker()
        {
            var config = Services.GetRequiredService<IConfiguration>();
            var collection = new ServiceCollection().AddSingleton(config).AddSingleton<TimeProvider>(Clock);
            WorkerProgram.ConfigureServices(collection, config);
            collection.RemoveAll<IQuarantineStore>();
            collection.AddSingleton<IQuarantineStore>(Quarantine);
            return collection.BuildServiceProvider();
        }

        internal async Task Cleanup(ServiceProvider? provider = null)
        {
            // Bind to the future production component, never substitute deletion logic in the test.
            var type = typeof(WorkerProgram).Assembly.GetType("CentralMobilidade.Telemetry.Worker.Retention.OperationalRetentionWorker");
            type.ShouldNotBeNull("T061 must implement and register OperationalRetentionWorker");
            var worker = (provider ?? Services).GetServices<IHostedService>().SingleOrDefault(type.IsInstanceOfType);
            worker.ShouldNotBeNull("T061 must register the retention worker in the production composition");
            var method = type.GetMethod("RunOnceAsync", [typeof(CancellationToken)]);
            method.ShouldNotBeNull("Expose one awaitable cleanup pass RunOnceAsync(CancellationToken) for deterministic retention tests");
            object? operation;
            try
            {
                operation = method.Invoke(worker, [CancellationToken.None]);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
            switch (operation)
            {
                case Task task:
                    await task.WaitAsync(TimeSpan.FromSeconds(15));
                    break;
                case ValueTask task:
                    await task.AsTask().WaitAsync(TimeSpan.FromSeconds(15));
                    break;
                default:
                    throw new InvalidOperationException("The production cleanup pass must return Task or ValueTask.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Database.DisposeAsync();
            var resolved = Path.GetFullPath(Root);
            Path.GetDirectoryName(resolved).ShouldBe(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
            Path.GetFileName(resolved).ShouldStartWith("centralmobilidade-t056-");
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }

    private sealed class FailedDeletion(IQuarantineStore inner) : IQuarantineStore
    {
        public ValueTask<QuarantineReceipt> WriteAsync(QuarantineWriteRequest request, Stream raw, CancellationToken token) => inner.WriteAsync(request, raw, token);
        public ValueTask<QuarantineMetadata?> GetMetadataAsync(Guid id, CancellationToken token) => inner.GetMetadataAsync(id, token);
        public ValueTask<IQuarantineContent?> OpenReadAsync(Guid id, CancellationToken token) => inner.OpenReadAsync(id, token);
        public ValueTask<bool> DeleteExpiredAsync(Guid id, DateTimeOffset cutoff, CancellationToken token) => throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Transient);
    }
}

public sealed class OperationalRetentionDatabase : IAsyncLifetime
{
    private MigratedPostgresFixture database = null!;
    internal string Connection => database.Connection;
    public async Task InitializeAsync() => database = await MigratedPostgresFixture.StartAsync();
    public async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }
}

/// <summary>One server and one 60-day database shared by the retention class.</summary>
public sealed class OperationalRetentionInflux : IAsyncLifetime
{
    private readonly InfluxContractServer server = new();
    internal string Url => server.Url;
    public async Task InitializeAsync()
    {
        await server.InitializeAsync();
        using var client = new HttpClient();
        using var response = await client.PostAsJsonAsync(Url + "/api/v3/configure/database", new
        {
            db = "telemetry",
            retention_period = "60d"
        });
        response.EnsureSuccessStatusCode();
    }
    public Task DisposeAsync() => server.DisposeAsync();
}
