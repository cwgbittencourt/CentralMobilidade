using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Persistence;

public sealed class PostgresAuditWriterTests
{
    [Fact]
    public async Task PostgreSql_persists_idempotent_audit_and_rejects_collisions_and_stale_versions()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        var services = new ServiceCollection().AddTelemetryControlPersistence(fixture.Connection).BuildServiceProvider();
        await using var provider = services;
        var factory = services.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>();
        await using (var setup = await factory.CreateDbContextAsync())
            await setup.Database.MigrateAsync();
        var writer = services.GetRequiredService<IAuditWriter>();
        var record = new AuditRecord(Guid.NewGuid(), new OperatorActor("operator-test", OperatorActorKind.Human),
            AuditAction.ReadQuarantineContent, AuditResourceCategory.Quarantine, Guid.NewGuid().ToString(),
            [new CompanyId("b"), new CompanyId("a")], DateTimeOffset.UtcNow, AuditOutcome.Denied, "audit-test-trace");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => writer.AppendAsync(record, CancellationToken.None).AsTask()));
        results.Distinct().Count().ShouldBe(1);
        results[0].CommittedAt.Offset.ShouldBe(TimeSpan.Zero);
        await Should.ThrowAsync<InvalidOperationException>(() => writer.AppendAsync(record with { Outcome = AuditOutcome.Allowed }, CancellationToken.None).AsTask());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => writer.AppendAsync(record with { EventId = Guid.NewGuid() }, canceled.Token).AsTask());
        await using var db = await factory.CreateDbContextAsync();
        (await db.AuditEvents.CountAsync(e => e.EventId == record.EventId)).ShouldBe(1);
        (await db.AuditEvents.SingleAsync(e => e.EventId == record.EventId)).CompanyScope.ShouldBe(["a", "b"]);
        var batch = new IngestionBatch
        {
            BatchId = Guid.NewGuid(),
            ProducerId = "producer",
            TraceId = "trace",
            ReceivedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(29)
        };
        db.Batches.Add(batch);
        await db.SaveChangesAsync();
        await using var stale = await factory.CreateDbContextAsync();
        var old = await stale.Batches.SingleAsync(e => e.BatchId == batch.BatchId);
        batch.Accepted = 1;
        await db.SaveChangesAsync();
        old.Accepted = 2;
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        db.LineResults.Add(new LineProcessingResult
        {
            BatchId = batch.BatchId,
            LineNumber = 1,
            Outcome = LineOutcome.Rejected,
            ErrorCode = "LP_INVALID_SYNTAX",
            SafeDigest = "keyed-digest",
            OccurredAt = now
        });
        db.QuarantinedRejections.Add(new QuarantinedRejection
        {
            QuarantineId = Guid.NewGuid(),
            BatchId = batch.BatchId,
            LineNumber = 1,
            ErrorCode = "LP_INVALID_SYNTAX",
            EncryptedContentLocation = "protected-reference",
            ContentDigest = "digest",
            CreatedAt = now,
            DeleteAfter = now.AddDays(30)
        });
        db.IdentityGuards.Add(new EventIdentityGuard
        {
            CompanyId = "a",
            VehicleId = "v",
            EventTimestamp = now,
            OwnerBatchId = batch.BatchId,
            OwnerLineNumber = 1,
            FencingVersion = 1,
            LeaseUntil = now.AddMinutes(1),
            GuardExpiresAt = now.AddDays(60),
            TelemetryExpiresAt = now.AddDays(60)
        });
        db.IdentityEvidence.Add(new EventIdentityEvidence
        {
            CompanyId = "a",
            VehicleId = "v",
            EventTimestamp = now,
            CanonicalFingerprint = new byte[32],
            FirstBatchId = batch.BatchId,
            FirstSeenAt = now,
            EvidenceExpiresAt = now.AddDays(30)
        });
        db.OperatorProfiles.Add(new CentralMobilidade.Telemetry.Infrastructure.Persistence.OperatorAccessProfile
        {
            ActorId = Guid.NewGuid().ToString(),
            Kind = OperatorActorKind.ViewerPresentationService,
            GrafanaOrganizationId = "org-test",
            Enabled = true,
            Permissions = ["telemetry.read"],
            CompanyIds = ["a"],
            CredentialIds = ["hashed-credential-reference"]
        });
        await db.SaveChangesAsync();
        await using (var reload = await factory.CreateDbContextAsync())
        {
            (await reload.LineResults.SingleAsync(e => e.BatchId == batch.BatchId)).LineNumber.ShouldBe(1);
            (await reload.QuarantinedRejections.SingleAsync(e => e.BatchId == batch.BatchId)).DeleteAfter.ShouldBe(now.AddDays(30));
            (await reload.IdentityGuards.SingleAsync(e => e.EventTimestamp == now)).FencingVersion.ShouldBe(1);
            var evidence = await reload.IdentityEvidence.SingleAsync(e => e.EventTimestamp == now);
            reload.IdentityEvidence.Remove(evidence);
            await reload.SaveChangesAsync();
            (await reload.IdentityGuards.AnyAsync(e => e.EventTimestamp == now)).ShouldBeTrue();
            (await reload.OperatorProfiles.SingleAsync(e => e.GrafanaOrganizationId == "org-test")).CompanyIds.ShouldBe(["a"]);
        }
        db.LineResults.Add(new LineProcessingResult { BatchId = batch.BatchId, LineNumber = 0 });
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
