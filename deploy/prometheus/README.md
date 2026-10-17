# Metrics scraping (T082)

`prometheus.yml` uses the private Compose `backend` network, a 15-second interval,
10-second timeout and the existing 30-day Prometheus volume. No exporter port is
published to the host. `/metrics` is the default path for every job.

| Job | Target | Source |
| --- | --- | --- |
| prometheus | localhost:9090 | Scraper health |
| rabbitmq | rabbitmq:15692 | Native aggregate plugin: throughput, backlog, connections, resources |
| postgres | postgres-exporter:9187 | Database connections, transactions, locks, size, checkpoints |
| api | api:8080 | Application aggregate ingestion metrics |
| worker | worker:9464 | Application processing, queue and persistence metrics |
| host | node-exporter:9100 | CPU, memory, disk IO, filesystem capacity and free space |
| influxdb | influxdb:8181 | Native server metrics |

## Enable the exporters

The base stack always enables RabbitMQ's aggregate plugin and mounts this config.
Application jobs require the existing `app` profile. PostgreSQL requires the
monitoring overlay and a separate login, not the application owner credential.
Disabled/missing optional services remain visibly DOWN (`up=0`); Prometheus itself
can start without them. An exporter HTTP success is not proof its dependency is
healthy: inspect `pg_up` and `pg_exporter_last_scrape_error` too.

For an existing or fresh database, run the reviewed
[`monitoring-role.sql`](../postgres/monitoring-role.sql) using an owner `psql`
session connected to `telemetry_control`, then use `\password telemetry_monitor`
to set a generated secret interactively. Supply the same secret as
the contents of a protected UTF-8 file referenced by the absolute deployment
`POSTGRES_METRICS_PASSWORD_FILE` path. The script
is repeatable and never sets or prints a password. It grants `pg_monitor` and
database CONNECT, with no business table grants. Check existing PUBLIC grants and
role memberships before reusing an existing role; role creation does not revoke
privileges granted elsewhere. No production database is changed automatically.

From the repository root (after configuring the existing deployment secrets):

```powershell
docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml -f deploy/compose.monitoring.yaml --profile app --profile host-metrics up -d --build
```

The PostgreSQL password is mounted as a Compose secret and read using
`DATA_SOURCE_PASS_FILE`; it is absent from the exporter URI and command arguments.
Compose file-backed secrets require protection and container-readable ownership of
the source file. Exporter images are pinned. Neither exporter creates a data volume.

The `host-metrics` profile mounts the **Linux host root read-only** with private
propagation (compatible with Docker Desktop's non-shared root) and uses the
host PID namespace. Enable it only on a trusted monitoring host. Filesystem
collection includes root and explicit operational `/data`, `/srv`,
`/var/lib/docker`, `/var/lib/containers` mounts; add approved mount points when the
deployment stores data elsewhere. CPU/memory/proc/sys and disk statistics come
from the Linux host. On Docker Desktop this means the Docker Linux VM, **not the
Windows host, NTFS volumes or Windows disk free space**. Monitoring Windows itself
requires a separately managed Windows exporter and an appropriate private target.
No Windows host coverage is claimed by this overlay.

## Privacy and collection boundaries

RabbitMQ uses `/metrics` with `prometheus.return_per_object_metrics=false`, never
per-object/detailed endpoints. PostgreSQL query-text, table, activity and
replication-identity collectors are disabled; ingestion retains selected aggregate
families and only `telemetry_control` or unlabelled database statistics. API/worker
retain only `telemetry_*` metrics, whose instrumentation removes dynamic labels
and exemplars. Host collection omits home/container mount paths. InfluxDB drops
series carrying database/table labels and exposes only its numeric metrics route
without a token; read/write API routes retain authentication. This route is for
the private backend: do not expose its port through an untrusted ingress.

Sample limits bound unexpected cardinality by failing the affected scrape visibly.
These filters are not a general sanitizer for arbitrary new exporter labels:
review endpoint families and labels when upgrading any pinned exporter/server.
No payload, vehicle, company, batch, SQL text, secret or location belongs in a metric.
Alerts, dashboard completion and Loki retention remain separate tasks.

## Validation

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~PrometheusScrapingTests
```

Tests run the pinned `promtool`, real servers/exporters and a real Prometheus on
an isolated disposable network. API/worker use actual production instrumentation
in local Kestrel hosts, reached through Docker Desktop's `host.docker.internal`
using test-only target substitution and a one-second scrape interval. Tests do
not prove production app startup/configuration or managed-service availability.
Node collector smoke tests run without host mounts/PID privileges and therefore
do not validate the physical host's mount topology. A deployment acceptance check
must inspect `/api/v1/targets`, verify all enabled targets are UP, confirm `pg_up=1`
and `pg_exporter_last_scrape_error=0`, and check expected host mount series.

References: [Prometheus configuration](https://prometheus.io/docs/prometheus/latest/configuration/configuration/),
[RabbitMQ monitoring](https://www.rabbitmq.com/docs/monitoring),
[PostgreSQL exporter](https://github.com/prometheus-community/postgres_exporter/tree/v0.20.1),
[node exporter](https://github.com/prometheus/node_exporter/tree/v1.12.1),
[InfluxDB server information](https://docs.influxdata.com/influxdb3/core/api/server-information/).
