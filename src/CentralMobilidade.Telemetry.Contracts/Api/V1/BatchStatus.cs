using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CentralMobilidade.Telemetry.Contracts.Api.V1;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BatchStatus(
    [property: JsonPropertyName("batchId"), JsonRequired]
    Guid BatchId,
    [property: JsonPropertyName("state"), JsonRequired]
    BatchState State,
    [property: JsonPropertyName("receivedAt"), JsonRequired]
    DateTimeOffset ReceivedAt,
    [property: JsonPropertyName("acceptedLines"), JsonRequired, Range(typeof(long), "0", "9223372036854775807")]
    long AcceptedLines,
    [property: JsonPropertyName("rejectedLines"), JsonRequired, Range(typeof(long), "0", "9223372036854775807")]
    long RejectedLines,
    [property: JsonPropertyName("replayedLines"), JsonRequired, Range(typeof(long), "0", "9223372036854775807")]
    long ReplayedLines,
    [property: JsonPropertyName("processingStartedAt")]
    DateTimeOffset? ProcessingStartedAt = null,
    [property: JsonPropertyName("completedAt")]
    DateTimeOffset? CompletedAt = null,
    [property: JsonPropertyName("totalLines"), Range(typeof(long), "0", "9223372036854775807")]
    long? TotalLines = null,
    [property: JsonPropertyName("errorSummary"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ErrorCount>? ErrorSummary = null,
    [property: JsonPropertyName("links"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    BatchLinks? Links = null);
