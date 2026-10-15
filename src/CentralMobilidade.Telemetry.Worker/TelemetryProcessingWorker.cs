using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Processing;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CentralMobilidade.Telemetry.Worker;

public sealed class WorkerRuntimeOptions
{
    public int Concurrency
    {
        get;
    }
    public TimeSpan HeartbeatInterval
    {
        get;
    }
    public TimeSpan RecoveryDelay
    {
        get;
    }
    public TimeSpan DrainTimeout
    {
        get;
    }
    public TimeSpan PublishTimeout
    {
        get;
    }
    public WorkerRuntimeOptions(IConfiguration configuration)
    {
        Concurrency = configuration.GetValue("Worker:Concurrency", 4);
        HeartbeatInterval = configuration.GetValue("Worker:HeartbeatInterval", TimeSpan.FromSeconds(5));
        RecoveryDelay = configuration.GetValue("Worker:RecoveryDelay", TimeSpan.FromSeconds(1));
        DrainTimeout = configuration.GetValue("Queue:RabbitMq:DrainTimeout", TimeSpan.FromSeconds(30));
        PublishTimeout = configuration.GetValue("Queue:RabbitMq:PublishTimeout", TimeSpan.FromSeconds(5));
        var prefetch = configuration.GetValue("Queue:RabbitMq:Prefetch", 8);
        var freshness = configuration.GetValue("Health:WorkerMaxAge", TimeSpan.FromSeconds(30));
        if (Concurrency is < 1 or > 64 || Concurrency > prefetch || prefetch is < 1 or > 64
            || HeartbeatInterval < TimeSpan.FromMilliseconds(100) || HeartbeatInterval > TimeSpan.FromSeconds(10) || HeartbeatInterval >= freshness
            || RecoveryDelay < TimeSpan.FromMilliseconds(100) || RecoveryDelay > TimeSpan.FromSeconds(30)
            || DrainTimeout < TimeSpan.Zero || DrainTimeout > TimeSpan.FromMinutes(5)
            || PublishTimeout <= TimeSpan.Zero || PublishTimeout > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("Invalid worker concurrency, freshness or shutdown limits.");
    }
}

public interface IWorkerBatchProcessor
{
    ValueTask ProcessAsync(IQueueDelivery delivery, CancellationToken cancellationToken);
}

public sealed class ScopedWorkerBatchProcessor : IWorkerBatchProcessor
{
    private readonly IServiceScopeFactory scopes;
    public ScopedWorkerBatchProcessor(IServiceScopeFactory scopes)
    {
        this.scopes = scopes;
        // Resolve mandatory adapters and validate the diagnostic key before subscribing to the broker.
        using var scope = scopes.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<ProcessTelemetryBatchHandler>();
    }
    public async ValueTask ProcessAsync(IQueueDelivery delivery, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ProcessTelemetryBatchHandler>().HandleAsync(delivery, cancellationToken);
    }
}

public sealed class TelemetryProcessingWorker(RabbitMqBatchConsumer consumer, IWorkerBatchProcessor processor,
    WorkerRuntimeOptions options, WorkerProgress progress, ILogger<TelemetryProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        progress.MarkUnavailable();
        while (!stoppingToken.IsCancellationRequested)
        {
            using var receiving = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            using var processing = new CancellationTokenSource();
            using var slots = new SemaphoreSlim(options.Concurrency);
            long drainStarted = 0;
            void BeginDrain()
            {
                if (Interlocked.CompareExchange(ref drainStarted, Stopwatch.GetTimestamp(), 0) == 0)
                    processing.CancelAfter(options.DrainTimeout);
            }
            using var shutdown = stoppingToken.Register(() =>
            {
                progress.MarkUnavailable();
                BeginDrain();
            });
            var tasks = new HashSet<Task>();
            try
            {
                await foreach (var delivery in consumer.ConsumeAsync(available =>
                {
                    if (available && !receiving.IsCancellationRequested)
                    {
                        Interlocked.Exchange(ref failures, 0);
                        progress.MarkHeartbeat();
                    }
                    else
                    {
                        progress.MarkUnavailable();
                        BeginDrain();
                    }
                }, options.HeartbeatInterval, receiving.Token))
                {
                    try
                    {
                        await slots.WaitAsync(receiving.Token);
                    }
                    catch { await delivery.DisposeAsync(); throw; }
                    tasks.RemoveWhere(task => task.IsCompletedSuccessfully);
                    tasks.Add(Process(delivery, processing.Token, receiving, slots));
                }
            }
            catch (OperationCanceledException) when (receiving.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogWarning(error, "{EventCode}", OperationalLogCode.DependencyFailure);
            }
            finally
            {
                progress.MarkUnavailable();
                BeginDrain();
                var remaining = options.DrainTimeout - Stopwatch.GetElapsedTime(Volatile.Read(ref drainStarted));
                var cleanupBudget = (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) + options.PublishTimeout + TimeSpan.FromSeconds(5);
                var completion = Task.WhenAll(tasks);
                // The processing token is separate from reception: shutdown first drains, then cancels.
                try
                {
                    await completion.WaitAsync(cleanupBudget);
                }
                catch (TimeoutException)
                {
                    // A non-cooperative adapter must stop the host, not leave background tasks in a new session.
                    await processing.CancelAsync();
                    _ = completion.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw new InvalidOperationException("Worker processing did not stop within its shutdown budget.");
                }
            }
            if (!stoppingToken.IsCancellationRequested)
            {
                var delay = Math.Min(30_000, options.RecoveryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(failures++, 8)));
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delay * (0.8 + Random.Shared.NextDouble() * 0.2)), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task Process(IQueueDelivery delivery, CancellationToken token, CancellationTokenSource receiving, SemaphoreSlim slots)
    {
        try
        {
            await using (delivery)
            {
                try
                {
                    await processor.ProcessAsync(delivery, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error)
                {
                    logger.LogWarning("{EventCode}", OperationalLogCode.BatchRetryScheduled);
                    try
                    {
                        if (error is ProviderOperationException { Kind: ProviderFailureKind.Permanent, ErrorCode: TelemetryErrorCodes.RequestInvalid })
                            await delivery.DeadLetterAsync(TelemetryErrorCodes.RequestInvalid, token);
                        else
                            await delivery.RetryAsync(TimeSpan.Zero, token);
                    }
                    catch (Exception) { await receiving.CancelAsync(); }
                }
            }
        }
        catch (Exception)
        {
            progress.MarkUnavailable();
            await receiving.CancelAsync();
        }
        finally { slots.Release(); }
    }
}
