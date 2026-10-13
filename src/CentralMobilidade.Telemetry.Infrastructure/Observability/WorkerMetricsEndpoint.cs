using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;

namespace CentralMobilidade.Telemetry.Infrastructure.Observability;

public sealed class WorkerMetricsEndpoint(MeterProvider provider, ObservabilityOptions options) : IHostedService
{
    private WebApplication? app;
    public string? ListenUrl => app?.Urls.FirstOrDefault();
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        app = builder.Build();
        app.Urls.Add(options.WorkerMetricsUrl);
        app.MapPrometheusScrapingEndpoint("/metrics", provider, null, null);
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
