# Control-plane persistence (T018)

`TelemetryControlDbContext` maps the eight control-plane rows to PostgreSQL. Telemetry coordinates,
raw rejected lines, driver names, CPF and bearer tokens are not columns. Protected quarantine content
is referenced by an opaque quarantine ID and a protected storage reference. Credential IDs in operator
profiles refer to the separate hashed credential store; this task does not replace the configured
authentication store from T017.

The API and worker register persistence when `ConnectionStrings:TelemetryControl` is nonempty.
An empty connection leaves the API's existing audit failure behavior in place. Registration never
creates a database or runs migrations. T019 supplies the production migrations and database-level
append-only protections/least-privilege deployment policy. Do not use `EnsureCreated` in production.

Tables use snake_case names and quoted CLR property names for columns. Timestamp columns are
`timestamp with time zone`; tracked writes reject non-UTC offsets. Company/vehicle keys preserve
case. Batch/profile/promotion `RowVersion` is a manually incremented optimistic concurrency token.
Guard `FencingVersion` is a concurrency token controlled by the future reservation adapter. Guard and
evidence have independent expiry indexes and no cascading dependency on batches or one another.
No telemetry-lifetime reservation algorithm or target-scale performance is claimed: T041/ADR-001
must approve the physical strategy before T047 implements the identity port.

`PostgresAuditWriter` uses a new context and transaction per append, parameterized SQL and
`INSERT ... ON CONFLICT DO NOTHING`. An identical event ID/content returns its original receipt;
the same ID with different content fails. Company scopes are sorted and deduplicated and occurrence
timestamps normalized to PostgreSQL microseconds before comparison. The receipt timestamp is generated
by PostgreSQL and returned only after transaction commit with `synchronous_commit=on`. A connection or
commit failure propagates to the existing fail-closed authorization handler. PostgreSQL must run with
`fsync=on`; this is local database durability, not protection against physical host loss.

Tracked audit updates/deletes are rejected by both synchronous and asynchronous SaveChanges.
Direct SQL and EF bulk operations bypass that check; T019 must add database-level restrictions before
production deployment. The audit writer exposes only append and never performs update/delete.

## Verification

The integration tests validate keys, absence of raw telemetry columns, UTC enforcement, tracked audit
immutability, PostgreSQL round-trips for all eight entities, independent evidence deletion, database
constraints, stale batch version rejection, eight concurrent identical appends, persistent receipts,
collision rejection and cancellation. The database fixture uses PostgreSQL 17.11 Testcontainers by
default, or a dedicated disposable database supplied through `TELEMETRY_TEST_POSTGRES`. Never point
that override at an application database. Schema creation is test-only.

On 2026-10-13 the Windows build passed with zero warnings/errors and the full suite ran in the Linux
.NET 10.0.401 SDK image against an isolated PostgreSQL container. Windows Application Control still
blocks loading the Application assembly in native tests; that policy was preserved. Validation files
and database volume live outside versioned source. Architecture/load projects are still skeletons
and provide no capacity evidence.
