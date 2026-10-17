using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Shouldly;
using Xunit;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Infrastructure.DependencyInjection;
using CentralMobilidade.Telemetry.Infrastructure.Queue;
using CentralMobilidade.Telemetry.Infrastructure.Queue.PubSub;
using CentralMobilidade.Telemetry.Infrastructure.Queue.RabbitMq;
using System.Threading.Tasks;

namespace CentralMobilidade.Telemetry.UnitTests.Queue;

public sealed class QueueProviderRegistrationTests
{
    [Theory]
    [InlineData("Queue:Provider", "unknown")]
    [InlineData("Queue:Provider", "")]
    [InlineData("Queue:PubSub:ProjectId", "")]
    [InlineData("Queue:PubSub:UseEmulator", "not-a-bool")]
    [InlineData("Queue:PubSub:Endpoint", "http://localhost:8085")]
    [InlineData("Queue:PubSub:Endpoint", "localhost:0")]
    [InlineData("Queue:PubSub:Endpoint", "user:secret@localhost:8085")]
    [InlineData("Queue:PubSub:Topic", "projects/other/topics/main")]
    [InlineData("Queue:PubSub:DeadLetterTopic", "projects/project/topics/main")]
    [InlineData("Queue:PubSub:Subscription", "projects/project/topics/main")]
    [InlineData("Queue:PubSub:PullTimeout", "00:00:00")]
    [InlineData("Queue:PubSub:DeadLetterSubscription", "")]
    [InlineData("Queue:PubSub:Endpoint", "pubsub.googleapis.com:443")]
    [InlineData("Queue:PubSub:Surprise", "synthetic-private")]
    [InlineData("Queue:PubSub:ConcurrentDeliveries", "0")]
    [InlineData("Queue:PubSub:ConcurrentDeliveries", "65")]
    [InlineData("Queue:StartupTimeout", "00:00:00")]
    public void Invalid_selected_provider_settings_fail_before_any_network_or_credentials(string key, string value)
    {
        var settings = Settings();
        settings[key] = value;
        var error = Should.Throw<InvalidOperationException>(() => Register(settings));
        error.Message.ShouldBe("Invalid queue provider configuration.");
        error.InnerException.ShouldBeNull();
    }

    [Fact]
    public void Emulator_mode_is_rejected_in_production()
    {
        var error = Should.Throw<InvalidOperationException>(() => Register(Settings(), "Production"));
        error.Message.ShouldBe("Invalid queue provider configuration.");
    }

    [Theory]
    [InlineData("Password", "")]
    [InlineData("Host", "http://user:secret@localhost")]
    [InlineData("Port", "0")]
    [InlineData("Prefetch", "0")]
    [InlineData("MaxAttempts", "0")]
    [InlineData("PublishTimeout", "-00:00:01")]
    public void Invalid_selected_rabbit_settings_fail_before_connecting(string key, string value)
    {
        var settings = Settings();
        settings["Queue:Provider"] = "RabbitMq";
        settings["Queue:RabbitMq:Host"] = "localhost";
        settings["Queue:RabbitMq:Username"] = "synthetic";
        settings["Queue:RabbitMq:Password"] = "synthetic";
        settings["Queue:RabbitMq:" + key] = value;
        Should.Throw<InvalidOperationException>(() => Register(settings)).Message.ShouldBe("Invalid queue provider configuration.");
    }

    [Theory]
    [InlineData("PubSub")]
    [InlineData("pUbSuB")]
    public async Task Only_selected_pubsub_services_are_registered_without_reading_inactive_rabbit_credentials(string provider)
    {
        var settings = Settings();
        settings["Queue:Provider"] = provider;
        settings["Queue:RabbitMq:Port"] = "synthetic-private";
        await using var services = Register(settings).BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        services.GetRequiredService<IDurableBatchQueue>().ShouldBeOfType<PubSubBatchQueue>();
        services.GetRequiredService<IMonitoredBatchConsumer>().ShouldBeOfType<PubSubBatchConsumer>();
        services.GetService<RabbitMqConnectionManager>().ShouldBeNull();
    }

    [Fact]
    public async Task Rabbit_registration_is_lazy_and_ignores_invalid_inactive_pubsub_configuration()
    {
        var settings = Settings();
        settings["Queue:Provider"] = "RabbitMq";
        settings["Queue:RabbitMq:Host"] = "localhost";
        settings["Queue:RabbitMq:Username"] = "synthetic";
        settings["Queue:RabbitMq:Password"] = "synthetic";
        settings["Queue:PubSub:UseEmulator"] = "invalid";
        await using var services = Register(settings).BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        services.GetRequiredService<IDurableBatchQueue>().ShouldNotBeNull();
        services.GetRequiredService<IMonitoredBatchConsumer>().ShouldBeOfType<RabbitMqBatchConsumer>();
        services.GetService<PubSubBatchQueue>().ShouldBeNull();
    }

    [Theory]
    [InlineData("localhost:8085")]
    [InlineData("http://pubsub.googleapis.com:443")]
    [InlineData("pubsub.googleapis.com:80")]
    [InlineData("pubsub.googleapis.com:443?secret=value")]
    public void Managed_mode_refuses_insecure_or_custom_cloud_destination(string endpoint)
    {
        var settings = Settings();
        settings["Queue:PubSub:UseEmulator"] = "false";
        settings["Queue:PubSub:Endpoint"] = endpoint;
        Should.Throw<InvalidOperationException>(() => Register(settings, "Production"));
    }

    [Fact]
    public void Managed_mode_uses_explicit_tls_endpoint_and_redacts_option_formatting()
    {
        var settings = Settings();
        settings["Queue:PubSub:UseEmulator"] = "false";
        settings.Remove("Queue:PubSub:Endpoint");
        var options = QueueRuntimeOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), "Production");
        options.Endpoint.ShouldBe("pubsub.googleapis.com:443");
        options.UseEmulator.ShouldBeFalse();
        options.ToString().ShouldBe("[QueueRuntimeOptions]");
    }

    [Fact]
    public void Duplicate_registration_and_runtime_provider_mutation_do_not_change_the_selected_adapter()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();
        var services = new ServiceCollection();
        var environment = new EnvironmentStub("Development");
        services.AddTelemetryQueue(config, environment);
        config["Queue:Provider"] = "RabbitMq";
        config["Queue:PubSub:Endpoint"] = "other:8085";
        var options = (QueueRuntimeOptions)System.Linq.Enumerable.Single(services, s => s.ServiceType == typeof(QueueRuntimeOptions)).ImplementationInstance!;
        options.Provider.ShouldBe("PubSub");
        options.Endpoint.ShouldBe("localhost:8085");
        Should.Throw<InvalidOperationException>(() => services.AddTelemetryQueue(options));
    }

    internal static Dictionary<string, string?> Settings() => new()
    {
        ["Queue:Provider"] = "PubSub",
        ["Queue:PubSub:ProjectId"] = "project",
        ["Queue:PubSub:UseEmulator"] = "true",
        ["Queue:PubSub:Endpoint"] = "localhost:8085",
        ["Queue:PubSub:Topic"] = "projects/project/topics/main",
        ["Queue:PubSub:Subscription"] = "projects/project/subscriptions/main",
        ["Queue:PubSub:DeadLetterTopic"] = "projects/project/topics/dead",
        ["Queue:PubSub:DeadLetterSubscription"] = "projects/project/subscriptions/dead"
    };

    private static IServiceCollection Register(Dictionary<string, string?> settings, string environment = "Development")
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return services.AddTelemetryQueue(config, new EnvironmentStub(environment));
    }

    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "queue-test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
