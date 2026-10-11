# Research: Vehicle Telemetry Ingestion

## Decision 1: Runtime baseline

**Decision**: Use .NET 10 LTS and ASP.NET Core 10.

**Rationale**: .NET 10 is supported through November 2028 and meets the requirement not to begin on an
already aging runtime. Request decompression supports gzip and decompressed-body limits, although the
gateway will use an explicit bounded pipeline where it needs separate compressed and decompressed
controls.

**Alternatives considered**: .NET 8 is near end of support; a future/preview runtime is unsuitable for
the initial production baseline.

## Decision 2: InfluxDB edition and write API

**Decision**: Use self-hosted InfluxDB 3 Core and native `POST /api/v3/write_lp` with
`accept_partial=true`, explicit `precision=millisecond`, and durable sync (`no_sync=false`).

**Rationale**: Core is free, open source, Docker-supported, and supports GCS object storage later. The
v3 endpoint natively accepts gzip and reports partial writes; the v2 endpoint is compatibility-only.
Durable synchronization is preferred because the worker must not acknowledge the queue before storage
accepts responsibility.

**Alternatives considered**: InfluxDB Enterprise adds HA and historical compaction but is commercial;
the v2 API gives weaker partial-write behavior; direct SDK point construction adds unnecessary
allocation when the producer already sends Line Protocol.

## Decision 3: Historical query envelope

**Decision**: Accept Core for the MVP, restrict historical dashboard use to bounded single-vehicle
trip ranges, and validate old-trip queries with representative Parquet file counts.

**Rationale**: Core defaults to an approximately 72-hour query range per query due to its file limit,
including historical windows. A trip lasting a few hours can fit even when it occurred years ago, but
five-year retention does not imply efficient five-year scans.

**Alternatives considered**: Enterprise for long-range compaction; exporting historical analysis to a
separate analytical store. Both remain future options if measured queries exceed Core's envelope.

## Decision 4: Durable local queue

**Decision**: Use RabbitMQ with durable topology, persistent messages, mandatory routing, publisher
confirms, manual consumer acknowledgement, bounded prefetch, retry exchanges, and a dead-letter path.
Use a provider-neutral queue port.

**Rationale**: RabbitMQ confirms establish the required `202` durability boundary. Manual acknowledgements
provide at-least-once processing. The provider is easy to operate in Docker Compose and offers mature
monitoring.

**Alternatives considered**: A memory channel cannot satisfy durability. Kafka/Redpanda provides long
log retention but adds unnecessary operational complexity to the initial batch pipeline. The Pub/Sub
emulator is for compatibility testing only because its state lasts only for the emulator session.

## Decision 5: RabbitMQ topology on one local host

**Decision**: Use a durable classic queue for the single-node pilot and document quorum queues as the
multi-node production option. Do not claim host-loss protection locally.

**Rationale**: A quorum queue needs an odd multi-node cluster to provide its intended safety. Running
three broker containers on one physical host creates the appearance, not the substance, of fault
tolerance. Publisher confirms remain mandatory in either topology.

**Alternatives considered**: A single-node quorum queue offers no physical redundancy; three containers
on one host share the same failure domain.

## Decision 6: Queue message granularity

**Decision**: Enqueue one original compressed HTTP batch per message, with a small versioned envelope
and message attributes; do not enqueue each telemetry line separately.

**Rationale**: Preserving gzip avoids recompression on intake, reduces broker cost/volume, and prevents
the 1 KB Pub/Sub billing minimum from applying to every small event. An 8 MiB compressed limit leaves
room below Pub/Sub's 10 MB message maximum.

**Alternatives considered**: Per-line messages simplify partial retries but greatly increase message
count, broker metadata, and future Pub/Sub cost. Storing the body in object storage and queueing a
pointer becomes attractive only when batches exceed the provider ceiling.

## Decision 7: Control-plane persistence

**Decision**: Use PostgreSQL for batch state, error summaries, audit, configuration, cleanup leases,
and bounded idempotency/conflict evidence. Store raw rejected lines in a separate protected quarantine.

**Rationale**: Batch status requires transactional state transitions and retention queries. Separating
raw rejection content enables tighter access and deletion controls and keeps it out of ordinary logs.

**Alternatives considered**: Keeping status only in RabbitMQ makes random lookup and 30-day retention
awkward. Storing raw lines in log aggregation violates privacy constraints.

## Decision 8: Duplicate and conflict detection

**Decision**: Separate permanent write protection from diagnostic classification. Define a
provider-neutral, recoverable leased identity guard retained for at least the telemetry-retention
period, and run a mandatory benchmark before fixing its physical implementation. The original owner may
resume an incomplete reservation; a competing worker may take over only after lease expiry with a new
fencing version. Only a confirmed `Committed` identity permanently blocks another Influx write. Retain
canonical fingerprints for 30 days
to classify an existing identity as identical replay or conflict; after that window, reject the write as
already existing without promising detailed classification. Candidate implementations include a
partitioned compact identity index or an atomic external reservation store; Influx last-write-wins is
not an acceptable guard.

Because PostgreSQL and InfluxDB cannot commit atomically, a crash after an accepted Influx write but
before guard confirmation may cause the owner to repeat the identical physical write. The guarantee is
one immutable logical event, not one network call. Promotion is modeled separately as a fenced guard
location transition from pending to queryable storage and is not classified as a duplicate.

**Rationale**: At 5,000 events/s, even a 30-day relational ledger approaches 13 billion keys and a
retention-lifetime guard is a major capacity commitment that is not credible without measurement.
InfluxDB's duplicate-point merge behavior alone cannot guarantee
"preserve the original and reject a conflicting second event" atomically. This must be proven, not
hidden behind a repository abstraction.

**Alternatives considered**: Read-before-write point lookups can overload storage and are race-prone; a
probabilistic Bloom filter can have false positives and cannot authorize a destructive decision;
allowing last-write-wins violates the agreed rule; requiring an upstream immutable event ID remains a
future contract improvement.

## Decision 8A: API identities and authorization

**Decision**: Use separate opaque bearer credential stores for the internal producer and local internal
operators. Operator profiles contain a stable actor ID, explicit permissions, and allowed-company
scopes. Producer credentials can submit and inspect only their own batches. Sensitive operator reads
require durable audit persistence before content is returned.

**Rationale**: Authentication proves who called; it does not grant administration. Separating credential
types prevents the high-volume producer secret from becoming a raw-quarantine or identity-access key,
while scoped operator profiles make forbidden and non-disclosing not-found behavior implementable and
testable.

## Decision 9: Grafana and identity

**Decision**: Use a hybrid Grafana model. A privileged internal organization, never shared with viewers,
uses the built-in InfluxDB 3 SQL/FlightSQL datasource and may view all known companies. Restricted
viewers are placed in separate Grafana organizations with no InfluxDB datasource and use the
Grafana-maintained Infinity JSON datasource against bounded presentation endpoints. Each viewer
organization receives a distinct non-human service credential scoped to exactly its companies. Resolve
driver identity through the same secured presentation API rather than joining personal data into
InfluxDB or exposing cadastral storage directly to dashboards.

YAML provisions only the internal default organization and its direct datasource/dashboards. After
Grafana is healthy, a protected `GrafanaBootstrapAdminCredential` uses Basic authentication for the
server-level Admin Organizations API, which does not accept service-account tokens. It reconciles
organizations and memberships, then creates or rotates a least-privilege
`GrafanaOrganizationServiceAccount` inside each organization for organization-local datasource and
dashboard operations. Each Infinity datasource holds a separate `ViewerPresentationServiceCredential`
recognized by the .NET API and scoped to that organization's companies; neither Grafana credential is
ever reused for presentation.
The container build pins compatible Grafana OSS and signed Infinity versions. In the local-user MVP,
viewer audit attribution stops at organization and presentation credential. Individual human
attribution requires future trusted per-user identity propagation and is not claimed by this design.

**Rationale**: Direct Influx access preserves flexibility and performance for trusted internal users.
Grafana OSS dashboard filters are not a tenant-security boundary, so viewer queries require server-side
scope enforcement. Infinity consumes bounded JSON APIs and keeps viewer organizations free of direct
Influx credentials. Backend identity resolution can enforce authorization, return only name plus CPF
suffix, and audit each disclosure.

**Alternatives considered**: Storing names/CPF in telemetry duplicates personal data and expands breach
impact. Client-side dashboard joins risk exposing unmasked source fields.

## Decision 10: Observability

**Decision**: Instrument API and worker with OpenTelemetry; expose Prometheus metrics; emit structured,
privacy-safe logs to Loki; provision Grafana dashboards and local-only alerts.

**Rationale**: Correlation uses `traceId`, `batchId`, producer, company, stable vehicle ID, and line
position. Seeded-secret/privacy tests can verify forbidden content never enters telemetry systems.

**Alternatives considered**: Logging raw rejected lines would aid debugging but violates minimization;
protected quarantine supplies the required diagnostic path.

## Primary references

- [.NET lifecycle](https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core)
- [ASP.NET Core request decompression](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/request-decompression?view=aspnetcore-10.0)
- [InfluxDB 3 write API](https://docs.influxdata.com/influxdb3/core/api/write-data/)
- [InfluxDB 3 gzip writes](https://docs.influxdata.com/influxdb3/core/write-data/http-api/v3-write-lp/)
- [InfluxDB 3 Core query limits](https://docs.influxdata.com/influxdb3/core/get-started/query/)
- [RabbitMQ confirms and acknowledgements](https://www.rabbitmq.com/docs/confirms)
- [RabbitMQ reliability](https://www.rabbitmq.com/docs/reliability)
- [Pub/Sub emulator](https://docs.cloud.google.com/pubsub/docs/emulator)
- [Pub/Sub quotas](https://docs.cloud.google.com/pubsub/quotas)
- [Grafana InfluxDB data source](https://grafana.com/docs/grafana/latest/datasources/influxdb/)
