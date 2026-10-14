using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;

public sealed class RabbitMqTopologyOptions
{
    internal RabbitMqTopologyOptions(IConfiguration config)
    {
        string Name(string key, string fallback)
        {
            var value = config["Queue:RabbitMq:" + key] ?? fallback;
            if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 255 || value.Any(char.IsControl)
                || value.StartsWith("amq.", StringComparison.Ordinal))
                throw new ArgumentException("Invalid RabbitMQ topology name.");
            return value;
        }
        Exchange = Name("Exchange", "telemetry.batches.exchange");
        Queue = Name("Queue", "telemetry.batches.queue");
        RoutingKey = Name("RoutingKey", "batch");
        RetryExchange = Name("RetryExchange", "telemetry.batches.retry.exchange");
        RetryQueue = Name("RetryQueue", "telemetry.batches.retry.queue");
        DeadLetterExchange = Name("DeadLetterExchange", "telemetry.batches.dead.exchange");
        DeadLetterQueue = Name("DeadLetterQueue", "telemetry.batches.dead.queue");
        if (new[] { Exchange, RetryExchange, DeadLetterExchange }.Distinct(StringComparer.Ordinal).Count() != 3
            || new[] { Queue, RetryQueue, DeadLetterQueue }.Distinct(StringComparer.Ordinal).Count() != 3)
            throw new ArgumentException("RabbitMQ topology resources must be distinct.");
        var retention = config.GetValue<TimeSpan?>("Telemetry:QueueRetention") ?? TimeSpan.FromDays(7);
        var retry = config.GetValue<TimeSpan?>("Queue:RabbitMq:RetryDelay") ?? TimeSpan.FromSeconds(1);
        if (retention < TimeSpan.FromDays(7) || retention.TotalMilliseconds > long.MaxValue
            || retry.TotalMilliseconds < 1 || retry >= retention)
            throw new ArgumentException("Invalid RabbitMQ retention/retry limits.");
        RetentionMilliseconds = checked((long)retention.TotalMilliseconds);
        RetryMilliseconds = checked((long)retry.TotalMilliseconds);
    }
    public string Exchange
    {
        get;
    }
    public string Queue
    {
        get;
    }
    public string RoutingKey
    {
        get;
    }
    public string RetryExchange
    {
        get;
    }
    public string RetryQueue
    {
        get;
    }
    public string DeadLetterExchange
    {
        get;
    }
    public string DeadLetterQueue
    {
        get;
    }
    public long RetentionMilliseconds
    {
        get;
    }
    public long RetryMilliseconds
    {
        get;
    }
    public override string ToString() => "[RabbitMqTopologyOptions]";
}

public sealed class RabbitMqTopology
{
    public RabbitMqTopology(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Options = new(configuration);
    }
    public RabbitMqTopologyOptions Options
    {
        get;
    }
    public IDictionary<string, object?> MainQueueArguments() => new Dictionary<string, object?>
    {
        ["x-queue-type"] = "classic",
        ["x-message-ttl"] = Options.RetentionMilliseconds,
        ["x-dead-letter-exchange"] = Options.DeadLetterExchange
    };
    public IDictionary<string, object?> RetryQueueArguments() => new Dictionary<string, object?>
    {
        ["x-queue-type"] = "classic",
        ["x-message-ttl"] = Options.RetryMilliseconds,
        ["x-dead-letter-exchange"] = Options.Exchange,
        ["x-dead-letter-routing-key"] = Options.RoutingKey
    };
    public IDictionary<string, object?> DeadQueueArguments() => new Dictionary<string, object?>
    {
        ["x-queue-type"] = "classic",
        ["x-message-ttl"] = Options.RetentionMilliseconds
    };

    public async Task EnsureAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        foreach (var exchange in new[] { Options.Exchange, Options.RetryExchange, Options.DeadLetterExchange })
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(Options.Queue, true, false, false, MainQueueArguments(), cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(Options.RetryQueue, true, false, false, RetryQueueArguments(), cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(Options.DeadLetterQueue, true, false, false, DeadQueueArguments(), cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Options.Queue, Options.Exchange, Options.RoutingKey, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Options.RetryQueue, Options.RetryExchange, Options.RoutingKey, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Options.DeadLetterQueue, Options.DeadLetterExchange, Options.RoutingKey, cancellationToken: cancellationToken);
    }
}

/// <summary>Owns a recovering connection; callers own each returned confirm-enabled channel.</summary>
public sealed class RabbitMqConnectionManager : IAsyncDisposable
{
    private readonly ConnectionFactory factory;
    private readonly RabbitMqTopology topology;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;
    private bool disposed;
    public RabbitMqConnectionManager(IConfiguration config, RabbitMqTopology topology)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(topology);
        this.topology = topology;
        var section = config.GetSection("Queue:RabbitMq");
        var host = section["Host"];
        var username = section["Username"];
        var password = section["Password"];
        var port = section.GetValue<int?>("Port") ?? 5672;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || port is < 1 or > 65535)
            throw new ArgumentException("RabbitMQ connection configuration is incomplete.");
        factory = new ConnectionFactory
        {
            HostName = host,
            Port = port,
            UserName = username,
            Password = password,
            VirtualHost = section["VirtualHost"] ?? "/",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(1),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<IChannel> CreatePublishChannelAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        IChannel? channel = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            connection ??= await factory.CreateConnectionAsync(cancellationToken);
            // Reuse the autorecovering connection rather than disposing channels during recovery.
            using var recoveryDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            recoveryDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (!connection.IsOpen)
                await Task.Delay(50, recoveryDeadline.Token);
            channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), recoveryDeadline.Token);
            await topology.EnsureAsync(channel, recoveryDeadline.Token);
            return channel;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (channel is not null)
                await channel.DisposeAsync();
            throw;
        }
        catch (ObjectDisposedException) when (disposed) { throw; }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException error) when (error.ShutdownReason?.ReplyCode is 403 or 406)
        {
            if (channel is not null)
                await channel.DisposeAsync();
            throw new ProviderOperationException(TelemetryErrorCodes.DurabilityUnconfirmed, ProviderFailureKind.Permanent);
        }
        catch (Exception)
        {
            if (channel is not null)
                await channel.DisposeAsync();
            throw new ProviderOperationException(TelemetryErrorCodes.DurabilityUnconfirmed, ProviderFailureKind.Transient);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed)
                return;
            disposed = true;
            if (connection is not null)
                await connection.DisposeAsync();
        }
        finally { gate.Release(); }
    }
}
