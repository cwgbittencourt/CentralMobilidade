using System;
using System.Globalization;
using System.Linq;
using CentralMobilidade.Telemetry.Domain.Telemetry;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Domain;

public sealed class TelemetryEventTests
{
    private static readonly CompanyId Company = new("company");
    private static readonly VehicleId Vehicle = new("vehicle");
    private static readonly EventTimestamp Timestamp = EventTimestamp.FromUnixTimeMilliseconds(1791686400123);

    [Theory]
    [InlineData(-90, -180, 0, 0, false)]
    [InlineData(90, 180, 82.5, long.MaxValue, true)]
    public void Inclusive_boundaries_preserve_contract_values(double latitude, double longitude, double speed, long rpm, bool ignition)
    {
        var point = new TelemetryEvent(Company, Vehicle, Timestamp, latitude, longitude, speed, rpm, ignition);
        point.CompanyId.ShouldBe(Company);
        point.VehicleId.ShouldBe(Vehicle);
        point.Timestamp.ShouldBe(Timestamp);
        point.Latitude.ShouldBe(latitude);
        point.Longitude.ShouldBe(longitude);
        point.Speed.ShouldBe(speed); // km/h, no implicit unit conversion.
        point.Rpm.ShouldBe(rpm);
        point.Ignition.ShouldBe(ignition);
        point.DriverId.ShouldBeNull();
        point.Plate.ShouldBeNull();
    }

    [Theory]
    [InlineData("latitude", -90.0001)]
    [InlineData("latitude", 90.0001)]
    [InlineData("latitude", double.NaN)]
    [InlineData("latitude", double.NegativeInfinity)]
    [InlineData("latitude", double.PositiveInfinity)]
    [InlineData("longitude", -180.0001)]
    [InlineData("longitude", 180.0001)]
    [InlineData("longitude", double.NaN)]
    [InlineData("longitude", double.NegativeInfinity)]
    [InlineData("longitude", double.PositiveInfinity)]
    [InlineData("speed", -0.0001)]
    [InlineData("speed", double.NaN)]
    [InlineData("speed", double.NegativeInfinity)]
    [InlineData("speed", double.PositiveInfinity)]
    public void Invalid_numeric_values_are_rejected_without_echoing_data(string field, double value)
    {
        var error = Should.Throw<ArgumentOutOfRangeException>(() => new TelemetryEvent(Company, Vehicle, Timestamp,
            field == "latitude" ? value : 1, field == "longitude" ? value : 2, field == "speed" ? value : 3, 0, false));
        error.ParamName.ShouldBe(field);
        error.ActualValue.ShouldBeNull();
    }

    [Fact]
    public void Negative_rpm_is_rejected()
    {
        var error = Should.Throw<ArgumentOutOfRangeException>(() => new TelemetryEvent(Company, Vehicle, Timestamp, 1, 2, 3, -1, true));
        error.ParamName.ShouldBe("rpm");
        error.ActualValue.ShouldBeNull();
    }

    [Theory]
    [InlineData("companyId")]
    [InlineData("vehicleId")]
    [InlineData("timestamp")]
    public void Identity_components_are_required(string field)
    {
        var error = Should.Throw<ArgumentNullException>(() => new TelemetryEvent(field == "companyId" ? null! : Company,
            field == "vehicleId" ? null! : Vehicle, field == "timestamp" ? null! : Timestamp, 1, 2, 3, 0, false));
        error.ParamName.ShouldBe(field);
    }

    [Fact]
    public void Pseudonymous_driver_and_decoded_plate_are_preserved_without_resolution()
    {
        var driver = new DriverId("pseudonym-42");
        var point = new TelemetryEvent(Company, Vehicle, Timestamp, 1, 2, 3, 0, true, driver, "bra\"2\\e19");
        point.DriverId.ShouldBe(driver);
        point.Plate.ShouldBe("BRA\"2\\E19");
        point.GetType().GetProperties().Select(p => p.Name).ShouldNotContain("DriverName");
        point.GetType().GetProperties().Select(p => p.Name).ShouldNotContain("Cpf");
    }

    [Theory]
    [InlineData("plate\nvalue")]
    [InlineData("plate\0value")]
    [InlineData("plate\tvalue")]
    public void Plate_rejects_controls_and_malformed_unicode(string plate)
    {
        var error = Should.Throw<ArgumentException>(() => new TelemetryEvent(Company, Vehicle, Timestamp, 1, 2, 3, 0, false, plate: plate));
        error.ParamName.ShouldBe("plate");
        error.Message.ShouldNotContain("plate\nvalue");
    }

    [Fact]
    public void Plate_rejects_unpaired_surrogate_constructed_without_test_data_serialization()
    {
        var plate = new string((char)0xd800, 1);
        var error = Should.Throw<ArgumentException>(() => new TelemetryEvent(Company, Vehicle, Timestamp, 1, 2, 3, 0, false, plate: plate));
        error.ParamName.ShouldBe("plate");
    }

    [Fact]
    public void Plate_normalization_is_invariant_and_diagnostics_are_private()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var point = new TelemetryEvent(Company, Vehicle, Timestamp, -23.55052, -46.633308, 82.5, 2150, true,
                new DriverId("SEEDED_DRIVER"), "iabc123");
            point.Plate.ShouldBe("IABC123");
            point.ToString().ShouldBe("[TelemetryEvent]");
            point.GetType().GetProperties().All(property => property.SetMethod is null).ShouldBeTrue();
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Value_equality_uses_normalized_plate_and_all_event_values()
    {
        var first = new TelemetryEvent(Company, Vehicle, Timestamp, 1, 2, 3, 4, true, plate: "abc1234");
        var equivalent = new TelemetryEvent(Company, Vehicle, Timestamp, 1, 2, 3, 4, true, plate: "ABC1234");
        first.ShouldBe(equivalent);
        first.ShouldNotBe(new TelemetryEvent(Company, Vehicle, Timestamp, 1, 2, 4, 4, true, plate: "ABC1234"));
    }
}
