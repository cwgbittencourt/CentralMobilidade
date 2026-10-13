using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CentralMobilidade.Telemetry.Contracts.Api.V1;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IngestionHealthPoint(
    [property: JsonPropertyName("empresaId"), JsonRequired, Required]
    string EmpresaId,
    [property: JsonPropertyName("time"), JsonRequired]
    DateTimeOffset Time,
    [property: JsonPropertyName("accepted"), JsonRequired, Range(typeof(long), "0", "9223372036854775807")]
    long Accepted,
    [property: JsonPropertyName("rejected"), JsonRequired, Range(typeof(long), "0", "9223372036854775807")]
    long Rejected);
