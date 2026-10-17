using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;

/// <summary>Each enumeration owns a channel. Unsettled deliveries are requeued on channel close.</summary>
public sealed class RabbitMqBatchConsumer : IMonitoredBatchConsumer
{
    private readonly RabbitMqConnectionManager connections;
    private readonly RabbitMqTopology topology;
    private readonly ushort prefetch;
    private readonly int maxAttempts;
    private readonly TimeSpan initialDelay, maxDelay, drainTimeout, publishTimeout;

    public RabbitMqBatchConsumer(RabbitMqConnectionManager connections, IConfiguration configuration)
    {
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));
        topology = new(configuration);
        var count = configuration.GetValue<int?>("Queue:RabbitMq:Prefetch") ?? 8;
        maxAttempts = configuration.GetValue<int?>("Queue:RabbitMq:MaxAttempts") ?? 10;
        initialDelay = configuration.GetValue<TimeSpan?>("Queue:RabbitMq:RetryDelay") ?? TimeSpan.FromSeconds(1);
        maxDelay = configuration.GetValue<TimeSpan?>("Queue:RabbitMq:MaxRetryDelay") ?? TimeSpan.FromMinutes(1);
        drainTimeout = configuration.GetValue<TimeSpan?>("Queue:RabbitMq:DrainTimeout") ?? TimeSpan.FromSeconds(30);
        publishTimeout = configuration.GetValue<TimeSpan?>("Queue:RabbitMq:PublishTimeout") ?? TimeSpan.FromSeconds(5);
        if (count is < 1 or > 64 || maxAttempts is < 1 or > 1000 || initialDelay < TimeSpan.FromMilliseconds(1)
            || maxDelay < initialDelay || maxDelay > TimeSpan.FromMinutes(10)
            || drainTimeout < TimeSpan.Zero || drainTimeout > TimeSpan.FromMinutes(5)
            || publishTimeout <= TimeSpan.Zero || publishTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentException("Invalid RabbitMQ consumer limits.");
        prefetch = (ushort)count;
    }

    public IAsyncEnumerable<IQueueDelivery> ConsumeAsync(CancellationToken cancellationToken = default) => ConsumeCoreAsync(null, TimeSpan.FromSeconds(5), cancellationToken);

    /// <summary>Reports subscription health only after a successful RPC on its active channel.</summary>
    public IAsyncEnumerable<IQueueDelivery> ConsumeAsync(Action<bool> reportAvailability, TimeSpan heartbeatInterval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportAvailability);
        if (heartbeatInterval < TimeSpan.FromMilliseconds(100) || heartbeatInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        return ConsumeCoreAsync(reportAvailability, heartbeatInterval, cancellationToken);
    }

    private async IAsyncEnumerable<IQueueDelivery> ConsumeCoreAsync(Action<bool>? reportAvailability, TimeSpan heartbeatInterval, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var channel = await connections.CreatePublishChannelAsync(cancellationToken);
        await using var session = new Session(channel, this);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += session.Receive;
        consumer.UnregisteredAsync += (_, _) => { session.Stop(); return Task.CompletedTask; };
        channel.ChannelShutdownAsync += (_, _) => { session.Fail(); return Task.CompletedTask; };
        await channel.BasicQosAsync(0, prefetch, false, cancellationToken);
        var tag = await channel.BasicConsumeAsync(topology.Options.Queue, false, consumer, cancellationToken);
        session.StartMonitoring(reportAvailability, heartbeatInterval);
        try
        {
            await foreach (var delivery in session.Inbox.Reader.ReadAllAsync(cancellationToken))
                yield return delivery;
        }
        finally
        {
            session.Stop();
            using var stop = new CancellationTokenSource(publishTimeout);
            try
            {
                await channel.BasicCancelAsync(tag, false, stop.Token);
            }
            catch (Exception) { session.Fail(); }
            await session.Drain();
        }
    }

    private TimeSpan RetryDelay(int attempt, TimeSpan requested)
    {
        if (requested < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requested));
        var exponential = initialDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30));
        var jittered = exponential * (0.8 + Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromMilliseconds(Math.Min(maxDelay.TotalMilliseconds, Math.Max(requested.TotalMilliseconds, jittered)));
    }

    private static ProviderOperationException Failure() => new(TelemetryErrorCodes.DurabilityUnconfirmed, ProviderFailureKind.Ambiguous);

    private sealed class Session(IChannel channel, RabbitMqBatchConsumer owner) : IAsyncDisposable
    {
        internal Channel<Delivery> Inbox { get; } = Channel.CreateBounded<Delivery>(owner.prefetch);
        private readonly ConcurrentDictionary<ulong, Delivery> outstanding = new();
        private readonly SemaphoreSlim io = new(1, 1);
        private readonly CancellationTokenSource lifetime = new();
        private readonly CancellationTokenSource monitoring = new();
        private Task monitorTask = Task.CompletedTask;
        private int stopped, failed;

        internal void Stop()
        {
            Interlocked.Exchange(ref stopped, 1);
            monitoring.Cancel();
            Inbox.Writer.TryComplete();
        }
        internal void Fail()
        {
            Interlocked.Exchange(ref failed, 1);
            lifetime.Cancel();
            Inbox.Writer.TryComplete(Failure());
        }

        internal void StartMonitoring(Action<bool>? reportAvailability, TimeSpan interval)
        {
            if (reportAvailability is not null)
                monitorTask = Monitor(reportAvailability, interval);
        }

        private async Task Monitor(Action<bool> reportAvailability, TimeSpan interval)
        {
            using var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(monitoring.Token, lifetime.Token);
            var token = tokenSource.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(owner.publishTimeout);
                    await io.WaitAsync(deadline.Token);
                    try
                    {
                        if (Volatile.Read(ref stopped) != 0 || Volatile.Read(ref failed) != 0 || !channel.IsOpen)
                            throw Failure();
                        // This is a bounded RPC for the subscribed queue, not an independent timestamp timer.
                        await channel.QueueDeclarePassiveAsync(owner.topology.Options.Queue, deadline.Token);
                        if (!token.IsCancellationRequested)
                            reportAvailability(true);
                    }
                    finally { io.Release(); }
                    await Task.Delay(interval, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception) { Fail(); }
            finally { reportAvailability(false); }
        }

        internal async Task Receive(object sender, BasicDeliverEventArgs args)
        {
            if (Volatile.Read(ref stopped) != 0 || Volatile.Read(ref failed) != 0)
                return;
            // RabbitMQ v7 owns the callback memory: snapshot before returning from this callback.
            if (args.Body.Length > 8 * 1024 * 1024)
            {
                Fail(); // Fail closed; never ACK a body outside the bounded envelope contract.
                return;
            }
            var delivery = new Delivery(this, args.DeliveryTag, args.Body.ToArray());
            outstanding[args.DeliveryTag] = delivery;
            try
            {
                delivery.Read(args.BasicProperties);
            }
            catch (Exception error) when (error is ArgumentException or FormatException or System.ComponentModel.DataAnnotations.ValidationException or InvalidDataException or OverflowException)
            {
                try
                {
                    await Settle(delivery, "dead", TimeSpan.Zero, "INVALID_ENVELOPE", lifetime.Token);
                }
                catch (Exception) { Fail(); }
                finally { await delivery.DisposeAsync(); }
                return;
            }
            if (delivery.DeliveryAttempt > owner.maxAttempts || delivery.Metadata.ReceivedAt + TimeSpan.FromMilliseconds(owner.topology.Options.RetentionMilliseconds) <= DateTimeOffset.UtcNow)
            {
                try
                {
                    await Settle(delivery, "dead", TimeSpan.Zero, "RETRY_EXHAUSTED", lifetime.Token);
                }
                catch (Exception) { Fail(); }
                finally { await delivery.DisposeAsync(); }
                return;
            }
            if (!Inbox.Writer.TryWrite(delivery))
                Stop(); // Outstanding delivery remains unacknowledged until shutdown closes the channel.
        }

        internal async ValueTask Settle(Delivery delivery, string action, TimeSpan requested, string? errorCode, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            var token = linked.Token;
            await delivery.Gate.WaitAsync(token);
            try
            {
                if (delivery.Action is not null)
                {
                    if (delivery.Action != action)
                        throw new InvalidOperationException("Delivery already settled with another action.");
                    return;
                }
                ObjectDisposedException.ThrowIf(delivery.Disposed, delivery);
                if (Volatile.Read(ref failed) != 0 || !channel.IsOpen)
                    throw Failure();
                var dead = action == "dead" || action == "retry" && delivery.DeliveryAttempt >= owner.maxAttempts;
                if (action == "retry" && !dead)
                    await Task.Delay(owner.RetryDelay(delivery.DeliveryAttempt, requested), token);
                await io.WaitAsync(token);
                try
                {
                    if (Volatile.Read(ref failed) != 0 || !channel.IsOpen)
                        throw Failure();
                    if (action != "complete")
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        deadline.CancelAfter(owner.publishTimeout);
                        var properties = delivery.Properties ?? new BasicProperties { Persistent = true, MessageId = Guid.NewGuid().ToString("D"), Headers = new Dictionary<string, object?>() };
                        properties.Headers!["deliveryAttempt"] = dead ? Math.Max(1, delivery.DeliveryAttempt) : delivery.DeliveryAttempt + 1;
                        if (dead)
                            properties.Headers["errorCode"] = action == "retry" ? "RETRY_EXHAUSTED" : SafeCode(errorCode);
                        // The channel has publisher confirms + tracking enabled. Mandatory return/nack throws.
                        // Delay while retaining the original, then confirm the new copy before ACKing it.
                        await channel.BasicPublishAsync(dead ? owner.topology.Options.DeadLetterExchange : owner.topology.Options.Exchange,
                            owner.topology.Options.RoutingKey, true, properties, delivery.Bytes, deadline.Token);
                    }
                    await channel.BasicAckAsync(delivery.Tag, false, token);
                    delivery.Action = action;
                    outstanding.TryRemove(delivery.Tag, out _);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { Fail(); throw Failure(); }
                finally { io.Release(); }
            }
            finally { delivery.Gate.Release(); }
        }

        private static string SafeCode(string? code)
        {
            if (string.IsNullOrEmpty(code) || code.Length > 64)
                return "POISON_BATCH";
            foreach (var c in code)
                if (c is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '_')
                    return "POISON_BATCH";
            return code;
        }

        internal async Task Drain()
        {
            // Messages still buffered have never been handed to the caller and cannot finish processing.
            while (Inbox.Reader.TryRead(out var buffered))
                await buffered.DisposeAsync();
            var elapsed = Stopwatch.StartNew();
            while (!outstanding.IsEmpty && Volatile.Read(ref failed) == 0 && elapsed.Elapsed < owner.drainTimeout)
                await Task.Delay(10);
            lifetime.Cancel();
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            lifetime.Cancel();
            await monitorTask;
            // Dispose closes even an automatically recovered channel, preventing stale delivery-tag ACKs.
            await channel.DisposeAsync();
            foreach (var delivery in outstanding.Values)
                await delivery.DisposeAsync();
            lifetime.Dispose();
            monitoring.Dispose();
        }

        internal sealed class Delivery(Session session, ulong tag, byte[] bytes) : IQueueDelivery
        {
            internal ulong Tag => tag;
            internal byte[] Bytes => bytes;
            internal SemaphoreSlim Gate { get; } = new(1, 1);
            internal string? Action
            {
                get; set;
            }
            internal bool Disposed
            {
                get; private set;
            }
            internal BasicProperties? Properties
            {
                get; private set;
            }
            public TelemetryBatchEnvelope Metadata { get; private set; } = null!;
            public Stream Body { get; } = new MemoryStream(bytes, writable: false);
            public int DeliveryAttempt { get; private set; } = 1;

            internal void Read(IReadOnlyBasicProperties properties)
            {
                var headers = properties.Headers ?? throw new InvalidDataException();
                string Text(string name)
                {
                    if (!headers.TryGetValue(name, out var value))
                        throw new InvalidDataException();
                    var text = value switch
                    {
                        byte[] data when data.Length <= 512 => new UTF8Encoding(false, true).GetString(data),
                        string s when s.Length <= 512 => s,
                        _ => throw new InvalidDataException()
                    };
                    return text;
                }
                long Number(string name) => headers.TryGetValue(name, out var value) && value is int or long ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : throw new InvalidDataException();
                Metadata = new(Text("schemaVersion"), Guid.Parse(Text("batchId")), Text("traceId"), Text("producerId"),
                    DateTimeOffset.ParseExact(Text("receivedAt"), "O", CultureInfo.InvariantCulture), Text("contentType"), Text("contentEncoding"),
                    Number("compressedBytes"), Text("payloadSha256"), headers.ContainsKey("contractVersion") ? Text("contractVersion") : null);
                Metadata.Validate();
                if (headers.ContainsKey("deliveryAttempt"))
                    DeliveryAttempt = checked((int)Number("deliveryAttempt"));
                if (DeliveryAttempt < 1 || Metadata.CompressedBytes != bytes.Length || bytes.Length < 10
                    || bytes[0] != 0x1f || bytes[1] != 0x8b || bytes[2] != 8 || (bytes[3] & 0xe0) != 0
                    || Metadata.ReceivedAt > DateTimeOffset.UtcNow.AddMinutes(5)
                    || !properties.Persistent || properties.MessageId != Metadata.BatchId.ToString("D")
                    || properties.CorrelationId != Metadata.TraceId || properties.ContentType != Metadata.ContentType || properties.ContentEncoding != Metadata.ContentEncoding
                    || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(Metadata.PayloadSha256)))
                    throw new InvalidDataException();
                var allowed = new Dictionary<string, object?>();
                foreach (var name in new[] { "schemaVersion", "batchId", "traceId", "producerId", "receivedAt", "contentType", "contentEncoding", "compressedBytes", "payloadSha256", "contractVersion" })
                    if (headers.TryGetValue(name, out var value))
                        allowed[name] = value;
                Properties = new BasicProperties
                {
                    Persistent = true,
                    MessageId = properties.MessageId,
                    CorrelationId = properties.CorrelationId,
                    ContentType = Metadata.ContentType,
                    ContentEncoding = Metadata.ContentEncoding,
                    Headers = allowed
                };
            }

            public ValueTask CompleteAsync(CancellationToken cancellationToken) => session.Settle(this, "complete", TimeSpan.Zero, null, cancellationToken);
            public ValueTask RetryAsync(TimeSpan delay, CancellationToken cancellationToken) => session.Settle(this, "retry", delay, null, cancellationToken);
            public ValueTask DeadLetterAsync(string errorCode, CancellationToken cancellationToken) => session.Settle(this, "dead", TimeSpan.Zero, errorCode, cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await Gate.WaitAsync();
                try
                {
                    if (Disposed)
                        return;
                    Disposed = true;
                    await Body.DisposeAsync();
                    CryptographicOperations.ZeroMemory(bytes);
                    session.outstanding.TryRemove(tag, out _);
                }
                finally { Gate.Release(); }
            }
        }
    }
}
