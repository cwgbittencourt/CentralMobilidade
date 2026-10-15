using System;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;
namespace CentralMobilidade.Telemetry.IntegrationTests.Persistence;

public sealed partial class ControlMigrationTests
{
    [Fact]
    public async Task Replay_migration_preserves_existing_batch_state_and_original_processing_time()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync("20261014230000_SingleTelemetryStore");
        await using var db = fixture.Create();
        var id = Guid.NewGuid();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ingestion_batches ("BatchId", "TraceId", "ProducerId", "ContractVersion", "State", "ReceivedAt", "ExpiresAt", "ProcessingStartedAt", "CompressedBytes", "Accepted", "Rejected", "Replayed", "RetryCount", "CompanyScope", "CompanyScopeComplete", "RowVersion")
            VALUES ({id}, '', 'internal', 'v1', 'Processing', {at}, {at.AddDays(30)}, {at.AddSeconds(1)}, 0, 3, 0, 0, 1, ARRAY['company-a']::text[], FALSE, 7)
            """);
        await db.Database.MigrateAsync();
        var row = await db.Batches.SingleAsync();
        row.BatchId.ShouldBe(id);
        row.State.ShouldBe(BatchLifecycle.Processing);
        row.ProcessingStartedAt.ShouldBe(at.AddSeconds(1));
        row.LastTransitionAt.ShouldBeNull(); // An old resume timestamp cannot be safely reconstructed.
        row.Accepted.ShouldBe(3);
        row.RetryCount.ShouldBe(1);
        row.RowVersion.ShouldBe(7);
    }
}
