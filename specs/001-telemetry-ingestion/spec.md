# Feature Specification: Vehicle Telemetry Ingestion

**Feature Branch**: `001-telemetry-ingestion`

**Created**: 2026-10-11

**Status**: Draft

**Input**: Build a durable, multi-company vehicle telemetry ingestion platform that accepts gzip-compressed InfluxDB Line Protocol, supports delayed delivery and partial acceptance, makes data available for Grafana, and can move from a local containerized environment to Google Cloud.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Durably receive telemetry (Priority: P1)

The internal telemetry center sends a compressed batch containing vehicle events and receives an
acknowledgement only after the batch is safe for asynchronous processing.

**Why this priority**: Reliable receipt is the platform's primary business capability; no downstream
feature is useful if acknowledged events can disappear during an application restart.

**Independent Test**: Send a valid compressed batch, stop the ingestion process immediately after the
acknowledgement, restart processing, and verify that every acknowledged event is processed once or
safely recognized as a replay.

**Acceptance Scenarios**:

1. **Given** an authenticated internal producer and a valid gzip batch, **When** the durable handoff is
   confirmed, **Then** the service returns `202 Accepted` with a unique batch identifier.
2. **Given** the durable handoff cannot be confirmed, **When** the producer submits a batch, **Then**
   the service does not return a successful acknowledgement and communicates a retryable outcome.
3. **Given** an empty, non-gzip, oversized, truncated, or unauthorized request, **When** it is received,
   **Then** it is rejected without entering the durable processing pipeline.

---

### User Story 2 - Process valid events and isolate failures (Priority: P1)

Operations personnel need valid lines to reach telemetry storage even when other lines in the same
batch are invalid or conflict with a previously accepted event.

**Why this priority**: A single malformed point must not discard thousands of valid vehicle events.

**Independent Test**: Submit a batch containing valid lines, schema violations, an identical replay,
and a conflicting duplicate; verify the resulting counts and stored data.

**Acceptance Scenarios**:

1. **Given** a mixed batch, **When** processing completes, **Then** valid lines are stored and invalid
   lines are quarantined with stable error codes and line positions.
2. **Given** an identical replay for the same company, vehicle, and event time, **When** it is processed,
   **Then** no second logical event is created.
3. **Given** a different event for an already accepted company, vehicle, and event time, **When** it is
   processed, **Then** the original is preserved and the conflicting line is rejected for review.
4. **Given** the telemetry store is unavailable, **When** processing fails, **Then** the batch remains
   recoverable and retrying does not lose or silently duplicate events.

---

### User Story 3 - Inspect batch processing (Priority: P2)

An authorized internal operator uses the batch identifier to determine whether processing is pending,
complete, partially accepted, or failed and to understand rejection reasons.

**Why this priority**: Asynchronous acceptance requires a durable and supportable feedback mechanism.

**Independent Test**: Follow a batch from submission through partial completion and verify every state,
count, timestamp, and error category returned to the operator.

**Acceptance Scenarios**:

1. **Given** a known batch identifier, **When** an authorized operator requests its status, **Then** the
   current state, totals, accepted count, rejected count, timestamps, and categorized errors are shown.
2. **Given** an unknown, expired, or unauthorized identifier, **When** its status is requested, **Then**
   no batch or tenant information is disclosed.
3. **Given** status and quarantine retention have expired, **When** cleanup runs, **Then** the expired
   records and raw rejected content are deleted according to policy while aggregate metrics remain.

---

### User Story 4 - Observe fleets and trips (Priority: P2)

Internal users view the latest position of vehicles, inspect one vehicle's trip, and monitor ingestion
health by company without exposing unauthorized driver details.

**Why this priority**: The ingested data must become operationally useful and demonstrably trustworthy.

**Independent Test**: Ingest representative data for multiple companies and verify the three initial
views, including an old single-vehicle trip and an unknown company that must not be displayed.

**Acceptance Scenarios**:

1. **Given** known companies and recent events, **When** an internal user opens the latest-position view,
   **Then** the latest available position is shown for each selected vehicle.
2. **Given** a vehicle and trip time range, **When** a user requests its trajectory, **Then** events are
   ordered by event timestamp even when they arrived late or out of order.
3. **Given** an event for an unknown company, **When** dashboards are queried, **Then** it remains stored
   in a pending telemetry area that is inaccessible to dashboards until the company exists in the
   cadastral source and the event is promoted to the queryable telemetry area.
4. **Given** a driver identifier and an authorized user, **When** identity is requested, **Then** only the
   permitted display name and masked CPF suffix are returned and the access is audited.

---

### User Story 5 - Operate and migrate the platform (Priority: P3)

Operators run the complete platform locally, diagnose its health, and later change infrastructure
providers without changing the telemetry contract or domain behavior.

**Why this priority**: The first environment is local but its lifetime is indefinite and future cloud
migration must not force a rewrite of the business flow.

**Independent Test**: Start a clean local environment, ingest and visualize data, simulate dependency
failures, then run the same contract suite against an alternative queue adapter.

**Acceptance Scenarios**:

1. **Given** a supported container host, **When** an operator starts the documented local composition,
   **Then** all required services become healthy with persistent development storage.
2. **Given** a dependency restart, **When** the platform recovers, **Then** acknowledged work resumes and
   health, backlog, retry, and latency indicators explain the recovery.
3. **Given** an alternative durable-queue provider, **When** the provider configuration changes, **Then**
   the external ingestion and batch-status contracts remain unchanged.
4. **Given** a generated acceptance dataset containing exactly 100,000 valid telemetry events, **When**
   it is submitted through the public ingestion contract, **Then** all 100,000 events reach terminal
   accepted storage and the provisioned Grafana views display and reconcile the resulting telemetry.

### Edge Cases

- A gzip body is valid but expands beyond its configured decompressed-size or line-count limit.
- A client sends slowly, disconnects mid-body, omits a final newline, or supplies invalid UTF-8.
- A single Line Protocol record exceeds the configured maximum line length.
- Events contain timestamps far in the past or future, invalid coordinates, or incompatible field types.
- A vehicle reconnects and sends hours of accumulated events while normal traffic continues.
- The same batch is submitted concurrently, partially retried, or redelivered after consumer failure.
- A company identifier is new, removed, or temporarily unavailable in the cadastral source.
- Telemetry storage accepts only part of a write or becomes unavailable after a partial response.
- The durable queue reaches capacity or its oldest message approaches the retention limit.
- A cleanup job runs while a batch status or quarantined rejection is being inspected.
- Driver cadastral data is missing, changed, or unavailable while telemetry remains queryable.
- Local disk approaches capacity or the single local host is lost.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST expose a versioned endpoint that accepts only authenticated, gzip-encoded,
  UTF-8 InfluxDB Line Protocol batches from the internal telemetry producer.
- **FR-002**: The system MUST validate authentication, encoding, compressed size, decompressed size,
  line length, line count, and processing time using configurable limits.
- **FR-003**: The system MUST return `202 Accepted` only after durable persistence is confirmed and MUST
  include a globally unique `batchId` and status location.
- **FR-004**: The system MUST return a non-successful, classifiable response when durable persistence
  cannot be confirmed, without representing the batch as accepted.
- **FR-005**: The system MUST process request content and individual lines with bounded resource usage.
- **FR-006**: The accepted schema MUST be closed and versioned and MUST require `empresa_id`,
  `veiculo_id`, latitude, longitude, speed, RPM, ignition, and `DataHoraEvento`.
- **FR-007**: The schema MUST allow optional `motorista_id` and plate values and MUST prohibit driver
  name, CPF, phone, and other direct driver identity fields.
- **FR-008**: `DataHoraEvento` MUST become the event timestamp in UTC and late events MUST retain their
  original event time.
- **FR-009**: The system MUST permit partial batch success and MUST isolate invalid lines without
  preventing valid lines from being stored.
- **FR-010**: The system MUST assign each rejected line a stable error code, line position, batch
  reference, company reference when extractable, vehicle reference when extractable, and safe digest.
- **FR-011**: The complete rejected line MUST be stored only in protected quarantine and automatically
  deleted after 30 days.
- **FR-012**: A logical event MUST be identified by company, stable vehicle identifier, and event
  timestamp.
- **FR-013**: Throughout the configured telemetry-retention period, an immutable identity guard MUST
  prevent any second committed write for the same company, stable vehicle identifier, and event
  timestamp. A reservation MUST be recoverable by its owning batch after process or dependency failure
  and MUST become permanently blocking only after the corresponding telemetry write is confirmed.
  Promotion from pending to queryable storage is an authorized relocation of the same logical event,
  tracked by the guard's storage location, and MUST NOT be classified as a duplicate.
  Within 30 days of first acceptance, an identical replay MUST additionally be classified as a replay;
  after 30 days it MAY be reported only as an already-existing identity.
- **FR-014**: Throughout the configured telemetry-retention period, a second write for an existing
  identity MUST NOT modify or merge fields into the original event. Within 30 days of first acceptance,
  a different fingerprint MUST additionally create an inspectable conflict rejection; after 30 days,
  detailed comparison and rejection evidence are not guaranteed.
- **FR-015**: Event processing MUST tolerate redelivery and MUST resume after dependency or process
  restart without losing acknowledged batches.
- **FR-016**: Unprocessed accepted batches MUST remain available for retry for up to seven days.
- **FR-017**: The system MUST expose an authenticated batch-status endpoint returning state, total lines,
  accepted lines, rejected lines, timestamps, and categorized error summaries.
- **FR-018**: Batch status MUST be retained for 30 days and then deleted automatically.
- **FR-019**: The system MUST accept telemetry for a previously unknown company into a separate pending
  telemetry area that dashboards cannot access, and MUST promote it idempotently to the queryable
  telemetry area only after the cadastral source recognizes the company. An unavailable, timed-out, or
  inconclusive cadastral lookup MUST be treated conservatively as pending and MUST NOT block durable
  ingestion or expose the event in a dashboard.
- **FR-020**: The cadastral dependency MUST be replaceable without changing ingestion or telemetry
  storage contracts.
- **FR-021**: Driver identity MUST be resolved outside telemetry storage from `motorista_id` and MUST
  disclose only authorized fields, including at most name and the last four CPF digits.
- **FR-022**: Access to driver identity, raw quarantine content, and cross-company information MUST be
  restricted to authenticated internal operators with explicit permissions and company scopes, separate
  from the producer credential, and recorded in an audit trail.
- **FR-023**: The system MUST provide views for latest vehicle position, one-vehicle trip trajectory,
  and ingestion health by company.
- **FR-024**: Trip events MUST be returned in event-time order regardless of arrival order.
- **FR-025**: The system MUST expose health and operational indicators covering throughput, payload
  volume, backlog size and age, processing latency, accepted and rejected points, duplicate outcomes,
  retries, dependency failures, and capacity pressure.
- **FR-026**: Technical logs MUST support correlation by trace, batch, producer, company, vehicle, and
  line position without containing payloads, plate values, locations, driver identity, or secrets.
- **FR-027**: Raw rejected content MUST never appear in general logs, metrics, or traces.
- **FR-028**: The local environment MUST preserve service data across ordinary container restarts and
  MUST apply a 60-day telemetry retention policy.
- **FR-029**: The production design MUST support a five-year telemetry retention policy when external
  durable storage and validated capacity are available.
- **FR-030**: The durable queue provider MUST be replaceable by configuration and adapter deployment
  without changing external behavior.
- **FR-031**: Development MUST permit unencrypted local access; non-development environments MUST
  support operator-managed encrypted transport.
- **FR-032**: Storage writes MUST group no more than 5,000 points by default and MUST also close a group
  when its configured byte or time threshold is reached first.
- **FR-033**: The release validation suite MUST generate and ingest exactly 100,000 valid events through
  the external gzip contract, verify durable batch totals and stored-event totals with no unexplained
  loss or duplication, and capture evidence that Grafana can query and display the resulting dataset.
- **FR-034**: Every production implementation change MUST have corresponding automated verification in
  the appropriate unit, contract, integration, architecture, or load-test project; untested production
  behavior MUST block release unless explicitly excluded by an approved test rationale.
- **FR-035**: The system MUST expose an administrative raw-quarantine lookup by opaque rejection
  identifier; every successful or denied lookup MUST be authorized, audited, and subject to the 30-day
  deletion deadline.
- **FR-036**: Producer authentication MUST authorize only batch submission and producer-visible status;
  it MUST NOT authorize driver identity, raw quarantine, administrative rejection, or cross-company
  operations. Operator authentication MUST use a separate credential and enforce action permissions plus
  allowed-company scope; a global cross-company permission MUST be explicit.
- **FR-037**: Sensitive reads of driver identity or raw quarantine MUST fail closed when the audit record
  cannot be durably persisted. Denied, missing, expired, and successful attempts MUST be audited without
  placing sensitive content in the audit event.
- **FR-038**: Batch status and rejection visibility MUST be deterministic: the submitting producer MAY
  inspect its own submitted batches; an operator requires `batch.read` and every company in the batch
  MUST fall within the operator's allowed-company scope unless `cross_company.read` is granted. A caller
  failing this rule MUST receive the same not-found response used for an unknown batch.
- **FR-039**: Privileged internal Grafana users MAY query the queryable InfluxDB database directly and
  are explicitly authorized to view every known company. Restricted viewers MUST use separate Grafana
  organizations that contain no InfluxDB datasource and MUST query only bounded presentation API
  endpoints through a `ViewerPresentationServiceCredential` limited to that organization's allowed
  companies. This credential MUST be distinct from both the `GrafanaBootstrapAdminCredential`, used only
  through Basic authentication for server-level organization bootstrap, and each organization-local
  `GrafanaOrganizationServiceAccount`, used to reconcile resources inside that organization.
- **FR-040**: Viewer telemetry endpoints MUST provide latest position, one-vehicle trip, and company
  ingestion health without accepting arbitrary SQL. They MUST apply service-credential company scope on
  the server and MUST return a non-disclosing response for unauthorized company or vehicle selections.
  Configurable defaults MUST limit latest-position pages to 500 vehicles, trip queries to one vehicle,
  24 hours, and 10,000 points, and ingestion-health queries to seven days, one-minute minimum
  granularity, and 10,000 points.
- **FR-041**: In the local-user MVP, restricted-viewer access MUST be audited to the Grafana organization
  and its non-human `ViewerPresentationServiceCredential`. Individual human attribution is a future
  capability requiring a trusted per-user identity mechanism; the MVP MUST NOT claim individual
  attribution it cannot prove.
- **FR-042**: The local Docker deployment MUST pin compatible Grafana OSS and Grafana Infinity plugin
  versions, install the signed plugin reproducibly during image construction, and fail validation when
  the expected plugin version is unavailable or unhealthy.
- **FR-043**: Grafana server-level organization and membership reconciliation MUST use a protected,
  rotatable `GrafanaBootstrapAdminCredential` because Grafana OSS service-account tokens cannot call the
  Admin Organizations API. After bootstrap, organization-local operations MUST use least-privilege
  `GrafanaOrganizationServiceAccount` tokens; the bootstrap credential MUST NOT be stored in a datasource
  or sent to the presentation API.

### Key Entities

- **Telemetry Event**: A vehicle observation identified by company, stable vehicle, and UTC event time;
  contains measured values and optional pseudonymous driver and plate references.
- **Ingestion Batch**: The durable unit acknowledged to the producer; tracks receipt, lifecycle,
  counts, contract version, and correlation identifiers.
- **Line Processing Result**: The outcome for a line, including acceptance or a stable rejection code
  and safe diagnostic references.
- **Quarantined Rejection**: Protected raw rejected content with access audit and deletion deadline.
- **Company Reference**: Tenant identifier supplied by telemetry and validated before presentation.
- **Vehicle Reference**: Stable vehicle identifier used for event identity and safe operational
  correlation; resolves to mutable vehicle attributes outside logs.
- **Driver Reference**: Pseudonymous `motorista_id` resolved through a separate cadastral source.
- **Audit Event**: Immutable record of security-sensitive reads, configuration changes, and cleanup.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: At a sustained 5,000 events per second with representative payloads, 99% of acknowledged
  events become queryable within five minutes and no acknowledged event is lost during application or
  container restart tests.
- **SC-002**: On the reference 8-vCPU/16-GiB application test profile, the platform supports telemetry
  from 100,000 vehicles reporting every 20 seconds without out-of-memory termination, while sustained
  application CPU remains at or below 85% and application memory remains at or below 12 GiB.
- **SC-003**: While normal 5,000-event/s traffic continues, 10,000 vehicles may deliver one hour of
  accumulated 20-second telemetry over ten minutes; the system either processes it or returns explicit
  backpressure, loses no acknowledged batch, and drains the resulting backlog within 20 minutes after
  the burst ends.
- **SC-004**: A mixed batch stores 100% of its valid lines and reports 100% of invalid or conflicting
  lines with stable, actionable categories.
- **SC-005**: Replaying any acknowledged batch during telemetry retention produces no additional logical
  events and cannot alter the fields of the original event; replays within 30 days additionally receive
  their exact replay-or-conflict classification.
- **SC-006**: Authorized operators can retrieve current batch state and error summaries in under two
  seconds for 95% of requests under normal load.
- **SC-007**: Internal users can display a vehicle's latest position and a single historical trip in
  under five seconds for 95% of requests within the validated query envelope.
- **SC-008**: Unknown-company telemetry remains exclusively in the pending telemetry area and appears
  in no user dashboard until an idempotent promotion completes after cadastral validation.
- **SC-009**: Automated privacy checks find no raw payload, plate, coordinate, driver name, CPF, token,
  or secret in general logs, metrics, and traces.
- **SC-010**: Recovery exercises prove seven-day unprocessed-message retention, 30-day status and
  quarantine cleanup, and successful continuation after each required dependency restarts.
- **SC-011**: A final acceptance run generates exactly 100,000 valid events, receives terminal accepted
  totals for all 100,000, finds exactly 100,000 corresponding logical events in telemetry storage, and
  successfully renders the latest-position, trip, and ingestion-total evidence in Grafana.
- **SC-012**: An automated isolation test proves that a restricted viewer can render authorized-company
  telemetry through the presentation API, cannot discover or query an unauthorized company, and has no
  InfluxDB datasource in its Grafana organization, while a privileged internal operator retains direct
  InfluxDB dashboard access.

## Assumptions

- The initial producer is a single internal telemetry center authenticated with a rotatable technical
  credential; per-company producer credentials are outside the MVP.
- Duplicate replay/conflict classification is guaranteed for the 30-day operational window. Throughout
  telemetry retention, an immutable identity guard prevents an additional write or mutation of the
  original point; beyond 30 days, detailed classification and rejection evidence are not guaranteed.
- Local API operators use credentials distinct from the telemetry producer. Operator profiles carry
  explicit permissions and allowed-company scopes; there is no implicit administrative access.
- Restricted viewers in the MVP are internal users grouped by identical company scope. Customer-facing
  access, self-service credentials, and individual viewer attribution are outside the MVP.
- The local single-host environment protects against application and container failure, not physical
  host or disk loss; external storage is required before claiming that protection.
- Local users are privileged internal operators or restricted internal viewers. Customer-facing
  dashboard access and self-service credentials are outside the MVP.
- Mobile-phone GPS ingestion, vehicle commands, and bidirectional protocols are future features.
- The cadastral source exists but its technology and interface can change; temporary unavailability
  must not prevent durable telemetry ingestion.
- Five-year retention and long-range query performance require representative capacity tests and an
  external durable storage design before production commitment.
