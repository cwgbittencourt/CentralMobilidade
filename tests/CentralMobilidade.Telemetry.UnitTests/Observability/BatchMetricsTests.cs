using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Infrastructure.Observability;
using Shouldly;
using Xunit;
namespace CentralMobilidade.Telemetry.UnitTests.Observability;

public sealed class BatchMetricsTests
{
    [Fact]
    public void Instruments_are_closed_and_measurements_never_carry_labels()
    {
        var instruments = new HashSet<string>();
        var values = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name != BatchMetrics.MeterName)
                return;
            instruments.Add(instrument.Name);
            owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => { tags.Length.ShouldBe(0); values.Add(value); });
        listener.Start();
        using var metrics = new BatchMetrics();
        foreach (var state in Enum.GetValues<BatchLifecycle>())
            metrics.RecordState(state);
        foreach (var code in TelemetryErrorCodes.RejectionCodes)
            metrics.RecordRejection(code);
        metrics.RecordRejection("PRIVATE_TOKEN_PLATE_CPF");
        instruments.Count.ShouldBe(Enum.GetValues<BatchLifecycle>().Length + TelemetryErrorCodes.RejectionCodes.Count);
        instruments.ShouldNotContain(name => name.Contains("PRIVATE", StringComparison.Ordinal));
        values.Count.ShouldBe(instruments.Count);
        values.ShouldAllBe(value => value == 1);
    }
    [Fact]
    public void A_broken_metrics_listener_cannot_fail_a_business_operation()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == BatchMetrics.MeterName) owner.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => throw new InvalidOperationException("PRIVATE_EXPORTER_FAILURE"));
        listener.Start();
        using var metrics = new BatchMetrics();
        Should.NotThrow(() => metrics.RecordState(BatchLifecycle.Accepted));
        Should.NotThrow(() => metrics.RecordRejection(TelemetryErrorCodes.LpInvalidSyntax));
    }
}
