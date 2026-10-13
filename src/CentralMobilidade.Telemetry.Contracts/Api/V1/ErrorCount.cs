using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CentralMobilidade.Telemetry.Contracts.Api.V1;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ErrorCount(
    [property: JsonPropertyName("code"), JsonRequired, Required]
    string Code,
    [property: JsonPropertyName("count"), JsonRequired, Range(typeof(long), "1", "9223372036854775807")]
    long Count);
