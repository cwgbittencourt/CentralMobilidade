using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Infrastructure.Auditing;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.ContractTests.Auditing;

public sealed class AuditPolicyContractTests
{
    public static IEnumerable<object[]> InvalidRecords()
    {
        var record = Valid();
        yield return [record with { Action = AuditAction.ResolveDriver, ResourceCategory = AuditResourceCategory.Telemetry }];
        yield return [record with { CompanyScope = [] }];
        yield return [record with { CompanyScope = null! }];
        yield return [record with { CompanyScope = [null!] }];
        yield return [record with { OpaqueResourceId = Guid.Empty.ToString("D") }];
        yield return [record with { CompanyScope = [new("a"), new("b")] }];
        yield return [record with { Actor = new("viewer", OperatorActorKind.ViewerPresentationService) }];
        yield return [record with { Actor = new("human", OperatorActorKind.Human, "spoofed-org") }];
        yield return [record with { Actor = new("unauthenticated", OperatorActorKind.Unidentified) }];
        yield return [record with { Actor = new("unauthenticated", OperatorActorKind.Unidentified), Outcome = AuditOutcome.Denied }];
        yield return [record with { Action = AuditAction.ReadAcrossCompanies, Actor = new("viewer", OperatorActorKind.ViewerPresentationService, "org-42") }];
        yield return [record with { Action = AuditAction.ReadAcrossCompanies, CompanyScope = [] }];
        yield return [record with { Actor = new("human\nprivate", OperatorActorKind.Human) }];
        yield return [record with { TraceId = "trace\nprivate" }];
        yield return [record with { OccurredAt = default }];
        foreach (var secret in new[] { "PRIVATE DRIVER NAME", "11122233344", "ABC1D23", "-23.55052,-46.633308", "Bearer private-token", "telemetria latitude=1.0", "raw-quarantine-content" })
            yield return [record with { OpaqueResourceId = secret }];
    }

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public async Task Unsafe_or_inconsistent_metadata_is_rejected_before_database_access(AuditRecord record)
    {
        var factory = new NoDatabase();
        var writer = new PostgresAuditWriter(factory);
        var exception = await Should.ThrowAsync<ArgumentException>(() => writer.AppendAsync(record, default).AsTask());
        exception.Message.ShouldBe("Invalid audit metadata. (Parameter 'record')");
        factory.Calls.ShouldBe(0);
    }

    [Fact]
    public void Diagnostic_record_format_does_not_include_arbitrary_metadata()
    {
        var record = Valid() with
        {
            OpaqueResourceId = "PRIVATE DRIVER NAME",
            TraceId = "Bearer private-token"
        };
        record.ToString().ShouldBe(nameof(AuditRecord));
    }

    internal static AuditRecord Valid() => new(Guid.NewGuid(), new("human", OperatorActorKind.Human),
        AuditAction.ResolveDriver, AuditResourceCategory.Driver, Guid.NewGuid().ToString("D"),
        [new("company")], DateTimeOffset.UtcNow, AuditOutcome.Allowed, "trace");
    private sealed class NoDatabase : IDbContextFactory<TelemetryControlDbContext>
    {
        public int Calls
        {
            get; private set;
        }
        public TelemetryControlDbContext CreateDbContext()
        {
            Calls++;
            throw new InvalidOperationException("Database must not be consulted for invalid metadata.");
        }
        public Task<TelemetryControlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
