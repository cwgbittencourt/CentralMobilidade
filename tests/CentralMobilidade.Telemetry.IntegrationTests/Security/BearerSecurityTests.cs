using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Security;

public sealed class BearerSecurityTests
{
    [Theory]
    [Trait("Feature", "FoundationSingleInflux")]
    [InlineData("/telemetry/not-in-registry-a")]
    [InlineData("/driver/not-in-registry-a")]
    [InlineData("/raw/not-in-registry-a")]
    [InlineData("/combined-raw/not-in-registry-a")]
    public async Task Shared_ingestion_credential_does_not_grant_operator_or_viewer_privileges(string path)
    {
        await using var fixture = await Fixture.StartAsync();
        using var response = await fixture.GetAsync(path, ProducerToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Authentication is required.");
    }

    private static readonly string ProducerToken = OpaqueBearerTokens.Generate(BearerCredentialKind.Producer);
    private static readonly string OperatorToken = OpaqueBearerTokens.Generate(BearerCredentialKind.HumanOperator);
    private static readonly string ViewerToken = OpaqueBearerTokens.Generate(BearerCredentialKind.ViewerPresentationService);
    private static readonly BearerTokenHash ProducerHash = BearerTokenHash.Create(ProducerToken);
    private static readonly BearerTokenHash OperatorHash = BearerTokenHash.Create(OperatorToken);
    private static readonly BearerTokenHash ViewerHash = BearerTokenHash.Create(ViewerToken);

    [Fact]
    public async Task CredentialTypesAreIsolatedAndScoped()
    {
        await using var fixture = await Fixture.StartAsync();
        (await fixture.GetAsync("/producer", ProducerToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.GetAsync("/producer", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await fixture.GetAsync("/producer", ViewerToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await fixture.GetAsync("/telemetry/empresa-1", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.GetAsync("/telemetry/empresa-1", ViewerToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.GetAsync("/driver/empresa-1", ProducerToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await fixture.GetAsync("/driver/empresa-1", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.GetAsync("/raw/empresa-1", ViewerToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await fixture.GetAsync("/driver/empresa-2", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.GetAsync("/driver/empresa-2", ViewerToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        fixture.Audit.Records.Count.ShouldBe(4);
        foreach (var record in fixture.Audit.Records)
        {
            record.Outcome.ShouldBe(AuditOutcome.Denied);
            record.OpaqueResourceId.ShouldBeNull();
        }
        var viewerDenied = fixture.Audit.Records.ToArray()[1];
        viewerDenied.Actor.Kind.ShouldBe(OperatorActorKind.ViewerPresentationService);
        viewerDenied.Actor.GrafanaOrganizationId.ShouldBe("org-42");
    }

    [Fact]
    public async Task MissingInvalidDisabledExpiredAndWrongTypeTokensHaveGenericFailures()
    {
        await using var fixture = await Fixture.StartAsync();
        foreach (var token in new string?[] { null, "invalid", ProducerToken })
        {
            var response = await fixture.GetAsync("/driver/empresa-1", token);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await response.Content.ReadAsStringAsync()).ShouldContain("Authentication is required.");
        }
        fixture.Set("Authentication:Operators:0:Enabled", "false");
        (await fixture.GetAsync("/driver/empresa-1", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        fixture.Set("Authentication:Operators:0:Enabled", "true");
        fixture.Set("Authentication:Operators:0:ExpiresAt", "2000-01-01T00:00:00Z");
        (await fixture.GetAsync("/driver/empresa-1", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AuditFailureBlocksDeniedResponseAndDoesNotDiscloseDiagnostics()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Audit.Fail = true;
        var response = await fixture.GetAsync("/driver/empresa-2", OperatorToken);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("AUDIT_UNAVAILABLE");
        body.ShouldNotContain("secret backend diagnostic");
        body.ShouldNotContain(OperatorToken);
    }

    [Fact]
    public async Task RotationKeepsActorAndRevokesOldCredential()
    {
        await using var fixture = await Fixture.StartAsync();
        var replacement = OpaqueBearerTokens.Generate(BearerCredentialKind.HumanOperator);
        fixture.SetHash("Authentication:Operators:1", "rotated", "human-1", BearerTokenHash.Create(replacement));
        (await fixture.GetAsync("/driver/empresa-1", replacement)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.GetAsync("/driver/empresa-1", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Set("Authentication:Operators:0:Enabled", "false");
        (await fixture.GetAsync("/driver/empresa-1", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await fixture.GetAsync("/driver/empresa-1", replacement)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExplicitCrossCompanyPermissionAndProfileRevocationAreRechecked()
    {
        await using var fixture = await Fixture.StartAsync();
        (await fixture.GetAsync("/driver/empresa-2", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        fixture.Set("Authentication:Profiles:0:Permissions:1", "cross_company.read");
        (await fixture.GetAsync("/driver/empresa-2", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Set("Authentication:Profiles:0:Enabled", "false");
        (await fixture.GetAsync("/driver/empresa-1", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void HashesAreSaltedAndNeverStoreRawTokens()
    {
        var second = BearerTokenHash.Create(OperatorToken);
        second.Salt.ShouldNotBe(OperatorHash.Salt);
        second.Verifier.ShouldNotBe(OperatorHash.Verifier);
        second.Matches(OperatorToken).ShouldBeTrue();
        second.Matches(ViewerToken).ShouldBeFalse();
        JsonSerializer.Serialize(second).ShouldNotContain(OperatorToken);
        second.ToString().ShouldNotContain(second.Verifier);
        Should.Throw<ArgumentException>(() => BearerTokenHash.Create("weak-password"));
    }

    [Fact]
    public async Task BatchPoliciesRequireEveryCompanyAndProducerOwnership()
    {
        await using var fixture = await Fixture.StartAsync();
        var id = fixture.Batches.Snapshot.BatchId.Value;
        (await fixture.GetAsync($"/producer-batches/{id}", ProducerToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Batches.Snapshot = fixture.Batches.Snapshot with
        {
            ProducerId = "other-producer"
        };
        (await fixture.GetAsync($"/producer-batches/{id}", ProducerToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.GetAsync($"/batches/{id}", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        fixture.Set("Authentication:Profiles:0:Permissions:3", "cross_company.read");
        (await fixture.GetAsync($"/batches/{id}", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Batches.Snapshot = fixture.Batches.Snapshot with
        {
            CompanyScopeComplete = false
        };
        (await fixture.GetAsync($"/batches/{id}", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Set("Authentication:Profiles:0:Permissions:3", "telemetry.read");
        (await fixture.GetAsync($"/batches/{id}", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        fixture.Batches.Snapshot = fixture.Batches.Snapshot with
        {
            ProducerId = "producer-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        (await fixture.GetAsync($"/producer-batches/{id}", ProducerToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.GetAsync($"/producer-batches/{Guid.NewGuid()}", ProducerToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void StartupValidationRejectsViewerPrivilegesAndInvalidHashConfiguration()
    {
        var validator = new SecurityOptionsValidator();
        var privilegedViewer = new SecurityOptions
        {
            Profiles = [new("viewer", OperatorActorKind.ViewerPresentationService,
            ["cross_company.read"], ["empresa-1"], true, "org-42")]
        };
        validator.Validate(null, privilegedViewer).Failed.ShouldBeTrue();
        var invalidHash = new SecurityOptions { Producers = [new("id", "producer", ProducerHash with { Iterations = 1 })] };
        var failure = validator.Validate(null, invalidHash);
        failure.Failed.ShouldBeTrue();
        string.Join(" ", failure.Failures).ShouldNotContain(ProducerHash.Verifier);
    }

    [Fact]
    public async Task MalformedAndDuplicateBearerHeadersDoNotAuthenticate()
    {
        await using var fixture = await Fixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/driver/empresa-1");
        request.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + OperatorToken, "Bearer " + ViewerToken });
        (await fixture.Client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await fixture.GetAsync("/driver/empresa-1", OperatorToken + "x")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        foreach (var token in new[] { ProducerToken, OperatorToken, ViewerToken })
        {
            foreach (var log in fixture.Logs.Messages)
            {
                log.ShouldNotContain(token);
            }
        }
    }

    [Fact]
    public async Task InvalidViewerPrivilegeConfigurationPreventsActualHostStartup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Profiles:0:ActorId"] = "viewer",
            ["Authentication:Profiles:0:Kind"] = "ViewerPresentationService",
            ["Authentication:Profiles:0:GrafanaOrganizationId"] = "org-42",
            ["Authentication:Profiles:0:Permissions:0"] = "cross_company.read",
            ["Authentication:Profiles:0:CompanyIds:0"] = "empresa-1"
        });
        builder.Services.AddTelemetrySecurity(builder.Configuration);
        await using var app = builder.Build();
        var failure = await Should.ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => app.StartAsync());
        failure.Message.ShouldContain("Authentication configuration is invalid.");
    }

    [Fact]
    public async Task CombinedPoliciesAuditTheSensitiveOperationAndMissingWriterFailsClosed()
    {
        await using var fixture = await Fixture.StartAsync();
        (await fixture.GetAsync("/combined-raw/empresa-1", ViewerToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        fixture.Audit.Records.TryPeek(out var record).ShouldBeTrue();
        record!.Action.ShouldBe(AuditAction.ReadQuarantineContent);
        record.ResourceCategory.ShouldBe(AuditResourceCategory.Quarantine);
        await using var withoutAudit = await Fixture.StartAsync(withAudit: false);
        (await withoutAudit.GetAsync("/driver/empresa-2", OperatorToken)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication app;
        public HttpClient Client
        {
            get;
        }
        public AuditStub Audit
        {
            get;
        }
        public BatchStoreStub Batches
        {
            get;
        }
        private readonly IConfigurationRoot configuration;
        public CapturedLogs Logs
        {
            get;
        }

        private Fixture(WebApplication app, AuditStub audit, BatchStoreStub batches, CapturedLogs logs, IConfigurationRoot configuration)
        {
            this.app = app;
            Audit = audit;
            Batches = batches;
            Logs = logs;
            this.configuration = configuration;
            Client = app.GetTestClient();
        }

        public static async Task<Fixture> StartAsync(bool withAudit = true)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var logs = new CapturedLogs();
            builder.Logging.AddProvider(logs);
            builder.WebHost.UseTestServer();
            var values = new Dictionary<string, string?>();
            AddHash(values, "Authentication:Producers:0", "producer-credential", "producer-1", ProducerHash);
            values.Remove("Authentication:Producers:0:ActorId");
            AddHash(values, "Authentication:Operators:0", "operator-credential", "human-1", OperatorHash);
            AddHash(values, "Authentication:ViewerServices:0", "viewer-credential", "viewer-1", ViewerHash);
            values["Authentication:Producers:0:ProducerId"] = "producer-1";
            values["Authentication:ViewerServices:0:GrafanaOrganizationId"] = "org-42";
            foreach (var index in new[] { 0, 1 })
            {
                var prefix = $"Authentication:Profiles:{index}:";
                values[prefix + "ActorId"] = index == 0 ? "human-1" : "viewer-1";
                values[prefix + "Kind"] = index == 0 ? "Human" : "ViewerPresentationService";
                values[prefix + "Enabled"] = "true";
                values[prefix + "CompanyIds:0"] = "empresa-1";
                values[prefix + "Permissions:0"] = "driver.read";
                values[prefix + "Permissions:1"] = "telemetry.read";
            }
            values["Authentication:Profiles:1:GrafanaOrganizationId"] = "org-42";
            values["Authentication:Profiles:0:Permissions:2"] = "batch.read";
            builder.Configuration.AddInMemoryCollection(values);
            var audit = new AuditStub();
            if (withAudit)
            {
                builder.Services.AddSingleton<IAuditWriter>(audit);
            }
            var batches = new BatchStoreStub();
            builder.Services.AddSingleton<IBatchStateStore>(batches);
            builder.Services.AddTelemetrySecurity(builder.Configuration);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/producer", () => "ok").RequireAuthorization(OperatorAuthorizationPolicies.Producer);
            app.MapGet("/producer-batches/{batchId}", () => "ok").RequireAuthorization(OperatorAuthorizationPolicies.ProducerBatchRead);
            app.MapGet("/batches/{batchId}", () => "ok").RequireAuthorization(OperatorAuthorizationPolicies.BatchRead);
            app.MapGet("/driver/{empresaId}", () => "ok").RequireAuthorization(OperatorAuthorizationPolicies.DriverRead);
            app.MapGet("/telemetry/{empresaId}", () => "ok").RequireAuthorization(OperatorAuthorizationPolicies.TelemetryRead);
            app.MapGet("/raw/{empresaId}", () => "ok").RequireAuthorization(OperatorAuthorizationPolicies.QuarantineRawRead);
            app.MapGet("/combined-raw/{empresaId}", () => "ok").RequireAuthorization(OperatorAuthorizationPolicies.BatchRead, OperatorAuthorizationPolicies.QuarantineRawRead);
            await app.StartAsync();
            return new Fixture(app, audit, batches, logs, builder.Configuration);
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return await Client.SendAsync(request);
        }

        public void Set(string key, string value)
        {
            configuration[key] = value;
            configuration.Reload();
        }

        public void SetHash(string prefix, string credentialId, string actorId, BearerTokenHash hash)
        {
            var values = new Dictionary<string, string?>();
            AddHash(values, prefix, credentialId, actorId, hash);
            foreach (var pair in values)
            {
                configuration[pair.Key] = pair.Value;
            }
            configuration.Reload();
        }

        private static void AddHash(Dictionary<string, string?> values, string prefix, string id, string actor, BearerTokenHash hash)
        {
            values[prefix + ":CredentialId"] = id;
            values[prefix + ":ActorId"] = actor;
            values[prefix + ":Enabled"] = "true";
            values[prefix + ":Hash:LookupDigest"] = hash.LookupDigest;
            values[prefix + ":Hash:Salt"] = hash.Salt;
            values[prefix + ":Hash:Verifier"] = hash.Verifier;
            values[prefix + ":Hash:Iterations"] = hash.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose()
        {
        }
        private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class BatchStoreStub : IBatchStateStore
    {
        public BatchSnapshot Snapshot
        {
            get; set;
        } = new(new BatchId(Guid.NewGuid()), "producer-1", BatchLifecycle.Accepted,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), null, null, new BatchCounters(0, 0, 0, null),
            new Dictionary<string, long>(), [new CompanyId("empresa-1"), new CompanyId("empresa-2")], true, 1);
        public ValueTask<BatchSnapshot?> GetAsync(BatchId batchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(batchId == Snapshot.BatchId ? Snapshot : null);
        public ValueTask<BatchSnapshot> CreateAsync(TelemetryBatchEnvelope metadata, DateTimeOffset expiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> TryTransitionAsync(BatchId batchId, long expectedVersion, BatchTransition transition, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask RecordLineResultsAsync(BatchId batchId, IReadOnlyCollection<LineProcessingOutcome> results, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<RejectionResultPage> GetRejectionsAsync(BatchId batchId, string? cursor, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AuditStub : IAuditWriter
    {
        public ConcurrentQueue<AuditRecord> Records { get; } = new();
        public bool Fail
        {
            get; set;
        }
        public ValueTask<AuditReceipt> AppendAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail)
            {
                throw new IOException("secret backend diagnostic");
            }
            Records.Enqueue(record);
            return ValueTask.FromResult(new AuditReceipt(record.EventId, DateTimeOffset.UtcNow));
        }
    }
}
