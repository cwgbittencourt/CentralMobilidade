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

/// <summary>Authored OpenAPI/DTO guarantees; runtime authorization and audit proofs belong to T057–T060.</summary>
[Trait("Category", "Contract")]
public sealed class BatchStatusContractTests
{
    private const string Status = "/api/v1/telemetry/batches/{batchId}";
    private const string Rejections = Status + "/rejections";
    private const string Raw = Rejections + "/{quarantineId}/content";
    private static readonly Lazy<Task<OpenApiDocument>> Contract = new(LoadContract);

    [Theory]
    [InlineData(Status, "getTelemetryBatch", "200,401,404")]
    [InlineData(Rejections, "listTelemetryBatchRejections", "200,400,401,404,500")]
    [InlineData(Raw, "getQuarantinedRejectionContent", "200,401,403,404,500,503")]
    public async Task Read_operations_have_exact_documented_outcomes(string route, string id, string outcomes)
    {
        var document = await Contract.Value;
        document.Paths[route].Operations!.Keys.ShouldBe(new[] { HttpMethod.Get });
        var operation = Get(document, route);
        operation.OperationId.ShouldBe(id);
        operation.RequestBody.ShouldBeNull();
        operation.Responses!.Keys.Order().ShouldBe(outcomes.Split(',').Order());
    }

    [Fact]
    public async Task Status_allows_producer_OR_operator_never_anonymous_or_both_required()
    {
        var operation = Get(await Contract.Value, Status);
        operation.Security!.Count.ShouldBe(2);
        operation.Security.Select(requirement => requirement.ShouldHaveSingleItem().Key.Reference.Id).Order()
            .ShouldBe(new[] { "operatorBearer", "producerBearer" });
        operation.Security.SelectMany(requirement => requirement.Values).ShouldAllBe(scopes => scopes.Count == 0);
        operation.Description!.ShouldContain("submitting producer");
        operation.Description!.ShouldContain("own batch");
    }

    [Theory]
    [InlineData(Status)]
    [InlineData(Rejections)]
    public async Task Operator_metadata_visibility_requires_permission_and_the_entire_company_scope(string route)
    {
        var description = Get(await Contract.Value, route).Description!;
        description.ShouldContain("batch.read");
        description.ShouldContain("every company");
        description.ShouldContain("cross_company.read");
        description.ShouldContain("explicit");
    }

    [Theory]
    [InlineData(Rejections, "batch.read")]
    [InlineData(Raw, "quarantine.raw.read")]
    public async Task Administrative_reads_accept_only_operator_bearers_and_explicit_permissions(string route, string permission)
    {
        var document = await Contract.Value;
        var operation = Get(document, route);
        var requirement = operation.Security!.ShouldHaveSingleItem().ShouldHaveSingleItem();
        requirement.Key.Reference.Id.ShouldBe("operatorBearer");
        requirement.Value.ShouldBeEmpty();
        var permissions = operation.Extensions!["x-required-permissions"].ShouldBeOfType<JsonNodeExtension>().Node!.AsArray();
        permissions.Select(value => value!.GetValue<string>()).ShouldBe(new[] { permission });
        var scheme = document.Components!.SecuritySchemes!["operatorBearer"];
        scheme.Type.ShouldBe(SecuritySchemeType.Http);
        scheme.Scheme.ShouldBe("bearer");
        scheme.BearerFormat.ShouldBe("opaque");
    }

    [Theory]
    [InlineData(Status)]
    [InlineData(Rejections)]
    [InlineData(Raw)]
    public async Task Missing_invalid_disabled_or_wrong_credential_type_gets_a_bearer_challenge(string route)
    {
        var response = Get(await Contract.Value, route).Responses!["401"];
        foreach (var condition in new[] { "Missing", "invalid", "disabled", "wrong credential type" })
            response.Description!.ShouldContain(condition);
        var header = response.Headers!["WWW-Authenticate"];
        header.Required.ShouldBeTrue();
        header.Schema!.Const.ShouldBe("Bearer");
        AssertProblem(response, TelemetryErrorCodes.AuthenticationRequired, 401);
    }

    [Theory]
    [InlineData(Status)]
    [InlineData(Rejections)]
    public async Task Unknown_expired_or_unauthorized_batch_shares_non_disclosing_404(string route)
    {
        var operation = Get(await Contract.Value, route);
        operation.Responses!.ContainsKey("403").ShouldBeFalse("Metadata visibility must not reveal whether an unauthorized batch exists");
        var response = operation.Responses["404"];
        response.Description!.ShouldContain("does not exist");
        response.Description!.ShouldContain("expired");
        response.Description!.ShouldContain("not visible");
        AssertProblem(response, TelemetryErrorCodes.ResourceNotFound, 404);
    }

    [Theory]
    [InlineData(Status)]
    [InlineData(Rejections)]
    [InlineData(Raw)]
    public async Task Batch_identifier_is_a_required_path_uuid_and_never_a_company_selector(string route)
    {
        var parameters = Get(await Contract.Value, route).Parameters!;
        var batch = parameters.Single(parameter => parameter.Name == "batchId");
        batch.In.ShouldBe(ParameterLocation.Path);
        batch.Required.ShouldBeTrue();
        batch.Schema!.Type.ShouldBe(JsonSchemaType.String);
        batch.Schema.Format.ShouldBe("uuid");
        parameters.ShouldAllBe(parameter => parameter.Name != "empresaId" && parameter.Name != "producerId");
    }

    [Fact]
    public async Task Rejection_pagination_is_optional_bounded_and_has_an_opaque_continuation()
    {
        var operation = Get(await Contract.Value, Rejections);
        var cursor = operation.Parameters!.Single(parameter => parameter.Name == "cursor");
        cursor.In.ShouldBe(ParameterLocation.Query);
        cursor.Required.ShouldBeFalse();
        cursor.Schema!.Type.ShouldBe(JsonSchemaType.String);
        var limit = operation.Parameters!.Single(parameter => parameter.Name == "limit");
        limit.In.ShouldBe(ParameterLocation.Query);
        limit.Required.ShouldBeFalse();
        limit.Schema!.Type.ShouldBe(JsonSchemaType.Integer);
        limit.Schema.Minimum.ShouldBe("1");
        limit.Schema.Maximum.ShouldBe("200");
        limit.Schema.Default!.GetValue<decimal>().ShouldBe(50m);
        AssertProblem(operation.Responses!["400"], TelemetryErrorCodes.RequestInvalid, 400);
        var schema = SuccessSchema(operation, "RejectionPage");
        schema.Required!.ShouldBe(new[] { "items" });
        schema.Properties!.Keys.Order().ShouldBe(new[] { "items", "nextCursor" });
        schema.Properties["nextCursor"].Type.ShouldBe(JsonSchemaType.String | JsonSchemaType.Null);
        schema.Properties["items"].Type.ShouldBe(JsonSchemaType.Array);
        schema.Properties["items"].Items!.ShouldBeOfType<OpenApiSchemaReference>().Reference.Id.ShouldBe("Rejection");
        using var lastPage = JsonDocument.Parse(JsonSerializer.Serialize(new RejectionPage([], null)));
        lastPage.RootElement.GetProperty("items").GetArrayLength().ShouldBe(0);
        lastPage.RootElement.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Rejection_items_expose_only_the_allowlisted_privacy_safe_metadata()
    {
        var schema = (await Contract.Value).Components!.Schemas!["Rejection"];
        schema.AdditionalPropertiesAllowed.ShouldBeFalse();
        schema.Properties!.Keys.Order().ShouldBe(new[] { "code", "empresaId", "lineNumber", "occurredAt", "safeDigest", "veiculoId" });
        schema.Required!.Order().ShouldBe(new[] { "code", "lineNumber", "occurredAt", "safeDigest" });
        schema.Properties["lineNumber"].Minimum.ShouldBe("1");
        schema.Properties["occurredAt"].Format.ShouldBe("date-time");
        var item = new Rejection(1, "LP_INVALID_SYNTAX", "keyed-safe-digest", DateTimeOffset.UnixEpoch, "company-a", "vehicle-a");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(item));
        json.RootElement.EnumerateObject().Select(property => property.Name).Order().ShouldBe(schema.Properties.Keys.Order());
        Get(await Contract.Value, Rejections).Responses!["200"].Description!.ShouldContain("Raw lines require a separately audited flow");
    }

    [Fact]
    public async Task Status_schema_and_dto_keep_all_counts_timestamps_summaries_and_links()
    {
        var schema = SuccessSchema(Get(await Contract.Value, Status), "BatchStatus");
        schema.AdditionalPropertiesAllowed.ShouldBeFalse();
        schema.Required!.Order().ShouldBe(new[] { "acceptedLines", "batchId", "receivedAt", "rejectedLines", "replayedLines", "state" });
        var now = DateTimeOffset.UnixEpoch;
        var dto = new BatchStatus(Guid.NewGuid(), BatchState.CompletedWithRejections, now, 2, 1, 1,
            now, now, 4, [new("LP_INVALID_SYNTAX", 1)], new("/rejections"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto));
        json.RootElement.EnumerateObject().Select(property => property.Name).Order().ShouldBe(schema.Properties!.Keys.Order());
        foreach (var name in new[] { "acceptedLines", "rejectedLines", "replayedLines", "totalLines" })
            schema.Properties![name].Minimum.ShouldBe("0");
        foreach (var name in new[] { "receivedAt", "processingStartedAt", "completedAt" })
            schema.Properties![name].Format.ShouldBe("date-time");
        schema.Properties!["batchId"].Format.ShouldBe("uuid");
        schema.Properties["errorSummary"].Items!.ShouldBeOfType<OpenApiSchemaReference>().Reference.Id.ShouldBe("ErrorCount");
        schema.Properties["links"].Properties!.Keys.ShouldBe(new[] { "rejections" });
    }

    [Fact]
    public async Task Status_exposes_exactly_the_public_lifecycle_states()
    {
        var schema = SuccessSchema(Get(await Contract.Value, Status), "BatchStatus");
        var states = schema.Properties!["state"].Enum!.Select(value => value!.GetValue<string>()).Order();
        states.ShouldBe(Enum.GetValues<BatchState>().Select(state => JsonSerializer.Serialize(state).Trim('"')).Order());
        states.ShouldBe(new[] { "accepted", "completed", "completed_with_rejections", "expired", "failed", "processing", "retrying" });
    }

    [Fact]
    public async Task Raw_lookup_requires_an_opaque_rejection_uuid_associated_with_the_batch()
    {
        var operation = Get(await Contract.Value, Raw);
        var id = operation.Parameters!.Single(parameter => parameter.Name == "quarantineId");
        id.In.ShouldBe(ParameterLocation.Path);
        id.Required.ShouldBeTrue();
        id.Schema!.Type.ShouldBe(JsonSchemaType.String);
        id.Schema.Format.ShouldBe("uuid");
        operation.Parameters!.Select(parameter => parameter.Name).Order().ShouldBe(new[] { "batchId", "quarantineId" });
        var success = operation.Responses!["200"];
        success.Content!.Keys.ShouldBe(new[] { "text/plain" });
        success.Content["text/plain"].Schema!.Type.ShouldBe(JsonSchemaType.String);
        success.Description!.ShouldContain("30-day deletion deadline");
        var missing = operation.Responses["404"];
        foreach (var reason in new[] { "missing", "expired", "deleted", "not associated with the batch" })
            missing.Description!.ShouldContain(reason);
        AssertProblem(missing, TelemetryErrorCodes.ResourceNotFound, 404);
    }

    [Fact]
    public async Task Raw_content_is_released_only_after_durable_audit_and_all_outcomes_are_audited()
    {
        var operation = Get(await Contract.Value, Raw);
        operation.Extensions!["x-audit-mode"].ShouldBeOfType<JsonNodeExtension>().Node!.GetValue<string>().ShouldBe("fail-closed");
        foreach (var outcome in new[] { "Successful", "denied", "missing", "expired" })
            operation.Description!.ShouldContain(outcome);
        operation.Description!.ShouldContain("durably persisted before content is returned");
        var denied = operation.Responses!["403"];
        denied.Description!.ShouldContain("administrative quarantine permission");
        denied.Description!.ShouldContain("audited");
        AssertProblem(denied, TelemetryErrorCodes.AccessDenied, 403);
        var unavailable = operation.Responses["503"];
        unavailable.Description!.ShouldContain("No raw content");
        AssertProblem(unavailable, TelemetryErrorCodes.AuditUnavailable, 503);
    }

    private static void AssertProblem(IOpenApiResponse response, string code, int status)
    {
        response.Content.ShouldNotBeNull();
        response.Content.Keys.ShouldBe(new[] { "application/problem+json" });
        var schema = response.Content["application/problem+json"].Schema!.ShouldBeOfType<OpenApiSchemaReference>();
        schema.Reference.Id.ShouldBe("Problem");
        schema.AdditionalPropertiesAllowed.ShouldBeFalse();
        schema.Required!.Order().ShouldBe(new[] { "code", "detail", "status", "title", "traceId", "type" });
        schema.Properties!["code"].Enum!.Select(value => value!.GetValue<string>()).ShouldContain(code);
        TelemetryErrorCodes.GetProblem(code).Status.ShouldBe(status);
    }

    private static IOpenApiSchema SuccessSchema(OpenApiOperation operation, string name)
    {
        var response = operation.Responses!["200"];
        response.Content!.Keys.ShouldBe(new[] { "application/json" });
        var schema = response.Content["application/json"].Schema!;
        schema.ShouldBeOfType<OpenApiSchemaReference>().Reference.Id.ShouldBe(name);
        return schema;
    }
    private static OpenApiOperation Get(OpenApiDocument document, string route) => document.Paths[route].Operations![HttpMethod.Get];
    private static async Task<OpenApiDocument> LoadContract()
    {
        var settings = new OpenApiReaderSettings { LoadExternalRefs = false };
        settings.AddYamlReader();
        var result = await OpenApiDocument.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Contracts", "openapi.yaml"), settings);
        result.Diagnostic!.Errors.ShouldBeEmpty();
        return result.Document.ShouldNotBeNull();
    }
}
