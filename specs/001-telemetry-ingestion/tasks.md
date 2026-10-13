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
- [ ] T021 [P] Configure structured Serilog output with destructuring filters that remove payload, plate, coordinates, driver identity, CPF, and secrets in `src/CentralMobilidade.Telemetry.Infrastructure/Observability/PrivacyLoggingPolicy.cs`
- [ ] T022 Implement liveness/readiness checks for PostgreSQL, durable queue publication, InfluxDB, and worker freshness in `src/CentralMobilidade.Telemetry.Infrastructure/Health/TelemetryHealthChecks.cs`
- [ ] T023 Add architecture tests enforcing dependency direction and prohibiting infrastructure references from Domain/Application in `tests/CentralMobilidade.Telemetry.ArchitectureTests/LayerDependencyTests.cs`

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

- [ ] T024 [P] [US1] Add OpenAPI contract tests for `POST /api/v1/telemetry/batches`, including 202, 400, 401, 408, 413, 415, 429, and 503 responses, in `tests/CentralMobilidade.Telemetry.ContractTests/Api/V1/IngestBatchContractTests.cs`
- [ ] T025 [P] [US1] Add boundary tests for compressed-size, duration, content type, charset, gzip header, empty body, request cancellation, trusted proxies, and spoofed forwarded headers in `tests/CentralMobilidade.Telemetry.IntegrationTests/Ingestion/IntakeBoundaryTests.cs`
- [ ] T026 [P] [US1] Add RabbitMQ provider contract tests for durable topology, mandatory routing, native gzip body bytes, metadata attributes, publisher confirm/nack, timeout, and envelope integrity in `tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/RabbitMqPublisherContractTests.cs`
- [ ] T027 [US1] Add crash-boundary test that kills the API after publisher confirmation and verifies the accepted envelope remains available in `tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/AcceptedBatchSurvivalTests.cs`

### Implementation for User Story 1

- [ ] T028 [P] [US1] Implement `IngestionBatch` lifecycle with `Receiving -> Publishing -> Accepted` and failure transitions in `src/CentralMobilidade.Telemetry.Domain/Batches/IngestionBatch.cs`
- [ ] T029 [P] [US1] Implement streaming compressed-body hashing and a bounded spool that never materializes the body as a string in `src/CentralMobilidade.Telemetry.Infrastructure/Streaming/BoundedRequestSpool.cs`
- [ ] T030 [P] [US1] Implement RabbitMQ connection/channel recovery and declare durable exchange, queue, retry, and dead-letter topology in `src/CentralMobilidade.Telemetry.Infrastructure/Queue/RabbitMq/RabbitMqTopology.cs`
- [ ] T031 [US1] Implement mandatory persistent publication with correlated publisher confirms and explicit timeout/nack mapping in `src/CentralMobilidade.Telemetry.Infrastructure/Queue/RabbitMq/RabbitMqBatchPublisher.cs`
- [ ] T032 [US1] Implement transactional batch creation and replay-safe acceptance transitions in `src/CentralMobilidade.Telemetry.Infrastructure/Persistence/PostgresBatchStateStore.cs`
- [ ] T033 [US1] Implement intake orchestration so `202` is impossible before publisher confirmation in `src/CentralMobilidade.Telemetry.Application/Ingestion/AcceptTelemetryBatchHandler.cs`
- [ ] T034 [US1] Implement `POST /api/v1/telemetry/batches` with producer-only bearer authorization, required v1 headers, backpressure, `Location`, and `Retry-After` in `src/CentralMobilidade.Telemetry.Api/Endpoints/V1/IngestTelemetryBatchEndpoint.cs`
- [ ] T035 [US1] Wire API middleware ordering, Kestrel compressed request limit, authentication, problem details, explicit trusted-proxy networks, forwarded-header symmetry/known-proxy enforcement, health checks, and endpoint mappings in `src/CentralMobilidade.Telemetry.Api/Program.cs`

**Checkpoint**: The durable intake slice is deployable as an MVP even before telemetry parsing exists.

---

## Phase 4: User Story 2 — Process valid events and isolate failures (Priority: P1)

**Goal**: Incrementally validate mixed batches, write valid events, quarantine invalid/conflicting lines,
and acknowledge the queue only after all durable outcomes are recorded.

**Independent Test**: Process a batch with valid lines, invalid schema, identical replay, and conflicting
duplicate; verify valid points exist once, the original conflict target remains unchanged, and every
rejection is inspectable.

### Tests for User Story 2

- [ ] T036 [P] [US2] Add golden Line Protocol v1 tests for escaping, LF/CRLF, UTF-8, field types, required tags/fields, units, and canonical fingerprints in `tests/CentralMobilidade.Telemetry.UnitTests/LineProtocol/LineProtocolV1GoldenTests.cs`
- [ ] T037 [P] [US2] Add property/fuzz tests for truncated gzip, CRC failure, 100:1 expansion, 64 MiB decompressed limit, 16 KiB line limit, and 50,000-line limit in `tests/CentralMobilidade.Telemetry.UnitTests/Streaming/GzipBoundaryPropertyTests.cs`
- [ ] T038 [P] [US2] Add InfluxDB v3 writer integration tests for gzip, millisecond precision, `accept_partial=true`, `no_sync=false`, 5,000-point/2 MiB/1-second flush thresholds, and partial errors in `tests/CentralMobilidade.Telemetry.IntegrationTests/Influx/InfluxV3WriterTests.cs`
- [ ] T039 [P] [US2] Add quarantine storage integration tests proving encryption/protected storage, metadata-only logging, integrity verification, and 30-day deletion deadline in `tests/CentralMobilidade.Telemetry.IntegrationTests/Quarantine/QuarantineStoreTests.cs`
- [ ] T040 [P] [US2] Create a representative deduplication corpus for 10,000 and 100,000 vehicles in `tests/CentralMobilidade.Telemetry.LoadTests/Data/DedupCorpus/README.md` and deterministic generator in `tests/CentralMobilidade.Telemetry.LoadTests/Generators/DedupCorpusGenerator.cs`
- [ ] T041 [US2] Benchmark atomic partitioned identity-guard alternatives at 5,000 events/s and prove first-writer immutability plus safe owner resume, lease takeover, and fencing under concurrency; separately benchmark 30-day fingerprint evidence, project 60-day and five-year guard storage, and record correctness, storage, p95/p99, and the selected design in `specs/001-telemetry-ingestion/adr/001-conflict-detector.md`
- [ ] T042 [US2] Add redelivery, concurrent same-identity, evidence-expired, and failure-at-every-`Reserved/Writing/Committed`-boundary tests proving exactly one immutable logical event while permitting identical physical rewrites after ambiguous Influx outcomes, plus owner resume, fenced lease takeover, original preservation, and acknowledgement only after durable outcomes in `tests/CentralMobilidade.Telemetry.IntegrationTests/Recovery/WorkerAcknowledgementBoundaryTests.cs`

### Implementation for User Story 2

- [ ] T043 [P] [US2] Implement `TelemetryEvent` validation with required `empresa_id`, `veiculo_id`, latitude -90..90, longitude -180..180, non-negative speed/RPM, boolean ignition, optional pseudonymous `motorista_id`, and optional plate in `src/CentralMobilidade.Telemetry.Domain/Telemetry/TelemetryEvent.cs`
- [ ] T044 [P] [US2] Implement stable `LineProcessingResult` and protected `QuarantinedRejection` entities from `data-model.md` in `src/CentralMobilidade.Telemetry.Domain/Processing/LineProcessingResult.cs` and `QuarantinedRejection.cs`
- [ ] T045 [US2] Implement bounded streaming gzip decompression with independent byte/ratio/line/time cancellation in `src/CentralMobilidade.Telemetry.Infrastructure/Streaming/BoundedGzipLineReader.cs`
- [ ] T046 [US2] Implement closed-schema incremental parser and canonical fingerprinting for `contracts/line-protocol-v1.md` in `src/CentralMobilidade.Telemetry.Application/LineProtocol/TelemetryLineProtocolV1Parser.cs`
- [ ] T047 [US2] Implement the ADR-001 recoverable identity guard with atomic `Reserved/Writing/Committed` transitions, stable owner, renewable lease, fenced takeover, commit only after confirmed Influx persistence, `PendingCommitted/Promoting/QueryableCommitted` storage location, telemetry-lifetime committed guard, and 30-day fingerprint evidence in `src/CentralMobilidade.Telemetry.Infrastructure/Idempotency/SelectedEventIdentityGuard.cs`
- [ ] T048 [P] [US2] Implement protected filesystem quarantine with atomic writes, integrity digest, restrictive permissions, and no path disclosure in `src/CentralMobilidade.Telemetry.Infrastructure/Quarantine/FileSystemQuarantineStore.cs`
- [ ] T049 [P] [US2] Implement the replaceable company-visibility adapter with conservative pending result for unavailable, timed-out, or inconclusive lookups, and the InfluxDB 3 native gzip writer with separate queryable/pending destinations, `/api/v3/write_lp`, millisecond precision, partial acceptance, durable sync, and bounded response parsing in `src/CentralMobilidade.Telemetry.Infrastructure/Cadastral/ConfiguredCadastralAdapter.cs` and `Influx/InfluxV3TelemetryWriter.cs`
- [ ] T050 [US2] Implement count/byte/time batch assembly with default ceilings of 5,000 points, 2 MiB, and 1 second in `src/CentralMobilidade.Telemetry.Application/Processing/InfluxWriteBatcher.cs`
- [ ] T051 [US2] Implement mixed-line processing that acquires/resumes identity leases, confirms the guard only after reconciled Influx persistence, never rewrites committed identities, performs exact 30-day replay/conflict handling, routes unavailable/inconclusive company lookups to pending storage, aggregates rejections, and safely recovers partial results in `src/CentralMobilidade.Telemetry.Application/Processing/ProcessTelemetryBatchHandler.cs`
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
- [ ] T056 [P] [US3] Add retention tests proving batch/rejection metadata and quarantine content are deleted after 30 days, queryable/pending telemetry expires after 60 days locally, and aggregate metrics remain in `tests/CentralMobilidade.Telemetry.IntegrationTests/Retention/OperationalRetentionTests.cs`

### Implementation for User Story 3

- [ ] T057 [P] [US3] Implement producer ownership and operator permission/company-scope batch visibility, cursor-based privacy-safe rejection queries, and operator-only raw-content retrieval that durably audits successful, denied, missing, and expired attempts before returning content and fails closed on audit failure in `src/CentralMobilidade.Telemetry.Infrastructure/Persistence/PostgresRejectionQuery.cs` and `src/CentralMobilidade.Telemetry.Application/Batches/GetQuarantinedRejectionContentHandler.cs`
- [ ] T058 [US3] Implement batch status projection with submitting-producer ownership, operator `batch.read`, all-companies-in-scope or explicit `cross_company.read`, and non-disclosing not-found behavior in `src/CentralMobilidade.Telemetry.Application/Batches/GetBatchStatusHandler.cs`
- [ ] T059 [US3] Implement `GET /api/v1/telemetry/batches/{batchId}` matching `contracts/openapi.yaml` in `src/CentralMobilidade.Telemetry.Api/Endpoints/V1/GetBatchStatusEndpoint.cs`
- [ ] T060 [US3] Implement rejection pagination and audited administrative raw-content endpoints with operator-only policies, company scope, explicit `quarantine.raw.read`, and producer-token denial from `contracts/openapi.yaml` in `src/CentralMobilidade.Telemetry.Api/Endpoints/V1/ListBatchRejectionsEndpoint.cs` and `GetQuarantinedRejectionContentEndpoint.cs`
- [ ] T061 [US3] Implement leased, idempotent cleanup for 30-day batch/quarantine/fingerprint evidence while retaining each immutable identity guard until its queryable or pending telemetry expires, including 60-day local InfluxDB retention, in `src/CentralMobilidade.Telemetry.Worker/Retention/OperationalRetentionWorker.cs`
- [ ] T062 [US3] Add batch-state and rejection metrics without tenant/vehicle high-cardinality labels in `src/CentralMobilidade.Telemetry.Infrastructure/Observability/BatchMetrics.cs`

**Checkpoint**: Asynchronous processing is supportable through a stable API and expired operational data
is removed automatically.

---

## Phase 6: User Story 4 — Observe fleets and trips (Priority: P2)

**Goal**: Provide latest-position, one-vehicle trip, and ingestion-health dashboards while keeping
unknown companies hidden and driver disclosure minimal and audited.

**Independent Test**: Ingest multiple companies including an unknown company, display a known vehicle's
latest point and old trip in event-time order, and resolve a driver to name plus optional CPF suffix.

### Tests for User Story 4

- [ ] T063 [P] [US4] Add cadastral/promotion contract tests for known/unknown company, pending invisibility, changing provider, missing driver, masked CPF suffix, timeout, cache invalidation, fenced `PendingCommitted -> Promoting -> QueryableCommitted` identity relocation, and crash recovery at every promotion boundary without loss or double display in `tests/CentralMobilidade.Telemetry.ContractTests/Cadastral/CadastralAdapterContractTests.cs`
- [ ] T064 [P] [US4] Add authorization tests for scoped and cross-company humans/services, producer-token denial, audited middleware denials, fail-closed audit persistence, organization/service-account audit attribution, every numeric viewer-query boundary, unauthorized-company non-disclosure, and audited driver identity disclosure in `tests/CentralMobilidade.Telemetry.IntegrationTests/Privacy/PresentationAuthorizationTests.cs`
- [ ] T065 [P] [US4] Add old single-vehicle trip query tests proving event-time ordering and documented Core query-envelope behavior in `tests/CentralMobilidade.Telemetry.IntegrationTests/Queries/VehicleTripQueryTests.cs`
- [ ] T066 [P] [US4] Add Grafana provisioning tests proving pinned image/plugin versions and Infinity health, internal YAML provisioning, Basic-only server-admin bootstrap, organization-local service-account reconciliation, strict three-way separation of `GrafanaBootstrapAdminCredential`, `GrafanaOrganizationServiceAccount`, and `ViewerPresentationServiceCredential`, absence of viewer Influx datasources, and rendering of all panels in `tests/CentralMobilidade.Telemetry.IntegrationTests/Grafana/GrafanaProvisioningTests.cs`

### Implementation for User Story 4

- [ ] T067 [P] [US4] Extend the replaceable cadastral adapter with driver resolution and implement a durable idempotent promotion state machine that obtains a fenced identity relocation, writes the identical fingerprint to queryable storage, confirms identity/count, advances the guard to `QueryableCommitted`, deletes pending source only afterward, and resumes safely after every intermediate failure in `src/CentralMobilidade.Telemetry.Infrastructure/Cadastral/ConfiguredCadastralAdapter.cs` and `src/CentralMobilidade.Telemetry.Worker/Promotion/PendingTelemetryPromotionWorker.cs`
- [ ] T068 [P] [US4] Extend append-only audit policies for identity and cross-company reads while excluding raw telemetry, coordinates, plate, name, CPF, token, and quarantine content in `src/CentralMobilidade.Telemetry.Infrastructure/Auditing/PostgresAuditWriter.cs`
- [ ] T069 [US4] Implement latest-position, one-vehicle-trip, ingestion-health, and driver-presentation handlers with `telemetry.read`/`driver.read`, server-side company scope, no arbitrary SQL, non-disclosing failures, configurable 500-vehicle, 24-hour/10,000-point, and seven-day/one-minute/10,000-point limits, and durable audit-before-identity-disclosure in `src/CentralMobilidade.Telemetry.Application/Presentation/`
- [ ] T070 [US4] Implement the operator/service-only presentation endpoints from `contracts/openapi.yaml`, including producer denial, server-side company scope, bounded query validation, and fail-closed identity audit behavior in `src/CentralMobilidade.Telemetry.Api/Endpoints/V1/Presentation/`
- [ ] T071 [P] [US4] Provision only the internal default organization, direct Influx datasource, internal dashboards, Prometheus, and Loki through `deploy/grafana/provisioning/`; create `scripts/Provision-GrafanaOrganizations.ps1` that uses a secret-sourced Basic `GrafanaBootstrapAdminCredential` only for idempotent server-level organization/membership reconciliation, then creates or rotates a least-privilege `GrafanaOrganizationServiceAccount` per organization to reconcile its Infinity datasource containing a distinct scoped `ViewerPresentationServiceCredential` and its viewer dashboards after health readiness
- [ ] T072 [P] [US4] Create internal-direct and viewer-API latest-position dashboards in `deploy/grafana/dashboards/internal/latest-vehicle-position.json` and `deploy/grafana/dashboards/viewer/latest-vehicle-position.json`
- [ ] T073 [P] [US4] Create internal-direct and viewer-API single-vehicle trip dashboards with bounded time range and event-time ordering in `deploy/grafana/dashboards/internal/vehicle-trip.json` and `deploy/grafana/dashboards/viewer/vehicle-trip.json`
- [ ] T074 [P] [US4] Create internal and viewer-API per-company ingestion-health dashboards plus a viewer masked-driver panel in `deploy/grafana/dashboards/internal/company-ingestion-health.json`, `deploy/grafana/dashboards/viewer/company-ingestion-health.json`, and `deploy/grafana/dashboards/viewer/driver-presentation.json`
- [ ] T075 [US4] Document separate rotation, storage, and least privilege for `GrafanaBootstrapAdminCredential`, `GrafanaOrganizationServiceAccount`, and `ViewerPresentationServiceCredential`, idempotent viewer-organization reconciliation, company-scope provisioning, service-credential audit attribution and its lack of individual attribution, company visibility, identity disclosure, plugin upgrades, and audit review procedures in `docs/operations/grafana-access.md`

**Checkpoint**: Internal users can use all three initial dashboards, unknown companies remain hidden,
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

- [ ] T090 [P] Add unit coverage for option validation, authorization permissions/company scopes, fail-closed audit decisions, promotion state transitions, canonicalization, error mapping, and separate identity-guard/evidence retention clocks in `tests/CentralMobilidade.Telemetry.UnitTests/`
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
- [ ] T101 Add a deterministic final-acceptance dataset spanning at least 10 known companies, 1,000 vehicles, ordered and delayed points, and one named trip while totaling exactly 100,000 valid events; submit it through gzip, await terminal batches, reconcile exactly 100,000 logical InfluxDB events, and assert both privileged-direct and restricted-viewer query paths in `tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/Final100kGrafanaProofScenario.cs`
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
  proceed after Phase 2.
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
