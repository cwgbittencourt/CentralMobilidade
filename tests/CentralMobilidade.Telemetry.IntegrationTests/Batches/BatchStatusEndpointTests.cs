using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Endpoints.V1;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using OperatorAccessProfile = CentralMobilidade.Telemetry.Application.Security.OperatorAccessProfile;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
namespace CentralMobilidade.Telemetry.IntegrationTests.Batches;

public sealed class BatchStatusEndpointTests(BatchLifecycleDatabase database) : IClassFixture<BatchLifecycleDatabase>
{
    private static readonly string[] Names = ["producer", "other", "operator", "scoped", "cross", "noperm", "viewer", "disabled", "expired"];
    private static readonly Dictionary<string, string> Tokens = Names.ToDictionary(x => x, x => OpaqueBearerTokens.Generate(
        x is "producer" or "other" ? BearerCredentialKind.Producer : x == "viewer" ? BearerCredentialKind.ViewerPresentationService : BearerCredentialKind.HumanOperator));
    private static readonly Dictionary<string, BearerTokenHash> Hashes = Tokens.ToDictionary(x => x.Key, x => BearerTokenHash.Create(x.Value));

    [Theory]
    [InlineData("producer", 200)]
    [InlineData("other", 404)]
    [InlineData("operator", 200)]
    [InlineData("scoped", 404)]
    [InlineData("cross", 200)]
    [InlineData("noperm", 404)]
    [InlineData("viewer", 401)]
    [InlineData("disabled", 401)]
    [InlineData("expired", 401)]
    public async Task Http_status_enforces_credential_kind_ownership_and_all_companies(string identity, int status)
    {
        await using var app = await Start();
        var id = await Seed(app);
        using var client = app.GetTestClient();
        using var response = await Get(client, id.ToString("D"), identity);
        ((int)response.StatusCode).ShouldBe(status);
        if (status == 200)
        {
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
            response.Headers.CacheControl!.NoStore.ShouldBeTrue();
            var body = await response.Content.ReadFromJsonAsync<BatchStatus>();
            body.ShouldNotBeNull().BatchId.ShouldBe(id);
            body.State.ShouldBe(BatchState.Completed);
            body.AcceptedLines.ShouldBe(2);
            body.RejectedLines.ShouldBe(0);
            body.TotalLines.ShouldBe(2);
            body.ProcessingStartedAt.ShouldNotBeNull();
            body.CompletedAt.ShouldNotBeNull();
            if (identity == "producer")
                body.Links.ShouldBeNull();
            var json = await response.Content.ReadAsStringAsync();
            json.ShouldNotContain("producerId");
            json.ShouldNotContain("companyScope");
            json.ShouldNotContain(Tokens[identity]);
        }
        else
            await AssertProblem(response, status == 401 ? "AUTHENTICATION_REQUIRED" : "RESOURCE_NOT_FOUND");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("malformed")]
    [InlineData("unknown")]
    [InlineData("multiple")]
    public async Task Invalid_authentication_returns_one_bearer_challenge_and_one_problem(string variant)
    {
        await using var app = await Start();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, GetBatchStatusEndpoint.Route.Replace("{batchId}", Guid.NewGuid().ToString("D")));
        if (variant == "malformed")
            request.Headers.TryAddWithoutValidation("Authorization", "Basic PRIVATE_DIAGNOSTIC");
        if (variant == "unknown")
            request.Headers.Authorization = new("Bearer", OpaqueBearerTokens.Generate(BearerCredentialKind.Producer));
        if (variant == "multiple")
            request.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + Tokens["producer"], "Bearer " + Tokens["operator"] });
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await AssertProblem(response, "AUTHENTICATION_REQUIRED");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("invalid-uuid")]
    [InlineData("empty-uuid")]
    [InlineData("receiving")]
    public async Task Unknown_expired_unacknowledged_and_invalid_paths_use_the_same_safe_404(string scenario)
    {
        await using var app = await Start();
        var id = await Seed(app, scenario == "expired" ? 31 : 0);
        if (scenario == "receiving")
        {
            await using var db = database.CreateContext();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ingestion_batches SET \"State\"='Receiving', \"RowVersion\"=\"RowVersion\"+1 WHERE \"BatchId\"={id}");
        }
        var path = scenario switch
        {
            "missing" => Guid.NewGuid().ToString("D"),
            "invalid-uuid" => "PRIVATE_INVALID_UUID",
            "empty-uuid" => Guid.Empty.ToString("D"),
            _ => id.ToString("D")
        };
        using var client = app.GetTestClient();
        using var response = await Get(client, path, "producer");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AssertProblem(response, "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Profile_revocation_is_observed_on_the_next_http_request()
    {
        await using var app = await Start();
        var id = await Seed(app);
        using var client = app.GetTestClient();
        using var before = await Get(client, id.ToString("D"), "operator");
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        var options = app.Services.GetRequiredService<IOptionsMonitor<SecurityOptions>>().CurrentValue;
        var index = Array.FindIndex(options.Profiles, x => x.ActorId == "operator");
        options.Profiles[index] = options.Profiles[index] with
        {
            Enabled = false
        };
        using var after = await Get(client, id.ToString("D"), "operator");
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await AssertProblem(after, "AUTHENTICATION_REQUIRED");
    }

    private static async Task AssertProblem(HttpResponseMessage response, string code)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("code").GetString().ShouldBe(code);
        json.RootElement.GetProperty("status").GetInt32().ShouldBe((int)response.StatusCode);
        json.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
        json.RootElement.ToString().ShouldNotContain("PRIVATE_");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            response.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("Bearer");
    }
    private static Task<HttpResponseMessage> Get(HttpClient client, string id, string identity)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, GetBatchStatusEndpoint.Route.Replace("{batchId}", id));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens[identity]);
        return client.SendAsync(request);
    }
    private async Task<WebApplication> Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddTelemetrySecurity(builder.Configuration);
        builder.Services.Configure<SecurityOptions>(options =>
        {
            options.Producers = [new("producer-key", "producer", Hashes["producer"]), new("other-key", "other", Hashes["other"])];
            options.Operators = Names.Where(x => x is not ("producer" or "other" or "viewer"))
                .Select(x => new HumanOperatorCredential(x + "-key", x, Hashes[x], ExpiresAt: x == "expired" ? DateTimeOffset.UnixEpoch : null)).ToArray();
            options.ViewerServices = [new("viewer-key", "viewer", "org", Hashes["viewer"])];
            options.Profiles = Names.Where(x => x is not ("producer" or "other"))
                .Select(x => new OperatorAccessProfile(x, x == "viewer" ? OperatorActorKind.ViewerPresentationService : OperatorActorKind.Human,
                    x == "viewer" ? ["telemetry.read"] : x == "noperm" ? [] : x == "cross" ? ["batch.read", "cross_company.read"] : ["batch.read"],
                    x == "scoped" ? ["company-a"] : ["company-a", "company-b"], x != "disabled", x == "viewer" ? "org" : null)).ToArray();
        });
        builder.Services.AddTelemetryControlPersistence(database.Connection).AddTelemetryBatchInspection(builder.Configuration);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGetBatchStatusEndpoint();
        await app.StartAsync();
        return app;
    }
    private static async Task<Guid> Seed(WebApplication app, int ageDays = 0)
    {
        var store = app.Services.GetRequiredService<IBatchStateStore>();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddDays(-ageDays).ToUnixTimeMilliseconds());
        var envelope = new TelemetryBatchEnvelope("1", Guid.NewGuid(), new string('a', 32), "producer", at,
            "text/plain; charset=utf-8", "gzip", 128, new string('a', 64), "v1");
        var batch = await store.CreateAsync(envelope, at.AddDays(30), default);
        foreach (var state in new[] { BatchLifecycle.Publishing, BatchLifecycle.Accepted, BatchLifecycle.Processing })
        {
            (await store.TryTransitionAsync(batch.BatchId, batch.Version, new(state, at.AddSeconds(batch.Version)), default)).ShouldBeTrue();
            batch = (await store.GetAsync(batch.BatchId, default))!;
        }
        await store.RecordLineResultsAsync(batch.BatchId, [new(1, LineOutcome.Accepted, null, new("company-a"), new("vehicle"), "hmac-sha256:v1:" + new string('a', 64), at),
            new(2, LineOutcome.Accepted, null, new("company-b"), new("vehicle"), "hmac-sha256:v1:" + new string('b', 64), at)], default);
        batch = (await store.GetAsync(batch.BatchId, default))!;
        (await store.TryTransitionAsync(batch.BatchId, batch.Version, new(BatchLifecycle.Completed, at.AddSeconds(20), new(2, 0, 0, 2)), default)).ShouldBeTrue();
        return batch.BatchId.Value;
    }
}
