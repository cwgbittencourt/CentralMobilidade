using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Endpoints.V1;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
namespace CentralMobilidade.Telemetry.IntegrationTests.Batches;

public sealed partial class BatchInspectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("producer")]
    public async Task Http_authentication_denial_omits_empty_resource_UUID_from_audit(string? actor)
    {
        await using var test = Create();
        await using var app = await HttpApp(test);
        using var response = await HttpGet(app, RawUrl(Guid.NewGuid(), Guid.Empty), actor);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var record = test.Audit.Records.ShouldHaveSingleItem();
        record.OpaqueResourceId.ShouldBeNull();
        record.Actor.Kind.ShouldBe(OperatorActorKind.Unidentified);
        record.Outcome.ShouldBe(AuditOutcome.Denied);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("other-batch")]
    public async Task Http_cursor_cannot_be_reused_outside_its_authenticated_context(string state)
    {
        await using var test = Create();
        var batch = await test.Seed();
        await using var app = await HttpApp(test);
        using var first = await HttpGet(app, ListUrl(batch.Id.Value) + "?limit=2");
        using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var cursor = json.RootElement.GetProperty("nextCursor").GetString()!;
        if (state == "tampered")
            cursor = (cursor[0] == 'A' ? "B" : "A") + cursor[1..];
        if (state == "expired")
            test.Clock.UtcNow = test.Clock.UtcNow.AddMinutes(16);
        if (state == "other-batch")
            batch = await test.Seed();
        using var response = await HttpGet(app, ListUrl(batch.Id.Value) + "?cursor=" + cursor);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await HttpProblem(response, TelemetryErrorCodes.RequestInvalid);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("incomplete")]
    public async Task Http_rejection_listing_does_not_reveal_unavailable_batches(string state)
    {
        await using var test = Create();
        var batch = await test.Seed(unknownCompany: state == "incomplete", ageDays: state == "expired" ? 31 : 0);
        await using var app = await HttpApp(test);
        using var response = await HttpGet(app, ListUrl(state == "missing" ? Guid.NewGuid() : batch.Id.Value));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await HttpProblem(response, TelemetryErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task Http_raw_stream_is_disposed_when_the_transfer_fails()
    {
        await using var test = Create();
        var batch = await test.Seed();
        test.Quarantine.FailRead = true;
        await using var app = await HttpApp(test);
        await Should.ThrowAsync<Exception>(() => HttpGet(app, RawUrl(batch.Id.Value, batch.QuarantineId)));
        test.Quarantine.DisposeCalls.ShouldBe(1);
        test.Audit.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AuditOutcome.Allowed);
    }

    [Fact]
    public async Task Http_request_cancellation_during_audit_does_not_open_raw_content()
    {
        await using var test = Create();
        var batch = await test.Seed();
        await using var app = await HttpApp(test);
        test.Audit.Pause = true;
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, RawUrl(batch.Id.Value, batch.QuarantineId));
        request.Headers.Authorization = new("Bearer", HttpTokens["operator"]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = client.SendAsync(request, cancellation.Token);
        await test.Audit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        test.Quarantine.OpenCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Http_status_links_are_administrative_and_page_permissions_are_rechecked()
    {
        await using var test = Create();
        var batch = await test.Seed();
        await using var app = await HttpApp(test);
        var url = "/api/v1/telemetry/batches/" + batch.Id.Value.ToString("D");
        using var status = await HttpGet(app, url);
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("links").GetProperty("rejections").GetString().ShouldBe(ListUrl(batch.Id.Value));
        using var producer = await HttpGet(app, url, "producer");
        using var producerJson = JsonDocument.Parse(await producer.Content.ReadAsStringAsync());
        producerJson.RootElement.TryGetProperty("links", out _).ShouldBeFalse();
        test.Credentials.Profiles["operator"] = test.Credentials.Profiles["operator"] with
        {
            Permissions = []
        };
        using var denied = await HttpGet(app, ListUrl(batch.Id.Value));
        denied.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static readonly string[] HttpNames = ["producer", "operator", "scoped", "cross", "noperm", "viewer", "disabled", "listonly", "rawonly"];
    private static readonly Dictionary<string, string> HttpTokens = HttpNames.ToDictionary(x => x, x => OpaqueBearerTokens.Generate(
        x == "producer" ? BearerCredentialKind.Producer : x == "viewer" ? BearerCredentialKind.ViewerPresentationService : BearerCredentialKind.HumanOperator));
    private static readonly Dictionary<string, BearerTokenHash> HttpHashes = HttpTokens.ToDictionary(x => x.Key, x => BearerTokenHash.Create(x.Value));

    [Theory]
    [InlineData("producer", 401)]
    [InlineData("operator", 200)]
    [InlineData("scoped", 404)]
    [InlineData("cross", 200)]
    [InlineData("noperm", 404)]
    [InlineData("viewer", 401)]
    [InlineData("disabled", 401)]
    [InlineData("listonly", 200)]
    [InlineData("rawonly", 404)]
    public async Task Http_rejection_list_enforces_operator_batch_permission_and_scope(string actor, int status)
    {
        await using var test = Create();
        var batch = await test.Seed();
        await using var app = await HttpApp(test);
        using var response = await HttpGet(app, ListUrl(batch.Id.Value), actor);
        ((int)response.StatusCode).ShouldBe(status);
        if (status != 200)
        {
            await HttpProblem(response);
            return;
        }
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        json.RootElement.GetProperty("items").GetArrayLength().ShouldBe(5);
        foreach (var forbidden in new[] { "quarantineId", "protectedContent", "contentSha256", "PRIVATE_RAW", "ContentLocation", "outcome" })
            text.ShouldNotContain(forbidden);
        var keys = json.RootElement.GetProperty("items")[0].EnumerateObject().Select(x => x.Name).Order().ToArray();
        keys.ShouldBe(new[] { "code", "empresaId", "lineNumber", "occurredAt", "safeDigest", "veiculoId" });
    }

    [Theory]
    [InlineData("producer", 401, AuditOutcome.Denied)]
    [InlineData("operator", 200, AuditOutcome.Allowed)]
    [InlineData("scoped", 404, AuditOutcome.Denied)]
    [InlineData("cross", 200, AuditOutcome.Allowed)]
    [InlineData("noperm", 403, AuditOutcome.Denied)]
    [InlineData("viewer", 403, AuditOutcome.Denied)]
    [InlineData("disabled", 401, AuditOutcome.Denied)]
    [InlineData("listonly", 403, AuditOutcome.Denied)]
    [InlineData("rawonly", 200, AuditOutcome.Allowed)]
    public async Task Http_raw_read_requires_its_own_permission_and_audits_once(string actor, int status, AuditOutcome outcome)
    {
        await using var test = Create();
        var batch = await test.Seed();
        await using var app = await HttpApp(test);
        using var response = await HttpGet(app, RawUrl(batch.Id.Value, batch.QuarantineId), actor);
        ((int)response.StatusCode).ShouldBe(status);
        if (status == 200)
        {
            response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
            response.Headers.CacheControl!.NoStore.ShouldBeTrue();
            response.Headers.GetValues("X-Content-Type-Options").ShouldHaveSingleItem().ShouldBe("nosniff");
            (await response.Content.ReadAsStringAsync()).ShouldBe(Fixture.RawText);
            test.Quarantine.OpenCalls.ShouldBe(1);
            test.Quarantine.DisposeCalls.ShouldBe(1);
        }
        else
        {
            await HttpProblem(response);
            test.Quarantine.OpenCalls.ShouldBe(0);
        }
        test.Audit.Records.ShouldHaveSingleItem().Outcome.ShouldBe(outcome);
        await using var db = database.CreateContext();
        (await db.AuditEvents.SingleAsync(x => x.OpaqueResourceId == batch.QuarantineId.ToString("D"))).Outcome.ShouldBe(outcome);
    }

    [Fact]
    public async Task Http_cursor_pages_are_opaque_stable_and_exclude_protected_fields()
    {
        await using var test = Create();
        var batch = await test.Seed();
        await using var app = await HttpApp(test);
        var lines = new List<int>();
        string? cursor = null;
        do
        {
            using var response = await HttpGet(app, ListUrl(batch.Id.Value) + "?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            lines.AddRange(json.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("lineNumber").GetInt32()));
            cursor = json.RootElement.GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        lines.ShouldBe(new[] { 1, 3, 4, 6, 8 });
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("limit=abc")]
    [InlineData("limit=2&limit=3")]
    [InlineData("cursor=")]
    [InlineData("cursor=a&cursor=b")]
    [InlineData("cursor=invalid")]
    public async Task Http_invalid_paging_returns_safe_catalog_problem(string query)
    {
        await using var test = Create();
        var batch = await test.Seed();
        await using var app = await HttpApp(test);
        using var response = await HttpGet(app, ListUrl(batch.Id.Value) + "?" + query);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await HttpProblem(response, TelemetryErrorCodes.RequestInvalid);
    }

    [Theory]
    [InlineData("missing", AuditOutcome.Missing)]
    [InlineData("wrong-batch", AuditOutcome.Missing)]
    [InlineData("expired", AuditOutcome.Expired)]
    [InlineData("deleted", AuditOutcome.Expired)]
    [InlineData("missing-file", AuditOutcome.Missing)]
    [InlineData("invalid-batch", AuditOutcome.Missing)]
    [InlineData("invalid-quarantine", AuditOutcome.Missing)]
    public async Task Http_unavailable_raw_reads_are_non_disclosing_and_audited(string state, AuditOutcome outcome)
    {
        await using var test = Create();
        var batch = await test.Seed(ageDays: state == "expired" ? 31 : 0);
        var parent = state == "wrong-batch" ? (await test.Seed()).Id : batch.Id;
        var id = state == "missing" ? Guid.NewGuid() : batch.QuarantineId;
        if (state == "deleted")
        {
            await using var db = database.CreateContext();
            await db.QuarantinedRejections.Where(x => x.QuarantineId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, test.Clock.UtcNow));
        }
        if (state == "missing-file")
            test.Quarantine.MissingMetadata = true;
        var url = RawUrl(parent.Value, id);
        if (state == "invalid-batch")
            url = url.Replace(parent.Value.ToString("D"), "PRIVATE_INVALID");
        if (state == "invalid-quarantine")
            url = url.Replace(id.ToString("D"), Guid.Empty.ToString("D"));
        await using var app = await HttpApp(test);
        using var response = await HttpGet(app, url);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await HttpProblem(response, TelemetryErrorCodes.ResourceNotFound);
        test.Quarantine.OpenCalls.ShouldBe(0);
        test.Audit.Records.ShouldHaveSingleItem().Outcome.ShouldBe(outcome);
    }

    [Theory]
    [InlineData("allowed")]
    [InlineData("denied")]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("producer")]
    [InlineData("anonymous")]
    [InlineData("wrong-receipt")]
    public async Task Http_audit_failure_blocks_every_raw_response(string state)
    {
        await using var test = Create();
        var batch = await test.Seed(ageDays: state == "expired" ? 31 : 0);
        test.Audit.Fail = state != "wrong-receipt";
        test.Audit.WrongReceipt = state == "wrong-receipt";
        await using var app = await HttpApp(test);
        using var response = await HttpGet(app, RawUrl(batch.Id.Value, state == "missing" ? Guid.NewGuid() : batch.QuarantineId),
            state == "producer" ? "producer" : state == "anonymous" ? null : state == "denied" ? "noperm" : "operator");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        await HttpProblem(response, TelemetryErrorCodes.AuditUnavailable);
        test.Quarantine.OpenCalls.ShouldBe(0);
        test.Audit.Records.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Http_raw_response_waits_for_the_audit_commit_before_opening_content()
    {
        await using var test = Create();
        var batch = await test.Seed();
        await using var app = await HttpApp(test);
        test.Audit.Pause = true;
        var pending = HttpGet(app, RawUrl(batch.Id.Value, batch.QuarantineId));
        await test.Audit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pending.IsCompleted.ShouldBeFalse();
        test.Quarantine.OpenCalls.ShouldBe(0);
        test.Audit.Release.TrySetResult();
        using var response = await pending;
        (await response.Content.ReadAsStringAsync()).ShouldBe(Fixture.RawText);
    }

    [Fact]
    public async Task Http_paging_defaults_to_fifty_and_accepts_at_most_two_hundred()
    {
        await using var test = Create();
        var batch = await test.Seed(finalize: false);
        for (var line = 9; line <= 55; line++)
            await test.AddRejected(batch.Id, line);
        await using var app = await HttpApp(test);
        using var response = await HttpGet(app, ListUrl(batch.Id.Value), "cross");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("items").GetArrayLength().ShouldBe(50);
        json.RootElement.GetProperty("nextCursor").GetString().ShouldNotBeNullOrWhiteSpace();
        using var maximum = await HttpGet(app, ListUrl(batch.Id.Value) + "?limit=200", "cross");
        using var all = JsonDocument.Parse(await maximum.Content.ReadAsStringAsync());
        all.RootElement.GetProperty("items").GetArrayLength().ShouldBe(52);
    }

    private static string ListUrl(Guid batch) => "/api/v1/telemetry/batches/" + batch.ToString("D") + "/rejections";
    private static string RawUrl(Guid batch, Guid quarantine) => ListUrl(batch) + "/" + quarantine.ToString("D") + "/content";
    private static async Task<HttpResponseMessage> HttpGet(WebApplication app, string url, string? actor = "operator")
    {
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (actor is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", HttpTokens[actor]);
        return await client.SendAsync(request);
    }
    private static async Task HttpProblem(HttpResponseMessage response, string? code = null)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var text = await response.Content.ReadAsStringAsync();
        text.ShouldNotContain("PRIVATE");
        using var json = JsonDocument.Parse(text);
        if (code is not null)
            json.RootElement.GetProperty("code").GetString().ShouldBe(code);
        json.RootElement.GetProperty("status").GetInt32().ShouldBe((int)response.StatusCode);
        json.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            response.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("Bearer");
    }
    private async Task<WebApplication> HttpApp(Fixture test)
    {
        test.Credentials.Profiles["listonly"] = new("listonly", OperatorActorKind.Human, ["batch.read"], ["company-a", "company-b"]);
        test.Credentials.Profiles["rawonly"] = new("rawonly", OperatorActorKind.Human, ["quarantine.raw.read"], ["company-a", "company-b"]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(test.Services.GetRequiredService<IConfiguration>().AsEnumerable());
        builder.Services.AddTelemetrySecurity(builder.Configuration);
        builder.Services.AddSingleton<IClock>(test.Clock).AddSingleton<IAuthenticationCredentialStore>(new HttpCredentials(test.Credentials));
        builder.Services.AddTelemetryControlPersistence(database.Connection).AddTelemetryBatchInspection(builder.Configuration);
        builder.Services.AddSingleton<IAuditWriter>(test.Audit).AddSingleton<IQuarantineStore>(test.Quarantine);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapListBatchRejectionsEndpoint();
        app.MapGetQuarantinedRejectionContentEndpoint();
        app.MapGetBatchStatusEndpoint();
        await app.StartAsync();
        return app;
    }
    private sealed class HttpCredentials(Credentials profiles) : IAuthenticationCredentialStore
    {
        public ValueTask<ProducerCredential?> FindProducerAsync(string lookup, CancellationToken token) => ValueTask.FromResult<ProducerCredential?>(
            lookup == HttpHashes["producer"].LookupDigest ? new("producer-key", "producer", HttpHashes["producer"]) : null);
        public ValueTask<OperatorBearerCredential?> FindOperatorAsync(string lookup, BearerCredentialKind kind, CancellationToken token)
        {
            var name = HttpNames.FirstOrDefault(x => x != "producer" && HttpHashes[x].LookupDigest == lookup);
            return ValueTask.FromResult<OperatorBearerCredential?>(name is null ? null : name == "viewer"
                ? new ViewerPresentationServiceCredential("viewer-key", name, "org", HttpHashes[name])
                : new HumanOperatorCredential(name + "-key", name, HttpHashes[name]));
        }
        public ValueTask<CentralMobilidade.Telemetry.Application.Security.OperatorAccessProfile?> GetProfileAsync(string id, CancellationToken token) => profiles.GetProfileAsync(id, token);
    }
}
