# Local dependency stack

Run Docker Compose from the repository root. This stack persists PostgreSQL, RabbitMQ, queryable
InfluxDB, pending InfluxDB, Prometheus, Loki, Grafana, and rejection quarantine in separate named volumes.
Only Grafana is published, on `127.0.0.1:3000`. Development ports and application profiles follow in T009.

Set `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, and `GRAFANA_ADMIN_PASSWORD` in the shell or an untracked
root `.env`. Use distinct secrets. `GRAFANA_ADMIN_USER` defaults to `admin`; `GRAFANA_PORT` defaults to
`3000`. PostgreSQL and Grafana receive their passwords through Docker Compose secrets.

Create two distinct offline InfluxDB admin token files in an untracked directory, for example:

```powershell
New-Item -ItemType Directory -Force data/secrets
$tokenDirectory = (Resolve-Path data/secrets).Path
docker run --rm --mount "type=bind,source=$tokenDirectory,target=/tokens" influxdb:3.11.6-core influxdb3 create token --admin --offline --name queryable-bootstrap --output-file /tokens/queryable.json
docker run --rm --mount "type=bind,source=$tokenDirectory,target=/tokens" influxdb:3.11.6-core influxdb3 create token --admin --offline --name pending-bootstrap --output-file /tokens/pending.json
$env:INFLUXDB_QUERYABLE_ADMIN_TOKEN_FILE = Join-Path $tokenDirectory queryable.json
$env:INFLUXDB_PENDING_ADMIN_TOKEN_FILE = Join-Path $tokenDirectory pending.json
docker compose -f deploy/compose.yaml config --quiet
docker compose -f deploy/compose.yaml up -d --build --wait
docker compose -f deploy/compose.yaml ps -a
```

Keep these token files private and available across restarts; do not regenerate them for an existing
catalog. The init jobs create `telemetry` and `telemetry_pending` with 60-day retention and skip existing
databases. They fail on connection/authentication errors. Each InfluxDB instance has its own catalog,
WAL, Parquet files, network, and bootstrap credential. Pending storage has no host port and Grafana
does not join its network or receive its token. Provision scoped application/read credentials in the
later provider and Grafana provisioning tasks; bootstrap tokens are for administration.

The Grafana image fixes OSS 12.2.5 and signed Infinity 4.1.1, whose declared minimum for the 12.2 line
is 12.2.5. Plugins are installed at build time outside `/var/lib/grafana` so the data volume cannot hide
them. Unsigned plugins are not enabled. Organization, datasource, and dashboard provisioning follow
in T071–T074. Prometheus currently scrapes itself; Loki has a persistent single-node bootstrap config.
Full scraping, privacy policies, and Loki retention follow in T082–T083.

`quarantine-init` creates a private `rejections` directory owned by application UID 1654. Application
containers must mount `quarantine_data` at `/quarantine` and use `/quarantine/rejections`.

Stop containers while preserving data:

```powershell
docker compose -f deploy/compose.yaml down
```

Never use `down --volumes` for ordinary shutdown or recovery. Named volumes survive container
recreation, but a single physical host does not provide protection against host loss.

References: [Infinity releases](https://grafana.com/api/plugins/yesoreyeram-infinity-datasource/versions),
[InfluxDB offline admin tokens](https://docs.influxdata.com/influxdb3/core/admin/tokens/),
[InfluxDB retention](https://docs.influxdata.com/influxdb3/core/reference/internals/data-retention/).

## T008 validation

Validated on 2026-10-12 with Docker Desktop 4.95.0, Engine 29.8.2, and Compose 5.6.0 in the isolated
`centralmobilidade-t008` project. Compose configuration and image builds passed. Seven long-running
services reported healthy, and three init jobs exited successfully. Grafana reported Infinity 4.1.1
with `signature: valid` and `signatureType: grafana` through its authenticated plugin API.

After `down` and `up --wait` without deleting volumes, proof files survived in all eight named volumes;
a PostgreSQL row and one point in each InfluxDB database remained queryable. Repeated database init
completed successfully. Quarantine permissions were `700`, owned by `1654:1654`. Configuration checks
confirmed fixed image tags and no Grafana attachment to the pending network. Validation containers
were stopped and their volumes preserved. This checks local persistence, not production HA or the
later queue, retention, privacy, and load-test gates.
