# Append-only audit policy — T068

`PostgresAuditWriter` persists only the closed `AuditRecord` metadata projection:
event ID, technical actor ID/kind, optional trusted Grafana organization ID,
action/resource category, optional opaque resource UUID, company IDs, UTC event
time, outcome and trace ID. The database supplies the UTC commit time.

Names, CPF/suffix, plate, coordinates, bearer credentials, telemetry bodies and
quarantine content have no payload fields in this projection. OpaqueResourceId
accepts only a nonempty UUID in D format or null, so raw IDs, source diagnostics,
paths and payloads cannot be substituted for that resource reference. Actor,
organization and trace metadata reject control characters and retain existing
length limits. `AuditRecord.ToString()` is redacted even for rejected input.

Callers must derive actor/organization from authenticated server-side profiles,
trace IDs from the trusted request context, and company scope from authorization.
These fields are technical identifiers, not display names or credential values.
The writer validates their structure and consistency; it does not authenticate
the caller or identify sensitive content deliberately supplied as a valid ID.
It must not be used to record arbitrary request headers, query strings or objects.

## Action and actor consistency

- ResolveDriver requires the Driver category. Allowed identity disclosure records
  exactly one distinct company. Missing/expired/denied attempts remain auditable.
- ReadTelemetry requires Telemetry; an allowed read has nonempty company scope.
- ReadBatch/ReadRejections require Batch; ReadQuarantineContent requires Quarantine.
  Existing privileged raw reads with incomplete company evidence remain supported.
- ReadAcrossCompanies can accompany any concrete resource category. Allowed
  cross-company access requires a human and at least one company, including the
  single out-of-scope company case. The writer does not imply or grant the
  cross_company.read permission; the application must check it independently.
- Human actors carry no Grafana service-organization attribution. Viewer service
  actors require an organization; their successful/non-denied actions are limited
  to telemetry/identity reads. Forbidden raw/cross-company attempts can still be
  recorded as Denied with the correct service/organization attribution.
- Unidentified actors use the `unauthenticated` sentinel, only Denied outcomes,
  no organization and no asserted company scope. Middleware drops empty resource
  UUIDs, preserving auditable 401 responses for malformed unauthenticated requests.

Invalid records are rejected with a stable metadata error before opening a
database connection. Null scope/entries, inconsistent category/actor, empty event
time, non-UTC time and unsupported enum values cannot yield an audit receipt.
Company scope is deduplicated and ordinally sorted before persistence/replay.

## Durability and immutability

The existing transaction retains `SET LOCAL synchronous_commit = on`. A receipt
is returned only after commit. EventId is idempotent under concurrent retries;
the normalized metadata must match every stored field, including organization,
scope, outcome and microsecond event time. A collision cannot overwrite the
original event. Errors never include submitted metadata values.

Existing PostgreSQL always-on triggers and runtime grants continue to prohibit
UPDATE, DELETE and TRUNCATE; no migration or audit-delete privilege is added.
The writer is the application policy boundary. Direct SQL inserts remain an
operation of trusted database roles, not another public application entry point.

## Validation and remaining work

Twenty-three contract cases cover pre-database policy rejection and diagnostic
redaction, including seeded name, CPF, coordinates, plate, bearer, telemetry and
quarantine content in the resource field. The original 20 cases were verified
red before implementation. Fifteen new PostgreSQL integration cases cover all
identity outcomes for human/service actors, trusted organization attribution,
cross-company categories, concurrent replay/scope normalization, forbidden viewer
attempts, changed attribution refusal and database mutation protection. Two HTTP
regressions cover empty UUIDs in missing/producer authentication denials.

T069/T070 still own permission checks and durable audit-before-disclosure in the
presentation handlers/routes. The 97 pre-existing presentation/Grafana TDD
failures are not resolved by this persistence policy. Ignored evidence is under
`artifacts/validation/T068/`.

Final regression: contracts 146/146, unit 244/244 and architecture 47/47 pass.
Functional integration (separate ADR measurement excluded) passes 558 cases with
only the 97 existing presentation/Grafana TDD failures, 655 total and zero skips.
Build and whitespace verification pass without warnings/errors.
