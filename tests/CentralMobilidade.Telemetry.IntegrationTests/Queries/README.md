# Historical single-vehicle trip queries (T065)

`VehicleTripQueryTests` adds ten executable cases: six provider controls pass on
the pinned `influxdb:3.11.6-core`; four active TDD cases require T069/T070 production
presentation composition. No case is skipped and no production query handler is
replaced with a test implementation.

## Real Core storage/query proof

One class fixture creates one disposable Testcontainers server with a filesystem
object store inside its container. Authentication is disabled only on this isolated
test server, with a random host port. The fixture never touches Compose, existing
volumes, catalogs or application secrets. Container disposal removes the fixture.

The start timestamp is midnight UTC 28 days before the test. It is older than 72
hours and still within the project's 60-day local retention window. This proves
historical querying, not five-year retention or the performance of aged catalogs.

The fixture writes native v3 Line Protocol with explicit LF on every platform,
millisecond precision, `accept_partial=false`, `no_sync=false`, and requires 204.
Five trip events arrive in order 15, 0, 20, 10, 5 minutes; distractors have another
vehicle in the same company and the same vehicle in another company. All companies
are valid IDs with no registry lookup. The bounded query returns only the selected
company/vehicle, sorts by event time before LIMIT, includes the lower bound and
excludes the upper bound. Point limits 1 and 3 must preserve the earliest events.

The separate `query_envelope` table contains 433 points in different 10-minute
generation-1 windows. The fixture retains the default query-file limit (432), fixes
generation-1 duration at its default 10 minutes, and accelerates WAL snapshots with
100 ms flush, one WAL file per snapshot and a one-second maximum snapshot age.
It polls `system.parquet_files`, requiring at least 433 envelope files and three
trip files before any query assertion runs. This avoids passing solely from the
in-memory write buffer. Polling has a 90-second deadline; failed writes, absent
files or server errors fail fixture initialization rather than count as TDD success.

The storage assertions demonstrate:

- A 24-hour historical interval returns exactly 144 persisted envelope points.
- A sparse seven-day interval over the trip table succeeds: 72 hours is not a hard
  duration validator applied to every query or a restriction to recent data.
- Scanning all 433 envelope files fails with the provider's Parquet/432 diagnostic;
  adding SQL LIMIT 1 does not bypass the scan limit. A subsequent one-hour query
  still returns its six points, proving the failure did not damage the data.

These are storage controls using fixed test SQL. They do not prove the production
application enforces authorization, bounds or safe SQL construction, and they do
not measure sustained throughput, query SLA or a production-size fleet.

## Production assertions awaiting T069/T070

Four active `PendingImplementation=T069-T070` cases require the real
`AddTelemetryPresentation(IServiceCollection, IConfiguration)` registration and
`MapPresentationEndpoints(IEndpointRouteBuilder)` mapping already described by
T064. They currently fail explicitly at the missing registration. When the real
composition differs, adapt fixture wiring while retaining the behavior assertions.

The harness supplies real bearer security, one scoped human with `telemetry.read`,
the actual Influx test URL/database and normal HttpClient registration. It does
not map a fake presentation route, inject fake telemetry results, or sort returned
DTOs before asserting. Tests require:

- The real trip route to return the old selected company's/vehicle's points in
  event order with exact timestamps and coordinates, both at the default limit
  and with limit 2.
- An empty old interval to return a successful empty array.
- A range one tick beyond 24 hours to return 400 REQUEST_INVALID even though the
  sparse provider control can query wider intervals.

The audit stub supplies receipts only to compose telemetry queries. It is not
durable audit proof and is never used to claim identity disclosure safety; that
remains the PostgreSQL-backed T064 tests and T068. No external cadastral dependency
is configured. HTTP authorization matrices and numeric limits remain covered by
T064; Core query failures must not become partial successful production results.

## Sources and evidence

Official sources checked on 2026-10-15:

- [Query data](https://docs.influxdata.com/influxdb3/core/get-started/query/):
  both recent and historical query ranges are subject to the approximate envelope.
- [Core query-file-limit](https://docs.influxdata.com/influxdb3/core/reference/config-options/#query-file-limit):
  the default limit is 432 Parquet files; 10-minute windows give approximately 72
  hours, potentially less when ingestion creates additional files in a window.
- [System data](https://docs.influxdata.com/influxdb3/core/admin/query-system-data/):
  use the query API to inspect storage metadata and executed queries.

The runtime test uses the pinned 3.11.6 binary; current documentation alone does
not establish the behavior of that version. Do not raise file limits or describe
five-year scans as supported on the basis of this small controlled dataset.

Run with `dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter
FullyQualifiedName~VehicleTripQueryTests`. Ignored logs/TRX are under
`artifacts/validation/T065/`. Initial fixture runs rejected platform-dependent
CRLF; explicit LF corrected the test payload without changing production code.

Final functional integration regression (excluding the separate ADR measurement
probe): 532 passed, 79 expected failures (75 T064 and four T065), 611 total,
zero skips and no unexpected failures. Unit tests passed 244/244 and architecture
47/47; contracts retained 86 passes and the 16 existing T067 failures. Solution
build had zero warnings/errors; whitespace verification passed. No load or
capacity benchmark was run for this task.
