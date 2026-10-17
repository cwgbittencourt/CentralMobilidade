# Platform operations (T084)

`platform-operations.json` is provisioned read-only into the **Platform Operations**
folder in internal organization 1, using `internal-prometheus`. It is not part of
the viewer reconciliation directory and has no company/vehicle/identity variables,
credentials or raw logs. Default range is one hour in UTC, refresh 30 seconds.

All rates use `$__rate_interval` and sum **after** `rate`, preserving counter-reset
handling. Histogram p95 values aggregate bucket rates by `le` before quantiles.
Duration buckets explicitly cover milliseconds through minutes, and delivery-age
buckets extend through seven days; quantiles remain bucket-interpolated estimates.
Missing series remain absent, rather than `or vector(0)`, last-value fill or a green
health mapping. Check scrape health first; at least two scrapes and activity are
needed for meaningful rates/quantiles. `up` alone does not prove application
readiness, storage connectivity or durability. PostgreSQL includes `pg_up` and its
scrape-error signal for that reason.

| Panels | Meaning and boundaries |
| --- | --- |
| Accepted rate / gzip bytes | HTTP intake observations after durable acceptance; not all attempts, point throughput or decompressed bytes |
| RabbitMQ depth | Ready/unacknowledged totals across local main/retry/dead queues; not Pub/Sub backlog |
| Delivery age p95 | Batch ReceivedAt to worker processor start, including slot/prefetch waits and redelivery; only delivered work, not the oldest still queued item |
| Attempt latency p95 | Whole worker processor call, including retry/cancellation paths; not ingestion-to-query visibility |
| Writer latency p95 | Writer call duration, including failure/cancellation; not per-point latency or time until queryable |
| Rejections | New committed line rejections from the fixed catalog; not all HTTP errors |
| Duplicate outcomes | Committed identical replay versus DUPLICATE_CONFLICT; a repeated persistence call does not increment again |
| Retry / terminal failures | Observed committed state entries; not raw broker delivery attempts or guaranteed storage failures |
| Storage failures | Thrown writer call failures excluding caller cancellation, and permanent per-line storage rejections as distinct series |
| CPU / memory / filesystem | Linux host metrics from T082; Docker Desktop means its Linux VM, not Windows/NTFS or per-volume bytes |
| Broker pressure | RSS/limit, free-disk/low-watermark; disk ratio below 1 warrants attention, no capacity certification |

The delivery-age histogram is deliberately observational: an idle/stalled consumer
cannot report queued message age. Future timestamps are clamped to zero rather
than producing negative ages; clock synchronization is still required. No
managed Pub/Sub retention/backlog visibility or oldest-queued-age guarantee is
provided by these local metrics. Alert rules and capacity acceptance remain T085
and the separate performance gates.

## Instrumentation supporting this dashboard

T084 connects the existing privacy-safe intake, processing and write instruments
to the API/worker/writer boundaries and adds fixed instruments for delivery age,
writer failures and committed identical replay. They contain no dynamic labels or
exemplars. Metrics listener failures cannot alter acceptance, worker retry handling
or storage outcomes. Processing durations use monotonic elapsed time; age uses UTC
ReceivedAt. Writer validation failures are counted as failed calls too; caller
cancellation is excluded from failure counts while its duration remains observed.

Replay observations happen only after the line-result transaction commits, using
the count of newly inserted IdenticalReplay rows. Existing rows and rolled-back
pages do not add observations. Counters reset at process restart and the gap
between durable commit and in-process emission can lose observations: they are
not an exactly-once audit ledger. API acceptance and delivery/write observations
are likewise process observations, not a historical backfill. The decompressed
byte instrument is not charted because this boundary does not measure it.

Enable the [T082 metrics composition](../../prometheus/README.md) and application
profile for real data. Apply the existing Grafana Compose service to mount the new
JSON and provider; preserve its named volume. Do not manually import the dashboard
into viewer organizations or grant those users access to internal Prometheus.

## Validation

`PlatformOperationsTests` provisions the real pinned Grafana, Prometheus and image
renderer. It evaluates every production query with real application instruments
and deterministic infrastructure **fixtures**, checks filesystem arithmetic and
absence behavior, and renders representative panels. This proves query/renderer
compatibility, not a real infrastructure load or managed-service test. The
fixture uses the exclusive batch-metrics collection because SDK instruments are
process-global, and waits for three successful scrapes before checking rates. The
persistence regression covers identical-replay commit/replay behavior. Existing
API, worker, writer and privacy suites cover their production call boundaries.
Rendered evidence stays ignored under `artifacts/validation/T084/render/`.

References: [Grafana JSON model](https://grafana.com/docs/grafana/latest/visualizations/dashboards/build-dashboards/view-dashboard-json-model/),
[provisioning](https://grafana.com/docs/grafana/latest/administration/provisioning/).
