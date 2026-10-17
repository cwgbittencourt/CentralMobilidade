using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Grafana;

[Collection("BatchMetricsIsolation")]
public sealed class PlatformOperationsTests
{
    [Fact]
    public async Task Provisioned_dashboard_queries_real_Prometheus_with_production_instruments_and_renders()
    {
        var root = GrafanaProvisioningTests.Root;
        using var dashboard = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "deploy/grafana/dashboards/platform-operations.json")));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://0.0.0.0:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Api);
        await using var app = builder.Build();
        app.MapTelemetryMetrics();
        // Deterministic infrastructure samples test query arithmetic, not real host/broker operation.
        app.MapGet("/fixture", () => """
            rabbitmq_queue_messages_ready 12
            rabbitmq_queue_messages_unacked 3
            rabbitmq_process_resident_memory_bytes 50
            rabbitmq_resident_memory_limit_bytes 100
            rabbitmq_disk_space_available_bytes 1000
            rabbitmq_disk_space_available_limit_bytes 100
            node_cpu_seconds_total{cpu="0",mode="idle"} 100
            node_memory_MemAvailable_bytes 25
            node_memory_MemTotal_bytes 100
            node_filesystem_avail_bytes{mountpoint="/",device="fixture",fstype="ext4"} 25
            node_filesystem_size_bytes{mountpoint="/",device="fixture",fstype="ext4"} 100
            pg_up 1
            pg_exporter_last_scrape_error 0

            """);
        await app.StartAsync();
        var port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        var endpoint = "host.docker.internal:" + port;
        var telemetry = app.Services.GetRequiredService<TelemetryObservability>();
        var metrics = app.Services.GetRequiredService<BatchMetrics>();
        void Record()
        {
            telemetry.RecordReceived(128);
            telemetry.RecordDeliveryAge(TimeSpan.FromSeconds(2));
            telemetry.RecordProcessingDuration(TimeSpan.FromMilliseconds(250));
            telemetry.RecordWriteDuration(TimeSpan.FromMilliseconds(100));
            telemetry.RecordWriteFailure();
            metrics.RecordIdenticalReplay();
            foreach (var state in Enum.GetValues<BatchLifecycle>())
                metrics.RecordState(state);
            foreach (var code in TelemetryErrorCodes.RejectionCodes)
                metrics.RecordRejection(code);
        }
        Record();
        using var lifetime = new CancellationTokenSource();
        var sampling = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(500, lifetime.Token);
                    Record();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        try
        {
            await using var network = new NetworkBuilder().Build();
            await network.CreateAsync();
            var config = "global:\n  scrape_interval: 1s\nscrape_configs:\n";
            foreach (var job in new[] { "api", "worker", "rabbitmq", "host", "postgres" })
                config += $"  - job_name: {job}\n    metrics_path: /{(job is "api" or "worker" ? "metrics" : "fixture")}\n    static_configs:\n      - targets: ['{endpoint}']\n";
            await using var prometheus = new ContainerBuilder("prom/prometheus:v3.15.0-busybox")
                .WithNetwork(network).WithNetworkAliases("prometheus").WithPortBinding(9090, true)
                .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/etc/prometheus/prometheus.yml")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/-/ready"))).Build();
            await prometheus.StartAsync();
            var token = "t084-" + Guid.NewGuid().ToString("N");
            await using var renderer = new ContainerBuilder("grafana/grafana-image-renderer:v4.0.17")
                .WithNetwork(network).WithNetworkAliases("renderer").WithEnvironment("AUTH_TOKEN", token)
                .WithEnvironment("HTTP_PORT", "8081")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8081)).Build();
            await renderer.StartAsync();
            await using var grafana = await GrafanaServer.StartAsync(Path.Combine(root, "deploy/grafana/provisioning"), network, token);
            for (var attempt = 0; attempt < 60; attempt++)
            {
                using var response = await grafana.Client.GetAsync("/api/dashboards/uid/platform-operations");
                if (response.IsSuccessStatusCode)
                    break;
                await Task.Delay(500);
            }
            using var loaded = await grafana.JsonAsync("/api/dashboards/uid/platform-operations");
            loaded.RootElement.GetProperty("meta").GetProperty("provisioned").GetBoolean().ShouldBeTrue();
            using var client = new HttpClient { BaseAddress = new Uri($"http://{prometheus.Hostname}:{prometheus.GetMappedPublicPort(9090)}") };
            var warmed = false;
            for (var attempt = 0; attempt < 120; attempt++)
            {
                var warmQuery = "count(count by(job) (count_over_time(telemetry_batches_received_total{job=~\"api|worker\"}[30s]) >= 3))";
                using var warm = JsonDocument.Parse(await client.GetStringAsync("/api/v1/query?query=" + Uri.EscapeDataString(warmQuery)));
                var values = warm.RootElement.GetProperty("data").GetProperty("result");
                if (values.GetArrayLength() == 1 && values[0].GetProperty("value")[1].GetString() == "2")
                {
                    warmed = true;
                    break;
                }
                await Task.Delay(1000);
            }
            warmed.ShouldBeTrue("Both real application scrape jobs need three successful samples before rate assertions.");
            foreach (var panel in dashboard.RootElement.GetProperty("panels").EnumerateArray().Where(p => p.GetProperty("type").GetString() == "timeseries"))
            {
                foreach (var target in panel.GetProperty("targets").EnumerateArray())
                {
                    var query = target.GetProperty("expr").GetString()!.Replace("$__rate_interval", "30s", StringComparison.Ordinal);
                    using var response = await client.GetAsync("/api/v1/query?query=" + Uri.EscapeDataString(query));
                    var body = await response.Content.ReadAsStringAsync();
                    response.IsSuccessStatusCode.ShouldBeTrue(query + "\n" + body);
                    using var result = JsonDocument.Parse(body);
                    result.RootElement.GetProperty("status").GetString().ShouldBe("success", query);
                    var samples = result.RootElement.GetProperty("data").GetProperty("result");
                    samples.GetArrayLength().ShouldBeGreaterThan(0, query);
                    samples.EnumerateArray().ShouldAllBe(s => s.GetProperty("value")[1].GetString() != "NaN", query);
                    if (panel.GetProperty("title").GetString() == "Pressão de filesystem")
                        samples[0].GetProperty("value")[1].GetString().ShouldBe("75");
                    if (panel.GetProperty("title").GetString() == "Idade observada na entrega — p95")
                        double.Parse(samples[0].GetProperty("value")[1].GetString()!, System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(1.8, 2.0);
                }
            }
            var output = Path.Combine(root, "artifacts/validation/T084/render");
            Directory.CreateDirectory(output);
            // Populate more than one Grafana 15-second query step before visual inspection.
            await Task.Delay(20000);
            foreach (var id in new[] { 1, 3, 6, 10, 16 })
            {
                using var image = await grafana.Client.GetAsync($"/render/d-solo/platform-operations/_?panelId={id}&width=1100&height=550&from=now-5m&to=now");
                image.EnsureSuccessStatusCode();
                image.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
                var png = await image.Content.ReadAsByteArrayAsync();
                png.Length.ShouldBeGreaterThan(1000);
                await File.WriteAllBytesAsync(Path.Combine(output, id + ".png"), png);
            }
            using (var overview = await grafana.Client.GetAsync("/render/d/platform-operations/_?width=1400&height=2900&from=now-5m&to=now&kiosk"))
            {
                overview.EnsureSuccessStatusCode();
                overview.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
                await File.WriteAllBytesAsync(Path.Combine(output, "overview.png"), await overview.Content.ReadAsByteArrayAsync());
            }
            // Missing application telemetry must remain absent, never coerced to healthy zero.
            using var absent = JsonDocument.Parse(await client.GetStringAsync("/api/v1/query?query=" + Uri.EscapeDataString("sum(rate(telemetry_batches_received_total{job=\"absent\"}[30s]))")));
            absent.RootElement.GetProperty("data").GetProperty("result").GetArrayLength().ShouldBe(0);
        }
        finally
        {
            await lifetime.CancelAsync();
            await sampling;
        }
    }
}
