using System;
using CentralMobilidade.Telemetry.Contracts.Errors;

namespace CentralMobilidade.Telemetry.Application.Abstractions;

public enum ProviderFailureKind
{
    Transient,
    Permanent,
    Ambiguous
}

/// <summary>Safe provider-neutral failure. Adapters keep SDK exceptions and sensitive diagnostics internal.</summary>
public sealed class ProviderOperationException : Exception
{
    public ProviderOperationException(string errorCode, ProviderFailureKind kind)
        : base("A provider operation failed.")
    {
        if (!TelemetryErrorCodes.IsRejectionCode(errorCode) && !TelemetryErrorCodes.TryGetProblem(errorCode, out _))
        {
            throw new ArgumentException("A catalogued error code is required.", nameof(errorCode));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ErrorCode = errorCode;
        Kind = kind;
    }

    public string ErrorCode
    {
        get;
    }

    public ProviderFailureKind Kind
    {
        get;
    }
}
