using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Api.Security;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Batches;
using CentralMobilidade.Telemetry.Application.Security;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Auditing;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.Infrastructure.Quarantine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Batches;

[Trait("Category", "Integration")]
public sealed partial class BatchInspectionTests(BatchLifecycleDatabase database) : IClassFixture<BatchLifecycleDatabase>
{
    [Theory]
    [InlineData("producer", true)]
    [InlineData("other-producer", false)]
    [InlineData("operator", true)]
    [InlineData("scoped", false)]
    [InlineData("cross", true)]
    [InlineData("noperm", false)]
    [InlineData("viewer", false)]
    [InlineData("disabled", false)]
    public async Task Visibility_uses_producer_ownership_or_current_operator_permissions_and_all_companies(string name, bool visible)
    {
        await using var test = Create();
        var batch = await test.Seed();
        ((await test.Visibility.GetVisibleAsync(batch.Id, Reader(name), default)) is not null).ShouldBe(visible);
    }

    [Fact]
    public async Task Unknown_company_scope_requires_explicit_cross_company_permission()
    {
        await using var test = Create();
        var batch = await test.Seed(unknownCompany: true);
        (await test.Visibility.GetVisibleAsync(batch.Id, Reader("operator"), default)).ShouldBeNull();
        (await test.Visibility.GetVisibleAsync(batch.Id, Reader("cross"), default)).ShouldNotBeNull();
        (await test.Visibility.GetVisibleAsync(batch.Id, Reader("producer"), default)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Expired_missing_and_unauthorized_batches_have_the_same_non_disclosing_result()
    {
        await using var test = Create();
        var expired = await test.Seed(ageDays: 31);
        var fresh = await test.Seed();
        (await test.Visibility.GetVisibleAsync(expired.Id, Reader("producer"), default)).ShouldBeNull();
        (await test.Visibility.GetVisibleAsync(new(Guid.NewGuid()), Reader("operator"), default)).ShouldBeNull();
        (await test.Visibility.GetVisibleAsync(fresh.Id, Reader("scoped"), default)).ShouldBeNull();
    }

    [Fact]
    public async Task Keyset_paging_returns_only_rejections_with_no_duplicates_or_protected_metadata()
    {
        await using var test = Create();
        var batch = await test.Seed();
        var seen = new List<int>();
        string? cursor = null;
        do
        {
            var page = (await test.List.HandleAsync(batch.Id, Reader("operator"), cursor, 2, default)).ShouldNotBeNull();
            page.Items.Count.ShouldBeLessThanOrEqualTo(2);
            page.Items.ShouldAllBe(item => item.Outcome == LineOutcome.Rejected && item.ProtectedContent == null);
            seen.AddRange(page.Items.Select(item => item.LineNumber));
            cursor = page.NextCursor;
        } while (cursor is not null);
        seen.ShouldBe(new[] { 1, 3, 4, 6, 8 });
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("different-batch")]
    [InlineData("expired")]
    [InlineData("oversized")]
    public async Task Invalid_or_out_of_context_cursor_fails_without_returning_a_page(string fault)
    {
        await using var test = Create();
        var batch = await test.Seed();
        var page = (await test.List.HandleAsync(batch.Id, Reader("operator"), null, 2, default))!;
        var cursor = page.NextCursor!;
        if (fault == "tamper")
            cursor = (cursor[0] == 'A' ? "B" : "A") + cursor[1..];
        if (fault == "different-batch")
            batch = await test.Seed();
        if (fault == "expired")
            test.Clock.UtcNow = test.Clock.UtcNow.AddMinutes(16);
        if (fault == "oversized")
            cursor = new string('a', 2048);
        var error = await Should.ThrowAsync<ProviderOperationException>(() => test.List.HandleAsync(batch.Id, Reader("operator"), cursor, 2, default).AsTask());
        error.ErrorCode.ShouldBe(TelemetryErrorCodes.RequestInvalid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task Page_limits_are_enforced_before_loading_an_unbounded_result(int limit)
    {
        await using var test = Create();
        var batch = await test.Seed();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => test.List.HandleAsync(batch.Id, Reader("operator"), null, limit, default).AsTask());
    }

    [Fact]
    public async Task Each_page_rechecks_profile_revocation_and_producers_cannot_list_rejections()
    {
        await using var test = Create();
        var batch = await test.Seed();
        var page = (await test.List.HandleAsync(batch.Id, Reader("operator"), null, 2, default))!;
        test.Credentials.Profiles["operator"] = test.Credentials.Profiles["operator"] with
        {
            Enabled = false
        };
        (await test.List.HandleAsync(batch.Id, Reader("operator"), page.NextCursor, 2, default)).ShouldBeNull();
        var error = await Should.ThrowAsync<ProviderOperationException>(() => test.List.HandleAsync(batch.Id, Reader("producer"), null, 2, default).AsTask());
        error.ErrorCode.ShouldBe(TelemetryErrorCodes.AuthenticationRequired);
    }

    [Fact]
    public async Task Cursor_high_water_mark_does_not_expand_when_processing_adds_more_rejections()
    {
        await using var test = Create();
        var batch = await test.Seed(finalize: false);
        var page = (await test.List.HandleAsync(batch.Id, Reader("cross"), null, 2, default))!;
        await test.AddRejected(batch.Id, 9);
        var rest = (await test.List.HandleAsync(batch.Id, Reader("cross"), page.NextCursor, 200, default))!;
        rest.Items.Select(item => item.LineNumber).ShouldBe(new[] { 4, 6, 8 });
        var restarted = (await test.List.HandleAsync(batch.Id, Reader("cross"), null, 200, default))!;
        restarted.Items.Select(item => item.LineNumber).ShouldBe(new[] { 1, 3, 4, 6, 8, 9 });
    }

    [Fact]
    public async Task Raw_content_is_opened_only_after_the_matching_audit_commit()
    {
        await using var test = Create();
        var batch = await test.Seed();
        test.Audit.Pause = true;
        var pending = test.Raw.HandleAsync(batch.Id, batch.QuarantineId, Reader("operator"), "trace-raw", default).AsTask();
        await test.Audit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        test.Quarantine.OpenCalls.ShouldBe(0);
        pending.IsCompleted.ShouldBeFalse();
        test.Audit.Release.TrySetResult();
        await using var result = await pending;
        result.ErrorCode.ShouldBeNull();
        result.Content.ShouldNotBeNull();
        using var copy = new MemoryStream();
        await result.Content.Content.CopyToAsync(copy);
        Encoding.UTF8.GetString(copy.ToArray()).ShouldBe(Fixture.RawText);
        await using var db = database.CreateContext();
        var audit = await db.AuditEvents.SingleAsync(row => row.OpaqueResourceId == batch.QuarantineId.ToString("D"));
        audit.Outcome.ShouldBe(AuditOutcome.Allowed);
        audit.Action.ShouldBe(AuditAction.ReadQuarantineContent);
        audit.TraceId.ShouldBe("trace-raw");
    }

    [Theory]
    [InlineData("producer", TelemetryErrorCodes.AuthenticationRequired)]
    [InlineData("scoped", TelemetryErrorCodes.ResourceNotFound)]
    [InlineData("noperm", TelemetryErrorCodes.AccessDenied)]
    [InlineData("viewer", TelemetryErrorCodes.AccessDenied)]
    [InlineData("disabled", TelemetryErrorCodes.AccessDenied)]
    public async Task Denied_raw_reads_are_audited_without_opening_content(string name, string code)
    {
        await using var test = Create();
        var batch = await test.Seed();
        await using var result = await test.Raw.HandleAsync(batch.Id, batch.QuarantineId, Reader(name), "trace-denied", default);
        result.ErrorCode.ShouldBe(code);
        result.Content.ShouldBeNull();
        test.Quarantine.OpenCalls.ShouldBe(0);
        test.Audit.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AuditOutcome.Denied);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-batch")]
    [InlineData("expired")]
    [InlineData("deleted")]
    [InlineData("missing-file")]
    public async Task Unavailable_raw_content_has_a_non_disclosing_result_and_the_appropriate_audit(string state)
    {
        await using var test = Create();
        var batch = await test.Seed(ageDays: state == "expired" ? 31 : 0);
        var id = state == "missing" ? Guid.NewGuid() : batch.QuarantineId;
        var parent = state == "wrong-batch" ? (await test.Seed()).Id : batch.Id;
        if (state == "deleted")
        {
            await using var db = database.CreateContext();
            await db.QuarantinedRejections.Where(row => row.QuarantineId == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DeletedAt, test.Clock.UtcNow));
        }
        if (state == "missing-file")
            test.Quarantine.MissingMetadata = true;
        await using var result = await test.Raw.HandleAsync(parent, id, Reader("operator"), "trace-missing", default);
        result.ErrorCode.ShouldBe(TelemetryErrorCodes.ResourceNotFound);
        test.Quarantine.OpenCalls.ShouldBe(0);
        test.Audit.Records.ShouldHaveSingleItem().Outcome.ShouldBe(state is "expired" or "deleted" ? AuditOutcome.Expired : AuditOutcome.Missing);
    }

    [Theory]
    [InlineData("allowed")]
    [InlineData("denied")]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("wrong-receipt")]
    public async Task Audit_failure_fails_closed_for_every_raw_outcome(string state)
    {
        await using var test = Create();
        var batch = await test.Seed(ageDays: state == "expired" ? 31 : 0);
        test.Audit.Fail = state != "wrong-receipt";
        test.Audit.WrongReceipt = state == "wrong-receipt";
        var actor = Reader(state == "denied" ? "noperm" : "operator");
        var id = state == "missing" ? Guid.NewGuid() : batch.QuarantineId;
        var error = await Should.ThrowAsync<ProviderOperationException>(() => test.Raw.HandleAsync(batch.Id, id, actor, "trace-audit-failure", default).AsTask());
        error.ErrorCode.ShouldBe(TelemetryErrorCodes.AuditUnavailable);
        test.Quarantine.OpenCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Expiry_during_audit_wait_does_not_release_raw_content()
    {
        await using var test = Create();
        var batch = await test.Seed();
        test.Audit.Pause = true;
        var pending = test.Raw.HandleAsync(batch.Id, batch.QuarantineId, Reader("operator"), "trace-expiry-race", default).AsTask();
        await test.Audit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        test.Clock.UtcNow = test.Clock.UtcNow.AddDays(31);
        test.Audit.Release.TrySetResult();
        await using var result = await pending;
        result.ErrorCode.ShouldBe(TelemetryErrorCodes.ResourceNotFound);
        test.Quarantine.OpenCalls.ShouldBe(0);
        test.Audit.Records.Last().Outcome.ShouldBe(AuditOutcome.Expired);
    }

    [Theory]
    [InlineData("revoked", AuditOutcome.Denied)]
    [InlineData("deleted", AuditOutcome.Expired)]
    [InlineData("missing-file", AuditOutcome.Missing)]
    public async Task Changes_during_audit_wait_fail_closed(string change, AuditOutcome outcome)
    {
        await using var test = Create();
        var batch = await test.Seed();
        test.Audit.Pause = true;
        var pending = test.Raw.HandleAsync(batch.Id, batch.QuarantineId, Reader("operator"), "trace-change", default).AsTask();
        await test.Audit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "revoked")
            test.Credentials.Profiles["operator"] = test.Credentials.Profiles["operator"] with
            {
                Enabled = false
            };
        if (change == "deleted")
        {
            await using var db = database.CreateContext();
            await db.QuarantinedRejections.Where(x => x.QuarantineId == batch.QuarantineId).ExecuteUpdateAsync(set => set.SetProperty(x => x.DeletedAt, test.Clock.UtcNow));
        }
        if (change == "missing-file")
            test.Quarantine.MissingContent = true;
        test.Audit.Release.TrySetResult();
        await using var result = await pending;
        result.ErrorCode.ShouldBe(TelemetryErrorCodes.ResourceNotFound);
        result.Content.ShouldBeNull();
        test.Audit.Records.Last().Outcome.ShouldBe(outcome);
    }

    [Fact]
    public async Task Corrupted_file_is_audited_without_exposing_the_storage_error()
    {
        await using var test = Create();
        var batch = await test.Seed();
        test.Quarantine.CorruptMetadata = true;
        await using var result = await test.Raw.HandleAsync(batch.Id, batch.QuarantineId, Reader("operator"), "trace-corrupt", default);
        result.ErrorCode.ShouldBe(TelemetryErrorCodes.ResourceNotFound);
        test.Quarantine.OpenCalls.ShouldBe(0);
        test.Audit.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AuditOutcome.Missing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("YQ==")]
    public async Task Paging_requires_an_explicit_valid_persistent_key(string key)
    {
        await using var test = Create();
        var batch = await test.Seed();
        test.Services.GetRequiredService<IConfiguration>()["BatchInspection:CursorKey"] = key;
        await Should.ThrowAsync<InvalidOperationException>(() => test.List.HandleAsync(batch.Id, Reader("operator"), null, 2, default).AsTask());
        // Status visibility remains independent of cursor key provisioning.
        (await test.Visibility.GetVisibleAsync(batch.Id, Reader("producer"), default)).ShouldNotBeNull();
    }
    private static BatchReadIdentity Reader(string name) => name is "producer" or "other-producer"
        ? BatchReadIdentity.ForProducer(name)
        : BatchReadIdentity.ForOperator(new(name, name == "viewer" ? OperatorActorKind.ViewerPresentationService : OperatorActorKind.Human, name == "viewer" ? "org" : null));

    private Fixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "centralmobilidade-t057-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Quarantine:Path"] = root,
            ["BatchInspection:CursorKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x57, 32).ToArray())
        }).Build();
        var clock = new Clock();
        var credentials = new Credentials();
        var filesystem = new FileSystemQuarantineStore(config, NullLogger<FileSystemQuarantineStore>.Instance);
        var tracked = new TrackedQuarantine(filesystem);
        var audit = new AuditProbe(new PostgresAuditWriter(database.CreateFactory()));
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config).AddSingleton<IClock>(clock)
            .AddSingleton<IAuthenticationCredentialStore>(credentials).AddScoped<IOperatorAuthorizer, StoredOperatorAuthorizer>()
            .AddTelemetryControlPersistence(database.Connection).AddTelemetryBatchInspection(config)
            .AddSingleton<IQuarantineStore>(tracked).AddSingleton<IAuditWriter>(audit).BuildServiceProvider();
        return new(services, clock, credentials, filesystem, tracked, audit, root);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
    private sealed class Credentials : IAuthenticationCredentialStore
    {
        internal Dictionary<string, CentralMobilidade.Telemetry.Application.Security.OperatorAccessProfile> Profiles { get; } = new();
        internal Credentials()
        {
            foreach (var name in new[] { "operator", "scoped", "cross", "noperm", "viewer", "disabled" })
                Profiles[name] = new(name, name == "viewer" ? OperatorActorKind.ViewerPresentationService : OperatorActorKind.Human,
                    name == "noperm" ? [] : name is "cross" or "viewer" ? ["batch.read", "quarantine.raw.read", "cross_company.read"] : ["batch.read", "quarantine.raw.read"],
                    name == "scoped" ? ["company-a"] : ["company-a", "company-b"], name != "disabled", name == "viewer" ? "org" : null);
        }
        public ValueTask<ProducerCredential?> FindProducerAsync(string digest, CancellationToken token) => ValueTask.FromResult<ProducerCredential?>(null);
        public ValueTask<OperatorBearerCredential?> FindOperatorAsync(string digest, BearerCredentialKind kind, CancellationToken token) => ValueTask.FromResult<OperatorBearerCredential?>(null);
        public ValueTask<CentralMobilidade.Telemetry.Application.Security.OperatorAccessProfile?> GetProfileAsync(string id, CancellationToken token) => ValueTask.FromResult(Profiles.GetValueOrDefault(id));
    }
    private sealed class AuditProbe(IAuditWriter inner) : IAuditWriter
    {
        internal bool Pause, Fail, WrongReceipt;
        internal ConcurrentQueue<AuditRecord> Records { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<AuditReceipt> AppendAsync(AuditRecord record, CancellationToken token)
        {
            Records.Enqueue(record);
            Entered.TrySetResult();
            if (Pause)
                await Release.Task.WaitAsync(token);
            if (Fail)
                throw new IOException("PRIVATE_AUDIT_DIAGNOSTIC");
            var receipt = await inner.AppendAsync(record, token);
            return WrongReceipt ? receipt with
            {
                EventId = Guid.NewGuid()
            } : receipt;
        }
    }
    private sealed class TrackedQuarantine(IQuarantineStore inner) : IQuarantineStore
    {
        internal int OpenCalls;
        internal bool MissingMetadata, MissingContent, CorruptMetadata;
        public ValueTask<QuarantineReceipt> WriteAsync(QuarantineWriteRequest request, Stream raw, CancellationToken token) => inner.WriteAsync(request, raw, token);
        public ValueTask<QuarantineMetadata?> GetMetadataAsync(Guid id, CancellationToken token) => CorruptMetadata ? throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Permanent) : MissingMetadata ? ValueTask.FromResult<QuarantineMetadata?>(null) : inner.GetMetadataAsync(id, token);
        public ValueTask<IQuarantineContent?> OpenReadAsync(Guid id, CancellationToken token)
        {
            OpenCalls++;
            return MissingContent ? ValueTask.FromResult<IQuarantineContent?>(null) : inner.OpenReadAsync(id, token);
        }
        public ValueTask<bool> DeleteExpiredAsync(Guid id, DateTimeOffset at, CancellationToken token) => inner.DeleteExpiredAsync(id, at, token);
    }
    private sealed record Fixture(ServiceProvider Services, Clock Clock, Credentials Credentials, FileSystemQuarantineStore Files, TrackedQuarantine Quarantine, AuditProbe Audit, string Root) : IAsyncDisposable
    {
        internal const string RawText = "PRIVATE_RAW_T057 placa=TEST cpf=12345678901 token=PRIVATE";
        internal BatchVisibility Visibility => Services.GetRequiredService<BatchVisibility>();
        internal ListBatchRejectionsHandler List => Services.GetRequiredService<ListBatchRejectionsHandler>();
        internal GetQuarantinedRejectionContentHandler Raw => Services.GetRequiredService<GetQuarantinedRejectionContentHandler>();
        internal async Task<(BatchId Id, Guid QuarantineId)> Seed(bool unknownCompany = false, bool finalize = true, int ageDays = 0)
        {
            var at = Clock.UtcNow.AddDays(-ageDays);
            var store = Services.GetRequiredService<IBatchStateStore>();
            var envelope = new TelemetryBatchEnvelope("1", Guid.NewGuid(), new string('a', 32), "producer", at, "text/plain; charset=utf-8", "gzip", 128, new string('a', 64), "v1");
            var snapshot = await store.CreateAsync(envelope, at.AddDays(30), default);
            foreach (var state in new[] { BatchLifecycle.Publishing, BatchLifecycle.Accepted, BatchLifecycle.Processing })
            {
                (await store.TryTransitionAsync(snapshot.BatchId, snapshot.Version, new(state, at.AddSeconds(snapshot.Version)), default)).ShouldBeTrue();
                snapshot = (await store.GetAsync(snapshot.BatchId, default))!;
            }
            Guid first = default;
            foreach (var number in new[] { 1, 3, 4, 6, 8 })
            {
                var id = await AddRejected(snapshot.BatchId, number, at, unknownCompany && number == 6);
                if (number == 1)
                    first = id;
            }
            await store.RecordLineResultsAsync(snapshot.BatchId, [new(2, LineOutcome.Accepted, null, new("company-a"), new("vehicle"), Digest, at),
                new(5, LineOutcome.Accepted, null, new("company-a"), new("vehicle"), Digest, at), new(7, LineOutcome.IdenticalReplay, null, new("company-b"), new("vehicle"), Digest, at)], default);
            snapshot = (await store.GetAsync(snapshot.BatchId, default))!;
            if (finalize)
                (await store.TryTransitionAsync(snapshot.BatchId, snapshot.Version, new(BatchLifecycle.CompletedWithRejections, at.AddSeconds(20), new(2, 5, 1, 8)), default)).ShouldBeTrue();
            return (snapshot.BatchId, first);
        }
        private const string Digest = "hmac-sha256:v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal async Task<Guid> AddRejected(BatchId batch, int number, DateTimeOffset? created = null, bool unknown = false)
        {
            var at = created ?? Clock.UtcNow;
            var id = Guid.NewGuid();
            using var raw = new MemoryStream(Encoding.UTF8.GetBytes(RawText));
            await Files.WriteAsync(new(id, batch, number, TelemetryErrorCodes.LpInvalidSyntax, at, at.AddDays(30)), raw, default);
            var metadata = (await Files.GetMetadataAsync(id, default))!;
            await Services.GetRequiredService<IBatchStateStore>().RecordLineResultsAsync(batch,
                [new(number, LineOutcome.Rejected, TelemetryErrorCodes.LpInvalidSyntax, unknown ? null : new(number == 3 ? "company-b" : "company-a"),
                    new("vehicle"), Digest, at, id, metadata)], default);
            return id;
        }
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            var resolved = Path.GetFullPath(Root);
            Path.GetDirectoryName(resolved).ShouldBe(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())));
            Path.GetFileName(resolved).ShouldStartWith("centralmobilidade-t057-");
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }
}

