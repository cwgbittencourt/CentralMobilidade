using System;
using System.Globalization;

namespace CentralMobilidade.Telemetry.Domain.ValueObjects;

public sealed record EventTimestamp
{
    public EventTimestamp(DateTimeOffset value)
    {
        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException("Event timestamp must have exact millisecond precision.", nameof(value));
        }

        Value = value.ToUniversalTime();
    }

    public DateTimeOffset Value
    {
        get;
    }

    public long UnixMilliseconds => Value.ToUnixTimeMilliseconds();

    public static EventTimestamp FromUnixTimeMilliseconds(long value) => new(DateTimeOffset.FromUnixTimeMilliseconds(value));

    public override string ToString() => Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
