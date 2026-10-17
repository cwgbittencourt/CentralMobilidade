using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Infrastructure.DependencyInjection;

namespace CentralMobilidade.Telemetry.Infrastructure.Queue.PubSub;

/// <summary>One bounded unary-pull lane per configured concurrent delivery.</summary>
public sealed class PubSubBatchConsumer(PubSubBatchQueue queue, QueueRuntimeOptions options) : IMonitoredBatchConsumer
{
    public async IAsyncEnumerable<IQueueDelivery> ConsumeAsync(Action<bool> reportAvailability, TimeSpan heartbeatInterval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inbox = Channel.CreateBounded<IQueueDelivery>(options.ConcurrentDeliveries);
        var availability = new bool[options.ConcurrentDeliveries];
        var sync = new object();
        var failed = false;
        void Report(int lane, bool ready)
        {
            lock (sync)
            {
                availability[lane] = ready;
                reportAvailability(!failed && !receiving.IsCancellationRequested && availability.All(value => value));
            }
        }
        async Task Run(int lane)
        {
            try
            {
                await foreach (var delivery in queue.ConsumeAsync(ready => Report(lane, ready), heartbeatInterval, receiving.Token))
                    await inbox.Writer.WriteAsync(delivery, receiving.Token);
            }
            catch (OperationCanceledException) when (receiving.IsCancellationRequested) { }
            catch (Exception error)
            {
                lock (sync)
                {
                    failed = true;
                    reportAvailability(false);
                }
                inbox.Writer.TryComplete(error);
                await receiving.CancelAsync();
            }
        }
        var tasks = Enumerable.Range(0, options.ConcurrentDeliveries).Select(Run).ToArray();
        try
        {
            await foreach (var delivery in inbox.Reader.ReadAllAsync(cancellationToken))
                yield return delivery;
        }
        finally
        {
            await receiving.CancelAsync();
            while (inbox.Reader.TryRead(out var buffered))
                await buffered.DisposeAsync();
            await Task.WhenAll(tasks);
            reportAvailability(false);
        }
    }
}
