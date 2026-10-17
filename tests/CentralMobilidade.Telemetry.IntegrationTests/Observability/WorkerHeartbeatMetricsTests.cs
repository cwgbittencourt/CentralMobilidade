using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Observability;

[Collection("BatchMetricsIsolation")]
public sealed class WorkerHeartbeatMetricsTests
{
    [Fact]
    public async Task Real_worker_metrics_export_consumer_age_before_any_delivery_and_never_refresh_it_on_scrape()
    {
        var clock = new MonotonicClock();
        var progress = new WorkerProgress(clock);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["Observability:WorkerMetricsUrl"] = "http://127.0.0.1:0";
        builder.Services.AddSingleton(progress);
        builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Worker);
        using var host = builder.Build();
        await host.StartAsync();
        var endpoint = host.Services.GetServices<IHostedService>().OfType<WorkerMetricsEndpoint>().Single();
        using var client = new HttpClient();
        var initial = await client.GetStringAsync(endpoint.ListenUrl + "/metrics");
        initial.Split('\n').Single(line => line.StartsWith("telemetry_storage_write_failures_total", StringComparison.Ordinal))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].ShouldBe("0");
        (await Age()).ShouldBe(double.PositiveInfinity);
        progress.MarkHeartbeat();
        (await Age()).ShouldBe(0);
        clock.Ticks += TimeSpan.FromSeconds(31).Ticks;
        (await Age()).ShouldBe(31);
        (await Age()).ShouldBe(31); // Reading metrics cannot create progress.
        progress.MarkUnavailable();
        (await Age()).ShouldBe(double.PositiveInfinity);
        var observations = host.Services.GetRequiredService<TelemetryObservability>();
        observations.RecordDeliveryAge(TimeSpan.FromDays(6));
        observations.RecordDeliveryAge(TimeSpan.FromDays(6) + TimeSpan.FromSeconds(1));
        var histogram = await client.GetStringAsync(endpoint.ListenUrl + "/metrics");
        histogram.Split('\n').Single(line => line.StartsWith("telemetry_queue_delivery_age_seconds_bucket", StringComparison.Ordinal)
            && line.Contains("le=\"518400\"", StringComparison.Ordinal))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].ShouldBe("1");
        histogram.Split('\n').Single(line => line.StartsWith("telemetry_queue_delivery_age_seconds_count", StringComparison.Ordinal))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].ShouldBe("2");
        await host.StopAsync();

        async Task<double> Age()
        {
            var text = await client.GetStringAsync(endpoint.ListenUrl + "/metrics");
            var sample = text.Split('\n').Single(line => line.StartsWith("telemetry_worker_heartbeat_age_seconds", StringComparison.Ordinal));
            var value = sample.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
            return value == "+Inf" ? double.PositiveInfinity : double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class MonotonicClock : TimeProvider
    {
        internal long Ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
}
