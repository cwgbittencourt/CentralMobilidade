using System;
using System.Diagnostics;
using System.Linq;
using OpenTelemetry;

namespace CentralMobilidade.Telemetry.Infrastructure.Observability;

public sealed class PrivacySpanProcessor : BaseProcessor<Activity>
{
    public override void OnStart(Activity span)
    {
        span.TraceStateString = null;
        foreach (var baggage in span.Baggage.ToArray())
            span.SetBaggage(baggage.Key, null);
    }

    public override void OnEnd(Activity span)
    {
        // Activity events/links have no public removal API. Suppress the entire unsafe span before exporters.
        if (span.Events.Any() || span.Links.Any())
            span.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        span.TraceStateString = null;
        foreach (var baggage in span.Baggage.ToArray())
            span.SetBaggage(baggage.Key, null);
        span.SetStatus(span.Status, null);
        var operation = span.GetTagItem("telemetry.operation") as string;
        var knownOperation = operation is "receive_batch" or "publish_batch" or "process_batch" or "write_telemetry" or "quarantine" or "promote_telemetry";
        span.DisplayName = span.Source.Name == TelemetryObservability.ActivitySourceName && knownOperation
            ? "telemetry." + operation : span.Kind == ActivityKind.Server ? "http.server" : "http.client";
        foreach (var tag in span.TagObjects.ToArray())
        {
            var allowed = tag.Key switch
            {
                "telemetry.operation" => knownOperation,
                "batch.id" => tag.Value is string id && Guid.TryParseExact(id, "D", out var batch) && batch != Guid.Empty,
                "trace.id" => tag.Value is string trace && trace == span.TraceId.ToHexString(),
                "http.request.method" => tag.Value is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS",
                "http.response.status_code" => tag.Value is int code && code is >= 100 and <= 599,
                _ => false
            };
            if (!allowed)
                span.SetTag(tag.Key, null);
        }
        span.SetTag("trace.id", span.TraceId.ToHexString());
    }
}
