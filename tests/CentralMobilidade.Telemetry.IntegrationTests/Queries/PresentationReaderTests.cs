using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Presentation;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Auditing;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Infrastructure.Presentation;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Queries;

public sealed class PresentationReaderTests(HistoricalTripServer server) : IClassFixture<HistoricalTripServer>
{
    private InfluxPresentationReader Reader => new(server.Client, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["InfluxDb:Queryable:Url"] = server.Url,
        ["InfluxDb:Queryable:Database"] = "queryable",
        ["InfluxDb:Queryable:Token"] = "test-only-token"
    }).Build());

    [Theory]
    [InlineData(2)]
    [InlineData(10000)]
    public async Task Closed_trip_query_reads_old_Parquet_in_order_with_parameters_and_optional_columns(int limit)
    {
        var rows = await Reader.TripAsync(new("unregistered-company"), new("trip-vehicle"), server.Start, server.Start.AddMinutes(20), limit, default);
        rows.Count.ShouldBe(Math.Min(4, limit));
        rows.Select(row => row.Latitude).ShouldBe(Enumerable.Range(1, rows.Count).Select(i => (double)i));
        rows.ShouldAllBe(row => row.EmpresaId == "unregistered-company" && row.VehicleId == "trip-vehicle" && row.DriverId == null);
        rows.Select(row => row.EventTime).ShouldBe(rows.Select(row => row.EventTime).Order());
    }

    [Fact]
    public async Task Latest_is_scoped_and_cursor_is_applied_before_paging()
    {
        var rows = await Reader.LatestAsync(new("unregistered-company"), new("trip-vehicle"), null, 2, default);
        rows.Single().EventTime.ShouldBe(server.Start.AddMinutes(20));
        (await Reader.LatestAsync(new("unregistered-company"), new("trip-vehicle"), "trip-vehicle", 2, default)).ShouldBeEmpty();
        (await Reader.LatestAsync(new("unknown' OR 1=1 --"), null, null, 2, default)).ShouldBeEmpty();
    }
}

public sealed class PresentationHealthReaderTests
{
    [Fact]
    public async Task Health_groups_received_time_by_company_excludes_replay_and_keeps_half_open_bounds()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        var factory = new Factory(fixture);
        var now = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        var batch = Guid.NewGuid();
        await using (var db = fixture.Create())
        {
            db.Batches.Add(new IngestionBatch { BatchId = batch, TraceId = "trace", ProducerId = "producer", ReceivedAt = now, ExpiresAt = now.AddDays(30) });
            var outcomes = new[] { LineOutcome.Accepted, LineOutcome.Rejected, LineOutcome.IdenticalReplay, LineOutcome.Accepted, LineOutcome.Accepted, LineOutcome.Rejected };
            for (var i = 0; i < outcomes.Length; i++)
                db.LineResults.Add(new LineProcessingResult { BatchId = batch, LineNumber = i + 1, Outcome = outcomes[i], CompanyId = i == 3 ? "other" : "company", SafeDigest = "digest", OccurredAt = i == 5 ? now.AddMinutes(2) : i == 4 ? now.AddMinutes(1) : now });
            await db.SaveChangesAsync();
        }
        var reader = new PostgresIngestionHealthReader(factory);
        var rows = await reader.ReadAsync(new("company"), now, now.AddMinutes(2), 60, 10000, default);
        rows.Count.ShouldBe(2);
        rows[0].Accepted.ShouldBe(1);
        rows[0].Rejected.ShouldBe(1);
        rows[1].Accepted.ShouldBe(1);
        rows[1].Rejected.ShouldBe(0);
        (await reader.ReadAsync(new("company"), now, now.AddTicks(1), 60, 10000, default)).Single().Accepted.ShouldBe(1);
        (await reader.ReadAsync(new("company"), now.AddTicks(1), now.AddTicks(2), 60, 10000, default)).ShouldBeEmpty();
        (await reader.ReadAsync(new("company"), now, now.AddMinutes(2), 60, 1, default)).Count.ShouldBe(1);
        (await reader.ReadAsync(new("unknown' OR 1=1 --"), now, now.AddMinutes(2), 60, 10000, default)).ShouldBeEmpty();

        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        services.AddOptions<SecurityOptions>().Configure(options => options.Profiles = [new("operator", OperatorActorKind.Human, ["driver.read"], ["company"])]);
        services.AddSingleton<IAuthenticationCredentialStore, ConfiguredSecurityStore>();
        services.AddSingleton<IOperatorAuthorizer, StoredOperatorAuthorizer>();
        services.AddTelemetryControlPersistence(fixture.Connection);
        services.AddSingleton<IDriverResolver, Resolver>();
        services.AddTelemetryPresentation(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<DriverPresentationHandler>().HandleAsync(new(new("operator", OperatorActorKind.Human), "trace"), "company", "driver", default);
        result.Value.ShouldNotBeNull().DisplayName.ShouldBe("Example driver");
        await using var verify = fixture.Create();
        var committed = await verify.AuditEvents.SingleAsync();
        committed.Action.ShouldBe(AuditAction.ResolveDriver);
        committed.Outcome.ShouldBe(AuditOutcome.Allowed);
        committed.CompanyScope.ShouldBe(["company"]);
        committed.CommittedAt.ShouldNotBe(default);
        committed.OpaqueResourceId.ShouldBeNull();
    }

    private sealed class Factory(MigratedPostgresFixture fixture) : IDbContextFactory<TelemetryControlDbContext>
    {
        public TelemetryControlDbContext CreateDbContext() => fixture.Create();
        public Task<TelemetryControlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(fixture.Create());
    }
    private sealed class Resolver : IDriverResolver
    {
        public ValueTask<DriverIdentityPresentation?> ResolveAsync(CompanyId company, DriverId driver, CancellationToken token) => ValueTask.FromResult<DriverIdentityPresentation?>(new(driver, "Example driver", "1234"));
    }
}
