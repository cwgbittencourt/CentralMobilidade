using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Observability;

public sealed partial class TelemetryObservabilityTests
{
    [Fact]
    public async Task Prometheus_scrape_exports_aggregate_metrics_without_seeded_labels_or_exemplars()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Api);
        await using var app = builder.Build();
        app.MapTelemetryMetrics();
        app.MapGet("/", () => "ok");
        await app.StartAsync();
        var telemetry = app.Services.GetRequiredService<TelemetryObservability>();
        telemetry.RecordReceived(128);
        telemetry.RecordDecompressed(1024);
        telemetry.RecordProcessingDuration(TimeSpan.FromMilliseconds(20));
        using var injected = new Meter(TelemetryObservability.MeterName);
        var counter = injected.CreateCounter<long>("telemetry.test.privacy");
        counter.Add(1, new TagList { { "plate", "ABC1D23" }, { "latitude", "-23.123456" }, { "cpf", "12345678900" },
            { "bearer", "cm_p_SEEDED_SECRET" }, { "batch.id", Guid.NewGuid().ToString() }, { "empresa_id", "seeded-company" },
            { "outcome", "SEEDED_DRIVER_NAME" }, { "trace.id", "SEEDED_TRACE_VALUE" } });
        using var client = app.GetTestClient();
        await client.GetAsync("/?token=cm_p_SEEDED_SECRET&driver=SEEDED_DRIVER_NAME");
        var text = await client.GetStringAsync("/metrics");
        text.ShouldContain("telemetry_batches_received");
        text.ShouldContain("telemetry_input_compressed_bytes");
        text.ShouldContain("telemetry_processing_duration");
        text.ShouldContain("telemetry_test_privacy");
        foreach (var seed in new[] { "ABC1D23", "-23.123456", "12345678900", "cm_p_SEEDED_SECRET", "SEEDED_DRIVER_NAME", "seeded-company", "SEEDED_TRACE_VALUE" })
            text.ShouldNotContain(seed);
        text.ShouldNotContain("batch_id=");
        var resource = app.Services.GetRequiredService<TracerProvider>().GetResource();
        resource.Attributes.Single(a => a.Key == "service.name").Value.ShouldBe("centralmobilidade.telemetry.api");
        await app.StopAsync();
    }

    [Fact]
    public async Task Api_http_spans_remove_raw_url_query_headers_and_preserve_trace_context()
    {
        using var capture = new CaptureExporter();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Api);
        builder.Services.AddOpenTelemetry().WithTracing(t => t.AddProcessor(new OpenTelemetry.SimpleActivityExportProcessor(capture)));
        await using var app = builder.Build();
        app.MapGet("/", (TelemetryObservability telemetry) =>
        {
            using var span = telemetry.StartBatchActivity(TelemetryOperation.ReceiveBatch, BatchId.New());
            return "ok";
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/?cpf=12345678900&token=SEEDED_SECRET");
        request.Headers.Add("traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        request.Headers.Add("baggage", "driver=SEEDED_DRIVER_NAME");
        using var response = await client.SendAsync(request);
        capture.Spans.ShouldNotBeEmpty();
        foreach (var span in capture.Spans)
        {
            span.TraceId.ToHexString().ShouldBe("0123456789abcdef0123456789abcdef");
            span.DisplayName.ShouldBeOneOf("http.server", "telemetry.receive_batch");
            span.TagObjects.Any(t => t.Key is "url.full" or "url.path" or "url.query" or "http.route").ShouldBeFalse();
            span.Baggage.ShouldBeEmpty();
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task Worker_serves_metrics_and_stops_the_listener()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["Observability:WorkerMetricsUrl"] = "http://127.0.0.1:0";
        builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Worker);
        using var host = builder.Build();
        await host.StartAsync();
        host.Services.GetRequiredService<TelemetryObservability>().RecordReceived(16);
        var endpoint = host.Services.GetServices<IHostedService>().OfType<WorkerMetricsEndpoint>().Single();
        using var client = new HttpClient();
        var text = await client.GetStringAsync(endpoint.ListenUrl + "/metrics");
        text.ShouldContain("telemetry_batches_received");
        await host.StopAsync();
        endpoint.ListenUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData("Observability:TraceSamplingRatio", "2")]
    [InlineData("Observability:OtlpTraceEndpoint", "https://user:secret@localhost:4317")]
    [InlineData("Observability:WorkerMetricsUrl", "http://localhost:9464/?token=secret")]
    [InlineData("Observability:Unknown", "secret")]
    public void Invalid_configuration_is_rejected_without_printing_values(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        config[key] = value;
        var error = Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddTelemetryObservability(config, TelemetryServiceRole.Api));
        error.Message.ShouldNotContain(value);
    }
}
