# Structured privacy-safe logs (T021)

API, worker and the worker metrics listener use `PrivacyLoggingPolicy` through Serilog's host
integration. Default Microsoft console/debug/event-source providers are removed; there is one protected
JSON-lines output to stdout. The policy does not read arbitrary Serilog sink/enricher configuration
from settings. Application code uses DI `Microsoft.Extensions.Logging.ILogger<T>` or DI Serilog
`ILogger`; do not create an unfiltered logger, enable Serilog SelfLog to production stdout/stderr,
write request data directly to Console, or add a parallel unprotected provider/sink.

## Privacy boundary

Destructuring uses a bounded depth (4), collection size (16) and string length (256). Unknown complex
objects become `[REDACTED]` without reflection/getter access. A domain BatchId can become an opaque
UUID. Credentials, operator/driver objects, request bodies, payload bytes, coordinates, plate, name,
CPF and protected quarantine references are not destructured into log data.

Destructuring alone cannot protect interpolated message literals, plain scalar arguments, exceptions,
scopes or library messages. Therefore the final sink builds a new event containing only validated
metadata before JSON formatting. Original message templates/rendered messages are replaced with
`Operational event`, and exceptions are replaced by a closed category (canceled, timeout, io,
database, access_denied, invalid_argument, invalid_operation or unexpected). No exception message,
stack, Data, inner exception or type name is serialized. SourceContext is converted to a fixed
application/http/database/host category; original source strings are not retained.

This deliberately removes free-form diagnostic detail. Use the stable event/error codes below and
protected quarantine for investigation. Logs are operational metadata, not durable audit evidence.
The PostgreSQL audit writer and its fail-closed behavior are independent.

## Allowed metadata

| Input property | Output and validation |
| --- | --- |
| BatchId, QuarantineId | Nonempty UUID (including domain BatchId with destructuring) |
| TraceId, SpanId | Nonzero lowercase W3C hex of 32/16 characters |
| EventCode | Closed `OperationalLogCode` enum; defaults to ApplicationEvent |
| ErrorCode | Exact member of the v1 rejection or HTTP problem catalog |
| State | Closed BatchLifecycle enum |
| Accepted, Rejected, Replayed, LineNumber, RetryCount, CompressedBytes, DecompressedBytes, PointCount | Nonnegative int/long; output keys are normalized lowercase |
| StatusCode | Integer HTTP status 100–599 |
| ElapsedMilliseconds | Finite nonnegative double |

Unknown keys/values and nonscalar structures are removed. Input key spelling is case-insensitive and
accepts underscore/dot aliases, with duplicates collapsed to a single output property. Service and
Source are assigned by the policy and cannot be overridden by callers. Tenant, vehicle and driver
IDs are omitted by this initial allowlist; new correlations need an explicit reviewed policy change.
Neither syntactic validation nor logging grants authority: supply identifiers from trusted application
metadata and never disguise private values as counters, codes or opaque identifiers.

With an active Activity, TraceId/SpanId come from its W3C context and BatchId from a validated
`batch.id` tag. Activity names, baggage, other tags, URLs, query strings and headers are not copied.
Existing T020 propagation/trace sanitization and metric-label policy continue to apply separately.

Example for future processing code:

```csharp
using var scope = logger.BeginScope(new Dictionary<string, object?> { ["BatchId"] = batchId.Value });
logger.LogInformation("Batch outcome {EventCode} {State} {Accepted} {Rejected}",
    OperationalLogCode.BatchCompleted, BatchLifecycle.CompletedWithRejections, accepted, rejected);
```

The template is replaced at output; its validated operational properties remain. The minimum level is
Information, with ASP.NET Core and EF Core overridden to Warning. Arbitrary Logging/Serilog settings
do not weaken the sink policy. The writer is synchronized, flushes complete JSON lines and does not
keep an unbounded asynchronous event queue. The caller retains ownership of injected test writers.

## Startup and verification

Both entry points create a protected bootstrap logger before host configuration. Unexpected startup
or run failures emit ServiceFailure plus a safe exception category and exit with code 1, preventing
CLR's default unhandled-exception dump from exposing configuration values. Intentional HostAbortedException
from host tooling is preserved. Normal host shutdown still uses the framework lifetime.

On 2026-10-13 the complete Linux suite passed: 72 unit, 40 contract and 39 integration tests (151 total).
Nine logging cases cover literal/property/exception removal, unknown object getter protection, Activity
correlation, invalid metadata/aliases, ILogger scopes and real HTTP requests, concurrent JSON writes,
real API/worker startup failures with sanitized stdout/empty stderr, and WebApplicationFactory hosting.
Logging/observability test classes share an xUnit collection because OTel listeners/propagators are
process-global. PostgreSQL regression tests used an isolated 17.11 container. Windows build and
formatting verification pass; Windows Application Control remains unchanged and tests run in Linux.
These are historical T021 results, not a description of the current test suite.

## Loki ingestion policy (T083)

The [deployment guide](../../../deploy/loki/README.md) documents 30-day retention
and the OTLP attribute policy. Forward this sink's JSON as the log body, with only
a fixed deployment-controlled `service.name` (`api` or `worker`) as an index label.
Correlation IDs remain in the sanitized JSON, not in labels. Loki drops other
OTLP attributes but does not sanitize an unsafe body or validate every native push
label; restrict both routes to trusted senders. There is no automatic stdout
forwarder in this configuration. Real-Loki tests verify ingestion of this production
logger's sanitized output and exclusion of seeded sensitive OTLP attributes.
