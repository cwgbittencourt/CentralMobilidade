using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Persistence;

public sealed class PresentationAuditPolicyTests(PresentationAuditDatabase database) : IClassFixture<PresentationAuditDatabase>
{
    public static IEnumerable<object[]> IdentityOutcomes()
    {
        foreach (var actor in new[] { OperatorActorKind.Human, OperatorActorKind.ViewerPresentationService })
            foreach (var outcome in Enum.GetValues<AuditOutcome>())
                yield return [actor, outcome];
    }

    [Theory]
    [MemberData(nameof(IdentityOutcomes))]
    public async Task Identity_outcomes_commit_minimal_metadata_with_trusted_actor_and_organization(OperatorActorKind kind, AuditOutcome outcome)
    {
        var actor = kind == OperatorActorKind.Human ? new OperatorActor("operator-account", kind) : new("viewer-account", kind, "org-42");
        var record = Record(actor, AuditAction.ResolveDriver, AuditResourceCategory.Driver, outcome);
        var receipt = await database.Writer.AppendAsync(record, default);
        receipt.EventId.ShouldBe(record.EventId);
        receipt.CommittedAt.Offset.ShouldBe(TimeSpan.Zero);
        receipt.CommittedAt.ShouldNotBe(default);
        await using var db = database.Create();
        var stored = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.EventId == record.EventId);
        stored.ActorId.ShouldBe(actor.ActorId);
        stored.ActorKind.ShouldBe(kind);
        stored.GrafanaOrganizationId.ShouldBe(actor.GrafanaOrganizationId);
        stored.Action.ShouldBe(AuditAction.ResolveDriver);
        stored.ResourceCategory.ShouldBe(AuditResourceCategory.Driver);
        stored.Outcome.ShouldBe(outcome);
        stored.CompanyScope.ShouldBe(new[] { "company-a" });
        stored.OpaqueResourceId.ShouldBe(record.OpaqueResourceId);
        stored.CommittedAt.ShouldBe(receipt.CommittedAt);
        var json = JsonSerializer.Serialize(stored);
        foreach (var forbidden in new[] { "displayName", "cpf", "latitude", "longitude", "plate", "token", "content", "PRIVATE_DRIVER_NAME", "11122233344" })
            json.Contains(forbidden, StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    [Theory]
    [InlineData(AuditResourceCategory.Driver)]
    [InlineData(AuditResourceCategory.Telemetry)]
    [InlineData(AuditResourceCategory.Batch)]
    [InlineData(AuditResourceCategory.Quarantine)]
    public async Task Explicit_cross_company_reads_commit_normalized_scope_and_idempotent_concurrent_receipts(AuditResourceCategory category)
    {
        var record = Record(new("cross-company-operator", OperatorActorKind.Human), AuditAction.ReadAcrossCompanies, category, AuditOutcome.Allowed)
            with
        {
            CompanyScope = [new("company-b"), new("company-a"), new("company-b")]
        };
        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.Writer.AppendAsync(record, default).AsTask()));
        receipts.Distinct().Count().ShouldBe(1);
        var replay = record with
        {
            CompanyScope = [new("company-a"), new("company-b")]
        };
        (await database.Writer.AppendAsync(replay, default)).ShouldBe(receipts[0]);
        await using var db = database.Create();
        (await db.AuditEvents.CountAsync(e => e.EventId == record.EventId)).ShouldBe(1);
        var stored = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.EventId == record.EventId);
        stored.CompanyScope.ShouldBe(new[] { "company-a", "company-b" });
        stored.ResourceCategory.ShouldBe(category);
        var collision = await Should.ThrowAsync<InvalidOperationException>(() => database.Writer.AppendAsync(record with { CompanyScope = [new("different-company")] }, default).AsTask());
        collision.Message.ShouldNotContain("different-company");
    }

    [Theory]
    [InlineData(AuditAction.ReadAcrossCompanies, AuditResourceCategory.Telemetry)]
    [InlineData(AuditAction.ReadQuarantineContent, AuditResourceCategory.Quarantine)]
    public async Task Forbidden_viewer_attempts_are_still_auditable_without_authorizing_them(AuditAction action, AuditResourceCategory category)
    {
        var record = Record(new("viewer-account", OperatorActorKind.ViewerPresentationService, "org-42"), action, category, AuditOutcome.Denied)
            with
        {
            CompanyScope = []
        };
        var receipt = await database.Writer.AppendAsync(record, default);
        await using var db = database.Create();
        (await db.AuditEvents.AsNoTracking().SingleAsync(e => e.EventId == receipt.EventId)).Outcome.ShouldBe(AuditOutcome.Denied);
    }

    [Fact]
    public async Task Identity_events_reject_changed_attribution_and_database_mutation()
    {
        var record = Record(new("viewer-account", OperatorActorKind.ViewerPresentationService, "org-42"), AuditAction.ResolveDriver, AuditResourceCategory.Driver, AuditOutcome.Allowed);
        await database.Writer.AppendAsync(record, default);
        await Should.ThrowAsync<InvalidOperationException>(() => database.Writer.AppendAsync(record with { Actor = record.Actor with { GrafanaOrganizationId = "other-org" } }, default).AsTask());
        await using var db = database.Create();
        foreach (var sql in new[] { "UPDATE audit_events SET \"TraceId\" = 'changed'", "DELETE FROM audit_events", "TRUNCATE audit_events" })
            (await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState.ShouldBe("42501");
        (await db.AuditEvents.AsNoTracking().SingleAsync(e => e.EventId == record.EventId)).GrafanaOrganizationId.ShouldBe("org-42");
    }

    private static AuditRecord Record(OperatorActor actor, AuditAction action, AuditResourceCategory category, AuditOutcome outcome) =>
        new(Guid.NewGuid(), actor, action, category, Guid.NewGuid().ToString("D"), [new("company-a")], DateTimeOffset.UtcNow, outcome, "audit-policy-trace");
}

public sealed class PresentationAuditDatabase : IAsyncLifetime
{
    private MigratedPostgresFixture fixture = null!;
    private ServiceProvider services = null!;
    internal IAuditWriter Writer => services.GetRequiredService<IAuditWriter>();
    internal TelemetryControlDbContext Create() => fixture.Create();
    public async Task InitializeAsync()
    {
        fixture = await MigratedPostgresFixture.StartAsync();
        services = new ServiceCollection().AddTelemetryControlPersistence(fixture.Connection).BuildServiceProvider();
    }
    public async Task DisposeAsync()
    {
        if (services is not null)
            await services.DisposeAsync();
        if (fixture is not null)
            await fixture.DisposeAsync();
    }
}
