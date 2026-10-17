# Initial local alerts (T085)

`telemetry-alerts.yaml` provisions six Grafana-managed rules into internal org 1,
folder **Platform Operations**, group `telemetry-local`. They evaluate the existing
`internal-prometheus` datasource every 30 seconds and link the T084 dashboard.
The annotations/labels contain only fixed technical text; no company, vehicle,
batch, trace, payload, filesystem path or credential becomes an alert dimension.

| Rule | Initial threshold | Pending period |
| --- | --- | --- |
| Backlog age | Observed delivery-age p95 > 120 seconds over 5 minutes | 2 minutes |
| Five-minute queryability risk | Delivery-age p95 + processor-call p95 > 180 seconds | 1 minute |
| Queue retention risk | More than 0.5 extrapolated deliveries above the exact 518400-second bucket over 5 minutes | 1 minute |
| Storage failure | More than 0.5 extrapolated thrown writer failures over 5 minutes, API and worker | 1 minute |
| Disk pressure | Minimum monitored filesystem available/size < 0.10 | 5 minutes |
| Worker silence | Consumer heartbeat age > 30 seconds, unavailable consumer or failed worker scrape | 1 minute |

These are initial operational thresholds, not capacity acceptance criteria.
`increase` is extrapolated, so thresholds of 0.5 tolerate rounding while detecting
roughly one observed event. Review them against the actual environment before
depending on them. Rule evaluation and pending time add detection delay. The
queryability warning uses a three-minute signal plus one-minute pending period to
leave initial headroom before five minutes; scheduling adds up to one evaluation
interval and does not guarantee an early warning.

## Evidence and limitations

Age means **ReceivedAt to processor start for delivered work**, including slot,
prefetch and redelivery waits. Neither age rule sees the oldest still-queued
message, parked dead letters or managed Pub/Sub backlog. The retention rule uses
an explicit six-day histogram boundary, not interpolation across a multi-day
quantile bucket. The selector accepts `518400` and normalized `518400.0` labels; Prometheus 3 normalizes classic histogram boundaries on ingestion. It warns before the seven-day operational retention objective
but cannot inspect broker TTL, prove retention or detect every stationary queue.

Adding two p95 estimates does not produce a joint/end-to-end p95. Processing already
includes writer work, so write latency is not added a second time. The rule does
not measure the 99% queryability objective or actual Influx visibility. Use real
intake/query probes and the separate performance/retention acceptance gates for
those guarantees. State/line counters and observations remain process-local, with
commit/emission crash gaps and resets described in the T084 guide.

Storage failure counts thrown writer calls, excluding caller cancellation, and
includes argument-validation failures. Permanent reconciled per-line rejections
are a separate dashboard signal. The counter publishes an initial observed zero;
that is not a connectivity probe or proof of healthy storage. Disk pressure needs
the T082 host exporter and approved mounts; Docker Desktop means the Linux VM,
not Windows/NTFS, cloud disks or per-volume usage.

Worker silence uses a new unlabelled observable gauge,
`telemetry_worker_heartbeat_age_seconds`. Its age comes from the same monotonic
`WorkerProgress` signal used by readiness, not HTTP traffic or a standalone timer.
Before a consumer heartbeat or after MarkUnavailable it exports `+Inf`. Idle active
subscriptions keep signaling normally. A failed scrape adds a large score so a
previous healthy sample cannot hide that failure during Prometheus's lookback.
Missing service/data and query errors also require attention, even if HTTP metrics
would otherwise remain UP. The initial 30-second threshold must be reviewed when
changing the consumer heartbeat/freshness configuration.

All rules use **Alerting** for NoData and execution errors. This deliberately avoids
healthy-zero fallbacks; it also raises attention when exporters/apps are disabled,
before the first samples, or when an idle system has no recent delivery samples.
Inspect Grafana's evaluation reason/error and the T084 scrape/depth panels to
distinguish missing evidence from a measured threshold breach. NoData/Error may
bypass the ordinary pending period; do not interpret every firing as a proven SLO
or retention breach.

## Investigation

First inspect the rule's evaluation reason, datasource health and linked dashboard.
Then use the relevant signal:

| Signal | Initial response |
| --- | --- |
| Backlog age | Compare ready/unacked depth, delivery rate and worker processing/writer latency; check the consumer and its dependencies. |
| Queryability risk | Follow a real accepted batch through processing and query visibility; use the actual intake/query timestamps to assess the five-minute objective. |
| Retention risk | Inspect the queue/provider's retention settings and actual backlog evidence; delivered-age samples alone cannot establish the oldest queued message. |
| Storage failure | Inspect sanitized writer errors and dependency readiness; distinguish thrown calls from permanent per-line rejection outcomes. |
| Disk pressure | Identify the monitored Linux filesystem and owning data volume before planning capacity or cleanup; preserve backups and retained data. |
| Worker silence | Check Prometheus scrape health, worker readiness and the active consumer connection; a successful HTTP request cannot establish consumer progress. |

## Local-only notifications

Rules remain visible and evaluate normally. Every rule has an explicit
`notification_settings` reference to the shipped `grafana-default-email` receiver
and the `telemetry-local-only` mute interval (00:00–24:00 every day). The permanent
mute suppresses notifications, including resolved notifications, without pausing
rule evaluation. **No contact point, SMTP configuration, webhook, external
Alertmanager or recipient is created.** Existing root notification policy and
contact-point definitions are not replaced.

This assumes the shipped Grafana setup, including the default receiver and no
external alert forwarding. If an existing deployment renamed/deleted that receiver,
adapt the rule references to its reviewed receiver while retaining the permanent
mute before applying. Independently configured external forwarding needs its own
review; this file controls Grafana's internal notification routing. Enabling real
notifications is a separate explicitly authorized operational change: configure
the intended receiver, review the routing/NoData behavior, and remove the permanent
mute only as part of that change. Do not unmute merely to test a rule.

## Apply and validation

The existing Compose provisioning-directory mount includes this file. On a fresh
stack it loads at startup. For an existing Grafana, review UID/name conflicts and
the receiver/mute prerequisites before restarting/reloading provisioning; preserve
the named Grafana volume and existing secrets. Enable the app and the T082
monitoring/host profiles before interpreting the alerts. Inspect **Alerting → Alert
rules**, the six `telemetry-*` UIDs, and the permanent mute in org 1.

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter 'FullyQualifiedName~TelemetryAlertsTests|FullyQualifiedName~WorkerHeartbeatMetricsTests'
```

The tests load the shipped file in real pinned Grafana. A separate disposable
Grafana/Prometheus fixture copies it and accelerates only evaluation/pending/lookback
durations, retaining thresholds, math, labels and notification settings. Controlled
Prometheus samples demonstrate Normal → Pending → Firing → Normal, empty-data and
datasource-error behavior. Runtime Alertmanager configuration must reference the
all-day mute. No notification test is sent. A separate real worker metrics endpoint
test proves unavailable/fresh/aged/unavailable consumer age and that scrapes cannot
refresh it. Synthetic alert samples are not load, physical-disk or managed-retention
proof. Raw evidence remains ignored under `artifacts/validation/T085/`.

References: [Grafana file provisioning](https://grafana.com/docs/grafana/latest/alerting/set-up/provision-alerting-resources/file-provisioning/),
[notification configuration](https://grafana.com/docs/grafana/latest/alerting/configure-notifications/), [Prometheus 3 label normalization](https://prometheus.io/docs/prometheus/3.1/migration/).
