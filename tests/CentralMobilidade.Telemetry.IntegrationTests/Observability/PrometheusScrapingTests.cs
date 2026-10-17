using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Observability;

[Collection("Observability")]
public sealed class PrometheusScrapingTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(directory.FullName, "CentralMobilidade.slnx")))
                directory = directory.Parent ?? throw new InvalidOperationException("Repository root missing.");
            return directory.FullName;
        }
    }

    [Fact]
    public async Task Shipped_config_passes_pinned_promtool()
    {
        await using var tool = new ContainerBuilder("prom/prometheus:v3.15.0-busybox")
            .WithResourceMapping(Path.Combine(Root, "deploy/prometheus/prometheus.yml"), "/etc/prometheus/")
            .WithEntrypoint("/bin/sh").WithCommand("-c", "sleep 120").Build();
        await tool.StartAsync();
        var check = await tool.ExecAsync(["/bin/promtool", "check", "config", "/etc/prometheus/prometheus.yml"]);
        check.ExitCode.ShouldBe(0, check.Stdout + check.Stderr);
        check.Stdout.ShouldContain("SUCCESS");
    }

    [Fact]
    public async Task Real_services_export_numeric_metrics_and_Prometheus_detects_loss_and_recovery()
    {
        await using var api = await AppAsync(TelemetryServiceRole.Api);
        await using var worker = await AppAsync(TelemetryServiceRole.Worker);
        await using var network = new NetworkBuilder().Build();
        await network.CreateAsync();
        var password = Guid.NewGuid().ToString("N");
        await using var postgres = new ContainerBuilder("postgres:17.11-bookworm")
            .WithNetwork(network).WithNetworkAliases("postgres")
            .WithEnvironment("POSTGRES_PASSWORD", password).WithEnvironment("POSTGRES_DB", "telemetry_control")
            .WithResourceMapping(Path.Combine(Root, "deploy/postgres/monitoring-role.sql"), "/")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-U", "postgres")).Build();
        await using var rabbit = new ContainerBuilder("rabbitmq:4.3.6-management-alpine")
            .WithNetwork(network).WithNetworkAliases("rabbitmq")
            .WithResourceMapping(Path.Combine(Root, "deploy/rabbitmq/enabled_plugins"), "/etc/rabbitmq/")
            .WithResourceMapping(Path.Combine(Root, "deploy/rabbitmq/rabbitmq.conf"), "/etc/rabbitmq/")
            .WithPortBinding(15692, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(15692).ForPath("/metrics"))).Build();
        await using var influx = new ContainerBuilder("influxdb:3.11.6-core")
            .WithNetwork(network).WithNetworkAliases("influxdb").WithPortBinding(8181, true)
            .WithEntrypoint("sh", "-ec")
            .WithCommand("influxdb3 create token --admin --offline --name t082 --output-file /tmp/admin.json >/dev/null; exec influxdb3 serve --node-id t082 --object-store memory --admin-token-file /tmp/admin.json --disable-authz health,ping,metrics")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8181).ForPath("/health"))).Build();
        // Isolated collector smoke test: no host PID/root mount or claim about Windows disks.
        await using var node = new ContainerBuilder("prom/node-exporter:v1.12.1")
            .WithNetwork(network).WithNetworkAliases("node-exporter")
            .WithCommand("--collector.disable-defaults", "--collector.cpu", "--collector.meminfo", "--collector.filesystem", "--collector.diskstats", "--collector.loadavg", "--collector.time", "--collector.stat")
            .WithPortBinding(9100, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9100).ForPath("/metrics"))).Build();
        await Task.WhenAll(postgres.StartAsync(), rabbit.StartAsync(), influx.StartAsync(), node.StartAsync());
        await using var connection = new NpgsqlConnection($"Host={postgres.Hostname};Port={postgres.GetMappedPublicPort(5432)};Database=telemetry_control;Username=postgres;Password={password}");
        await connection.OpenAsync();
        var provision = await postgres.ExecAsync(["psql", "-U", "postgres", "-d", "telemetry_control", "-f", "/monitoring-role.sql"]);
        provision.ExitCode.ShouldBe(0, provision.Stderr);
        (await postgres.ExecAsync(["psql", "-U", "postgres", "-d", "telemetry_control", "-f", "/monitoring-role.sql"])).ExitCode.ShouldBe(0);
        await using (var setup = new NpgsqlCommand($"ALTER ROLE telemetry_monitor PASSWORD '{password}'; CREATE TABLE private_payload(value text);", connection))
            await setup.ExecuteNonQueryAsync();
        await using (var database = new NpgsqlCommand("CREATE DATABASE \"SEEDED_PRIVATE_DATABASE\"", connection))
            await database.ExecuteNonQueryAsync();
        await using (var monitor = new NpgsqlConnection($"Host={postgres.Hostname};Port={postgres.GetMappedPublicPort(5432)};Database=telemetry_control;Username=telemetry_monitor;Password={password}"))
        {
            await monitor.OpenAsync();
            await using var forbidden = new NpgsqlCommand("SELECT * FROM public.private_payload", monitor);
            var denied = await Should.ThrowAsync<PostgresException>(() => forbidden.ExecuteReaderAsync());
            denied.SqlState.ShouldBe("42501");
        }
        await using var exporter = new ContainerBuilder("quay.io/prometheuscommunity/postgres-exporter:v0.20.1")
            .WithNetwork(network).WithNetworkAliases("postgres-exporter")
            .WithEnvironment("DATA_SOURCE_URI", "postgres:5432/telemetry_control?sslmode=disable")
            .WithEnvironment("DATA_SOURCE_USER", "telemetry_monitor")
            .WithEnvironment("DATA_SOURCE_PASS_FILE", "/run/secrets/postgres_metrics_password")
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(password), "/run/secrets/postgres_metrics_password")
            .WithCommand("--no-collector.stat_statements", "--no-collector.stat_user_tables", "--no-collector.statio_user_tables", "--no-collector.stat_activity", "--no-collector.stat_progress_vacuum", "--no-collector.replication", "--no-collector.replication_slots", "--no-collector.stat_replication")
            .WithPortBinding(9187, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9187).ForPath("/metrics"))).Build();
        await exporter.StartAsync();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var influxUrl = $"http://{influx.Hostname}:{influx.GetMappedPublicPort(8181)}";
        (await client.GetAsync(influxUrl + "/metrics")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(influxUrl + "/api/v3/query_sql?db=telemetry&q=SELECT%201")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsync(influxUrl + "/api/v3/write_lp?db=telemetry", new StringContent("secret value=1"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var config = (await File.ReadAllTextAsync(Path.Combine(Root, "deploy/prometheus/prometheus.yml")))
            .Replace("scrape_interval: 15s", "scrape_interval: 1s", StringComparison.Ordinal)
            .Replace("scrape_timeout: 10s", "scrape_timeout: 1s", StringComparison.Ordinal)
            .Replace("api:8080", HostEndpoint(api), StringComparison.Ordinal)
            .Replace("worker:9464", HostEndpoint(worker), StringComparison.Ordinal);
        await using var prometheus = new ContainerBuilder("prom/prometheus:v3.15.0-busybox")
            .WithNetwork(network).WithPortBinding(9090, true)
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(config), "/etc/prometheus/prometheus.yml")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/-/ready"))).Build();
        await prometheus.StartAsync();
        var url = $"http://{prometheus.Hostname}:{prometheus.GetMappedPublicPort(9090)}";
        await EventuallyAsync(async () => (await QueryAsync(client, url, "sum(up)")).GetArrayLength() == 1 &&
            (await QueryAsync(client, url, "sum(up)"))[0].GetProperty("value")[1].GetString() == "7");
        foreach (var query in new[] { "rabbitmq_process_resident_memory_bytes", "pg_up", "node_cpu_seconds_total", "node_memory_MemTotal_bytes", "node_filesystem_size_bytes", "telemetry_batches_received_total{job=\"api\"}", "telemetry_batches_received_total{job=\"worker\"}" })
            (await QueryAsync(client, url, query)).GetArrayLength().ShouldBeGreaterThan(0, query);
        (await QueryAsync(client, url, "pg_up"))[0].GetProperty("value")[1].GetString().ShouldBe("1");
        (await QueryAsync(client, url, "pg_exporter_last_scrape_error"))[0].GetProperty("value")[1].GetString().ShouldBe("0");
        (await QueryAsync(client, url, "{job=\"influxdb\",__name__!~\"up|scrape_.*\"}")).GetArrayLength().ShouldBeGreaterThan(0);
        (await client.GetStringAsync(url + "/api/v1/series?match%5B%5D=%7Bjob%3D%22postgres%22%7D")).ShouldNotContain("SEEDED_PRIVATE_DATABASE");
        await rabbit.StopAsync();
        await EventuallyAsync(async () => (await QueryAsync(client, url, "up{job=\"rabbitmq\"}"))[0].GetProperty("value")[1].GetString() == "0");
        await rabbit.StartAsync();
        await EventuallyAsync(async () => (await QueryAsync(client, url, "up{job=\"rabbitmq\"}"))[0].GetProperty("value")[1].GetString() == "1");
    }

    private static async Task<WebApplication> AppAsync(TelemetryServiceRole role)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://0.0.0.0:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTelemetryObservability(builder.Configuration, role);
        var app = builder.Build();
        app.MapTelemetryMetrics();
        await app.StartAsync();
        app.Services.GetRequiredService<TelemetryObservability>().RecordReceived(128);
        return app;
    }

    private static string HostEndpoint(WebApplication app) => "host.docker.internal:" + new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;

    private static async Task<JsonElement> QueryAsync(HttpClient client, string url, string query)
    {
        using var json = JsonDocument.Parse(await client.GetStringAsync(url + "/api/v1/query?query=" + Uri.EscapeDataString(query)));
        json.RootElement.GetProperty("status").GetString().ShouldBe("success");
        return json.RootElement.GetProperty("data").GetProperty("result").Clone();
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (await condition())
                return;
            await Task.Delay(500);
        }
        Assert.Fail("Prometheus condition was not met within 90 seconds.");
    }
}
