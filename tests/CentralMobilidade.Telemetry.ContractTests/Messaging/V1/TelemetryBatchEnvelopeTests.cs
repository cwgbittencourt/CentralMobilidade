using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.ContractTests.Messaging.V1;

public sealed class TelemetryBatchEnvelopeTests
{
    private static TelemetryBatchEnvelope ValidEnvelope() => new(
        "1", Guid.Parse("0199c55a-6200-7b65-b936-f775309a6942"), new string('a', 32), "producer-1",
        new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero), "text/plain; charset=utf-8", "gzip",
        100, new string('b', 64), "v1");

    [Fact]
    public void MetadataMatchesClosedSchemaAndContainsNoPayload()
    {
        var envelope = ValidEnvelope();
        envelope.Validate();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .ShouldBe(new[] { "batchId", "compressedBytes", "contentEncoding", "contentType", "contractVersion", "payloadSha256", "producerId", "receivedAt", "schemaVersion", "traceId" });
        JsonSerializer.Deserialize<TelemetryBatchEnvelope>(json.RootElement.GetRawText()).ShouldBe(envelope);
        envelope.ToString().ShouldNotContain(envelope.PayloadSha256);
        typeof(TelemetryBatchEnvelope).GetProperties().Any(property => property.PropertyType == typeof(byte[])).ShouldBeFalse();
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<TelemetryBatchEnvelope>("{}"));
        var withPayload = json.RootElement.GetRawText().TrimEnd('}') + ",\"payload\":\"H4sI\"}";
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<TelemetryBatchEnvelope>(withPayload));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(8388608L)]
    public void CompressedSizeBoundariesAreAccepted(long bytes)
    {
        (ValidEnvelope() with
        {
            CompressedBytes = bytes
        }).Validate();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(8388609L)]
    public void InvalidCompressedSizesAreRejected(long bytes)
    {
        Should.Throw<ValidationException>(() => (ValidEnvelope() with { CompressedBytes = bytes }).Validate());
    }

    [Fact]
    public void VersionTransportIdentityAndDigestConstraintsAreEnforced()
    {
        TelemetryBatchEnvelope[] invalid =
        [
            ValidEnvelope() with { SchemaVersion = "2" },
            ValidEnvelope() with { ContractVersion = "v2" },
            ValidEnvelope() with { ContractVersion = "" },
            ValidEnvelope() with { ContentEncoding = "identity" },
            ValidEnvelope() with { ContentType = "application/json" },
            ValidEnvelope() with { BatchId = Guid.Empty },
            ValidEnvelope() with { TraceId = new string('a', 15) },
            ValidEnvelope() with { TraceId = new string('a', 65) },
            ValidEnvelope() with { ProducerId = "" },
            ValidEnvelope() with { ProducerId = null! },
            ValidEnvelope() with { ProducerId = new string('a', 129) },
            ValidEnvelope() with { PayloadSha256 = new string('A', 64) },
            ValidEnvelope() with { PayloadSha256 = new string('a', 63) },
            ValidEnvelope() with { PayloadSha256 = new string('z', 64) }
        ];
        foreach (var envelope in invalid)
        {
            Should.Throw<ValidationException>(envelope.Validate);
        }
    }

    [Fact]
    public void IdentifierLengthEdgesAndDateTimeWireFormatArePreserved()
    {
        (ValidEnvelope() with
        {
            TraceId = new string('a', 16),
            ProducerId = "p"
        }).Validate();
        (ValidEnvelope() with
        {
            TraceId = new string('a', 64),
            ProducerId = new string('p', 128)
        }).Validate();
        var json = JsonSerializer.Serialize(ValidEnvelope());
        var invalidDate = json.Replace("2026-10-13T00:00:00+00:00", "not-a-date", StringComparison.Ordinal);
        invalidDate.ShouldNotBe(json);
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<TelemetryBatchEnvelope>(invalidDate));
    }

    [Fact]
    public void OptionalContractVersionCanBeOmitted()
    {
        var envelope = ValidEnvelope() with
        {
            ContractVersion = null
        };
        envelope.Validate();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        json.RootElement.TryGetProperty("contractVersion", out _).ShouldBeFalse();
        JsonSerializer.Deserialize<TelemetryBatchEnvelope>(json.RootElement.GetRawText()).ShouldBe(envelope);
    }
}
