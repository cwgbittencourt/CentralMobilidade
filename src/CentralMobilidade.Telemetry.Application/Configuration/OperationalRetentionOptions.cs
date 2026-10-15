using System;
namespace CentralMobilidade.Telemetry.Application.Configuration;

public sealed class OperationalRetentionOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PassTimeout { get; set; } = TimeSpan.FromMinutes(1);
    public int PageSize { get; set; } = 200;
    public int MaxPages { get; set; } = 20;
    public bool IsValid() => Interval >= TimeSpan.FromSeconds(1) && Interval <= TimeSpan.FromHours(1)
        && PassTimeout >= TimeSpan.FromSeconds(1) && PassTimeout <= TimeSpan.FromMinutes(5)
        && PageSize is >= 1 and <= 1000 && MaxPages is >= 1 and <= 100;
}
