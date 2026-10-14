using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Queue;

public sealed class RabbitMqTopologyTests(RabbitMqContractBroker broker) : IClassFixture<RabbitMqContractBroker>
{
    [Fact]
    public async Task Durable_topology_is_idempotent_and_routes_retry_and_dead_letters()
    {
        var config = Configuration();
        var topology = new RabbitMqTopology(config);
        await using var connection = await broker.Connect();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        try
        {
            await topology.EnsureAsync(channel);
            await topology.EnsureAsync(channel);
            var options = topology.Options;
            options.Queue.ShouldBe(config["Queue:RabbitMq:Queue"]);
            options.Exchange.ShouldBe(config["Queue:RabbitMq:Exchange"]);
            await channel.ExchangeDeclarePassiveAsync(options.Exchange);
            await channel.ExchangeDeclarePassiveAsync(options.RetryExchange);
            await channel.ExchangeDeclarePassiveAsync(options.DeadLetterExchange);
            await channel.QueueDeclareAsync(options.Queue, true, false, false, new Dictionary<string, object?>
            {
                ["x-queue-type"] = "classic",
                ["x-message-ttl"] = 604800000L,
                ["x-dead-letter-exchange"] = options.DeadLetterExchange
            });
            await channel.QueueDeclareAsync(options.RetryQueue, true, false, false, new Dictionary<string, object?>
            {
                ["x-queue-type"] = "classic",
                ["x-message-ttl"] = 100L,
                ["x-dead-letter-exchange"] = options.Exchange,
                ["x-dead-letter-routing-key"] = options.RoutingKey
            });
            await channel.QueueDeclareAsync(options.DeadLetterQueue, true, false, false, new Dictionary<string, object?>
            {
                ["x-queue-type"] = "classic",
                ["x-message-ttl"] = 604800000L
            });
            await channel.BasicPublishAsync(options.RetryExchange, options.RoutingKey, true, new BasicProperties { Persistent = true }, new byte[] { 1, 2, 3 });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var retried = await Get(channel, options.Queue, timeout.Token);
            retried.Body.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
            await channel.BasicNackAsync(retried.DeliveryTag, false, false);
            var dead = await Get(channel, options.DeadLetterQueue, timeout.Token);
            dead.Body.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
            await channel.BasicAckAsync(dead.DeliveryTag, false);
            options.RetentionMilliseconds.ShouldBe(604800000L);
        }
        finally { await Cleanup(topology); }
    }

    [Fact]
    public async Task Real_connection_and_channel_recover_after_transport_loss()
    {
        var remote = new ConnectionFactory { Uri = broker.Endpoint };
        await using var proxy = new DisconnectProxy(remote.HostName, remote.Port);
        var config = Configuration(proxy.Port);
        var topology = new RabbitMqTopology(config);
        await using var owner = new RabbitMqConnectionManager(config, topology);
        await using var channel = await owner.CreatePublishChannelAsync();
        try
        {
            await channel.BasicPublishAsync(topology.Options.Exchange, topology.Options.RoutingKey, true,
                new BasicProperties { Persistent = true }, new byte[] { 7 });
            proxy.Disconnect();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // A fresh passive RPC detects the broken transport and waits for SDK recovery.
            while (true)
            {
                try
                {
                    if (proxy.ConnectionCount >= 2 && channel.IsOpen)
                    {
                        (await channel.QueueDeclarePassiveAsync(topology.Options.Queue, deadline.Token)).MessageCount.ShouldBe(1u);
                        break;
                    }
                }
                catch (RabbitMQ.Client.Exceptions.AlreadyClosedException) { }
                await Task.Delay(50, deadline.Token);
            }
            await channel.BasicPublishAsync(topology.Options.Exchange, topology.Options.RoutingKey, true,
                new BasicProperties { Persistent = true }, new byte[] { 8 }, deadline.Token);
            (await channel.QueueDeclarePassiveAsync(topology.Options.Queue, deadline.Token)).MessageCount.ShouldBe(2u);
        }
        finally { await Cleanup(topology); }
    }

    [Theory]
    [InlineData("Queue:RabbitMq:Queue", "")]
    [InlineData("Queue:RabbitMq:RetryDelay", "00:00:00")]
    [InlineData("Telemetry:QueueRetention", "1.00:00:00")]
    public void Invalid_topology_is_rejected_before_connecting(string key, string value)
    {
        var config = Configuration();
        config[key] = value;
        Should.Throw<ArgumentException>(() => new RabbitMqTopology(config));
    }

    [Fact]
    public async Task Closed_channel_is_replaced_and_disposed_manager_cannot_connect_again()
    {
        var config = Configuration();
        var topology = new RabbitMqTopology(config);
        await using var manager = new RabbitMqConnectionManager(config, topology);
        try
        {
            await using var first = await manager.CreatePublishChannelAsync();
            await first.CloseAsync();
            await using var second = await manager.CreatePublishChannelAsync();
            second.IsOpen.ShouldBeTrue();
            await second.QueueDeclarePassiveAsync(topology.Options.Queue);
            await manager.DisposeAsync();
            await Should.ThrowAsync<ObjectDisposedException>(() => manager.CreatePublishChannelAsync());
        }
        finally { await Cleanup(topology); }
    }

    [Fact]
    public void Duplicate_resources_and_missing_credentials_are_rejected()
    {
        var config = Configuration();
        config["Queue:RabbitMq:RetryQueue"] = config["Queue:RabbitMq:Queue"];
        Should.Throw<ArgumentException>(() => new RabbitMqTopology(config));
        config = Configuration();
        var topology = new RabbitMqTopology(config);
        config["Queue:RabbitMq:Password"] = "";
        Should.Throw<ArgumentException>(() => new RabbitMqConnectionManager(config, topology));
    }

    private IConfigurationRoot Configuration(int? proxyPort = null)
    {
        var endpoint = new ConnectionFactory { Uri = broker.Endpoint };
        var prefix = "t030." + Guid.NewGuid().ToString("N");
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Queue:RabbitMq:Host"] = proxyPort.HasValue ? "127.0.0.1" : endpoint.HostName,
            ["Queue:RabbitMq:Port"] = (proxyPort ?? endpoint.Port).ToString(),
            ["Queue:RabbitMq:Username"] = endpoint.UserName,
            ["Queue:RabbitMq:Password"] = endpoint.Password,
            ["Queue:RabbitMq:VirtualHost"] = endpoint.VirtualHost,
            ["Queue:RabbitMq:Exchange"] = prefix + ".exchange",
            ["Queue:RabbitMq:Queue"] = prefix + ".queue",
            ["Queue:RabbitMq:RetryExchange"] = prefix + ".retry.exchange",
            ["Queue:RabbitMq:RetryQueue"] = prefix + ".retry.queue",
            ["Queue:RabbitMq:DeadLetterExchange"] = prefix + ".dead.exchange",
            ["Queue:RabbitMq:DeadLetterQueue"] = prefix + ".dead.queue",
            ["Queue:RabbitMq:RoutingKey"] = "batch",
            ["Queue:RabbitMq:RetryDelay"] = "00:00:00.100",
            ["Telemetry:QueueRetention"] = "7.00:00:00"
        }).Build();
    }

    private async Task Cleanup(RabbitMqTopology topology)
    {
        await using var connection = await broker.Connect();
        await using var channel = await connection.CreateChannelAsync();
        var o = topology.Options;
        foreach (var queue in new[] { o.Queue, o.RetryQueue, o.DeadLetterQueue })
            await channel.QueueDeleteAsync(queue);
        foreach (var exchange in new[] { o.Exchange, o.RetryExchange, o.DeadLetterExchange })
            await channel.ExchangeDeleteAsync(exchange);
    }
    private static async Task<BasicGetResult> Get(IChannel channel, string queue, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await channel.BasicGetAsync(queue, false, cancellationToken);
            if (result is not null)
                return result;
            await Task.Delay(25, cancellationToken);
        }
    }

    private sealed class DisconnectProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly List<TcpClient> clients = [];
        private readonly Task loop;
        private int connections;
        internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        internal int ConnectionCount => Volatile.Read(ref connections);
        internal DisconnectProxy(string host, int port)
        {
            listener.Start();
            loop = Run(host, port);
        }
        private async Task Run(string host, int port)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(host, port, stop.Token);
                    lock (clients)
                    {
                        clients.Add(client);
                        clients.Add(upstream);
                    }
                    Interlocked.Increment(ref connections);
                    try
                    {
                        var outbound = client.GetStream().CopyToAsync(upstream.GetStream(), stop.Token);
                        var inbound = upstream.GetStream().CopyToAsync(client.GetStream(), stop.Token);
                        await Task.WhenAny(outbound, inbound);
                        client.Close();
                        upstream.Close();
                        try
                        {
                            await Task.WhenAll(outbound, inbound);
                        }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                    }
                    finally { lock (clients) { clients.Remove(client); clients.Remove(upstream); } }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }
        internal void Disconnect()
        {
            lock (clients)
                foreach (var client in clients)
                    client.Close();
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            Disconnect();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Dispose();
        }
    }
}
