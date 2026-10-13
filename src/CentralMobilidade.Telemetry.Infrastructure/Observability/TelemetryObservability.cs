using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CentralMobilidade.Telemetry.Infrastructure.Observability;

public enum TelemetryServiceRole
{
    Api, Worker
}
public enum TelemetryOperation
{
    ReceiveBatch, PublishBatch, ProcessBatch, WriteTelemetry, Quarantine, PromoteTelemetry
}

public sealed class TelemetryObservability : IDisposable
{
    public const string ActivitySourceName = "CentralMobilidade.Telemetry";
    public const string MeterName = "CentralMobilidade.Telemetry";
    private readonly ActivitySource source = new(ActivitySourceName);
    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> received;
    private readonly Counter<long> compressed;
    private readonly Counter<long> decompressed;
    private readonly Histogram<double> processing;
    private readonly Histogram<double> writes;

    public TelemetryObservability()
    {
        received = meter.CreateCounter<long>("telemetry.batches.received", "{batch}");
        compressed = meter.CreateCounter<long>("telemetry.input.compressed_bytes", "By");
        decompressed = meter.CreateCounter<long>("telemetry.input.decompressed_bytes", "By");
        processing = meter.CreateHistogram<double>("telemetry.processing.duration", "s");
        writes = meter.CreateHistogram<double>("telemetry.storage.write.duration", "s");
    }

    public Activity? StartBatchActivity(TelemetryOperation operation, BatchId batchId, ActivityContext parent = default)
    {
        ArgumentNullException.ThrowIfNull(batchId);
        var name = OperationName(operation);
        var span = source.StartActivity("telemetry." + name, ActivityKind.Internal, parent);
        span?.SetTag("telemetry.operation", name);
        span?.SetTag("batch.id", batchId.ToString());
        span?.SetTag("trace.id", span.TraceId.ToHexString());
        return span;
    }

    internal static string OperationName(TelemetryOperation operation) => operation switch
    {
        TelemetryOperation.ReceiveBatch => "receive_batch",
        TelemetryOperation.PublishBatch => "publish_batch",
        TelemetryOperation.ProcessBatch => "process_batch",
        TelemetryOperation.WriteTelemetry => "write_telemetry",
        TelemetryOperation.Quarantine => "quarantine",
        TelemetryOperation.PromoteTelemetry => "promote_telemetry",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public void RecordReceived(long compressedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(compressedBytes);
        received.Add(1);
        compressed.Add(compressedBytes);
    }
    public void RecordDecompressed(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        decompressed.Add(bytes);
    }
    public void RecordProcessingDuration(TimeSpan duration) => RecordDuration(processing, duration);
    public void RecordWriteDuration(TimeSpan duration) => RecordDuration(writes, duration);
    private static void RecordDuration(Histogram<double> histogram, TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        histogram.Record(duration.TotalSeconds);
    }
    public void Dispose()
    {
        source.Dispose();
        meter.Dispose();
    }
}

public static class TelemetryObservabilityRegistration
{
    public static IServiceCollection AddTelemetryObservability(this IServiceCollection services, IConfiguration configuration,
        TelemetryServiceRole role)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        var options = new ObservabilityOptions();
        configuration.GetSection("Observability").Bind(options, b => b.ErrorOnUnknownConfiguration = true);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<TelemetryObservability>();
        // Only W3C trace context crosses dependency boundaries. Never propagate baggage.
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.Clear().AddService(role == TelemetryServiceRole.Api
                ? "centralmobilidade.telemetry.api" : "centralmobilidade.telemetry.worker", serviceVersion: "1.0.0"))
            .WithTracing(tracing =>
            {
                tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.TraceSamplingRatio)))
                    .AddSource(TelemetryObservability.ActivitySourceName)
                    .AddHttpClientInstrumentation(o => o.RecordException = false)
                    .AddProcessor(new PrivacySpanProcessor());
                if (role == TelemetryServiceRole.Api)
                    tracing.AddAspNetCoreInstrumentation(o =>
                    {
                        o.RecordException = false;
                        o.Filter = context => context.Request.Path != "/metrics" && !context.Request.Path.StartsWithSegments("/health");
                    });
                if (options.OtlpTraceEndpoint is not null)
                    tracing.AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(options.OtlpTraceEndpoint);
                        o.Protocol = OtlpExportProtocol.HttpProtobuf;
                        o.TimeoutMilliseconds = 5000;
                    });
            })
            .WithMetrics(metrics => metrics.AddMeter(TelemetryObservability.MeterName, "Microsoft.AspNetCore.Hosting", "System.Net.Http")
                .AddRuntimeInstrumentation()
                .SetExemplarFilter(ExemplarFilterType.AlwaysOff)
                // Fail closed on labels, including unknown tags and their values. Correlation belongs in traces.
                .AddView(_ => new MetricStreamConfiguration { TagKeys = [] })
                .AddPrometheusExporter(o => o.ScrapeResponseCacheDurationMilliseconds = 0));
        if (role == TelemetryServiceRole.Worker)
            services.AddHostedService<WorkerMetricsEndpoint>();
        return services;
    }

    public static IEndpointConventionBuilder MapTelemetryMetrics(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPrometheusScrapingEndpoint("/metrics");
}

public sealed class ObservabilityOptions
{
    public double TraceSamplingRatio { get; set; } = 1;
    public string? OtlpTraceEndpoint
    {
        get; set;
    }
    public string WorkerMetricsUrl { get; set; } = "http://127.0.0.1:9464";
    public void Validate()
    {
        if (!double.IsFinite(TraceSamplingRatio) || TraceSamplingRatio < 0 || TraceSamplingRatio > 1)
            throw new InvalidOperationException("Observability trace sampling ratio must be between zero and one.");
        if (OtlpTraceEndpoint is not null && !ValidHttpUri(OtlpTraceEndpoint))
            throw new InvalidOperationException("Observability OTLP endpoint must be an HTTP(S) URI without credentials, query or fragment.");
        if (!ValidHttpUri(WorkerMetricsUrl) || new Uri(WorkerMetricsUrl).AbsolutePath != "/")
            throw new InvalidOperationException("Observability worker metrics URL must be an HTTP(S) origin.");
    }
    private static bool ValidHttpUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
