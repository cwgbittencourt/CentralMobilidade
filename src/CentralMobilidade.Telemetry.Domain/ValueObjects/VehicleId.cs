using System;

namespace CentralMobilidade.Telemetry.Domain.ValueObjects;

public sealed record VehicleId
{
    public const int MaxUtf8Bytes = IdentifierValidation.MaxUtf8Bytes;

    public VehicleId(string value)
    {
        Value = IdentifierValidation.Validate(value);
    }

    public string Value { get; }

    public override string ToString() => Value;
}
