# Control-plane persistence (T018/T019)

`TelemetryControlDbContext` maps the eight control-plane rows to PostgreSQL. Telemetry coordinates,
raw rejected lines, driver names, CPF and bearer tokens are not columns. Protected quarantine content
is referenced by an opaque quarantine ID and a protected storage reference. Credential IDs in operator
profiles refer to the separate hashed credential store; this task does not replace the configured
authentication store from T017.

The API and worker register persistence when `ConnectionStrings:TelemetryControl` is nonempty.
An empty connection leaves the API's existing audit failure behavior in place. Registration never
creates a database or runs migrations. T019 supplies the initial migration and database-level
append-only protections. Apply it with a separate migration identity as described in
[the migration instructions](Migrations/README.md). Do not use `EnsureCreated` in production.

Tables use snake_case names and quoted CLR property names for columns. Timestamp columns are
`timestamp with time zone`; tracked writes reject non-UTC offsets. Company/vehicle keys preserve
case. Batch/profile/promotion `RowVersion` is a manually incremented optimistic concurrency token.
Database triggers require each batch/profile/promotion update to advance its version by one.
Guard `FencingVersion` is a concurrency token controlled by the future reservation adapter. Guard and
evidence have independent expiry indexes and no cascading dependency on batches or one another.
No telemetry-lifetime reservation algorithm or target-scale performance is claimed: T041/ADR-001
must approve the physical strategy before T047 implements the identity port.

`PostgresBatchStateStore` creates ingestion batches with `INSERT ... ON CONFLICT` inside a database
transaction. Reusing a `BatchId` with identical persisted metadata returns the existing batch; a
metadata collision is rejected. Lifecycle changes use a single version-checked SQL update, so legal
transitions increment `RowVersion` exactly once. An exact retry succeeds without a second mutation,
including concurrent retries of `Publishing -> Accepted`; a different timestamp, stale version,
illegal source state, or backward timestamp is rejected without changing the row.

Acceptance records `DurableAt` in the same atomic update. The intake orchestrator supplies that
transition only after the durable queue publisher confirms the matching batch. PostgreSQL timestamp
precision is normalized before comparisons, and creation requires the exact 30-day retention boundary.

`PostgresAuditWriter` uses a new context and transaction per append, parameterized SQL and
`INSERT ... ON CONFLICT DO NOTHING`. An identical event ID/content returns its original receipt;
the same ID with different content fails. Company scopes are sorted and deduplicated and occurrence
timestamps normalized to PostgreSQL microseconds before comparison. The receipt timestamp is generated
by PostgreSQL and returned only after transaction commit with `synchronous_commit=on`. A connection or
commit failure propagates to the existing fail-closed authorization handler. PostgreSQL must run with
`fsync=on`; this is local database durability, not protection against physical host loss.

Tracked audit updates/deletes are rejected by both synchronous and asynchronous SaveChanges.
The migration also rejects direct SQL and EF bulk UPDATE/DELETE/TRUNCATE through an ALWAYS statement
trigger. Use a runtime role with SELECT/INSERT only on audit events and no ownership or DDL privileges;
the deployment grant template is `deploy/postgres/runtime-grants.sql`. Superusers and migration owners
can change the schema and must be kept separate from runtime credentials. The audit writer exposes
only append and never performs update/delete.

## Verification

The integration tests validate keys, absence of raw telemetry columns, UTC enforcement, tracked audit
immutability, PostgreSQL round-trips for all eight entities, independent evidence deletion, database
constraints, stale batch version rejection, eight concurrent identical appends, persistent receipts,
collision rejection and cancellation. The database fixture uses PostgreSQL 17.11 Testcontainers by
default, or a dedicated disposable database supplied through `TELEMETRY_TEST_POSTGRES`. Never point
that override at an application database. Tests now apply the actual migration rather than EnsureCreated.
Migration tests also prove repeated application and idempotent SQL execution, schema snapshot consistency,
eight concurrent identity inserts, fenced lease takeover, expiry-aware guard deletion, SQL audit mutation
denial, runtime-role schema denial, scope restrictions and 30-day retention bounds.

On 2026-10-13 the Windows build passed with zero warnings/errors and the full suite ran in the Linux
.NET 10.0.401 SDK image against an isolated PostgreSQL container. Windows Application Control still
blocks loading the Application assembly in native tests; that policy was preserved. Validation files
and database volume live outside versioned source. Architecture/load projects are still skeletons
and provide no capacity evidence.
