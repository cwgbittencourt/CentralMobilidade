# Local dependency stack

Run Docker Compose from the repository root. This stack persists PostgreSQL, RabbitMQ, queryable
InfluxDB, pending InfluxDB, Prometheus, Loki, Grafana, and rejection quarantine in separate named volumes.
The base file publishes only Grafana, on `127.0.0.1:3000`. Load the development override explicitly
to publish dependency ports and enable optional application/emulator profiles.

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
WAL, Parquet files, network, and bootstrap credential. Pending storage has no host port in the base file and Grafana
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

## Development override and profiles

The development file must be included explicitly in every start, status, logs, or shutdown command.
It publishes ports only on IPv4 loopback and preserves authentication, named volumes, and pending
network isolation. Use the same secret setup described above.

| Service | Default host port | Override variable |
| --- | --- | --- |
| PostgreSQL | 5432 | `POSTGRES_PORT` |
| RabbitMQ AMQP / management | 5672 / 15672 | `RABBITMQ_PORT` / `RABBITMQ_MANAGEMENT_PORT` |
| InfluxDB queryable / pending | 8181 / 8182 | `INFLUXDB_QUERYABLE_PORT` / `INFLUXDB_PENDING_PORT` |
| Prometheus | 9090 | `PROMETHEUS_PORT` |
| Loki | 3100 | `LOKI_PORT` |
| API (`app` profile) | 8080 | `API_PORT` |
| Pub/Sub (`pubsub-emulator` profile) | 8085 | `PUBSUB_EMULATOR_PORT` |

Dependency-only development startup excludes both optional profiles:

```powershell
docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml up -d --build --wait
```

Build and start the API and Worker in Development mode:

```powershell
docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml --profile app up -d --build
docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml --profile app ps -a
```

The API serves its skeleton at `http://localhost:8080/` and aggregate Prometheus metrics at `/metrics`.
The Worker now remains running and serves `/metrics` on internal port 9464; no worker port is published
on the host. Its queue consumer is implemented in T053. Application templates are described below,
dependency adapters follow in later tasks, and readiness checks in T022. A running metrics endpoint
does not prove queue-consumer readiness.

Run the Pub/Sub emulator independently:

```powershell
docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml --profile pubsub-emulator up -d --wait pubsub-emulator
$env:PUBSUB_EMULATOR_HOST = "127.0.0.1:8085"
$env:GOOGLE_CLOUD_PROJECT = "centralmobilidade-local"
```

The fixed Google Cloud CLI `588.0.0-emulators` image listens on port 8085 and uses the fake project
`centralmobilidade-local` (`PUBSUB_PROJECT_ID` can override it). Host-based SDK tests use the host
endpoint above; clients on the `backend` Docker network use `pubsub-emulator:8085`. .NET clients must
explicitly enable emulator detection in their builders; the adapter and parity tests follow in
T077/T080. Activating this profile does not switch the application's initial RabbitMQ provider or
mount cloud credentials. Both profiles can be selected together by repeating `--profile`.

The emulator has no persistent volume, and its state must not be treated as durable queue storage.
Remove `PUBSUB_EMULATOR_HOST` from your shell before using real Pub/Sub. See the
[official emulator instructions](https://docs.cloud.google.com/pubsub/docs/emulator).

Stop every selected profile and preserve dependency volumes:

```powershell
docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml --profile app --profile pubsub-emulator down
```

T009 validation on 2026-10-12 confirmed that base-only commands remain unchanged, both profiles are
disabled by default, all development port bindings use `127.0.0.1`, and the eight persistent volumes
remain declared. Both application builds passed. The Development API returned HTTP 200; the Worker
skeleton exited with code zero, and a separate check confirmed its UID 1654 can write to the shared
quarantine directory. The emulator reported healthy and completed topic/subscription creation,
publish, pull, and acknowledgement through its REST API. Validation used the isolated
`centralmobilidade-t009` project and non-default host ports. Its containers were stopped without
deleting dependency volumes. Provider parity and worker readiness remain later implementation gates.

## Configuration templates

Copy `.env.example` to an untracked root `.env` and fill in its blank secret values and absolute token
file paths. The example intentionally cannot start the stack with working credentials. Compose reads
this file for interpolation; it does not automatically export every key into API/Worker containers.

Both application `appsettings.json` files contain the same `Telemetry` limit defaults from the plan:
8 MiB compressed, 64 MiB decompressed, 100:1 expansion, 16 KiB per line, 50,000 lines, 30-second
upload, 5,000-point/2 MiB/1-second write batches, seven-day queue retention, 30-day status/quarantine/
fingerprint evidence, and 60-day local telemetry retention. Durations use .NET `TimeSpan` strings.
Viewer limits are 500 vehicles, a 24-hour/10,000-point trip, and seven-day/one-minute/10,000-point health
queries. API and Worker bind the `Telemetry` section to shared options and validate it before host
startup. Unknown keys, malformed values, nonpositive sizes/counts/durations, nonfinite expansion
ratios, and inconsistent byte limits fail startup. Viewer limits may be reduced, but cannot exceed
the documented maxima or use health granularity below one minute. Quarantine retention may be reduced
but cannot exceed 30 days; queue retention must cover seven days. Batch status and fingerprint evidence
must remain at 30 days, and local telemetry retention must match the databases' fixed 60-day policy.
Request/stream enforcement and retention cleanup are implemented in the later processing tasks.

Application PostgreSQL connection strings, RabbitMQ passwords, and InfluxDB tokens are blank. Supply
them through the application process environment or a deployment secret provider using .NET keys
such as `ConnectionStrings__TelemetryControl`, `Queue__RabbitMq__Password`,
`InfluxDb__Queryable__Token`, and, for the Worker only, `InfluxDb__Pending__Token`. Use least-privilege
application credentials rather than the InfluxDB bootstrap admin token files. Credential loading and
provider registration are implemented in the subsequent security/infrastructure tasks. Do not store
producer or operator bearer tokens in these files; their separate credential stores follow in T017.

Service URLs in the JSON templates use Docker DNS names. For a host-based development process,
override `Queue__RabbitMq__Host` to `localhost`, its port to the development AMQP port, and
`InfluxDb__Queryable__Url` / `InfluxDb__Pending__Url` to the corresponding loopback URLs. Set the
PostgreSQL connection string to the published PostgreSQL endpoint. Override `Quarantine__Path` to
a protected local directory and `AllowedHosts` for any additional API hostname. Pub/Sub fields are
inactive while `Queue:Provider` is `RabbitMq`; emulator configuration must be selected explicitly.

T010 validation confirmed valid JSON, matching limits in both applications, blank credential fields,
coverage of the Compose interpolation variables in `.env.example`, and inclusion of each JSON file
in its Release publish output. Compose configuration passes with supplied validation secrets and
rejects the untouched example. The real `.env` and token-file directory remain ignored by Git.
