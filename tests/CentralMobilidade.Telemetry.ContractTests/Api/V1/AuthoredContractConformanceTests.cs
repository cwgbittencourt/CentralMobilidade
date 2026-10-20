using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.LineProtocol;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.ContractTests.Api.V1;

public sealed class AuthoredContractConformanceTests
{
    [Theory]
    [InlineData("/api/v1/telemetry/batches/{batchId}/rejections")]
    [InlineData("/api/v1/telemetry/batches/{batchId}/rejections/{quarantineId}/content")]
    public async Task Administrative_provider_failure_has_a_documented_problem(string route)
    {
        var operation = (await OpenApi()).Paths[route].Operations![HttpMethod.Get];
        operation.Responses!.ContainsKey("500").ShouldBeTrue();
        operation.Responses["500"].Content!["application/problem+json"].Schema!
            .ShouldBeOfType<OpenApiSchemaReference>().Reference.Id.ShouldBe("Problem");
    }

    [Fact]
    public async Task Driver_permission_denial_describes_the_actual_problem_body()
    {
        var response = (await OpenApi()).Paths["/api/v1/presentation/drivers/{driverId}"].Operations![HttpMethod.Get].Responses!["403"];
        response.Content.ShouldNotBeNull();
        response.Content["application/problem+json"].Schema!.ShouldBeOfType<OpenApiSchemaReference>().Reference.Id.ShouldBe("Problem");
    }

    [Fact]
    public void Queue_schema_properties_and_required_members_match_the_runtime_metadata()
    {
        using var schema = QueueSchema();
        var properties = typeof(TelemetryBatchEnvelope).GetProperties();
        schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order()
            .ShouldBe(properties.Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name).Order());
        schema.RootElement.GetProperty("required").EnumerateArray().Select(p => p.GetString()).Order()
            .ShouldBe(properties.Where(p => p.GetCustomAttribute<JsonRequiredAttribute>() is not null)
                .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name).Order());
        schema.RootElement.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        var envelope = Envelope();
        envelope.Validate();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        json.RootElement.TryGetProperty("contractVersion", out _).ShouldBeFalse();
        JsonSerializer.Deserialize<TelemetryBatchEnvelope>(json.RootElement.GetRawText())!.Validate();
        foreach (var name in new[] { "schemaVersion", "contentType", "contentEncoding" })
            json.RootElement.GetProperty(name).GetString().ShouldBe(schema.RootElement.GetProperty("properties").GetProperty(name).GetProperty("const").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Queue_size_boundaries_come_from_the_authored_schema(bool minimum)
    {
        using var schema = QueueSchema();
        var bounds = schema.RootElement.GetProperty("properties").GetProperty("compressedBytes");
        var size = bounds.GetProperty(minimum ? "minimum" : "maximum").GetInt64();
        (Envelope() with
        {
            CompressedBytes = size
        }).Validate();
        Should.Throw<ValidationException>(() => (Envelope() with { CompressedBytes = size + (minimum ? -1 : 1) }).Validate());
    }

    [Fact]
    public void Queue_schema_and_runtime_both_reject_the_empty_UUID()
    {
        using var schema = QueueSchema();
        var pattern = schema.RootElement.GetProperty("properties").GetProperty("batchId").GetProperty("pattern").GetString()!;
        Regex.IsMatch(Guid.Empty.ToString("D"), pattern).ShouldBeFalse();
        Regex.IsMatch(Envelope().BatchId.ToString("D"), pattern).ShouldBeTrue();
        Should.Throw<ValidationException>(() => (Envelope() with { BatchId = Guid.Empty }).Validate());
    }

    [Fact]
    public void Published_Line_Protocol_example_is_accepted_by_the_production_parser()
    {
        var markdown = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "line-protocol-v1.md"));
        var example = Regex.Matches(markdown, @"(?m)^telemetria,.*$").Select(m => m.Value.TrimEnd('\r'))
            .Single(line => !line.Contains('<'));
        var result = new TelemetryLineProtocolV1Parser().Parse(Encoding.UTF8.GetBytes(example));
        result.ErrorCode.ShouldBeNull();
        result.Event.ShouldNotBeNull().CompanyId.Value.ShouldBe("empresa-42");
        result.Event.VehicleId.Value.ShouldBe("veiculo-900");
        result.Event.Timestamp.UnixMilliseconds.ShouldBe(1791686400123);
    }

    private static TelemetryBatchEnvelope Envelope() => new("1", Guid.Parse("0199c55a-6200-7b65-b936-f775309a6942"),
        new string('a', 32), "producer-1", DateTimeOffset.Parse("2026-10-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        "text/plain; charset=utf-8", "gzip", 100, new string('b', 64));
    private static JsonDocument QueueSchema() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "queue-envelope.schema.json")));
    private static async Task<OpenApiDocument> OpenApi()
    {
        var settings = new OpenApiReaderSettings { LoadExternalRefs = false };
        settings.AddYamlReader();
        var result = await OpenApiDocument.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Contracts", "openapi.yaml"), settings);
        result.Diagnostic!.Errors.ShouldBeEmpty();
        return result.Document.ShouldNotBeNull();
    }
}
