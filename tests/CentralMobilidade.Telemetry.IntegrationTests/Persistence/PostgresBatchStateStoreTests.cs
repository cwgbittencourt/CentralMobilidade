using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Persistence;

public sealed class PostgresBatchStateStoreTests
{
    [Fact]
    public async Task Concurrent_creation_is_transactional_and_exact_replay_is_idempotent()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        await using var services = new ServiceCollection().AddTelemetryControlPersistence(fixture.Connection).BuildServiceProvider();
        var store = services.GetRequiredService<IBatchStateStore>();
        var metadata = Envelope();
        var expiresAt = metadata.ReceivedAt.AddDays(30);

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => store.CreateAsync(metadata, expiresAt, CancellationToken.None).AsTask()));

        snapshots.ShouldAllBe(snapshot => snapshot.BatchId == new BatchId(metadata.BatchId));
        snapshots.ShouldAllBe(snapshot => snapshot.State == BatchLifecycle.Receiving && snapshot.Version == 1);
        await using var db = fixture.Create();
        (await db.Batches.CountAsync(e => e.BatchId == metadata.BatchId)).ShouldBe(1);

        await Should.ThrowAsync<InvalidOperationException>(() => store.CreateAsync(
            metadata with
            {
                ProducerId = "different-producer"
            }, expiresAt, CancellationToken.None).AsTask());
        await Should.ThrowAsync<ValidationException>(() => store.CreateAsync(
            metadata with
            {
                BatchId = Guid.NewGuid()
            }, expiresAt.AddSeconds(1), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Acceptance_uses_compare_and_swap_and_exact_replay_does_not_advance_version_twice()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        await using var services = new ServiceCollection().AddTelemetryControlPersistence(fixture.Connection).BuildServiceProvider();
        var store = services.GetRequiredService<IBatchStateStore>();
        var metadata = Envelope();
        var id = new BatchId(metadata.BatchId);
        var receiving = await store.CreateAsync(metadata, metadata.ReceivedAt.AddDays(30), CancellationToken.None);
        var publishingAt = metadata.ReceivedAt.AddSeconds(1);
        var publishing = new BatchTransition(BatchLifecycle.Publishing, publishingAt);

        (await store.TryTransitionAsync(id, receiving.Version, publishing, CancellationToken.None)).ShouldBeTrue();
        (await store.TryTransitionAsync(id, receiving.Version, publishing, CancellationToken.None)).ShouldBeTrue();
        (await store.GetAsync(id, CancellationToken.None))!.Version.ShouldBe(2);

        var durableAt = publishingAt.AddSeconds(1);
        var accepted = new BatchTransition(BatchLifecycle.Accepted, durableAt);
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => store.TryTransitionAsync(id, 2, accepted, CancellationToken.None).AsTask()));
        results.ShouldAllBe(result => result);

        var snapshot = await store.GetAsync(id, CancellationToken.None);
        snapshot.ShouldNotBeNull();
        snapshot.State.ShouldBe(BatchLifecycle.Accepted);
        snapshot.Version.ShouldBe(3);
        (await store.TryTransitionAsync(id, 2,
            accepted with
            {
                OccurredAt = durableAt.AddMilliseconds(1)
            }, CancellationToken.None)).ShouldBeFalse();
        (await store.TryTransitionAsync(id, 3,
            new BatchTransition(BatchLifecycle.Completed, durableAt.AddSeconds(1)), CancellationToken.None)).ShouldBeFalse();
        (await store.GetAsync(id, CancellationToken.None))!.Version.ShouldBe(3);
    }

    private static TelemetryBatchEnvelope Envelope()
    {
        var receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return new("1", Guid.NewGuid(), "0123456789abcdef0123456789abcdef", "batch-store-test", receivedAt,
            "text/plain; charset=utf-8", "gzip", 128, new string('a', 64), "v1");
    }
}
