using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.LineProtocol;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Idempotency;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Persistence;

public sealed class SingleTelemetryStoreMigrationTests(UpgradePostgresServer server) : IClassFixture<UpgradePostgresServer>
{
    internal const string LegacyMigration = "20261014190000_PartitionIdentityGuard";
    internal const string TargetMigration = "20261014230000_SingleTelemetryStore";
    private Task<MigratedPostgresFixture> Start(string? target = null) => MigratedPostgresFixture.StartAsync(target, server.Connection);

    [Fact]
    public async Task Fresh_schema_has_no_pending_table_or_guard_routing_columns()
    {
        await using var fixture = await Start();
        await using var db = fixture.Create();
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='public' AND table_name='pending_telemetry_promotions'").SingleAsync()).ShouldBe(0);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema='public' AND table_name='event_identity_guards' AND column_name IN ('Destination','StorageLocation','PromotionOwner','PromotionLeaseUntil')").SingleAsync()).ShouldBe(0);
        db.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task Runtime_grant_template_applies_to_new_schema_without_granting_ddl_audit_mutation_or_truncate()
    {
        await using var fixture = await Start();
        await using var db = fixture.Create();
        await db.Database.OpenConnectionAsync();
        using var quoting = new NpgsqlCommandBuilder();
        var role = quoting.QuoteIdentifier("t106_runtime_" + Guid.NewGuid().ToString("N"));
        await ExecuteLiteral(db, "CREATE ROLE " + role + " NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE");
        try
        {
            var template = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Upgrade","runtime-grants.sql"));
            var sql = string.Join("\n",template.Split('\n').Where(line => !line.TrimStart().StartsWith('\\')))
                .Replace(":\"runtime_role\"",role,StringComparison.Ordinal);
            await using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
            await ExecuteLiteral(db, "SET ROLE " + role);
            (await db.IdentityGuards.CountAsync()).ShouldBe(0);
            (await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audit_events"))).SqlState.ShouldBe("42501");
            (await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE event_identity_guards"))).SqlState.ShouldBe("42501");
            (await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("ALTER TABLE event_identity_guards ADD COLUMN forbidden integer"))).SqlState.ShouldBe("42501");
        }
        finally
        {
            await ExecuteLiteral(db, "ROLLBACK; RESET ROLE; DROP OWNED BY " + role + "; DROP ROLE " + role);
        }
    }

    [Theory]
    [InlineData("PendingCommitted", "Committed")]
    [InlineData("Promoting", "Committed")]
    [InlineData("PendingCommitted", "Reserved")]
    [InlineData("PendingCommitted", "Writing")]
    public async Task Unreconciled_guard_aborts_entire_upgrade_without_mutating_legacy_data(string location, string state)
    {
        await using var fixture = await Start(LegacyMigration);
        await using var db = fixture.Create();
        await Seed(db, location, state);
        var failure = await Should.ThrowAsync<PostgresException>(() => db.Database.MigrateAsync());
        failure.SqlState.ShouldBe("23514");
        (await db.Database.GetAppliedMigrationsAsync()).ShouldNotContain(TargetMigration);
        (await db.Database.SqlQueryRaw<string>("SELECT \"StorageLocation\" AS \"Value\" FROM event_identity_guards").SingleAsync()).ShouldBe(location);
        (await db.Database.SqlQueryRaw<string>("SELECT \"State\" AS \"Value\" FROM event_identity_guards").SingleAsync()).ShouldBe(state);
        (await db.IdentityEvidence.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData("Discovered")]
    [InlineData("Copying")]
    [InlineData("Copied")]
    [InlineData("SourceDeleted")]
    [InlineData("Completed")]
    [InlineData("Retrying")]
    public async Task Promotion_rows_require_explicit_reconciliation_and_archival_even_when_marked_completed(string state)
    {
        await using var fixture = await Start(LegacyMigration);
        await using var db = fixture.Create();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO pending_telemetry_promotions
             ("PromotionId","CompanyId","SourceBatchId","RangeStart","RangeEnd","State","Attempts","ExpectedPointCount","ConfirmedPointCount","CreatedAt","UpdatedAt","RowVersion")
            VALUES ({Guid.NewGuid()},'legacy',{Guid.NewGuid()},{now},{now},{state},1,1,1,{now},{now},1)
            """);
        (await Should.ThrowAsync<PostgresException>(() => db.Database.MigrateAsync())).SqlState.ShouldBe("23514");
        (await db.Database.SqlQueryRaw<string>("SELECT \"State\" AS \"Value\" FROM pending_telemetry_promotions").SingleAsync()).ShouldBe(state);
        (await db.Database.GetAppliedMigrationsAsync()).ShouldNotContain(TargetMigration);
    }

    [Theory]
    [InlineData("PendingCommitted")]
    [InlineData("Promoting")]
    [InlineData("QueryableCommitted")]
    public async Task Verified_page_reconciliation_and_upgrade_preserve_provenance_retention_and_immutability(string location)
    {
        await using var fixture = await Start(LegacyMigration);
        await using var db = fixture.Create();
        var seed = await Seed(db, location);
        await Manifest(db, seed);
        await Reconcile(db);
        await Reconcile(db); // Repeat does not relocate twice or advance the fence again.
        (await db.Database.SqlQueryRaw<long>("SELECT \"FencingVersion\" AS \"Value\" FROM event_identity_guards").SingleAsync()).ShouldBe(location == "QueryableCommitted" ? 1L : 2L);
        await db.Database.MigrateAsync();
        db.Database.HasPendingModelChanges().ShouldBeFalse();
        var row = await db.IdentityGuards.AsNoTracking().SingleAsync();
        row.State.ShouldBe(IdentityGuardState.Committed);
        row.OwnerBatchId.ShouldBe(seed.Owner);
        row.OwnerLineNumber.ShouldBe(1);
        row.GuardExpiresAt.ShouldBe(seed.Time.AddDays(60));
        row.TelemetryExpiresAt.ShouldBe(seed.Time.AddDays(60));
        var evidence = await db.IdentityEvidence.AsNoTracking().SingleAsync();
        evidence.FirstBatchId.ShouldBe(seed.Owner);
        evidence.CanonicalFingerprint.ShouldBe(Convert.FromHexString(seed.Fingerprint));
        evidence.EvidenceExpiresAt.ShouldBe(seed.Time.AddDays(30));
        (await db.Batches.AsNoTracking().SingleAsync()).State.ShouldBe(BatchLifecycle.Accepted);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_inherits WHERE inhparent='event_identity_guards'::regclass").SingleAsync()).ShouldBe(16);
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM event_identity_guards"));
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE event_identity_guards_p0"));
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE event_identity_guards SET \"GuardExpiresAt\"=clock_timestamp()"));
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE event_identity_guards SET \"OwnerBatchId\"=gen_random_uuid(), \"FencingVersion\"=\"FencingVersion\"+1"));
        await using var services = new ServiceCollection().AddTelemetryControlPersistence(fixture.Connection).BuildServiceProvider();
        var guard = new SelectedEventIdentityGuard(services.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>(), new Clock());
        var identity = new EventIdentity(new("legacy"), new("vehicle"), EventTimestamp.FromUnixTimeMilliseconds(seed.Time.ToUnixTimeMilliseconds()));
        var now = DateTimeOffset.UtcNow;
        var request = new IdentityReservationRequest(identity, new(new BatchId(Guid.NewGuid()),1), seed.Fingerprint,
            now, now.AddMinutes(1), seed.Time.AddDays(60), seed.Time.AddDays(30));
        (await guard.AcquireOrResumeAsync(request, default)).ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(ExistingIdentityOutcome.IdenticalReplay);
        (await guard.AcquireOrResumeAsync(request with { Fingerprint = new string('b',64) }, default)).ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(ExistingIdentityOutcome.Conflict);
        (await guard.GetCommittedAsync(identity, default)).ShouldNotBeNull();
        // Nonempty downgrade cannot silently reconstruct or drop historical records.
        await Should.ThrowAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(LegacyMigration));
        (await db.Database.GetAppliedMigrationsAsync()).ShouldContain(TargetMigration);
        (await db.IdentityGuards.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData("missing-manifest")]
    [InlineData("missing-evidence")]
    [InlineData("expired-evidence")]
    [InlineData("conflicting-evidence")]
    [InlineData("conflicting-destination")]
    [InlineData("active-lease")]
    [InlineData("uncommitted")]
    [InlineData("missing-guard")]
    [InlineData("empty")]
    [InlineData("oversize")]
    [InlineData("second-row-conflict")]
    public async Task Reconciliation_fails_closed_and_rolls_back_the_whole_page(string scenario)
    {
        await using var fixture = await Start(LegacyMigration);
        await using var db = fixture.Create();
        var seed = await Seed(db, scenario == "active-lease" ? "Promoting" : "PendingCommitted", scenario == "uncommitted" ? "Writing" : "Committed");
        if (scenario != "missing-manifest") await Manifest(db, seed);
        else await db.Database.OpenConnectionAsync();
        switch (scenario)
        {
            case "missing-evidence": await db.Database.ExecuteSqlRawAsync("DELETE FROM event_identity_evidence"); break;
            case "expired-evidence": await db.Database.ExecuteSqlRawAsync("UPDATE event_identity_evidence SET \"FirstSeenAt\"=statement_timestamp()-interval '31 days', \"EvidenceExpiresAt\"=statement_timestamp()-interval '1 day'"); break;
            case "conflicting-evidence": await db.Database.ExecuteSqlRawAsync("UPDATE event_identity_evidence SET \"CanonicalFingerprint\"=decode(repeat('b',64),'hex')"); break;
            case "conflicting-destination": await db.Database.ExecuteSqlRawAsync("UPDATE pg_temp.single_influx_verified_page SET destination_fingerprint=repeat('b',64)"); break;
            case "active-lease": await db.Database.ExecuteSqlRawAsync("UPDATE event_identity_guards SET \"PromotionLeaseUntil\"=clock_timestamp()+interval '5 minutes'"); break;
            case "missing-guard": await db.Database.ExecuteSqlRawAsync("UPDATE pg_temp.single_influx_verified_page SET vehicle_id='absent'"); break;
            case "empty": await db.Database.ExecuteSqlRawAsync("TRUNCATE pg_temp.single_influx_verified_page"); break;
            case "oversize": await db.Database.ExecuteSqlRawAsync("INSERT INTO pg_temp.single_influx_verified_page SELECT company_id, 'other-'||n, event_time, source_fingerprint,destination_fingerprint FROM pg_temp.single_influx_verified_page CROSS JOIN generate_series(1,500) n"); break;
            case "second-row-conflict": await db.Database.ExecuteSqlRawAsync("INSERT INTO pg_temp.single_influx_verified_page SELECT company_id,'zz-absent',event_time,source_fingerprint,destination_fingerprint FROM pg_temp.single_influx_verified_page"); break;
        }
        (await Should.ThrowAsync<PostgresException>(() => Reconcile(db))).SqlState.ShouldBe("23514");
        await db.Database.ExecuteSqlRawAsync("ROLLBACK");
        (await db.Database.SqlQueryRaw<string>("SELECT \"Destination\" AS \"Value\" FROM event_identity_guards").SingleAsync()).ShouldBe("Pending");
        (await db.Database.SqlQueryRaw<long>("SELECT \"FencingVersion\" AS \"Value\" FROM event_identity_guards").SingleAsync()).ShouldBe(1);
    }

    private static async Task<SeededIdentity> Seed(TelemetryControlDbContext db, string location, string state = "Committed")
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var owner = Guid.NewGuid();
        var line = $"telemetria,empresa_id=legacy,veiculo_id=vehicle latitude=1.0,longitude=2.0,velocidade=0.0,rpm=0i,ignicao=false {time.ToUnixTimeMilliseconds()}";
        var fingerprint = new TelemetryLineProtocolV1Parser().Parse(System.Text.Encoding.UTF8.GetBytes(line)).Event.ShouldNotBeNull().Fingerprint;
        // Seed the historical schema without columns introduced by later migrations.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ingestion_batches ("BatchId", "TraceId", "ProducerId", "ContractVersion", "State", "ReceivedAt", "ExpiresAt", "CompressedBytes", "Accepted", "Rejected", "Replayed", "RetryCount", "CompanyScope", "CompanyScopeComplete", "RowVersion")
            VALUES ({owner}, '', 'internal', 'v1', 'Accepted', {time}, {time.AddDays(30)}, 0, 0, 0, 0, 0, ARRAY[]::text[], FALSE, 1)
            """);
        var destination = location == "QueryableCommitted" ? "Queryable" : "Pending";
        DateTimeOffset? lease = state == "Committed" ? null : time.AddMinutes(5);
        Guid? promotionOwner = location == "Promoting" ? Guid.NewGuid() : null;
        DateTimeOffset? promotionUntil = location == "Promoting" ? time.AddMinutes(-1) : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO event_identity_guards ("CompanyId","VehicleId","EventTimestamp","State","OwnerBatchId","OwnerLineNumber","LeaseUntil","FencingVersion","Destination","StorageLocation","GuardExpiresAt","TelemetryExpiresAt","PromotionOwner","PromotionLeaseUntil")
            VALUES ('legacy','vehicle',{time},{state},{owner},1,{lease},1,{destination},{location},{time.AddDays(60)},{time.AddDays(60)},{promotionOwner},{promotionUntil});
            INSERT INTO event_identity_evidence ("CompanyId","VehicleId","EventTimestamp","CanonicalFingerprint","FirstBatchId","FirstSeenAt","EvidenceExpiresAt")
            VALUES ('legacy','vehicle',{time},{Convert.FromHexString(fingerprint)},{owner},{time},{time.AddDays(30)});
            """);
        return new(owner,time,fingerprint);
    }

    private static async Task Manifest(TelemetryControlDbContext db, SeededIdentity seed)
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE single_influx_verified_page(company_id text NOT NULL,vehicle_id text NOT NULL,event_time timestamptz NOT NULL,source_fingerprint text NOT NULL,destination_fingerprint text NOT NULL,PRIMARY KEY(company_id,vehicle_id,event_time))");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO pg_temp.single_influx_verified_page VALUES ('legacy','vehicle',{seed.Time},{seed.Fingerprint},{seed.Fingerprint})");
    }
    private static async Task ExecuteLiteral(TelemetryControlDbContext db, string sql)
    {
        // Role identifiers are generated UUIDs quoted with NpgsqlCommandBuilder, never user input.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Reconcile(TelemetryControlDbContext db)
    {
        // This is literal psql-compatible SQL, not an EF composite format string ({64} is a regex).
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Upgrade","reconcile-single-influx-page.sql"));
        command.CommandTimeout = 130;
        await command.ExecuteNonQueryAsync();
    }
    private sealed record SeededIdentity(Guid Owner, DateTimeOffset Time, string Fingerprint);
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
}

public sealed class UpgradePostgresServer : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17.11-bookworm")
        .WithCommand("-c","fsync=on","-c","synchronous_commit=on").Build();
    public string Connection => container.GetConnectionString();
    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}
