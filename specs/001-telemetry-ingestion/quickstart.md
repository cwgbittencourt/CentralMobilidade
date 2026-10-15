# Quickstart Validation: Vehicle Telemetry Ingestion

This guide includes current startup and future acceptance scenarios. The
[T112 foundation gate](../../artifacts/validation/foundation-single-influx/README.md) passed; T050 bounded single-destination batch assembly is implemented. T051 processing, T052/T053 consumption, status/presentation endpoints, Grafana
provisioning, Pub/Sub parity and load/final-proof commands below are not all implemented. Sections
4–10 are acceptance targets, not obtained results. Check tasks.md before running task-specific scripts;
`Provision-GrafanaOrganizations.ps1` is planned for T071.

## Prerequisites

- Docker Engine with Compose v2
- .NET 10 SDK
- PowerShell 7
- At least 8 CPU cores, 16 GiB RAM, and 100 GiB free disk for the local validation profile
- Local ports required by the compose file available

## 1. Configure local secrets

From the repository root, copy `.env.example` to an untracked `.env` and fill its blank secret values.
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
does not invoke Compose. The following Grafana provisioning checks await T071–T075; the current
bootstrap supplies the pinned image/plugin, not the finished dashboards or reconciler.

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
dotnet test CentralMobilidade.slnx --filter Category=Contract
docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml --profile app up -d --build --wait
```

Before application startup, apply migrations with a separate identity following the
[migration instructions](../../src/CentralMobilidade.Telemetry.Infrastructure/Persistence/Migrations/README.md).
The application does not migrate its database. Supply runtime PostgreSQL/RabbitMQ credentials,
least-privilege `InfluxDb:Queryable:Token` and hashed authentication credentials. Compose does not
inject arbitrary `.env` keys into application containers. Use a private override under ignored `data/`
(for example `data/compose.app-secrets.yaml`, included with `-f` before `--profile app`), or configure
host processes directly. Do not put secrets in command arguments or tracked settings. The command
above starts hosts with templates; it does not provision runtime credentials or implement consumption.

API readiness requires PostgreSQL durability and durable queue publication, independently of downstream
Influx/worker availability. Worker readiness requires PostgreSQL, Queryable connectivity and real
consumer freshness; it remains unavailable until T053 wires the consumer. Liveness alone does not
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

Expected: none of the seeded sensitive values appears. `veiculo_id`, `batchId`, and safe keyed digests
remain sufficient for correlation. The authorized driver-presentation request returns only display name
and optional CPF suffix and produces an audit event.

## 8. Validate provider parity

Start the Google Pub/Sub emulator profile and execute the queue contract suite against RabbitMQ and the
emulator.

Expected: publish confirmation, redelivery, manual completion, error mapping, correlation attributes,
and envelope validation have the same domain-visible behavior. Emulator persistence is not used as a
durability claim.

## 9. Execute capacity gates

```powershell
dotnet test CentralMobilidade.slnx --filter Category=Integration
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario steady-5000-eps
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario reconnect-burst
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario influx-outage
dotnet run --project tests/CentralMobilidade.Telemetry.LoadTests -- --scenario dedup-strategy
```

Expected reports include p50/p95/p99 latency, events/s, compressed/decompressed throughput, CPU,
memory, disk growth, queue depth and oldest age, Influx write latency, rejection rate, and deduplication
storage/lookup cost. The conflict detector cannot be selected or production-approved without passing
the target-load experiment.

## 10. Prove 100,000-event ingestion and Grafana visualization

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

## Foundation amendment acceptance gate (before T050)

Complete T104-T112 first. Using the same producer credential, submit valid events for multiple IDs
absent from the cadastral source with company lookup disabled/unavailable. Verify single-store
persistence and authorized visibility, unchanged invalid-ID rejection, and no pending health/network/
configuration requirement. Test both fresh schema and populated legacy upgrade with count/identity
reconciliation; preserve original volumes. An asynchronous batch state called pending/Accepted is
not unknown-company storage and must not be removed. Record actual checks without implying capacity
approval: ADR-001 throughput remains a separate unresolved gate.
