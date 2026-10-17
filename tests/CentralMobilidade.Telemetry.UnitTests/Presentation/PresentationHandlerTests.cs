using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.Presentation;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Api.V1;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Presentation;

public sealed class PresentationHandlerTests
{
    [Theory]
    [InlineData(OperatorAuthorizationDecision.Denied, false)]
    [InlineData(OperatorAuthorizationDecision.Allowed, true)]
    public async Task Authorizer_denial_or_failure_prevents_disclosure(OperatorAuthorizationDecision decision, bool fail)
    {
        var f = new Fixture { Decision = decision, FailAuthorization = fail };
        (await f.Latest.HandleAsync(f.Context, "company", null, null, null, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.ResourceNotFound);
        f.Reads.ShouldBe(0);
        f.Records.Single().Outcome.ShouldBe(AuditOutcome.Denied);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("disabled")]
    [InlineData("kind")]
    [InlineData("permission-case")]
    [InlineData("company-case")]
    public async Task Trusted_profile_must_match_actor_permission_and_exact_scope(string fault)
    {
        var f = new Fixture();
        f.Profile = fault switch
        {
            "actor" => f.Profile with { ActorId = "someone-else" },
            "disabled" => f.Profile with { Enabled = false },
            "kind" => f.Profile with { Kind = OperatorActorKind.ViewerPresentationService },
            "permission-case" => f.Profile with { Permissions = ["Telemetry.Read"] },
            _ => f.Profile with { CompanyIds = ["Company"] }
        };
        (await f.Latest.HandleAsync(f.Context, "company", null, null, null, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.ResourceNotFound);
        f.Reads.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_commit_time_in_audit_receipt_blocks_sensitive_disclosure(bool nonUtc)
    {
        var f = new Fixture { ReceiptTime = nonUtc ? DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(1)) : default(DateTimeOffset) };
        var error = await Should.ThrowAsync<ProviderOperationException>(async () => await f.Driver.HandleAsync(f.Context, "company", "driver", default));
        error.ErrorCode.ShouldBe(TelemetryErrorCodes.AuditUnavailable);
        error.Kind.ShouldBe(ProviderFailureKind.Transient);
        f.Resolves.ShouldBe(1);
        f.Records.Single().Action.ShouldBe(AuditAction.ResolveDriver);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Latest_rejects_limits_without_reading(int limit)
    {
        var f = new Fixture();
        (await f.Latest.HandleAsync(f.Context, "company", null, limit, null, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
        f.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task Latest_has_stable_continuation_and_one_row_lookahead()
    {
        var f = new Fixture();
        f.Positions = [new("company", "a", f.UtcNow, 1, 2), new("company", "b", f.UtcNow, 3, 4)];
        var first = (await f.Latest.HandleAsync(f.Context, "company", null, 1, null, default)).Value.ShouldNotBeNull();
        first.Items.Single().VehicleId.ShouldBe("a");
        first.NextCursor.ShouldNotBeNull();
        await f.Latest.HandleAsync(f.Context, "company", null, 1, first.NextCursor, default);
        f.After.ShouldBe("a");
        f.Limit.ShouldBe(2);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("missing")]
    public async Task Out_of_scope_is_nondisclosing_and_does_not_query(string company)
    {
        var f = new Fixture();
        (await f.Latest.HandleAsync(f.Context, company, null, null, null, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.ResourceNotFound);
        f.Reads.ShouldBe(0);
        f.Records.Single().Outcome.ShouldBe(AuditOutcome.Denied);
        f.Records.Single().CompanyScope.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cross_company_permission_does_not_grant_telemetry_or_identity_permission()
    {
        var f = new Fixture { Profile = new("operator", OperatorActorKind.Human, ["cross_company.read"], []) };
        (await f.Latest.HandleAsync(f.Context, "other", null, null, null, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.ResourceNotFound);
        (await f.Driver.HandleAsync(f.Context, "other", "driver", default)).ErrorCode.ShouldBe(TelemetryErrorCodes.AccessDenied);
        f.Reads.ShouldBe(0);
        f.Resolves.ShouldBe(0);
    }

    [Fact]
    public async Task Viewer_cannot_elevate_using_cross_company_permission()
    {
        var f = new Fixture { Profile = new("operator", OperatorActorKind.ViewerPresentationService, ["telemetry.read", "cross_company.read"], ["company"], true, "org") };
        var context = new PresentationContext(new("operator", OperatorActorKind.ViewerPresentationService, "org"), "trace");
        (await f.Latest.HandleAsync(context, "other", null, null, null, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.ResourceNotFound);
        f.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task Cross_company_audit_failure_prevents_query()
    {
        var f = new Fixture { Profile = new("operator", OperatorActorKind.Human, ["telemetry.read", "cross_company.read"], []), FailAudit = true };
        var error = await Should.ThrowAsync<ProviderOperationException>(async () => await f.Latest.HandleAsync(f.Context, "other", null, null, null, default));
        error.ErrorCode.ShouldBe(TelemetryErrorCodes.AuditUnavailable);
        f.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task Revocation_during_query_prevents_disclosure()
    {
        var f = new Fixture();
        f.OnRead = () => f.Profile = f.Profile with { Enabled = false };
        (await f.Latest.HandleAsync(f.Context, "company", null, null, null, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task Identity_is_held_until_durable_audit_completes()
    {
        var f = new Fixture();
        var barrier = new TaskCompletionSource<AuditReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.AuditBarrier = barrier;
        var task = f.Driver.HandleAsync(f.Context, "company", "driver", default).AsTask();
        task.IsCompleted.ShouldBeFalse();
        var record = f.Records.Single();
        record.OpaqueResourceId.ShouldBeNull();
        record.ToString().ShouldNotContain("Sensitive name");
        barrier.SetResult(new(record.EventId, f.UtcNow));
        (await task).Value.ShouldNotBeNull().DisplayName.ShouldBe("Sensitive name");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_or_mismatched_audit_receipt_blocks_identity(bool fail)
    {
        var f = new Fixture { FailAudit = fail, WrongReceipt = !fail };
        await Should.ThrowAsync<ProviderOperationException>(async () => await f.Driver.HandleAsync(f.Context, "company", "driver", default));
    }

    [Fact]
    public async Task Revocation_during_audit_prevents_identity_disclosure()
    {
        var f = new Fixture();
        f.OnAudit = () => f.Profile = f.Profile with { Enabled = false };
        (await f.Driver.HandleAsync(f.Context, "company", "driver", default)).ErrorCode.ShouldBe(TelemetryErrorCodes.AccessDenied);
    }

    [Fact]
    public async Task Newly_cross_company_scope_is_audited_after_lookup()
    {
        var f = new Fixture { Profile = new("operator", OperatorActorKind.Human, ["driver.read", "cross_company.read"], ["company"]) };
        f.OnResolve = () => f.Profile = f.Profile with { CompanyIds = [] };
        (await f.Driver.HandleAsync(f.Context, "company", "driver", default)).Value.ShouldNotBeNull();
        f.Records.ShouldContain(record => record.Action == AuditAction.ReadAcrossCompanies);
    }

    [Theory]
    [InlineData("12345678900")]
    [InlineData("ABCD")]
    public async Task Resolver_cannot_leak_full_or_malformed_CPF(string cpf)
    {
        var f = new Fixture { Cpf = cpf };
        (await f.Driver.HandleAsync(f.Context, "company", "driver", default)).ErrorCode.ShouldBe(TelemetryErrorCodes.ResourceNotFound);
        f.Records.Single().Outcome.ShouldBe(AuditOutcome.Missing);
    }

    [Fact]
    public async Task Invalid_windows_and_granularity_do_not_query()
    {
        var f = new Fixture();
        (await f.Trip.HandleAsync(f.Context, "company", "vehicle", f.UtcNow, f.UtcNow.AddHours(24).AddTicks(1), null, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
        (await f.Health.HandleAsync(f.Context, "company", f.UtcNow, f.UtcNow.AddDays(7), 59, null, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
        (await f.Health.HandleAsync(f.Context, "company", f.UtcNow, f.UtcNow.AddDays(7).AddTicks(1), 60, null, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
        f.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task Configured_lower_limits_apply_to_all_queries()
    {
        var f = new Fixture();
        f.Options.CurrentValue.Viewer.LatestPositionMaxVehicles = 2;
        f.Options.CurrentValue.Viewer.TripMaxPoints = 2;
        f.Options.CurrentValue.Viewer.HealthMaxPoints = 2;
        (await f.Latest.HandleAsync(f.Context, "company", null, 3, null, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
        (await f.Trip.HandleAsync(f.Context, "company", "vehicle", f.UtcNow, f.UtcNow.AddHours(1), 3, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
        (await f.Health.HandleAsync(f.Context, "company", f.UtcNow, f.UtcNow.AddHours(1), 60, 3, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        var f = new Fixture();
        await Should.ThrowAsync<OperationCanceledException>(async () => await f.Driver.HandleAsync(f.Context, "company", "driver", new CancellationToken(true)));
    }

    [Fact]
    public async Task A_misbehaving_query_adapter_cannot_disclose_another_company()
    {
        var f = new Fixture { Positions = [new("other", "vehicle", DateTimeOffset.UtcNow, 1, 2)] };
        await Should.ThrowAsync<ProviderOperationException>(async () => await f.Latest.HandleAsync(f.Context, "company", null, null, null, default));
        await Should.ThrowAsync<ProviderOperationException>(async () => await f.Trip.HandleAsync(f.Context, "company", "vehicle", f.UtcNow, f.UtcNow.AddHours(1), null, default));
    }

    [Fact]
    public async Task Exact_maximum_windows_and_default_limits_are_accepted()
    {
        var f = new Fixture();
        (await f.Trip.HandleAsync(f.Context, "company", "vehicle", f.UtcNow, f.UtcNow.AddHours(24), null, default)).ErrorCode.ShouldBeNull();
        f.Limit.ShouldBe(10000);
        (await f.Health.HandleAsync(f.Context, "company", f.UtcNow, f.UtcNow.AddDays(7), null, null, default)).ErrorCode.ShouldBeNull();
        (await f.Latest.HandleAsync(f.Context, "company", null, null, null, default)).ErrorCode.ShouldBeNull();
        f.Limit.ShouldBe(501);
    }

    [Theory]
    [InlineData("!")]
    [InlineData("AA==")]
    [InlineData("")]
    public async Task Invalid_cursor_fails_before_IO(string cursor)
    {
        var f = new Fixture();
        (await f.Latest.HandleAsync(f.Context, "company", null, null, cursor, default)).ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
        f.Reads.ShouldBe(0);
    }

    private sealed class Fixture : IOperatorAuthorizer, IAuthenticationCredentialStore, IAuditWriter, IClock, ITelemetryPresentationReader, IIngestionHealthReader, IDriverResolver
    {
        public OperatorAccessProfile Profile = new("operator", OperatorActorKind.Human, ["telemetry.read", "driver.read"], ["company"]);
        public PresentationContext Context { get; } = new(new("operator", OperatorActorKind.Human), "trace");
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        public IOptionsMonitor<TelemetryOptions> Options { get; } = new ServiceCollection().AddOptions().Configure<TelemetryOptions>(_ => { }).BuildServiceProvider().GetRequiredService<IOptionsMonitor<TelemetryOptions>>();
        public List<AuditRecord> Records = [];
        public IReadOnlyList<TelemetryPosition> Positions = [];
        public int Reads, Resolves, Limit;
        public string? After;
        public string? Cpf = "1234";
        public bool FailAudit, WrongReceipt;
        public bool FailAuthorization;
        public OperatorAuthorizationDecision Decision = OperatorAuthorizationDecision.Allowed;
        public DateTimeOffset? ReceiptTime;
        public Action? OnRead, OnAudit, OnResolve;
        public TaskCompletionSource<AuditReceipt>? AuditBarrier;
        public PresentationAccess Access => new(this, this, this, this);
        public LatestPositionHandler Latest => new(Access, this, Options);
        public VehicleTripHandler Trip => new(Access, this, Options);
        public IngestionHealthHandler Health => new(Access, this, Options);
        public DriverPresentationHandler Driver => new(Access, this);
        public ValueTask<OperatorAuthorizationDecision> AuthorizeAsync(OperatorActor actor, OperatorPermission permission, IReadOnlyCollection<CompanyId> companies, CancellationToken token)
            => FailAuthorization ? throw new InvalidOperationException("private authorization provider detail") : ValueTask.FromResult(Decision);
        public ValueTask<OperatorAccessProfile?> GetProfileAsync(string actorId, CancellationToken token) => ValueTask.FromResult<OperatorAccessProfile?>(Profile);
        public ValueTask<ProducerCredential?> FindProducerAsync(string digest, CancellationToken token) => ValueTask.FromResult<ProducerCredential?>(null);
        public ValueTask<OperatorBearerCredential?> FindOperatorAsync(string digest, BearerCredentialKind kind, CancellationToken token) => ValueTask.FromResult<OperatorBearerCredential?>(null);
        public ValueTask<AuditReceipt> AppendAsync(AuditRecord record, CancellationToken token)
        {
            Records.Add(record);
            OnAudit?.Invoke();
            if (FailAudit)
                throw new InvalidOperationException("private provider details");
            return AuditBarrier is null ? ValueTask.FromResult(new AuditReceipt(WrongReceipt ? Guid.NewGuid() : record.EventId, ReceiptTime ?? UtcNow)) : new(AuditBarrier.Task);
        }
        public ValueTask<IReadOnlyList<TelemetryPosition>> LatestAsync(CompanyId company, VehicleId? vehicle, string? after, int limit, CancellationToken token)
        {
            Reads++;
            After = after;
            Limit = limit;
            OnRead?.Invoke();
            return ValueTask.FromResult<IReadOnlyList<TelemetryPosition>>(Positions.Where(row => after is null || string.CompareOrdinal(row.VehicleId, after) > 0).ToArray());
        }
        public ValueTask<IReadOnlyList<TelemetryPosition>> TripAsync(CompanyId company, VehicleId vehicle, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken token)
            => LatestAsync(company, vehicle, null, limit, token);
        public ValueTask<IReadOnlyList<IngestionHealthPoint>> ReadAsync(CompanyId company, DateTimeOffset from, DateTimeOffset to, int granularity, int limit, CancellationToken token)
        {
            Reads++;
            return ValueTask.FromResult<IReadOnlyList<IngestionHealthPoint>>([]);
        }
        public ValueTask<DriverIdentityPresentation?> ResolveAsync(CompanyId company, DriverId driver, CancellationToken token)
        {
            Resolves++;
            OnResolve?.Invoke();
            return ValueTask.FromResult<DriverIdentityPresentation?>(new(driver, "Sensitive name", Cpf));
        }
    }
}
