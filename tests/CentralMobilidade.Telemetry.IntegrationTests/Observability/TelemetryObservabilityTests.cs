using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Observability;

public sealed partial class TelemetryObservabilityTests
{
    [Fact]
    public void Traces_keep_w3c_batch_correlation_and_remove_untrusted_attributes()
    {
        using var capture = new CaptureExporter();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource(TelemetryObservability.ActivitySourceName)
            .AddProcessor(new PrivacySpanProcessor()).AddProcessor(new SimpleActivityExportProcessor(capture)).Build();
        using var telemetry = new TelemetryObservability();
        var batch = BatchId.New();
        using (var span = telemetry.StartBatchActivity(TelemetryOperation.ProcessBatch, batch))
        {
            span.ShouldNotBeNull();
            span.IdFormat.ShouldBe(ActivityIdFormat.W3C);
            span.SetTag("url.full", "https://private/CPF123?token=secret");
            span.SetTag("latitude", "-23.123");
            span.SetTag("http.request.method", "secret");
            span.AddBaggage("bearer", "secret");
            span.TraceStateString = "private=secret";
            span.SetStatus(ActivityStatusCode.Error, "driver name secret");
            span.DisplayName = "raw line secret";
        }
        capture.Spans.Count.ShouldBe(1);
        var exported = capture.Spans.Single();
        exported.DisplayName.ShouldBe("telemetry.process_batch");
        exported.GetTagItem("batch.id").ShouldBe(batch.ToString());
        exported.GetTagItem("trace.id").ShouldBe(exported.TraceId.ToHexString());
        exported.GetTagItem("url.full").ShouldBeNull();
        exported.GetTagItem("latitude").ShouldBeNull();
        exported.GetTagItem("http.request.method").ShouldBeNull();
        exported.TraceStateString.ShouldBeNull();
        exported.StatusDescription.ShouldBeNull();
        exported.Baggage.ShouldBeEmpty();
    }

    [Fact]
    public void Spans_with_untrusted_events_are_not_exported()
    {
        using var capture = new CaptureExporter();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource(TelemetryObservability.ActivitySourceName)
            .AddProcessor(new PrivacySpanProcessor()).AddProcessor(new SimpleActivityExportProcessor(capture)).Build();
        using var telemetry = new TelemetryObservability();
        using (var span = telemetry.StartBatchActivity(TelemetryOperation.PublishBatch, BatchId.New()))
            span!.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection { { "exception.message", "secret CPF" } }));
        capture.Spans.ShouldBeEmpty();
    }

    internal sealed class CaptureExporter : BaseExporter<Activity>
    {
        internal List<Activity> Spans { get; } = [];
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var span in batch)
                Spans.Add(span);
            return ExportResult.Success;
        }
    }
}
