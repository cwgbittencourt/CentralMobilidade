using System;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.LineProtocol;
using CentralMobilidade.Telemetry.Application.Processing;
using CentralMobilidade.Telemetry.Infrastructure.Influx;
using CentralMobilidade.Telemetry.Infrastructure.Quarantine;
using CentralMobilidade.Telemetry.Infrastructure.DependencyInjection;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using CentralMobilidade.Telemetry.Worker.Retention;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            ConfigureServices(builder.Services, builder.Configuration, builder.Environment);
            var host = builder.Build();
            try
            {
                await host.RunAsync();
            }
            finally
            {
                if (host is IAsyncDisposable asyncHost)
                    await asyncHost.DisposeAsync();
                else
                    host.Dispose();
            }
        }
        catch (Exception error) when (error is not HostAbortedException)
        {
            startupLogger.ForContext("EventCode", OperationalLogCode.ServiceFailure).Fatal(error, "Service failure");
            Environment.ExitCode = 1;
        }
    }

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        var connection = configuration.GetConnectionString("TelemetryControl");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Worker control storage configuration is required.");
        var queue = QueueRuntimeOptions.Read(configuration, environment?.EnvironmentName ?? Environments.Production);
        services.AddTelemetryQueue(queue);
        var runtime = new WorkerRuntimeOptions(configuration, queue);
        services.AddSingleton(runtime);
        services.AddPrivacyLogging(TelemetryServiceRole.Worker);
        services.AddTelemetryOptions(configuration);
        services.AddTelemetryObservability(configuration, TelemetryServiceRole.Worker);
        services.AddTelemetryHealthChecks(configuration, TelemetryServiceRole.Worker);
        services.AddTelemetryControlPersistence(connection);
        services.AddOptions<OperationalRetentionOptions>().Bind(configuration.GetSection("Retention"))
            .Validate(options => options.IsValid(), "Operational retention configuration is invalid.").ValidateOnStart();
        services.AddSingleton<IOperationalRetentionCleanup, PostgresOperationalRetentionCleanup>();
        services.AddTelemetryStorageAdapters();
        services.AddSingleton<IClock, WorkerClock>();
        services.AddSingleton<IQuarantineStore, FileSystemQuarantineStore>();
        services.AddSingleton<TelemetryLineProtocolV1Parser>();
        services.AddScoped<ProcessTelemetryBatchHandler>();
        services.AddSingleton<IWorkerBatchProcessor, ScopedWorkerBatchProcessor>();
        services.Configure<HostOptions>(options => options.ShutdownTimeout = runtime.DrainTimeout + runtime.PublishTimeout + TimeSpan.FromSeconds(10));
        services.AddHostedService<TelemetryProcessingWorker>();
        services.AddHostedService<OperationalRetentionWorker>();
    }

    private sealed class WorkerClock(TimeProvider time) : IClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow().ToUniversalTime();
    }
}
