using System;

namespace CentralMobilidade.Telemetry.Domain.ValueObjects;

public sealed record BatchId
{
    public BatchId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Batch identifier must not be an empty UUID.", nameof(value));
        }

        Value = value;
    }

    public Guid Value
    {
        get;
    }

    public static BatchId New() => new(Guid.NewGuid());

    public static BatchId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Guid.TryParseExact(value, "D", out var parsed))
        {
            throw new ArgumentException("Batch identifier must be a hyphenated UUID.", nameof(value));
        }

        return new BatchId(parsed);
    }

    public override string ToString() => Value.ToString("D");
}
