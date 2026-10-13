# Data Model: Vehicle Telemetry Ingestion

## TelemetryEvent

Logical time-series point stored in InfluxDB.

| Attribute | Type | Required | Rule |
|---|---|---:|---|
| time | UTC timestamp, millisecond precision | yes | Derived from `DataHoraEvento` |
| empresa_id | string tag | yes | Non-empty bounded identifier |
| veiculo_id | string tag | yes | Stable; used in logical identity |
| motorista_id | string tag | no | Pseudonymous; never resolved inside InfluxDB |
| placa | string field | no | Uppercase normalized representation; forbidden in logs |
| latitude | float field | yes | -90 through 90 |
| longitude | float field | yes | -180 through 180 |
| velocidade | float field | yes | Non-negative in the contract unit |
| rpm | integer field | yes | Non-negative |
| ignicao | boolean field | yes | `true` or `false` |
| event_fingerprint | string field | yes | Canonical content digest used for replay comparison |

Logical identity: `(empresa_id, veiculo_id, time)`. The fingerprint is not part of identity.

### Value object representation (T012)

Company, vehicle, and optional driver identifiers are immutable, distinct types, bounded to 256 UTF-8
bytes each. They reject null, whitespace-only values, control characters, and malformed Unicode.
Decoded values preserve case, whitespace, and Unicode representation; they are never trimmed or
case-folded. Line Protocol escaping belongs to the parser. A missing driver is represented by null,
not an empty `DriverId`; its diagnostic string does not expose its value.

`BatchId` wraps a nonempty UUID and formats/parses the hyphenated UUID representation.
`EventTimestamp` normalizes `DateTimeOffset` values to UTC and rejects sub-millisecond precision rather
than silently changing event identity. Unix millisecond conversion supports the representable
`DateTimeOffset` range, including pre-epoch values; storage-specific range validation belongs to the
Line Protocol/provider boundary. These types do not depend on infrastructure or wall-clock time.

## IngestionBatch

| Attribute | Type | Rule |
|---|---|---|
| batch_id | UUID | Primary identifier returned to producer |
| trace_id | string | Correlates intake and processing |
| producer_id | string | Technical caller identifier, never a secret |
| contract_version | string | `v1` initially |
| state | enum | See lifecycle below |
| received_at | timestamp | UTC |
| durable_at | timestamp? | Set after broker confirmation |
| processing_started_at | timestamp? | UTC |
| completed_at | timestamp? | UTC |
| expires_at | timestamp | Received time plus 30 days |
| compressed_bytes | integer | Non-negative |
| decompressed_bytes | integer? | Set by worker |
| total_lines | integer? | Set after parsing |
| accepted_lines | integer | Default zero |
| rejected_lines | integer | Default zero |
| replayed_lines | integer | Default zero |
| retry_count | integer | Default zero |
| terminal_error_code | string? | Stable code only |
| row_version | concurrency token | Protects replay-safe transitions |

Lifecycle:

```text
Receiving -> Publishing -> Accepted -> Processing -> Completed
    |             |            |            |           |
    v             v            v            v           v
Rejected      PublishFailed  Expired     Retrying  CompletedWithRejections
                                             |
                                             v
                                         Failed
```

Only broker confirmation permits `Publishing -> Accepted`. Terminal processing states are
`Completed`, `CompletedWithRejections`, `Failed`, and `Expired`.

## LineProcessingResult

| Attribute | Type | Rule |
|---|---|---|
| batch_id | UUID | Parent batch |
| line_number | integer | One-based decompressed line position |
| outcome | enum | Accepted, IdenticalReplay, Rejected |
| error_code | string? | Stable catalog value |
| empresa_id | string? | Present only when safely parsed |
| veiculo_id | string? | Stable ID; safe operational correlation |
| safe_digest | string | Keyed digest, not raw line hash vulnerable to guessing |
| quarantine_id | UUID? | Present only for protected raw content |
| occurred_at | timestamp | UTC |

Detailed accepted rows need not be retained after batch aggregation. Rejections and conflict evidence
follow the 30-day operational window.

## QuarantinedRejection

| Attribute | Type | Rule |
|---|---|---|
| quarantine_id | UUID | Opaque identifier |
| batch_id | UUID | Parent batch |
| line_number | integer | Original position |
| error_code | string | Stable category |
| encrypted_content_location | string | Protected store reference, never logged |
| content_digest | string | Integrity check |
| created_at | timestamp | UTC |
| delete_after | timestamp | No later than 30 days |
| deleted_at | timestamp? | Proof of cleanup |

Raw content is retrieved only through an administrative application service using the opaque
`quarantine_id`. The service authorizes the internal actor and writes an audit event for successful,
denied, missing, and expired requests before returning any content.

## EventIdentityEvidence

The model has two lifetimes. `EventIdentityGuard` is an atomic, recoverable first-writer state machine
kept for at least as long as its telemetry point. `EventIdentityEvidence` adds the canonical fingerprint
and original provenance for the 30-day diagnostic window. Their physical representation is selected
only after the target-load benchmark.

| Attribute | Type | Rule |
|---|---|---|
| empresa_id | string | Identity component |
| veiculo_id | string | Identity component |
| event_time | timestamp | Identity component |
| fingerprint | fixed binary/string | Canonical event content |
| first_batch_id | UUID | Original provenance |
| first_seen_at | timestamp | UTC |
| state | enum | `Reserved`, `Writing`, or `Committed` |
| owner_batch_id | UUID | Processing owner allowed to resume |
| owner_line_number | integer | Stable source line |
| lease_until | timestamp/null | Takeover boundary for incomplete work |
| fencing_version | integer | Rejects stale owners after takeover |
| storage_location | enum | `PendingCommitted`, `Promoting`, or `QueryableCommitted` |
| guard_expires_at | timestamp | No earlier than telemetry expiration |
| evidence_expires_at | timestamp | First seen plus 30 days |

Unique logical key: `(empresa_id, veiculo_id, event_time)`.

Reservation and state transitions MUST be atomic. `Committed` returns `AlreadyExists` and never
authorizes another InfluxDB write. The current owner may resume `Reserved` or `Writing`; another worker
may take over only after lease expiry and must receive a new fencing version. Stale owners cannot commit.
While evidence exists, its fingerprint determines identical replay versus conflict; after evidence
expiry, a committed identity returns `AlreadyExistsUnclassified`.

Promotion is the only authorized second physical placement. It atomically changes guard location from
`PendingCommitted` to `Promoting`, writes the identical fingerprint to queryable storage, reconciles it,
advances to `QueryableCommitted`, and only then permits deletion from pending storage. Fencing prevents
a stale promotion worker from completing after takeover.

## OperatorAccessProfile

Local internal API identity containing a stable actor ID, hashed rotatable credential references,
enabled state, explicit permissions, and allowed company IDs. `cross_company.read` is never implied.
Producer credentials are stored separately and cannot be mapped to an operator profile.

A restricted Grafana organization uses a non-human `ViewerPresentationServiceCredential` profile with
only `telemetry.read` and optionally `driver.read`, scoped to the organization's companies. It is an API
credential and is never the Basic-authenticated `GrafanaBootstrapAdminCredential` used for server-level
organization bootstrap or a `GrafanaOrganizationServiceAccount` used for organization-local Grafana
resource management.
Privileged internal Grafana users do not share their direct InfluxDB organization or datasource with
restricted viewers. MVP audit actor identity for viewer operations is the presentation credential plus
Grafana organization ID; individual human identity is intentionally not asserted.

## AuditEvent

Append-only security record containing actor, action, resource category, opaque resource identifier,
tenant scope, UTC timestamp, outcome, and trace identifier. It MUST NOT include raw telemetry,
coordinates, plate, name, CPF, bearer tokens, or quarantine content.

## Cadastral projections

The system depends on interfaces rather than owning these records:

- `CompanyVisibility`: determines whether an `empresa_id` may appear in user-facing results.
- `VehicleIdentity`: resolves stable `veiculo_id` to authorized presentation attributes.
- `DriverIdentity`: resolves `motorista_id` and returns only display name plus optional CPF suffix.

Cache entries MUST be short-lived, tenant-scoped, and invalidatable. Cache failure cannot block durable
ingestion; it can delay presentation.

## PendingTelemetryPromotion

Tracks idempotent movement from the dashboard-inaccessible pending InfluxDB database to the queryable
telemetry database after a company becomes valid. It contains company ID, source batch/range, state,
attempt count, timestamps, and safe error code. Grafana credentials have no access to pending storage.
States are `Discovered`, `Copying`, `Copied`, `SourceDeleted`, `Completed`, and `Retrying`. Deletion of
pending source points is permitted only after confirmed idempotent destination write plus identity and
count reconciliation. Every transition is durable so a crash can resume without loss or double display.

## Stable rejection codes

- `LP_EMPTY_LINE`
- `LP_INVALID_SYNTAX`
- `LP_UNKNOWN_MEASUREMENT`
- `LP_UNKNOWN_COLUMN`
- `LP_MISSING_REQUIRED_TAG`
- `LP_MISSING_REQUIRED_FIELD`
- `LP_INVALID_FIELD_TYPE`
- `LP_INVALID_TIMESTAMP`
- `LP_TIMESTAMP_OUT_OF_RANGE`
- `LP_COORDINATE_OUT_OF_RANGE`
- `LP_LINE_TOO_LARGE`
- `PAYLOAD_DECOMPRESSED_LIMIT`
- `PAYLOAD_EXPANSION_LIMIT`
- `DUPLICATE_CONFLICT`
- `STORAGE_SCHEMA_CONFLICT`
- `STORAGE_REJECTED`
