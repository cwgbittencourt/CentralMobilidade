using System;

namespace CentralMobilidade.Telemetry.Domain.ValueObjects;

public sealed record CompanyId
{
    public const int MaxUtf8Bytes = IdentifierValidation.MaxUtf8Bytes;

    public CompanyId(string value)
    {
        Value = IdentifierValidation.Validate(value);
    }

    public string Value { get; }

    public override string ToString() => Value;
}
