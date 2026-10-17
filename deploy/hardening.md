# Compose runtime hardening — T086

The base composition and the optional app, emulator and monitoring profiles use
explicit Engine CPU/memory/process limits, a read-only root filesystem,
`no-new-privileges` and dropped Linux capabilities. Application images retain the
.NET non-root user and include a pinned BusyBox HTTP client for health probes.
Configuration/secret mounts stay read-only; the seven existing named data volumes,
their keys, paths and Influx node identity are unchanged.

## Initial budgets

| Service | UID:GID | CPU ceiling | Memory ceiling | PID ceiling |
| --- | --- | ---: | ---: | ---: |
| PostgreSQL | 999:999 | 1 | 1 GiB | 256 |
| RabbitMQ | 100:101 | 1 | 1 GiB | 512 |
| InfluxDB | 1500:1500 | 2 | 4 GiB | 512 |
| Prometheus | 65534:65534 | 0.5 | 768 MiB | 256 |
| Loki | 10001:10001 | 0.5 | 768 MiB | 256 |
| Grafana | 472:0 | 0.5 | 512 MiB | 256 |
| API | 1654:1654 | 1 | 1 GiB | 512 |
| Worker | 1654:1654 | 2 | 2 GiB | 512 |
| Pub/Sub emulator | 65532:65532 | 1 | 1 GiB | 512 |
| Each optional exporter | 65534:65534 | 0.25 | 128 MiB | 128 |
| Influx initializer | 1500:1500 | 0.5 | 256 MiB | 64 |
| Quarantine initializer | 0:0 | 0.25 | 64 MiB | 32 |

These are initial development ceilings, not reservations or measured capacity.
Base daemons sum to 8 GiB; all optional daemons add 4.25 GiB. Initializers and the
Engine/OS need additional headroom. CPU ceilings may be oversubscribed. Validate
the actual Docker VM/host budget and tune with representative load before claiming
the 5,000-event/s target or sizing retention. A memory/PID limit can terminate or
prevent work; inspect OOM/restart state and backlog rather than treating recovery
alone as evidence of adequate capacity.

## Writable boundaries

Persistent state remains on PostgreSQL, RabbitMQ, InfluxDB, Prometheus, Loki,
Grafana and quarantine named volumes. Other writable paths are bounded tmpfs:

- Base services: `/tmp`, 64 MiB; PostgreSQL additionally has a 16 MiB runtime socket
  directory owned by UID 999.
- API, worker and emulator: `/tmp`, 256 MiB. API upload spooling uses this temporary
  space; it is shared by concurrent requests and contributes to the memory ceiling.
  Exhaustion must fail the operation, not make unconfirmed data accepted.
- Exporters: `/tmp`, 16 MiB. Emulator HOME/Cloud SDK configuration also lives in
  temporary space; emulator state remains session-local.

Tmpfs uses `noexec,nosuid,nodev`; it disappears on container replacement. Shared
memory mounts managed by Docker remain ephemeral. Provisioning, plugins, binaries
and configuration do not require a writable root filesystem.

The terminating quarantine initializer is the sole UID-0 exception. It has no
network, drops all default capabilities and restores only CHOWN, FOWNER and
DAC_OVERRIDE to create/fix the app-owned rejection directory with mode 0700.
It does not run the application. All continuous services and the Influx initializer
use explicit non-root users. Node exporter retains its opt-in read-only host mount
and host PID namespace; it receives no additional capabilities or root user. Some
host filesystems may remain inaccessible to that user.
The host bind uses private propagation so it also works when Docker Desktop's
root mount is not shared. Newly added host mounts may require exporter recreation;
review propagation separately on hosts that need live mount discovery.

## Health and restart semantics

Daemons use `restart: unless-stopped`. Initializers use `restart: "no"` and completion
dependencies so a failed job blocks its dependents. API/worker retain a 60-second
stop budget, covering the worker's default 45-second shutdown budget. If runtime
drain settings increase, increase the Compose grace period accordingly.

Engine restarts apply to process exits; an **unhealthy** status alone does not
restart a container. A manual stop remains stopped. Invalid configuration can
therefore either remain unhealthy or enter an exit/restart loop; inspect the cause.
Dependency `service_healthy` conditions gate startup, not ongoing availability.

Database/broker/native service probes remain in place. API/worker probes check
`/health/live` on ports 8080/9464: they prove the HTTP process is alive, not durable
intake readiness. Use `/health/ready`, `/health/dependencies` and `/health/worker`
for dependency/consumer readiness. Exporter probes check their HTTP metrics endpoint;
an exporter can be healthy while `pg_up` or a collector reports failure. Review
Prometheus scrape/dependency signals and the [local alert runbook](grafana/provisioning/alerting/README.md).
Changing app listen ports requires changing the matching Compose health probe.

## Existing deployments and evidence

File-backed secrets replace the earlier environment-backed Compose secrets, which
the installed Compose implementation cannot populate into read-only service roots.
Configure absolute `POSTGRES_PASSWORD_FILE`, `GRAFANA_ADMIN_PASSWORD_FILE`,
`INFLUXDB_GRAFANA_READ_TOKEN_FILE` and (monitoring overlay)
`POSTGRES_METRICS_PASSWORD_FILE` paths in the private deployment environment. Files
contain the existing raw UTF-8 value, without JSON or a BOM. Keep the Influx admin
token JSON source unchanged and keep RabbitMQ's existing environment setting.
On Linux, source-file ownership/ACLs must allow the relevant service UID to read
the file while excluding other host users; local Compose bind-backed secrets do
not remap source ownership through `uid`/`gid` options. Protect the parent directory
and Windows ACLs too. Do not commit these files or print resolved configuration.

InfluxDB's Python package management is explicitly disabled: this ingestion stack
uses the .NET worker and no Python plugin package-install API. InfluxDB otherwise
tries to initialize a mutable Python environment during startup. Retained data,
query/write APIs and catalog identity remain unchanged; enabling Python package
installation later needs a separately reviewed immutable environment configuration.

Review the merged model with the same explicitly selected Compose files/profiles
used for startup. Apply through the normal reviewed restart/recreation procedure,
preserving the project name, existing volumes and token files. Explicit non-root
users require existing data directories to have compatible ownership; the pinned
official images and newly created volumes are tested. If a custom/legacy volume
has other ownership, inspect and reconcile that specific volume offline with its
backup before starting the changed service. The app startup does not recursively
change database volume ownership.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~ComposeRecoveryTests
```

The suite resolves every profile and inspects actual Engine restrictions. It runs
the base stack, restarts/recreates it, checks rows, confirmed durable messages,
authenticated Influx points and all seven volume markers, and exercises automatic
broker restart. It also builds the actual app Dockerfiles and starts the app,
emulator and exporters under their shipped restrictions. App profile validation
checks process liveness and quarantine permissions, not full ingestion acceptance;
separate integration suites cover the application flow. Exporter endpoint checks
do not certify host permissions or monitoring-role authorization.

Tests use UUID projects, ephemeral loopback ports and synthetic credentials. They
reset the optional worker env-file declaration before resolution and never load
the user's `.env.worker`. Cleanup removes only test-owned resources after checking
their ownership; no existing stack is reconfigured. Private evidence is ignored
under `artifacts/validation/T086/` and the shared fixture's `T078/` directory.
Load/soak, host loss, disk exhaustion and production security scanning remain
separate acceptance gates.

References: [Compose service controls](https://docs.docker.com/reference/compose-file/services/),
[tmpfs semantics](https://docs.docker.com/engine/storage/tmpfs/).
