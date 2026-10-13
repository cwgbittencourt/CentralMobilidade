using System;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using var startupLogger = PrivacyLoggingPolicy.CreateLogger(TelemetryServiceRole.Api);
try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddPrivacyLogging(TelemetryServiceRole.Api);
    builder.Services.AddTelemetryOptions(builder.Configuration);
    builder.Services.AddTelemetrySecurity(builder.Configuration);
    builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Api);
    builder.Services.AddTelemetryHealthChecks(builder.Configuration, TelemetryServiceRole.Api);
    var controlConnection = builder.Configuration.GetConnectionString("TelemetryControl");
    if (!string.IsNullOrWhiteSpace(controlConnection))
        builder.Services.AddTelemetryControlPersistence(controlConnection);
    await using var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/", () => "Central Mobilidade Telemetry API");
    app.MapTelemetryMetrics();
    app.MapTelemetryHealthEndpoints();

    await app.RunAsync();
}
catch (Exception error) when (error is not Microsoft.Extensions.Hosting.HostAbortedException)
{
    startupLogger.ForContext("EventCode", OperationalLogCode.ServiceFailure).Fatal(error, "Service failure");
    Environment.ExitCode = 1;
}
