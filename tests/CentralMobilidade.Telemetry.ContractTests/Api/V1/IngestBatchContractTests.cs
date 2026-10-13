using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Contracts.Errors;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.ContractTests.Api.V1;

/// <summary>Guards the authored OpenAPI contract; runtime intake behavior is covered by US1 integration tests.</summary>
[Trait("Category", "Contract")]
public sealed class IngestBatchContractTests
{
    private const string Route = "/api/v1/telemetry/batches";

    [Fact]
    public async Task Intake_has_exactly_the_documented_responses()
    {
        var document = await LoadContract();
        var operation = Intake(document);
        operation.OperationId.ShouldBe("ingestTelemetryBatch");
        operation.Responses!.Keys.Order().ShouldBe(new[] { "202", "400", "401", "408", "413", "415", "429", "503" });
    }

    [Fact]
    public async Task Intake_requires_only_an_opaque_producer_bearer()
    {
        var document = await LoadContract();
        var security = Intake(document).Security!.ShouldHaveSingleItem();
        var requirement = security.ShouldHaveSingleItem();
        requirement.Key.Reference.Id.ShouldBe("producerBearer");
        requirement.Value.ShouldBeEmpty();
        var scheme = document.Components!.SecuritySchemes!["producerBearer"];
        scheme.Type.ShouldBe(SecuritySchemeType.Http);
        scheme.Scheme.ShouldBe("bearer");
        scheme.BearerFormat.ShouldBe("opaque");
    }

    [Theory]
    [InlineData("X-Contract-Version", "v1")]
    [InlineData("Content-Encoding", "gzip")]
    public async Task Intake_requires_version_and_compression_headers(string name, string value)
    {
        var operation = Intake(await LoadContract());
        var parameter = operation.Parameters!.Single(parameter => parameter.Name == name);
        parameter.In.ShouldBe(ParameterLocation.Header);
        parameter.Required.ShouldBeTrue();
        parameter.Schema!.Type.ShouldBe(JsonSchemaType.String);
        parameter.Schema.Const.ShouldBe(value);
    }

    [Fact]
    public async Task Trace_context_is_an_optional_header()
    {
        var parameter = Intake(await LoadContract()).Parameters!.Single(parameter => parameter.Name == "traceparent");
        parameter.In.ShouldBe(ParameterLocation.Header);
        parameter.Required.ShouldBeFalse();
        parameter.Schema!.Type.ShouldBe(JsonSchemaType.String);
    }

    [Fact]
    public async Task Intake_body_is_required_binary_plain_text_with_utf8_documented()
    {
        var body = Intake(await LoadContract()).RequestBody!;
        body.Required.ShouldBeTrue();
        body.Content!.Keys.ShouldBe(new[] { "text/plain" });
        body.Content["text/plain"].Schema!.Type.ShouldBe(JsonSchemaType.String);
        body.Content["text/plain"].Schema!.Format.ShouldBe("binary");
        body.Description.ShouldNotBeNullOrWhiteSpace();
        body.Description.ShouldContain("charset=utf-8");
    }

    [Fact]
    public async Task Accepted_response_has_required_location_and_matches_the_dto()
    {
        var document = await LoadContract();
        var response = Intake(document).Responses!["202"];
        response.Description!.ShouldContain("durable queue confirmed");
        var location = response.Headers!["Location"];
        location.Required.ShouldBeTrue();
        location.Schema!.Type.ShouldBe(JsonSchemaType.String);
        response.Content!.Keys.ShouldBe(new[] { "application/json" });
        var schema = response.Content["application/json"].Schema!;
        schema.ShouldBeOfType<OpenApiSchemaReference>().Reference.Id.ShouldBe("BatchAccepted");
        schema.AdditionalPropertiesAllowed.ShouldBeFalse();

        var id = Guid.Parse("0199c55a-6200-7b65-b936-f775309a6942");
        var time = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        var statusUrl = Route + "/" + id;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new BatchAccepted(id, time, statusUrl)));
        json.RootElement.EnumerateObject().Select(property => property.Name).Order()
            .ShouldBe(schema.Required!.Order());
        schema.Properties!.Keys.Order().ShouldBe(schema.Required!.Order());
        schema.Properties["batchId"].Format.ShouldBe("uuid");
        schema.Properties["receivedAt"].Format.ShouldBe("date-time");
        schema.Properties["state"].Const.ShouldBe("accepted");
        json.RootElement.GetProperty("state").GetString().ShouldBe(schema.Properties["state"].Const);
        json.RootElement.GetProperty("statusUrl").GetString().ShouldBe(statusUrl);
        json.RootElement.GetProperty("batchId").GetGuid().ShouldBe(id);
        json.RootElement.GetProperty("receivedAt").GetDateTimeOffset().ShouldBe(time);
    }

    [Theory]
    [InlineData("202", false)]
    [InlineData("429", true)]
    [InlineData("503", true)]
    public async Task Retry_after_is_a_positive_integer(string status, bool required)
    {
        var header = Intake(await LoadContract()).Responses![status].Headers!["Retry-After"];
        header.Required.ShouldBe(required);
        header.Schema!.Type.ShouldBe(JsonSchemaType.Integer);
        header.Schema.Minimum.ShouldBe("1");
    }

    [Fact]
    public async Task Authentication_failure_documents_the_bearer_challenge()
    {
        var header = Intake(await LoadContract()).Responses!["401"].Headers!["WWW-Authenticate"];
        header.Required.ShouldBeTrue();
        header.Schema!.Type.ShouldBe(JsonSchemaType.String);
        header.Schema.Const.ShouldBe("Bearer");
    }

    [Theory]
    [InlineData("400", TelemetryErrorCodes.RequestInvalid)]
    [InlineData("401", TelemetryErrorCodes.AuthenticationRequired)]
    [InlineData("408", TelemetryErrorCodes.UploadTimeout)]
    [InlineData("413", TelemetryErrorCodes.PayloadCompressedLimit)]
    [InlineData("415", TelemetryErrorCodes.UnsupportedMediaType)]
    [InlineData("429", TelemetryErrorCodes.IntakeBackpressure)]
    [InlineData("503", TelemetryErrorCodes.DurabilityUnconfirmed)]
    public async Task Intake_failures_use_problem_details_and_stable_codes(string status, string code)
    {
        var document = await LoadContract();
        var response = Intake(document).Responses![status];
        response.Description.ShouldNotBeNullOrWhiteSpace();
        response.Content.ShouldNotBeNull($"HTTP {status} must describe its Problem Details body");
        response.Content.Keys.ShouldBe(new[] { "application/problem+json" });
        var schema = response.Content["application/problem+json"].Schema!;
        schema.ShouldBeOfType<OpenApiSchemaReference>().Reference.Id.ShouldBe("Problem");
        schema.Required.ShouldNotBeNull("Problem Details must require its stable fields");
        schema.Required.Order().ShouldBe(new[] { "code", "detail", "status", "title", "traceId", "type" });
        schema.AdditionalPropertiesAllowed.ShouldBeFalse();
        schema.Properties!["code"].Enum!.Select(value => value!.GetValue<string>()).ShouldContain(code);
        TelemetryErrorCodes.GetProblem(code).Status.ShouldBe(int.Parse(status));
        schema.Properties["status"].Type.ShouldBe(JsonSchemaType.Integer);
        foreach (var field in new[] { "code", "detail", "title", "traceId", "type" })
        {
            schema.Properties[field].Type.ShouldBe(JsonSchemaType.String);
        }

        schema.Properties.Keys.Order().ShouldBe(schema.Required!.Order());
    }

    private static OpenApiOperation Intake(OpenApiDocument document) => document.Paths[Route].Operations![HttpMethod.Post];

    private static async Task<OpenApiDocument> LoadContract()
    {
        var settings = new OpenApiReaderSettings { LoadExternalRefs = false };
        settings.AddYamlReader();
        var result = await OpenApiDocument.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Contracts", "openapi.yaml"), settings);
        result.Diagnostic!.Errors.ShouldBeEmpty();
        result.Document.ShouldNotBeNull();
        return result.Document;
    }
}
