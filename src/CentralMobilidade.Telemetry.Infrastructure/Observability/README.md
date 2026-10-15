# Observability foundation (T020)

The API and worker register OpenTelemetry through `AddTelemetryObservability`. Resources use fixed
service names (`centralmobilidade.telemetry.api` / `centralmobilidade.telemetry.worker`) and do not
include environment-derived attributes. ASP.NET Core requests and outbound HttpClient calls are
instrumented; request headers, bodies and exception capture are not enabled. `/metrics` requests are
excluded from API traces. Runtime, HTTP and the application meter are exposed through Prometheus.

The API exposes `/metrics` on its existing port. The worker hosts a separate `/metrics` endpoint on
9464 and now stays running until shutdown. Its queue consumer is still T053. The class default binds
loopback; the container appsettings bind 0.0.0.0 on the private Docker network, without publishing a
worker host port. Keep scrape endpoints internal. Prometheus job provisioning follows in T082.

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
