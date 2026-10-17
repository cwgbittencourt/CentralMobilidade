using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
namespace CentralMobilidade.Telemetry.Infrastructure.Observability;

// Fixed instrument names preserve the exporter-wide policy of removing every label.
public sealed class BatchMetrics : IDisposable
{
    public const string MeterName = "CentralMobilidade.Telemetry.Batches";
    private readonly Meter meter = new(MeterName);
    private readonly IReadOnlyDictionary<BatchLifecycle, Counter<long>> states;
    private readonly IReadOnlyDictionary<string, Counter<long>> rejections;
    private readonly Counter<long> identicalReplays;
    public BatchMetrics()
    {
        states = Enum.GetValues<BatchLifecycle>().ToDictionary(state => state,
            state => meter.CreateCounter<long>("telemetry.batch.state." + StateName(state), "{entry}", "Committed entries into a batch lifecycle state"));
        rejections = TelemetryErrorCodes.RejectionCodes.ToDictionary(code => code,
            code => meter.CreateCounter<long>("telemetry.rejections." + code.ToLowerInvariant(), "{line}", "New committed rejected lines"), StringComparer.Ordinal);
        identicalReplays = meter.CreateCounter<long>("telemetry.lines.identical_replay", "{line}", "New committed identical replay outcomes");
    }
    public void RecordState(BatchLifecycle state)
    {
        if (states.TryGetValue(state, out var counter))
            Add(counter);
    }
    public void RecordRejection(string? code)
    {
        // Never create instruments from unknown codes, payload or exception text.
        if (code is not null && rejections.TryGetValue(code, out var counter))
            Add(counter);
    }
    public void RecordIdenticalReplay() => Add(identicalReplays);
    private static void Add(Counter<long> counter)
    {
        try
        {
            counter.Add(1);
        }
        catch (Exception) { /* An observer failure must not turn an already committed operation into failure. */ }
    }
    private static string StateName(BatchLifecycle state) => state switch
    {
        BatchLifecycle.CompletedWithRejections => "completed_with_rejections",
        BatchLifecycle.PublishFailed => "publish_failed",
        _ => state.ToString().ToLowerInvariant()
    };
    public void Dispose() => meter.Dispose();
}
