using System;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CentralMobilidade.Telemetry.IntegrationTests.Persistence;

internal sealed class MigratedPostgresFixture : IAsyncDisposable
{
    private readonly PostgreSqlContainer? container;
    private MigratedPostgresFixture(string connection, PostgreSqlContainer? container)
    {
        Connection = connection;
        this.container = container;
    }
    internal string Connection
    {
        get;
    }
    internal TelemetryControlDbContext Create() => new(new DbContextOptionsBuilder<TelemetryControlDbContext>().UseNpgsql(Connection).Options);

    internal static async Task<MigratedPostgresFixture> StartAsync(string? targetMigration = null, string? connectionOverride = null)
    {
        var connection = connectionOverride ?? Environment.GetEnvironmentVariable("TELEMETRY_TEST_POSTGRES");
        PostgreSqlContainer? container = null;
        if (connection is null)
        {
            // Override Testcontainers' speed-oriented fsync=off default: readiness requires durability.
            container = new PostgreSqlBuilder("postgres:17.11-bookworm")
                .WithCommand("-c", "fsync=on", "-c", "synchronous_commit=on")
                .Build();
            await container.StartAsync();
            connection = container.GetConnectionString();
        }
        var settings = new NpgsqlConnectionStringBuilder(connection) { Database = "t019_" + Guid.NewGuid().ToString("N") };
        var fixture = new MigratedPostgresFixture(settings.ConnectionString, container);
        try
        {
            await using var db = fixture.Create();
            await db.GetService<IMigrator>().MigrateAsync(targetMigration);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (container is not null)
            await container.DisposeAsync();
        // External override databases are isolated by UUID and preserved in the validation volume.
    }
}
