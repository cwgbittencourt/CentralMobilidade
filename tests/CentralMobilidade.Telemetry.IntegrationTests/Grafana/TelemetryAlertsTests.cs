using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

// ASP.NET activity listeners are process-global, including this synthetic scrape server.
[Collection("BatchMetricsIsolation")]
public sealed class TelemetryAlertsTests
{
    [Fact]
    public async Task File_rules_evaluate_normal_pending_firing_recovery_and_missing_data_with_notifications_muted()
    {
        var root = GrafanaProvisioningTests.Root;
        var source = Path.Combine(root, "deploy/grafana/provisioning");
        var path = Path.Combine(root, "artifacts/validation/T085", Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(path, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var rulesPath = Path.Combine(path, "alerting/telemetry-alerts.yaml");
        var production = await File.ReadAllTextAsync(rulesPath);
        production.ShouldContain("interval: 30s");
        production.ShouldContain("for: 5m");
        production.ShouldContain("for: 2m");
        production.ShouldContain("le=~\"518400([.]0)?\"");
        // Only timing is accelerated; the threshold/math/labels/routing remain production definitions.
        await File.WriteAllTextAsync(rulesPath, production.Replace("interval: 30s", "interval: 10s", StringComparison.Ordinal)
            .Replace("for: 1m", "for: 1s", StringComparison.Ordinal)
            .Replace("for: 2m", "for: 1s", StringComparison.Ordinal)
            .Replace("for: 5m", "for: 1s", StringComparison.Ordinal)
            .Replace("[5m]", "[15s]", StringComparison.Ordinal));

        var phase = 0;
        long total = 0, safe = 0, failures = 0;
        var snapshotLock = new object();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://0.0.0.0:0");
        await using var app = builder.Build();
        app.MapGet("/metrics", () =>
        {
            lock (snapshotLock)
            {
                var state = Volatile.Read(ref phase);
                if (state == 2)
                    return "# deliberately empty successful scrape\n";
                var count = Interlocked.Increment(ref total);
                if (state == 0)
                    Interlocked.Increment(ref safe);
                else
                    Interlocked.Increment(ref failures);
                var within = Interlocked.Read(ref safe);
                return $$"""
                # TYPE telemetry_queue_delivery_age_seconds histogram
                telemetry_queue_delivery_age_seconds_bucket{le="10"} {{within}}
                telemetry_queue_delivery_age_seconds_bucket{le="120"} {{within}}
                telemetry_queue_delivery_age_seconds_bucket{le="518400"} {{within}}
                telemetry_queue_delivery_age_seconds_bucket{le="+Inf"} {{count}}
                telemetry_queue_delivery_age_seconds_count {{count}}
                telemetry_queue_delivery_age_seconds_sum {{count * 10}}
                # TYPE telemetry_processing_duration_seconds histogram
                telemetry_processing_duration_seconds_bucket{le="1"} {{count}}
                telemetry_processing_duration_seconds_bucket{le="+Inf"} {{count}}
                telemetry_processing_duration_seconds_count {{count}}
                telemetry_processing_duration_seconds_sum {{count}}
                # TYPE telemetry_storage_write_failures_total counter
                telemetry_storage_write_failures_total {{Interlocked.Read(ref failures)}}
                telemetry_worker_heartbeat_age_seconds {{(state == 0 ? 0 : 60)}}
                node_filesystem_avail_bytes{mountpoint="/",device="fixture",fstype="ext4"} {{(state == 0 ? 50 : 5)}}
                node_filesystem_size_bytes{mountpoint="/",device="fixture",fstype="ext4"} 100

                """;
            }
        });
        await app.StartAsync();
        var port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        await using var network = new NetworkBuilder().Build();
        await network.CreateAsync();
        var prometheusConfig = $"""
            global:
              scrape_interval: 1s
            scrape_configs:
              - job_name: worker
                static_configs:
                  - targets: ['host.docker.internal:{port}']
              - job_name: host
                static_configs:
                  - targets: ['host.docker.internal:{port}']
            """;
        await using var prometheus = new ContainerBuilder("prom/prometheus:v3.15.0-busybox")
            .WithNetwork(network).WithNetworkAliases("prometheus").WithPortBinding(9090, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(prometheusConfig), "/etc/prometheus/prometheus.yml")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/-/ready"))).Build();
        await prometheus.StartAsync();
        await using var grafana = await GrafanaServer.StartAsync(path, network);
        using (var rules = await grafana.JsonAsync("/api/v1/provisioning/alert-rules"))
        {
            rules.RootElement.GetArrayLength().ShouldBe(6);
            foreach (var rule in rules.RootElement.EnumerateArray())
            {
                rule.GetProperty("noDataState").GetString().ShouldBe("Alerting");
                rule.GetProperty("execErrState").GetString().ShouldBe("Alerting");
                rule.GetRawText().ShouldContain("telemetry-local-only");
                rule.GetRawText().ShouldContain("internal-prometheus");
            }
        }
        using (var mute = await grafana.JsonAsync("/api/v1/provisioning/mute-timings/telemetry-local-only"))
        {
            mute.RootElement.GetRawText().ShouldContain("00:00");
            mute.RootElement.GetRawText().ShouldContain("24:00");
        }
        await EventuallyAsync(async () =>
        {
            using var configuration = await grafana.JsonAsync("/api/alertmanager/grafana/config/api/v1/alerts");
            return HasMuteReference(configuration.RootElement);
        }, "Runtime Alertmanager must reference the permanent mute on a notification route.");
        await AssertStates("inactive");
        Volatile.Write(ref phase, 1);
        var pendingObserved = false;
        await EventuallyAsync(async () =>
        {
            var states = await StatesAsync(grafana, requireThreshold: true);
            pendingObserved |= states.Contains("pending", StringComparer.Ordinal);
            return states.Length == 6 && states.All(state => state == "firing");
        }, "All six rules must fire for controlled unsafe telemetry.");
        pendingObserved.ShouldBeTrue("The accelerated hold must still pass through Pending.");
        Volatile.Write(ref phase, 0);
        await AssertStates("inactive");
        Volatile.Write(ref phase, 2);
        await AssertStates("firing"); // Empty scrapes must not become healthy zero.
        Volatile.Write(ref phase, 0);
        await AssertStates("inactive");
        await prometheus.StopAsync();
        await AssertStates("firing"); // Datasource transport errors also fail closed.

        async Task AssertStates(string state)
        {
            try
            {
                await EventuallyAsync(async () =>
                {
                    var states = await StatesAsync(grafana);
                    return states.Length == 6 && states.All(value => value == state);
                }, "Expected six rule states: " + state);
            }
            catch
            {
                using var snapshot = await grafana.JsonAsync("/api/prometheus/grafana/api/v1/rules");
                await File.WriteAllTextAsync(Path.Combine(root, "artifacts/validation/T085/evaluation-failure.json"), snapshot.RootElement.GetRawText());
                using var diagnostics = new HttpClient();
                var address = $"http://{prometheus.Hostname}:{prometheus.GetMappedPublicPort(9090)}";
                var query = "clamp_min(sum(increase(telemetry_queue_delivery_age_seconds_count{job=\"worker\"}[15s])) - sum(increase(telemetry_queue_delivery_age_seconds_bucket{job=\"worker\",le=~\"518400([.]0)?\"}[15s])), 0)";
                await File.WriteAllTextAsync(Path.Combine(root, "artifacts/validation/T085/prometheus-failure.json"), await diagnostics.GetStringAsync(address + "/api/v1/query?query=" + Uri.EscapeDataString(query)));
                await File.WriteAllTextAsync(Path.Combine(root, "artifacts/validation/T085/prometheus-series.json"), await diagnostics.GetStringAsync(address + "/api/v1/query?query=" + Uri.EscapeDataString("{job=\"worker\"}")));
                throw;
            }
        }
    }

    private static async Task<string[]> StatesAsync(GrafanaServer grafana, bool requireThreshold = false)
    {
        using var response = await grafana.JsonAsync("/api/prometheus/grafana/api/v1/rules");
        return response.RootElement.GetProperty("data").GetProperty("groups").EnumerateArray()
            .SelectMany(group => group.GetProperty("rules").EnumerateArray())
            .Where(rule => rule.GetProperty("name").GetString() is { } name && (name.Contains("Backlog", StringComparison.Ordinal) ||
                name.Contains("Risco", StringComparison.Ordinal) || name.Contains("Retenção", StringComparison.Ordinal) ||
                name.Contains("Armazenamento", StringComparison.Ordinal) || name.Contains("Disco", StringComparison.Ordinal) || name.Contains("Worker", StringComparison.Ordinal)))
            .Select(rule =>
            {
                if (!rule.TryGetProperty("alerts", out var alerts) || alerts.GetArrayLength() == 0)
                    return "unevaluated";
                var state = rule.GetProperty("state").GetString()!;
                return requireThreshold && state == "firing" && alerts.EnumerateArray().Any(alert => alert.GetProperty("state").GetString() != "Alerting")
                    ? "missing-or-error" : state;
            }).ToArray();
    }

    private static bool HasMuteReference(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
            return node.EnumerateObject().Any(property => (property.Name == "mute_time_intervals" && property.Value.ValueKind == JsonValueKind.Array &&
                property.Value.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "telemetry-local-only")) || HasMuteReference(property.Value));
        return node.ValueKind == JsonValueKind.Array && node.EnumerateArray().Any(HasMuteReference);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> predicate, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (await predicate())
                return;
            await Task.Delay(500);
        }
        Assert.Fail(failure);
    }
}
