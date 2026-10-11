# Implementation Plan: Vehicle Telemetry Ingestion

**Branch**: `001-telemetry-ingestion` | **Date**: 2026-10-11 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-telemetry-ingestion/spec.md`

## Summary

Build a .NET 10 ingestion gateway and worker that accept bounded gzip Line Protocol streams, publish
the original compressed batch to a durable provider only with broker confirmation, validate and write
valid points asynchronously to InfluxDB 3 Core, and expose durable batch status from PostgreSQL.
RabbitMQ is the initial runtime provider; Google Pub/Sub uses the same queue port and is verified
locally against its emulator. Docker Compose supplies InfluxDB, PostgreSQL, RabbitMQ, Grafana,
Prometheus, and Loki for the initial local deployment. The design is at-least-once and makes all
storage operations idempotent.

## Technical Context

**Language/Version**: C# 14 on .NET 10 LTS

**Primary Dependencies**: ASP.NET Core 10 Minimal APIs; `System.IO.Pipelines`; RabbitMQ .NET client;
Google Cloud Pub/Sub client; Npgsql with EF Core 10 for control-plane persistence; `HttpClient` for
InfluxDB v3 writes; OpenTelemetry; Serilog; FluentValidation; Testcontainers; xUnit; NBomber or k6
for load tests

**Storage**: InfluxDB 3 Core for telemetry; PostgreSQL for batch state, rejection metadata,
idempotency/conflict evidence, configuration, and audit; RabbitMQ durable messages initially;
filesystem-backed quarantine initially; Google Cloud Storage as the future durable object store

**Testing**: xUnit unit, contract, component, and integration tests; Testcontainers for PostgreSQL,
RabbitMQ, and InfluxDB; Pub/Sub emulator contract suite; k6/NBomber load, burst, soak, and recovery tests

**Target Platform**: Linux containers on Docker Compose initially; portable to Google Cloud containers
and Google Pub/Sub; x64 and ARM64 images where dependencies support both

**Project Type**: Containerized web API plus background worker with shared domain/application libraries

**Performance Goals**: Sustain 5,000 telemetry events/second; absorb representative offline-device
reconnection bursts; make 99% of acknowledged normal-load events queryable within five minutes;
return 95% of batch-status reads within two seconds

**Constraints**: Gzip-only ingestion; 5,000-point Influx write default; bounded streaming; seven-day
queue retention; 30-day status and quarantine retention; 60-day local telemetry retention; five-year
future production retention; InfluxDB 3 Core is single-node; initial Docker Compose host has no
physical-loss guarantee; no PII in telemetry observability

**Scale/Scope**: 100 companies; initial 10,000 and target 100,000 vehicles; normal interval 20 seconds;
single internal producer; three initial business dashboards; internal users only

## Constitution Check

*GATE: Passed before Phase 0 and re-checked after Phase 1.*

| Principle | Design evidence | Result |
|---|---|---|
| Durable acceptance | Publisher confirm is required before `202`; consumer ack follows durable outcome | PASS |
| Bounded streaming | Separate compressed/decompressed/line/count/time limits; no whole-body string | PASS |
| Privacy and isolation | Pseudonymous IDs, cadastral adapter, quarantine isolation, audited identity lookup | PASS |
| Versioned/idempotent contracts | Versioned endpoint/schema, batch envelope, replay and conflict rules | PASS, with performance gate |
| Observable/testable/portable | OpenTelemetry, queue/cadastral ports, contract and recovery test matrices | PASS |

The exact conflict-detection strategy at target scale remains a mandatory benchmark gate. The plan
does not assume that a multi-billion-row relational deduplication ledger is viable. Phase 1 therefore
defines a pluggable conflict detector and a correctness/performance experiment before selecting the
production retention window beyond the guaranteed 30-day operational window.

## Architecture

```text
Internal telemetry center
        |
        | POST gzip Line Protocol
        v
Telemetry.Api
  - separate producer and operator bearer authentication
  - operator permission and company-scope authorization
  - compressed request limits and streaming spool
  - create batch status
  - durable publish + confirmation
        |
        +--------------------> PostgreSQL (batch lifecycle/audit/config)
        |
        v
IDurableBatchQueue
  - RabbitMQ provider (initial)
  - Pub/Sub provider (future/emulator tests)
        |
        v
Telemetry.Worker
  - bounded gzip decompression
  - incremental Line Protocol validation
  - tenant/vehicle/timestamp identity and fingerprint
  - retention-lifetime immutable identity reservation
  - exact duplicate/conflict decision inside the 30-day evidence window
  - company visibility decision before queryable/pending routing
  - groups <= 5,000 points by bytes/time/count
        |
        +--------------------> protected rejection quarantine
        +--------------------> InfluxDB 3 Core /api/v3/write_lp
        +--------------------> PostgreSQL batch results

Privileged internal Grafana org --> InfluxDB SQL/FlightSQL (all known companies)
Restricted viewer Grafana orgs --> Infinity datasource --> bounded presentation API
Presentation API --> InfluxDB + cadastral adapter (scoped telemetry and masked identity)
Prometheus/Loki <-- metrics and privacy-safe structured logs
```

Known-company events are written to the queryable telemetry database. Unknown-company events are
written to a separate pending database whose credentials are never provisioned to Grafana. A promotion
worker periodically resolves company visibility and idempotently copies eligible points to the
queryable database before deleting them from pending storage. This storage boundary, rather than a
dashboard variable, enforces invisibility.

The producer credential is accepted only for submission and status of batches submitted by that
producer. Internal operator credentials are stored separately and carry explicit action permissions and
allowed-company scopes. Raw quarantine and driver identity require operator-only permissions; they fail
closed if the append-only audit event cannot be persisted. Batch and rejection queries disclose a batch
to an operator only when every company in it is allowed, unless the operator has explicit
`cross_company.read` permission.

Grafana uses a hybrid trust model. The privileged internal organization is not shared with viewers and
keeps the direct InfluxDB datasource for maximum query flexibility. Each restricted viewer organization
has no InfluxDB datasource and receives only an Infinity JSON datasource configured with a distinct
`ViewerPresentationServiceCredential` whose company scopes exactly match that organization. The API exposes only
bounded latest-position, single-vehicle-trip, ingestion-health, and driver-presentation queries; it does
not accept SQL or a caller-supplied company authorization list. Organization and datasource provisioning
is automated so adding companies does not require hand-editing dashboard security.

### Processing and acknowledgement boundaries

1. The API authenticates the producer, applies compressed-body limits, streams the original gzip body
   to the queue publisher, and creates a pending batch record.
2. The queue provider confirms durable acceptance. Only then may the API mark the batch `Accepted` and
   return `202`. If the response is lost, line-level idempotency makes producer retry safe.
3. The worker resolves company visibility, routes the event to queryable or pending storage, and uses
   manual consumer acknowledgement. It acknowledges only after telemetry writes,
   rejection quarantine, and batch outcome are durably recorded.
4. A transient failure negatively acknowledges/retries with backoff. A permanent batch failure records
   a terminal state and routes protected material to quarantine/dead-letter handling.
5. The worker may receive the same queue message repeatedly. Every state transition and write path is
   conditional and replay-safe.
6. Before an Influx write, the worker atomically acquires or resumes a leased logical-identity reservation
   owned by the batch/line. The state advances `Reserved -> Writing -> Committed`; only `Committed`
   permanently blocks writes. The owner may resume an incomplete reservation, and a competing consumer
   may take over only after lease expiry using fencing/version checks. Fingerprint evidence retained for
   30 days provides exact replay/conflict classification. Because PostgreSQL and InfluxDB do not share a
   transaction, an ambiguous failure may repeat the identical physical Influx write; the guarantee is
   exactly one immutable logical event, not exactly one network write.
7. Promotion records use `Discovered -> Copying -> Copied -> SourceDeleted -> Completed`. Queryable
   writes are idempotent; pending source data is deleted only after the destination write is confirmed
   and its identity/count reconciliation succeeds. The identity guard location advances
   `PendingCommitted -> Promoting -> QueryableCommitted`; this authorized relocation is fenced and is not
   treated as a duplicate. Recovery resumes safely from every state.

Viewer query limits default to 500 vehicles per latest-position page; one vehicle, 24 hours, and 10,000
points per trip; and seven days, one-minute minimum granularity, and 10,000 points for ingestion health.
All limits are startup-validated configuration and may be reduced per deployment.

The internal default organization, direct InfluxDB datasource, and internal dashboards use startup YAML
provisioning. After Grafana is healthy, a protected `GrafanaBootstrapAdminCredential` authenticates with
Basic authentication only to reconcile server-level organizations and memberships. The bootstrap flow
then creates or rotates a least-privilege `GrafanaOrganizationServiceAccount` inside each organization;
that organization-scoped token reconciles its Infinity datasource and viewer dashboards. Each Infinity
datasource stores a separate scoped `ViewerPresentationServiceCredential` for the .NET presentation API.
Neither Grafana credential is ever sent to the presentation API, and the bootstrap credential is never
stored in a datasource. MVP audit records identify the viewer organization and presentation credential,
not an individual human. The Docker image pins compatible Grafana OSS and signed Infinity plugin
versions; individual attribution requires a future trusted identity propagation design.

### Initial configurable safety limits

Defaults are hypotheses to validate, not advertised capacity guarantees:

| Limit | Initial value | Reason |
|---|---:|---|
| Compressed HTTP body | 8 MiB | Queue providers carry these bytes natively, below Pub/Sub's 10 MB ceiling |
| Decompressed body | 64 MiB | Bounds gzip expansion and worker memory/disk pressure |
| Expansion ratio | 100:1 | Rejects suspicious compression bombs even below absolute limit |
| Line length | 16 KiB | Prevents pathological single-record allocation |
| Lines per ingestion batch | 50,000 | Allows upload bursts while bounding validation work |
| Influx write points | 5,000 | User-selected normal write batch ceiling |
| Influx write bytes | 2 MiB | Avoids count-only oversized writes |
| Influx flush interval | 1 second | Keeps normal query availability well under five minutes |
| API upload duration | 30 seconds | Limits slow-body resource occupancy |

All limits are strongly typed configuration, validated at startup, surfaced in diagnostics, and tested
at boundary values.

## Project Structure

### Documentation (this feature)

```text
specs/001-telemetry-ingestion/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── checklists/
│   └── requirements.md
└── contracts/
    ├── openapi.yaml
    ├── line-protocol-v1.md
    └── queue-envelope.schema.json
```

### Source Code (repository root)

```text
CentralMobilidade.slnx
Directory.Build.props
Directory.Packages.props
src/
├── CentralMobilidade.Telemetry.Api/
├── CentralMobilidade.Telemetry.Worker/
├── CentralMobilidade.Telemetry.Domain/
├── CentralMobilidade.Telemetry.Application/
├── CentralMobilidade.Telemetry.Infrastructure/
└── CentralMobilidade.Telemetry.Contracts/
tests/
├── CentralMobilidade.Telemetry.UnitTests/
├── CentralMobilidade.Telemetry.ContractTests/
├── CentralMobilidade.Telemetry.IntegrationTests/
├── CentralMobilidade.Telemetry.ArchitectureTests/
└── CentralMobilidade.Telemetry.LoadTests/
deploy/
├── compose.yaml
├── compose.override.yaml
├── containers/
├── grafana/
├── prometheus/
└── loki/
```

**Structure Decision**: Separate API and worker processes scale and fail independently. Domain and
application projects contain provider-neutral rules; infrastructure owns RabbitMQ, Pub/Sub,
PostgreSQL, InfluxDB, quarantine, and cadastral adapters. Contracts remain independently testable.
This is intentional separation, not a microservice split: API and worker share one bounded context and
one versioned deployment release.

Test projects are organized by verification type rather than creating one test assembly for every
production assembly: Domain/Application behavior maps primarily to UnitTests; Contracts to
ContractTests; API/Worker/Infrastructure behavior to IntegrationTests; dependency rules to
ArchitectureTests; and throughput/end-to-end acceptance to LoadTests. Every production change still
requires one or more traceable automated tests in the appropriate project.

Queue providers MUST transmit the original gzip payload as native message-body bytes. Version,
`batchId`, trace, producer, size, and digest are provider headers/attributes described by the envelope
metadata schema; the payload MUST NOT be JSON/Base64 wrapped on the wire.

## Delivery Phases

1. **Foundation**: solution, central package management, configuration validation, health endpoints,
   container builds, local composition, secrets templates, and baseline telemetry.
2. **Durable intake**: authentication, bounded gzip upload, batch persistence, RabbitMQ publisher
   confirms, status endpoint, and restart tests.
3. **Validation and storage**: streaming parser, schema v1, partial results, Influx v3 writer,
   pending/queryable tenant separation, promotion worker, quarantine, retry policy, and manual queue
   acknowledgement.
4. **Idempotency experiment**: benchmark alternative conflict detectors using representative 10k and
   100k vehicle data; record an ADR and implement the selected bounded strategy.
5. **Presentation and privacy**: tenant visibility check, cadastral adapter, masked driver view, audit,
   and initial Grafana dashboards.
6. **Operational readiness**: Prometheus, Loki, alerts, retention jobs, backup documentation, burst,
   soak, dependency-failure, and recovery testing.
7. **Cloud portability**: Pub/Sub emulator contract parity, real Pub/Sub configuration template, GCS
   migration design, and documented queue drain/cutover procedure.

## Verification Strategy

- Contract tests exercise the same durable-queue suite against RabbitMQ and the Pub/Sub emulator.
- Integration tests kill API/worker containers at every acknowledgement boundary and verify recovery.
- Golden Line Protocol fixtures cover escaping, types, timestamps, UTF-8, newlines, and mixed errors.
- Property/fuzz tests exercise gzip truncation, expansion, line boundaries, and parser state.
- Load tests model steady 5,000 events/s, offline bursts, consumer slowdown, and InfluxDB outage.
- The burst gate models 10,000 vehicles sending one hour of accumulated 20-second events over ten
  minutes while the normal 5,000-event/s flow continues; the backlog must drain within 20 minutes.
- The 100,000-vehicle reference gate uses 8 application vCPUs and 16 GiB RAM, with sustained CPU no
  greater than 85% and application memory no greater than 12 GiB.
- The final release proof generates exactly 100,000 valid events through the external gzip endpoint,
  reconciles accepted and stored totals to exactly 100,000, and captures Grafana query/render evidence.
- Soak tests measure disk growth and backlog age long enough to extrapolate 60-day and five-year needs.
- Privacy tests scan logs, metrics, and traces for seeded plates, coordinates, names, CPF, and secrets.
- Dashboard tests verify unknown tenants remain hidden and driver disclosure is audited.

## Post-Design Constitution Check

Phase 1 contracts preserve durable acknowledgement, bounded streaming, privacy separation, versioned
schemas, provider portability, and measurable tests. No constitution violation is accepted. The
immutable identity-guard benchmark is a release gate, not an exception.

## Complexity Tracking

No constitution violations require justification. Separate API and worker containers are necessary
to make the durability boundary and independent scaling explicit; they remain one bounded context.
