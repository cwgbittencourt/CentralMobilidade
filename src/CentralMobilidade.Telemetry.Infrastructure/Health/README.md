# Health checks (T022)

API and worker register `AddTelemetryHealthChecks`. The API maps health routes on its existing port;
the worker shares its private metrics listener (9464). Endpoints accept GET/HEAD and return only
`{"status":"healthy"}` (200) or `{"status":"unhealthy"}` (503), with no-store caching. Degraded status
also maps to 503. Dependency names, addresses, credentials, response bodies, exception text and heartbeat
timestamps are not disclosed. Keep these operational endpoints internal; they grant no ingestion or
administrative permissions. ASP.NET Core health/metrics requests are excluded from API tracing.

| Route | API | Worker |
| --- | --- | --- |
| `/health/live` | HTTP process responds; no dependency probes | Same |
| `/health/ready` | PostgreSQL + actual durable publication capability | PostgreSQL + queryable Influx connectivity + consumer freshness |
| `/health/dependencies` | PostgreSQL, publication, queryable Influx, remote worker freshness | PostgreSQL, publication, queryable Influx, local freshness |
| `/health/worker` | Not mapped | Local consumer freshness only |

Downstream storage/worker failure must not turn API intake readiness unhealthy when PostgreSQL and
durable publishing remain available: the queue can buffer acknowledged batches. Conversely, worker
storage failures prevent worker readiness, while its liveness remains independent. Health status is
not permission to acknowledge a batch: actual per-operation confirmations/commits remain mandatory.

## Dependencies and configuration

`Health:ProbeTimeout` defaults to three seconds (100 ms–30 seconds allowed). `Health:WorkerMaxAge`
defaults to 30 seconds (1 second–10 minutes allowed). API `WorkerHealthUrl` is configured as
`http://worker:9464`, an HTTP(S) origin without embedded credentials, query or fragment. Unknown health
configuration keys fail startup registration. Missing dependency configuration produces unhealthy
results without preventing the process from serving liveness. Endpoint/token rotation requires a
restart; health registration uses startup configuration snapshots.

PostgreSQL uses the configured EF context factory and a read-only probe query. It verifies that the
seven current control tables are visible, the session is not read-only/recovery, fsync is enabled, synchronous
commit is not off, and batch/audit read/write privileges required for intake are present. It does not
create/migrate tables, insert rows, mutate batch state or require access to migration history. This is
not a full schema/constraint verification or proof of every processing privilege. Apply T019 migrations
and the deployment grant policy separately. Local durability does not imply protection from host loss.

`IDurablePublicationProbe` is an adapter hook for the actual ingestion route. Its four flags require
connection, durable route, confirmation capability and publish permission. A management/TCP ping,
another queue/topic or a connected non-confirming channel cannot report ready. Missing/failed/timed-out
probes fail closed. Queue adapters in T027/T080 and provider registration in T081 must register their
real implementation, honoring cancellation and validating the actual route. This task does not create
a placeholder publisher or publish health batches into the business queue. Until that implementation
is registered, default API readiness is 503 even when the RabbitMQ container is running.

Influx checks require configured URL/token and perform authenticated GET `/health` with bounded
timeouts, redirects/cookies disabled and response bodies ignored. API checks queryable storage;
worker checks the same single queryable store. Legacy Pending settings are ignored. This verifies server reachability, not token write permissions,
database existence, retention, capacity or successful Influx persistence. `/health` can be exempt
from authorization on the configured Influx server. Actual writer acceptance still governs queue ack.

The API fetches the worker's `/health/worker`, requiring HTTP 200 and the healthy JSON status. Response
reading is capped at 256 bytes; malformed, oversized, stale or failed responses are unhealthy. URLs
come from trusted startup configuration, never from request parameters. No unbounded body is parsed.

## Consumer integration

`WorkerProgress` uses TimeProvider's monotonic timestamp, not wall-clock time. It starts unhealthy.
The T053 consumer/recovery loop must call `MarkHeartbeat()` after real consumer progress/healthy idle
polling and `MarkUnavailable()` when disconnected or stopped. Host startup, a standalone timer and
health requests never refresh it. No consumer exists yet, so the current worker reports live but
not fresh/ready until T053 connects this hook. This is intentional fail-closed behavior rather than
claiming that a running metrics server is a working queue consumer.

## Evidence

Readiness correction on 2026-10-14: Testcontainers' PostgreSQL fixture defaulted to
`fsync=off`, so the production probe correctly returned unhealthy in the two healthy
API/worker scenarios. `MigratedPostgresFixture` now explicitly enables `fsync=on` and
`synchronous_commit=on`. All 18 health tests pass, including a regression check of
these settings. The production durability checks and private responses are unchanged.
Local evidence: `artifacts/validation/T022-readiness/T022-readiness.trx` (Git ignored).

Validation on 2026-10-13: 168 tests passed in Linux (72 unit, 40 contract, 56 integration), including
17 health cases. Coverage includes missing dependencies without liveness failure, healthy durable
intake while downstream is unavailable, real worker listener routes, consumer heartbeat expiry and
invalidation, PostgreSQL probes against an isolated migrated 17.11 database, read-only/nondurable
sessions, authenticated Influx requests, bounded worker replies, cancellation deadlines, degraded
status, private responses and invalid configuration. Influx/publication capability tests use controlled
handlers/probes; real RabbitMQ/PubSub/Influx adapter integration remains in their respective tasks.
Windows build and formatting verification pass; native test execution remains blocked by the existing
Windows Application Control policy and therefore runs in Linux. Named validation volumes are retained.
Compose health/restart hardening is T086. Architecture/load projects remain skeletons.

## Single-store amendment (T108, 2026-10-14)

API and worker now register only `influx_queryable`, reading `InfluxDb:Queryable:Url/Token`.
No pending storage or company registry is probed. PostgreSQL readiness expects the seven current
control tables after the guarded T106 migration, without `pending_telemetry_promotions`; read-only,
recovery, fsync, synchronous commit and intake privileges remain enforced. API readiness still
requires PostgreSQL and durable publication; worker readiness still requires PostgreSQL, Influx
connectivity and a fresh consumer heartbeat. HTTP health requests cannot refresh that heartbeat.

The worker template now contains only Queryable. The API template and writer registration already
use only that store and needed no source changes. Service DNS stays `influxdb-queryable` until T109
renames the Compose services together. `.env.example` removes obsolete pending settings and explains
the temporary Compose prerequisite; existing private files and volumes must be preserved.

Validation: the new registration regression first failed for the worker (two Influx checks instead
of one). All 37 directed health/security/intake scenarios then passed, including migrated real
PostgreSQL, worker ready/unready/recovery with Queryable only, expired consumer heartbeat, API durable
intake despite downstream outage, legacy Pending settings ignored, and privacy-safe responses.
The same shared producer credential accepts two company IDs absent from any registry; operator/viewer
types, scoped presentation and audited denials remain independent. The authentication route test uses
synthetic endpoints; the intake regression uses the production API with queue/state doubles and
proves acceptance, not end-to-end processing. Influx health uses controlled HTTP responses, not proof
of database/write permissions or persistence. T050/T051/T053 remain future work.

Evidence: `artifacts/validation/T108/t108-red.trx` and `t108-health-security-final.trx` (Git ignored).
An initial sandbox run failed five PostgreSQL cases because Docker's named pipe was denied; the
successful directed run used Docker access. No deployed database, Compose topology or volume was changed.
Unit 242/242, contract 58/58 and architecture 47/47 tests passed. The final sequential solution build
completed with zero warnings/errors after resolving a concurrent-test DLL copy lock.
