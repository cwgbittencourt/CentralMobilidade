using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Idempotency;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Recovery;

public sealed class SelectedEventIdentityGuardTests
{
    [Fact]
    public async Task Pending_promotion_is_fenced_and_preserves_original_evidence()
    {
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        await using var services = new ServiceCollection().AddTelemetryControlPersistence(postgres.Connection).BuildServiceProvider();
        var guard = new SelectedEventIdentityGuard(services.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>(), new Clock());
        var request = Request("promotion", TelemetryDestination.Pending);
        var lease = (await guard.AcquireOrResumeAsync(request, default)).ShouldBeOfType<IdentityReservationResult.Granted>().Lease;
        (await guard.TryMarkWritingAsync(lease, DateTimeOffset.UtcNow, default)).ShouldBeTrue();
        (await guard.TryCommitAsync(lease with
        {
            State = IdentityGuardState.Writing
        }, DateTimeOffset.UtcNow, default)).ShouldBeTrue();
        (await guard.GetCommittedAsync(request.Identity, default))!.Location.ShouldBe(IdentityStorageLocation.PendingCommitted);
        var owner = Guid.NewGuid();
        var promotion = (await guard.TryBeginPromotionAsync(request.Identity, owner, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default)).ShouldNotBeNull();
        var resumed = (await guard.TryBeginPromotionAsync(request.Identity, owner, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default)).ShouldNotBeNull();
        resumed.FencingVersion.ShouldBe(promotion.FencingVersion);
        (await guard.TryBeginPromotionAsync(request.Identity, Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default)).ShouldBeNull();
        await using (var db = postgres.Create())
            await db.Database.ExecuteSqlRawAsync("UPDATE event_identity_guards SET \"PromotionLeaseUntil\"=clock_timestamp()-interval '1 second'");
        var successor = (await guard.TryBeginPromotionAsync(request.Identity, Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default)).ShouldNotBeNull();
        successor.FencingVersion.ShouldBeGreaterThan(promotion.FencingVersion);
        (await guard.RenewPromotionAsync(promotion, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(2), default)).ShouldBeNull();
        (await guard.TryCompletePromotionAsync(promotion, DateTimeOffset.UtcNow, default)).ShouldBeFalse();
        (await guard.TryCompletePromotionAsync(successor, DateTimeOffset.UtcNow, default)).ShouldBeTrue();
        (await guard.GetCommittedAsync(request.Identity, default))!.Location.ShouldBe(IdentityStorageLocation.QueryableCommitted);
        (await guard.AcquireOrResumeAsync(request, default)).ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(ExistingIdentityOutcome.IdenticalReplay);
    }

    [Fact]
    public async Task Partition_upgrade_preserves_committed_identity_and_protects_leaf_truncate()
    {
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        await using var services = new ServiceCollection().AddTelemetryControlPersistence(postgres.Connection).BuildServiceProvider();
        await using var db = postgres.Create();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261013182313_InitialControlPlane");
        var guard = new SelectedEventIdentityGuard(services.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>(), new Clock());
        var request = Request("upgrade");
        var lease = (await guard.AcquireOrResumeAsync(request, default)).ShouldBeOfType<IdentityReservationResult.Granted>().Lease;
        (await guard.TryMarkWritingAsync(lease, DateTimeOffset.UtcNow, default)).ShouldBeTrue();
        (await guard.TryCommitAsync(lease with
        {
            State = IdentityGuardState.Writing
        }, DateTimeOffset.UtcNow, default)).ShouldBeTrue();
        await db.Database.MigrateAsync();
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_inherits WHERE inhparent='event_identity_guards'::regclass").SingleAsync()).ShouldBe(16);
        (await guard.GetCommittedAsync(request.Identity, default)).ShouldNotBeNull();
        (await guard.AcquireOrResumeAsync(request, default)).ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(ExistingIdentityOutcome.IdenticalReplay);
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE event_identity_guards_p0"));
    }

    [Fact]
    public async Task Measure_actual_per_identity_adapter_without_claiming_bulk_prototype_throughput()
    {
        await using var postgres = await MigratedPostgresFixture.StartAsync();
        await using var services = new ServiceCollection().AddTelemetryControlPersistence(postgres.Connection).BuildServiceProvider();
        var guard = new SelectedEventIdentityGuard(services.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>(), new Clock());
        var times = new ConcurrentBag<double>();
        const int count = 1000;
        var watch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (i, token) =>
        {
            var operation = Stopwatch.StartNew();
            var request = Request("benchmark-" + i);
            var lease = (await guard.AcquireOrResumeAsync(request, token)).ShouldBeOfType<IdentityReservationResult.Granted>().Lease;
            (await guard.TryMarkWritingAsync(lease, DateTimeOffset.UtcNow, token)).ShouldBeTrue();
            (await guard.TryCommitAsync(lease with
            {
                State = IdentityGuardState.Writing
            }, DateTimeOffset.UtcNow, token)).ShouldBeTrue();
            times.Add(operation.Elapsed.TotalMilliseconds);
        });
        watch.Stop();
        await using var db = postgres.Create();
        (await db.IdentityGuards.CountAsync(row => row.State == IdentityGuardState.Committed)).ShouldBe(count);
        (await db.IdentityEvidence.CountAsync()).ShouldBe(count);
        var sorted = times.OrderBy(value => value).ToArray();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CentralMobilidade.slnx")))
            directory = directory.Parent;
        var output = Path.Combine(directory!.FullName, "artifacts", "validation", "T047");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "adapter-benchmark.json"), JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow,
            count,
            concurrency = 32,
            seconds = watch.Elapsed.TotalSeconds,
            achievedEventsPerSecond = count / watch.Elapsed.TotalSeconds,
            targetEventsPerSecond = 5000,
            targetMet = count / watch.Elapsed.TotalSeconds >= 5000,
            p95Ms = sorted[950],
            p99Ms = sorted[990],
            scope = "Actual partitioned schema/adapter reserve+Writing+Committed with separate evidence, no Influx; cold-start short probe, no bulk API"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IdentityReservationRequest Request(string vehicle, TelemetryDestination destination = TelemetryDestination.Queryable)
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = EventTimestamp.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        return new(new(new("guard-tests"), new(vehicle), timestamp), new(new BatchId(Guid.NewGuid()), 1), new string('a', 64),
            destination, now, now.AddMinutes(1), now.AddDays(60), now.AddDays(30));
    }
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
