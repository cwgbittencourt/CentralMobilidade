using System;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CentralMobilidade.Telemetry.Worker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var startupLogger = PrivacyLoggingPolicy.CreateLogger(TelemetryServiceRole.Worker);
        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            builder.Services.AddPrivacyLogging(TelemetryServiceRole.Worker);
            builder.Services.AddTelemetryOptions(builder.Configuration);
            builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Worker);
            builder.Services.AddTelemetryHealthChecks(builder.Configuration, TelemetryServiceRole.Worker);
            var controlConnection = builder.Configuration.GetConnectionString("TelemetryControl");
            if (!string.IsNullOrWhiteSpace(controlConnection))
                builder.Services.AddTelemetryControlPersistence(controlConnection);
            using var host = builder.Build();
            // Keep metrics alive; the hosted queue consumer is implemented in T053.
            await host.RunAsync();
        }
        catch (Exception error) when (error is not HostAbortedException)
        {
            startupLogger.ForContext("EventCode", OperationalLogCode.ServiceFailure).Fatal(error, "Service failure");
            Environment.ExitCode = 1;
        }
    }
}
