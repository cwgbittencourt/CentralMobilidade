using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Infrastructure.Queue;
using CentralMobilidade.Telemetry.Infrastructure.Queue.PubSub;
using CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;
using Google.Apis.Auth.OAuth2;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CentralMobilidade.Telemetry.Infrastructure.DependencyInjection;

public sealed class QueueRuntimeOptions
{
    public string Provider { get; private init; } = "";
    public bool UseEmulator
    {
        get; private init;
    }
    public string Endpoint { get; private init; } = "";
    public int ConcurrentDeliveries
    {
        get; private init;
    }
    public TimeSpan DrainTimeout
    {
        get; private init;
    }
    public TimeSpan PublishTimeout
    {
        get; private init;
    }
    public TimeSpan StartupTimeout
    {
        get; private init;
    }
    internal IConfiguration Configuration { get; private init; } = null!;
    private QueueRuntimeOptions()
    {
    }

    public static QueueRuntimeOptions Read(IConfiguration configuration, string environmentName)
    {
        try
        {
            var provider = configuration["Queue:Provider"];
            provider = string.Equals(provider, "RabbitMq", StringComparison.OrdinalIgnoreCase) ? "RabbitMq"
                : string.Equals(provider, "PubSub", StringComparison.OrdinalIgnoreCase) ? "PubSub" : throw new ArgumentException();
            var snapshot = new ConfigurationBuilder().AddInMemoryCollection(configuration.GetSection("Queue").AsEnumerable()
                .Concat(new[] { new KeyValuePair<string, string?>("Telemetry:QueueRetention", configuration["Telemetry:QueueRetention"]) })).Build();
            var prefix = "Queue:" + provider + ":";
            var drain = snapshot.GetValue(prefix + "DrainTimeout", TimeSpan.FromSeconds(30));
            var publish = snapshot.GetValue(prefix + "PublishTimeout", TimeSpan.FromSeconds(5));
            var startup = snapshot.GetValue("Queue:StartupTimeout", TimeSpan.FromSeconds(10));
            if (drain < TimeSpan.Zero || drain > TimeSpan.FromMinutes(5) || publish < TimeSpan.FromMilliseconds(1)
                || publish > TimeSpan.FromMinutes(1) || startup < TimeSpan.FromMilliseconds(100) || startup > TimeSpan.FromMinutes(1))
                throw new ArgumentException();
            var emulator = false;
            var endpoint = "";
            int concurrency;
            if (provider == "RabbitMq")
            {
                _ = new RabbitMqTopology(snapshot);
                var host = snapshot[prefix + "Host"];
                var port = snapshot.GetValue(prefix + "Port", 5672);
                concurrency = snapshot.GetValue(prefix + "Prefetch", 8);
                var attempts = snapshot.GetValue(prefix + "MaxAttempts", 10);
                var retry = snapshot.GetValue(prefix + "RetryDelay", TimeSpan.FromSeconds(1));
                var maxRetry = snapshot.GetValue(prefix + "MaxRetryDelay", TimeSpan.FromMinutes(1));
                if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535
                    || string.IsNullOrWhiteSpace(snapshot[prefix + "Username"]) || string.IsNullOrEmpty(snapshot[prefix + "Password"])
                    || concurrency is < 1 or > 64 || attempts is < 1 or > 1000 || retry < TimeSpan.FromMilliseconds(1)
                    || maxRetry < retry || maxRetry > TimeSpan.FromMinutes(10))
                    throw new ArgumentException();
            }
            else
            {
                string[] allowed = ["ProjectId", "UseEmulator", "Endpoint", "Topic", "Subscription", "DeadLetterTopic", "DeadLetterSubscription",
                    "PublishTimeout", "PullTimeout", "MaxAttempts", "DrainTimeout", "RetryDelay", "MaxRetryDelay", "AckDeadlineSeconds",
                    "LeaseRenewalInterval", "MaxLeaseDuration", "ConcurrentDeliveries"];
                if (snapshot.GetSection("Queue:PubSub").GetChildren().Any(child => !allowed.Contains(child.Key, StringComparer.OrdinalIgnoreCase)))
                    throw new ArgumentException();
                if (string.IsNullOrWhiteSpace(snapshot[prefix + "ProjectId"]))
                    throw new ArgumentException();
                _ = PubSubQueueOptions.Read(snapshot);
                emulator = snapshot.GetValue(prefix + "UseEmulator", false);
                endpoint = snapshot[prefix + "Endpoint"] ?? (emulator ? "" : "pubsub.googleapis.com:443");
                if (!Uri.TryCreate("http://" + endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0
                    || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port is < 1 or > 65535
                    || endpoint.Any(char.IsWhiteSpace) || endpoint.Contains("/", StringComparison.Ordinal) || !endpoint.Contains(':', StringComparison.Ordinal))
                    throw new ArgumentException();
                if (emulator && environmentName is not ("Development" or "Testing"))
                    throw new ArgumentException();
                if (emulator && uri.Host.EndsWith(".googleapis.com", StringComparison.Ordinal))
                    throw new ArgumentException();
                if (!emulator && (uri.Port != 443 || !(uri.Host == "pubsub.googleapis.com"
                    || uri.Host.EndsWith("-pubsub.googleapis.com", StringComparison.Ordinal) && uri.Host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))))
                    throw new ArgumentException();
                concurrency = snapshot.GetValue(prefix + "ConcurrentDeliveries", 4);
                if (concurrency is < 1 or > 64)
                    throw new ArgumentException();
                var deadSubscription = snapshot[prefix + "DeadLetterSubscription"];
                if (string.IsNullOrEmpty(deadSubscription) || !SubscriptionName.TryParse(deadSubscription, out var name)
                    || name.ProjectId != snapshot[prefix + "ProjectId"] || deadSubscription == snapshot[prefix + "Subscription"])
                    throw new ArgumentException();
            }
            return new QueueRuntimeOptions
            {
                Provider = provider,
                UseEmulator = emulator,
                Endpoint = endpoint,
                ConcurrentDeliveries = concurrency,
                DrainTimeout = drain,
                PublishTimeout = publish,
                StartupTimeout = startup,
                Configuration = snapshot
            };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Invalid queue provider configuration.");
        }
    }
    public override string ToString() => "[QueueRuntimeOptions]";
}

public static class QueueProviderRegistration
{
    public static IServiceCollection AddTelemetryQueue(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
        => services.AddTelemetryQueue(QueueRuntimeOptions.Read(configuration, environment.EnvironmentName));

    public static IServiceCollection AddTelemetryQueue(this IServiceCollection services, QueueRuntimeOptions options)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(QueueRuntimeOptions)))
            throw new InvalidOperationException("Queue provider is already registered.");
        services.AddSingleton(options);
        if (options.Provider == "RabbitMq")
        {
            services.AddSingleton(_ => new RabbitMqTopology(options.Configuration));
            services.AddSingleton(sp => new RabbitMqConnectionManager(options.Configuration, sp.GetRequiredService<RabbitMqTopology>()));
            services.AddSingleton(sp => new RabbitMqBatchConsumer(sp.GetRequiredService<RabbitMqConnectionManager>(), options.Configuration));
            services.AddSingleton<IMonitoredBatchConsumer>(sp => sp.GetRequiredService<RabbitMqBatchConsumer>());
            services.AddSingleton<IDurableBatchQueue, RabbitMqQueue>();
        }
        else
        {
            services.AddSingleton(_ => new PubSubClients(options));
            services.AddSingleton(sp => new PubSubBatchQueue(sp.GetRequiredService<PubSubClients>().Publisher,
                sp.GetRequiredService<PubSubClients>().Subscriber, options.Configuration));
            services.AddSingleton<IDurableBatchQueue>(sp => sp.GetRequiredService<PubSubBatchQueue>());
            services.AddSingleton<IMonitoredBatchConsumer, PubSubBatchConsumer>();
            services.AddHostedService<PubSubStartup>();
        }
        return services;
    }

    private sealed class RabbitMqQueue(RabbitMqConnectionManager connections, RabbitMqBatchConsumer consumer, QueueRuntimeOptions options)
        : IDurableBatchQueue, IAsyncDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private RabbitMqBatchPublisher? publisher;
        private bool disposed;
        public async ValueTask<DurablePublishReceipt> PublishAsync(TelemetryBatchEnvelope metadata, System.IO.Stream gzipBody, CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                publisher ??= await RabbitMqBatchPublisher.CreateAsync(connections, options.Configuration, token);
                return await publisher.PublishAsync(metadata, gzipBody, token);
            }
            finally { gate.Release(); }
        }
        public IAsyncEnumerable<IQueueDelivery> ConsumeAsync(CancellationToken token) => consumer.ConsumeAsync(token);
        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync();
            try
            {
                if (disposed)
                    return;
                disposed = true;
                if (publisher is not null)
                    await publisher.DisposeAsync();
            }
            finally { gate.Release(); }
        }
    }

    private sealed class PubSubClients : IAsyncDisposable
    {
        private readonly GrpcChannel channel;
        private readonly Lazy<Task<GoogleCredential>> credentials;
        private readonly CancellationTokenSource lifetime = new();
        internal PublisherServiceApiClient Publisher
        {
            get;
        }
        internal SubscriberServiceApiClient Subscriber
        {
            get;
        }
        internal PubSubClients(QueueRuntimeOptions options)
        {
            credentials = new(async () => (await GoogleCredential.GetApplicationDefaultAsync(lifetime.Token))
                .CreateScoped(PublisherServiceApiClient.DefaultScopes));
            var channelOptions = new Grpc.Net.Client.GrpcChannelOptions { MaxReceiveMessageSize = 10 * 1024 * 1024, MaxSendMessageSize = 10 * 1024 * 1024 };
            if (!options.UseEmulator)
                channelOptions.Credentials = ChannelCredentials.Create(new SslCredentials(), CallCredentials.FromInterceptor(async (context, metadata) =>
                {
                    var credential = await credentials.Value.WaitAsync(context.CancellationToken);
                    var token = await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(context.ServiceUrl, context.CancellationToken);
                    metadata.Add("Authorization", "Bearer " + token);
                }));
            channel = GrpcChannel.ForAddress((options.UseEmulator ? "http://" : "https://") + options.Endpoint, channelOptions);
            Publisher = new PublisherServiceApiClientBuilder { CallInvoker = channel.CreateCallInvoker() }.Build();
            Subscriber = new SubscriberServiceApiClientBuilder { CallInvoker = channel.CreateCallInvoker() }.Build();
        }
        internal async Task ValidateCredentials(CancellationToken token) => _ = await credentials.Value.WaitAsync(token);
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            channel.Dispose();
            lifetime.Dispose();
        }
    }

    private sealed class PubSubStartup(IServiceProvider services, QueueRuntimeOptions options) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.StartupTimeout);
            try
            {
                var clients = services.GetRequiredService<PubSubClients>();
                if (!options.UseEmulator)
                    await clients.ValidateCredentials(deadline.Token);
                var settings = CallSettings.FromCancellationToken(deadline.Token).WithTimeout(options.StartupTimeout)
                    .WithRetry(RetrySettings.FromConstantBackoff(1, TimeSpan.FromMilliseconds(1), _ => false));
                var config = options.Configuration;
                foreach (var key in new[] { "Topic", "DeadLetterTopic" })
                    _ = await clients.Publisher.GetTopicAsync(new GetTopicRequest { Topic = config["Queue:PubSub:" + key] }, settings).WaitAsync(deadline.Token);
                foreach (var (key, topic) in new[] { ("Subscription", "Topic"), ("DeadLetterSubscription", "DeadLetterTopic") })
                {
                    var subscription = await clients.Subscriber.GetSubscriptionAsync(new GetSubscriptionRequest { Subscription = config["Queue:PubSub:" + key] }, settings).WaitAsync(deadline.Token);
                    if (subscription.Topic != config["Queue:PubSub:" + topic] || subscription.Detached
                        || !options.UseEmulator && (subscription.MessageRetentionDuration is null || subscription.MessageRetentionDuration.ToTimeSpan() < TimeSpan.FromDays(7)))
                        throw new InvalidOperationException();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { throw new InvalidOperationException("Queue provider startup validation failed."); }
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
