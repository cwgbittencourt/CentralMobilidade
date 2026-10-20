using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Infrastructure.Health;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;

/// <summary>Real mandatory persistent control publication; no business batch or fabricated telemetry.</summary>
public sealed class RabbitMqDurablePublicationProbe(RabbitMqConnectionManager connections, IConfiguration configuration,
    TimeProvider? timeProvider = null) : IDurablePublicationProbe
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly RabbitMqTopology topology = new(configuration);
    private DurablePublicationStatus cached = new(false, false, false, false);
    private long observed;
    private bool hasObservation;
    private Task<DurablePublicationStatus>? observation;

    public async ValueTask<DurablePublicationStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(configuration.GetValue<TimeSpan?>("Health:ProbeTimeout") ?? TimeSpan.FromSeconds(3));
        await gate.WaitAsync(deadline.Token);
        try
        {
            if (hasObservation && clock.GetElapsedTime(observed) < TimeSpan.FromSeconds(5))
                return cached;
            // A timed-out SDK operation can still be closing its channel. Never start overlapping replacements.
            if (observation is { IsCompleted: false })
                return new(false, false, false, false);
            observation = ObserveAsync(deadline.Token);
            try
            {
                cached = await observation.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { cached = new(false, false, false, false); }
            observed = clock.GetTimestamp();
            hasObservation = true;
            return cached;
        }
        finally { gate.Release(); }
    }

    private async Task<DurablePublicationStatus> ObserveAsync(CancellationToken token)
    {
        try
        {
            // This manager declares/checks canonical durability/binding, exactly as runtime publishers do.
            await using var channel = await connections.CreatePublishChannelAsync(token);
            var sequence = await channel.GetNextPublishSequenceNumberAsync(token);
            if (!channel.IsOpen || sequence == 0)
                return new(false, false, false, false);
            // Tracking awaits this publish's ACK and rejects NACK/mandatory return.
            await channel.BasicPublishAsync(topology.Options.Exchange, topology.Options.RoutingKey, true,
                PublicationProbeControl.Properties(), PublicationProbeControl.Body, token);
            return new(true, true, true, true);
        }
        catch (Exception) { return new(false, false, false, false); } // Observe cleanup faults even after the caller deadline.
    }
}

/// <summary>Separate operational wire type. Only an exact control is ACKed outside business processing.</summary>
public static class PublicationProbeControl
{
    public const string Type = "centralmobilidade.publication-probe.v1";
    public const string ContentType = "application/vnd.centralmobilidade.publication-probe.v1";
    public static ReadOnlyMemory<byte> Body => "CM-PUBLICATION-PROBE-V1"u8.ToArray();
    public static BasicProperties Properties() => new()
    {
        Type = Type,
        ContentType = ContentType,
        Persistent = true,
        MessageId = "health-" + Guid.NewGuid().ToString("N")
    };
    public static bool IsControl(IReadOnlyBasicProperties properties, ReadOnlySpan<byte> body) =>
        properties.Type == Type && properties.ContentType == ContentType && properties.Persistent &&
        string.IsNullOrEmpty(properties.ContentEncoding) && (properties.Headers is null || properties.Headers.Keys.All(k => k == Constants.PublishSequenceNumberHeader)) &&
        properties.MessageId is { Length: 39 } id && id.StartsWith("health-", StringComparison.Ordinal) &&
        Guid.TryParseExact(id.AsSpan(7), "N", out var guid) && guid != Guid.Empty && body.SequenceEqual(Body.Span);
}
