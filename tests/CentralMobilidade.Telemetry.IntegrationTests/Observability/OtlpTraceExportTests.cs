using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Observability;

public sealed partial class TelemetryObservabilityTests
{
    [Fact]
    public async Task Otlp_http_protobuf_exports_sanitized_traces_to_a_real_collector_endpoint()
    {
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collectorBuilder = WebApplication.CreateSlimBuilder();
        collectorBuilder.Logging.ClearProviders();
        await using var collector = collectorBuilder.Build();
        collector.Urls.Add("http://127.0.0.1:0");
        collector.MapPost("/v1/traces", async (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            using var bytes = new MemoryStream();
            await context.Request.Body.CopyToAsync(bytes);
            context.Response.ContentType = "application/x-protobuf";
            received.TrySetResult(bytes.ToArray());
        });
        await collector.StartAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["Observability:OtlpTraceEndpoint"] = collector.Urls.Single() + "/v1/traces";
        builder.Configuration["Observability:WorkerMetricsUrl"] = "http://127.0.0.1:0";
        builder.Services.AddTelemetryObservability(builder.Configuration, TelemetryServiceRole.Worker);
        using var host = builder.Build();
        await host.StartAsync();
        var telemetry = host.Services.GetRequiredService<TelemetryObservability>();
        var batch = BatchId.New();
        using (var span = telemetry.StartBatchActivity(TelemetryOperation.ProcessBatch, batch))
        {
            span!.SetTag("motorista_id", "SEEDED_DRIVER");
            span.SetTag("authorization", "SEEDED_BEARER");
            span.SetTag("url.full", "https://private/?cpf=SEEDED_CPF");
            span.SetStatus(ActivityStatusCode.Error, "SEEDED_EXCEPTION");
        }
        using (var unsafeSpan = telemetry.StartBatchActivity(TelemetryOperation.PublishBatch, batch))
            unsafeSpan!.AddEvent(new ActivityEvent("SEEDED_EVENT"));
        host.Services.GetRequiredService<TracerProvider>().ForceFlush(5000).ShouldBeTrue();
        var text = Encoding.UTF8.GetString(await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        text.ShouldContain("centralmobilidade.telemetry.worker");
        text.ShouldContain("telemetry.process_batch");
        text.ShouldContain(batch.ToString());
        foreach (var seed in new[] { "SEEDED_DRIVER", "SEEDED_BEARER", "SEEDED_CPF", "SEEDED_EXCEPTION", "SEEDED_EVENT" })
            text.ShouldNotContain(seed);
        text.ShouldNotContain("telemetry.publish_batch");
        await host.StopAsync();
        await collector.StopAsync();
    }
}
