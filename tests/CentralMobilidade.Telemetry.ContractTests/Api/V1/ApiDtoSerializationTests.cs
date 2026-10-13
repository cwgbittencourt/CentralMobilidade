using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.ContractTests.Api.V1;

public sealed class ApiDtoSerializationTests
{
    private static readonly DateTimeOffset Time = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.Parse("0199c55a-6200-7b65-b936-f775309a6942");

    [Fact]
    public void AcceptedBatchMatchesWireShape()
    {
        var result = new BatchAccepted(Id, Time, "/api/v1/telemetry/batches/" + Id);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Names(json.RootElement).ShouldBe(new[] { "batchId", "receivedAt", "state", "statusUrl" });
        json.RootElement.GetProperty("state").GetString().ShouldBe("accepted");
        json.RootElement.GetProperty("batchId").GetGuid().ShouldBe(Id);
        json.RootElement.GetProperty("receivedAt").GetDateTimeOffset().ShouldBe(Time);
    }

    [Theory]
    [InlineData(BatchState.Accepted, "accepted")]
    [InlineData(BatchState.Processing, "processing")]
    [InlineData(BatchState.Retrying, "retrying")]
    [InlineData(BatchState.Completed, "completed")]
    [InlineData(BatchState.CompletedWithRejections, "completed_with_rejections")]
    [InlineData(BatchState.Failed, "failed")]
    [InlineData(BatchState.Expired, "expired")]
    public void BatchStatesHaveExactWireNames(BatchState state, string wireName)
    {
        JsonSerializer.Serialize(state).ShouldBe('"' + wireName + '"');
        JsonSerializer.Deserialize<BatchState>('"' + wireName + '"').ShouldBe(state);
    }

    [Fact]
    public void UndefinedOrNumericBatchStatesAreRejected()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<BatchState>("123"));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<BatchState>("\"receiving\""));
        Should.Throw<JsonException>(() => JsonSerializer.Serialize((BatchState)123));
    }

    [Fact]
    public void BatchStatusIncludesOptionalOutcomesAndLinks()
    {
        var status = new BatchStatus(Id, BatchState.CompletedWithRejections, Time, 1, 1, 0,
            Time, Time, 2, [new ErrorCount("LP_INVALID_SYNTAX", 1)], new BatchLinks("/rejections"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status));
        Names(json.RootElement).ShouldBe(new[] { "acceptedLines", "batchId", "completedAt", "errorSummary", "links", "processingStartedAt", "receivedAt", "rejectedLines", "replayedLines", "state", "totalLines" });
        json.RootElement.GetProperty("links").GetProperty("rejections").GetString().ShouldBe("/rejections");
        var error = json.RootElement.GetProperty("errorSummary")[0];
        Names(error).ShouldBe(new[] { "code", "count" });
        JsonSerializer.Deserialize<BatchStatus>(JsonSerializer.Serialize(status))!.State.ShouldBe(status.State);
        using var pending = JsonDocument.Parse(JsonSerializer.Serialize(new BatchStatus(Id, BatchState.Accepted, Time, 0, 0, 0)));
        pending.RootElement.TryGetProperty("errorSummary", out _).ShouldBeFalse();
        pending.RootElement.TryGetProperty("links", out _).ShouldBeFalse();
    }

    [Fact]
    public void RejectionPageContainsOnlyContractMetadata()
    {
        var page = new RejectionPage([new Rejection(1, "LP_INVALID_SYNTAX", "safe-digest", Time, "empresa-42", "veiculo-900")], "next");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page));
        Names(json.RootElement).ShouldBe(new[] { "items", "nextCursor" });
        Names(json.RootElement.GetProperty("items")[0]).ShouldBe(new[] { "code", "empresaId", "lineNumber", "occurredAt", "safeDigest", "veiculoId" });
        JsonSerializer.Deserialize<RejectionPage>(JsonSerializer.Serialize(page))!.Items.Count.ShouldBe(1);
    }

    [Fact]
    public void DriverPresentationExposesOnlyMaskedIdentity()
    {
        var driver = new DriverPresentation("motorista-77", "Authorized name", "1234");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(driver));
        Names(json.RootElement).ShouldBe(new[] { "cpfLast4", "displayName", "driverId" });
        driver.ToString().ShouldNotContain("Authorized name");
        IsValid(driver).ShouldBeTrue();
        IsValid(driver with
        {
            CpfLast4 = "12345678900"
        }).ShouldBeFalse();
        IsValid(driver with
        {
            CpfLast4 = "abcd"
        }).ShouldBeFalse();
    }

    [Fact]
    public void LatestAndTripResponsesAreArraysOfPositions()
    {
        TelemetryPosition[] positions = [new("empresa-42", "veiculo-900", Time, -23.5, -46.6, 10, true, "motorista-77")];
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(positions));
        json.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        Names(json.RootElement[0]).ShouldBe(new[] { "driverId", "empresaId", "eventTime", "ignition", "latitude", "longitude", "speed", "vehicleId" });
        positions[0].ToString().ShouldNotContain("-23.5");
    }

    [Fact]
    public void IngestionHealthUsesTheContractCounterNames()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new IngestionHealthPoint("empresa-42", Time, 10, 2) }));
        Names(json.RootElement[0]).ShouldBe(new[] { "accepted", "empresaId", "rejected", "time" });
    }

    [Fact]
    public void ClosedSchemasRejectMissingRequiredAndUnexpectedFields()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Rejection>("{}"));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<TelemetryPosition>("{}"));
        var value = new DriverPresentation("driver", "name");
        var json = JsonSerializer.Serialize(value).TrimEnd('}') + ",\"cpf\":\"12345678900\"}";
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DriverPresentation>(json));
    }

    [Fact]
    public void QueryDefaultsAndIndividualBoundsMatchOpenApi()
    {
        IsValid(new IngestBatchHeaders("v1", "text/plain; charset=utf-8", "gzip")).ShouldBeTrue();
        IsValid(new IngestBatchHeaders("v2", "text/plain; charset=utf-8", "gzip")).ShouldBeFalse();
        new LatestPositionsRequest("empresa").Limit.ShouldBe(500);
        new VehicleTripRequest("empresa", "veiculo", Time, Time.AddHours(1)).Limit.ShouldBe(10000);
        new IngestionHealthRequest("empresa", Time, Time.AddHours(1)).GranularitySeconds.ShouldBe(60);
        new ListBatchRejectionsRequest(Id).Limit.ShouldBe(50);
        IsValid(new LatestPositionsRequest("empresa", Limit: 501)).ShouldBeFalse();
        IsValid(new LatestPositionsRequest("empresa", Cursor: new string('x', 513))).ShouldBeFalse();
        IsValid(new VehicleTripRequest("empresa", "veiculo", Time, Time.AddHours(1), 10001)).ShouldBeFalse();
        IsValid(new IngestionHealthRequest("empresa", Time, Time.AddHours(1), 59)).ShouldBeFalse();
        IsValid(new ListBatchRejectionsRequest(Id, Limit: 201)).ShouldBeFalse();
        IsValid(new DriverPresentationRequest(new string('x', 129), "empresa")).ShouldBeFalse();
    }

    [Fact]
    public void NullableFieldsAndWebSerializerKeepTheSameContract()
    {
        var position = new TelemetryPosition("empresa", "veiculo", Time, 0, 0);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(position, options));
        json.RootElement.GetProperty("speed").ValueKind.ShouldBe(JsonValueKind.Null);
        json.RootElement.GetProperty("ignition").ValueKind.ShouldBe(JsonValueKind.Null);
        json.RootElement.GetProperty("driverId").ValueKind.ShouldBe(JsonValueKind.Null);
        JsonSerializer.Deserialize<TelemetryPosition>(json.RootElement.GetRawText(), options).ShouldBe(position);
        IsValid(new ErrorCount("LP_INVALID_SYNTAX", 0)).ShouldBeFalse();
        IsValid(new Rejection(0, "LP_INVALID_SYNTAX", "digest", Time)).ShouldBeFalse();
        IsValid(new IngestionHealthPoint("empresa", Time, -1, 0)).ShouldBeFalse();
    }

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

    private static bool IsValid(object value) => Validator.TryValidateObject(value, new ValidationContext(value), new List<ValidationResult>(), true);
}
