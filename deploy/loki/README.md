# Loki retention and ingestion policy (T083)

The pinned Loki 3.7.0 single-node service mounts `loki-config.yaml`. It preserves
the existing `v13` TSDB schema epoch, `index_` prefix, 24-hour index period and
filesystem chunk store. The existing `loki_data:/loki` volume holds chunks, WAL,
TSDB indexes/cache and compactor work/retention markers. Do not replace/delete that
volume or change the schema epoch to apply this configuration.
The explicit TSDB paths preserve the prior bootstrap defaults
(`/loki/tsdb-shipper-active` and `/loki/tsdb-shipper-cache`).

## Thirty-day retention

`limits_config.retention_period: 720h` enables the 30-day policy with the compactor
(`retention_enabled: true`, filesystem delete-request store). Compaction runs every
10 minutes. Chunk removal is asynchronous, with a two-hour deletion delay after
marking; 30 days is the eligibility boundary, not a guarantee of physical erasure
at exactly that instant. Compactor markers persist in `/loki/compactor` so pending
deletion can resume after a restart. Monitor failures, disk headroom and compactor
progress. Filesystem storage does not remove logs just because disk space is low.

Queries are limited to a 720-hour lookback, and ingestion rejects timestamps older
than 720 hours. The query bound hides expired history while physical deletion is
pending. Accepted historical logs can require chunk flushing and index shipping
before historical queries find them; ingestion success is not immediate visibility. Retention
does not delete Docker's stdout log files, backups or protected quarantine; their
policies remain separate. Increasing retention cannot recover deleted data.

## Labels and trusted ingestion

The reviewed OTLP route is `http://loki:3100/otlp/v1/logs` (an OTLP HTTP exporter
uses base endpoint `http://loki:3100/otlp`). Only the resource attribute
`service.name` becomes an index label (`service_name`). Send the fixed value `api`
or `worker` from trusted deployment configuration. Default resource mappings are
disabled; all other resource, scope and log attributes are dropped, rather than
silently retained as structured metadata. Automatic service/level discovery is
disabled. The extra-label budget is one (`service_name` is excluded by Loki),
with name/value bounds of 32/64 bytes. The OTLP mapping itself emits only `service_name`.
Batch/trace/company/vehicle IDs, instance/pod/container identifiers, driver data,
coordinates, plates, CPF and credentials must never become stream labels.
The mapping applies to new OTLP ingestion; it does not rewrite stored bodies or
existing indexed labels. Review pre-policy data separately when adopting it.

Loki configuration filters **attribute names**, not arbitrary label values or log
bodies. It does not enforce `api|worker` as the service value, redact an unsafe body
or filter the intrinsic OTLP scope name/version/severity fields. Configure those
fields as fixed technical values. Forward only JSON lines produced by the
application's [privacy logging policy](../../src/CentralMobilidade.Telemetry.Infrastructure/Observability/PrivacyLogging.md).
Validated opaque batch/trace correlation can remain in that sanitized JSON body
and be parsed at query time; do not promote it into labels. Keep the original body
as a string; forwarding the JSON's individual fields as OTLP attributes would
discard them under this policy. No log shipper or automatic stdout forwarding is
installed by this configuration task.

The native `/loki/api/v1/push` API bypasses OTLP attribute rules. Its label-count
limit allows one arbitrary extra label in addition to `service_name`, and is
**not** a key/value allowlist: a trusted native sender must itself use only
`{service_name="api"}` or `{service_name="worker"}` and sanitized bodies. Do not
accept producer/client-supplied labels or arbitrary container logs. If untrusted
senders need ingestion, first add an authenticated validating gateway/collector
and restrict direct access to Loki; this deployment does not supply that boundary.

`auth_enabled: false` selects one tenant; it is not authentication. The base
composition publishes no Loki host port. The development override publishes
loopback only. Restrict the backend network and development access to trusted
operators; Grafana's internal datasource queries that private service. Do not
interpret organizational Grafana isolation as authorization on Loki itself.

## Apply and verify

After reviewing the change and configuring the existing deployment secrets:

```powershell
docker compose -f deploy/compose.yaml up -d --build loki
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter FullyQualifiedName~LokiIngestionTests
```

Tests use disposable pinned Loki containers and the actual configuration validator.
They verify 31-day rejection, 29-day ingestion acceptance, current-log query and restart recovery, plus
real OTLP ingestion of the production logger's sanitized JSON alongside seeded
sensitive resource/scope/log attributes. Queries confirm those attributes are
absent from indexed labels and returned metadata. A native-push negative control
proves an arbitrary label is accepted, while two extra labels exceed the count limit.
This is not a 30-day soak or proof of physical chunk removal at production scale;
deployment acceptance must observe compactor retention/deletion progress and
disk usage over time. The Compose recovery regression also checks the existing
named-volume topology and dependency readiness after restart/recreation.

References: [Loki retention](https://grafana.com/docs/loki/latest/operations/storage/retention/),
[OTLP ingestion](https://grafana.com/docs/loki/latest/send-data/otel/),
[pinned OTLP matching semantics](https://github.com/grafana/loki/blob/v3.7.0/pkg/loghttp/push/otlp_config.go).
