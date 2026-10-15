# Bounded single-destination assembly — T050

## Mixed-line processing — T051

`ProcessTelemetryBatchHandler.HandleAsync(IQueueDelivery, CancellationToken)` composes
the parser, identity guard, batcher/writer, quarantine and batch store. It validates
the envelope and bounded compressed length/SHA-256, then verifies the gzip trailer and
decompression/line/count/ratio limits before storage side effects. The same borrowed
line reader is shared with intake through an Infrastructure compatibility facade.
Compressed staging is capped by MaxCompressedBytes; the decompressed body is never
materialized. A second bounded pass parses one line at a time. LF/CRLF framing is
removed by the reader; the rejected line bytes themselves go only to quarantine.

All syntactically valid company IDs go to the single writer without lookup. Valid
W3C envelope trace IDs establish the processing activity and propagate to write
batches even without an installed Activity listener; the only processing activity
tag added here is the opaque batch UUID. Valid
events are serialized in stable tag/field order with invariant numbers, escaped tags,
normalized plate, optional pseudonymous driver ID, millisecond timestamp and the
canonical event_fingerprint field. Provider payloads are generated from validated
values, not untrusted raw interpolation. Canonical and original pending bytes have
independent WriteBatchMaxBytes ceilings, plus a current bounded line. Owners are
bounded by WriteBatchMaxPoints. An event that cannot fit the configured write-byte
budget is quarantined before reserving its identity.

Reservations use stable `(BatchId, LineNumber)` ownership. They enter Writing before
batching, are renewed before provider IO and periodically during long writes, and
commit only for points whose reconciled provider result confirms persistence. Lease
loss cancels the attempt/provider IO and prevents ack; fencing remains authoritative
after ambiguous IO. Duplicate identities inside a buffered write force an earlier
flush so the later line observes a durable result rather than deadlocking on its
own reservation. Live contention requests a retry. No retry loop rewrites an identity
already observed committed.

Within the original 30-day evidence window, identical content is a replay and changed
content a conflict. Expired/missing evidence produces an unclassified rejection and
never grants a new write for a committed guard. The committed snapshot includes its
original owner: a resumed original line records Accepted, not a new replay count.
Retention covers at least the telemetry lifetime, independently of evidence expiry.

Rejections use a deterministic opaque quarantine ID from batch/line, stable ReceivedAt
and deletion deadline, verified content SHA-256 and an HMAC diagnostic digest. The
PostgreSQL result transaction locks the batch, stores protected quarantine metadata
and the immutable line result, and aggregates counters/company scope. Repeating an
identical result is safe; conflicting results roll back. On redelivery, a stored
line result with the same HMAC is reused before parsing/reservation/provider IO,
including permanent provider rejections if the schema changes between attempts.
Terminal state is entered
only after the persisted accepted/rejected/replayed counts cover the parsed total.
CompanyScopeComplete is set only for a completed batch whose stored lines all have
known company IDs. A terminal redelivery acknowledges without another provider write.

Configuration required by the T053 worker:

- `Processing:DiagnosticKey`: a persistent, protected Base64 key of at least 32 decoded
  bytes. Never use the synthetic test key. Retain a consistent key across processing
  retries/restarts; key generation/provisioning/rotation is a deployment responsibility.
- `Processing:LeaseDuration`: default five minutes, positive and at most ten minutes.
- `Processing:RenewalInterval`: default 30 seconds, positive and shorter than the lease.
- `Processing:AttemptTimeout`: default ten minutes, positive and at most one hour.
  This bounds the whole processing attempt and its second decompression pass rather
  than imposing the shorter intake upload deadline across database/provider work.
- Existing Telemetry, InfluxDb:Queryable, PostgreSQL and Quarantine settings still apply.

No raw data, driver identity, path or provider body is logged by this handler. Caller
cancellation propagates. Unconfirmed storage/gzip attempts remain unacknowledged and
request retry; bounded backoff and poison routing are implemented by T052, with
host wiring and shutdown in T053. There is no retry/ack on disposal of the delivery. T051 does not start a
RabbitMQ consumer, prove process/power-loss recovery, or approve the ADR-001 throughput
gate. Rejection paging remains the batch-inspection task; the existing paging stub
is not used by processing.

Validation includes the 21 original processing boundary scenarios, cancellation,
partial rejection, concurrent result replay, gzip integrity, raw/canonical buffer
limits and lease renewal/loss. A composition test uses real PostgreSQL, InfluxDB and
filesystem quarantine and checks terminal state before completion. It uses a controlled
delivery, not a live broker consumer. See the Recovery test documentation for counts.

## Assembly API

`InfluxWriteBatcher(ITelemetryWriter, TelemetryOptions, TimeProvider)` accepts one
validated UTF-8 Line Protocol line, including its final LF, and its original
`TelemetryWritePoint` owner through `AddAsync`. The parser remains responsible for
schema/UTF-8 validation; the batcher rejects oversized, incomplete or multiline input
and duplicate owners within a write. It never consults a company registry or selects
a pending destination.

The first threshold closes the batch: 5,000 points, 2 MiB of **uncompressed** bytes
(including LF), or one second measured from the oldest point with the supplied
TimeProvider's monotonic clock. Adding a line that would exceed the byte ceiling first
flushes existing points; a single line exceeding the configured byte/line ceiling is
rejected. Count and exact-byte thresholds flush immediately. An idle batch flushes
without another Add. A queued old timer callback checks batch age before flushing a
newer batch.

Input is streamed into gzip as Add executes, so caller memory can be reused after
Add returns. The batcher owns one compressed buffer, a bounded ordered owner list and
one in-flight write, without a queue of detached batches or accumulated outcomes.
The gzip footer is finalized and the stream rewound before `ITelemetryWriter` runs.
The stream is disposed after its result is handled. The first point's ambient W3C
trace ID is propagated, or a fresh valid ID is generated. New Add/Flush operations
await an in-flight write and its outcome handler, providing backpressure; callers
must also bound their own concurrent producers.

The four-argument constructor accepts an awaited callback:

```csharp
Func<TelemetryWriteRequest, TelemetryWriteResult, CancellationToken, ValueTask>
```

It receives the immutable ordered owner snapshot and reconciled result for **every**
count/byte/time/manual/disposal flush. Counts must cover the batch and rejection
owners must be unique members of that batch. T051 must record committed guards and
rejections through this callback (or an outcome-aware writer decorator) before
acknowledging deliveries. The three-argument constructor has no outcome callback;
assembly completion alone is not proof of batch terminal state or queue ack.
Callbacks must not reenter this batcher while its write is in progress.

`FlushAsync` drains a partial batch; empty flushes perform no IO. `DisposeAsync` stops
the timer and drains remaining points, awaiting completion and releasing owned memory;
repeated disposal shares the same completion. Cancellation while waiting to Add does
not consume a point or damage the current batch. Writer/callback failures or cancellation
during assembly/write fault the batcher: future Add/Flush/Dispose observe the failure,
including background timer failures. It never automatically retries an ambiguous write.
T051 creates a new processing attempt and recovers through the durable identity guard.
Writer deadlines govern in-flight shutdown; this class does not dispose the injected
writer or acknowledge queue messages.

Validation: the original three T038 threshold cases and 12 additional deterministic
filesystem-free cases pass (byte overflow, drain, buffer reuse, empty flush, oldest-point
timer, backpressure/cancellation, background failure, partial result callback, invalid
outcomes and invalid input). A separate real InfluxDB test composes this batcher with
the native writer and queries both company IDs from the single database. This proves
assembly/provider composition, not T051 orchestration, queue ack or sustained capacity.
The ADR-001 throughput gate remains separate.
