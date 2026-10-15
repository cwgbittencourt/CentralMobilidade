# PostgreSQL control-plane migrations

## Single telemetry store (T106)

The current model has seven control tables and no promotion/routing fields.
`20261014230000_SingleTelemetryStore` is a new forward migration; the two earlier migration
sources are unchanged. Before applying it to a populated database, follow
[the offline reconciliation runbook](../../../../docs/runbooks/single-influx-upgrade.md).
The migration refuses any unreconciled guard or unarchived promotion row before dropping schema.
Existing identities, evidence, batches, partitioning and invariant triggers are preserved.
The Down path is restricted to empty guard/evidence ledgers; populated recovery uses coordinated
backups. Runtime grants no longer reference the removed table. The health probe
remains a T108 adaptation; do not deploy this intermediate foundation before T107–T112 pass.

The sections below record the original T019 schema and validation; promotion transitions
apply only to the pre-T106 schema.

## Initial PostgreSQL migration (T019)

`20261013182313_InitialControlPlane` creates all eight control tables, constraints and indexes;
the designer and model snapshot were scaffolded with EF Core 10.0.12/Npgsql 10.0.3. PostgreSQL 17.11
is the validated version. The migration adds SQL triggers through `InitialControlPlaneGuards`.
Custom trigger SQL is reviewed/tested separately from EF's schema snapshot.

## Apply

Use a dedicated database with the normal `public` search path. Keep the migration identity separate
from the runtime identity. Run these commands in a supported .NET 10 environment:

```powershell
dotnet tool restore
dotnet ef migrations script --idempotent --project src/CentralMobilidade.Telemetry.Infrastructure --output artifacts/validation/control-plane-migration.sql
# Supply ConnectionStrings__TelemetryControl from your secret manager, without printing it.
dotnet ef database update --project src/CentralMobilidade.Telemetry.Infrastructure
```

The design-time factory uses the environment connection for updates. When it is absent it uses an
offline design-only connection with no password; script generation/scaffolding does not connect.
The API/worker do not migrate on startup. Apply the migration once before starting either process.
Never apply it to a database previously created by EnsureCreated: it lacks migration history. For local
test-only T018 databases use a fresh database; preserve any real data and plan an explicit migration.
The local tool manifest pins dotnet-ef 10.0.12. On this Windows host Application Control blocks loading
the Application assembly, so generation and execution were validated in Linux containers without
changing the Windows security policy.

After migrating, create a dedicated runtime login through deployment secret management. It must be
NOSUPERUSER/NOCREATEDB/NOCREATEROLE, must not own the database/schema/tables, and must not inherit a
migration/admin role. As the migration owner, run the grant template with the existing runtime role:

```powershell
# Connection/credentials come from your protected psql environment or service configuration.
psql -v runtime_role=telemetry_runtime -f deploy/postgres/runtime-grants.sql
```

The template grants audit SELECT/INSERT, operator-profile SELECT and control-processing table DML;
it removes CREATE from PUBLIC on this dedicated database's public schema. Profile provisioning and
credential rotation use a separate administrator. Do not grant runtime access to migration history,
TRUNCATE, trigger alteration, schema DDL, or replication settings. PostgreSQL superusers/owners can
disable protections or drop tables, so these identities are not suitable runtime credentials.

## Integrity and retention

The composite company/vehicle/millisecond timestamp key uses C collation and provides atomic uniqueness.
Incomplete guards require an owner, positive fence and finite lease. Owner changes require an expired
lease and a larger fence. The trigger permits Reserved -> Writing -> Committed, same-owner renewal,
and fenced promotion location transitions; committed identities cannot reopen. Adapters must still
use owner/fence predicates on every update, because database constraints do not identify a caller's
lease. Tests prove a stale owner's conditional update affects zero rows. These are schema invariants,
not the selected identity adapter: T041/ADR-001 and T047 remain required before production processing.

`TelemetryExpiresAt` records the storage retention deadline supplied by the adapter;
`GuardExpiresAt` must be at least that deadline, and neither can decrease. Committed guards cannot
be deleted before guard expiry. Guard TRUNCATE is prohibited. No cascade connects the guard/evidence
to 30-day batch cleanup. Both 60-day local and longer configurable retention deadlines are representable;
this migration does not prove their capacity or InfluxDB cleanup behavior.

Batch expiry is bounded to 30 days. Fingerprint evidence expires 720 hours after first observation.
Quarantine deletion deadlines are bounded to 30 days. Expiry indexes support T061 cleanup; indexes
alone do not delete data. Viewer profiles require an organization, nonempty company scope and only
telemetry.read/driver.read. Human cross-company access requires the explicit cross_company.read entry.

Audit UPDATE/DELETE/TRUNCATE always fails, including statements affecting zero rows and replication
mode. SELECT/INSERT still works for the limited runtime role. Audit retention is not tied to batch
retention. Versioned rows require a one-step RowVersion increment; EF adds its concurrency predicate.

## Verification and rollback

Integration tests apply/reapply the migration, execute generated idempotent SQL twice, round-trip all
eight entities and exercise uniqueness, fencing, scope constraints, retention, audit restrictions and
optimistic concurrency against PostgreSQL. Down removes all control tables and trigger functions;
it is destructive and is tested only on an empty disposable database. Do not roll a populated system
back to zero. Prefer a reviewed forward migration and independent backups for deployed data.

Validation on 2026-10-13: full Linux suite passed (72 unit, 40 contract, 20 integration; 132 total),
Windows build passed with zero warnings/errors, and formatting verification passed. The real psql
grant template was also executed in the isolated validation database: audit INSERT and profile SELECT
were granted, while audit UPDATE, profile UPDATE and schema CREATE were denied. Temporary containers
were stopped/removed and validation volumes preserved. Architecture/load projects remain skeletons.

## T106 implementation and validation

The pre-change regression test failed because the pending table still existed. The new migration
then passed fresh-schema/snapshot, populated upgrade, refusal/rollback, replay/conflict, provenance,
retention, audit, owner/fencing, partition and truncate checks against PostgreSQL 17.11.
The reconciliation test uses the real Line Protocol parser for fingerprints and executes the same
literal SQL file documented in the runbook. Its imported source/destination fingerprints are
synthetic test evidence; tests do not prove copying or reading a live InfluxDB catalog.

On 2026-10-14, the local Docker context had no containers/volumes labeled as the `centralmobilidade`
Compose project. External databases were not inspected and no project data was migrated.
The previous applied migration sources/designer were left unchanged; only the current model
snapshot and a new forward migration were added/updated. No volumes were deleted.

The tests use isolated databases, preserving live services. Unit tests passed 242/242,
contract tests 58/58, architecture tests 47/47, and directed PostgreSQL integration 38/38.
An initial execution found an EF test-runner formatting error for the SQL regex `{64}`; the
runner was corrected to send literal SQL and the final selection passed. Reports, including
initial failures, are retained under ignored `artifacts/validation/T106/`.

Reproduce from the repository root with .NET 10 and Docker available:

```powershell
dotnet build CentralMobilidade.slnx --no-restore
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter "FullyQualifiedName~SingleTelemetryStoreMigrationTests|FullyQualifiedName~ControlMigrationTests|FullyQualifiedName~PostgresAuditWriterTests|FullyQualifiedName~Partition_upgrade_preserves_committed_identity_and_protects_leaf_truncate|FullyQualifiedName~Concurrent_reservation_has_one_winner_and_conflicting_contender_cannot_replace_it|FullyQualifiedName~Owner_resume_retains_fence_and_takeover_fences_stale_writers|FullyQualifiedName~Expired_evidence_still_blocks_committed_identity|FullyQualifiedName~Resuming_owner_cannot_change_reserved_fingerprint"
```

T050/T051 remain unimplemented; their intentionally red tests were not included in this selection.
Capacity approval and real Influx restart/copy verification are not claimed by these checks.

The separately executed actual runtime-grant template test also passed 1/1 after rebuilding the
integration project: grants apply to the seven-table schema while DDL, audit mutation and guard
truncate remain denied. Thus the final directed integration checks cover 39 distinct passing
scenarios (38 plus the new grants case), with no skips. No full integration suite claim is made.
