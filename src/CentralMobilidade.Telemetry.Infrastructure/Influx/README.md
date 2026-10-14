# InfluxDB v3 writer — T049

`InfluxV3TelemetryWriter(HttpClient, IConfiguration)` implements `ITelemetryWriter`.
Configure `InfluxDb:Queryable:{Url,Database,Token}` and
`InfluxDb:Pending:{Url,Database,Token}`. URLs must be HTTP(S) origins, without paths,
credentials, query strings or fragments. The resolved destinations must be distinct.
Tokens belong in protected configuration, never checked-in examples.

The writer streams the caller's existing gzip bytes without decompression or
recompression. It leaves the source open. Each POST uses `/api/v3/write_lp` with
`precision=millisecond`, `accept_partial=true` and `no_sync=false`, plus per-request
bearer authorization and the supplied trace ID. Limits follow
`Telemetry:WriteBatchMaxPoints` (default 5,000) and `Telemetry:WriteBatchMaxBytes`
(default 2 MiB). The bounded batcher is responsible for producing matching content
and ordered owners (T050); this adapter does not inflate the payload to recount it.

Only 204 confirms the complete write. A 400/422 partial response must contain the
documented error marker and a nonempty, unique list of one-based line numbers within
the submitted batch, each with a provider error. Each rejection maps back to its
original batch ID and line number. Column/field type conflicts become
`STORAGE_SCHEMA_CONFLICT`; other confirmed line errors become `STORAGE_REJECTED`.
The remaining point count is committed. Duplicate JSON properties/indices, invalid
JSON, missing information, unexpected status codes or an oversized response cannot
confirm persistence. All-rejected partial responses return zero committed points.

Partial-response parsing is bounded to 256 KiB and JSON depth 8, including responses
without Content-Length. A larger diagnostic body remains ambiguous; it is not silently
truncated or counted as successful. `InfluxDb:WriteTimeout` defaults to 30 seconds
(positive, at most five minutes) and bounds upload, headers and response parsing.
Transport/timeouts are ambiguous provider failures; caller cancellation propagates.
401/403/413 are permanent provider failures. Exceptions contain neither raw provider
text nor an inner exception. The writer performs no automatic retry: lease ownership
and replay reconciliation belong to T051.

`AddTelemetryStorageAdapters()` registers only the writer as a typed HTTP client with redirects, cookies and automatic decompression
disabled, all logged headers redacted, and connection pooling. Adapter deadlines own
timeouts. Worker wiring remains T053. Callers constructing adapters directly must
supply equally controlled HttpClient handlers.

Validation covers a real InfluxDB `3.11.6-core` container for persisted millisecond
timestamps and schema-conflict partial acceptance, plus HTTP request/response and
failure scenarios. These tests use memory storage and do not prove restart/WAL disk
recovery or physical-host durability. The timestamp assertion treats zone-less query
timestamps as UTC rather than interpreting them in the Windows local timezone.

Protocol source: [InfluxDB 3 Core write API](https://docs.influxdata.com/influxdb3/core/api/write-data/),
checked 2026-10-14. End-to-end persistence/ack recovery remains T051 and later recovery
tests; assembly of count/byte/time batches remains T050.

## T105 — application contract transition (2026-10-14)

`TelemetryWriteRequest` no longer has a destination parameter. All application writes
use Queryable; company existence is authoritative from the producer's valid `empresa_id`.
The obsolete company-visibility resolver/HTTP adapter and its registration were removed
with the application port. Driver resolution remains a separate port.

The constructor still validates the legacy Pending configuration shown above; removing
that configuration requirement is T107. PostgreSQL still retains its legacy schema until
T106. Do not infer that the single-container Compose transition has happened (T109).
