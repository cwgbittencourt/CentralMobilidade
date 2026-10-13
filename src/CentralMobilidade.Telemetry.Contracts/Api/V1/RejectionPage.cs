using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CentralMobilidade.Telemetry.Contracts.Api.V1;

public sealed record RejectionPage(
    [property: JsonPropertyName("items"), JsonRequired, Required]
    IReadOnlyList<Rejection> Items,
    [property: JsonPropertyName("nextCursor")]
    string? NextCursor = null);
