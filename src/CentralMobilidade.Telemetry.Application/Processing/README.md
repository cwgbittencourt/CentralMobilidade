# Bounded single-destination assembly — T050

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
