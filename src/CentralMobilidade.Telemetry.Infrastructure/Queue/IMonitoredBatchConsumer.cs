using System;
using System.Collections.Generic;
using System.Threading;
using CentralMobilidade.Telemetry.Application.Abstractions;

namespace CentralMobilidade.Telemetry.Infrastructure.Queue;

public interface IMonitoredBatchConsumer
{
    IAsyncEnumerable<IQueueDelivery> ConsumeAsync(Action<bool> reportAvailability, TimeSpan heartbeatInterval, CancellationToken cancellationToken = default);
}
