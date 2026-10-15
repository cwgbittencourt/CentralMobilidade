using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentralMobilidade.Telemetry.Application.Abstractions;
using CentralMobilidade.Telemetry.Application.Configuration;
using CentralMobilidade.Telemetry.Application.LineProtocol;
using CentralMobilidade.Telemetry.Application.Streaming;
using CentralMobilidade.Telemetry.Contracts.Errors;
using CentralMobilidade.Telemetry.Contracts.Messaging.V1;
using CentralMobilidade.Telemetry.Domain.Processing;
using CentralMobilidade.Telemetry.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CentralMobilidade.Telemetry.Application.Processing;

public sealed class ProcessTelemetryBatchHandler
{
    private static readonly ActivitySource Activities = new("CentralMobilidade.Telemetry");
    private readonly IBatchStateStore batches;
    private readonly IEventIdentityGuard guard;
    private readonly ITelemetryWriter writer;
    private readonly IQuarantineStore quarantine;
    private readonly TelemetryLineProtocolV1Parser parser;
    private readonly IClock clock;
    private readonly TelemetryOptions options;
    private readonly TimeProvider time;
    private readonly byte[] diagnosticKey;
    private readonly TimeSpan leaseDuration, renewalInterval, attemptTimeout;

    public ProcessTelemetryBatchHandler(IBatchStateStore batches, IEventIdentityGuard guard, ITelemetryWriter writer,
        IQuarantineStore quarantine, TelemetryLineProtocolV1Parser parser, IClock clock,
        IOptions<TelemetryOptions> options, TimeProvider timeProvider, IConfiguration configuration)
    {
        this.batches = batches;
        this.guard = guard;
        this.writer = writer;
        this.quarantine = quarantine;
        this.parser = parser;
        this.clock = clock;
        this.options = options.Value;
        time = timeProvider;
        try
        {
            diagnosticKey = Convert.FromBase64String(configuration["Processing:DiagnosticKey"] ?? "");
        }
        catch (FormatException) { throw new ArgumentException("A persistent diagnostic HMAC key is required."); }
        if (diagnosticKey.Length < 32)
            throw new ArgumentException("A persistent diagnostic HMAC key is required.");
        leaseDuration = configuration.GetValue("Processing:LeaseDuration", TimeSpan.FromMinutes(5));
        renewalInterval = configuration.GetValue("Processing:RenewalInterval", TimeSpan.FromSeconds(30));
        attemptTimeout = configuration.GetValue("Processing:AttemptTimeout", TimeSpan.FromMinutes(10));
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(10) || renewalInterval <= TimeSpan.Zero || renewalInterval >= leaseDuration ||
            attemptTimeout <= TimeSpan.Zero || attemptTimeout > TimeSpan.FromHours(1))
            throw new ArgumentException("Invalid identity lease intervals.");
    }

    public async ValueTask HandleAsync(IQueueDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.Metadata.Validate();
        if (!ActivityContext.TryParse($"00-{delivery.Metadata.TraceId}-{ActivitySpanId.CreateRandom()}-01", null, out var parent))
            throw new ProviderOperationException(TelemetryErrorCodes.RequestInvalid, ProviderFailureKind.Permanent);
        using var activity = Activities.StartActivity("process.batch", ActivityKind.Consumer, parent) ??
            new Activity("process.batch").SetIdFormat(ActivityIdFormat.W3C).SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags).Start();
        activity?.SetTag("telemetry.batch_id", delivery.Metadata.BatchId.ToString("D"));
        var id = new BatchId(delivery.Metadata.BatchId);
        try
        {
            var snapshot = await batches.GetAsync(id, cancellationToken) ?? throw Unconfirmed();
            if (snapshot.ProducerId != delivery.Metadata.ProducerId || snapshot.ReceivedAt != Microseconds(delivery.Metadata.ReceivedAt))
                throw Unconfirmed();
            if (Terminal(snapshot.State))
            {
                await delivery.CompleteAsync(cancellationToken);
                return;
            }
            if (snapshot.State is BatchLifecycle.Accepted or BatchLifecycle.Retrying)
            {
                if (!await batches.TryTransitionAsync(id, snapshot.Version, new(BatchLifecycle.Processing, Now(snapshot)), cancellationToken))
                    throw Unconfirmed();
            }
            else if (snapshot.State != BatchLifecycle.Processing)
                throw Unconfirmed();
            await Process(delivery, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is ProviderOperationException or IOException or InvalidDataException or OperationCanceledException)
        {
            // A failed/ambiguous attempt is never acknowledged. T052 owns bounded retry/poison policy.
            var snapshot = await batches.GetAsync(id, cancellationToken);
            if (snapshot?.State == BatchLifecycle.Processing)
                await batches.TryTransitionAsync(id, snapshot.Version, new(BatchLifecycle.Retrying, Now(snapshot)), cancellationToken);
            await delivery.RetryAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async ValueTask Process(IQueueDelivery delivery, CancellationToken token)
    {
        var metadata = delivery.Metadata;
        var id = new BatchId(metadata.BatchId);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        work.CancelAfter(attemptTimeout);
        using var input = await VerifyPayload(delivery, work.Token);
        var reader = new BoundedGzipLineReader();
        // Verify gzip trailer and all resource limits before exposing any storage side effects.
        await foreach (var ignored in reader.ReadAsync(input, metadata.CompressedBytes, options, work.Token))
        {
        }
        input.Position = 0;
        var pending = new ConcurrentDictionary<int, Pending>();
        long pendingRawBytes = 0;
        var readOptions = new TelemetryOptions
        {
            MaxCompressedBytes = options.MaxCompressedBytes,
            MaxDecompressedBytes = options.MaxDecompressedBytes,
            MaxExpansionRatio = options.MaxExpansionRatio,
            MaxLineBytes = options.MaxLineBytes,
            MaxLinesPerBatch = options.MaxLinesPerBatch,
            UploadTimeout = attemptTimeout
        };
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
        var renewal = RenewLeases(pending, work, renewalStop.Token);
        var scopedWriter = new ScopedWriter(writer, work.Token, async (request, ct) =>
        {
            foreach (var point in request.Points)
            {
                var item = pending[point.LineNumber];
                await item.Sync.WaitAsync(ct);
                try
                {
                    var now = clock.UtcNow;
                    item.Lease = await guard.RenewAsync(item.Lease, now, now.Add(leaseDuration), ct) ?? throw Unconfirmed();
                }
                finally { item.Sync.Release(); }
            }
        });
        var batchOptions = new TelemetryOptions
        {
            WriteBatchMaxPoints = options.WriteBatchMaxPoints,
            WriteBatchMaxBytes = options.WriteBatchMaxBytes,
            WriteBatchFlushInterval = options.WriteBatchFlushInterval,
            MaxLineBytes = checked(options.MaxLineBytes * 2 + 512)
        };
        long total = 0;
        try
        {
            await using (var batcher = new InfluxWriteBatcher(scopedWriter, batchOptions, time, async (request, result, ct) =>
            {
                using var completion = CancellationTokenSource.CreateLinkedTokenSource(work.Token, ct);
                ct = completion.Token;
                var rejected = result.Rejections.ToDictionary(r => r.LineNumber);
                foreach (var point in request.Points)
                {
                    var item = pending[point.LineNumber];
                    await item.Sync.WaitAsync(ct);
                    try
                    {
                        if (rejected.TryGetValue(point.LineNumber, out var rejection))
                            await Reject(metadata, point.LineNumber, item.Raw, item.Parsed, rejection.ErrorCode, ct);
                        else
                        {
                            if (!await guard.TryCommitAsync(item.Lease, clock.UtcNow, ct))
                            {
                                var committed = await guard.GetCommittedAsync(point.Identity, ct);
                                if (committed?.Owner != item.Lease.Owner)
                                    throw Unconfirmed();
                            }
                            await Record(metadata, point.LineNumber, item.Raw, item.Parsed, LineOutcome.Accepted, ct);
                        }
                        pending.TryRemove(point.LineNumber, out _);
                        Interlocked.Add(ref pendingRawBytes, -item.Raw.Length);
                    }
                    finally { item.Sync.Release(); }
                }
            }))
            {
                await foreach (var borrowed in reader.ReadAsync(input, metadata.CompressedBytes, readOptions, work.Token))
                {
                    var number = checked((int)++total);
                    var raw = borrowed.ToArray();
                    var previous = await batches.GetLineResultAsync(id, number, work.Token);
                    if (previous is not null)
                    {
                        if (previous.SafeDigest != SafeLineDigest.Compute(raw, diagnosticKey).Value)
                            throw Unconfirmed();
                        continue; // Includes permanent provider rejections: never resubmit a durable line outcome.
                    }
                    var parsed = parser.Parse(raw);
                    if (parsed.Event is not { } point)
                    {
                        await Reject(metadata, number, raw, null, parsed.ErrorCode!, work.Token);
                        continue;
                    }
                    // Duplicate identities inside one buffered batch must observe the earlier durable outcome.
                    if (pending.Values.Any(p => p.Parsed.Identity == point.Identity) ||
                        Interlocked.Read(ref pendingRawBytes) + raw.Length > options.WriteBatchMaxBytes)
                        await batcher.FlushAsync(work.Token);
                    var encoded = Encode(point);
                    if (encoded.Length > options.WriteBatchMaxBytes)
                    {
                        await Reject(metadata, number, raw, point, TelemetryErrorCodes.StorageRejected, work.Token);
                        continue;
                    }
                    var now = clock.UtcNow;
                    var expires = point.Timestamp.Value.Add(options.LocalTelemetryRetention);
                    if (expires < now.Add(options.LocalTelemetryRetention))
                        expires = now.Add(options.LocalTelemetryRetention);
                    var owner = new LineOwner(id, number);
                    var reservation = await guard.AcquireOrResumeAsync(new(point.Identity, owner, point.Fingerprint, now,
                        now.Add(leaseDuration), expires, now.AddDays(30)), work.Token);
                    if (reservation is IdentityReservationResult.Contended)
                    {
                        await batcher.FlushAsync(work.Token);
                        throw Unconfirmed();
                    }
                    if (reservation is IdentityReservationResult.Existing existing)
                    {
                        if (existing.Outcome == ExistingIdentityOutcome.IdenticalReplay)
                        {
                            var committed = await guard.GetCommittedAsync(point.Identity, work.Token);
                            await Record(metadata, number, raw, point, committed?.Owner == owner ? LineOutcome.Accepted : LineOutcome.IdenticalReplay, work.Token);
                        }
                        else
                            await Reject(metadata, number, raw, point, existing.Outcome == ExistingIdentityOutcome.Conflict
                            ? TelemetryErrorCodes.DuplicateConflict : TelemetryErrorCodes.StorageRejected, work.Token);
                        continue;
                    }
                    var lease = ((IdentityReservationResult.Granted)reservation).Lease;
                    if (lease.State == IdentityGuardState.Reserved && !await guard.TryMarkWritingAsync(lease, now, work.Token))
                        throw Unconfirmed();
                    pending[number] = new(lease with
                    {
                        State = IdentityGuardState.Writing
                    }, point, raw);
                    Interlocked.Add(ref pendingRawBytes, raw.Length);
                    await batcher.AddAsync(new(id, number, point.Identity), encoded, work.Token);
                }
                await batcher.FlushAsync(work.Token);
            }
            if (work.IsCancellationRequested)
                throw Unconfirmed();
        }
        finally { renewalStop.Cancel(); await renewal; }
        var snapshot = await batches.GetAsync(id, token) ?? throw Unconfirmed();
        if (snapshot.Counters.Accepted + snapshot.Counters.Rejected + snapshot.Counters.Replayed != total)
            throw Unconfirmed();
        if (!Terminal(snapshot.State))
        {
            var state = snapshot.Counters.Rejected > 0 ? BatchLifecycle.CompletedWithRejections : BatchLifecycle.Completed;
            if (!await batches.TryTransitionAsync(id, snapshot.Version, new(state, Now(snapshot), snapshot.Counters with
            {
                Total = total
            }), token))
                throw Unconfirmed();
        }
        await delivery.CompleteAsync(token);
    }

    private async Task RenewLeases(ConcurrentDictionary<int, Pending> pending, CancellationTokenSource work, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(renewalInterval, time);
            while (await timer.WaitForNextTickAsync(token))
                foreach (var pair in pending)
                {
                    await pair.Value.Sync.WaitAsync(token);
                    try
                    {
                        if (!pending.ContainsKey(pair.Key))
                            continue;
                        var now = clock.UtcNow;
                        pair.Value.Lease = await guard.RenewAsync(pair.Value.Lease, now, now.Add(leaseDuration), token) ?? throw Unconfirmed();
                    }
                    finally { pair.Value.Sync.Release(); }
                }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { work.Cancel(); }
    }

    private async ValueTask<MemoryStream> VerifyPayload(IQueueDelivery delivery, CancellationToken token)
    {
        var result = new MemoryStream();
        try
        {
            if (delivery.Metadata.CompressedBytes > options.MaxCompressedBytes)
                throw Unconfirmed();
            var buffer = new byte[65536];
            int count;
            while ((count = await delivery.Body.ReadAsync(buffer, token)) != 0)
            {
                if (result.Length + count > delivery.Metadata.CompressedBytes)
                    throw Unconfirmed();
                await result.WriteAsync(buffer.AsMemory(0, count), token);
            }
            if (result.Length != delivery.Metadata.CompressedBytes ||
                Convert.ToHexStringLower(SHA256.HashData(result.GetBuffer().AsSpan(0, (int)result.Length))) != delivery.Metadata.PayloadSha256)
                throw Unconfirmed();
            result.Position = 0;
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private async ValueTask Reject(TelemetryBatchEnvelope metadata, int number, byte[] raw, ParsedTelemetryEvent? point, string code, CancellationToken token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"quarantine:v1:{metadata.BatchId:N}:{number}"));
        var quarantineId = new Guid(hash.AsSpan(0, 16));
        using var stream = new MemoryStream(raw, writable: false);
        var receipt = await quarantine.WriteAsync(new(quarantineId, new(metadata.BatchId), number, code,
            metadata.ReceivedAt, metadata.ReceivedAt.Add(options.QuarantineRetention)), stream, token);
        if (receipt.QuarantineId != quarantineId || receipt.ContentSha256 != Convert.ToHexStringLower(SHA256.HashData(raw)))
            throw Unconfirmed();
        var protectedContent = new QuarantineMetadata(quarantineId, new(metadata.BatchId), number, code, metadata.ReceivedAt,
            metadata.ReceivedAt.Add(options.QuarantineRetention), receipt.ContentSha256);
        await batches.RecordLineResultsAsync(new(metadata.BatchId), [Outcome(metadata, number, raw, point, LineOutcome.Rejected, code, quarantineId) with { ProtectedContent = protectedContent }], token);
    }
    private ValueTask Record(TelemetryBatchEnvelope metadata, int number, byte[] raw, ParsedTelemetryEvent? point, LineOutcome outcome, CancellationToken token) =>
        batches.RecordLineResultsAsync(new(metadata.BatchId), [Outcome(metadata, number, raw, point, outcome, null, null)], token);
    private LineProcessingOutcome Outcome(TelemetryBatchEnvelope metadata, int number, byte[] raw, ParsedTelemetryEvent? point, LineOutcome outcome, string? code, Guid? quarantineId) =>
        new(number, outcome, code, point?.CompanyId, point?.VehicleId, SafeLineDigest.Compute(raw, diagnosticKey).Value, metadata.ReceivedAt, quarantineId);
    private DateTimeOffset Now(BatchSnapshot snapshot) => clock.UtcNow < snapshot.ReceivedAt ? snapshot.ReceivedAt : clock.UtcNow;
    private static DateTimeOffset Microseconds(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    private static bool Terminal(BatchLifecycle state) => state is BatchLifecycle.Completed or BatchLifecycle.CompletedWithRejections;
    private static ProviderOperationException Unconfirmed() => new(TelemetryErrorCodes.StorageRejected, ProviderFailureKind.Ambiguous);

    private static byte[] Encode(ParsedTelemetryEvent point)
    {
        static string Tag(string value) => value.Replace("\\", "\\\\").Replace(" ", "\\ ").Replace(",", "\\,").Replace("=", "\\=");
        static string Number(double value) => (value == 0 ? 0d : value).ToString("R", CultureInfo.InvariantCulture);
        var tags = "telemetria,empresa_id=" + Tag(point.CompanyId.Value) + ",veiculo_id=" + Tag(point.VehicleId.Value);
        if (point.DriverId is not null)
            tags += ",motorista_id=" + Tag(point.DriverId.Value);
        var fields = $" latitude={Number(point.Latitude)},longitude={Number(point.Longitude)},velocidade={Number(point.Speed)},rpm={point.Rpm.ToString(CultureInfo.InvariantCulture)}i,ignicao={(point.Ignition ? "true" : "false")}";
        if (point.Plate is not null)
            fields += ",placa=\"" + point.Plate.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        fields += ",event_fingerprint=\"" + point.Fingerprint + "\" " + point.Timestamp.UnixMilliseconds.ToString(CultureInfo.InvariantCulture) + "\n";
        return Encoding.UTF8.GetBytes(tags + fields);
    }
    private sealed class Pending(IdentityLease lease, ParsedTelemetryEvent parsed, byte[] raw)
    {
        internal IdentityLease Lease = lease;
        internal readonly ParsedTelemetryEvent Parsed = parsed;
        internal readonly byte[] Raw = raw;
        internal readonly SemaphoreSlim Sync = new(1, 1);
    }
    private sealed class ScopedWriter(ITelemetryWriter inner, CancellationToken attempt,
        Func<TelemetryWriteRequest, CancellationToken, ValueTask> prepare) : ITelemetryWriter
    {
        public async ValueTask<TelemetryWriteResult> WriteAsync(TelemetryWriteRequest request, Stream body, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(attempt, token);
            await prepare(request, linked.Token);
            return await inner.WriteAsync(request, body, linked.Token);
        }
    }
}
