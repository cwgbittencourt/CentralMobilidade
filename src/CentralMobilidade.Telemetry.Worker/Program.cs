using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CentralMobilidade.Telemetry.Worker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddTelemetryOptions(builder.Configuration);
        builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Worker);
        var controlConnection = builder.Configuration.GetConnectionString("TelemetryControl");
        if (!string.IsNullOrWhiteSpace(controlConnection))
            builder.Services.AddTelemetryControlPersistence(controlConnection);
        using var host = builder.Build();
        // Keep metrics alive; the hosted queue consumer is implemented in T053.
        await host.RunAsync();
    }
}
