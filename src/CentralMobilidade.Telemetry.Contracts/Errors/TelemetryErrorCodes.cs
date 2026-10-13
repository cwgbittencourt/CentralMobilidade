using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace CentralMobilidade.Telemetry.Contracts.Errors;

/// <summary>Stable v1 rejection and HTTP problem codes. Never use exception or payload text as a code.</summary>
public static class TelemetryErrorCodes
{
    public const string LpEmptyLine = "LP_EMPTY_LINE";
    public const string LpInvalidSyntax = "LP_INVALID_SYNTAX";
    public const string LpUnknownMeasurement = "LP_UNKNOWN_MEASUREMENT";
    public const string LpUnknownColumn = "LP_UNKNOWN_COLUMN";
    public const string LpMissingRequiredTag = "LP_MISSING_REQUIRED_TAG";
    public const string LpMissingRequiredField = "LP_MISSING_REQUIRED_FIELD";
    public const string LpInvalidFieldType = "LP_INVALID_FIELD_TYPE";
    public const string LpInvalidTimestamp = "LP_INVALID_TIMESTAMP";
    public const string LpTimestampOutOfRange = "LP_TIMESTAMP_OUT_OF_RANGE";
    public const string LpCoordinateOutOfRange = "LP_COORDINATE_OUT_OF_RANGE";
    public const string LpLineTooLarge = "LP_LINE_TOO_LARGE";
    public const string PayloadDecompressedLimit = "PAYLOAD_DECOMPRESSED_LIMIT";
    public const string PayloadExpansionLimit = "PAYLOAD_EXPANSION_LIMIT";
    public const string DuplicateConflict = "DUPLICATE_CONFLICT";
    public const string StorageSchemaConflict = "STORAGE_SCHEMA_CONFLICT";
    public const string StorageRejected = "STORAGE_REJECTED";

    public const string RequestInvalid = "REQUEST_INVALID";
    public const string AuthenticationRequired = "AUTHENTICATION_REQUIRED";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string UploadTimeout = "UPLOAD_TIMEOUT";
    public const string PayloadCompressedLimit = "PAYLOAD_COMPRESSED_LIMIT";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string IntakeBackpressure = "INTAKE_BACKPRESSURE";
    public const string DurabilityUnconfirmed = "DURABILITY_UNCONFIRMED";
    public const string AuditUnavailable = "AUDIT_UNAVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";

    public static IReadOnlySet<string> RejectionCodes
    {
        get;
    } = new[]
    {
        LpEmptyLine,
        LpInvalidSyntax,
        LpUnknownMeasurement,
        LpUnknownColumn,
        LpMissingRequiredTag,
        LpMissingRequiredField,
        LpInvalidFieldType,
        LpInvalidTimestamp,
        LpTimestampOutOfRange,
        LpCoordinateOutOfRange,
        LpLineTooLarge,
        PayloadDecompressedLimit,
        PayloadExpansionLimit,
        DuplicateConflict,
        StorageSchemaConflict,
        StorageRejected
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, TelemetryProblemDefinition> Problems
    {
        get;
    } =
        new Dictionary<string, TelemetryProblemDefinition>(StringComparer.Ordinal)
        {
            [RequestInvalid] = new(RequestInvalid, "urn:centralmobilidade:telemetry:problem:v1:bad-request",
                "Invalid request", 400, "The request metadata or body is invalid."),
            [AuthenticationRequired] = new(AuthenticationRequired, "urn:centralmobilidade:telemetry:problem:v1:unauthorized",
                "Authentication required", 401, "Authentication is required."),
            [AccessDenied] = new(AccessDenied, "urn:centralmobilidade:telemetry:problem:v1:forbidden",
                "Access denied", 403, "Access is denied."),
            [ResourceNotFound] = new(ResourceNotFound, "urn:centralmobilidade:telemetry:problem:v1:not-found",
                "Resource unavailable", 404, "The requested resource is unavailable."),
            [UploadTimeout] = new(UploadTimeout, "urn:centralmobilidade:telemetry:problem:v1:upload-timeout",
                "Upload timed out", 408, "The upload exceeded the configured duration."),
            [PayloadCompressedLimit] = new(PayloadCompressedLimit, "urn:centralmobilidade:telemetry:problem:v1:compressed-payload-limit",
                "Payload too large", 413, "The compressed payload exceeds the configured limit."),
            [UnsupportedMediaType] = new(UnsupportedMediaType, "urn:centralmobilidade:telemetry:problem:v1:unsupported-media-type",
                "Unsupported media type", 415, "Use UTF-8 text/plain with gzip content encoding."),
            [IntakeBackpressure] = new(IntakeBackpressure, "urn:centralmobilidade:telemetry:problem:v1:intake-backpressure",
                "Intake temporarily limited", 429, "Intake capacity is temporarily unavailable. Retry later."),
            [DurabilityUnconfirmed] = new(DurabilityUnconfirmed, "urn:centralmobilidade:telemetry:problem:v1:durability-unconfirmed",
                "Durable acceptance unavailable", 503, "Durable acceptance could not be confirmed. Retry later."),
            [AuditUnavailable] = new(AuditUnavailable, "urn:centralmobilidade:telemetry:problem:v1:audit-unavailable",
                "Operation unavailable", 503, "The operation cannot be completed at this time."),
            [InternalError] = new(InternalError, "urn:centralmobilidade:telemetry:problem:v1:internal-error",
                "Internal error", 500, "An unexpected error prevented the operation from completing."),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static bool IsRejectionCode(string? code) => code is not null && RejectionCodes.Contains(code);

    public static bool TryGetProblem(string? code, [NotNullWhen(true)] out TelemetryProblemDefinition? problem)
    {
        problem = null;
        return code is not null && Problems.TryGetValue(code, out problem);
    }

    public static TelemetryProblemDefinition GetProblem(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return TryGetProblem(code, out var problem)
            ? problem
            : throw new ArgumentException("Unknown HTTP problem code.", nameof(code));
    }
}
