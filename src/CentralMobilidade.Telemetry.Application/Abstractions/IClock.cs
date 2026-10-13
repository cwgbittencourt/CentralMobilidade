using System;

namespace CentralMobilidade.Telemetry.Application.Abstractions;

public interface IClock
{
    // Implementations must return offset zero; inject this clock for deterministic lease/retention tests.
    DateTimeOffset UtcNow
    {
        get;
    }
}
