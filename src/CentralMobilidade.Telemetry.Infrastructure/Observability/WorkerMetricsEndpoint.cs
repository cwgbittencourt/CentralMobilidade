using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using OpenTelemetry.Metrics;

namespace CentralMobilidade.Telemetry.Infrastructure.Observability;

public sealed class WorkerMetricsEndpoint(MeterProvider provider, ObservabilityOptions options, HealthCheckService? health = null) : IHostedService
{
    private WebApplication? app;
    public string? ListenUrl => app?.Urls.FirstOrDefault();
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddPrivacyLogging(TelemetryServiceRole.Worker);
        if (health is not null)
            builder.Services.AddSingleton(health);
        app = builder.Build();
        app.Urls.Add(options.WorkerMetricsUrl);
        app.MapPrometheusScrapingEndpoint("/metrics", provider, null, null);
        if (health is not null)
            app.MapTelemetryHealthEndpoints(worker: true);
        await app.StartAsync(cancellationToken);
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (app is null)
            return;
        await app.StopAsync(cancellationToken);
        await app.DisposeAsync();
        app = null;
    }
}
