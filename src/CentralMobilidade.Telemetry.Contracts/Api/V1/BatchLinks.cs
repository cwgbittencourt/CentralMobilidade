using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CentralMobilidade.Telemetry.Contracts.Api.V1;

public sealed record BatchLinks(
    [property: JsonPropertyName("rejections"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Rejections = null);
