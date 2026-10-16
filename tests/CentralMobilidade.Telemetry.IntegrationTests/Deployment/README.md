# Compose recovery tests — T078

`ComposeRecoveryTests` executes the repository's real `deploy/compose.yaml` with
Docker Compose. It does not replace dependency services with health stubs or a
test-only equivalent composition. The development override is also resolved to
check API/worker mount and dependency declarations; application processes are not
started by this suite.

Seven cases cover:

- The seven named persistent volumes, writable service mount targets, six native
  server health probes, initializer completion gates and app-profile quarantine/
  PostgreSQL/RabbitMQ declarations.
- A full dependency smoke run (six healthy servers and two successful one-shot
  jobs), then service restart and forced container recreation. Each phase checks
  a real PostgreSQL row, a publisher-confirmed persistent RabbitMQ message in a
  durable queue, two authenticated InfluxDB points and filesystem markers on all
  seven volumes. Message inspection requeues instead of consuming the recovery
  evidence before the next phase. Influx queries compare identities, values and
  millisecond timestamps; the existing initializer runs idempotently.
- An explicitly external required PostgreSQL volume absent from Docker, which
  prevents service startup. Ordinary non-external named volumes are automatically
  created by Compose on a fresh installation: this test does not claim the engine
  detects accidental deletion/replacement of a volume or reconstructs lost data.
- Read-only PostgreSQL, InfluxDB and quarantine volumes, each preventing a
  successful ready startup. The mount changes affect only isolated test overrides.
- A failed quarantine initializer preventing its dependent Grafana from running.

Initial execution exposed an actual Compose build-context mismatch: the Grafana
Dockerfile's `COPY start.sh` requires `deploy/grafana` as its build context, while
the composition previously supplied the repository root. The production Compose
context was corrected to `./grafana` / `Dockerfile`; the image/plugin pins and
entrypoint remain the same. The real build in fixture startup guards this path.

## Running and isolation

Docker Engine, Docker CLI and the Compose plugin are required. The suite was
validated with Compose v5.6.0; it uses `up --wait` and `config --no-env-resolution`.
The latter avoids resolving a user's optional `.env.worker` while inspecting the
development profile. The root `.env` is not loaded: commands explicitly choose
the tracked secret-free `.env.example` and supply synthetic credentials in each
child process, without modifying the test runner's global environment.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~ComposeRecoveryTests
```

CLI discovery supports PATH, Docker Desktop under Program Files or the user's
LocalAppData installation, and `TELEMETRY_TEST_DOCKER_CLI` for an explicit executable
path. A missing/inaccessible CLI is a failure, not a skipped test. Run with access
to the intended local Docker engine and permission to build/start disposable
containers. Each command has a bounded outer timeout; service readiness also has
Compose deadlines. The first run may need to acquire pinned images/plugins.

Every case uses a fresh `t078-<UUID>` project overriding the default project name.
Docker builds create uniquely tagged Grafana/Loki test images from the actual
Dockerfiles. Test overrides change only those image tags, ephemeral loopback
RabbitMQ/Grafana ports, the read-only validation-script mount, and explicit fault
injections. They never select an existing user project or its fixed ports.

The offline Influx admin token is generated for a new test catalog. PostgreSQL,
RabbitMQ and Grafana use synthetic secrets. Grafana receives a distinct synthetic
read-secret placeholder: this suite checks its actual container/API health and
filesystem persistence, not datasource query authorization. Actual authorized
business queries/rendering are covered separately by T072-T074. The Influx data
proof uses the isolated bootstrap admin through its secret file, never token
arguments. All validation data are technical markers, not user telemetry/identity.

Cleanup first runs `compose down` for the exact UUID project without deleting
volumes. It then inspects each exact expected volume name and requires its
`com.docker.compose.project` ownership label to match before removing that
test-created volume. No volume/container prune, wildcard deletion, forced volume
removal or existing-stack reset is used. The fixture removes only its unique image
tags. Cleanup failures remain test failures; interrupted runners can leave test
resources for explicit owner-checked recovery.

Private diagnostics/override files and the generated offline token are under
ignored `artifacts/validation/T078/<fixture-UUID>/`. Protect these files. Resolved
Compose models stay in memory; stdout/full command arguments are not logged.
The command log records operation/exit status, and failed stderr is redacted for
known generated credentials. Credentials are supplied through child environment
or files, not command arguments. This is not a general observability privacy scan.

## Evidence limits

Named-volume markers demonstrate filesystem survival for Grafana, Prometheus, Loki
and quarantine; they do not prove Grafana SQLite transaction recovery, historical
metric/log ingestion correctness or encrypted quarantine reader semantics. The
native PostgreSQL/RabbitMQ/Influx assertions provide additional data-level evidence
for those three stores. Health probes do not replace authenticated application
readiness, API/worker acknowledgement-boundary tests or complete ingestion proof.

These are ordinary restart/recreation tests on one Docker host. They do not prove
host-loss recovery, backup restore, HA, seven-day queue retention, 60-day expiry,
five-year capacity or the later 100,000-event gate. Production deployment hardening
remains T086 and backup/restore procedures T087. Never apply test fault overrides
or test cleanup commands to an existing deployment.

- [Compose up and mounted-volume preservation](https://docs.docker.com/reference/cli/docker/compose/up/)
- [Named/external volume semantics and ownership labels](https://docs.docker.com/reference/compose-file/volumes/)
- [Production deployment contract](../../../deploy/README.md)
