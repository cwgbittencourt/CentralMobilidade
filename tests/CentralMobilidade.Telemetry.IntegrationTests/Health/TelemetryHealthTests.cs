using System;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Health;

[Collection("Observability")]
public sealed partial class TelemetryHealthTests
{
    [Fact]
    public async Task Queue_readiness_requires_durable_routing_and_confirms_not_just_connectivity()
    {
        var probe = new PublicationProbe { Status = new(true, true, false, true) };
        var check = new DurablePublicationHealthCheck(probe);
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
        probe.Status = new(true, true, true, true);
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Worker_is_not_fresh_until_a_consumer_signals_and_becomes_stale_after_deadline()
    {
        var clock = new TestTimeProvider();
        var progress = new WorkerProgress(clock);
        var check = new WorkerFreshnessHealthCheck(progress, new HealthProbeOptions { WorkerMaxAge = TimeSpan.FromSeconds(30) });
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
        progress.MarkHeartbeat();
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Healthy);
        clock.Ticks += TimeSpan.FromSeconds(31).Ticks;
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
        progress.MarkHeartbeat();
        progress.MarkUnavailable();
        (await check.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        internal long Ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
    private sealed class PublicationProbe : IDurablePublicationProbe
    {
        internal DurablePublicationStatus Status = new(false, false, false, false);
        public ValueTask<DurablePublicationStatus> ProbeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Status);
    }
}
