using System;

namespace CentralMobilidade.Telemetry.Domain.ValueObjects;

public sealed record DriverId
{
    public const int MaxUtf8Bytes = IdentifierValidation.MaxUtf8Bytes;

    public DriverId(string value)
    {
        Value = IdentifierValidation.Validate(value);
    }

    public string Value
    {
        get;
    }

    public override string ToString() => "[DriverId]";
}
