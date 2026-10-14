<!--
Sync Impact Report
- Version change: 1.0.0 -> 2.0.0 (incompatible company-recognition rule).
- Modified principles: III. Tenant Isolation and Privacy by Design; IV. Explicit, Versioned,
  Idempotent Contracts; Technical and Operational Constraints.
- Added sections: none. Removed sections: none.
- Rationale: internal telemetry producer is authoritative for company existence; remove cadastral
  eligibility, pending storage and promotion. One shared ingestion credential covers all companies.
- Migration: reconcile any existing pending telemetry and identity guards before retiring storage;
  never discard acknowledged data or reset existing volumes. Adequation tasks block T050.
- Follow-up TODOs: none. Remove this review report before committing the amendment.
-->
# Centrral Mobilidade Constitution

## Core Principles

### I. Durable Acceptance and Recoverability
The ingestion API MUST acknowledge a batch only after a durable queue confirms persistence. An
acknowledged batch MUST survive application and container restarts and MUST remain recoverable for
the configured queue-retention period. Delivery MUST assume at-least-once semantics: consumers MUST
be idempotent, retries MUST use bounded exponential backoff with jitter, and poison messages MUST be
isolated without blocking healthy traffic. No implementation may claim protection from physical host
loss unless an independent replica or backup has been configured and validated.

Rationale: a fast `202 Accepted` is useful only when it has an explicit and testable durability
meaning.

### II. Streaming with Bounded Resources
Request bodies, gzip decompression, validation, queue transfer, batching, and InfluxDB writes MUST be
performed as bounded streaming operations. The service MUST NOT materialize an entire decompressed
request as a `string` or unbounded in-memory object graph. Limits MUST exist independently for
compressed bytes, decompressed bytes, line length, line count, processing time, and queued backlog.
Backpressure and overload responses MUST be explicit and observable. Batches sent to storage MUST
close on the first configured threshold reached: point count, byte count, or elapsed time.

Rationale: predictable resource usage is required during reconnection bursts and malformed or
adversarial gzip payloads.

### III. Tenant Isolation and Privacy by Design
Every telemetry point MUST carry a tenant identifier and a stable vehicle identifier. Tenant scope
MUST be enforced when data is presented. Every syntactically valid company identifier supplied by
the authenticated internal producer MUST be recognized as existing without a cadastral existence check.
The producer MUST use one shared ingestion credential across companies; per-company ingestion
credentials or company allowlists MUST NOT be required. Presentation permissions remain independent.
Telemetry MUST contain only a pseudonymous driver identifier; driver name and CPF MUST NOT be stored
in InfluxDB, logs, metrics, traces, or ordinary dead-letter metadata. Identity resolution MUST occur
through an authorized cadastral adapter and expose only the minimum required representation. Access
to identity, rejected raw lines, and cross-tenant views MUST be authorized and audited. Secrets MUST
never be logged, and all production traffic MUST support encrypted transport.

Rationale: vehicle location and driver identity are personal data whose combination requires
separation, minimization, and traceable access.

### IV. Explicit, Versioned, Idempotent Contracts
The accepted InfluxDB Line Protocol schema MUST be closed, documented, versioned, and validated.
`DataHoraEvento` MUST define the UTC event timestamp. `empresa_id` and `veiculo_id` MUST be present;
`motorista_id` and plate MAY be present according to the contract. A duplicate identity is defined by
tenant, stable vehicle identifier, and event timestamp. An identical replay MUST be safe; a conflicting
second event MUST preserve the original and record a diagnosable rejection. Partial success MUST be
represented by a durable batch status containing accepted and rejected counts and stable error codes.
The wire company tag remains `empresa_id` (the business identifier called `idEmpresa`); this
amendment MUST NOT silently rename it. Missing or invalid company identifiers MUST be rejected by
the existing validation contract. Breaking contract changes MUST use a new version and a documented migration window.

Rationale: producers can retry safely only when identity, validation, and evolution rules are
deterministic.

### V. Observable, Testable, and Portable Operations
All processing stages MUST propagate `traceId` and `batchId` and MUST expose metrics for input rate,
compressed and decompressed bytes, queue backlog and age, processing latency, accepted and rejected
points, duplicates, retries, and storage failures. Logs MUST be structured and privacy-safe. External
dependencies MUST be behind explicit adapters where portability is required, including durable queue
and cadastral lookup. Contract, integration, load, soak, restart, retry, partial-failure, and recovery
tests MUST pass before production release. Capacity claims MUST be supported by recorded tests using
representative payloads and cardinality.

Rationale: operational evidence, rather than assumptions, determines whether the gateway is safe to
scale or migrate.

## Technical and Operational Constraints

- The application baseline MUST be a supported .NET LTS release; the initial baseline is .NET 10.
- Deployable application components MUST be delivered as Docker containers. The local composition
  MUST be reproducible with Docker Compose.
- The ingestion contract MUST require UTF-8 InfluxDB Line Protocol carried with gzip content encoding.
- RabbitMQ MUST be the initial durable local provider. A Google Pub/Sub adapter and emulator-based
  integration tests MUST preserve migration without changing domain rules.
- InfluxDB 3 Core MUST be treated as a single-node dependency. Development retention is 60 days;
  target production retention is five years and requires independent capacity validation.
- Telemetry MUST use one InfluxDB server and one queryable telemetry database. Unknown-company
  pending storage and promotion MUST NOT be part of the processing model. Cadastral enrichment
  MUST NOT gate telemetry ingestion, storage, or visibility within an authorized presentation scope.
- PostgreSQL MUST hold batch lifecycle, validation results, configuration, idempotency metadata, and
  audit records; it MUST NOT become the primary telemetry store.
- Raw rejected lines MAY exist only in protected quarantine for at most 30 days. Batch status MAY be
  retained for 30 days. The durable queue MUST retain unprocessed data for up to seven days.
- Normal availability from receipt to query MUST be no more than five minutes at the agreed load.
- Target capacity is 100,000 vehicles producing one event every 20 seconds, including realistic
  reconnection bursts. Storage batches MUST default to no more than 5,000 points unless measurement
  proves and documents a safer bound.
- Development MAY use HTTP. Non-development deployments MUST allow DevOps-managed TLS termination and
  MUST correctly enforce forwarded-header and trusted-proxy configuration.

## Delivery Workflow and Quality Gates

Each feature MUST proceed through specification, clarification when necessary, implementation plan,
tasks, implementation, and cross-artifact analysis. Requirements MUST have measurable acceptance
criteria and MUST distinguish verified guarantees from assumptions or future capabilities.

Architecture-significant choices MUST be recorded in research notes or ADRs with alternatives and
consequences. Schema, OpenAPI, queue envelopes, retention policies, and error codes MUST be reviewed
as contracts before implementation. Automated tests MUST cover contract compatibility, gzip limits,
partial acceptance, durable acknowledgement, duplicate conflict handling, retry behavior, graceful
shutdown, and recovery after dependency restarts. Load and soak tests MUST report p50, p95, and p99
latency, throughput, backlog age, CPU, memory, disk growth, and rejection rates.

A release MUST NOT proceed while a constitution rule is violated without a written, time-bounded
exception that identifies risk, owner, mitigation, and removal date. Security and LGPD decisions that
depend on legal basis or policy MUST be reviewed by the responsible privacy role; a technical design
MUST NOT present itself as legal approval.

## Governance

This constitution is the highest project-level engineering authority. Specifications, plans, tasks,
and implementation reviews MUST include an explicit compliance check. Amendments require a written
rationale, impact assessment, migration implications, and approval by the project owner.

Constitution versions follow semantic versioning: MAJOR for incompatible governance changes, MINOR
for new or materially expanded principles, and PATCH for non-semantic clarification. Every amendment
MUST update the Sync Impact Report, version, and amendment date. Unjustified complexity or a silent
exception is non-compliant; when a simpler design cannot meet a principle, the plan MUST document why.

**Version**: 2.0.0 | **Ratified**: 2026-10-11 | **Last Amended**: 2026-10-14
