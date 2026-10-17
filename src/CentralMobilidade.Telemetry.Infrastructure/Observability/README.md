# Observability foundation (T020)

The API and worker register OpenTelemetry through `AddTelemetryObservability`. Resources use fixed
service names (`centralmobilidade.telemetry.api` / `centralmobilidade.telemetry.worker`) and do not
include environment-derived attributes. ASP.NET Core requests and outbound HttpClient calls are
instrumented; request headers, bodies and exception capture are not enabled. `/metrics` requests are
excluded from API traces. Runtime, HTTP and the application meter are exposed through Prometheus.

The API exposes `/metrics` on its existing port. The worker hosts a separate `/metrics` endpoint on
9464 and stays running until shutdown, with the configured queue consumer/provider. The class default binds
loopback; the container appsettings bind 0.0.0.0 on the private Docker network, without publishing a
worker host port. Keep scrape endpoints internal. The T082 Prometheus composition supplies the jobs.

## Configuration

`Observability:TraceSamplingRatio` defaults to 1 and accepts finite values from 0 through 1.
Sampling is parent-based. `Observability:OtlpTraceEndpoint` defaults to null, so traces are not sent
until a collector is configured. Set it to a complete OTLP HTTP/Protobuf trace URL, for example
`http://otel-collector:4318/v1/traces`. HTTP and HTTPS are supported; embedded credentials, query strings
and fragments are rejected. No collector is silently added to Compose. Trace export uses the SDK's
bounded batch processor and a five-second exporter timeout; export failure does not participate in
batch durability or authorization decisions. `WorkerMetricsUrl` controls the worker's listen origin.
Unknown configuration keys and invalid values fail during startup registration. Logs are configured
separately by [the T021 privacy logging policy](PrivacyLogging.md).

## Correlation and privacy

Use the DI singleton `TelemetryObservability.StartBatchActivity(operation, batchId, parentContext)`
at the future ingestion/processing boundaries. Operation is a closed enum, batch ID is a
validated domain value, and an optional W3C parent context preserves an incoming trace. With an
active HTTP span, child batch spans inherit its trace. Producers and queue adapters must propagate
W3C context through their transport adapters; the final queue spans are implemented with those adapters.
Only TraceContext is configured as the propagator; baggage is not propagated.

`PrivacySpanProcessor` runs before export, removes all attributes except validated batch/trace IDs,
known operation names, standard HTTP methods and integer HTTP status codes. It replaces display
names with fixed operation/HTTP names, clears status descriptions, tracestate and baggage, and drops
spans containing events or links, whose attributes cannot be removed through Activity's public API.
This deliberately trades detailed exception/link diagnostics for privacy. Do not add exporters
before this processor or replace the resource/propagator/privacy policy. The behavior is verified with
both SDK capture and a real local OTLP HTTP collector. SDK export processors check Recorded before
export ([upstream processor source](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry/Trace/Processor/BatchActivityExportProcessor.cs)).

All dynamic metric labels are removed through a global view; exemplars are disabled so filtered tags
cannot leak through exemplar metadata. Batch, trace, tenant, vehicle, driver, plate, coordinates, CPF,
tokens, URLs and unknown label keys/values are never dimensions. Prometheus histogram bucket labels
and fixed service resource metadata remain. Metrics therefore aggregate HTTP status/method and runtime
generation dimensions too. Keep instrument names, descriptions and units fixed in code; never derive
them from requests. The application facade exposes aggregate batch/input counters and processing/write
histograms with nonnegative amounts. Specific lifecycle/queue/rejection metrics and their recording
sites are added in T062 and the queue/worker tasks. Foundation configuration does not imply that the
unfinished pipeline already records ingestion outcomes.

## Evidence

Validation on 2026-10-13: 142 Linux tests passed (72 unit, 40 contract, 30 integration), including ten
observability cases. They cover W3C parent propagation, batch correlation, attribute/status/baggage
sanitization, event suppression, seeded forbidden labels, real Prometheus scrapes, OTLP HTTP/Protobuf
payload privacy, worker endpoint shutdown and invalid configuration. PostgreSQL regression tests ran
against an isolated 17.11 container. Windows build and formatting checks pass; native test execution
remains blocked by Windows Application Control, which was preserved. Architecture/load projects remain
skeletons; no throughput or storage capacity claim follows from these tests.

## Batch state and rejection counters (T062)

`BatchMetrics` has its own `CentralMobilidade.Telemetry.Batches` meter, registered
once per DI container and included in the existing API/worker Prometheus exporters.
The global view continues to strip every metric tag and exemplars remain disabled.
No tenant, vehicle, batch, producer, driver, trace or arbitrary code is accepted as
a dimension. Instrument names come exclusively from the compiled lifecycle enum
and the fixed v1 rejection catalog; unknown codes/states create no instruments.

The Prometheus family prefixes are:

- `telemetry_batch_state_<state>`: entries committed into Receiving, Publishing,
  Accepted, Processing, Retrying, Completed, CompletedWithRejections, Rejected,
  PublishFailed, Failed and Expired (snake_case suffixes).
- `telemetry_rejections_<code>`: newly committed rejected lines, split into the
  16 existing rejection codes in lowercase. No code label is required.

T062 originally supplied 27 fixed instruments and no measurement labels. Persistence
records Receiving only when its INSERT creates the batch, state entries only when
the compare-and-swap updates one row, and rejected lines only after the line-page
transaction commits. Exact create/transition/line replays do not increment again;
conflicting/rolled-back pages contribute nothing. Reads never record state entries.
A failure in a measurement listener is isolated from already committed operations.

These are cumulative process counters of observed commits, not gauges of rows
currently in a state. A real retry/resume enters states again and is counted again.
Retention/deletion does not decrement them; no database scan or historical backfill
is performed at startup. They reset when the process/provider restarts. The gap
between database commit and in-process recording is not transactionally durable,
so a crash can omit an observation; these metrics are not an exactly-once ledger.
Durable metric history depends on Prometheus and its retention/backup configuration.

Validation uses MeterListener and a real Prometheus scrape with PostgreSQL to prove
closed instruments, empty tag sets, listener-failure isolation, eight concurrent
create/transition replays, a duplicate rejection, transactional rollback and
preservation of counters after batch metadata deletion. The process-global meter
integration collection is serialized to keep exact assertions independent of
other concurrently running exporters. No capacity claim follows from these checks.

## Platform dashboard signals (T084)

The [operations dashboard guide](../../../deploy/grafana/dashboards/platform-operations.md)
defines each panel's observation boundary. API intake now records received/gzip
bytes after the handler confirms durable acceptance. The worker records delivery
age (ReceivedAt to processor start, future-clock values clamped to zero) and whole
processor-call duration. The Influx writer records call duration and thrown failure
counts except caller cancellation. Explicit seconds-based histogram buckets cover
millisecond latencies and delivery ages through seven days, retaining empty tag
sets and disabled exemplars. Quantiles are estimates within those buckets.

`BatchMetrics` adds one fixed `telemetry.lines.identical_replay` counter, emitted
only for newly inserted IdenticalReplay results after commit. Repeated rows and
rolled-back pages do not increment it. The existing commit/emission crash gap and
process-reset limitations still apply. Observation listener exceptions are isolated
from acceptance, storage and worker error/retry decisions. No new dynamic labels,
domain provider branches, retention rules or managed-provider backlog probes are added.
