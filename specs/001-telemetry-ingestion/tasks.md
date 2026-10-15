---
description: "Dependency-ordered implementation tasks for vehicle telemetry ingestion"
---

# Tasks: Vehicle Telemetry Ingestion

**Input**: Design documents from `/specs/001-telemetry-ingestion/`

**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

**Tests**: Required by the project constitution. Within each user story, test tasks precede production
implementation and must fail for the intended reason before implementation begins.

**Organization**: Tasks are grouped by user story so that each story has a demonstrable, independently
testable outcome.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel with adjacent tasks because it targets different files and does not
  depend on their unfinished output.
- **[Story]**: Maps the task to a user story in `spec.md`.
- Every task identifies its expected file path.

## Mandatory execution gate: foundation amendment 2026-10-14

Constitution 2.0.0 and FR-019 supersede company-existence lookup, unknown-company storage and
promotion. Completed tasks and their validation notes remain historical evidence, not the target
architecture. Complete **T104 -> T105 -> T106 -> T107 -> T108 -> T109 -> T110 -> T111 -> T112**
before starting **T050**, T051 or any later unfinished feature work. The numeric IDs preserve history;
they do not override this dependency order. Do not regenerate or renumber completed tasks.
One shared internal ingestion credential covers all company IDs. Operator/viewer identities and
presentation permissions remain separate; `empresa_id` remains the wire name of business `idEmpresa`.
The initial amendment was planning-only; T104-T112 validation notes below record subsequent source,
deployment and schema changes. Populated deployment reconciliation remains an explicit operator procedure.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the solution, repeatable container environment, central dependency management, and
test project skeletons.

- [X] T001 Create `CentralMobilidade.slnx` and add the six source projects and five test projects defined in `specs/001-telemetry-ingestion/plan.md`
- [X] T002 Create .NET 10 project files under `src/CentralMobilidade.Telemetry.Api/`, `src/CentralMobilidade.Telemetry.Worker/`, `src/CentralMobilidade.Telemetry.Domain/`, `src/CentralMobilidade.Telemetry.Application/`, `src/CentralMobilidade.Telemetry.Infrastructure/`, and `src/CentralMobilidade.Telemetry.Contracts/`
- [X] T003 Create test project files under `tests/CentralMobilidade.Telemetry.UnitTests/`, `tests/CentralMobilidade.Telemetry.ContractTests/`, `tests/CentralMobilidade.Telemetry.IntegrationTests/`, `tests/CentralMobilidade.Telemetry.ArchitectureTests/`, and `tests/CentralMobilidade.Telemetry.LoadTests/`
- [X] T004 [P] Configure nullable reference types, analyzers, warnings-as-errors, deterministic builds, and C# 14 in `Directory.Build.props`
- [X] T005 [P] Configure centrally pinned ASP.NET Core, EF Core, Npgsql, RabbitMQ, Pub/Sub, OpenTelemetry, Serilog, FluentValidation, xUnit, Testcontainers, and test assertion packages in `Directory.Packages.props`
- [X] T006 [P] Add repository formatting and line-ending rules in `.editorconfig`
- [X] T007 [P] Create multi-stage non-root container builds in `deploy/containers/api.Dockerfile` and `deploy/containers/worker.Dockerfile`
- [X] T008 Create persistent local service topology with named volumes for PostgreSQL, RabbitMQ, both queryable and pending InfluxDB databases, Prometheus, Loki, and quarantine; build Grafana from `deploy/grafana/Dockerfile` with explicitly pinned compatible Grafana OSS and signed Infinity plugin versions and no `latest` tags in `deploy/compose.yaml`
- [X] T009 [P] Create development-only overrides and optional application/Pub/Sub-emulator profiles in `deploy/compose.override.yaml`
- [X] T010 [P] Create secret-free configuration templates in `.env.example`, `src/CentralMobilidade.Telemetry.Api/appsettings.json`, and `src/CentralMobilidade.Telemetry.Worker/appsettings.json`

**Checkpoint**: The solution restores and the dependency containers can be started without application
functionality.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish contracts, configuration, persistence, observability, and provider ports used by
every user story.

**⚠️ CRITICAL**: No user story implementation begins until this phase passes its tests.

- [X] T011 Implement strongly typed, startup-validated limits for compressed bytes, decompressed bytes, expansion ratio, line length/count, upload duration, queue retention, write batch count/bytes/time, status/quarantine retention, and viewer defaults of 500 latest vehicles, 24-hour/10,000-point trip, and seven-day/one-minute/10,000-point health query in `src/CentralMobilidade.Telemetry.Application/Configuration/TelemetryOptions.cs`
- [X] T012 [P] Implement domain identifiers and UTC time value objects in `src/CentralMobilidade.Telemetry.Domain/ValueObjects/BatchId.cs`, `CompanyId.cs`, `VehicleId.cs`, `DriverId.cs`, and `EventTimestamp.cs`
- [X] T013 [P] Define stable problem details and rejection-code catalogs from `data-model.md` in `src/CentralMobilidade.Telemetry.Contracts/Errors/TelemetryErrorCodes.cs`
- [X] T014 [P] Define versioned request, batch-status, rejection-page, driver-presentation, latest-position, trip, and ingestion-health DTOs matching `contracts/openapi.yaml` in `src/CentralMobilidade.Telemetry.Contracts/Api/V1/`
- [X] T015 [P] Define the v1 queue metadata envelope matching `contracts/queue-envelope.schema.json`, keeping the original gzip payload as native provider body bytes rather than JSON/Base64, in `src/CentralMobilidade.Telemetry.Contracts/Messaging/V1/TelemetryBatchEnvelope.cs`
- [X] T016 Define provider ports for durable publishing/consumption, batch state, quarantine, atomic event-identity guarding, telemetry writes, company visibility, driver resolution, operator authorization, audit, and clock in `src/CentralMobilidade.Telemetry.Application/Abstractions/`
- [X] T017 Implement separate producer/operator opaque bearer authentication with hashed, rotatable human credentials and explicitly typed `ViewerPresentationServiceCredential` records, permissions, company scopes, credential-type isolation, privacy-safe failures, and an authorization-result handler that audits denied sensitive operations in `src/CentralMobilidade.Telemetry.Api/Security/ProducerBearerHandler.cs`, `OperatorBearerHandler.cs`, `OperatorAuthorizationPolicies.cs`, and `AuditedAuthorizationResultHandler.cs`
- [X] T018 Create EF Core control-plane context/entity configurations and the append-only audit writer for `IngestionBatch`, `LineProcessingResult`, `QuarantinedRejection`, recoverable leased `EventIdentityGuard`, `EventIdentityEvidence`, `OperatorAccessProfile`, `PendingTelemetryPromotion`, and `AuditEvent` in `src/CentralMobilidade.Telemetry.Infrastructure/Persistence/TelemetryControlDbContext.cs`, `Persistence/Configurations/`, and `Auditing/PostgresAuditWriter.cs`
- [X] T019 Create the initial PostgreSQL migration with atomic unique event-identity reservation state, owner/lease/fencing concurrency, telemetry-lifetime guard retention, 30-day fingerprint/status/quarantine deletion indexes, scoped human/service operator profiles, and optimistic concurrency in `src/CentralMobilidade.Telemetry.Infrastructure/Persistence/Migrations/`
- [X] T020 [P] Configure OpenTelemetry tracing/metrics, Prometheus export, trace/batch enrichment, and forbidden-label policy in `src/CentralMobilidade.Telemetry.Infrastructure/Observability/TelemetryObservability.cs`
- [X] T021 [P] Configure structured Serilog output with destructuring filters that remove payload, plate, coordinates, driver identity, CPF, and secrets in `src/CentralMobilidade.Telemetry.Infrastructure/Observability/PrivacyLoggingPolicy.cs`
- [X] T022 Implement liveness/readiness checks for PostgreSQL, durable queue publication, InfluxDB, and worker freshness in `src/CentralMobilidade.Telemetry.Infrastructure/Health/TelemetryHealthChecks.cs`
  - Readiness correction (2026-10-14): the Testcontainers fixture used `fsync=off`, correctly rejected by the production probe. Explicit `fsync=on` and `synchronous_commit=on` restore the healthy fixture; all 18 health tests pass, including a new durable-settings regression test. The two readiness failures recorded in T035/T036 are resolved.
- [X] T023 Add architecture tests enforcing dependency direction and prohibiting infrastructure references from Domain/Application in `tests/CentralMobilidade.Telemetry.ArchitectureTests/LayerDependencyTests.cs`

**Checkpoint**: Configuration fails fast, migrations apply, authentication works, health checks are
meaningful, and shared contracts compile without provider leakage.

---

## Phase 3: User Story 1 — Durably receive telemetry (Priority: P1) 🎯 MVP

**Goal**: Accept an authenticated gzip batch and return `202` with `batchId` only after RabbitMQ
confirms durable persistence.

**Independent Test**: Submit a valid compressed batch, terminate the API after its response, restart
processing, and prove the acknowledged envelope remains consumable; all pre-durability failures must
return non-success.

### Tests for User Story 1

- [X] T024 [P] [US1] Add OpenAPI contract tests for `POST /api/v1/telemetry/batches`, including 202, 400, 401, 408, 413, 415, 429, and 503 responses, in `tests/CentralMobilidade.Telemetry.ContractTests/Api/V1/IngestBatchContractTests.cs`
- [X] T025 [P] [US1] Add boundary tests for compressed-size, duration, content type, charset, gzip header, empty body, request cancellation, trusted proxies, and spoofed forwarded headers in `tests/CentralMobilidade.Telemetry.IntegrationTests/Ingestion/IntakeBoundaryTests.cs`
  - TDD validation: 35 executable boundary scenarios currently fail with 404 until T029/T033/T034/T035 implement intake; the 233 preceding tests pass. See `tests/CentralMobilidade.Telemetry.IntegrationTests/Ingestion/README.md`.
- [X] T026 [P] [US1] Add RabbitMQ provider contract tests for durable topology, mandatory routing, native gzip body bytes, metadata attributes, publisher confirm/nack, timeout, and envelope integrity in `tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/RabbitMqPublisherContractTests.cs`
  - TDD validation: real RabbitMQ connectivity/confirm probe passes; ten provider scenarios fail until T030/T031 supply the adapter. Full suite: 234 pass, 35 expected T025 failures and ten expected T026 failures. See `tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/README.md`.
- [X] T027 [US1] Add crash-boundary test that kills the API after publisher confirmation and verifies the accepted envelope remains available in `tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/AcceptedBatchSurvivalTests.cs`
  - TDD validation: the real API-process crash/broker-connectivity probe passes; the accepted-batch scenario fails with 404 until intake is implemented in T030–T035. Full suite: 235 pass and 46 expected T025/T026/T027 failures. See `tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/README.md`.

### Implementation for User Story 1

- [X] T028 [P] [US1] Implement `IngestionBatch` lifecycle with `Receiving -> Publishing -> Accepted` and failure transitions in `src/CentralMobilidade.Telemetry.Domain/Batches/IngestionBatch.cs`
  - Validation: 23 lifecycle tests pass; immutable transitions, matching durable confirmation, typed failures, UTC/retention and exact replay checks. Full suite: 258 pass with the same 46 pending T025–T027 TDD failures.
- [X] T029 [P] [US1] Implement streaming compressed-body hashing and a bounded spool that never materializes the body as a string in `src/CentralMobilidade.Telemetry.Infrastructure/Streaming/BoundedRequestSpool.cs`
  - Validation: 13 spool tests pass in Docker; bounded file/memory, incremental SHA-256, native gzip bytes, timeout/cancellation, read-only rewind and disposal cleanup. Full suite: 271 pass with the same 46 pending T025–T027 TDD failures.
- [X] T030 [P] [US1] Implement RabbitMQ connection/channel recovery and declare durable exchange, queue, retry, and dead-letter topology in `src/CentralMobilidade.Telemetry.Infrastructure/Queue/RabbitMq/RabbitMqTopology.cs`
  - Validation: seven real-broker tests pass, including TCP-loss connection/channel recovery, durable TTL topology, retry/dead-letter routing and closed-channel replacement. Full suite: 278 pass with the same 46 pending T025–T027 TDD failures.
- [X] T031 [US1] Implement mandatory persistent publication with correlated publisher confirms and explicit timeout/nack mapping in `src/CentralMobilidade.Telemetry.Infrastructure/Queue/RabbitMq/RabbitMqBatchPublisher.cs`
  - Validation: all 12 RabbitMQ publisher cases pass, resolving the ten T026 failures and adding an unrelated-ACK guard. Full suite: 289 pass; only 35 T025 and one T027 intake failures remain (36 total).
- [X] T032 [US1] Implement transactional batch creation and replay-safe acceptance transitions in `src/CentralMobilidade.Telemetry.Infrastructure/Persistence/PostgresBatchStateStore.cs`
  - Validation: two PostgreSQL integration tests pass, covering eight concurrent idempotent creates, metadata collision rejection, atomic version-checked transitions, eight concurrent exact acceptance replays, and illegal/stale transition rejection. Full suite: 291 pass; only the same 35 T025 and one T027 intake failures remain (36 total).
- [X] T033 [US1] Implement intake orchestration so `202` is impossible before publisher confirmation in `src/CentralMobilidade.Telemetry.Application/Ingestion/AcceptTelemetryBatchHandler.cs`
  - Validation: six orchestration tests pass, proving acceptance remains pending before the matching durable confirmation, replay skips republishing, provider/cancellation/mismatched-receipt paths never accept, and a post-confirmation state failure remains non-success. Full suite: 297 pass; only the same 35 T025 and one T027 endpoint failures remain (36 total).
- [X] T034 [US1] Implement `POST /api/v1/telemetry/batches` with producer-only bearer authorization, required v1 headers, backpressure, `Location`, and `Retry-After` in `src/CentralMobilidade.Telemetry.Api/Endpoints/V1/IngestTelemetryBatchEndpoint.cs`
  - Validation: 32 of 35 T025 boundary scenarios now pass plus the bounded admission-gate test; headers, media type, gzip, size, timeout, cancellation, native bytes/hash, `202`, `Location`, and `Retry-After` are covered. Full suite: 330 pass and four expected T035 failures (three trusted-proxy cases and one real-process provider-composition case).
- [X] T035 [US1] Wire API middleware ordering, Kestrel compressed request limit, authentication, problem details, explicit trusted-proxy networks, forwarded-header symmetry/known-proxy enforcement, health checks, and endpoint mappings in `src/CentralMobilidade.Telemetry.Api/Program.cs`
  - Validation: all 35 intake-boundary scenarios and both real-process recovery scenarios pass, including trusted proxy handling and durable RabbitMQ publication before `202`. The full suite has 332 passing tests and two unrelated PostgreSQL readiness failures in `TelemetryHealthTests`.

**Checkpoint**: The durable intake slice is deployable as an MVP even before telemetry parsing exists.

---

## Phase 4: User Story 2 — Process valid events and isolate failures (Priority: P1)

**Goal**: Incrementally validate mixed batches, write valid events, quarantine invalid/conflicting lines,
and acknowledge the queue only after all durable outcomes are recorded.

**Independent Test**: Process a batch with valid lines, invalid schema, identical replay, and conflicting
duplicate; verify valid points exist once, the original conflict target remains unchanged, and every
rejection is inspectable.

### Tests for User Story 2

- [X] T036 [P] [US2] Add golden Line Protocol v1 tests for escaping, LF/CRLF, UTF-8, field types, required tags/fields, units, and canonical fingerprints in `tests/CentralMobilidade.Telemetry.UnitTests/LineProtocol/LineProtocolV1GoldenTests.cs`
  - TDD validation: 45 executable golden scenarios compile and fail explicitly because the T046 production parser is absent; all 101 existing unit tests pass. Covers decoded values, escapes, strict UTF-8, LF/CRLF, closed schema, required columns, types, coordinate boundaries, units and fingerprint equivalence/content changes. See `tests/CentralMobilidade.Telemetry.UnitTests/LineProtocol/README.md`. The two T022 PostgreSQL readiness failures remain a separate recorded issue.
- [X] T037 [P] [US2] Add property/fuzz tests for truncated gzip, CRC failure, 100:1 expansion, 64 MiB decompressed limit, 16 KiB line limit, and 50,000-line limit in `tests/CentralMobilidade.Telemetry.UnitTests/Streaming/GzipBoundaryPropertyTests.cs`
  - TDD validation: 29 executable scenarios compile and fail explicitly because the T045 production reader is absent. Includes seeded fragmentation/UTF-8/LF/CRLF properties, truncation/CRC, independently isolated byte/ratio/line/count boundaries, cancellation and deadline. Unit suite: 101 pass and 74 expected failures (29 T037 + 45 T036). See `tests/CentralMobilidade.Telemetry.UnitTests/Streaming/README.md`.
- [X] T038 [P] [US2] Add InfluxDB v3 writer integration tests for gzip, millisecond precision, `accept_partial=true`, `no_sync=false`, 5,000-point/2 MiB/1-second flush thresholds, and partial errors in `tests/CentralMobilidade.Telemetry.IntegrationTests/Influx/InfluxV3WriterTests.cs`
  - TDD validation: real InfluxDB 3.11.6 gzip/partial-write/query control passes. Full integration suite: 130 pass and 15 expected absent-production-type failures (12 writer scenarios await T049; three batching thresholds await T050). Includes native-body HTTP assertions, destination isolation, original line-owner mapping, non-success/private failures and unreconcilable partial responses. See `tests/CentralMobilidade.Telemetry.IntegrationTests/Influx/README.md`.
- [X] T039 [P] [US2] Add quarantine storage integration tests proving encryption/protected storage, metadata-only logging, integrity verification, and 30-day deletion deadline in `tests/CentralMobilidade.Telemetry.IntegrationTests/Quarantine/QuarantineStoreTests.cs`
  - TDD validation: 11 executable filesystem scenarios compile and fail explicitly because the T048 production store is absent. Covers Windows ACL/Unix permissions, bytes/SHA-256, corruption rejection, unfiltered metadata-only logs, inclusive 30-day deletion, invalid retention, concurrent/conflicting replay and failed-write cleanup/retry. Protected permissions are required; encryption/key management is not claimed. Full integration suite: 130 pass and 26 expected failures (15 T038 + 11 T039). See `tests/CentralMobilidade.Telemetry.IntegrationTests/Quarantine/README.md`.
- [X] T040 [P] [US2] Create a representative deduplication corpus for 10,000 and 100,000 vehicles in `tests/CentralMobilidade.Telemetry.LoadTests/Data/DedupCorpus/README.md` and deterministic generator in `tests/CentralMobilidade.Telemetry.LoadTests/Generators/DedupCorpusGenerator.cs`
  - Validation: four tests pass, exporting and recovering the actual 33,300/333,000-line profiles (three samples per vehicle, ten companies, 10% identical replay and 1% conflict relative to originals). Checks identity cardinality, oracle reconciliation, invariant culture/seed determinism and semantic replay/conflict properties. Streaming gzip/JSONL export and versioned manifests are retained under ignored `artifacts/validation/T040/`. Performance/identity-guard claims remain gated by T041.
- [X] T041 [US2] Benchmark atomic partitioned identity-guard alternatives at 5,000 events/s and prove first-writer immutability plus safe owner resume, lease takeover, and fencing under concurrency; separately benchmark 30-day fingerprint evidence, project 60-day and five-year guard storage, and record correctness, storage, p95/p99, and the selected design in `specs/001-telemetry-ingestion/adr/001-conflict-detector.md`
  - Validation: five load-project tests pass. Durable PostgreSQL compares plain/hash16/range16 SQL prototypes; 100-row batches miss target (~2,100 guard events/s), while 1,000-row final batches achieve 5,077–5,081 guard events/s with independently measured evidence. Concurrent first reservation/conflict/takeover, owner resume and stale-fence rejection pass. ADR selects hash16 for development, records batch p95/p99 and measured allocation/projections (19.20 TB/60-day guard, 584.11 TB/five-year guard, 3.29 TB/30-day evidence). Short 50,000-row runs on a 16-processor Docker host do not approve combined pipeline, aged retention or production capacity; T047 must revalidate its actual schema/adapter and T091/T095 retain release gates.
- [X] T042 [US2] Add redelivery, concurrent same-identity, evidence-expired, and failure-at-every-`Reserved/Writing/Committed`-boundary tests proving exactly one immutable logical event while permitting identical physical rewrites after ambiguous Influx outcomes, plus owner resume, fenced lease takeover, original preservation, and acknowledgement only after durable outcomes in `tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/WorkerAcknowledgementBoundaryTests.cs`
  - TDD validation: 25 scenarios compile and fail for absent production types (six await T047; 19 await T051 and its processing dependencies). Real PostgreSQL guard/batch composition, 16-way reserve/takeover, stale fencing, evidence expiry, 13 injected persistence/ack boundaries, mixed-line quarantine recovery and conflicting workers are covered. Fresh processing objects per redelivery; controlled telemetry/quarantine ledgers isolate orchestration, without claiming real worker-process crash or provider restart proof. Integration suite: 130 pass and 51 expected failures (26 previous + 25 T042). See `tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/WorkerAcknowledgementBoundaryTests.md`.

### Implementation for User Story 2

- [X] T043 [P] [US2] Implement `TelemetryEvent` validation with required `empresa_id`, `veiculo_id`, latitude -90..90, longitude -180..180, non-negative speed/RPM, boolean ignition, optional pseudonymous `motorista_id`, and optional plate in `src/CentralMobilidade.Telemetry.Domain/Telemetry/TelemetryEvent.cs`
  - Validation: 27 new domain tests pass; unit suite has 128 pass and the same 74 expected T036/T037 failures. Immutable validated event, finite coordinate/speed limits, typed identity/timestamp, optional pseudonymous driver, invariant uppercase Unicode-safe plate and private diagnostics. Parser/canonical fingerprint remain T046. Architecture tests pass; see `src/CentralMobilidade.Telemetry.Domain/Telemetry/README.md`.
- [X] T044 [P] [US2] Implement stable `LineProcessingResult` and protected `QuarantinedRejection` entities from `data-model.md` in `src/CentralMobilidade.Telemetry.Domain/Processing/LineProcessingResult.cs` and `QuarantinedRejection.cs`
  - Validation: 23 new domain tests pass, including exact rejection-catalog parity, consistent immutable outcomes, keyed HMAC diagnostic digest, protected store reference/SHA-256, UTC and inclusive 30-day deletion metadata with idempotent confirmation. Unit suite: 151 pass and the same 74 expected T036/T037 failures; architecture: 47 pass. Entities perform no IO or key provisioning; adapter/persistence integration and retention remain T048/T051/T061. See `src/CentralMobilidade.Telemetry.Domain/Processing/README.md`.
- [X] T045 [US2] Implement bounded streaming gzip decompression with independent byte/ratio/line/time cancellation in `src/CentralMobilidade.Telemetry.Infrastructure/Streaming/BoundedGzipLineReader.cs`
  - Validation: all 29 T037 scenarios plus six additional EOF/empty-line/compressed-length cases pass. Bounded async borrowed-line iterator, independent size/ratio/line/count ceilings, linked deadline/cancellation, explicit single-member CRC32/ISIZE/trailer verification and no caller-stream disposal. Unit suite: 186 pass and 45 expected T036 parser failures; architecture: 47 pass. See `src/CentralMobilidade.Telemetry.Infrastructure/Streaming/BoundedGzipLineReader.md` for lifetime/integrity and single-member constraints.
- [X] T046 [US2] Implement closed-schema incremental parser and canonical fingerprinting for `contracts/line-protocol-v1.md` in `src/CentralMobilidade.Telemetry.Application/LineProtocol/TelemetryLineProtocolV1Parser.cs`
  - Validation: all 45 T036 cases plus ten additional parser/canonicalization scenarios pass. Bounded strict UTF-8 per-line scanner, closed schema, decoded escapes/types/units/ranges, validated T043 event and versioned binary SHA-256 fingerprint (all identity/content, optional presence, invariant numbers/plate, signed-zero normalization). Full unit suite: 241 pass; architecture: 47 pass in Linux Docker. Windows build succeeds but Application Control blocks the final test DLL; no policy changes. See `src/CentralMobilidade.Telemetry.Application/LineProtocol/README.md` for canonical frame and evidence.
- [X] T047 [US2] Implement the ADR-001 recoverable identity guard with atomic `Reserved/Writing/Committed` transitions, stable owner, renewable lease, fenced takeover, commit only after confirmed Influx persistence, `PendingCommitted/Promoting/QueryableCommitted` storage location, telemetry-lifetime committed guard, and 30-day fingerprint evidence in `src/CentralMobilidade.Telemetry.Infrastructure/Idempotency/SelectedEventIdentityGuard.cs`
  - Validation: six T042 guard scenarios and three additional adapter scenarios pass against PostgreSQL; full integration 139 pass / 45 expected failures, unit 241/241, architecture 47/47. Hash16 migration preserves rows and protections. Actual per-identity throughput misses 5,000 events/s; capacity approval remains blocked pending optimization and remeasurement (ADR-001 T047 revalidation).
- [X] T048 [P] [US2] Implement protected filesystem quarantine with atomic writes, integrity digest, restrictive permissions, and no path disclosure in `src/CentralMobilidade.Telemetry.Infrastructure/Quarantine/FileSystemQuarantineStore.cs`
  - Validation: 16 filesystem scenarios pass on Windows and Linux Docker as non-root UID 10001 (original 11 T039 plus re-instantiation, metadata conflict, raw corruption, cancellation and early retention). Bounded single-object staging/flush/exclusive rename, raw/envelope SHA-256, restrictive ACL/0700/0600 permissions, safe errors/logs and inclusive deadline deletion. Full integration: 155 pass / 34 expected T049/T051 failures; unit 241/241, architecture 47/47. No encryption or power-loss directory durability claim; worker wiring and scheduled cleanup remain T053/T061.
- [X] T049 [P] [US2] Implement the replaceable company-visibility adapter with conservative pending result for unavailable, timed-out, or inconclusive lookups, and the InfluxDB 3 native gzip writer with separate queryable/pending destinations, `/api/v3/write_lp`, millisecond precision, partial acceptance, durable sync, and bounded response parsing in `src/CentralMobilidade.Telemetry.Infrastructure/Cadastral/ConfiguredCadastralAdapter.cs` and `Influx/InfluxV3TelemetryWriter.cs`
  - Validation: original writer scenarios and real-server millisecond/partial schema-conflict persistence pass; 23 additional safety scenarios cover tenant-matched visibility, unavailable/inconclusive/timeout handling, bounded bodies, ambiguous outcomes, cancellation, duplicate JSON/indices, 422, destination separation and disabled redirects. Full integration: 191 pass / 22 expected failures (three T050, 19 T051); unit 241/241, architecture 47/47. Configured cadastral HTTP contract is documented, with unavailable default; no live external cadastral service is assumed. Worker wiring remains T053, no process-restart/host-loss durability claim.
### Intermediate foundation adequation — MUST finish before T050

**Source**: Constitution 2.0.0, FR-001/FR-006/FR-013/FR-019/FR-020, SC-008 and research decision
2026-10-14. This targeted amendment reconciles T008/T010/T016/T018/T019/T022/T042/T047/T049.
It is not a full-feature convergence assessment. Follow the explicit dependency chain below.

- [X] T104 [US2] Establish amendment regression coverage before refactoring in `tests/CentralMobilidade.Telemetry.IntegrationTests/Influx/StorageAdapterSafetyTests.cs`, `tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/WorkerAcknowledgementBoundaryTests.cs` and `tests/CentralMobilidade.Telemetry.IntegrationTests/Security/`: the same producer credential handles multiple valid company IDs absent from a registry; no existence HTTP call or pending destination is used; missing/malformed IDs retain current validation, privileged operations remain denied to producers, and pending batch lifecycle still works. Preserve red tests for unimplemented T050/T051 rather than claiming those features now exist.
  - TDD validation (2026-10-14): 15 new `FoundationSingleInflux` cases compile; 11 pass and four fail as intended (two legacy storage dependencies awaiting T105/T107 and two missing T051 processing cases). Existing T050/T051 red coverage remains. Focused regression: 123 cases, 96 pass, 26 expected failures and one existing one-minute lease timing failure during initial image acquisition; isolated lease recheck passes 1/1. Evidence and scope limits: `tests/CentralMobilidade.Telemetry.IntegrationTests/Influx/README.md`; TRX in ignored `artifacts/validation/T104/`. No production/deployment changes; T105 and later tasks remain unchecked.
- [X] T105 [US2] After T104, remove company visibility routing and promotion operations from `src/CentralMobilidade.Telemetry.Application/Abstractions/ICadastralResolvers.cs`, `ITelemetryWriter.cs`, `IEventIdentityGuard.cs` and affected Domain types; preserve `IDriverResolver`, `empresa_id` wire shape, immutable event identity and processing lease/fencing. Update all implementations/test doubles together so the solution compiles, without implementing T050/T051.
  - Validation (2026-10-14): removed company-visibility and promotion ports/types, destination arguments, obsolete adapter/registration and dependent doubles; retained driver resolution and processing leases/fencing. Legacy row enums remain Infrastructure-only until T106, with PostgreSQL coverage proving no legacy pending identity is rewritten/resumed as queryable. Solution build: zero warnings/errors; unit 242/242, contracts 58/58, architecture 47/47. Directed integration: 78 pass / 25 expected T050/T051/T107 failures / three stale-fixture-clock failures; after anchoring test leases after database setup, all six affected guard scenarios pass 6/6. Full integration selection was not repeated. No migration/Compose change or T050/T051 implementation. Removing the obsolete adapter was required by the port removal; T107 still must remove legacy Pending configuration. Evidence: `tests/CentralMobilidade.Telemetry.IntegrationTests/Influx/README.md`; ignored TRX under `artifacts/validation/T105/`.
- [X] T106 [US2] After T105, add a forward PostgreSQL migration and revise entities/configurations/snapshot under `src/CentralMobilidade.Telemetry.Infrastructure/Persistence/` plus `src/CentralMobilidade.Telemetry.Infrastructure/Idempotency/SelectedEventIdentityGuard.cs` to eliminate promotion tables/leases/locations and pending destinations. First inventory legacy pending points, promotion rows and guards, including in-flight promotion states; reconcile points, fingerprints, identities and counts into the queryable store using an explicit bounded upgrade procedure in `docs/runbooks/single-influx-upgrade.md`. Abort on unresolved conflicts or missing evidence; retain existing storage until reconciliation is verified. Preserve accepted batches, guard/evidence retention, original provenance, leases, fencing and immutable-write protections. Never rewrite applied migrations, reset databases or delete volumes; support fresh installs and populated upgrades.
  - Validation (2026-10-14): added guarded forward migration `20261014230000_SingleTelemetryStore`, updated current model/snapshot/guard, removed promotion entities and routing columns, and aligned runtime grants. Existing applied migrations/designer are unchanged. Offline runbook `docs/runbooks/single-influx-upgrade.md` and transactional SQL `scripts/sql/reconcile-single-influx-page.sql` require independently verified source/destination fingerprints, nonexpired matching evidence and pages of 1..500; conflicts, absent evidence, active/incomplete ownership and unresolved promotions block retirement. No live centralmobilidade containers/volumes were found in the inspected Docker context; external catalogs were not inspected and no real project database was migrated. Fresh/populated upgrades, refusals/rollback, owner/fence/retention/provenance, replay/conflict, 16 partitions, truncate protections and runtime grants passed: directed integration 38/38 plus runtime-grants 1/1; unit 242/242, contracts 58/58, architecture 47/47. Tests use synthetic Influx read proofs; actual catalog reconciliation remains an explicit operator procedure for populated deployments. Build passes; no Compose changes or volume deletion. Health/configuration alignment remains T107/T108 and T050/T051 remain unimplemented. Evidence: `src/CentralMobilidade.Telemetry.Infrastructure/Persistence/Migrations/README.md` and ignored `artifacts/validation/T106/`.
- [X] T107 [US2] After T106, refactor `src/CentralMobilidade.Telemetry.Infrastructure/Influx/InfluxV3TelemetryWriter.cs`, `StorageAdapterRegistration.cs` and `src/CentralMobilidade.Telemetry.Infrastructure/Cadastral/ConfiguredCadastralAdapter.cs` for one telemetry destination with no company lookup; remove the obsolete company-visibility adapter/registration while retaining independent driver enrichment if used. Preserve native gzip, millisecond timestamps, partial-result reconciliation, durable sync, bounded parsing, privacy and cancellation; adapt `tests/CentralMobilidade.Telemetry.IntegrationTests/Influx/`.
  - Validation (2026-10-14): writer/fixtures now require only Queryable; removed Pending field/config validation and destination comparison. Obsolete company adapter/registration were already removed in T105; independent driver port unchanged. Regressions cover stale Pending values ignored, invalid Queryable rejected without fallback/private-data disclosure, typed-client success/redirect behavior without cadastral calls, gzip/sync/partial reconciliation/cancellation/response bounds. Solution build: zero warnings/errors; writer namespace 41/41 pass including three real InfluxDB 3.11.6 memory-container scenarios; unit 242/242, contracts 58/58, architecture 47/47. An initial broader filter also selected two health cases: one passed, the readiness test failed because the probe still requires the removed promotion table; this is recorded for T108 and its source was not changed. No template/Compose changes, production data migration or T050/T051 implementation. Evidence: `tests/CentralMobilidade.Telemetry.IntegrationTests/Influx/README.md`; ignored TRX under `artifacts/validation/T107/`.
- [X] T108 [US2] After T107, update `src/CentralMobilidade.Telemetry.Infrastructure/Health/TelemetryHealthChecks.cs`, `.env.example`, `src/CentralMobilidade.Telemetry.Api/appsettings.json`, `src/CentralMobilidade.Telemetry.Worker/appsettings.json` and registration to require only the queryable store and no promotion table/company lookup. Keep PostgreSQL durability, queue publication and worker freshness checks. Confirm one producer credential has no company allowlist without broadening operator/viewer privileges; update health/security tests under `tests/CentralMobilidade.Telemetry.IntegrationTests/`.
  - Validation (2026-10-14): API/worker health registers only Queryable, ignores stale Pending settings and probes the seven current PostgreSQL tables without promotions. Durability/read-write privileges, durable publication, downstream-independent API readiness, worker storage availability and consumer freshness remain enforced. Worker/.env templates aligned; API template and writer registration already met the rule. Current service DNS remains until T109; the env template documents the temporary legacy Compose prerequisite and preserves private files/volumes. Registration regression first failed as expected (worker registered two Influx checks); directed health/security/production-intake tests passed 37/37, including real migrated PostgreSQL and the T107 readiness regression. Same producer credential accepts two unregistered company IDs, while operator/viewer type, scope and audit restrictions remain intact. Unit 242/242, contracts 58/58, architecture 47/47; final solution build zero warnings/errors. Initial sandbox Docker denial and a concurrent-build DLL lock were resolved by Docker-enabled tests and a subsequent sequential build. No Compose changes, deployed migration or T050/T051 implementation. Evidence: `src/CentralMobilidade.Telemetry.Infrastructure/Health/README.md`; ignored TRX under `artifacts/validation/T108/`.
- [X] T109 [US2] After T108 and verified T106 data reconciliation, simplify `deploy/compose.yaml`, `deploy/compose.override.yaml` and `deploy/influxdb/init-database.sh` to one InfluxDB server, one telemetry database and one terminating init job. Name the services `influxdb` (persistent telemetry server) and `influxdb-init` (one-shot database initialization), yielding default Compose container names `centralmobilidade-influxdb-1` and `centralmobilidade-influxdb-init-1`; retain project-scoped generated names rather than fixed `container_name`. Update service DNS URLs, depends_on references, scripts and documentation together while preserving the existing queryable volume identity. Remove pending service/init, token configuration, private pending network, port 8182 and obsolete dependencies. Preserve queryable volume identity and existing data; never delete the legacy pending volume automatically. Verify resolved Compose topology without exposing secrets.
  - Validation (2026-10-14): Compose now declares only influxdb and the terminating influxdb-init job; application/init DNS and Grafana/worker dependencies aligned. Removed pending service/init, secret, network and port; retained queryable volume key/generated identity, mount, node-id queryable and existing bootstrap token setting. No fixed container_name. Initializer requires the telemetry database and service URL, preserves an existing database and propagates errors. Topology regression first failed on the old pending-token requirement, then base plus development/app/emulator configurations passed with synthetic values and no resolved secrets printed. Seven isolated initializer CLI-double cases passed on pinned InfluxDB 3.11.6, including preservation and error propagation. Current Docker inventory found no project containers/volumes or matching legacy volume names; external catalogs were not inspected. No deployed migration, project service startup or volume removal; populated deployments still require the T106 reconciliation procedure. Real-server Compose persistence/restart validation remains T112. Evidence and commands: deploy/README.md, scripts/Test-SingleInfluxTopology.ps1 and scripts/Test-InfluxInitialization.sh. T110-T112 remain unchecked and T050/T051 unimplemented.
- [X] T110 [US2] After T109, align `tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/SelectedEventIdentityGuardTests.cs`, `WorkerAcknowledgementBoundaryTests.cs`, `tests/CentralMobilidade.Telemetry.IntegrationTests/Persistence/` and affected unit/contract tests with the single destination; replace obsolete promotion assertions with fresh/populated upgrade, immutable first-write, replay/conflict, owner resume, fenced takeover and unchanged acknowledgement-boundary coverage. Do not erase valid failure/recovery tests or implement the still-pending processing handler.
  - Validation (2026-10-14): replaced the committed-only partition upgrade test with six populated upgrades (initial/partitioned schemas x Reserved/Writing/Committed), preserving identity owner/fence/lease, provenance, fingerprint, retention, owner resumption, immutable replay/conflict and partition truncate protection. Added same-vehicle/time cross-company reservations and adapter-reinstantiation replay/conflict coverage. Model test now explicitly requires the seven current entities and absence of routing/promotion columns; it first failed on the obsolete count of eight, then passed in the final 3/3 model recheck. Existing T106 fresh/populated reconciliation/refusal/rollback/grant tests remain intact; legacy promotion assertions are migration safeguards only. Directed integration initial run: 71 total, 49 passed, 21 expected failures solely for absent T051 and one obsolete model assertion; after localized recheck, 50 distinct integration cases validated, with full selection not repeated. Unit 242/242, contracts 58/58, architecture 47/47; final solution build zero warnings/errors. Ack failure boundaries remain executable and red, without skips or substitute handler. Capacity benchmark excluded without resolving ADR-001 shortfall. No production/migration/Compose changes or T050/T051 implementation. Evidence: tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/WorkerAcknowledgementBoundaryTests.md and ignored artifacts/validation/T110/.
- [X] T111 [US2] After T110, update `deploy/README.md`, relevant source/test READMEs, `.env.example` examples and `specs/001-telemetry-ingestion/quickstart.md` with single-token-file/single-store setup, shared all-company producer credential, optional driver enrichment, safe legacy migration and explicit local Compose startup. Mark historical dual-store validation as superseded without claiming it validates the new topology; remove current instructions that require pending/promotion.
  - Validation (2026-10-14): aligned root/deployment/quickstart and relevant persistence, security, health, observability and Influx-test documentation with one server/database/bootstrap file, shared all-company producer credential, unchanged empresa_id/validation, independent operator/viewer scopes and optional driver enrichment. Removed current unknown-company routing/promotion instructions and labelled dual-store test evidence historical. Documented explicit Compose startup (no IDE hook), private runtime configuration, separately applied migrations, preserved queryable volume/node-id/token and mandatory offline reconciliation for legacy catalogs. Quickstart distinguishes current intake/hosts from unimplemented processing/consumer/status/presentation/Grafana/load scenarios; T112 remains the gate before T050. .env.example adds only commented single-store URL/database and startup/preservation guidance, with unique keys and blank secrets. Markdown local links/fences and diff whitespace checked; synthetic base/development/app/emulator topology check passed without printing secrets or starting services. No production/YAML/schema changes, runtime suites, database migration or volume deletion in this documentation task. Evidence: deploy/README.md, quickstart.md and linked source/test READMEs.
- [X] T112 [US2] After T111, execute changed unit/contract/integration/architecture checks, fresh/populated PostgreSQL migration and Compose persistence/restart smoke validation; record commands, counts, unresolved failures and data/identity reconciliation in `artifacts/validation/foundation-single-influx/README.md`. Verify same-credential multi-company acceptance at implemented boundaries, no company existence call/pending dependency and preserved authorization/privacy/durability. Run a focused cross-artifact consistency review of `specs/001-telemetry-ingestion/` and constitution 2.0.0. Release T050 only when amendment checks pass; explicitly distinguish pre-existing T050/T051 red tests and the separate ADR-001 capacity shortfall. Do not implement T050 as part of this gate.
  - Validation (2026-10-14/15 UTC): foundation gate passed; T050 released but not implemented. Full functional integration initially 226 pass/31 fail: 3 absent T050, 21 absent T051, 7 RabbitMQ/API timing failures. Normal publisher tests used a 500 ms deadline including cold topology; aligned to production 5 s while retaining 500 ms for explicit timeout fault. Focused recheck 13/14 pass; final failed crash scenario passed 1/1 after client timeout was aligned from 10 s to 45 s to cover the existing 30 s upload plus publication/state budget. Publisher/API production timeouts and durability assertions unchanged. Total distinct passes: integration 233, unit 242, contracts 58, architecture 47 (580); 24 expected TDD failures remain, no skips. Full suite not repeated after localized harness fixes. Final build zero warnings/errors. Real base Compose smoke passed six healthy servers/two successful jobs, one PG row/two company Influx events with millisecond timestamps and all seven volume markers across restart and down/up; same catalog/node/token/volume retained. First startup and CLI-harness failures retained in report; no volumes deleted or deployed data migrated. Fresh/populated migrations, owner/fencing/replay/retention, same-credential intake, no registry/pending dependency and presentation/privacy controls passed at implemented boundaries. Focused cross-artifact review corrected obsolete storage_location and 404 descriptions/planning tense. ADR-001 throughput shortfall remains a separate production gate. Public evidence: artifacts/validation/foundation-single-influx/README.md; only this report is unignored, raw logs/tokens remain private.

**Adequation checkpoint**: T104-T112 are complete with recorded evidence; one InfluxDB server and
one terminating init job remain; new valid company IDs need no registration; no acknowledged legacy
data is lost; shared producer credential and existing presentation controls are verified. Only then
resume T050 below. Deployment startup integration with the IDE is outside this amendment.

### Resume User Story 2 implementation after the adequation gate

- [X] T050 [US2] After T104-T112 pass, implement single-destination count/byte/time batch assembly with default ceilings of 5,000 points, 2 MiB, and 1 second in `src/CentralMobilidade.Telemetry.Application/Processing/InfluxWriteBatcher.cs`
  - Validation (2026-10-15): three original thresholds and 12 additional cases pass for byte overflow, final drain, input reuse, monotonic oldest-point timer, backpressure/cancellation, background failure, awaited partial outcome handling and invalid outcomes/input. One gzip buffer/in-flight write, original owner snapshots and trace correlation; no company lookup/destination routing. Full functional integration excluding the separate ADR benchmark: 248 pass / 21 expected T051 failures, zero skips; an added real Influx batcher/writer two-company test passed in directed recheck (249 distinct passes, full suite not repeated). Unit 242/242, contracts 58/58, architecture 47/47. T051 durable outcomes/ack, T053 wiring and ADR-001 capacity remain separate. See Processing/README.md and ignored artifacts/validation/T050/.
- [X] T051 [US2] Implement mixed-line processing that acquires/resumes identity leases, confirms the guard only after reconciled Influx persistence, never rewrites committed identities, performs exact 30-day replay/conflict handling, recognizes every valid producer-supplied company ID without lookup and writes directly to the single telemetry database, aggregates rejections, and safely recovers partial results in `src/CentralMobilidade.Telemetry.Application/Processing/ProcessTelemetryBatchHandler.cs`
  - Validation (2026-10-15): original 21 processing failures resolved. Final functional Linux suite: 282/282, unit 242/242, contracts 58/58, architecture 47/47, zero skips. After adding a separate processing-attempt deadline, directed final recovery/storage selection passed 41/41 (283 distinct integration cases; full suite not repeated after that localized change). Real PostgreSQL/Influx/filesystem composition, immutable result replay including permanent provider rejection across schema changes, owner-preserving counters, HMAC/quarantine metadata transactions, partial writes, cancellation, renewal/loss/timeout, gzip integrity, raw/canonical bounds and trace propagation validated. Shared bounded reader extracted to Application with compatible Infrastructure facade. Windows Application Control blocked rebuilt DLLs; Linux final checks replace those runs without security-policy changes. Persistent Processing:DiagnosticKey provisioning and consumer/wiring remain T052/T053; rejection paging and ADR-001 capacity are separate gates. Reports: ignored artifacts/validation/T051/ and Recovery/Processing READMEs.
- [ ] T052 [US2] Implement RabbitMQ consumer with bounded prefetch, manual ack, retry backoff/jitter, poison routing, cancellation, and graceful drain in `src/CentralMobilidade.Telemetry.Infrastructure/Queue/RabbitMq/RabbitMqBatchConsumer.cs`
- [ ] T053 [US2] Wire worker dependencies, concurrency limits, shutdown drain, heartbeat, and queue consumption in `src/CentralMobilidade.Telemetry.Worker/Program.cs`

**Checkpoint**: A mixed batch reaches terminal state with valid data persisted and invalid data isolated;
the selected conflict strategy has measured evidence at target scale.

---

## Phase 5: User Story 3 — Inspect batch processing (Priority: P2)

**Goal**: Let an authorized internal operator inspect current batch state and privacy-safe rejection
metadata for 30 days.

**Independent Test**: Follow a partially accepted batch through every state and verify counts,
timestamps, pagination, non-disclosure for missing/expired identifiers, and automatic cleanup.

### Tests for User Story 3

- [ ] T054 [P] [US3] Add OpenAPI contract tests for producer-owned status, operator permission/company-scope rules, rejection pagination, credential-type isolation, and administrative raw-quarantine lookup, including fail-closed audit and 200/401/403/non-disclosing-404 outcomes, in `tests/CentralMobilidade.Telemetry.ContractTests/Api/V1/BatchStatusContractTests.cs`
- [ ] T055 [P] [US3] Add lifecycle concurrency tests for replay-safe counters, terminal transitions, and optimistic conflicts in `tests/CentralMobilidade.Telemetry.IntegrationTests/Batches/BatchLifecycleTests.cs`
- [ ] T056 [P] [US3] Add retention tests proving batch/rejection metadata and quarantine content are deleted after 30 days, single-database telemetry expires after 60 days locally, and aggregate metrics remain in `tests/CentralMobilidade.Telemetry.IntegrationTests/Retention/OperationalRetentionTests.cs`

### Implementation for User Story 3

- [ ] T057 [P] [US3] Implement producer ownership and operator permission/company-scope batch visibility, cursor-based privacy-safe rejection queries, and operator-only raw-content retrieval that durably audits successful, denied, missing, and expired attempts before returning content and fails closed on audit failure in `src/CentralMobilidade.Telemetry.Infrastructure/Persistence/PostgresRejectionQuery.cs` and `src/CentralMobilidade.Telemetry.Application/Batches/GetQuarantinedRejectionContentHandler.cs`
- [ ] T058 [US3] Implement batch status projection with submitting-producer ownership, operator `batch.read`, all-companies-in-scope or explicit `cross_company.read`, and non-disclosing not-found behavior in `src/CentralMobilidade.Telemetry.Application/Batches/GetBatchStatusHandler.cs`
- [ ] T059 [US3] Implement `GET /api/v1/telemetry/batches/{batchId}` matching `contracts/openapi.yaml` in `src/CentralMobilidade.Telemetry.Api/Endpoints/V1/GetBatchStatusEndpoint.cs`
- [ ] T060 [US3] Implement rejection pagination and audited administrative raw-content endpoints with operator-only policies, company scope, explicit `quarantine.raw.read`, and producer-token denial from `contracts/openapi.yaml` in `src/CentralMobilidade.Telemetry.Api/Endpoints/V1/ListBatchRejectionsEndpoint.cs` and `GetQuarantinedRejectionContentEndpoint.cs`
- [ ] T061 [US3] Implement leased, idempotent cleanup for 30-day batch/quarantine/fingerprint evidence while retaining each immutable identity guard until its single-database telemetry expires, including 60-day local InfluxDB retention, in `src/CentralMobilidade.Telemetry.Worker/Retention/OperationalRetentionWorker.cs`
- [ ] T062 [US3] Add batch-state and rejection metrics without tenant/vehicle high-cardinality labels in `src/CentralMobilidade.Telemetry.Infrastructure/Observability/BatchMetrics.cs`

**Checkpoint**: Asynchronous processing is supportable through a stable API and expired operational data
is removed automatically.

---

## Phase 6: User Story 4 — Observe fleets and trips (Priority: P2)

**Goal**: Provide latest-position, one-vehicle trip, and ingestion-health dashboards while keeping
valid company IDs queryable within authorized scope and driver disclosure minimal and audited.

**Independent Test**: Ingest multiple valid company IDs absent from the registry, display a vehicle's
latest point and old trip in event-time order, and resolve a driver to name plus optional CPF suffix.

### Tests for User Story 4

- [ ] T063 [P] [US4] Add optional cadastral-enrichment contract tests for changing provider, missing driver, masked CPF suffix, timeout and cache invalidation; prove absent/unavailable company registry cannot gate valid-ID persistence or authorized telemetry visibility in `tests/CentralMobilidade.Telemetry.ContractTests/Cadastral/CadastralAdapterContractTests.cs`
- [ ] T064 [P] [US4] Add authorization tests for scoped and cross-company humans/services, producer-token denial, audited middleware denials, fail-closed audit persistence, organization/service-account audit attribution, every numeric viewer-query boundary, unauthorized-company non-disclosure, and audited driver identity disclosure in `tests/CentralMobilidade.Telemetry.IntegrationTests/Privacy/PresentationAuthorizationTests.cs`
- [ ] T065 [P] [US4] Add old single-vehicle trip query tests proving event-time ordering and documented Core query-envelope behavior in `tests/CentralMobilidade.Telemetry.IntegrationTests/Queries/VehicleTripQueryTests.cs`
- [ ] T066 [P] [US4] Add Grafana provisioning tests proving pinned image/plugin versions and Infinity health, internal YAML provisioning, Basic-only server-admin bootstrap, organization-local service-account reconciliation, strict three-way separation of `GrafanaBootstrapAdminCredential`, `GrafanaOrganizationServiceAccount`, and `ViewerPresentationServiceCredential`, absence of viewer Influx datasources, and rendering of all panels in `tests/CentralMobilidade.Telemetry.IntegrationTests/Grafana/GrafanaProvisioningTests.cs`

### Implementation for User Story 4

- [ ] T067 [P] [US4] Implement replaceable optional driver enrichment without company existence/visibility lookup, pending storage or promotion; missing enrichment must not hide authorized telemetry in `src/CentralMobilidade.Telemetry.Infrastructure/Cadastral/ConfiguredCadastralAdapter.cs`
- [ ] T068 [P] [US4] Extend append-only audit policies for identity and cross-company reads while excluding raw telemetry, coordinates, plate, name, CPF, token, and quarantine content in `src/CentralMobilidade.Telemetry.Infrastructure/Auditing/PostgresAuditWriter.cs`
- [ ] T069 [US4] Implement latest-position, one-vehicle-trip, ingestion-health, and driver-presentation handlers with `telemetry.read`/`driver.read`, server-side company scope, no arbitrary SQL, non-disclosing failures, configurable 500-vehicle, 24-hour/10,000-point, and seven-day/one-minute/10,000-point limits, and durable audit-before-identity-disclosure in `src/CentralMobilidade.Telemetry.Application/Presentation/`
- [ ] T070 [US4] Implement the operator/service-only presentation endpoints from `contracts/openapi.yaml`, including producer denial, server-side company scope, bounded query validation, and fail-closed identity audit behavior in `src/CentralMobilidade.Telemetry.Api/Endpoints/V1/Presentation/`
- [ ] T071 [P] [US4] Provision only the internal default organization, direct Influx datasource, internal dashboards, Prometheus, and Loki through `deploy/grafana/provisioning/`; create `scripts/Provision-GrafanaOrganizations.ps1` that uses a secret-sourced Basic `GrafanaBootstrapAdminCredential` only for idempotent server-level organization/membership reconciliation, then creates or rotates a least-privilege `GrafanaOrganizationServiceAccount` per organization to reconcile its Infinity datasource containing a distinct scoped `ViewerPresentationServiceCredential` and its viewer dashboards after health readiness
- [ ] T072 [P] [US4] Create internal-direct and viewer-API latest-position dashboards in `deploy/grafana/dashboards/internal/latest-vehicle-position.json` and `deploy/grafana/dashboards/viewer/latest-vehicle-position.json`
- [ ] T073 [P] [US4] Create internal-direct and viewer-API single-vehicle trip dashboards with bounded time range and event-time ordering in `deploy/grafana/dashboards/internal/vehicle-trip.json` and `deploy/grafana/dashboards/viewer/vehicle-trip.json`
- [ ] T074 [P] [US4] Create internal and viewer-API per-company ingestion-health dashboards plus a viewer masked-driver panel in `deploy/grafana/dashboards/internal/company-ingestion-health.json`, `deploy/grafana/dashboards/viewer/company-ingestion-health.json`, and `deploy/grafana/dashboards/viewer/driver-presentation.json`
- [ ] T075 [US4] Document separate rotation, storage, and least privilege for `GrafanaBootstrapAdminCredential`, `GrafanaOrganizationServiceAccount`, and `ViewerPresentationServiceCredential`, idempotent viewer-organization reconciliation, company-scope provisioning, service-credential audit attribution and its lack of individual attribution, company recognition from valid producer-supplied IDs, optional enrichment, identity disclosure, plugin upgrades, and audit review procedures in `docs/operations/grafana-access.md`

**Checkpoint**: Internal users can use all three initial dashboards for authorized company IDs,
and every driver identity disclosure is minimized and audited.

---

## Phase 7: User Story 5 — Operate and migrate the platform (Priority: P3)

**Goal**: Operate the local stack safely and prove that queue-provider migration preserves external
contracts and domain behavior.

**Independent Test**: Start a clean stack, run the same queue contract suite against RabbitMQ and the
Pub/Sub emulator, simulate dependency failures, and complete a documented drain/cutover rehearsal.

### Tests for User Story 5

- [ ] T076 [P] [US5] Implement reusable durable-queue provider contract tests for native gzip body bytes, metadata attributes, confirmation, redelivery, completion, correlation, and error mapping in `tests/CentralMobilidade.Telemetry.ContractTests/Queue/DurableBatchQueueContractTests.cs`
- [ ] T077 [P] [US5] Add Pub/Sub emulator adapter tests and document emulator limitations so session lifetime is never treated as durability in `tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/PubSubEmulatorTests.cs`
- [ ] T078 [P] [US5] Add Docker Compose smoke/restart tests for mandatory named volumes, missing/read-only volume failure, persistence, and dependency health in `tests/CentralMobilidade.Telemetry.IntegrationTests/Deployment/ComposeRecoveryTests.cs`
- [ ] T079 [P] [US5] Add automated observability privacy scan seeded with plate, coordinates, driver name, CPF, and bearer token in `tests/CentralMobilidade.Telemetry.IntegrationTests/Privacy/ObservabilityLeakTests.cs`

### Implementation for User Story 5

- [ ] T080 [P] [US5] Implement Pub/Sub native-byte message body plus metadata-attribute mapping, publish confirmation, pull consumption, acknowledgement, and transient/permanent error mapping in `src/CentralMobilidade.Telemetry.Infrastructure/Queue/PubSub/PubSubBatchQueue.cs`
- [ ] T081 [US5] Implement configuration-driven queue provider registration with startup validation and no domain branching in `src/CentralMobilidade.Telemetry.Infrastructure/DependencyInjection/QueueProviderRegistration.cs`
- [ ] T082 [P] [US5] Configure RabbitMQ, PostgreSQL, application, host, filesystem, and InfluxDB metrics scraping in `deploy/prometheus/prometheus.yml`
- [ ] T083 [P] [US5] Configure Loki retention to 30 days and privacy-safe ingestion labels in `deploy/loki/loki-config.yaml`
- [ ] T084 [P] [US5] Create technical Grafana dashboard for rate, bytes, backlog age, latency, rejection, duplicate outcome, retries, storage failure, and capacity pressure in `deploy/grafana/dashboards/platform-operations.json`
- [ ] T085 [US5] Configure initial Grafana-local alerts for backlog age, five-minute queryability risk, queue retention risk, storage failure, disk pressure, and worker silence in `deploy/grafana/provisioning/alerting/telemetry-alerts.yaml`
- [ ] T086 [US5] Harden Compose services with health checks, restart policy, resource limits, non-root users, read-only filesystems where possible, and named volumes in `deploy/compose.yaml`
- [ ] T087 [US5] Document RabbitMQ backup/restore, Influx/PostgreSQL volume backup, physical-host limitation, and recovery verification in `docs/runbooks/backup-restore.md`
- [ ] T088 [US5] Document zero/minimal-downtime RabbitMQ-to-Pub/Sub dual-publish, drain, verification, rollback, and accepted maintenance fallback in `docs/runbooks/queue-provider-cutover.md`
- [ ] T089 [US5] Document future GCS object-store configuration, identity, migration prerequisites, and five-year capacity gate in `docs/runbooks/gcs-migration.md`

**Checkpoint**: The platform is operable locally and queue-provider parity is proven without claiming
that the emulator or single host offers production HA.

---

## Phase 8: Polish & Cross-Cutting Production Gates

**Purpose**: Complete performance evidence, retention validation, security review, and executable
documentation across all stories.

- [ ] T090 [P] Add unit coverage for option validation, authorization permissions/company scopes, fail-closed audit decisions, single-store valid-ID recognition, canonicalization, error mapping, and separate identity-guard/evidence retention clocks in `tests/CentralMobilidade.Telemetry.UnitTests/`
- [ ] T091 [P] Add steady 5,000 events/s scenario on the 8-vCPU/16-GiB reference profile with p50/p95/p99, CPU <=85%, application memory <=12 GiB, disk, bytes, backlog, and write-latency reporting in `tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/SteadyStateScenario.cs`
- [ ] T092 [P] Add a burst of 10,000 vehicles sending one hour of 20-second backlog over ten minutes while 5,000 events/s continues, asserting no acknowledged loss and drain within 20 minutes, in `tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/ReconnectionBurstScenario.cs`
- [ ] T093 [P] Add seven-day-backlog extrapolation, Influx outage, and recovery scenario in `tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/InfluxOutageScenario.cs`
- [ ] T094 Execute 10,000-vehicle baseline and 100,000-vehicle target load/soak gates and save reproducible results in `artifacts/performance/telemetry-capacity-report.md`
- [ ] T095 Validate 60-day local and five-year future storage projections using measured compression, Parquet, PostgreSQL, queue, quarantine, and log growth in `artifacts/performance/storage-capacity-report.md`
- [ ] T096 [P] Add dependency vulnerability, container image, secret, license, Grafana plugin signature, and pinned-version scans to `scripts/Invoke-SecurityChecks.ps1`
- [ ] T097 Perform LGPD technical control review and record items requiring DPO/legal approval without claiming legal compliance in `docs/security/lgpd-technical-controls.md`
- [ ] T098 Execute every scenario in `specs/001-telemetry-ingestion/quickstart.md` and record commands, versions, timestamps, and results in `artifacts/validation/quickstart-results.md`
- [ ] T099 Reconcile implemented OpenAPI and queue/Line Protocol contracts against `specs/001-telemetry-ingestion/contracts/` and record compatibility results in `artifacts/validation/contract-conformance.md`
- [ ] T100 Create deployment readiness checklist covering all constitution gates and unresolved single-host risks in `docs/operations/deployment-readiness.md`
- [ ] T101 Add a deterministic final-acceptance dataset spanning at least 10 valid producer-supplied company IDs absent from any company registry, 1,000 vehicles, ordered and delayed points, and one named trip while totaling exactly 100,000 valid events; submit it through gzip, await terminal batches, reconcile exactly 100,000 logical InfluxDB events, and assert both privileged-direct and restricted-viewer query paths in `tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/Final100kGrafanaProofScenario.cs`
- [ ] T102 Execute the 100,000-event final proof and save batch reconciliation, InfluxDB count queries, Grafana API health, dashboard query results, and rendered/screenshot evidence in `artifacts/validation/100k-final-proof/README.md`
- [ ] T103 Create a production-to-test traceability matrix proving every source project and implemented requirement has automated coverage in `artifacts/validation/implementation-test-traceability.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 — Setup**: starts immediately.
- **Phase 2 — Foundational**: depends on Phase 1 and blocks every user story.
- **Phase 3 — US1**: depends on Phase 2; this is the first deployable MVP.
- **Phase 4 — US2**: depends on Phase 2 and consumes the durable envelopes delivered by US1.
- **Phase 5 — US3**: depends on Phase 2; its complete lifecycle demonstration depends on US2 outcomes.
- **Phase 6 — US4**: depends on stored telemetry from US2; identity and dashboard work can otherwise
  proceed after Phase 2 and the T104-T112 amendment gate.
- **Phase 7 — US5**: depends on Phase 2; full parity/recovery demonstration depends on US1 and US2.
- **Phase 8 — Polish**: begins after the stories included in the intended release are complete.

### User Story Dependency Graph

```text
Setup -> Foundation -> US1 Durable Intake -> US2 Processing
                         |                    |  \
                         |                    |   -> US4 Dashboards
                         |                    -> US3 Batch Inspection
                         +------------------------> US5 Operations/Portability

US1 + US2 + US3 + US4 + US5 -> Production Gates
```

### Within Each User Story

- Tests are written first and must fail for the intended missing behavior.
- Domain entities and contract mappings precede orchestration services.
- Services precede endpoints/workers and deployment integration.
- A queue delivery is never acknowledged before every durable result is recorded.
- ADR-001 and its benchmark (T041) must approve the recoverable identity-guard and conflict-evidence strategy before T047 begins.

## Parallel Opportunities

### User Story 1

After foundation completion, T024–T026 can run in parallel. T028–T030 can then run in parallel before
T031–T035 complete the sequential durability boundary.

### User Story 2

T036–T040 can run in parallel. Parser/domain work (T043–T046), quarantine (T048), and Influx writer
(T049) can proceed concurrently, but T047 waits for ADR-001 and T051 waits for all durable outcome
providers.

### User Story 3

T054–T056 run in parallel. Query work (T057) and lifecycle projection work (T058) may proceed in
parallel before endpoints and cleanup are integrated.

### User Story 4

T063–T066 run in parallel. Cadastral/audit work and the three dashboard JSON files target different
files and can be developed concurrently after their contract fixtures exist.

### User Story 5

T076–T079 run in parallel. Pub/Sub, Prometheus, Loki, and dashboard implementation are independent
until the provider-parity and full-stack recovery checkpoints.

## Implementation Strategy

### MVP First

1. Complete Setup and Foundation.
2. Complete US1 durable intake.
3. Stop and demonstrate publisher-confirmed `202`, restart survival, backpressure, and error responses.
4. Do not describe this slice as telemetry persistence; it proves durable acceptance only.

### First Useful Telemetry Release

1. Add US2 processing after ADR-001 passes.
2. Add US3 status inspection.
3. Execute steady-load, burst, restart, and privacy gates.
4. Release as a local pilot with the documented single-host limitation.

### Incremental Completion

1. Add US4 dashboards and identity controls.
2. Add US5 provider parity and operational runbooks.
3. Complete all Phase 8 production gates before claiming 100,000-vehicle capacity or five-year
   retention readiness.
4. The release is not complete until T101–T103 prove the exact 100,000-event Grafana acceptance run and
   trace every production implementation to an automated test project.

## Notes

- `[P]` marks tasks safe to parallelize only after their phase prerequisites are satisfied.
- Commit after each task or coherent test/implementation pair.
- Never bypass T041/T047 with an unmeasured identity guard or rely on InfluxDB merge semantics for immutability.
- Never use the Pub/Sub emulator as evidence of cloud durability.
- Never use `docker compose down --volumes` in normal validation or recovery procedures.
