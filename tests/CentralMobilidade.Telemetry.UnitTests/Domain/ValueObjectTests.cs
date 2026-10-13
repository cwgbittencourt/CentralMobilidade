using System;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Domain;

public sealed class ValueObjectTests
{
    [Fact]
    public void BatchIdsPreserveUuidIdentity()
    {
        var value = Guid.NewGuid();
        var id = new BatchId(value);
        id.Value.ShouldBe(value);
        id.ShouldBe(new BatchId(value));
        BatchId.Parse(id.ToString()).ShouldBe(id);
        BatchId.New().Value.ShouldNotBe(Guid.Empty);
        Should.Throw<ArgumentException>(() => new BatchId(Guid.Empty));
        Should.Throw<ArgumentException>(() => BatchId.Parse("not-a-uuid"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("id\nvalue")]
    [InlineData("id\0value")]
    public void InvalidIdentifiersAreRejected(string? value)
    {
        Should.Throw<ArgumentException>(() => new CompanyId(value!));
        Should.Throw<ArgumentException>(() => new VehicleId(value!));
        Should.Throw<ArgumentException>(() => new DriverId(value!));
    }

    [Fact]
    public void IdentifierBoundUsesUtf8Bytes()
    {
        new CompanyId(new string('a', 256)).Value.Length.ShouldBe(256);
        new VehicleId(new string('é', 128)).Value.Length.ShouldBe(128);
        new DriverId(new string('é', 128)).Value.Length.ShouldBe(128);
        Should.Throw<ArgumentException>(() => new CompanyId(new string('a', 257)));
        Should.Throw<ArgumentException>(() => new VehicleId(new string('é', 129)));
        Should.Throw<ArgumentException>(() => new DriverId(new string('é', 129)));
        Should.Throw<ArgumentException>(() => new CompanyId("\uD800"));
    }

    [Fact]
    public void IdentityIsCaseSensitiveAndDoesNotTrimOrNormalize()
    {
        var id = new CompanyId("empresa-42");
        id.ShouldBe(new CompanyId("empresa-42"));
        id.GetHashCode().ShouldBe(new CompanyId("empresa-42").GetHashCode());
        id.ShouldNotBe(new CompanyId("EMPRESA-42"));
        id.ShouldNotBe(new CompanyId(" empresa-42 "));
        new CompanyId("é").ShouldNotBe(new CompanyId("e\u0301"));
        new VehicleId("veiculo-900").ShouldBe(new VehicleId("veiculo-900"));
        new DriverId("motorista-77").ShouldBe(new DriverId("motorista-77"));
        new CompanyId("id").Equals(new VehicleId("id")).ShouldBeFalse();
        new DriverId("motorista-77").ToString().ShouldNotContain("motorista-77");
    }

    [Fact]
    public void EscapedTagCharactersRemainTheParsersResponsibility()
    {
        const string value = "ação, frota=42\\veículo";
        new CompanyId(value).Value.ShouldBe(value);
        new VehicleId(value).Value.ShouldBe(value);
        new DriverId(value).Value.ShouldBe(value);
    }

    [Fact]
    public void TimestampNormalizesOffsetAndPreservesMilliseconds()
    {
        var utc = new DateTimeOffset(2026, 10, 13, 3, 0, 0, TimeSpan.Zero).AddMilliseconds(123);
        var timestamp = new EventTimestamp(utc.ToOffset(TimeSpan.FromHours(-3)));
        timestamp.Value.Offset.ShouldBe(TimeSpan.Zero);
        timestamp.Value.ShouldBe(utc);
        timestamp.ShouldBe(new EventTimestamp(utc));
        EventTimestamp.FromUnixTimeMilliseconds(timestamp.UnixMilliseconds).ShouldBe(timestamp);
        timestamp.ToString().ShouldBe("2026-10-13T03:00:00.123Z");
        Should.Throw<ArgumentException>(() => new EventTimestamp(utc.AddTicks(1)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1791686400123L)]
    [InlineData(-62135596800000L)]
    [InlineData(253402300799999L)]
    public void UnixMillisecondsRoundTripWithinDateTimeOffsetRange(long milliseconds)
    {
        EventTimestamp.FromUnixTimeMilliseconds(milliseconds).UnixMilliseconds.ShouldBe(milliseconds);
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-62135596800001L)]
    [InlineData(253402300800000L)]
    public void UnrepresentableTimestampsAreRejected(long milliseconds)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => EventTimestamp.FromUnixTimeMilliseconds(milliseconds));
    }
}
