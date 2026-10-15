using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Worker.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;
namespace CentralMobilidade.Telemetry.IntegrationTests.Retention;

public sealed partial class OperationalRetentionTests
{
    [Fact]
    public async Task Hosted_worker_runs_cleanup_automatically_and_stops_while_waiting_for_the_next_pass()
    {
        await using var test = await Create();
        await test.Seed(test.Now.AddDays(-31));
        var worker = test.Services.GetServices<IHostedService>().OfType<OperationalRetentionWorker>().Single();
        await worker.StartAsync(default);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var db = test.Database.Create();
            while (await db.Batches.AnyAsync(deadline.Token))
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            (await db.QuarantinedRejections.CountAsync()).ShouldBe(0);
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(deadline.Token);
        }
    }

    [Fact]
    public async Task Database_lease_excludes_an_independent_worker_until_the_owner_finishes()
    {
        DeletionProbe probe = null!;
        await using var test = await Create(decorate: inner => probe = new(inner) { Pause = true });
        var seed = await test.Seed(test.Now.AddDays(-31));
        var owner = test.Cleanup();
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var other = test.AnotherWorker();
        var result = await other.GetRequiredService<IOperationalRetentionCleanup>().SweepAsync(test.Now, default);
        result.LeaseAcquired.ShouldBeFalse();
        probe.Calls.ShouldBe(1);
        (await test.Quarantine.GetMetadataAsync(seed.QuarantineId, default)).ShouldNotBeNull();
        probe.Release.TrySetResult();
        await owner;
        (await test.Quarantine.GetMetadataAsync(seed.QuarantineId, default)).ShouldBeNull();
        (await other.GetRequiredService<IOperationalRetentionCleanup>().SweepAsync(test.Now, default)).LeaseAcquired.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancelled_owner_releases_the_database_lease_and_preserves_recovery_metadata()
    {
        DeletionProbe probe = null!;
        await using var test = await Create(decorate: inner => probe = new(inner) { Pause = true });
        var seed = await test.Seed(test.Now.AddDays(-31));
        var worker = test.Services.GetServices<IHostedService>().OfType<OperationalRetentionWorker>().Single();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = worker.RunOnceAsync(stop.Token);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        await using var db = test.Database.Create();
        (await db.QuarantinedRejections.AnyAsync(x => x.QuarantineId == seed.QuarantineId)).ShouldBeTrue();
        probe.Pause = false;
        await test.Cleanup();
        (await db.Batches.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_object_does_not_block_other_expired_objects_and_can_be_retried()
    {
        DeletionProbe probe = null!;
        await using var test = await Create(decorate: inner => probe = new(inner));
        var broken = await test.Seed(test.Now.AddDays(-31));
        var healthy = await test.Seed(test.Now.AddDays(-31));
        probe.FailId = broken.QuarantineId;
        await test.Cleanup();
        await using var db = test.Database.Create();
        (await db.Batches.Select(x => x.BatchId).ToArrayAsync()).ShouldBe(new[] { broken.BatchId.Value });
        (await test.Quarantine.GetMetadataAsync(healthy.QuarantineId, default)).ShouldBeNull();
        (await test.Quarantine.GetMetadataAsync(broken.QuarantineId, default)).ShouldNotBeNull();
        probe.FailId = null;
        await test.Cleanup();
        (await db.Batches.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Deleted_bytes_with_uncommitted_metadata_are_reconciled_on_the_next_pass()
    {
        DeletionProbe probe = null!;
        await using var test = await Create(decorate: inner => probe = new(inner) { FailAfterDelete = true });
        var seed = await test.Seed(test.Now.AddDays(-31));
        await test.Cleanup();
        await using var db = test.Database.Create();
        (await db.QuarantinedRejections.AnyAsync()).ShouldBeTrue();
        (await test.Quarantine.GetMetadataAsync(seed.QuarantineId, default)).ShouldBeNull();
        probe.FailAfterDelete = false;
        await test.Cleanup();
        (await db.Batches.CountAsync()).ShouldBe(0);
        (await db.QuarantinedRejections.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_noop_file_deletion_preserves_the_parent_and_content_until_retry()
    {
        DeletionProbe probe = null!;
        await using var test = await Create(decorate: inner => probe = new(inner) { Noop = true });
        var seed = await test.Seed(test.Now.AddDays(-31));
        await test.Cleanup();
        await using var db = test.Database.Create();
        (await db.Batches.CountAsync()).ShouldBe(1);
        (await test.Quarantine.GetMetadataAsync(seed.QuarantineId, default)).ShouldNotBeNull();
        probe.Noop = false;
        await test.Cleanup();
        (await db.Batches.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Bounded_passes_make_progress_without_loading_or_deleting_the_whole_backlog()
    {
        await using var test = await Create(retentionSettings: new Dictionary<string, string?>
        {
            ["Retention:PageSize"] = "1",
            ["Retention:MaxPages"] = "1"
        });
        await test.Seed(test.Now.AddDays(-31));
        await test.Seed(test.Now.AddDays(-31));
        await test.Cleanup();
        await using var db = test.Database.Create();
        (await db.QuarantinedRejections.CountAsync()).ShouldBe(1);
        for (var pass = 0; pass < 4; pass++)
            await test.Cleanup();
        (await db.QuarantinedRejections.CountAsync()).ShouldBe(0);
        (await db.Batches.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Guard_cleanup_respects_extended_retention_and_an_active_incomplete_lease()
    {
        await using var test = await Create();
        var batch = await test.Seed(test.Now.AddDays(-61));
        await test.SeedGuard(batch.BatchId, test.Now.AddDays(-61));
        await using var db = test.Database.Create();
        await db.IdentityGuards.ExecuteUpdateAsync(s => s.SetProperty(x => x.GuardExpiresAt, test.Now.AddDays(1)));
        db.IdentityGuards.Add(new EventIdentityGuard
        {
            CompanyId = "company-b",
            VehicleId = "vehicle-b",
            EventTimestamp = test.Now.AddDays(-61),
            State = IdentityGuardState.Writing,
            LeaseUntil = test.Now.AddMinutes(1),
            OwnerBatchId = batch.BatchId.Value,
            OwnerLineNumber = 1,
            FencingVersion = 1,
            TelemetryExpiresAt = test.Now.AddDays(-1),
            GuardExpiresAt = test.Now.AddDays(-1)
        });
        await db.SaveChangesAsync();
        await test.Cleanup();
        (await db.IdentityGuards.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task A_future_application_clock_cannot_bypass_the_database_guard_lifetime()
    {
        await using var test = await Create();
        var batch = await test.Seed(test.Now.AddDays(-31));
        await test.SeedGuard(batch.BatchId, test.Now.AddDays(-31));
        test.Clock.UtcNow = test.Now.AddDays(60);
        await test.Cleanup();
        await using var db = test.Database.Create();
        (await db.IdentityGuards.CountAsync()).ShouldBe(1);
    }

    private sealed class DeletionProbe(IQuarantineStore inner) : IQuarantineStore
    {
        internal bool Pause, Noop, FailAfterDelete;
        internal Guid? FailId;
        internal int Calls;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<QuarantineReceipt> WriteAsync(QuarantineWriteRequest request, Stream raw, CancellationToken token) => inner.WriteAsync(request, raw, token);
        public ValueTask<QuarantineMetadata?> GetMetadataAsync(Guid id, CancellationToken token) => inner.GetMetadataAsync(id, token);
        public ValueTask<IQuarantineContent?> OpenReadAsync(Guid id, CancellationToken token) => inner.OpenReadAsync(id, token);
        public async ValueTask<bool> DeleteExpiredAsync(Guid id, DateTimeOffset cutoff, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Pause)
                await Release.Task.WaitAsync(token);
            if (FailId == id)
                throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Transient);
            if (Noop)
                return false;
            var deleted = await inner.DeleteExpiredAsync(id, cutoff, token);
            if (FailAfterDelete)
                throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Transient);
            return deleted;
        }
    }
}
