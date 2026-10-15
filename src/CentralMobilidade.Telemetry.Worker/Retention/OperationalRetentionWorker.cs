using System;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace CentralMobilidade.Telemetry.Worker.Retention;

public sealed class OperationalRetentionWorker(IOperationalRetentionCleanup cleanup, IClock clock,
    TimeProvider time, IOptions<OperationalRetentionOptions> options, ILogger<OperationalRetentionWorker> logger) : BackgroundService
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var result = await cleanup.SweepAsync(clock.UtcNow, cancellationToken);
        if (result.FailedObjects > 0)
            logger.LogWarning("Operational retention preserved objects for retry");
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Operational retention pass failed; a later pass will retry"); }
            try
            {
                await Task.Delay(options.Value.Interval, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
