using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CentralMobilidade.Telemetry.Contracts.Messaging.V1;

/// <summary>
/// Provider-neutral metadata mapped to queue headers/attributes. The original gzip bytes travel
/// separately as the native provider message body; this type must never contain or Base64-wrap them.
/// Producers and consumers call Validate before using metadata. Adapters verify size/hash against
/// the separate body and propagate correlation; serialization alone does not validate values.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TelemetryBatchEnvelope(
    [property: JsonPropertyName("schemaVersion"), JsonRequired, Required, RegularExpression("1")]
    string SchemaVersion,
    [property: JsonPropertyName("batchId"), JsonRequired]
    Guid BatchId,
    [property: JsonPropertyName("traceId"), JsonRequired, Required, StringLength(64, MinimumLength = 16)]
    string TraceId,
    [property: JsonPropertyName("producerId"), JsonRequired, Required, StringLength(128, MinimumLength = 1)]
    string ProducerId,
    [property: JsonPropertyName("receivedAt"), JsonRequired]
    DateTimeOffset ReceivedAt,
    [property: JsonPropertyName("contentType"), JsonRequired, Required, RegularExpression("text/plain; charset=utf-8")]
    string ContentType,
    [property: JsonPropertyName("contentEncoding"), JsonRequired, Required, RegularExpression("gzip")]
    string ContentEncoding,
    [property: JsonPropertyName("compressedBytes"), JsonRequired, Range(typeof(long), "1", "8388608")]
    long CompressedBytes,
    [property: JsonPropertyName("payloadSha256"), JsonRequired, Required, RegularExpression("[a-f0-9]{64}")]
    string PayloadSha256,
    [property: JsonPropertyName("contractVersion"), RegularExpression("v1"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ContractVersion = null)
{
    public void Validate()
    {
        Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
        if (BatchId == Guid.Empty)
        {
            throw new ValidationException("BatchId must be a nonempty UUID.");
        }

        if (ContractVersion is not null && ContractVersion != "v1")
        {
            throw new ValidationException("ContractVersion, when present, must be v1.");
        }
    }

    public override string ToString() => "[TelemetryBatchEnvelope]";
}
