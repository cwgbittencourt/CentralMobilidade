using System;
using System.Linq;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Persistence;

public sealed class ControlPersistenceTests
{
    private static TelemetryControlDbContext Create() => new(new DbContextOptionsBuilder<TelemetryControlDbContext>()
        .UseNpgsql("Host=localhost;Database=model_only;Username=model_only").Options);

    [Fact]
    public void Single_store_control_entities_have_keys_and_no_raw_telemetry()
    {
        using var db = Create();
        var expected = new[] { typeof(IngestionBatch), typeof(LineProcessingResult), typeof(QuarantinedRejection),
            typeof(EventIdentityGuard), typeof(EventIdentityEvidence), typeof(OperatorAccessProfile), typeof(AuditEvent) };
        db.Model.GetEntityTypes().Select(entity => entity.ClrType).OrderBy(type => type.Name)
            .ShouldBe(expected.OrderBy(type => type.Name));
        var guard = db.Model.FindEntityType(typeof(EventIdentityGuard))!;
        foreach (var removed in new[] { "Destination", "StorageLocation", "PromotionOwner", "PromotionLeaseUntil" })
            guard.FindProperty(removed).ShouldBeNull();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            entity.FindPrimaryKey().ShouldNotBeNull();
            entity.GetProperties().Any(p => p.Name is "RawContent" or "Latitude" or "Cpf").ShouldBeFalse();
        }
        db.Model.FindEntityType(typeof(EventIdentityGuard))!.FindPrimaryKey()!.Properties.Count.ShouldBe(3);
        db.Model.FindEntityType(typeof(EventIdentityEvidence))!.GetForeignKeys().ShouldBeEmpty();
        db.Model.FindEntityType(typeof(IngestionBatch))!.FindProperty("RowVersion")!.IsConcurrencyToken.ShouldBeTrue();
    }

    [Fact]
    public async Task Audit_updates_and_deletes_are_rejected_before_connecting()
    {
        await using var db = Create();
        var audit = new AuditEvent { EventId = Guid.NewGuid() };
        db.Attach(audit);
        db.Entry(audit).State = EntityState.Modified;
        await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.Entry(audit).State = EntityState.Deleted;
        await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Non_utc_timestamps_are_rejected_before_connecting()
    {
        await using var db = Create();
        db.Batches.Add(new IngestionBatch { BatchId = Guid.NewGuid(), ReceivedAt = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(-3)) });
        await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
