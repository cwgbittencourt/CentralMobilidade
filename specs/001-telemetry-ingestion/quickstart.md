# Quickstart Validation: Vehicle Telemetry Ingestion

This guide includes current startup and future acceptance scenarios. The
[T112 foundation gate](../../artifacts/validation/foundation-single-influx/README.md) passed; T050–T053 processing and RabbitMQ worker wiring are implemented, including a mixed-batch host test with real storage. Status/presentation endpoints, Grafana
provisioning, Pub/Sub parity and steady/burst/outage runners are implemented. The exact
100,000-event final runner is documented in [T101](../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/FINAL-100K.md);
its real acceptance execution remains T102. Sections 4–10 are acceptance targets,
not obtained results. Check tasks.md before running task-specific scripts;
`Provision-GrafanaOrganizations.ps1` is implemented in T071. The latest-position
dashboards and their query/render proof are implemented in T072, and the bounded
single-vehicle trip dashboards in T073; health/driver dashboards are implemented in T074.
Follow [the configuration contract](../../deploy/grafana/README.md).
Credential rotation, scope changes and audit review are covered by the
[T075 access operations runbook](../../docs/operations/grafana-access.md).

See the [T098 execution record](../../artifacts/validation/quickstart-results.md) for
dated results, fixture coverage and blocked acceptance gates. A passing functional
suite alone does not complete this quickstart.

## Prerequisites

- Docker Engine with Compose v2
- .NET 10 SDK
- PowerShell 7
- At least 8 CPU cores, 16 GiB RAM, and 100 GiB free disk for the local validation profile
- Local ports required by the compose file available

## 1. Configure local secrets

From the repository root, copy `.env.example` to an untracked `.env` and fill its blank secret values.
T086 requires absolute private `*_FILE` paths for PostgreSQL/Grafana/read-token
Compose secrets; each file contains the existing raw UTF-8 secret. Follow the
[hardening configuration](../../deploy/hardening.md) for ownership, memory budgets,
the optional monitoring secret and health/restart semantics before recreating services.
The example file must contain no working credentials. Producer and operator bearer tokens are distinct,
must be stored as hashes/secret references, and must not be passed on command lines in shared
environments. Seed at least one scoped operator and one explicit cross-company administrator for
authorization tests; neither credential may be reused as the producer token.

One internal producer credential covers all valid `empresa_id` values (business `idEmpresa`), without
company allowlists or existence lookup. Missing/invalid IDs retain contract rejection. Optional driver
enrichment cannot gate telemetry; operator/viewer scopes and audited identity disclosure remain separate.
Provision hashed producer credentials using protected `Authentication:Producers` configuration as
[documented by the API](../../src/CentralMobilidade.Telemetry.Api/Security/README.md).

For a fresh installation, create only the offline `queryable.json` token following
[deploy/README.md](../../deploy/README.md), then set the absolute `INFLUXDB_QUERYABLE_ADMIN_TOKEN_FILE`
path privately. Never regenerate that token for an existing catalog. For legacy installations, complete
[offline reconciliation](../../docs/runbooks/single-influx-upgrade.md) first. Preserve the project name,
`influxdb_queryable_data` volume, node-id `queryable`, and legacy pending volume/backups.

## 2. Start dependencies

```powershell
docker compose -f deploy/compose.yaml config --quiet
docker compose -f deploy/compose.yaml up -d --build --wait
docker compose -f deploy/compose.yaml ps -a
```

Expected: PostgreSQL, RabbitMQ, InfluxDB 3 Core, Grafana, Prometheus, and Loki report healthy. Volumes
are named and persistent. One server `influxdb` stays running; `influxdb-init` initializes/preserves
`telemetry` with 60-day retention and exits zero. Default container names are
`centralmobilidade-influxdb-1` and `centralmobilidade-influxdb-init-1`; the latter is a job.
Base Compose publishes only Grafana on loopback. To publish development ports, include
`-f deploy/compose.override.yaml` in start/status/stop commands. Starting the solution in the IDE
does not invoke Compose. T071–T075 implement provisioning, dashboards and access documentation;
the following checks still need to be performed for each intended deployment. The current
bootstrap supplies the pinned image/plugin and internal datasource/provider YAML;
latest-position, trip, health and driver templates have isolated query/render proof.
The T071 reconciler installs all available viewer templates. Health uses event-time
stored counts internally and processing-time Accepted/Rejected results in the viewer;
see the [dashboard behavior and limits](../../deploy/grafana/dashboards/README.md).
Driver lookup additionally requires server-provisioned driver.read and optional
enrichment configuration; every disclosure requires durable audit persistence.

After initial startup, YAML must provision only the internal default organization and its direct InfluxDB
datasource and dashboards. Confirm the image reports the pinned Grafana and signed Infinity versions.
Then run the administrative reconciler to create at least one restricted viewer organization with only
an Infinity datasource pointing to the presentation API. Viewer organizations must not contain or
inherit an InfluxDB datasource.

Run `scripts/Provision-GrafanaOrganizations.ps1` twice and verify both executions converge without
duplicate organizations, memberships, `ViewerPresentationServiceCredential` records, or datasources.

Verify that server-level calls use Basic authentication from a secret-sourced
`GrafanaBootstrapAdminCredential`, organization-local calls use that organization's
`GrafanaOrganizationServiceAccount`, and the Infinity datasource contains a third, distinct
`ViewerPresentationServiceCredential`. The bootstrap credential must never be stored in a datasource;
none of the three secrets may appear in logs, rendered dashboards, exported datasource JSON, or
command-line arguments.

## 3. Run migrations and start application processes

Complete the migration and private runtime configuration described below before invoking `up`.
Include your private override if using app containers; the checked-in templates alone are insufficient.

```powershell
dotnet restore CentralMobilidade.slnx
dotnet test tests/CentralMobilidade.Telemetry.ContractTests
docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml --profile app up -d --build --wait
```

Before application startup, apply migrations with a separate identity following the
[migration instructions](../../src/CentralMobilidade.Telemetry.Infrastructure/Persistence/Migrations/README.md).
The application does not migrate its database. Supply runtime PostgreSQL/RabbitMQ credentials,
least-privilege `InfluxDb:Queryable:Token` and hashed authentication credentials. Compose does not
inject arbitrary `.env` keys into application containers. Use a private override under ignored `data/`
(for example `data/compose.app-secrets.yaml`, included with `-f` before `--profile app`), or configure
host processes directly. Do not put secrets in command arguments or tracked settings. The command
above requires runtime credentials; it does not provision them. The worker also accepts a private
root `.env.worker` file in the app profile, using double-underscore environment keys and a persistent
Processing__DiagnosticKey. See the [worker configuration](../../src/CentralMobilidade.Telemetry.Worker/README.md).

API readiness requires PostgreSQL durability and durable queue publication, independently of downstream
Influx/worker availability. Worker readiness requires PostgreSQL, Queryable connectivity and real
consumer freshness from a successful RPC on its active subscription. Liveness alone does not
prove processing. Application URLs use `http://influxdb:8181`; host runs override this to loopback.

## 4. Submit a valid gzip batch

Create a small fixture from [line-protocol-v1.md](contracts/line-protocol-v1.md), gzip it, and send it to
`POST /api/v1/telemetry/batches` with the configured bearer token, `Content-Type: text/plain;
charset=utf-8`, `Content-Encoding: gzip`, and `X-Contract-Version: v1`.

Expected:

- HTTP `202` only after a RabbitMQ publisher confirm.
- Response contains `batchId`, `receivedAt`, `state: accepted`, and `statusUrl`.
- Polling the status reaches `completed`.
- The point is queryable from InfluxDB and appears in the provisioned latest-position dashboard.

Verify the same authorized point through a restricted viewer dashboard. Attempt the same API and
dashboard query for a company outside that viewer organization's service-credential scope; it must
return the same non-disclosing result as an unavailable resource. Confirm that Explore in the viewer
organization exposes no InfluxDB datasource.

Validate viewer limits at and immediately above 500 latest vehicles, a 24-hour/10,000-point trip, and a
seven-day/one-minute/10,000-point ingestion-health query. Viewer identity audit evidence must name the
Grafana organization and `ViewerPresentationServiceCredential` and must not claim an individual human
identity.

## 5. Validate partial acceptance

Send a fixture containing one valid line, one unknown field, one invalid coordinate, and one conflicting
duplicate.

Expected:

- Intake still returns `202` after durable handoff.
- Final state is `completed_with_rejections`.
- The valid point exists exactly once.
- Rejection metadata identifies line positions and stable error codes without raw line or plate.
- Protected quarantine contains the rejected raw lines and records their deletion deadline.

## 6. Validate restart recovery

For a fresh disposable small HTTP/recovery fixture, run
`py -3 scripts/Run-CapacityGates.py --quickstart-smoke` from the repository root.
It creates a UUID Compose project with private ephemeral credentials and retained volumes;
it never attaches to an existing deployment. It checks valid/partial batches, direct API
company scopes, audited raw access and an API/worker restart while InfluxDB is stopped.
This fixture uses bootstrap store credentials and the local `centralmobilidade-api:t094` /
`centralmobilidade-worker:t094` images from the capacity preparer. Verify their source revision
before use. It does not prove production hardening, Grafana rendering, exact oldest-message
age, target load or the final 100,000-event scenario.
The T098 attempt currently stops at the real API readiness gate; see the execution record
before treating any later check as executed.

For a coordinated backup and restore into new volumes, use the
[T087 backup/restore runbook](../../docs/runbooks/backup-restore.md) and its isolated
`BackupRestoreTests` drill. It is separate from the restart scenario below and
does not certify physical-host recovery, production RPO/RTO or an off-host copy.

The [T078 Compose tests](../../tests/CentralMobilidade.Telemetry.IntegrationTests/Deployment/README.md)
exercise dependency health, named-volume persistence, restart/recreation and missing/
read-only storage failures using disposable UUID projects. They do not replace
the application recovery scenario below or authorize resetting an existing stack.

Pause InfluxDB, submit a valid batch, and verify status becomes `retrying`. Restart the API and worker,
then restore InfluxDB.

Expected:

- The acknowledged batch remains present in RabbitMQ.
- The worker eventually stores it and acknowledges the queue delivery.
- Status becomes terminal without duplicate logical points.
- Metrics show retry count and backlog age.

## 7. Validate privacy

Use seeded fixture values for plate, coordinates, driver name, CPF, and bearer token. Run the privacy
scanner over application logs, Loki query results, Prometheus labels, and exported traces.

Run the [automated T079 privacy gate](../../tests/CentralMobilidade.Telemetry.IntegrationTests/Privacy/README.md#automated-observability-privacy-gate-t079)
with Docker running. It includes both service roles, nonempty-output checks and
deliberate-leak controls; its Loki transport is explicitly supplied by the test.

Expected: none of the seeded sensitive values appears. `veiculo_id`, `batchId`, and safe keyed digests
remain sufficient for correlation. The authorized driver-presentation request returns only display name
and optional CPF suffix and produces an audit event.

## 8. Validate provider parity

T076 provides the [shared queue contract suite](../../tests/CentralMobilidade.Telemetry.ContractTests/Queue/README.md)
and an executable real-RabbitMQ binding. T077 adds the emulator binding and four
real-SDK controls; T080 now implements the adapter and resolves its 22 original
failures, with 14 additional integration safety cases. T081 now supplies runtime
provider selection and host wiring; follow the [queue configuration contract](../../src/CentralMobilidade.Telemetry.Infrastructure/DependencyInjection/README.md)
for existing resources, credentials, explicit emulator mode and startup checks. See the
[emulator evidence and limitations](../../tests/CentralMobilidade.Telemetry.IntegrationTests/Queue/README.md).
A passing RabbitMQ run or emulator SDK probe alone does not establish parity.

For an operational change, use the [T088 cutover runbook](../../docs/runbooks/queue-provider-cutover.md).
The maintenance path requires intake closure, source drain and verification; dual-publish
remains blocked until its coordinator, recovery and handoff gates are implemented and proven.

Start the Google Pub/Sub emulator profile and execute the queue contract suite against RabbitMQ and the
emulator.

Expected: publish confirmation, redelivery, manual completion, error mapping, correlation attributes,
and envelope validation have the same domain-visible behavior. Emulator persistence is not used as a
durability claim.

## 9. Execute capacity gates

For future GCS storage and five-year retention, follow the [T089 migration prerequisites and capacity gate](../../docs/runbooks/gcs-migration.md). Current option validation and database initialization still enforce the local 60-day profile; no GCS migration or five-year capacity result is claimed.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario steady-5000-eps --config C:/caminho-privado/steady.json
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario reconnect-burst --config C:/caminho-privado/steady.json
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario influx-outage --config C:/caminho-privado/outage.json
dotnet test tests/CentralMobilidade.Telemetry.LoadTests --filter FullyQualifiedName~IdentityGuardBenchmarkTests
```

Expected reports include p50/p95/p99 latency, events/s, compressed/decompressed throughput, CPU,
memory, disk growth, queue depth and oldest age, Influx write latency, rejection rate, and deduplication
storage/lookup cost. The conflict detector cannot be selected or production-approved without passing
the target-load experiment.

The project commands include tests without a `Category` trait; filtering the solution by
`Category=Integration` or `Category=Contract` does not execute every test in those projects.
`dedup-strategy` is not a supported runner scenario. The benchmark above measures a SQL
prototype; its result does not establish actual pipeline throughput or approve the selected adapter.

The [T091 steady-state runner guide](../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/README.md)
defines private configuration, required monitoring, the 8-vCPU/16-GiB profile and report limits.
The steady, T092 burst and T093 outage runners are implemented. Outage uses a separate
nested configuration, an existing isolated Influx proxy and additional measured queue/write metrics;
see the shared guide for setup, restoration and seven-day extrapolation limits.
Burst requires DurationSeconds=600 and continues normal traffic through its additional
1,200-second recovery window; the shared guide explains its cohort/deadline assertions.

## 10. Prove 100,000-event ingestion and Grafana visualization

Prepare the isolated target and private credentials following the
[T101 final scenario guide](../../tests/CentralMobilidade.Telemetry.LoadTests/Scenarios/FINAL-100K.md), then run:

```powershell
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario final-100k-grafana --config data/final-100k.json
```

T102 records the real run and visual review. The T101 simulated-protocol controls
do not satisfy this step; current API readiness and other release gates still apply.

Before stopping, run the final acceptance scenario from the load-test project. It must generate exactly
100,000 deterministic valid events spanning at least 10 valid producer-supplied company IDs, 1,000 vehicles, delayed and
ordered points, and one named trip; submit them through the gzip API in contract-compliant batches,
wait for terminal batch states, reconcile exactly 100,000 logical events in InfluxDB, and save Grafana
API/render evidence for the latest-position, trip, and ingestion-total dashboards under
`artifacts/validation/100k-final-proof/`.

Any unexplained missing event, extra logical event, non-terminal batch, or dashboard query failure makes
the final proof fail.

## 11. Stop without deleting data

```powershell
docker compose -f deploy/compose.yaml down
# If started with development profiles, use the same files and profiles:
docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml --profile app --profile pubsub-emulator down
```

Do not pass `--volumes` when preserving local data. Backup and destructive cleanup procedures must be
separate, explicit runbooks.

## Metrics scraping (T082)

The [metrics deployment guide](../../deploy/prometheus/README.md) lists the seven
Prometheus jobs and the optional `compose.monitoring.yaml` exporters. Provision
the dedicated PostgreSQL monitoring role/password before enabling that overlay;
use the `app` profile for API/worker and `host-metrics` for the trusted Linux host
collector. Missing optional services are visibly DOWN. Docker Desktop host metrics
describe its Linux VM, not Windows/NTFS. Inspect all enabled scrape targets plus
`pg_up` and collector health before accepting deployment monitoring. InfluxDB's
private numeric `/metrics` route is unauthenticated; data APIs remain authenticated.
See the guide for collection/privacy limits and the isolated test command.

## Loki retention and labels (T083)

The [Loki guide](../../deploy/loki/README.md) documents its 30-day retention,
asynchronous compactor deletion and persistent paths. OTLP indexing allows only
the trusted fixed `service.name`; other attributes are dropped. Only sanitized
application JSON may be forwarded. Native push bypasses OTLP attribute rules,
and neither route redacts an unsafe body. No automatic stdout shipper is installed.
The base service stays private; the development port stays loopback-only. Run the
guide's isolated tests before applying the configuration to an existing volume.

## Platform operations dashboard (T084)

Internal organization 1 receives the **Operações da plataforma** dashboard in its
own Platform Operations folder. It uses the T082 Prometheus jobs, application
metrics and optional host/database exporters. See the [panel guide](../../deploy/grafana/dashboards/platform-operations.md)
for exact semantics: delivery age observes delivered work, not the oldest queued
message; replay/state counters observe commits; host metrics describe Linux;
missing samples stay missing. The dashboard is excluded from viewer provisioning.

## Foundation amendment acceptance gate (before T050)

Complete T104-T112 first. Using the same producer credential, submit valid events for multiple IDs
absent from the cadastral source with company lookup disabled/unavailable. Verify single-store
persistence and authorized visibility, unchanged invalid-ID rejection, and no pending health/network/
configuration requirement. Test both fresh schema and populated legacy upgrade with count/identity
reconciliation; preserve original volumes. An asynchronous batch state called pending/Accepted is
not unknown-company storage and must not be removed. Record actual checks without implying capacity
approval: ADR-001 throughput remains a separate unresolved gate.

## T085 — Grafana-local alerts

The [alert runbook](../../deploy/grafana/provisioning/alerting/README.md) describes six provisioned rules for observed backlog age, five-minute queryability risk, six-day delivered-message age, writer failures, filesystem pressure and worker heartbeat. All notifications are permanently muted; inspect states locally. NoData/Error requires investigation and does not prove a measured breach. These signals do not certify the SLO or managed-provider retention.
