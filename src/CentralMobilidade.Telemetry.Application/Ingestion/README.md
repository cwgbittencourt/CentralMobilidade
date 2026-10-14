# Durable intake orchestration

`AcceptTelemetryBatchHandler` owns the acceptance ordering between the control-plane store and the
durable queue. It creates the replay-safe batch row, atomically enters `Publishing`, and awaits a
provider receipt for the same `BatchId`. Only that matching, UTC, non-backward receipt permits the
`Accepted` transition and a successful result.

The handler returns an already accepted replay without publishing a second message. Concurrent callers
may both publish while the shared batch is `Publishing`, but the queue payload and state creation use
the same `BatchId`, and the store's compare-and-swap makes acceptance idempotent. A provider failure,
request cancellation during publication, mismatched receipt, or failure to persist acceptance never
returns success. Publication failures are recorded best-effort without replacing the original safe
provider exception. The caller retains ownership of the bounded gzip stream.

HTTP validation, backpressure, response mapping, and endpoint registration are implemented by T034.
T035 completes the host-level middleware and provider composition.
