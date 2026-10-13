using System;
using System.Linq;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.ContractTests.Errors;

public sealed class TelemetryErrorCodesTests
{
    [Fact]
    public void RejectionCatalogMatchesV1DataModel()
    {
        string[] expected =
        [
            "LP_EMPTY_LINE", "LP_INVALID_SYNTAX", "LP_UNKNOWN_MEASUREMENT", "LP_UNKNOWN_COLUMN",
            "LP_MISSING_REQUIRED_TAG", "LP_MISSING_REQUIRED_FIELD", "LP_INVALID_FIELD_TYPE",
            "LP_INVALID_TIMESTAMP", "LP_TIMESTAMP_OUT_OF_RANGE", "LP_COORDINATE_OUT_OF_RANGE",
            "LP_LINE_TOO_LARGE", "PAYLOAD_DECOMPRESSED_LIMIT", "PAYLOAD_EXPANSION_LIMIT",
            "DUPLICATE_CONFLICT", "STORAGE_SCHEMA_CONFLICT", "STORAGE_REJECTED"
        ];
        TelemetryErrorCodes.RejectionCodes.Order(StringComparer.Ordinal)
            .ShouldBe(expected.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("REQUEST_INVALID", 400, "bad-request")]
    [InlineData("AUTHENTICATION_REQUIRED", 401, "unauthorized")]
    [InlineData("ACCESS_DENIED", 403, "forbidden")]
    [InlineData("RESOURCE_NOT_FOUND", 404, "not-found")]
    [InlineData("UPLOAD_TIMEOUT", 408, "upload-timeout")]
    [InlineData("PAYLOAD_COMPRESSED_LIMIT", 413, "compressed-payload-limit")]
    [InlineData("UNSUPPORTED_MEDIA_TYPE", 415, "unsupported-media-type")]
    [InlineData("INTAKE_BACKPRESSURE", 429, "intake-backpressure")]
    [InlineData("DURABILITY_UNCONFIRMED", 503, "durability-unconfirmed")]
    [InlineData("AUDIT_UNAVAILABLE", 503, "audit-unavailable")]
    [InlineData("INTERNAL_ERROR", 500, "internal-error")]
    public void ProblemDefinitionsHaveStableCodesTypesAndStatuses(string code, int status, string typeSuffix)
    {
        var problem = TelemetryErrorCodes.GetProblem(code);
        problem.Code.ShouldBe(code);
        problem.Status.ShouldBe(status);
        problem.Type.ShouldBe("urn:centralmobilidade:telemetry:problem:v1:" + typeSuffix);
        Uri.IsWellFormedUriString(problem.Type, UriKind.Absolute).ShouldBeTrue();
        string.IsNullOrWhiteSpace(problem.Title).ShouldBeFalse();
        string.IsNullOrWhiteSpace(problem.Detail).ShouldBeFalse();
    }

    [Fact]
    public void LookupsAreOrdinalAndRejectUnknownCodes()
    {
        TelemetryErrorCodes.IsRejectionCode("LP_EMPTY_LINE").ShouldBeTrue();
        TelemetryErrorCodes.IsRejectionCode("lp_empty_line").ShouldBeFalse();
        TelemetryErrorCodes.IsRejectionCode(null).ShouldBeFalse();
        TelemetryErrorCodes.IsRejectionCode("AUTHENTICATION_REQUIRED").ShouldBeFalse();
        TelemetryErrorCodes.TryGetProblem("RESOURCE_NOT_FOUND", out var found).ShouldBeTrue();
        found.ShouldBe(TelemetryErrorCodes.GetProblem("RESOURCE_NOT_FOUND"));
        TelemetryErrorCodes.TryGetProblem("resource_not_found", out var missing).ShouldBeFalse();
        missing.ShouldBeNull();
        TelemetryErrorCodes.TryGetProblem(null, out _).ShouldBeFalse();
        var error = Should.Throw<ArgumentException>(() => TelemetryErrorCodes.GetProblem("sensitive-input"));
        error.Message.ShouldNotContain("sensitive-input");
    }

    [Fact]
    public void PrivacySensitiveFailuresUseGenericFixedDetails()
    {
        TelemetryErrorCodes.GetProblem("AUTHENTICATION_REQUIRED").Detail.ShouldBe("Authentication is required.");
        TelemetryErrorCodes.GetProblem("ACCESS_DENIED").Detail.ShouldBe("Access is denied.");
        TelemetryErrorCodes.GetProblem("RESOURCE_NOT_FOUND").Detail.ShouldBe("The requested resource is unavailable.");
        TelemetryErrorCodes.Problems.Values.Select(problem => problem.Type).Distinct().Count()
            .ShouldBe(TelemetryErrorCodes.Problems.Count);
    }
}
