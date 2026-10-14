using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Health;

public sealed partial class TelemetryHealthTests
{
    [Fact]
    public async Task Migrated_postgres_fixture_enables_durable_disk_commits()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        await using var connection = new NpgsqlConnection(fixture.Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_setting('fsync'), current_setting('synchronous_commit')", connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(0).ShouldBe("on", "The healthy fixture must use durable PostgreSQL settings");
        reader.GetString(1).ShouldBe("on");
    }

    [Fact]
    public async Task Api_liveness_stays_healthy_when_dependencies_are_missing_and_responses_are_private()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        using var logs = new StringWriter();
        builder.Services.AddPrivacyLogging(TelemetryServiceRole.Api, logs);
        builder.Services.AddTelemetryHealthChecks(builder.Configuration, TelemetryServiceRole.Api);
        await using var app = builder.Build();
        app.MapTelemetryHealthEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ready.Content.ReadAsStringAsync()).ShouldBe("{\"status\":\"unhealthy\"}");
        ready.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await client.PostAsync("/health/live", null)).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        await app.StopAsync();
    }

    [Fact]
    public async Task Api_can_accept_durable_intake_even_when_downstream_worker_and_influx_are_unavailable()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddTelemetryControlPersistence(fixture.Connection);
        builder.Services.AddSingleton<IDurablePublicationProbe>(new PublicationProbe { Status = new(true, true, true, true) });
        builder.Services.AddTelemetryHealthChecks(builder.Configuration, TelemetryServiceRole.Api);
        await using var app = builder.Build();
        app.MapTelemetryHealthEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/dependencies")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        await using var db = fixture.Create();
        (await db.Batches.CountAsync()).ShouldBe(0); // Probes create neither business batches nor telemetry.
        await app.StopAsync();
    }

    [Fact]
    public async Task Worker_listener_reports_consumer_freshness_without_refreshing_it_on_health_requests()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["Observability:WorkerMetricsUrl"] = "http://127.0.0.1:0";
        builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Worker);
        builder.Services.AddTelemetryHealthChecks(builder.Configuration, TelemetryServiceRole.Worker);
        using var host = builder.Build();
        await host.StartAsync();
        var endpoint = host.Services.GetServices<IHostedService>().OfType<WorkerMetricsEndpoint>().Single();
        using var client = new HttpClient { BaseAddress = new Uri(endpoint.ListenUrl!) };
        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/worker")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync("/health/worker")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        host.Services.GetRequiredService<WorkerProgress>().MarkHeartbeat();
        (await client.GetAsync("/health/worker")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        await host.StopAsync();
    }

    [Fact]
    public async Task Postgres_rejects_readonly_or_nondurable_sessions_without_changing_schema_or_data()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        foreach (var setting in new[] { "-c default_transaction_read_only=on", "-c synchronous_commit=off" })
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.Connection) { Options = setting };
            var services = new ServiceCollection().AddTelemetryControlPersistence(connection.ConnectionString);
            await using var provider = services.BuildServiceProvider();
            var check = new PostgresHealthCheck(provider.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>(), new());
            (await check.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
        }
    }

    [Fact]
    public async Task Configured_worker_is_ready_only_while_storage_is_available_and_consumer_is_fresh()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["Observability:WorkerMetricsUrl"] = "http://127.0.0.1:0";
        foreach (var store in new[] { "Queryable", "Pending" })
        {
            builder.Configuration[$"InfluxDb:{store}:Url"] = "http://influx.test";
            builder.Configuration[$"InfluxDb:{store}:Token"] = "TEST_TOKEN";
        }
        var clock = new TestTimeProvider();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddTelemetryControlPersistence(fixture.Connection);
        builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Worker);
        builder.Services.AddTelemetryHealthChecks(builder.Configuration, TelemetryServiceRole.Worker);
        builder.Services.AddHttpClient("telemetry-health").ConfigurePrimaryHttpMessageHandler(() => new HealthHandler());
        using var host = builder.Build();
        await host.StartAsync();
        host.Services.GetRequiredService<WorkerProgress>().MarkHeartbeat();
        var endpoint = host.Services.GetServices<IHostedService>().OfType<WorkerMetricsEndpoint>().Single();
        using var client = new HttpClient { BaseAddress = new Uri(endpoint.ListenUrl!) };
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        clock.Ticks += TimeSpan.FromSeconds(31).Ticks;
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await host.StopAsync();
    }

    [Fact]
    public async Task Degraded_checks_and_private_exceptions_never_become_ready_or_leak_details()
    {
        await using var fixture = await MigratedPostgresFixture.StartAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddTelemetryControlPersistence(fixture.Connection);
        builder.Services.AddSingleton<IDurablePublicationProbe>(new PublicationProbe { Status = new(true, true, true, true) });
        builder.Services.AddTelemetryHealthChecks(builder.Configuration, TelemetryServiceRole.Api);
        builder.Services.AddHealthChecks().AddCheck("private_failure", () => HealthCheckResult.Degraded("SEEDED_SECRET"), tags: ["ready"]);
        await using var app = builder.Build();
        app.MapTelemetryHealthEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/health/ready");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldBe("{\"status\":\"unhealthy\"}");
        await app.StopAsync();
    }

    [Fact]
    public async Task Influx_probe_uses_bearer_health_get_and_never_surfaces_dependency_content()
    {
        using var handler = new HealthHandler();
        using var client = new HttpClient(handler);
        var check = new InfluxHealthCheck(client, "http://influx.test", "SEEDED_BEARER", new());
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Healthy);
        handler.Method.ShouldBe(HttpMethod.Get);
        handler.Path.ShouldBe("/health");
        handler.Authorization.ShouldBe("Bearer SEEDED_BEARER");
        handler.Code = HttpStatusCode.ServiceUnavailable;
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description!.ShouldNotContain("SEEDED");
        result.Exception.ShouldBeNull();
    }

    [Theory]
    [InlineData("{\"status\":\"healthy\"}", HealthStatus.Healthy)]
    [InlineData("{\"status\":\"unhealthy\",\"secret\":\"SEEDED_CPF\"}", HealthStatus.Unhealthy)]
    [InlineData("SEEDED_PRIVATE_HTML", HealthStatus.Unhealthy)]
    public async Task Remote_worker_requires_a_valid_bounded_healthy_response(string body, HealthStatus expected)
    {
        using var handler = new HealthHandler { Body = body };
        using var client = new HttpClient(handler);
        var check = new RemoteWorkerHealthCheck(client, new()
        {
            WorkerHealthUrl = "http://worker.test"
        });
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.ShouldBe(expected);
        result.Exception.ShouldBeNull();
        handler.Path.ShouldBe("/health/worker");
    }

    [Fact]
    public async Task Oversized_remote_status_and_noncooperative_publication_timeout_fail_closed()
    {
        using var handler = new HealthHandler { Body = new string('x', 300) };
        using var client = new HttpClient(handler);
        var result = await new RemoteWorkerHealthCheck(client, new()
        {
            WorkerHealthUrl = "http://worker.test"
        }).CheckHealthAsync(new HealthCheckContext());
        result.Status.ShouldBe(HealthStatus.Unhealthy);
        var check = new DurablePublicationHealthCheck(new HangingPublicationProbe(), new()
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(100)
        });
        var before = System.Diagnostics.Stopwatch.StartNew();
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
        before.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("Health:ProbeTimeout", "00:00:00")]
    [InlineData("Health:WorkerMaxAge", "00:20:00")]
    [InlineData("Health:WorkerHealthUrl", "http://user:SEEDED_SECRET@worker.test")]
    [InlineData("Health:Unknown", "SEEDED_PRIVATE")]
    public void Invalid_health_configuration_fails_startup_without_echoing_values(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[key] = value;
        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddTelemetryHealthChecks(config, TelemetryServiceRole.Api))
            .Message.ShouldNotContain(value);
    }

    private sealed class HangingPublicationProbe : IDurablePublicationProbe
    {
        public ValueTask<DurablePublicationStatus> ProbeAsync(CancellationToken cancellationToken) =>
            new(new TaskCompletionSource<DurablePublicationStatus>().Task);
    }
    private sealed class HealthHandler : HttpMessageHandler
    {
        internal HttpStatusCode Code = HttpStatusCode.OK;
        internal string Body = "{\"status\":\"healthy\",\"private\":\"SEEDED_PRIVATE\"}";
        internal HttpMethod? Method;
        internal string? Path;
        internal string? Authorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(Code) { Content = new StringContent(Body) });
        }
    }
}
