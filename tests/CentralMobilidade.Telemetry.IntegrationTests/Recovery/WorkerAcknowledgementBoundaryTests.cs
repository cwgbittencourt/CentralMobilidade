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
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private static readonly EventIdentity Identity = new(new CompanyId("company"), new VehicleId("vehicle"), EventTimestamp.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds()));
    private static readonly string Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("original")));

    [Fact]
    public async Task Single_store_keeps_company_id_in_identity_and_reinstantiation_cannot_reopen_committed_events()
    {
        await using var fixture = await Fixture.Create(false);
        var now = DateTimeOffset.UtcNow;
        foreach (var company in new[] { "not-in-registry-a", "not-in-registry-b" })
        {
            var request = Reservation(now) with
            {
                Identity = Identity with
                {
                    CompanyId = new CompanyId(company)
                }
            };
            var lease = (await fixture.Guard.AcquireOrResumeAsync(request, default)).ShouldBeOfType<IdentityReservationResult.Granted>().Lease;
            (await fixture.Guard.TryMarkWritingAsync(lease, now, default)).ShouldBeTrue();
            (await fixture.Guard.TryCommitAsync(lease with
            {
                State = IdentityGuardState.Writing
            }, now, default)).ShouldBeTrue();
        }
        await using var services = new ServiceCollection().AddTelemetryControlPersistence(fixture.Database.Connection).BuildServiceProvider();
        var restarted = new CentralMobilidade.Telemetry.Infrastructure.Idempotency.SelectedEventIdentityGuard(
            services.GetRequiredService<IDbContextFactory<TelemetryControlDbContext>>(), fixture.Clock);
        foreach (var company in new[] { "not-in-registry-a", "not-in-registry-b" })
        {
            var request = Reservation(now) with
            {
                Identity = Identity with
                {
                    CompanyId = new CompanyId(company)
                }
            };
            (await restarted.AcquireOrResumeAsync(request, default)).ShouldBeOfType<IdentityReservationResult.Existing>()
                .Outcome.ShouldBe(ExistingIdentityOutcome.IdenticalReplay);
            (await restarted.AcquireOrResumeAsync(request with
            {
                Fingerprint = new string('b', 64)
            }, default))
                .ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(ExistingIdentityOutcome.Conflict);
            (await restarted.GetCommittedAsync(request.Identity, default)).ShouldNotBeNull();
        }
        await using var db = fixture.Database.Create();
        (await db.IdentityGuards.CountAsync()).ShouldBe(2);
        (await db.IdentityEvidence.CountAsync()).ShouldBe(2);
    }

    [Theory]
    [Trait("Feature", "FoundationSingleInflux")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Processing_valid_company_ids_needs_no_registry_or_pending_destination(bool legacyLookupConfigured)
    {
        // Remains red for absent T051; this task must not implement the processing handler.
        await using var fixture = await Fixture.Create(true, legacyLookupConfigured: legacyLookupConfigured);
        var raw = RawLine.Replace("empresa_id=company", "empresa_id=not-in-registry-a", StringComparison.Ordinal) +
            RawLine.Replace("empresa_id=company", "empresa_id=not-in-registry-b", StringComparison.Ordinal);
        var metadata = await fixture.Accepted(raw);
        await using var delivery = new Delivery(metadata, fixture.Probe, raw: raw);
        await fixture.Run(delivery);
        delivery.Completed.ShouldBeTrue();
        fixture.Writer.LastTraceId.ShouldBe(metadata.TraceId);
        fixture.Writer.Points.Keys.Select(identity => identity.CompanyId.Value).OrderBy(id => id)
            .ShouldBe(new[] { "not-in-registry-a", "not-in-registry-b" });
        var snapshot = (await fixture.RealStore.GetAsync(new(metadata.BatchId), default)).ShouldNotBeNull();
        snapshot.State.ShouldBe(BatchLifecycle.Completed);
        snapshot.Counters.Accepted.ShouldBe(2);
        snapshot.Counters.Rejected.ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_reservation_has_one_winner_and_conflicting_contender_cannot_replace_it()
    {
        await using var fixture = await Fixture.Create(false);
        var now = DateTimeOffset.UtcNow;
        var requests = Enumerable.Range(0, 16).Select(_ => Reservation(now)).ToArray();
        var results = await Task.WhenAll(requests.Select(request => fixture.Guard.AcquireOrResumeAsync(request, CancellationToken.None).AsTask()));
        results.OfType<IdentityReservationResult.Granted>().Count().ShouldBe(1);
        var lease = results.OfType<IdentityReservationResult.Granted>().Single().Lease;
        (await fixture.Guard.TryMarkWritingAsync(lease, now, CancellationToken.None)).ShouldBeTrue();
        (await fixture.Guard.TryCommitAsync(lease with
        {
            State = IdentityGuardState.Writing
        }, now, CancellationToken.None)).ShouldBeTrue();
        var conflict = await fixture.Guard.AcquireOrResumeAsync(Reservation(now) with
        {
            Fingerprint = new string('b', 64)
        }, CancellationToken.None);
        conflict.ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(ExistingIdentityOutcome.Conflict);
        var replay = await fixture.Guard.AcquireOrResumeAsync(Reservation(now), CancellationToken.None);
        replay.ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(ExistingIdentityOutcome.IdenticalReplay);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_resume_retains_fence_and_takeover_fences_stale_writers(bool writing)
    {
        await using var fixture = await Fixture.Create(false);
        var now = DateTimeOffset.UtcNow;
        var request = Reservation(now);
        var lease = (await fixture.Guard.AcquireOrResumeAsync(request, CancellationToken.None)).ShouldBeOfType<IdentityReservationResult.Granted>().Lease;
        if (writing)
        {
            (await fixture.Guard.TryMarkWritingAsync(lease, now, CancellationToken.None)).ShouldBeTrue();
            lease = lease with
            {
                State = IdentityGuardState.Writing
            };
        }
        var resume = (await fixture.Guard.AcquireOrResumeAsync(request with
        {
            Now = now.AddSeconds(1)
        }, CancellationToken.None)).ShouldBeOfType<IdentityReservationResult.Granted>();
        resume.IsResumption.ShouldBeTrue();
        resume.Lease.FencingVersion.ShouldBe(lease.FencingVersion);
        var competing = Reservation(now) with
        {
            Now = now.AddSeconds(2)
        };
        (await fixture.Guard.AcquireOrResumeAsync(competing, CancellationToken.None)).ShouldBeOfType<IdentityReservationResult.Contended>();
        // Represent real lease expiry in the isolated database: database triggers use server time.
        await using (var db = fixture.Database.Create())
            await db.Database.ExecuteSqlRawAsync("UPDATE event_identity_guards SET \"LeaseUntil\"=clock_timestamp()-interval '1 second' WHERE \"OwnerBatchId\"={0}", request.Owner.BatchId.Value);
        competing = competing with
        {
            Now = now.AddMinutes(2),
            LeaseUntil = now.AddMinutes(3)
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
        var now = DateTimeOffset.UtcNow;
        var request = Reservation(now);
        var lease = (await fixture.Guard.AcquireOrResumeAsync(request, CancellationToken.None)).ShouldBeOfType<IdentityReservationResult.Granted>().Lease;
        (await fixture.Guard.TryMarkWritingAsync(lease, now, CancellationToken.None)).ShouldBeTrue();
        (await fixture.Guard.TryCommitAsync(lease with
        {
            State = IdentityGuardState.Writing
        }, now, CancellationToken.None)).ShouldBeTrue();
        var fingerprint = previousClassification == ExistingIdentityOutcome.Conflict ? new string('b', 64) : Fingerprint;
        (await fixture.Guard.AcquireOrResumeAsync(Reservation(now) with
        {
            Fingerprint = fingerprint
        }, CancellationToken.None))
            .ShouldBeOfType<IdentityReservationResult.Existing>().Outcome.ShouldBe(previousClassification);
        await using var db = fixture.Database.Create();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM event_identity_evidence WHERE \"EvidenceExpiresAt\" <= {0}", now.AddDays(31));
        var expired = await fixture.Guard.AcquireOrResumeAsync(Reservation(now) with
        {
            Fingerprint = fingerprint,
            Now = now.AddDays(31),
            LeaseUntil = now.AddDays(31).AddMinutes(1),
            EvidenceExpiresAt = now.AddDays(61)
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
        snapshot.Counters.Accepted.ShouldBe(1);
        snapshot.Counters.Replayed.ShouldBe(0);
        snapshot.Counters.Total.ShouldBe(1);
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

    private static IdentityReservationRequest Reservation(DateTimeOffset now) => new(Identity, new(new BatchId(Guid.NewGuid()), 1), Fingerprint,
        now, now.AddMinutes(1), now.AddDays(60), now.AddDays(30));

    [Fact]
    public async Task Resuming_owner_cannot_change_reserved_fingerprint()
    {
        await using var fixture = await Fixture.Create(false);
        var now = DateTimeOffset.UtcNow;
        var request = Reservation(now);
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

    [Fact]
    public async Task Mixed_replay_conflict_and_invalid_lines_have_exact_durable_counters()
    {
        await using var fixture = await Fixture.Create(true, maxPoints: 10);
        var raw = RawLine + RawLine + RawLine.Replace("velocidade=0.0", "velocidade=99.0", StringComparison.Ordinal) + "invalid line\n";
        var metadata = await fixture.Accepted(raw);
        await using var delivery = new Delivery(metadata, fixture.Probe, raw: raw);
        await fixture.Run(delivery);
        delivery.Completed.ShouldBeTrue();
        var snapshot = (await fixture.RealStore.GetAsync(new(metadata.BatchId), default)).ShouldNotBeNull();
        snapshot.Counters.ShouldBe(new BatchCounters(1, 2, 1, 4));
        snapshot.CompanyScopeComplete.ShouldBeFalse();
        fixture.Writer.Calls.ShouldBe(1);
        await using var db = fixture.Database.Create();
        (await db.QuarantinedRejections.CountAsync()).ShouldBe(2);
        (await db.LineResults.ToListAsync()).ShouldAllBe(r => r.SafeDigest.StartsWith("hmac-sha256:v1:"));
    }

    [Fact]
    public async Task Partial_storage_rejection_is_quarantined_and_never_commits_its_guard()
    {
        await using var fixture = await Fixture.Create(true, maxPoints: 2);
        fixture.Writer.RejectLine = 2;
        var raw = RawLine + RawLine.Replace("veiculo_id=vehicle", "veiculo_id=vehicle-2", StringComparison.Ordinal);
        var metadata = await fixture.Accepted(raw);
        await using var delivery = new Delivery(metadata, fixture.Probe, raw: raw);
        await fixture.Run(delivery);
        delivery.Completed.ShouldBeTrue();
        var snapshot = (await fixture.RealStore.GetAsync(new(metadata.BatchId), default)).ShouldNotBeNull();
        snapshot.Counters.ShouldBe(new BatchCounters(1, 1, 0, 2));
        snapshot.CompanyScopeComplete.ShouldBeTrue();
        await using var db = fixture.Database.Create();
        (await db.IdentityGuards.CountAsync(r => r.State == IdentityGuardState.Committed)).ShouldBe(1);
        (await db.QuarantinedRejections.SingleAsync()).ErrorCode.ShouldBe(TelemetryErrorCodes.StorageSchemaConflict);
        (await db.LineResults.SingleAsync(r => r.LineNumber == 2)).Outcome.ShouldBe(LineOutcome.Rejected);
    }

    [Fact]
    public async Task Cancelled_attempt_is_not_acknowledged_and_a_fresh_attempt_resumes_writing()
    {
        await using var fixture = await Fixture.Create(true);
        var metadata = await fixture.Accepted();
        fixture.Writer.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();
        await using var first = new Delivery(metadata, fixture.Probe);
        var processing = fixture.Run(first, cancelled.Token);
        await fixture.Writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => processing);
        first.Completed.ShouldBeFalse();
        fixture.Writer.Points.ShouldBeEmpty();
        fixture.Writer.Hold.TrySetResult();
        await using var retry = new Delivery(metadata, fixture.Probe, 2);
        await fixture.Run(retry);
        retry.Completed.ShouldBeTrue();
        fixture.Writer.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Envelope_hash_mismatch_causes_retry_before_any_storage_side_effect()
    {
        await using var fixture = await Fixture.Create(true);
        var metadata = await fixture.Accepted();
        await using var delivery = new Delivery(metadata, fixture.Probe, raw: RawLine + "invalid\n");
        await fixture.Run(delivery);
        delivery.Completed.ShouldBeFalse();
        fixture.Probe.Events.ShouldContain("retry");
        fixture.Writer.Calls.ShouldBe(0);
        await using var db = fixture.Database.Create();
        (await db.IdentityGuards.CountAsync()).ShouldBe(0);
        (await db.LineResults.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Long_write_renews_its_lease_and_lost_lease_cancels_without_ack(bool loseLease, bool attemptTimeout)
    {
        await using var fixture = await Fixture.Create(true, shortLease: true, shortAttempt: attemptTimeout);
        fixture.Clock.UseSystem = true;
        var metadata = await fixture.Accepted();
        fixture.Writer.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var delivery = new Delivery(metadata, fixture.Probe);
        var processing = fixture.Run(delivery);
        await fixture.Writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var db = fixture.Database.Create();
        var initial = (await db.IdentityGuards.AsNoTracking().SingleAsync()).LeaseUntil;
        if (attemptTimeout)
        {
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
            delivery.Completed.ShouldBeFalse();
            fixture.Probe.Events.ShouldContain("retry");
            fixture.Writer.Calls.ShouldBe(0);
        }
        else if (loseLease)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE event_identity_guards SET \"LeaseUntil\"=clock_timestamp()-interval '1 second'");
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
            delivery.Completed.ShouldBeFalse();
            fixture.Probe.Events.ShouldContain("retry");
            fixture.Writer.Calls.ShouldBe(0);
        }
        else
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            var renewed = (await db.IdentityGuards.AsNoTracking().SingleAsync()).LeaseUntil;
            renewed!.Value.ShouldBeGreaterThan(initial!.Value);
            renewed.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
            fixture.Writer.Hold.TrySetResult();
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
            delivery.Completed.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Line_result_recording_is_concurrently_idempotent_and_rejects_conflicting_replay()
    {
        await using var fixture = await Fixture.Create(false);
        var metadata = await fixture.Accepted();
        var id = new BatchId(metadata.BatchId);
        var before = (await fixture.RealStore.GetAsync(id, default)).ShouldNotBeNull();
        (await fixture.RealStore.TryTransitionAsync(id, before.Version, new(BatchLifecycle.Processing, metadata.ReceivedAt), default)).ShouldBeTrue();
        var digest = CentralMobilidade.Telemetry.Domain.Processing.SafeLineDigest.Compute(Encoding.UTF8.GetBytes(RawLine), Enumerable.Repeat((byte)0x51, 32).ToArray()).Value;
        var outcome = new LineProcessingOutcome(1, LineOutcome.Accepted, null, new("company"), new("vehicle"), digest, metadata.ReceivedAt);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.RealStore.RecordLineResultsAsync(id, [outcome], default).AsTask()));
        var snapshot = (await fixture.RealStore.GetAsync(id, default)).ShouldNotBeNull();
        snapshot.Counters.Accepted.ShouldBe(1);
        await Should.ThrowAsync<ProviderOperationException>(() => fixture.RealStore.RecordLineResultsAsync(id, [outcome with { Outcome = LineOutcome.IdenticalReplay }], default).AsTask());
        await using var db = fixture.Database.Create();
        (await db.LineResults.CountAsync()).ShouldBe(1);
        (await db.LineResults.SingleAsync()).Outcome.ShouldBe(LineOutcome.Accepted);
    }

    [Fact]
    public async Task Valid_envelope_hash_cannot_hide_a_corrupt_gzip_trailer()
    {
        await using var fixture = await Fixture.Create(true);
        var original = await fixture.Accepted();
        var bytes = Gzip();
        bytes[^1] ^= 1;
        var metadata = original with
        {
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        };
        await using var delivery = new Delivery(metadata, fixture.Probe);
        delivery.Body.Position = 0;
        await delivery.Body.WriteAsync(bytes);
        delivery.Body.Position = 0;
        await fixture.Run(delivery);
        delivery.Completed.ShouldBeFalse();
        fixture.Writer.Calls.ShouldBe(0);
        await using var db = fixture.Database.Create();
        (await db.IdentityGuards.CountAsync()).ShouldBe(0);
        fixture.Probe.Events.ShouldContain("retry");
    }

    [Fact]
    public async Task Raw_quarantine_buffer_is_bounded_even_when_canonical_lines_are_much_smaller()
    {
        await using var fixture = await Fixture.Create(true, maxPoints: 10, maxBytes: 1500);
        var padded = RawLine.TrimEnd('\n') + new string(' ', 1000) + "\n";
        var raw = padded + padded.Replace("veiculo_id=vehicle", "veiculo_id=vehicle-2", StringComparison.Ordinal);
        var metadata = await fixture.Accepted(raw);
        await using var delivery = new Delivery(metadata, fixture.Probe, raw: raw);
        await fixture.Run(delivery);
        delivery.Completed.ShouldBeTrue();
        fixture.Writer.Calls.ShouldBe(2);
        (await fixture.RealStore.GetAsync(new(metadata.BatchId), default))!.Counters.ShouldBe(new BatchCounters(2, 0, 0, 2));
    }

    [Fact]
    public async Task Canonical_line_exceeding_write_budget_is_quarantined_before_reserving_identity()
    {
        await using var fixture = await Fixture.Create(true, maxBytes: 10);
        var metadata = await fixture.Accepted();
        await using var delivery = new Delivery(metadata, fixture.Probe);
        await fixture.Run(delivery);
        delivery.Completed.ShouldBeTrue();
        fixture.Writer.Calls.ShouldBe(0);
        (await fixture.RealStore.GetAsync(new(metadata.BatchId), default))!.Counters.ShouldBe(new BatchCounters(0, 1, 0, 1));
        await using var db = fixture.Database.Create();
        (await db.IdentityGuards.CountAsync()).ShouldBe(0);
        (await db.QuarantinedRejections.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Durable_provider_rejection_is_not_rewritten_after_schema_changes_on_redelivery()
    {
        await using var fixture = await Fixture.Create(true);
        fixture.Writer.RejectLine = 1;
        fixture.Probe.FailAt = "after-results";
        var raw = RawLine + RawLine.Replace("veiculo_id=vehicle", "veiculo_id=vehicle-2", StringComparison.Ordinal);
        var metadata = await fixture.Accepted(raw);
        await using var first = new Delivery(metadata, fixture.Probe, raw: raw);
        await fixture.Run(first);
        fixture.Probe.Triggered.ShouldBeTrue();
        first.Completed.ShouldBeFalse();
        fixture.Writer.RejectLine = null; // Provider would now accept the previously rejected payload.
        fixture.Probe.FailAt = null;
        await using var retry = new Delivery(metadata, fixture.Probe, 2, raw);
        await fixture.Run(retry);
        retry.Completed.ShouldBeTrue();
        fixture.Writer.Calls.ShouldBe(2);
        (await fixture.RealStore.GetAsync(new(metadata.BatchId), default))!.Counters.ShouldBe(new BatchCounters(1, 1, 0, 2));
        await using var db = fixture.Database.Create();
        (await db.IdentityGuards.CountAsync(r => r.State == IdentityGuardState.Committed)).ShouldBe(1);
        (await db.LineResults.SingleAsync(r => r.LineNumber == 1)).Outcome.ShouldBe(LineOutcome.Rejected);
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
        private Fixture(MigratedPostgresFixture database, Type guardType, Type? handlerType, bool legacyLookupConfigured, int maxPoints, bool shortLease, long maxBytes, bool shortAttempt)
        {
            Database = database;
            var collection = new ServiceCollection();
            collection.AddTelemetryControlPersistence(database.Connection);
            collection.AddLogging();
            collection.AddSingleton<IClock>(Clock);
            collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Processing:DiagnosticKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x51, 32).ToArray()),
                ["Processing:LeaseDuration"] = shortLease ? "00:00:02" : "00:05:00",
                ["Processing:RenewalInterval"] = shortLease ? "00:00:00.2" : "00:00:30",
                ["Processing:AttemptTimeout"] = shortAttempt ? "00:00:03" : "00:10:00",
                ["Cadastral:CompanyVisibilityUrl"] = legacyLookupConfigured ? "http://127.0.0.1:1/obsolete-company-lookup" : null
            }).Build());
            var options = new TelemetryOptions { WriteBatchMaxPoints = maxPoints, WriteBatchMaxBytes = maxBytes };
            collection.AddSingleton(options);
            collection.AddSingleton<IOptions<TelemetryOptions>>(Options.Create(options));
            collection.AddSingleton(TimeProvider.System);
            Writer = new(Probe);
            collection.AddSingleton<ITelemetryWriter>(Writer);
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
        internal static async Task<Fixture> Create(bool processing, bool legacyLookupConfigured = false, int maxPoints = 1, bool shortLease = false, long maxBytes = 2 * 1024 * 1024, bool shortAttempt = false)
        {
            var handlerType = typeof(AcceptTelemetryBatchHandler).Assembly.GetType("CentralMobilidade.Telemetry.Application.Processing.ProcessTelemetryBatchHandler");
            if (processing)
                handlerType.ShouldNotBeNull("T051 must implement the production processing/acknowledgement path");
            var guardType = typeof(PostgresBatchStateStore).Assembly.GetType("CentralMobilidade.Telemetry.Infrastructure.Idempotency.SelectedEventIdentityGuard");
            guardType.ShouldNotBeNull("T047 must implement the actual durable identity guard selected by ADR-001");
            var database = await MigratedPostgresFixture.StartAsync();
            try
            {
                return new(database, guardType, processing ? handlerType : null, legacyLookupConfigured, maxPoints, shortLease, maxBytes, shortAttempt);
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
        internal async Task Run(Delivery delivery, CancellationToken token = default)
        {
            // Every delivery/retry gets fresh processing and batching objects; durable state is external.
            var handler = ActivatorUtilities.CreateInstance(services, handlerType!);
            var method = handler.GetType().GetMethod("HandleAsync", [typeof(IQueueDelivery), typeof(CancellationToken)]).ShouldNotBeNull();
            try
            {
                var operation = method.Invoke(handler, [delivery, token]);
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
    }

    private sealed class StoreDecorator(IBatchStateStore inner, Probe probe) : IBatchStateStore
    {
        public ValueTask<LineProcessingOutcome?> GetLineResultAsync(BatchId id, int number, CancellationToken token) => inner.GetLineResultAsync(id, number, token);
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
        internal string? LastTraceId;
        internal bool ChangedOriginal;
        internal int? RejectLine;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Hold;
        public async ValueTask<TelemetryWriteResult> WriteAsync(TelemetryWriteRequest request, Stream body, CancellationToken token)
        {
            LastTraceId = request.TraceId;
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
                if (point.LineNumber == RejectLine)
                    continue;
                var original = Points.GetOrAdd(point.Identity, content);
                if (!original.AsSpan().SequenceEqual(content))
                    ChangedOriginal = true;
            }
            probe.Hit("after-influx");
            var rejected = request.Points.Where(p => p.LineNumber == RejectLine).Select(p => new TelemetryWriteRejection(p.BatchId, p.LineNumber, TelemetryErrorCodes.StorageSchemaConflict)).ToArray();
            return new(request.Points.Count - rejected.Length, rejected);
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
        private DateTimeOffset current = Now;
        internal bool UseSystem;
        public DateTimeOffset UtcNow
        {
            get => UseSystem ? DateTimeOffset.UtcNow : current; set => current = value;
        }
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
