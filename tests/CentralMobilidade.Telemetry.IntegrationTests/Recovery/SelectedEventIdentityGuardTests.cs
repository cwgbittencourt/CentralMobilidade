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
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Recovery;

public sealed class SelectedEventIdentityGuardTests
{
    [Theory]
    [InlineData("20261013182313_InitialControlPlane", IdentityGuardState.Reserved)]
    [InlineData("20261013182313_InitialControlPlane", IdentityGuardState.Writing)]
    [InlineData("20261013182313_InitialControlPlane", IdentityGuardState.Committed)]
    [InlineData("20261014190000_PartitionIdentityGuard", IdentityGuardState.Reserved)]
    [InlineData("20261014190000_PartitionIdentityGuard", IdentityGuardState.Writing)]
    [InlineData("20261014190000_PartitionIdentityGuard", IdentityGuardState.Committed)]
    public async Task Populated_upgrade_preserves_identity_and_resumable_owner_without_routing(string migration, IdentityGuardState state)
    {
        await using var postgres = await MigratedPostgresFixture.StartAsync(migration);
        await using var services = new ServiceCollection().AddTelemetryControlPersistence(postgres.Connection).BuildServiceProvider();
        await using var db = postgres.Create();
        var guard = new SelectedEventIdentityGuard(services.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>(), new Clock());
        var request = Request("upgrade");
        DateTimeOffset? leaseUntil = state == IdentityGuardState.Committed ? null : request.LeaseUntil;
        // Seed the applied legacy schema through its original SQL shape; the new adapter only
        // runs after the single-store migration, which removes the old NOT NULL routing columns.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO event_identity_guards
             ("CompanyId","VehicleId","EventTimestamp","State","OwnerBatchId","OwnerLineNumber","LeaseUntil","FencingVersion","Destination","StorageLocation","GuardExpiresAt","TelemetryExpiresAt")
            VALUES ({request.Identity.CompanyId.Value},{request.Identity.VehicleId.Value},{request.Identity.Timestamp.Value},
             {state.ToString()},{request.Owner.BatchId.Value},1,{leaseUntil},1,'Queryable','QueryableCommitted',{request.GuardExpiresAt},{request.GuardExpiresAt});
            INSERT INTO event_identity_evidence
             ("CompanyId","VehicleId","EventTimestamp","CanonicalFingerprint","FirstBatchId","FirstSeenAt","EvidenceExpiresAt")
            VALUES ({request.Identity.CompanyId.Value},{request.Identity.VehicleId.Value},{request.Identity.Timestamp.Value},
             {Convert.FromHexString(request.Fingerprint)},{request.Owner.BatchId.Value},{request.Now},{request.EvidenceExpiresAt});
            """);
        await db.Database.MigrateAsync();
        var row = await db.IdentityGuards.AsNoTracking().SingleAsync();
        row.State.ShouldBe(state);
        row.OwnerBatchId.ShouldBe(request.Owner.BatchId.Value);
        row.OwnerLineNumber.ShouldBe(1);
        row.FencingVersion.ShouldBe(1);
        row.LeaseUntil.ShouldBe(leaseUntil);
        row.GuardExpiresAt.ShouldBe(request.GuardExpiresAt);
        row.TelemetryExpiresAt.ShouldBe(request.GuardExpiresAt);
        var evidence = await db.IdentityEvidence.AsNoTracking().SingleAsync();
        evidence.FirstBatchId.ShouldBe(request.Owner.BatchId.Value);
        evidence.FirstSeenAt.ShouldBe(request.Now);
        evidence.EvidenceExpiresAt.ShouldBe(request.EvidenceExpiresAt);
        evidence.CanonicalFingerprint.ShouldBe(Convert.FromHexString(request.Fingerprint));
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name='event_identity_guards' AND column_name IN ('Destination','StorageLocation','PromotionOwner','PromotionLeaseUntil')").SingleAsync()).ShouldBe(0);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_inherits WHERE inhparent='event_identity_guards'::regclass").SingleAsync()).ShouldBe(16);
        (await guard.AcquireOrResumeAsync(request with { Fingerprint = new string('b', 64) }, default))
            .ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(ExistingIdentityOutcome.Conflict);
        if (state != IdentityGuardState.Committed)
        {
            (await guard.GetCommittedAsync(request.Identity, default)).ShouldBeNull();
            var resumed = (await guard.AcquireOrResumeAsync(request, default)).ShouldBeOfType<IdentityReservationResult.Granted>();
            resumed.IsResumption.ShouldBeTrue();
            resumed.Lease.FencingVersion.ShouldBe(1);
            resumed.Lease.State.ShouldBe(state);
            if (state == IdentityGuardState.Reserved)
                (await guard.TryMarkWritingAsync(resumed.Lease, DateTimeOffset.UtcNow, default)).ShouldBeTrue();
            (await guard.TryCommitAsync(resumed.Lease with { State = IdentityGuardState.Writing }, DateTimeOffset.UtcNow, default)).ShouldBeTrue();
        }
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

    private static IdentityReservationRequest Request(string vehicle)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var timestamp = EventTimestamp.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        return new(new(new("guard-tests"), new(vehicle), timestamp), new(new BatchId(Guid.NewGuid()), 1), new string('a', 64),
            now, now.AddMinutes(1), now.AddDays(60), now.AddDays(30));
    }
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
