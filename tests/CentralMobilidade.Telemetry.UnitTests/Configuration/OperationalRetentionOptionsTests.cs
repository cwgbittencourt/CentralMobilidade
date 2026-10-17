using System;
using CentralMobilidade.Telemetry.Application.Configuration;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Configuration;

public sealed class OperationalRetentionOptionsTests
{
    [Theory]
    [InlineData(1, 1, 1, 1, true)]
    [InlineData(3600, 300, 1000, 100, true)]
    [InlineData(0, 60, 200, 20, false)]
    [InlineData(3601, 60, 200, 20, false)]
    [InlineData(300, 0, 200, 20, false)]
    [InlineData(300, 301, 200, 20, false)]
    [InlineData(300, 60, 0, 20, false)]
    [InlineData(300, 60, 1001, 20, false)]
    [InlineData(300, 60, 200, 0, false)]
    [InlineData(300, 60, 200, 101, false)]
    public void Cleanup_work_is_bounded_at_configuration_boundary(int interval, int timeout, int page, int pages, bool valid)
        => new OperationalRetentionOptions { Interval = TimeSpan.FromSeconds(interval), PassTimeout = TimeSpan.FromSeconds(timeout), PageSize = page, MaxPages = pages }.IsValid().ShouldBe(valid);
}
