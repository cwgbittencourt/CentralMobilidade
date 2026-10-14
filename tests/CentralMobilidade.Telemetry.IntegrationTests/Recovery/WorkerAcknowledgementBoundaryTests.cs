using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.Ingestion;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using CentralMobilidade.Telemetry.Infrastructure.Persistence;
using CentralMobilidade.Telemetry.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.IntegrationTests.Recovery;

[Trait("Category", "Integration")]
public sealed class WorkerAcknowledgementBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly EventIdentity Identity = new(new CompanyId("company"), new VehicleId("vehicle"), EventTimestamp.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds()));
    private static readonly string Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("original")));

    [Fact]
    public async Task Concurrent_reservation_has_one_winner_and_conflicting_contender_cannot_replace_it()
    {
        await using var fixture = await Fixture.Create(false);
        var requests = Enumerable.Range(0, 16).Select(_ => Reservation()).ToArray();
        var results = await Task.WhenAll(requests.Select(request => fixture.Guard.AcquireOrResumeAsync(request, CancellationToken.None).AsTask()));
        results.OfType<IdentityReservationResult.Granted>().Count().ShouldBe(1);
        var lease = results.OfType<IdentityReservationResult.Granted>().Single().Lease;
        (await fixture.Guard.TryMarkWritingAsync(lease, Now, CancellationToken.None)).ShouldBeTrue();
        (await fixture.Guard.TryCommitAsync(lease with
        {
            State = IdentityGuardState.Writing
        }, Now, CancellationToken.None)).ShouldBeTrue();
        var conflict = await fixture.Guard.AcquireOrResumeAsync(Reservation() with
        {
            Fingerprint = new string('b', 64)
        }, CancellationToken.None);
        conflict.ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(ExistingIdentityOutcome.Conflict);
        var replay = await fixture.Guard.AcquireOrResumeAsync(Reservation(), CancellationToken.None);
        replay.ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(ExistingIdentityOutcome.IdenticalReplay);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_resume_retains_fence_and_takeover_fences_stale_writers(bool writing)
    {
        await using var fixture = await Fixture.Create(false);
        var request = Reservation();
        var lease = (await fixture.Guard.AcquireOrResumeAsync(request, CancellationToken.None)).ShouldBeOfType<IdentityReservationResult.Granted>().Lease;
        if (writing)
        {
            (await fixture.Guard.TryMarkWritingAsync(lease, Now, CancellationToken.None)).ShouldBeTrue();
            lease = lease with
            {
                State = IdentityGuardState.Writing
            };
        }
        var resume = (await fixture.Guard.AcquireOrResumeAsync(request with
        {
            Now = Now.AddSeconds(1)
        }, CancellationToken.None)).ShouldBeOfType<IdentityReservationResult.Granted>();
        resume.IsResumption.ShouldBeTrue();
        resume.Lease.FencingVersion.ShouldBe(lease.FencingVersion);
        var competing = Reservation() with
        {
            Now = Now.AddSeconds(2)
        };
        (await fixture.Guard.AcquireOrResumeAsync(competing, CancellationToken.None)).ShouldBeOfType<IdentityReservationResult.Contended>();
        competing = competing with
        {
            Now = Now.AddMinutes(2),
            LeaseUntil = Now.AddMinutes(3)
        };
        var takeovers = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => fixture.Guard.AcquireOrResumeAsync(competing with { Owner = new(new BatchId(Guid.NewGuid()), 1) }, CancellationToken.None).AsTask()));
        var successor = takeovers.OfType<IdentityReservationResult.Granted>().Single().Lease;
        successor.FencingVersion.ShouldBeGreaterThan(lease.FencingVersion);
        (await fixture.Guard.RenewAsync(lease, competing.Now, competing.LeaseUntil, CancellationToken.None)).ShouldBeNull();
        (await fixture.Guard.TryMarkWritingAsync(lease, competing.Now, CancellationToken.None)).ShouldBeFalse();
        (await fixture.Guard.TryCommitAsync(lease with
        {
            State = IdentityGuardState.Writing
        }, competing.Now, CancellationToken.None)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ExistingIdentityOutcome.IdenticalReplay)]
    [InlineData(ExistingIdentityOutcome.Conflict)]
    public async Task Expired_evidence_still_blocks_committed_identity(ExistingIdentityOutcome previousClassification)
    {
        await using var fixture = await Fixture.Create(false);
        var request = Reservation();
        var lease = (await fixture.Guard.AcquireOrResumeAsync(request, CancellationToken.None)).ShouldBeOfType<IdentityReservationResult.Granted>().Lease;
        (await fixture.Guard.TryMarkWritingAsync(lease, Now, CancellationToken.None)).ShouldBeTrue();
        (await fixture.Guard.TryCommitAsync(lease with
        {
            State = IdentityGuardState.Writing
        }, Now, CancellationToken.None)).ShouldBeTrue();
        var fingerprint = previousClassification == ExistingIdentityOutcome.Conflict ? new string('b', 64) : Fingerprint;
        (await fixture.Guard.AcquireOrResumeAsync(Reservation() with
        {
            Fingerprint = fingerprint
        }, CancellationToken.None))
            .ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(previousClassification);
        await using var db = fixture.Database.Create();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM event_identity_evidence WHERE \"EvidenceExpiresAt\" <= {0}", Now.AddDays(31));
        var expired = await fixture.Guard.AcquireOrResumeAsync(Reservation() with
        {
            Fingerprint = fingerprint,
            Now = Now.AddDays(31),
            LeaseUntil = Now.AddDays(31).AddMinutes(1),
            EvidenceExpiresAt = Now.AddDays(61)
        }, CancellationToken.None);
        expired.ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(ExistingIdentityOutcome.AlreadyExistsUnclassified);
        (await fixture.Guard.GetCommittedAsync(Identity, CancellationToken.None)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("before-reserved")]
    [InlineData("after-reserved")]
    [InlineData("before-writing")]
    [InlineData("after-writing")]
    [InlineData("before-influx")]
    [InlineData("after-influx")]
    [InlineData("before-committed")]
    [InlineData("after-committed")]
    [InlineData("before-results")]
    [InlineData("after-results")]
    [InlineData("before-terminal")]
    [InlineData("after-terminal")]
    [InlineData("before-ack")]
    public async Task Redelivery_after_each_failure_boundary_preserves_one_logical_event_and_delays_ack(string boundary)
    {
        await using var fixture = await Fixture.Create(true);
        var metadata = await fixture.Accepted();
        fixture.Probe.FailAt = boundary;
        await using var first = new Delivery(metadata, fixture.Probe);
        await fixture.Run(first);
        fixture.Probe.Triggered.ShouldBeTrue("The production processing path must reach the selected boundary");
        first.Completed.ShouldBeFalse();
        fixture.Probe.FailAt = null;
        await using var retry = new Delivery(metadata, fixture.Probe, 2);
        await fixture.Run(retry);
        retry.Completed.ShouldBeTrue();
        fixture.Writer.Points.Count.ShouldBe(1);
        fixture.Writer.ChangedOriginal.ShouldBeFalse();
        var snapshot = (await fixture.RealStore.GetAsync(new(metadata.BatchId), CancellationToken.None)).ShouldNotBeNull();
        snapshot.State.ShouldBeOneOf(BatchLifecycle.Completed, BatchLifecycle.CompletedWithRejections);
        fixture.Probe.Events.Last().ShouldBe("ack");
        if (boundary is "after-influx" or "before-committed")
            fixture.Writer.Calls.ShouldBeGreaterThanOrEqualTo(2, "Ambiguous persistence may repeat identical physical writes");
        if (boundary is "after-committed" or "before-results" or "after-results" or "before-terminal" or "after-terminal" or "before-ack")
            fixture.Writer.Calls.ShouldBe(1, "A committed identity cannot trigger another physical write");
    }

    [Fact]
    public async Task Queue_ack_waits_for_telemetry_persistence_and_guard_commit()
    {
        await using var fixture = await Fixture.Create(true);
        var metadata = await fixture.Accepted();
        fixture.Writer.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var delivery = new Delivery(metadata, fixture.Probe);
        var processing = fixture.Run(delivery);
        await fixture.Writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        delivery.Completed.ShouldBeFalse();
        (await fixture.Guard.GetCommittedAsync(Identity, CancellationToken.None)).ShouldBeNull();
        fixture.Writer.Hold.SetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
        delivery.Completed.ShouldBeTrue();
        (await fixture.Guard.GetCommittedAsync(Identity, CancellationToken.None)).ShouldNotBeNull();
    }

    private static IdentityReservationRequest Reservation() => new(Identity, new(new BatchId(Guid.NewGuid()), 1), Fingerprint,
        TelemetryDestination.Queryable, Now, Now.AddMinutes(1), Now.AddDays(60), Now.AddDays(30));

    [Fact]
    public async Task Resuming_owner_cannot_change_reserved_fingerprint()
    {
        await using var fixture = await Fixture.Create(false);
        var request = Reservation();
        (await fixture.Guard.AcquireOrResumeAsync(request, CancellationToken.None)).ShouldBeOfType<IdentityReservationResult.Granted>();
        var changed = await fixture.Guard.AcquireOrResumeAsync(request with
        {
            Fingerprint = new string('b', 64)
        }, CancellationToken.None);
        (changed is IdentityReservationResult.Granted).ShouldBeFalse();
        var resumed = (await fixture.Guard.AcquireOrResumeAsync(request, CancellationToken.None)).ShouldBeOfType<IdentityReservationResult.Granted>();
        resumed.IsResumption.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Processing_after_evidence_expiry_cannot_rewrite_original(bool changedContent)
    {
        await using var fixture = await Fixture.Create(true);
        var original = await fixture.Accepted();
        await using var first = new Delivery(original, fixture.Probe);
        await fixture.Run(first);
        first.Completed.ShouldBeTrue();
        fixture.Clock.UtcNow = Now.AddDays(31);
        await using (var db = fixture.Database.Create())
            await db.Database.ExecuteSqlRawAsync("DELETE FROM event_identity_evidence WHERE \"EvidenceExpiresAt\" <= {0}", fixture.Clock.UtcNow);
        var raw = changedContent ? RawLine.Replace("velocidade=0.0", "velocidade=99.0", StringComparison.Ordinal) : RawLine;
        var metadata = await fixture.Accepted(raw);
        await using var repeated = new Delivery(metadata, fixture.Probe, raw: raw);
        await fixture.Run(repeated);
        repeated.Completed.ShouldBeTrue();
        fixture.Writer.Calls.ShouldBe(1);
        fixture.Writer.Points.Count.ShouldBe(1);
        fixture.Writer.ChangedOriginal.ShouldBeFalse();
        var snapshot = (await fixture.RealStore.GetAsync(new(metadata.BatchId), CancellationToken.None)).ShouldNotBeNull();
        snapshot.Counters.Rejected.ShouldBe(1, "Expired evidence cannot promise identical replay classification");
    }
    private static string RawLine => "telemetria,empresa_id=company,veiculo_id=vehicle latitude=1.0,longitude=2.0,velocidade=0.0,rpm=0i,ignicao=false " + Now.ToUnixTimeMilliseconds() + "\n";
    [Theory]
    [InlineData("before-quarantine")]
    [InlineData("after-quarantine")]
    public async Task Mixed_batch_ack_requires_durable_quarantine_and_replay_safe_rejections(string boundary)
    {
        await using var fixture = await Fixture.Create(true);
        var raw = RawLine + "invalid line\n";
        var metadata = await fixture.Accepted(raw);
        fixture.Probe.FailAt = boundary;
        await using var first = new Delivery(metadata, fixture.Probe, raw: raw);
        await fixture.Run(first);
        fixture.Probe.Triggered.ShouldBeTrue();
        first.Completed.ShouldBeFalse();
        fixture.Probe.FailAt = null;
        await using var retry = new Delivery(metadata, fixture.Probe, 2, raw);
        await fixture.Run(retry);
        retry.Completed.ShouldBeTrue();
        fixture.Writer.Points.Count.ShouldBe(1);
        fixture.Quarantine.Items.Count.ShouldBe(1);
        var batch = (await fixture.RealStore.GetAsync(new(metadata.BatchId), CancellationToken.None)).ShouldNotBeNull();
        batch.State.ShouldBe(BatchLifecycle.CompletedWithRejections);
        batch.Counters.Rejected.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_workers_cannot_write_conflicting_content_for_same_identity()
    {
        await using var fixture = await Fixture.Create(true);
        var original = await fixture.Accepted();
        var changed = RawLine.Replace("velocidade=0.0", "velocidade=99.0", StringComparison.Ordinal);
        var conflicting = await fixture.Accepted(changed);
        fixture.Writer.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = new Delivery(original, fixture.Probe);
        var processing = fixture.Run(first);
        await fixture.Writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var second = new Delivery(conflicting, fixture.Probe, raw: changed);
        await fixture.Run(second).WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Writer.Points.Count.ShouldBe(0, "The original write is still blocked");
        fixture.Writer.Hold.SetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(5));
        if (!second.Completed)
        {
            await using var retry = new Delivery(conflicting, fixture.Probe, 2, changed);
            await fixture.Run(retry);
            retry.Completed.ShouldBeTrue();
        }
        fixture.Writer.Calls.ShouldBe(1);
        fixture.Writer.Points.Count.ShouldBe(1);
        fixture.Writer.ChangedOriginal.ShouldBeFalse();
        var snapshot = (await fixture.RealStore.GetAsync(new(conflicting.BatchId), CancellationToken.None)).ShouldNotBeNull();
        snapshot.Counters.Rejected.ShouldBe(1);
    }

    private static byte[] Gzip(string? raw = null)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
            gzip.Write(Encoding.UTF8.GetBytes(raw ?? RawLine));
        return output.ToArray();
    }

    private sealed class Probe
    {
        internal string? FailAt;
        internal bool Triggered;
        internal ConcurrentQueue<string> Events { get; } = new();
        internal void Hit(string stage)
        {
            Events.Enqueue(stage);
            if (stage == FailAt && !Triggered)
            {
                Triggered = true;
                throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Ambiguous);
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal MigratedPostgresFixture Database
        {
            get;
        }
        private readonly ServiceProvider services;
        internal IEventIdentityGuard Guard
        {
            get;
        }
        internal IBatchStateStore RealStore
        {
            get;
        }
        internal Probe Probe { get; } = new();
        internal Writer Writer
        {
            get;
        }
        internal Quarantine Quarantine
        {
            get;
        }
        internal Clock Clock { get; } = new();
        private readonly Type? handlerType;
        private Fixture(MigratedPostgresFixture database, Type guardType, Type? handlerType)
        {
            Database = database;
            var collection = new ServiceCollection();
            collection.AddTelemetryControlPersistence(database.Connection);
            collection.AddLogging();
            collection.AddSingleton<IClock>(Clock);
            collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            var options = new TelemetryOptions { WriteBatchMaxPoints = 1 };
            collection.AddSingleton(options);
            collection.AddSingleton<IOptions<TelemetryOptions>>(Options.Create(options));
            collection.AddSingleton(TimeProvider.System);
            Writer = new(Probe);
            collection.AddSingleton<ITelemetryWriter>(Writer);
            collection.AddSingleton<ICompanyVisibilityResolver>(new Visibility());
            Quarantine = new(Probe);
            collection.AddSingleton<IQuarantineStore>(Quarantine);
            collection.AddTransient<IEventIdentityGuard>(provider => new GuardDecorator(ActivatorUtilities.CreateInstance(provider, guardType).ShouldBeAssignableTo<IEventIdentityGuard>()!, Probe));
            collection.AddTransient<IBatchStateStore>(provider => new StoreDecorator(new PostgresBatchStateStore(provider.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>()), Probe));
            foreach (var name in new[] { "CentralMobilidade.Telemetry.Application.LineProtocol.TelemetryLineProtocolV1Parser", "CentralMobilidade.Telemetry.Application.Processing.InfluxWriteBatcher" })
                if (typeof(AcceptTelemetryBatchHandler).Assembly.GetType(name) is { } type)
                    collection.AddTransient(type);
            services = collection.BuildServiceProvider();
            Guard = services.GetRequiredService<IEventIdentityGuard>();
            RealStore = new PostgresBatchStateStore(services.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>());
            this.handlerType = handlerType;
        }
        internal static async Task<Fixture> Create(bool processing)
        {
            var handlerType = typeof(AcceptTelemetryBatchHandler).Assembly.GetType("CentralMobilidade.Telemetry.Application.Processing.ProcessTelemetryBatchHandler");
            if (processing)
                handlerType.ShouldNotBeNull("T051 must implement the production processing/acknowledgement path");
            var guardType = typeof(PostgresBatchStateStore).Assembly.GetType("CentralMobilidade.Telemetry.Infrastructure.Idempotency.SelectedEventIdentityGuard");
            guardType.ShouldNotBeNull("T047 must implement the actual durable identity guard selected by ADR-001");
            var database = await MigratedPostgresFixture.StartAsync();
            try
            {
                return new(database, guardType, processing ? handlerType : null);
            }
            catch { await database.DisposeAsync(); throw; }
        }
        internal async Task<TelemetryBatchEnvelope> Accepted(string? raw = null)
        {
            var bytes = Gzip(raw);
            var received = Clock.UtcNow;
            var metadata = new TelemetryBatchEnvelope("1", Guid.NewGuid(), "0123456789abcdef0123456789abcdef", "recovery-producer", received,
                "text/plain; charset=utf-8", "gzip", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "v1");
            var batch = await RealStore.CreateAsync(metadata, received.AddDays(30), CancellationToken.None);
            (await RealStore.TryTransitionAsync(batch.BatchId, batch.Version, new(BatchLifecycle.Publishing, received), CancellationToken.None)).ShouldBeTrue();
            (await RealStore.TryTransitionAsync(batch.BatchId, batch.Version + 1, new(BatchLifecycle.Accepted, received), CancellationToken.None)).ShouldBeTrue();
            return metadata;
        }
        internal async Task Run(Delivery delivery)
        {
            // Every delivery/retry gets fresh processing and batching objects; durable state is external.
            var handler = ActivatorUtilities.CreateInstance(services, handlerType!);
            var method = handler.GetType().GetMethod("HandleAsync", [typeof(IQueueDelivery), typeof(CancellationToken)]).ShouldNotBeNull();
            try
            {
                var operation = method.Invoke(handler, [delivery, CancellationToken.None]);
                if (operation is Task task)
                    await task;
                else
                    await operation.ShouldBeOfType<ValueTask>();
            }
            catch (TargetInvocationException error) when (error.InnerException is ProviderOperationException && Probe.Triggered) { }
            catch (ProviderOperationException) when (Probe.Triggered) { }
        }
        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed class GuardDecorator(IEventIdentityGuard inner, Probe probe) : IEventIdentityGuard
    {
        public ValueTask<CommittedIdentitySnapshot?> GetCommittedAsync(EventIdentity identity, CancellationToken token) => inner.GetCommittedAsync(identity, token);
        public async ValueTask<IdentityReservationResult> AcquireOrResumeAsync(IdentityReservationRequest request, CancellationToken token)
        {
            probe.Hit("before-reserved");
            var result = await inner.AcquireOrResumeAsync(request, token);
            if (result is IdentityReservationResult.Granted)
                probe.Hit("after-reserved");
            return result;
        }
        public ValueTask<IdentityLease?> RenewAsync(IdentityLease lease, DateTimeOffset now, DateTimeOffset until, CancellationToken token) => inner.RenewAsync(lease, now, until, token);
        public async ValueTask<bool> TryMarkWritingAsync(IdentityLease lease, DateTimeOffset now, CancellationToken token)
        {
            probe.Hit("before-writing");
            var result = await inner.TryMarkWritingAsync(lease, now, token);
            probe.Hit("after-writing");
            return result;
        }
        public async ValueTask<bool> TryCommitAsync(IdentityLease lease, DateTimeOffset now, CancellationToken token)
        {
            probe.Hit("before-committed");
            var result = await inner.TryCommitAsync(lease, now, token);
            probe.Hit("after-committed");
            return result;
        }
        public ValueTask<PromotionLease?> TryBeginPromotionAsync(EventIdentity identity, Guid owner, DateTimeOffset now, DateTimeOffset until, CancellationToken token) => inner.TryBeginPromotionAsync(identity, owner, now, until, token);
        public ValueTask<PromotionLease?> RenewPromotionAsync(PromotionLease lease, DateTimeOffset now, DateTimeOffset until, CancellationToken token) => inner.RenewPromotionAsync(lease, now, until, token);
        public ValueTask<bool> TryCompletePromotionAsync(PromotionLease lease, DateTimeOffset now, CancellationToken token) => inner.TryCompletePromotionAsync(lease, now, token);
    }

    private sealed class StoreDecorator(IBatchStateStore inner, Probe probe) : IBatchStateStore
    {
        public ValueTask<BatchSnapshot> CreateAsync(TelemetryBatchEnvelope metadata, DateTimeOffset expires, CancellationToken token) => inner.CreateAsync(metadata, expires, token);
        public ValueTask<BatchSnapshot?> GetAsync(BatchId id, CancellationToken token) => inner.GetAsync(id, token);
        public async ValueTask<bool> TryTransitionAsync(BatchId id, long version, BatchTransition transition, CancellationToken token)
        {
            var terminal = transition.TargetState is BatchLifecycle.Completed or BatchLifecycle.CompletedWithRejections or BatchLifecycle.Rejected;
            if (terminal)
                probe.Hit("before-terminal");
            var result = await inner.TryTransitionAsync(id, version, transition, token);
            if (terminal)
                probe.Hit("after-terminal");
            return result;
        }
        public async ValueTask RecordLineResultsAsync(BatchId id, IReadOnlyCollection<LineProcessingOutcome> results, CancellationToken token)
        {
            probe.Hit("before-results");
            await inner.RecordLineResultsAsync(id, results, token);
            probe.Hit("after-results");
        }
        public ValueTask<RejectionResultPage> GetRejectionsAsync(BatchId id, string? cursor, int limit, CancellationToken token) => inner.GetRejectionsAsync(id, cursor, limit, token);
    }

    private sealed class Writer(Probe probe) : ITelemetryWriter
    {
        internal ConcurrentDictionary<EventIdentity, byte[]> Points { get; } = new();
        internal int Calls;
        internal bool ChangedOriginal;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Hold;
        public async ValueTask<TelemetryWriteResult> WriteAsync(TelemetryWriteRequest request, Stream body, CancellationToken token)
        {
            probe.Hit("before-influx");
            Entered.TrySetResult();
            if (Hold is not null)
                await Hold.Task.WaitAsync(token);
            using var bytes = new MemoryStream();
            using var gzip = new GZipStream(body, CompressionMode.Decompress, true);
            await gzip.CopyToAsync(bytes, token);
            Interlocked.Increment(ref Calls);
            var content = bytes.ToArray();
            foreach (var point in request.Points)
            {
                var original = Points.GetOrAdd(point.Identity, content);
                if (!original.AsSpan().SequenceEqual(content))
                    ChangedOriginal = true;
            }
            probe.Hit("after-influx");
            return new(request.Points.Count, []);
        }
    }

    private sealed class Delivery(TelemetryBatchEnvelope metadata, Probe probe, int attempt = 1, string? raw = null) : IQueueDelivery
    {
        public TelemetryBatchEnvelope Metadata => metadata;
        public Stream Body { get; } = new MemoryStream(Gzip(raw));
        public int DeliveryAttempt => attempt;
        internal bool Completed;
        public ValueTask CompleteAsync(CancellationToken token)
        {
            probe.Hit("before-ack");
            Completed = true;
            probe.Hit("ack");
            return ValueTask.CompletedTask;
        }
        public ValueTask RetryAsync(TimeSpan delay, CancellationToken token)
        {
            probe.Events.Enqueue("retry");
            return ValueTask.CompletedTask;
        }
        public ValueTask DeadLetterAsync(string code, CancellationToken token)
        {
            probe.Events.Enqueue("dead-letter");
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Body.Dispose();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }
    private sealed class Visibility : ICompanyVisibilityResolver
    {
        public ValueTask<CompanyVisibility> ResolveAsync(CompanyId company, CancellationToken token) => ValueTask.FromResult(CompanyVisibility.Visible);
    }
    private sealed class Quarantine(Probe probe) : IQuarantineStore
    {
        internal ConcurrentDictionary<Guid, byte[]> Items { get; } = new();
        public async ValueTask<QuarantineReceipt> WriteAsync(QuarantineWriteRequest request, Stream raw, CancellationToken token)
        {
            probe.Hit("before-quarantine");
            using var bytes = new MemoryStream();
            await raw.CopyToAsync(bytes, token);
            var content = bytes.ToArray();
            var original = Items.GetOrAdd(request.QuarantineId, content);
            original.ShouldBe(content);
            probe.Hit("after-quarantine");
            return new(request.QuarantineId, Convert.ToHexStringLower(SHA256.HashData(content)));
        }
        public ValueTask<QuarantineMetadata?> GetMetadataAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IQuarantineContent?> OpenReadAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<bool> DeleteExpiredAsync(Guid id, DateTimeOffset cutoff, CancellationToken token) => throw new NotSupportedException();
    }
}
