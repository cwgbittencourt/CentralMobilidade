using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Auditing;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using OperatorAccessProfile = CentralMobilidade.Telemetry.Application.Security.OperatorAccessProfile;

namespace CentralMobilidade.Telemetry.IntegrationTests.Privacy;

public sealed class PresentationAuthorizationTests(PresentationAuthorizationDatabase database)
    : IClassFixture<PresentationAuthorizationDatabase>
{
    private const string Company = "valid-unregistered-company";
    private const string OtherCompany = "other-unregistered-company";
    private const string Driver = "driver-private-123";
    private const string Name = "Synthetic Private Driver";
    private const string Latest = "/api/v1/presentation/telemetry/latest";
    private const string Trip = "/api/v1/presentation/telemetry/trips/vehicle-1";
    private const string Health = "/api/v1/presentation/telemetry/ingestion-health";
    private const string DriverRoute = "/api/v1/presentation/drivers/" + Driver;
    private const string Start = "2026-10-01T00:00:00Z";

    // These routes are middleware probes, never substitutes for production presentation routes.
    [Theory]
    [InlineData("human", "driver", true)]
    [InlineData("service", "driver", true)]
    [InlineData("human", "telemetry", true)]
    [InlineData("service", "telemetry", true)]
    [InlineData("human", "driver", false)]
    [InlineData("service", "driver", false)]
    [InlineData("human", "telemetry", false)]
    [InlineData("service", "telemetry", false)]
    public async Task Middleware_enforces_company_scope_and_persists_trusted_denial_attribution(string actor, string operation, bool inScope)
    {
        await using var host = await Host.StartAsync(database);
        using var response = await host.GetAsync($"/_policy/{operation}/{(inScope ? Company : OtherCompany)}", actor);
        response.StatusCode.ShouldBe(inScope ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        host.Executions.ShouldBe(inScope ? 1 : 0);
        if (inScope)
            host.Audit.Records.ShouldBeEmpty();
        else
        {
            var record = host.Audit.Records.ShouldHaveSingleItem();
            record.Actor.ActorId.ShouldBe(actor == "service" ? "viewer-account" : "human-account");
            record.Actor.Kind.ShouldBe(actor == "service" ? OperatorActorKind.ViewerPresentationService : OperatorActorKind.Human);
            record.Actor.GrafanaOrganizationId.ShouldBe(actor == "service" ? "org-42" : null);
            record.Action.ShouldBe(operation == "driver" ? AuditAction.ResolveDriver : AuditAction.ReadTelemetry);
            record.Outcome.ShouldBe(AuditOutcome.Denied);
            record.CompanyScope.ShouldBeEmpty();
            record.OpaqueResourceId.ShouldBeNull();
            await host.AssertPersistedAsync(record);
            AssertPrivate(JsonSerializer.Serialize(record), host);
        }
    }

    [Theory]
    [InlineData("human", false, false)]
    [InlineData("human", true, true)]
    [InlineData("service", false, false)]
    [InlineData("service", true, false)]
    public async Task Only_explicit_human_cross_company_permission_expands_scope(string actor, bool cross, bool expected)
    {
        await using var host = await Host.StartAsync(database);
        var principal = new OperatorActor(actor == "human" ? "human-account" : "viewer-account",
            actor == "human" ? OperatorActorKind.Human : OperatorActorKind.ViewerPresentationService,
            actor == "human" ? null : "org-42");
        // Exercise the real authorizer even with a malicious privileged service profile.
        var store = new ProfileStore(new OperatorAccessProfile(principal.ActorId, principal.Kind,
            cross ? ["driver.read", "telemetry.read", "cross_company.read"] : ["driver.read", "telemetry.read"],
            [Company], true, principal.GrafanaOrganizationId));
        var authorizer = new StoredOperatorAuthorizer(store);
        foreach (var permission in new[] { OperatorPermission.DriverRead, OperatorPermission.TelemetryRead })
            (await authorizer.AuthorizeAsync(principal, permission, [new(Company), new(OtherCompany)], default)).ShouldBe(expected ? OperatorAuthorizationDecision.Allowed : OperatorAuthorizationDecision.Denied);
        (await authorizer.AuthorizeAsync(principal, OperatorPermission.QuarantineRawRead, [new(OtherCompany)], default)).ShouldBe(OperatorAuthorizationDecision.Denied);
    }

    [Theory]
    [InlineData(OperatorPermission.DriverRead)]
    [InlineData(OperatorPermission.TelemetryRead)]
    public async Task Cross_company_permission_never_implies_the_resource_permission(OperatorPermission permission)
    {
        var store = new ProfileStore(new("human-account", OperatorActorKind.Human, ["cross_company.read"], [Company]));
        var authorizer = new StoredOperatorAuthorizer(store);
        (await authorizer.AuthorizeAsync(new("human-account", OperatorActorKind.Human), permission, [new(OtherCompany)], default))
            .ShouldBe(OperatorAuthorizationDecision.Denied);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other-org")]
    public async Task Service_organization_must_match_the_server_side_profile(string? organization)
    {
        var store = new ProfileStore(new("viewer-account", OperatorActorKind.ViewerPresentationService,
            ["telemetry.read", "driver.read"], [Company], true, "org-42"));
        var authorizer = new StoredOperatorAuthorizer(store);
        foreach (var permission in new[] { OperatorPermission.DriverRead, OperatorPermission.TelemetryRead })
            (await authorizer.AuthorizeAsync(new("viewer-account", OperatorActorKind.ViewerPresentationService, organization), permission, [new(Company)], default))
                .ShouldBe(OperatorAuthorizationDecision.Denied);
    }

    [Theory]
    [InlineData("producer", "driver")]
    [InlineData("producer", "telemetry")]
    [InlineData("missing", "driver")]
    [InlineData("invalid", "driver")]
    public async Task Middleware_denials_audit_unidentified_callers_and_never_run_the_resource(string actor, string operation)
    {
        await using var host = await Host.StartAsync(database);
        using var response = await host.GetAsync($"/_policy/{operation}/{Company}", actor);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        host.Executions.ShouldBe(0);
        var record = host.Audit.Records.ShouldHaveSingleItem();
        record.Actor.Kind.ShouldBe(OperatorActorKind.Unidentified);
        record.Actor.ActorId.ShouldBe("unauthenticated");
        record.Actor.GrafanaOrganizationId.ShouldBeNull();
        await host.AssertPersistedAsync(record);
        AssertPrivate(await response.Content.ReadAsStringAsync(), host);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("wrong-event")]
    [InlineData("missing-time")]
    [InlineData("non-utc")]
    public async Task Middleware_fails_closed_on_audit_failure_or_untrustworthy_receipt(string mode)
    {
        await using var host = await Host.StartAsync(database);
        host.Audit.Mode = mode;
        using var response = await host.GetAsync($"/_policy/driver/{OtherCompany}", "service");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("AUDIT_UNAVAILABLE");
        AssertPrivate(body, host);
        host.Executions.ShouldBe(0);
    }

    [Fact]
    public async Task Middleware_waits_for_real_Postgres_commit_before_completing_a_denial()
    {
        await using var host = await Host.StartAsync(database);
        host.Audit.Mode = "barrier";
        var pending = host.GetAsync($"/_policy/driver/{OtherCompany}", "service");
        try
        {
            await host.Audit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pending.IsCompleted.ShouldBeFalse();
            await using var db = database.Create();
            var id = host.Audit.Records.ShouldHaveSingleItem().EventId;
            (await db.AuditEvents.AnyAsync(e => e.EventId == id)).ShouldBeFalse();
        }
        finally { host.Audit.Release.TrySetResult(); }
        using var response = await pending;
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await host.AssertPersistedAsync(host.Audit.Records.ShouldHaveSingleItem());
    }

    [Theory]
    [Trait("PendingImplementation", "T070")]
    [InlineData(Latest)]
    [InlineData(Trip)]
    [InlineData(Health)]
    [InlineData(DriverRoute)]
    public async Task Production_presentation_routes_reject_the_shared_producer_token(string route)
    {
        await using var host = await Host.StartAsync(database, production: true);
        using var response = await host.GetAsync(Query(route), "producer");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        host.Driver.Calls.ShouldBe(0);
        host.Backend.Calls.ShouldBe(0);
        host.Audit.Records.ShouldContain(r => r.Outcome == AuditOutcome.Denied && r.Actor.Kind == OperatorActorKind.Unidentified);
        AssertPrivate(await response.Content.ReadAsStringAsync(), host);
    }

    [Theory]
    [Trait("PendingImplementation", "T070")]
    [InlineData("human", Latest)]
    [InlineData("service", Latest)]
    [InlineData("human", Trip)]
    [InlineData("service", Trip)]
    [InlineData("human", Health)]
    [InlineData("service", Health)]
    [InlineData("human", DriverRoute)]
    [InlineData("service", DriverRoute)]
    public async Task Unauthorized_company_is_not_disclosed_or_queried_even_with_spoofed_scope_headers(string actor, string route)
    {
        await using var host = await Host.StartAsync(database, production: true);
        using var response = await host.GetAsync(Query(route, OtherCompany), actor);
        // Driver contract permits 403; telemetry deliberately uses indistinguishable 404.
        response.StatusCode.ShouldBe(route == DriverRoute ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound);
        host.Driver.Calls.ShouldBe(0);
        host.Backend.Calls.ShouldBe(0);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain(OtherCompany);
        AssertPrivate(body, host);
        foreach (var record in host.Audit.Records)
            await host.AssertPersistedAsync(record);
        host.Audit.Records.ShouldContain(r => r.Outcome == AuditOutcome.Denied);
    }

    public static IEnumerable<object[]> NumericBoundaries()
    {
        foreach (var (route, maximum) in new[] { (Latest, 500), (Trip, 10000), (Health, 10000) })
            foreach (var (limit, valid) in new[] { ("-1", false), ("0", false), ("1", true), (maximum.ToString(CultureInfo.InvariantCulture), true),
                ((maximum + 1).ToString(CultureInfo.InvariantCulture), false), ("2147483648", false), ("1.5", false) })
                yield return [route, "limit=" + limit, valid];
        foreach (var (seconds, valid) in new[] { ("-1", false), ("0", false), ("59", false), ("60", true), ("61", true), ("2147483648", false) })
            yield return [Health, "granularitySeconds=" + seconds, valid];
        foreach (var (route, max) in new[] { (Trip, TimeSpan.FromHours(24)), (Health, TimeSpan.FromDays(7)) })
            foreach (var (duration, valid) in new[] { (TimeSpan.FromTicks(-1), false), (TimeSpan.Zero, false), (TimeSpan.FromTicks(1), true),
                (max, true), (max + TimeSpan.FromTicks(1), false) })
                yield return [route, "toUtc=" + Uri.EscapeDataString(DateTimeOffset.Parse(Start, CultureInfo.InvariantCulture).Add(duration).ToString("O", CultureInfo.InvariantCulture)), valid];
    }

    [Theory]
    [Trait("PendingImplementation", "T069-T070")]
    [MemberData(nameof(NumericBoundaries))]
    public async Task Every_numeric_query_boundary_is_enforced_by_production_before_backend_IO(string route, string query, bool valid)
    {
        await using var host = await Host.StartAsync(database, production: true);
        using var response = await host.GetAsync(Query(route, extra: query), "service");
        response.StatusCode.ShouldBe(valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
        if (!valid)
        {
            host.Backend.Calls.ShouldBe(0);
            (await response.Content.ReadAsStringAsync()).ShouldContain("REQUEST_INVALID");
        }
    }

    [Theory]
    [Trait("PendingImplementation", "T069-T070")]
    [InlineData(Latest, "LatestPositionMaxVehicles", "2", "limit=3", false)]
    [InlineData(Latest, "LatestPositionMaxVehicles", "2", "limit=2", true)]
    [InlineData(Trip, "TripMaxPoints", "2", "limit=3", false)]
    [InlineData(Trip, "TripMaxPoints", "2", "limit=2", true)]
    [InlineData(Trip, "TripMaxDuration", "00:30:00", "toUtc=2026-10-01T00:30:00.0000001Z", false)]
    [InlineData(Trip, "TripMaxDuration", "00:30:00", "toUtc=2026-10-01T00:30:00Z", true)]
    [InlineData(Health, "HealthMaxPoints", "2", "limit=3", false)]
    [InlineData(Health, "HealthMaxPoints", "2", "limit=2", true)]
    [InlineData(Health, "HealthMaxDuration", "01:00:00", "toUtc=2026-10-01T01:00:00.0000001Z", false)]
    [InlineData(Health, "HealthMaxDuration", "01:00:00", "toUtc=2026-10-01T01:00:00Z", true)]
    [InlineData(Health, "HealthMinGranularity", "00:02:00", "granularitySeconds=119", false)]
    [InlineData(Health, "HealthMinGranularity", "00:02:00", "granularitySeconds=120", true)]
    public async Task Tighter_configured_viewer_limits_override_public_maxima(string route, string option, string value, string query, bool valid)
    {
        await using var host = await Host.StartAsync(database, production: true, option: option, value: value);
        using var response = await host.GetAsync(Query(route, extra: query), "human");
        response.StatusCode.ShouldBe(valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
        if (!valid)
            host.Backend.Calls.ShouldBe(0);
    }

    [Theory]
    [Trait("PendingImplementation", "T069-T070")]
    [InlineData("human", Latest)]
    [InlineData("human", Trip)]
    [InlineData("human", Health)]
    [InlineData("service", Latest)]
    [InlineData("service", Trip)]
    [InlineData("service", Health)]
    public async Task Scoped_humans_and_services_can_query_unregistered_company_with_default_limits(string actor, string route)
    {
        await using var host = await Host.StartAsync(database, production: true);
        using var response = await host.GetAsync(Query(route), actor);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        host.Driver.Calls.ShouldBe(0);
    }

    [Theory]
    [Trait("PendingImplementation", "T068-T070")]
    [InlineData("human")]
    [InlineData("service")]
    public async Task Driver_identity_is_minimal_and_audited_with_company_and_trusted_actor_before_disclosure(string actor)
    {
        await using var host = await Host.StartAsync(database, production: true);
        using var response = await host.GetAsync(Query(DriverRoute), actor);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(new[] { "cpfLast4", "displayName", "driverId" });
        json.RootElement.GetProperty("displayName").GetString().ShouldBe(Name);
        json.RootElement.GetProperty("driverId").GetString().ShouldBe(Driver);
        json.RootElement.GetProperty("cpfLast4").GetString().ShouldBe("1234");
        host.Driver.Scopes.ShouldBe(new[] { Company });
        host.Driver.Drivers.ShouldBe(new[] { Driver });
        var record = host.Audit.Records.Single(r => r.Action == AuditAction.ResolveDriver && r.Outcome == AuditOutcome.Allowed);
        record.CompanyScope.Select(c => c.Value).ShouldBe(new[] { Company });
        record.Actor.ActorId.ShouldBe(actor == "human" ? "human-account" : "viewer-account");
        record.Actor.GrafanaOrganizationId.ShouldBe(actor == "service" ? "org-42" : null);
        await host.AssertPersistedAsync(record);
        AssertPrivate(JsonSerializer.Serialize(record), host);
        foreach (var log in host.Logs.Messages)
            AssertPrivate(log, host);
    }

    [Theory]
    [Trait("PendingImplementation", "T068-T070")]
    [InlineData("throw")]
    [InlineData("wrong-event")]
    [InlineData("missing-time")]
    [InlineData("non-utc")]
    public async Task Driver_identity_never_escapes_if_audit_cannot_prove_commit(string mode)
    {
        await using var host = await Host.StartAsync(database, production: true);
        host.Audit.Mode = mode;
        using var response = await host.GetAsync(Query(DriverRoute), "service");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("AUDIT_UNAVAILABLE");
        AssertPrivate(body, host);
    }

    [Fact]
    [Trait("PendingImplementation", "T068-T070")]
    public async Task Driver_response_waits_for_durable_audit_commit()
    {
        await using var host = await Host.StartAsync(database, production: true);
        host.Audit.Mode = "barrier";
        var pending = host.GetAsync(Query(DriverRoute), "human");
        try
        {
            await host.Audit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pending.IsCompleted.ShouldBeFalse();
        }
        finally { host.Audit.Release.TrySetResult(); }
        using var response = await pending;
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await host.AssertPersistedAsync(host.Audit.Records.Single(r => r.Outcome == AuditOutcome.Allowed));
    }

    [Fact]
    [Trait("PendingImplementation", "T068-T070")]
    public async Task Human_cross_company_identity_read_requires_both_permissions_and_audits_the_expansion()
    {
        await using var host = await Host.StartAsync(database, production: true, cross: true);
        using var response = await host.GetAsync(Query(DriverRoute, OtherCompany), "human");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Driver.Scopes.ShouldBe(new[] { OtherCompany });
        host.Audit.Records.ShouldContain(r => r.Action == AuditAction.ReadAcrossCompanies && r.Outcome == AuditOutcome.Allowed);
        host.Audit.Records.ShouldContain(r => r.Action == AuditAction.ResolveDriver && r.Outcome == AuditOutcome.Allowed);
        foreach (var record in host.Audit.Records)
        {
            record.Actor.ActorId.ShouldBe("human-account");
            record.CompanyScope.Select(c => c.Value).ShouldContain(OtherCompany);
            await host.AssertPersistedAsync(record);
            AssertPrivate(JsonSerializer.Serialize(record), host);
        }
    }

    private static string Query(string route, string company = Company, string? extra = null)
    {
        var time = route == Trip || route == Health;
        return route + "?empresaId=" + company + (time ? "&fromUtc=" + Start + (extra?.StartsWith("toUtc=", StringComparison.Ordinal) == true ? "" : "&toUtc=2026-10-01T00:01:00Z") : "")
            + (extra is null ? "" : "&" + extra);
    }

    private static void AssertPrivate(string text, Host host)
    {
        foreach (var secret in new[] { Driver, Name, "11122231234", "ABC1D23", "-23.55052", "-46.633308", "private backend diagnostic", "raw-line-secret" }.Concat(host.Tokens.Values))
            text.ShouldNotContain(secret);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly PresentationAuthorizationDatabase database;
        public HttpClient Client { get; private set; } = null!;
        public Dictionary<string, string> Tokens { get; } = new();
        public AuditProbe Audit
        {
            get;
        }
        public DriverProbe Driver { get; } = new();
        public BackendProbe Backend { get; } = new();
        public LogCapture Logs { get; } = new();
        public int Executions
        {
            get; private set;
        }

        private Host(WebApplication app, PresentationAuthorizationDatabase database, AuditProbe audit, Dictionary<string, string> tokens, DriverProbe driver, BackendProbe backend, LogCapture logs)
        {
            this.app = app;
            this.database = database;
            Audit = audit;
            Tokens = tokens;
            Driver = driver;
            Backend = backend;
            Logs = logs;
        }

        public static async Task<Host> StartAsync(PresentationAuthorizationDatabase database, bool production = false,
            string? option = null, string? value = null, bool cross = false)
        {
            // Fail at the absent production composition seam, never map dummy data routes.
            var registration = production ? ProductionMethod("AddTelemetryPresentation", typeof(IServiceCollection), typeof(IConfiguration)) : null;
            var mapping = production ? ProductionMethod("MapPresentationEndpoints", typeof(IEndpointRouteBuilder)) : null;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            var logs = new LogCapture();
            builder.Logging.AddProvider(logs);
            var settings = new Dictionary<string, string?>();
            var tokens = new Dictionary<string, string>();
            foreach (var (actor, kind, section, id) in new[] {
                ("human", BearerCredentialKind.HumanOperator, "Operators", "human-account"),
                ("service", BearerCredentialKind.ViewerPresentationService, "ViewerServices", "viewer-account"),
                ("producer", BearerCredentialKind.Producer, "Producers", "shared-producer") })
            {
                tokens[actor] = OpaqueBearerTokens.Generate(kind);
                var hash = BearerTokenHash.Create(tokens[actor]);
                var prefix = $"Authentication:{section}:0:";
                settings[prefix + "CredentialId"] = actor + "-credential";
                settings[prefix + (actor == "producer" ? "ProducerId" : "ActorId")] = id;
                settings[prefix + "Hash:LookupDigest"] = hash.LookupDigest;
                settings[prefix + "Hash:Salt"] = hash.Salt;
                settings[prefix + "Hash:Verifier"] = hash.Verifier;
                settings[prefix + "Hash:Iterations"] = hash.Iterations.ToString(CultureInfo.InvariantCulture);
                if (actor == "producer")
                    continue;
                var profile = $"Authentication:Profiles:{(actor == "human" ? 0 : 1)}:";
                settings[profile + "ActorId"] = id;
                settings[profile + "Kind"] = actor == "human" ? "Human" : "ViewerPresentationService";
                settings[profile + "CompanyIds:0"] = Company;
                settings[profile + "Permissions:0"] = "telemetry.read";
                settings[profile + "Permissions:1"] = "driver.read";
                if (cross && actor == "human")
                    settings[profile + "Permissions:2"] = "cross_company.read";
                if (actor == "service")
                {
                    settings[prefix + "GrafanaOrganizationId"] = "org-42";
                    settings[profile + "GrafanaOrganizationId"] = "org-42";
                }
            }
            if (option is not null)
                settings["Telemetry:Viewer:" + option] = value;
            settings["InfluxDb:Queryable:Url"] = "https://influx.invalid";
            settings["InfluxDb:Queryable:Database"] = "telemetry";
            settings["InfluxDb:Queryable:Token"] = "synthetic-private-influx-token";
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddTelemetryOptions(builder.Configuration);
            builder.Services.AddTelemetryControlPersistence(database.Connection);
            builder.Services.AddTelemetrySecurity(builder.Configuration);
            var driver = new DriverProbe();
            var backend = new BackendProbe();
            builder.Services.AddSingleton<IDriverResolver>(driver);
            builder.Services.AddSingleton(new HttpClient(backend));
            builder.Services.AddSingleton<IHttpClientFactory>(new ClientFactory(backend));
            registration?.Invoke(null, [builder.Services, builder.Configuration]);
            builder.Services.AddSingleton<IAuditWriter>(services => new AuditProbe(new PostgresAuditWriter(services.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>())));
            var app = builder.Build();
            try
            {
                app.UseAuthentication();
                app.UseAuthorization();
                var host = new Host(app, database, (AuditProbe)app.Services.GetRequiredService<IAuditWriter>(), tokens, driver, backend, logs);
                app.MapGet("/_policy/driver/{empresaId}", () => { host.Executions++; return "authorized"; }).RequireAuthorization(OperatorAuthorizationPolicies.DriverRead);
                app.MapGet("/_policy/telemetry/{empresaId}", () => { host.Executions++; return "authorized"; }).RequireAuthorization(OperatorAuthorizationPolicies.TelemetryRead);
                mapping?.Invoke(null, [app]);
                await app.StartAsync();
                host.Client = app.GetTestClient();
                return host;
            }
            catch { await app.DisposeAsync(); throw; }
        }

        private static MethodInfo ProductionMethod(string name, params Type[] arguments)
        {
            var assemblies = new[] { typeof(SecurityRegistration).Assembly, typeof(TelemetryOptions).Assembly, typeof(PostgresAuditWriter).Assembly };
            var methods = assemblies.SelectMany(a => a.GetTypes()).SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.Name == name && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(arguments)).ToArray();
            methods.Length.ShouldBe(1, $"T069/T070: missing production {name} composition; wire the real registration/routes, never substitute test handlers.");
            return methods[0];
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string actor)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (actor != "missing")
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.GetValueOrDefault(actor, "invalid-token"));
            request.Headers.TryAddWithoutValidation("X-Grafana-Org-Id", "spoofed-org");
            request.Headers.TryAddWithoutValidation("X-Actor-Id", "spoofed-account");
            request.Headers.TryAddWithoutValidation("X-Empresa-Id", OtherCompany);
            return await Client.SendAsync(request);
        }

        public async Task AssertPersistedAsync(AuditRecord record)
        {
            await using var db = database.Create();
            var stored = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.EventId == record.EventId);
            stored.ActorId.ShouldBe(record.Actor.ActorId);
            stored.ActorKind.ShouldBe(record.Actor.Kind);
            stored.GrafanaOrganizationId.ShouldBe(record.Actor.GrafanaOrganizationId);
            stored.Action.ShouldBe(record.Action);
            stored.Outcome.ShouldBe(record.Outcome);
            stored.CompanyScope.ShouldBe(record.CompanyScope.Select(c => c.Value).Distinct().Order().ToArray());
            stored.CommittedAt.ShouldNotBe(default);
            stored.TraceId.ShouldBe(record.TraceId);
            AssertPrivate(JsonSerializer.Serialize(stored), this);
        }

        public async ValueTask DisposeAsync()
        {
            Audit.Release.TrySetResult();
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class AuditProbe(IAuditWriter durable) : IAuditWriter
    {
        public string Mode { get; set; } = "normal";
        public ConcurrentQueue<AuditRecord> Records { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<AuditReceipt> AppendAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            Records.Enqueue(record);
            Entered.TrySetResult();
            if (Mode == "throw")
                throw new IOException("private backend diagnostic");
            if (Mode == "wrong-event")
                return new(Guid.NewGuid(), DateTimeOffset.UtcNow);
            if (Mode == "missing-time")
                return new(record.EventId, default);
            if (Mode == "non-utc")
                return new(record.EventId, DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(1)));
            if (Mode == "barrier")
                await Release.Task.WaitAsync(cancellationToken);
            return await durable.AppendAsync(record, cancellationToken);
        }
    }

    private sealed class DriverProbe : IDriverResolver
    {
        public int Calls => Scopes.Count;
        public List<string> Scopes { get; } = new();
        public List<string> Drivers { get; } = new();
        public ValueTask<DriverIdentityPresentation?> ResolveAsync(CompanyId companyId, DriverId driverId, CancellationToken cancellationToken)
        {
            Scopes.Add(companyId.Value);
            Drivers.Add(driverId.Value);
            return ValueTask.FromResult<DriverIdentityPresentation?>(new(driverId, Name, "1234"));
        }
    }

    private sealed class BackendProbe : HttpMessageHandler
    {
        public int Calls
        {
            get; private set;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") });
        }
    }
    private sealed class ClientFactory(BackendProbe backend) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(backend, disposeHandler: false);
    }
    private sealed class ProfileStore(OperatorAccessProfile profile) : IAuthenticationCredentialStore
    {
        public ValueTask<ProducerCredential?> FindProducerAsync(string lookupDigest, CancellationToken cancellationToken) => ValueTask.FromResult<ProducerCredential?>(null);
        public ValueTask<OperatorBearerCredential?> FindOperatorAsync(string lookupDigest, BearerCredentialKind kind, CancellationToken cancellationToken) => ValueTask.FromResult<OperatorBearerCredential?>(null);
        public ValueTask<OperatorAccessProfile?> GetProfileAsync(string actorId, CancellationToken cancellationToken) => ValueTask.FromResult<OperatorAccessProfile?>(profile);
    }
    private sealed class LogCapture : ILoggerProvider
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
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}

public sealed class PresentationAuthorizationDatabase : IAsyncLifetime
{
    private MigratedPostgresFixture fixture = null!;
    internal string Connection => fixture.Connection;
    internal TelemetryControlDbContext Create() => fixture.Create();
    public async Task InitializeAsync() => fixture = await MigratedPostgresFixture.StartAsync();
    public async Task DisposeAsync() => await fixture.DisposeAsync();
}
