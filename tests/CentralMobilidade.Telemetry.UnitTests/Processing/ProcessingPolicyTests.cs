using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.LineProtocol;
using CentralMobilidade.Telemetry.Application.Processing;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CentralMobilidade.Telemetry.UnitTests.Processing;

public sealed class ProcessingPolicyTests
{
    [Theory]
    [InlineData(-45)]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Guard_covers_retained_telemetry_while_evidence_uses_processing_clock(int eventDays)
    {
        using var f = new Fixture(eventDays);
        await f.Handler.HandleAsync(f, default);
        var request = f.Reservations.Single();
        request.GuardExpiresAt.ShouldBe((eventDays > 0 ? f.UtcNow.AddDays(eventDays) : f.UtcNow).AddDays(60));
        request.EvidenceExpiresAt.ShouldBe(f.UtcNow.AddDays(30));
        request.Now.ShouldBe(f.UtcNow);
        request.Owner.BatchId.Value.ShouldBe(f.Metadata.BatchId);
        f.Results.Single().Outcome.ShouldBe(LineOutcome.Accepted);
        f.Writes.ShouldBe(1);
        f.Completed.ShouldBeTrue();
        f.Retried.ShouldBeFalse();
    }

    [Theory]
    [InlineData("never-registered")]
    [InlineData(" São-Paulo ")]
    [InlineData("company,=42")]
    public async Task Valid_producer_company_id_is_preserved_and_written_to_single_store(string company)
    {
        using var f = new Fixture(company: company);
        await f.Handler.HandleAsync(f, default);
        f.Reservations.Single().Identity.CompanyId.Value.ShouldBe(company);
        f.WrittenPoints.Single().Identity.CompanyId.Value.ShouldBe(company);
        f.Results.Single().CompanyId!.Value.ShouldBe(company);
        f.Completed.ShouldBeTrue();
        f.Writes.ShouldBe(1);
        f.Reservations.Single().Identity.VehicleId.Value.ShouldBe("vehicle");
    }

    [Theory]
    [InlineData(ExistingIdentityOutcome.IdenticalReplay, LineOutcome.IdenticalReplay, null)]
    [InlineData(ExistingIdentityOutcome.Conflict, LineOutcome.Rejected, TelemetryErrorCodes.DuplicateConflict)]
    [InlineData(ExistingIdentityOutcome.AlreadyExistsUnclassified, LineOutcome.Rejected, TelemetryErrorCodes.StorageRejected)]
    public async Task Existing_identity_never_rewrites_and_has_stable_outcome(ExistingIdentityOutcome existing, LineOutcome outcome, string? code)
    {
        using var f = new Fixture { Existing = existing };
        await f.Handler.HandleAsync(f, default);
        f.Writes.ShouldBe(0);
        f.Results.Single().Outcome.ShouldBe(outcome);
        f.Results.Single().ErrorCode.ShouldBe(code);
        f.Quarantines.ShouldBe(code is null ? 0 : 1);
        f.Completed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ProviderFailureKind.Transient)]
    [InlineData(ProviderFailureKind.Ambiguous)]
    public async Task Unconfirmed_storage_retries_without_commit_or_ack(ProviderFailureKind failure)
    {
        using var f = new Fixture { WriteFailure = failure };
        await f.Handler.HandleAsync(f, default);
        f.Commits.ShouldBe(0);
        f.Results.ShouldBeEmpty();
        f.Completed.ShouldBeFalse();
        f.Retried.ShouldBeTrue();
        f.Snapshot.State.ShouldBe(BatchLifecycle.Retrying);
    }

    private sealed class Fixture : IBatchStateStore, IEventIdentityGuard, ITelemetryWriter, IQuarantineStore, IQueueDelivery, IClock, IDisposable
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public TelemetryBatchEnvelope Metadata
        {
            get;
        }
        public Stream Body
        {
            get;
        }
        public int DeliveryAttempt => 1;
        public BatchSnapshot Snapshot;
        public List<IdentityReservationRequest> Reservations = [];
        public List<LineProcessingOutcome> Results = [];
        public List<TelemetryWritePoint> WrittenPoints = [];
        public ExistingIdentityOutcome? Existing;
        public ProviderFailureKind? WriteFailure;
        public int Writes, Commits, Quarantines;
        public bool Completed, Retried;
        public ProcessTelemetryBatchHandler Handler => new(this, this, this, this, new(), this,
            Options.Create(new TelemetryOptions()), TimeProvider.System,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Processing:DiagnosticKey"] = Convert.ToBase64String(new byte[32])
            }).Build());

        public Fixture(int eventDays = 0, string company = "unregistered")
        {
            static string Escape(string value) => value.Replace("\\", "\\\\").Replace(" ", "\\ ").Replace(",", "\\,").Replace("=", "\\=");
            var line = $"telemetria,empresa_id={Escape(company)},veiculo_id=vehicle latitude=1,longitude=2,velocidade=3,rpm=4i,ignicao=true {UtcNow.AddDays(eventDays).ToUnixTimeMilliseconds()}\n";
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
                gzip.Write(Encoding.UTF8.GetBytes(line));
            var bytes = buffer.ToArray();
            Body = new MemoryStream(bytes);
            Metadata = new("1", Guid.NewGuid(), "0123456789abcdef0123456789abcdef", "producer", UtcNow.AddDays(-2),
                "text/plain; charset=utf-8", "gzip", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "v1");
            Snapshot = new(new(Metadata.BatchId), Metadata.ProducerId, BatchLifecycle.Accepted, Metadata.ReceivedAt,
                Metadata.ReceivedAt.AddDays(30), null, null, new(0, 0, 0, null), new Dictionary<string, long>(), [], false, 1);
        }

        public ValueTask<BatchSnapshot?> GetAsync(BatchId id, CancellationToken token) => ValueTask.FromResult<BatchSnapshot?>(Snapshot);
        public ValueTask<LineProcessingOutcome?> GetLineResultAsync(BatchId id, int number, CancellationToken token) => ValueTask.FromResult<LineProcessingOutcome?>(null);
        public ValueTask<bool> TryTransitionAsync(BatchId id, long version, BatchTransition transition, CancellationToken token)
        {
            Snapshot = Snapshot with
            {
                State = transition.TargetState,
                Version = version + 1,
                Counters = transition.Counters ?? Snapshot.Counters
            };
            return ValueTask.FromResult(true);
        }
        public ValueTask RecordLineResultsAsync(BatchId id, IReadOnlyCollection<LineProcessingOutcome> results, CancellationToken token)
        {
            Results.AddRange(results);
            Snapshot = Snapshot with
            {
                Counters = new(Results.Count(x => x.Outcome == LineOutcome.Accepted), Results.Count(x => x.Outcome == LineOutcome.Rejected), Results.Count(x => x.Outcome == LineOutcome.IdenticalReplay), null)
            };
            return ValueTask.CompletedTask;
        }
        public ValueTask<IdentityReservationResult> AcquireOrResumeAsync(IdentityReservationRequest request, CancellationToken token)
        {
            Reservations.Add(request);
            return ValueTask.FromResult<IdentityReservationResult>(Existing is { } outcome
                ? new IdentityReservationResult.Existing(outcome)
                : new IdentityReservationResult.Granted(new(request.Identity, request.Owner, 1, request.LeaseUntil, IdentityGuardState.Reserved), false));
        }
        public ValueTask<CommittedIdentitySnapshot?> GetCommittedAsync(EventIdentity identity, CancellationToken token) => ValueTask.FromResult<CommittedIdentitySnapshot?>(null);
        public ValueTask<IdentityLease?> RenewAsync(IdentityLease lease, DateTimeOffset now, DateTimeOffset until, CancellationToken token) => ValueTask.FromResult<IdentityLease?>(lease with { LeaseUntil = until });
        public ValueTask<bool> TryMarkWritingAsync(IdentityLease lease, DateTimeOffset now, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<bool> TryCommitAsync(IdentityLease lease, DateTimeOffset now, CancellationToken token)
        {
            Commits++;
            return ValueTask.FromResult(true);
        }
        public ValueTask<TelemetryWriteResult> WriteAsync(TelemetryWriteRequest request, Stream body, CancellationToken token)
        {
            Writes++;
            WrittenPoints.AddRange(request.Points);
            if (WriteFailure is { } failure)
                throw new ProviderOperationException(TelemetryErrorCodes.StorageRejected, failure);
            return ValueTask.FromResult(new TelemetryWriteResult(request.Points.Count, []));
        }
        public async ValueTask<QuarantineReceipt> WriteAsync(QuarantineWriteRequest request, Stream content, CancellationToken token)
        {
            Quarantines++;
            return new(request.QuarantineId, Convert.ToHexStringLower(await SHA256.HashDataAsync(content, token)));
        }
        public ValueTask CompleteAsync(CancellationToken token)
        {
            Completed = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask RetryAsync(TimeSpan delay, CancellationToken token)
        {
            Retried = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask DeadLetterAsync(string code, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<BatchSnapshot> CreateAsync(TelemetryBatchEnvelope metadata, DateTimeOffset expires, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<RejectionResultPage> GetRejectionsAsync(BatchId id, string? cursor, int limit, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<QuarantineMetadata?> GetMetadataAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IQuarantineContent?> OpenReadAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<bool> DeleteExpiredAsync(Guid id, DateTimeOffset cutoff, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
        public void Dispose() => Body.Dispose();
    }
}
