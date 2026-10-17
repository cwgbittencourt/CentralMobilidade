using System;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Errors;

public sealed class ProviderFailureTests
{
    [Theory]
    [InlineData(TelemetryErrorCodes.DurabilityUnconfirmed, ProviderFailureKind.Ambiguous, 503)]
    [InlineData(TelemetryErrorCodes.AuditUnavailable, ProviderFailureKind.Transient, 503)]
    [InlineData(TelemetryErrorCodes.AccessDenied, ProviderFailureKind.Permanent, 403)]
    public void Public_problem_mapping_preserves_failure_class_without_provider_diagnostics(string code, ProviderFailureKind kind, int status)
    {
        var error = new ProviderOperationException(code, kind);
        error.ErrorCode.ShouldBe(code);
        error.Kind.ShouldBe(kind);
        error.InnerException.ShouldBeNull();
        error.Message.ShouldBe("A provider operation failed.");
        var problem = TelemetryErrorCodes.GetProblem(error.ErrorCode);
        problem.Status.ShouldBe(status);
        problem.Code.ShouldBe(code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PRIVATE_PROVIDER_RESPONSE")]
    [InlineData("audit_unavailable")]
    public void Uncatalogued_provider_codes_cannot_escape_as_public_failures(string? code)
    {
        var error = Should.Throw<ArgumentException>(() => new ProviderOperationException(code!, ProviderFailureKind.Transient));
        error.Message.ShouldNotContain("PRIVATE_PROVIDER_RESPONSE");
        TelemetryErrorCodes.TryGetProblem(code, out var problem).ShouldBeFalse();
        problem.ShouldBeNull();
    }

    [Fact]
    public void Async_rejection_is_not_an_HTTP_problem()
    {
        var error = new ProviderOperationException(TelemetryErrorCodes.DuplicateConflict, ProviderFailureKind.Permanent);
        TelemetryErrorCodes.IsRejectionCode(error.ErrorCode).ShouldBeTrue();
        TelemetryErrorCodes.TryGetProblem(error.ErrorCode, out _).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => new ProviderOperationException(TelemetryErrorCodes.StorageRejected, (ProviderFailureKind)99));
    }
}
