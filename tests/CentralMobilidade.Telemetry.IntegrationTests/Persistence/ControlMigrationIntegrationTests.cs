using System;
using System.Linq;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Persistence;

public sealed partial class ControlMigrationTests
{
    private static Task<int> RoleSqlAsync(TelemetryControlDbContext db, string sql, string role)
    {
        using var identifiers = new NpgsqlCommandBuilder();
        return db.Database.ExecuteSqlRawAsync(sql.Replace("{role}", identifiers.QuoteIdentifier(role), StringComparison.Ordinal));
    }
    private static EventIdentityGuard Guard(string company, Guid owner, DateTimeOffset now) => new()
    {
        CompanyId = company,
        VehicleId = "vehicle",
        EventTimestamp = now,
        OwnerBatchId = owner,
        OwnerLineNumber = 1,
        FencingVersion = 1,
        LeaseUntil = now.AddMinutes(10),
        TelemetryExpiresAt = now.AddDays(60),
        GuardExpiresAt = now.AddDays(60)
    };

    [Fact]
    public async Task Migration_reapplies_and_empty_database_can_roll_back_and_reapply()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        await using var db = fixture.Create();
        await db.Database.MigrateAsync();
        (await db.Database.GetAppliedMigrationsAsync()).Count().ShouldBe(4);
        var indexes = await db.Database.SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'").ToListAsync();
        indexes.ShouldContain("IX_event_identity_guards_GuardExpiresAt");
        indexes.ShouldContain("IX_event_identity_evidence_EvidenceExpiresAt");
        indexes.ShouldContain("IX_ingestion_batches_ExpiresAt");
        indexes.ShouldContain("IX_quarantined_rejections_DeleteAfter");
        await db.GetService<IMigrator>().MigrateAsync("0"); // This fixture is empty and isolated.
        var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        await db.Database.ExecuteSqlRawAsync(script);
        await db.Database.ExecuteSqlRawAsync(script);
        db.Database.HasPendingModelChanges().ShouldBeFalse();
        (await db.Database.GetAppliedMigrationsAsync()).Count().ShouldBe(4);
    }

    [Fact]
    public async Task Unique_identity_expired_takeover_fencing_and_committed_retention_are_enforced()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var owner = Guid.NewGuid();
        var winners = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var contender = fixture.Create();
            contender.IdentityGuards.Add(Guard("company", owner, now));
            try
            {
                await contender.SaveChangesAsync();
                return 1;
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" }) { return 0; }
        }));
        winners.Sum().ShouldBe(1);
        await using var db = fixture.Create();
        var other = Guid.NewGuid();
        var blocked = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE event_identity_guards SET "OwnerBatchId" = {other}, "FencingVersion" = 2
            WHERE "CompanyId" = 'company'
            """));
        blocked.SqlState.ShouldBe("23514");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE event_identity_guards SET "LeaseUntil" = {now.AddMinutes(-1)} WHERE "CompanyId" = 'company'
            """);
        var takeovers = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var contender = fixture.Create();
            var nextOwner = Guid.NewGuid();
            return await contender.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE event_identity_guards SET "OwnerBatchId" = {nextOwner}, "FencingVersion" = 2, "LeaseUntil" = {now.AddMinutes(10)}
                WHERE "CompanyId" = 'company' AND "FencingVersion" = 1
                """);
        }));
        takeovers.Sum().ShouldBe(1);
        (await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE event_identity_guards SET "State" = 'Writing' WHERE "CompanyId" = 'company'
                AND "OwnerBatchId" = {owner} AND "FencingVersion" = 1
            """)).ShouldBe(0);
        await db.Database.ExecuteSqlRawAsync("UPDATE event_identity_guards SET \"State\" = 'Writing' WHERE \"CompanyId\" = 'company'");
        // The same fenced owner can renew/resume an expired Writing lease without reopening the identity.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE event_identity_guards SET "LeaseUntil" = {now.AddMinutes(-1)} WHERE "CompanyId" = 'company'
            """);
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE event_identity_guards SET \"State\" = 'Committed', \"LeaseUntil\" = NULL WHERE \"CompanyId\" = 'company'"));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE event_identity_guards SET "LeaseUntil" = {now.AddMinutes(10)} WHERE "CompanyId" = 'company' AND "FencingVersion" = 2
            """);
        await db.Database.ExecuteSqlRawAsync("UPDATE event_identity_guards SET \"State\" = 'Committed', \"LeaseUntil\" = NULL WHERE \"CompanyId\" = 'company'");
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM event_identity_guards WHERE \"CompanyId\" = 'company'"));
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE event_identity_guards SET \"State\" = 'Reserved', \"LeaseUntil\" = now() WHERE \"CompanyId\" = 'company'"));
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE event_identity_guards"));
        // Long production retention is representable without hard-coding the local 60-day window.
        var longGuard = Guard("five-years", owner, now);
        longGuard.TelemetryExpiresAt = now.AddYears(5);
        longGuard.GuardExpiresAt = now.AddYears(5);
        db.IdentityGuards.Add(longGuard);
        await db.SaveChangesAsync();
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE event_identity_guards SET \"GuardExpiresAt\" = now() WHERE \"CompanyId\" = 'five-years'"));
        var expired = Guard("expired", owner, now.AddDays(-61));
        expired.State = IdentityGuardState.Committed;
        expired.LeaseUntil = null;
        db.IdentityGuards.Add(expired);
        await db.SaveChangesAsync();
        (await db.Database.ExecuteSqlRawAsync("DELETE FROM event_identity_guards WHERE \"CompanyId\" = 'expired'")).ShouldBe(1);
    }

    [Fact]
    public async Task Audit_cannot_be_updated_deleted_or_truncated_and_runtime_role_cannot_change_schema()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        await using var db = fixture.Create();
        foreach (var statement in new[] { "UPDATE audit_events SET \"TraceId\" = 'changed'", "DELETE FROM audit_events", "TRUNCATE audit_events" })
            (await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(statement))).SqlState.ShouldBe("42501");
        await Should.ThrowAsync<PostgresException>(() => db.AuditEvents.ExecuteDeleteAsync());
        // SECURITY INVOKER/ALWAYS trigger also runs under replication mode, unlike an ordinary trigger.
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE audit_events"));
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = origin");
        var role = "t019_runtime_" + Guid.NewGuid().ToString("N"); // Identifier generated by this test, not user input.
        await RoleSqlAsync(db, "CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE", role);
        try
        {
            await RoleSqlAsync(db, "GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT, INSERT ON audit_events TO {role}", role);
            await RoleSqlAsync(db, "SET ROLE {role}", role);
            db.AuditEvents.Add(new AuditEvent
            {
                EventId = Guid.NewGuid(),
                ActorId = "runtime-test",
                TraceId = "runtime-test",
                OccurredAt = DateTimeOffset.UtcNow,
                Action = AuditAction.ReadTelemetry,
                ResourceCategory = AuditResourceCategory.Telemetry
            });
            await db.SaveChangesAsync();
            await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("ALTER TABLE audit_events DISABLE TRIGGER audit_events_no_mutation"));
            await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events"));
            await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audit_events"));
            await db.Database.ExecuteSqlRawAsync("RESET ROLE");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("RESET ROLE");
            await RoleSqlAsync(db, "DROP OWNED BY {role}; DROP ROLE {role}", role);
        }
    }

    [Fact]
    public async Task Profiles_require_explicit_scopes_and_status_retention_cannot_exceed_thirty_days()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        await using var db = fixture.Create();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var profile = new CentralMobilidade.Telemetry.Infrastructure.Persistence.OperatorAccessProfile
        {
            ActorId = "viewer",
            Kind = OperatorActorKind.ViewerPresentationService,
            Enabled = true,
            GrafanaOrganizationId = "org",
            CompanyIds = ["company"],
            Permissions = ["telemetry.read"]
        };
        db.OperatorProfiles.Add(profile);
        await db.SaveChangesAsync();
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE operator_access_profiles SET \"Permissions\" = ARRAY['cross_company.read'], \"RowVersion\" = \"RowVersion\" + 1"));
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE operator_access_profiles SET \"GrafanaOrganizationId\" = NULL, \"RowVersion\" = \"RowVersion\" + 1"));
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE operator_access_profiles SET \"CompanyIds\" = ARRAY[]::text[], \"RowVersion\" = \"RowVersion\" + 1"));
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE operator_access_profiles SET \"Kind\" = 'Producer', \"RowVersion\" = \"RowVersion\" + 1"));
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE operator_access_profiles SET \"Permissions\" = ARRAY['unknown.permission'], \"RowVersion\" = \"RowVersion\" + 1"));
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE operator_access_profiles SET \"Enabled\" = false"));
        var batch = new IngestionBatch { BatchId = Guid.NewGuid(), ReceivedAt = now, ExpiresAt = now.AddDays(30) };
        db.Batches.Add(batch);
        await db.SaveChangesAsync();
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ingestion_batches SET "ExpiresAt" = {now.AddDays(31)}, "RowVersion" = "RowVersion" + 1
            WHERE "BatchId" = {batch.BatchId}
            """));
        db.QuarantinedRejections.Add(new QuarantinedRejection
        {
            QuarantineId = Guid.NewGuid(),
            BatchId = batch.BatchId,
            LineNumber = 1,
            CreatedAt = now,
            DeleteAfter = now.AddDays(31)
        });
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
