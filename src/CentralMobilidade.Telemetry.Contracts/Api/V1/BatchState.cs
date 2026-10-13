using System.Text.Json.Serialization;

namespace CentralMobilidade.Telemetry.Contracts.Api.V1;

[JsonConverter(typeof(BatchStateJsonConverter))]
public enum BatchState
{
    [JsonStringEnumMemberName("accepted")]
    Accepted,
    [JsonStringEnumMemberName("processing")]
    Processing,
    [JsonStringEnumMemberName("retrying")]
    Retrying,
    [JsonStringEnumMemberName("completed")]
    Completed,
    [JsonStringEnumMemberName("completed_with_rejections")]
    CompletedWithRejections,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("expired")]
    Expired
}

public sealed class BatchStateJsonConverter : JsonStringEnumConverter<BatchState>
{
    public BatchStateJsonConverter() : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}
