using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CentralMobilidade.Telemetry.Worker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddTelemetryOptions(builder.Configuration);
        var controlConnection = builder.Configuration.GetConnectionString("TelemetryControl");
        if (!string.IsNullOrWhiteSpace(controlConnection))
            builder.Services.AddTelemetryControlPersistence(controlConnection);
        using var host = builder.Build();
        // Validate startup now; the hosted queue consumer is implemented in T053.
        await host.StartAsync();
        await host.StopAsync();
    }
}
