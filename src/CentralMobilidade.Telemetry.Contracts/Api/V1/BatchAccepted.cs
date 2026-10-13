using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CentralMobilidade.Telemetry.Contracts.Api.V1;

/// <summary>Output-only acknowledgement constructed after durable publication confirmation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BatchAccepted(
    [property: JsonPropertyName("batchId"), JsonRequired] Guid BatchId,
    [property: JsonPropertyName("receivedAt"), JsonRequired] DateTimeOffset ReceivedAt,
    [property: JsonPropertyName("statusUrl"), JsonRequired, Required] string StatusUrl)
{
    [JsonPropertyName("state")]
    public string State => "accepted";
}
