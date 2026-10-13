using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Configuration;

public sealed class TelemetryOptionsTests
{
    [Theory]
    [InlineData("Api")]
    [InlineData("Worker")]
    public async Task ApplicationTemplatesBindAndAllowStartup(string application)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile($"Configuration/Templates/{application}.json", optional: false);
        builder.Services.AddTelemetryOptions(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value;
        options.MaxCompressedBytes.ShouldBe(8 * 1024 * 1024);
        options.LocalTelemetryRetention.ShouldBe(TimeSpan.FromDays(60));
        options.Viewer.HealthMaxPoints.ShouldBe(10000);
        await host.StopAsync();
    }

    [Fact]
    public async Task DefaultsAllowHostStartup()
    {
        using var host = CreateHost(new Dictionary<string, string?>());
        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value;
        options.MaxCompressedBytes.ShouldBe(8 * 1024 * 1024);
        options.MaxDecompressedBytes.ShouldBe(64 * 1024 * 1024);
        options.MaxExpansionRatio.ShouldBe(100);
        options.MaxLineBytes.ShouldBe(16 * 1024);
        options.MaxLinesPerBatch.ShouldBe(50000);
        options.UploadTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.QueueRetention.ShouldBe(TimeSpan.FromDays(7));
        options.WriteBatchMaxPoints.ShouldBe(5000);
        options.WriteBatchMaxBytes.ShouldBe(2 * 1024 * 1024);
        options.WriteBatchFlushInterval.ShouldBe(TimeSpan.FromSeconds(1));
        options.BatchStatusRetention.ShouldBe(TimeSpan.FromDays(30));
        options.QuarantineRetention.ShouldBe(TimeSpan.FromDays(30));
        options.FingerprintEvidenceRetention.ShouldBe(TimeSpan.FromDays(30));
        options.LocalTelemetryRetention.ShouldBe(TimeSpan.FromDays(60));
        options.Viewer.LatestPositionMaxVehicles.ShouldBe(500);
        options.Viewer.TripMaxDuration.ShouldBe(TimeSpan.FromHours(24));
        options.Viewer.TripMaxPoints.ShouldBe(10000);
        options.Viewer.HealthMaxDuration.ShouldBe(TimeSpan.FromDays(7));
        options.Viewer.HealthMinGranularity.ShouldBe(TimeSpan.FromMinutes(1));
        options.Viewer.HealthMaxPoints.ShouldBe(10000);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("MaxCompressedBytes", "0")]
    [InlineData("MaxCompressedBytes", "-1")]
    [InlineData("MaxDecompressedBytes", "0")]
    [InlineData("MaxDecompressedBytes", "-1")]
    [InlineData("MaxExpansionRatio", "NaN")]
    [InlineData("MaxExpansionRatio", "Infinity")]
    [InlineData("MaxExpansionRatio", "0.5")]
    [InlineData("MaxLineBytes", "0")]
    [InlineData("MaxLineBytes", "-1")]
    [InlineData("MaxLinesPerBatch", "0")]
    [InlineData("MaxLinesPerBatch", "-1")]
    [InlineData("UploadTimeout", "00:00:00")]
    [InlineData("QueueRetention", "00:00:00")]
    [InlineData("QueueRetention", "6.23:59:59")]
    [InlineData("WriteBatchMaxPoints", "0")]
    [InlineData("WriteBatchMaxPoints", "-1")]
    [InlineData("WriteBatchMaxBytes", "0")]
    [InlineData("WriteBatchMaxBytes", "-1")]
    [InlineData("WriteBatchFlushInterval", "00:00:00")]
    [InlineData("WriteBatchFlushInterval", "-00:00:01")]
    [InlineData("BatchStatusRetention", "31.00:00:00")]
    [InlineData("BatchStatusRetention", "29.00:00:00")]
    [InlineData("QuarantineRetention", "31.00:00:00")]
    [InlineData("QuarantineRetention", "00:00:00")]
    [InlineData("FingerprintEvidenceRetention", "00:00:00")]
    [InlineData("FingerprintEvidenceRetention", "31.00:00:00")]
    [InlineData("LocalTelemetryRetention", "00:00:00")]
    [InlineData("Viewer:LatestPositionMaxVehicles", "501")]
    [InlineData("Viewer:LatestPositionMaxVehicles", "0")]
    [InlineData("Viewer:TripMaxDuration", "1.00:00:01")]
    [InlineData("Viewer:TripMaxDuration", "00:00:00")]
    [InlineData("Viewer:TripMaxPoints", "10001")]
    [InlineData("Viewer:TripMaxPoints", "0")]
    [InlineData("Viewer:HealthMaxDuration", "7.00:00:01")]
    [InlineData("Viewer:HealthMaxDuration", "00:00:00")]
    [InlineData("Viewer:HealthMinGranularity", "00:00:59")]
    [InlineData("Viewer:HealthMaxPoints", "10001")]
    [InlineData("Viewer:HealthMaxPoints", "0")]
    public async Task InvalidLimitPreventsHostStartup(string key, string value)
    {
        using var host = CreateHost(new Dictionary<string, string?> { ["Telemetry:" + key] = value });
        var error = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync());
        var propertyName = key.Split(':')[^1];
        error.Failures.ShouldContain(failure => failure.Contains(propertyName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InconsistentResourceLimitsPreventStartup()
    {
        using var host = CreateHost(new Dictionary<string, string?>
        {
            ["Telemetry:MaxCompressedBytes"] = "100000",
            ["Telemetry:MaxDecompressedBytes"] = "100",
            ["Telemetry:LocalTelemetryRetention"] = "1.00:00:00",
            ["Telemetry:Viewer:HealthMinGranularity"] = "8.00:00:00"
        });
        var error = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync());
        error.Failures.ShouldContain(failure => failure.Contains("MaxDecompressedBytes", StringComparison.Ordinal));
        error.Failures.ShouldContain(failure => failure.Contains("MaxLineBytes", StringComparison.Ordinal));
        error.Failures.ShouldContain(failure => failure.Contains("LocalTelemetryRetention", StringComparison.Ordinal));
        error.Failures.ShouldContain(failure => failure.Contains("HealthMinGranularity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReducedViewerLimitsBindAndAllowStartup()
    {
        using var host = CreateHost(new Dictionary<string, string?>
        {
            ["Telemetry:Viewer:LatestPositionMaxVehicles"] = "1",
            ["Telemetry:Viewer:TripMaxPoints"] = "1",
            ["Telemetry:Viewer:HealthMaxPoints"] = "1",
            ["Telemetry:Viewer:TripMaxDuration"] = "01:00:00",
            ["Telemetry:Viewer:HealthMaxDuration"] = "01:00:00",
            ["Telemetry:Viewer:HealthMinGranularity"] = "00:05:00"
        });
        await host.StartAsync();
        var viewer = host.Services.GetRequiredService<IOptions<TelemetryOptions>>().Value.Viewer;
        viewer.LatestPositionMaxVehicles.ShouldBe(1);
        viewer.TripMaxPoints.ShouldBe(1);
        viewer.HealthMaxPoints.ShouldBe(1);
        viewer.TripMaxDuration.ShouldBe(TimeSpan.FromHours(1));
        viewer.HealthMinGranularity.ShouldBe(TimeSpan.FromMinutes(5));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("MisspelledLimit", "1")]
    [InlineData("Viewer:MisspelledLimit", "1")]
    [InlineData("MaxCompressedBytes", "not-a-number")]
    [InlineData("UploadTimeout", "not-a-duration")]
    public async Task UnknownOrMalformedConfigurationPreventsStartup(string key, string value)
    {
        using var host = CreateHost(new Dictionary<string, string?> { ["Telemetry:" + key] = value });
        await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync());
    }

    [Fact]
    public void MissingViewerObjectIsRejected()
    {
        var result = new TelemetryOptionsValidator().Validate(null, new TelemetryOptions { Viewer = null! });
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain("Viewer limits are required.");
    }

    [Fact]
    public async Task PositiveResourceBoundariesAllowStartup()
    {
        using var host = CreateHost(new Dictionary<string, string?>
        {
            ["Telemetry:MaxCompressedBytes"] = "1",
            ["Telemetry:MaxDecompressedBytes"] = "1",
            ["Telemetry:MaxLineBytes"] = "1",
            ["Telemetry:MaxExpansionRatio"] = "1",
            ["Telemetry:MaxLinesPerBatch"] = "1",
            ["Telemetry:WriteBatchMaxPoints"] = "1",
            ["Telemetry:WriteBatchMaxBytes"] = "1",
            ["Telemetry:UploadTimeout"] = "00:00:00.001",
            ["Telemetry:WriteBatchFlushInterval"] = "00:00:00.001",
            ["Telemetry:QuarantineRetention"] = "00:00:00.001"
        });
        await host.StartAsync();
        await host.StopAsync();
    }

    private static IHost CreateHost(Dictionary<string, string?> values)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddTelemetryOptions(builder.Configuration);
        return builder.Build();
    }
}
