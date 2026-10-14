using CentralMobilidade.Telemetry.Api.Endpoints.V1;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Ingestion;

public sealed class IntakeAdmissionGateTests
{
    [Fact]
    public void Admission_is_bounded_non_blocking_and_reusable()
    {
        using var gate = new IntakeAdmissionGate(capacity: 1, retryAfterSeconds: 3);

        gate.TryEnter(out var first).ShouldBeTrue();
        gate.TryEnter(out var rejected).ShouldBeFalse();
        rejected.ShouldBeNull();
        gate.RetryAfterSeconds.ShouldBe(3);

        first!.Dispose();
        gate.TryEnter(out var next).ShouldBeTrue();
        next!.Dispose();
    }
}
