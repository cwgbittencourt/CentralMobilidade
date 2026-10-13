using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CentralMobilidade.Telemetry.Contracts.Api.V1;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DriverPresentation(
    [property: JsonPropertyName("driverId"), JsonRequired, Required]
    string DriverId,
    [property: JsonPropertyName("displayName"), JsonRequired, Required]
    string DisplayName,
    [property: JsonPropertyName("cpfLast4"), RegularExpression(@"^[0-9]{4}$")]
    string? CpfLast4 = null)
{
    public override string ToString() => "[DriverPresentation]";
}
