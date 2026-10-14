using System;
using System.Text;
using CentralMobilidade.Telemetry.Domain.ValueObjects;

namespace CentralMobilidade.Telemetry.Domain.Telemetry;

/// <summary>Validated v1 event. Speed is km/h; timestamp is UTC with exact millisecond precision.</summary>
public sealed record TelemetryEvent
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public TelemetryEvent(CompanyId companyId, VehicleId vehicleId, EventTimestamp timestamp,
        double latitude, double longitude, double speed, long rpm, bool ignition,
        DriverId? driverId = null, string? plate = null)
    {
        ArgumentNullException.ThrowIfNull(companyId);
        ArgumentNullException.ThrowIfNull(vehicleId);
        ArgumentNullException.ThrowIfNull(timestamp);
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitude), "Latitude must be finite and between -90 and 90.");
        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(longitude), "Longitude must be finite and between -180 and 180.");
        if (!double.IsFinite(speed) || speed < 0)
            throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be finite and non-negative in km/h.");
        if (rpm < 0)
            throw new ArgumentOutOfRangeException(nameof(rpm), "RPM must be non-negative.");
        if (plate is not null)
        {
            foreach (var character in plate)
                if (char.IsControl(character))
                    throw new ArgumentException("Plate must not contain control characters.", nameof(plate));
            try
            {
                _ = StrictUtf8.GetByteCount(plate);
            }
            catch (EncoderFallbackException) { throw new ArgumentException("Plate must contain valid Unicode.", nameof(plate)); }
        }
        CompanyId = companyId;
        VehicleId = vehicleId;
        Timestamp = timestamp;
        Latitude = latitude;
        Longitude = longitude;
        Speed = speed;
        Rpm = rpm;
        Ignition = ignition;
        DriverId = driverId;
        Plate = plate?.ToUpperInvariant();
    }

    public CompanyId CompanyId
    {
        get;
    }
    public VehicleId VehicleId
    {
        get;
    }
    public EventTimestamp Timestamp
    {
        get;
    }
    public double Latitude
    {
        get;
    }
    public double Longitude
    {
        get;
    }
    public double Speed
    {
        get;
    }
    public long Rpm
    {
        get;
    }
    public bool Ignition
    {
        get;
    }
    public DriverId? DriverId
    {
        get;
    }
    public string? Plate
    {
        get;
    }

    public override string ToString() => "[TelemetryEvent]";
}
