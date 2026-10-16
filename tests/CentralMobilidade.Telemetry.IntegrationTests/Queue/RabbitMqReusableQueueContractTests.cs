using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.ContractTests.Queue;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Queue;

[Trait("Category", "Integration")]
[Trait("Feature", "ReusableQueueContract")]
public sealed class RabbitMqReusableQueueContractTests(RabbitMqContractBroker broker) : DurableBatchQueueContractTests, IClassFixture<RabbitMqContractBroker>
{
    protected override async Task<DurableQueueContractHarness> CreateAsync(QueuePublishFault fault = QueuePublishFault.None)
    {
        var prefix = "t076." + Guid.NewGuid().ToString("N");
        var endpoint = broker.Endpoint;
        var credentials = endpoint.UserInfo.Split(':');
        var values = new Dictionary<string, string?>
        {
            ["Queue:RabbitMq:Host"] = endpoint.Host,
            ["Queue:RabbitMq:Port"] = endpoint.Port.ToString(CultureInfo.InvariantCulture),
            ["Queue:RabbitMq:Username"] = Uri.UnescapeDataString(credentials[0]),
            ["Queue:RabbitMq:Password"] = Uri.UnescapeDataString(credentials[1]),
            ["Queue:RabbitMq:Prefetch"] = "1",
            ["Queue:RabbitMq:RetryDelay"] = "00:00:00.050",
            ["Queue:RabbitMq:MaxRetryDelay"] = "00:00:01",
            ["Queue:RabbitMq:DrainTimeout"] = "00:00:00",
            ["Queue:RabbitMq:PublishTimeout"] = fault == QueuePublishFault.HoldConfirmation ? "00:00:05" : "00:00:00.500"
        };
        foreach (var key in new[] { "Exchange", "Queue", "RetryExchange", "RetryQueue", "DeadLetterExchange", "DeadLetterQueue" })
            values["Queue:RabbitMq:" + key] = prefix + "." + key;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var topology = new RabbitMqTopology(config);
        var connections = new RabbitMqConnectionManager(config, topology);
        try
        {
            var channel = await connections.CreatePublishChannelAsync();
            var proxy = DispatchProxy.Create<IChannel, RabbitMqPublisherContractTests.PublicationInterceptor>();
            var interceptor = (RabbitMqPublisherContractTests.PublicationInterceptor)proxy;
            interceptor.Inner = channel;
            interceptor.Fault = fault switch
            {
                QueuePublishFault.HoldConfirmation => "manual-confirm",
                QueuePublishFault.NegativeConfirmation => "nack",
                QueuePublishFault.MissingConfirmation => "timeout",
                _ => null
            };
            return new Harness(connections, channel, topology, new RabbitMqBatchPublisher(proxy, config), new(connections, config), interceptor);
        }
        catch { await connections.DisposeAsync(); throw; }
    }

    private sealed class Harness(RabbitMqConnectionManager connections, IChannel channel, RabbitMqTopology topology, RabbitMqBatchPublisher publisher, RabbitMqBatchConsumer consumer,
        RabbitMqPublisherContractTests.PublicationInterceptor interceptor) : DurableQueueContractHarness
    {
        public override IDurableBatchQueue Publisher => publisher;
        public override IAsyncEnumerable<IQueueDelivery> ConsumeAsync(CancellationToken token) => consumer.ConsumeAsync(token);
        public override Task PublishEntered => interceptor.Entered.Task;
        public override async Task ConfirmAsync() => await interceptor.EmitAck(await channel.GetNextPublishSequenceNumberAsync());
        public override async Task<NativeQueueContractMessage?> ReadNativeAsync(bool deadLetter, bool wait)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            do
            {
                var message = await channel.BasicGetAsync(deadLetter ? topology.Options.DeadLetterQueue : topology.Options.Queue, autoAck: true, deadline.Token);
                if (message is not null)
                {
                    message.BasicProperties.Persistent.ShouldBeTrue();
                    var attributes = message.BasicProperties.Headers!.ToDictionary(p => p.Key, p => p.Value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : Convert.ToString(p.Value, CultureInfo.InvariantCulture)!);
                    message.BasicProperties.MessageId.ShouldBe(attributes["batchId"]);
                    message.BasicProperties.CorrelationId.ShouldBe(attributes["traceId"]);
                    message.BasicProperties.ContentEncoding.ShouldBe(attributes["contentEncoding"]);
                    message.BasicProperties.ContentType.ShouldBe(attributes["contentType"]);
                    return new(message.Body.ToArray(), attributes);
                }
                if (!wait)
                    return null;
                await Task.Delay(20, deadline.Token);
            } while (true);
        }
        public override async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var queue in new[] { topology.Options.Queue, topology.Options.RetryQueue, topology.Options.DeadLetterQueue })
                    await channel.QueueDeleteAsync(queue);
                foreach (var exchange in new[] { topology.Options.Exchange, topology.Options.RetryExchange, topology.Options.DeadLetterExchange })
                    await channel.ExchangeDeleteAsync(exchange);
            }
            finally
            {
                await publisher.DisposeAsync();
                await channel.DisposeAsync();
                await connections.DisposeAsync();
            }
        }
    }
}
