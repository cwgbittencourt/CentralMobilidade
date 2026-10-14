using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;

/// <summary>Returns a receipt only after the matching publisher acknowledgement, never on send alone.</summary>
public sealed class RabbitMqBatchPublisher : IDurableBatchQueue, IAsyncDisposable
{
    private readonly IChannel channel;
    private readonly RabbitMqTopology topology;
    private readonly TimeSpan publishTimeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;
    private bool disposed;
    private bool ownsChannel;

    public RabbitMqBatchPublisher(IChannel channel, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(configuration);
        this.channel = channel;
        topology = new(configuration);
        publishTimeout = configuration.GetValue<TimeSpan?>("Queue:RabbitMq:PublishTimeout") ?? TimeSpan.FromSeconds(5);
        if (publishTimeout.TotalMilliseconds < 1 || publishTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(configuration), "Publish timeout must be a supported positive duration.");
    }

    public static async Task<RabbitMqBatchPublisher> CreateAsync(RabbitMqConnectionManager connections, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var channel = await connections.CreatePublishChannelAsync(cancellationToken);
        try
        {
            return new(channel, configuration)
            {
                ownsChannel = true
            };
        }
        catch { await channel.DisposeAsync(); throw; }
    }

    public async ValueTask<DurablePublishReceipt> PublishAsync(TelemetryBatchEnvelope metadata, Stream gzipBody, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(gzipBody);
        try
        {
            metadata.Validate();
        }
        catch (ValidationException) { throw Failure(ProviderFailureKind.Permanent, TelemetryErrorCodes.RequestInvalid); }
        if (!gzipBody.CanRead)
            throw new ArgumentException("The compressed body must be readable.", nameof(gzipBody));
        await gate.WaitAsync(cancellationToken);
        byte[]? bytes = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(publishTimeout);
            bytes = ArrayPool<byte>.Shared.Rent(checked((int)metadata.CompressedBytes));
            var length = (int)metadata.CompressedBytes;
            var offset = 0;
            while (offset < length)
            {
                var read = await gzipBody.ReadAsync(bytes.AsMemory(offset, length - offset), deadline.Token);
                if (read == 0)
                    throw Failure(ProviderFailureKind.Permanent, TelemetryErrorCodes.RequestInvalid);
                offset += read;
            }
            var extra = new byte[1];
            if (length < 10 || bytes[0] != 0x1f || bytes[1] != 0x8b || bytes[2] != 8 || (bytes[3] & 0xe0) != 0
                || await gzipBody.ReadAsync(extra, deadline.Token) != 0
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, length)), Convert.FromHexString(metadata.PayloadSha256)))
                throw Failure(ProviderFailureKind.Permanent, TelemetryErrorCodes.RequestInvalid);

            if (!initialized)
            {
                await topology.EnsureAsync(channel, deadline.Token);
                initialized = true;
            }
            var sequence = await channel.GetNextPublishSequenceNumberAsync(deadline.Token);
            if (sequence == 0)
                throw Failure(ProviderFailureKind.Permanent);
            var batch = metadata.BatchId.ToString("D");
            var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task Ack(object sender, BasicAckEventArgs args)
            {
                if (args.DeliveryTag == sequence || (args.Multiple && args.DeliveryTag >= sequence))
                    confirmation.TrySetResult();
                return Task.CompletedTask;
            }
            Task Nack(object sender, BasicNackEventArgs args)
            {
                if (args.DeliveryTag == sequence || (args.Multiple && args.DeliveryTag >= sequence))
                    confirmation.TrySetException(Failure(ProviderFailureKind.Transient));
                return Task.CompletedTask;
            }
            Task Returned(object sender, BasicReturnEventArgs args)
            {
                if (args.BasicProperties.MessageId == batch)
                    confirmation.TrySetException(Failure(ProviderFailureKind.Permanent));
                return Task.CompletedTask;
            }
            channel.BasicAcksAsync += Ack;
            channel.BasicNacksAsync += Nack;
            channel.BasicReturnAsync += Returned;
            try
            {
                var headers = new Dictionary<string, object?>
                {
                    ["schemaVersion"] = metadata.SchemaVersion,
                    ["batchId"] = batch,
                    ["traceId"] = metadata.TraceId,
                    ["producerId"] = metadata.ProducerId,
                    ["receivedAt"] = metadata.ReceivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    ["contentType"] = metadata.ContentType,
                    ["contentEncoding"] = metadata.ContentEncoding,
                    ["compressedBytes"] = metadata.CompressedBytes,
                    ["payloadSha256"] = metadata.PayloadSha256
                };
                if (metadata.ContractVersion is not null)
                    headers["contractVersion"] = metadata.ContractVersion;
                var properties = new BasicProperties
                {
                    Persistent = true,
                    ContentType = metadata.ContentType,
                    ContentEncoding = metadata.ContentEncoding,
                    MessageId = batch,
                    CorrelationId = metadata.TraceId,
                    Headers = headers
                };
                await channel.BasicPublishAsync(topology.Options.Exchange, topology.Options.RoutingKey, mandatory: true,
                    properties, bytes.AsMemory(0, length), deadline.Token);
                await confirmation.Task.WaitAsync(deadline.Token);
                return new(new BatchId(metadata.BatchId), DateTimeOffset.UtcNow);
            }
            finally
            {
                channel.BasicAcksAsync -= Ack;
                channel.BasicNacksAsync -= Nack;
                channel.BasicReturnAsync -= Returned;
                // Observe a return/nack fault even when the SDK's own publish await failed first.
                if (confirmation.Task.IsFaulted)
                    _ = confirmation.Task.Exception;
            }
        }
        catch (ProviderOperationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) when (disposed) { throw; }
        catch (PublishException error) { throw Failure(error.IsReturn ? ProviderFailureKind.Permanent : ProviderFailureKind.Transient); }
        catch (OperationInterruptedException error) when (error.ShutdownReason?.ReplyCode is 403 or 406) { throw Failure(ProviderFailureKind.Permanent); }
        catch (Exception) { throw Failure(ProviderFailureKind.Ambiguous); }
        finally
        {
            if (bytes is not null)
                ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
            gate.Release();
        }
    }

    public IAsyncEnumerable<IQueueDelivery> ConsumeAsync(CancellationToken cancellationToken) => throw new NotSupportedException("RabbitMQ consumption is implemented in the worker delivery tasks.");
    private static ProviderOperationException Failure(ProviderFailureKind kind, string code = TelemetryErrorCodes.DurabilityUnconfirmed) => new(code, kind);
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed)
                return;
            disposed = true;
            if (ownsChannel)
                await channel.DisposeAsync();
        }
        finally { gate.Release(); }
    }
}
