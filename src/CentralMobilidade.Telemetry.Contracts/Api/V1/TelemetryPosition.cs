using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CentralMobilidade.Telemetry.Contracts.Api.V1;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TelemetryPosition(
    [property: JsonPropertyName("empresaId"), JsonRequired, Required]
    string EmpresaId,
    [property: JsonPropertyName("vehicleId"), JsonRequired, Required]
    string VehicleId,
    [property: JsonPropertyName("eventTime"), JsonRequired]
    DateTimeOffset EventTime,
    [property: JsonPropertyName("latitude"), JsonRequired]
    double Latitude,
    [property: JsonPropertyName("longitude"), JsonRequired]
    double Longitude,
    [property: JsonPropertyName("speed")]
    double? Speed = null,
    [property: JsonPropertyName("ignition")]
    bool? Ignition = null,
    [property: JsonPropertyName("driverId")]
    string? DriverId = null)
{
    public override string ToString() => "[TelemetryPosition]";
}
