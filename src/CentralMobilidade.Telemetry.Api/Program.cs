using System;
using System.Net;
using CentralMobilidade.Telemetry.Api.Endpoints.V1;
using CentralMobilidade.Telemetry.Api.Endpoints.V1.Presentation;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.Ingestion;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using CentralMobilidade.Telemetry.Infrastructure.Cadastral;
using CentralMobilidade.Telemetry.Infrastructure.Presentation;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using var startupLogger = PrivacyLoggingPolicy.CreateLogger(TelemetryServiceRole.Api);
try
{
    var builder = WebApplication.CreateBuilder(args);
    var maxCompressedBytes = builder.Configuration.GetValue<long?>("Telemetry:MaxCompressedBytes")
        ?? 8 * 1024 * 1024;
    builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxCompressedBytes);
    builder.Logging.ClearProviders();
    builder.Services.AddPrivacyLogging(TelemetryServiceRole.Api);
    builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    {
        var problem = TelemetryErrorCodes.GetProblem(TelemetryErrorCodes.InternalError);
        context.ProblemDetails.Type = problem.Type;
        context.ProblemDetails.Title = problem.Title;
        context.ProblemDetails.Status ??= problem.Status;
        context.ProblemDetails.Detail = problem.Detail;
        context.ProblemDetails.Extensions["code"] = problem.Code;
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    });
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = builder.Configuration.GetValue<int?>("Proxy:ForwardLimit") ?? 1;
        options.RequireHeaderSymmetry = true;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var value in builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [])
            options.KnownProxies.Add(IPAddress.Parse(value));
        foreach (var value in builder.Configuration.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [])
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(value));
    });
    builder.Services.AddTelemetryOptions(builder.Configuration);
    builder.Services.AddTelemetryIngestion();
    builder.Services.AddTelemetryIntakeEndpoint();
    builder.Services.AddTelemetrySecurity(builder.Configuration);
    builder.Services.AddOptionalDriverEnrichment();
    builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Api);
    builder.Services.AddTelemetryHealthChecks(builder.Configuration, TelemetryServiceRole.Api);
    var controlConnection = builder.Configuration.GetConnectionString("TelemetryControl");
    if (!string.IsNullOrWhiteSpace(controlConnection))
    {
        builder.Services.AddTelemetryControlPersistence(controlConnection).AddTelemetryBatchInspection(builder.Configuration);
        builder.Services.AddTelemetryPresentation(builder.Configuration);
    }
    if (string.Equals(builder.Configuration["Queue:Provider"], "RabbitMq", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<RabbitMqTopology>();
        builder.Services.AddSingleton<RabbitMqConnectionManager>();
        builder.Services.AddSingleton<IDurableBatchQueue>(services =>
            RabbitMqBatchPublisher.CreateAsync(
                services.GetRequiredService<RabbitMqConnectionManager>(),
                services.GetRequiredService<IConfiguration>()).GetAwaiter().GetResult());
    }
    await using var app = builder.Build();
    app.UseExceptionHandler();
    app.UseForwardedHeaders();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/", () => "Central Mobilidade Telemetry API");
    app.MapIngestTelemetryBatchEndpoint();
    app.MapGetBatchStatusEndpoint();
    app.MapListBatchRejectionsEndpoint();
    app.MapGetQuarantinedRejectionContentEndpoint();
    app.MapPresentationEndpoints();
    app.MapTelemetryMetrics();
    app.MapTelemetryHealthEndpoints();

    await app.RunAsync();
}
catch (Exception error) when (error is not Microsoft.Extensions.Hosting.HostAbortedException)
{
    startupLogger.ForContext("EventCode", OperationalLogCode.ServiceFailure).Fatal(error, "Service failure");
    Environment.ExitCode = 1;
}
