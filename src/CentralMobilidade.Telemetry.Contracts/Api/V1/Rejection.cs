using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CentralMobilidade.Telemetry.Contracts.Api.V1;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Rejection(
    [property: JsonPropertyName("lineNumber"), JsonRequired, Range(1, int.MaxValue)]
    int LineNumber,
    [property: JsonPropertyName("code"), JsonRequired, Required]
    string Code,
    [property: JsonPropertyName("safeDigest"), JsonRequired, Required]
    string SafeDigest,
    [property: JsonPropertyName("occurredAt"), JsonRequired]
    DateTimeOffset OccurredAt,
    [property: JsonPropertyName("empresaId")]
    string? EmpresaId = null,
    [property: JsonPropertyName("veiculoId")]
    string? VeiculoId = null);
