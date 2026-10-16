# Latest vehicle position (T072)

The internal and viewer `latest-vehicle-position.json` dashboards show the latest
stored event per vehicle for one selected company. They use a native table with
filterable vehicle/field columns, UTC event time, latitude/longitude, speed in km/h
and ignition. They omit plate, driver identity, name and CPF. They refresh every
30 seconds and hide the irrelevant global time picker: a vehicle's last known
position must not disappear merely because it is older than the selected time range.
The event timestamp makes old positions visible; the table is not a liveness claim.

The internal dashboard binds only `internal-influx`, provisioned in organization 1.
Its SQL uses a quoted company variable (`:sqlstring`), partitions by company/vehicle,
orders by event time descending before selecting row one, and applies a stable
vehicle order and hard 500-vehicle limit. It does not query a company registry or
join personal data. Enter an exact producer-supplied company ID; no case folding or
trimming is applied. Core's retained-file query envelope still applies; a query
failure must not be interpreted as an empty fleet. This is for privileged internal
operators and does not provide a tenant boundary by itself.

The viewer dashboard uses only Infinity's backend JSON parser and
`/api/v1/presentation/telemetry/latest`, with percent-encoded company/limit variables.
No SQL, arbitrary URL, credentials or direct Influx datasource occur in its query
model. The API applies current service-credential permissions and company scope.
Changing a Grafana variable grants no access. Organization inventories must remain
Infinity-only. A lower API vehicle ceiling can be selected in the Limit textbox;
invalid or over-ceiling values are rejected by the API.

The viewer table explicitly shows the first page only (default 500). It does not
silently claim to cover larger fleets: follow `X-Next-Cursor` through the API for
subsequent pages. No unbounded backend auto-pagination is enabled. The table's
column filters operate on the displayed page, not on the entire fleet.

Deploy the internal file through the existing YAML provider. Use the T071
reconciler to install the viewer template: `${VIEWER_DATASOURCE_UID}` is replaced
with that organization's source UID, and the complete JSON string
`${VIEWER_COMPANY_ID}` is safely replaced with the first configured permitted
company for the initial selection. The API profile remains the authorization
source; this initial selection is only a convenience. Manual imports must set
these template values explicitly. Neither dashboard enables public sharing or
contains a bearer token.

Automated validation uses the production models, pinned InfluxDB/Grafana/Infinity,
the production T069/T070 handlers/routes, PostgreSQL audits, the T071 reconciler
and a test-only pinned image renderer. The fixture seeds unordered 28-day-old
events plus another vehicle/company, checks real SQL/Infinity frames and API
isolation/limits, and renders each panel to PNG. It does not create substitute
business dashboards or fake query handlers. Images are private/ignored under
`artifacts/validation/T072/render/`; no existing stack is modified. Rendering
infrastructure is confined to tests and does not change production Compose.

Official references:

- [Grafana SQL string and URL variable formatting](https://grafana.com/docs/grafana/latest/dashboards/variables/variable-syntax/)
- [Infinity backend parser](https://grafana.com/docs/plugins/yesoreyeram-infinity-datasource/latest/query/backend/)
- [Remote image rendering](https://grafana.com/docs/grafana/latest/setup-grafana/image-rendering/)

# Single-vehicle trip (T073)

Both `vehicle-trip.json` dashboards display an ordered coordinate table for one
exact company/vehicle pair, with UTC event time, speed and ignition. Enter the
stable vehicle ID, then select an absolute or relative range in Grafana's time
picker (default: last hour). Historical windows are supported; their age does not
change the 24-hour duration ceiling. Start is inclusive and end is exclusive.
Neither model resolves a driver or includes plate, name or CPF.

The internal SQL uses escaped `:sqlstring` company/vehicle values, millisecond
Grafana time bounds converted with `to_timestamp_millis`, a positive-duration
guard of at most 86,400,000 milliseconds, and `ORDER BY time ASC LIMIT 10000`.
An empty, reversed or over-24-hour window produces no rows; it is not silently
clamped. This template guard is for privileged operators, not an authorization
boundary against users who already have direct datasource access. The retained
file query envelope still applies; errors are not proof of an empty trip.

The viewer uses only the closed `/api/v1/presentation/telemetry/trips/{vehicleId}`
endpoint through Infinity backend JSON. Company and vehicle values are URL
encoded; time bounds use Grafana's UTC ISO formatter. The API enforces current
company scope and configured duration/point ceilings. Invalid ranges yield an
API error, and a changed unauthorized company yields a non-disclosing 404.
The point-limit textbox defaults to 10,000 and can be reduced for deployments
with tighter configured limits. No SQL or automatic pagination is exposed.

Both paths order events before limiting. At the point ceiling the displayed
trajectory can be incomplete: narrow the time window to inspect later events.
The table's sorting/filtering affects displayed points only. The selected range
has millisecond precision. Refresh is manual by default to keep a historical
inspection stable; the time picker also offers bounded refresh intervals.
Provisioning uses the same internal YAML and viewer reconciler as T072.

Validation queries the actual production targets through both real datasources,
then renders their PNGs with the test-only renderer. Unordered 28-day-old points,
another vehicle and another company verify event order and isolation; cases cover
exactly 24 hours, half-open bounds, empty/reversed/over-ceiling windows, viewer
point limits and unauthorized scope. Reviewed PNGs and reports are ignored under
`artifacts/validation/T073/`. This is query/render evidence, not a capacity claim.

- [Grafana global time variables and UTC ISO formatting](https://grafana.com/docs/grafana/latest/visualizations/dashboards/variables/global-variables/)
- [InfluxDB SQL time conversion](https://docs.influxdata.com/influxdb3/core/reference/sql/functions/time-and-date/#to_timestamp_millis)

# Company ingestion health and driver presentation (T074)

The two `company-ingestion-health.json` tables deliberately label their different
clocks and sources. Internal users query `internal-influx` directly: stored points
per company and minute of **event time**, including delayed historical events.
These are not rejected counts, receipt-time throughput or process counters. Viewer
users query the bounded ingestion-health API: persisted Accepted/Rejected line
results by **processing time** in PostgreSQL. Identical replays and results with no
company are excluded; rejected events do not exist in the telemetry table. Metadata
retention can therefore make the viewer series shorter than retained telemetry.
Do not reconcile these two series as if they measured the same events/time window.

Both default to the last hour, refresh every 30 seconds, use UTC and half-open
positive windows, and cap the range at seven days and output at 10,000 intervals.
Internal intervals are fixed at one minute, aligned to the selected start. Its SQL
returns no rows for an invalid window. The viewer interval and point limit can be
adjusted to the API's configured ceilings (minimum interval: 60 seconds); invalid
values produce an API error. Both omit empty intervals. Absence of a row does not
prove a healthy dependency or a measured zero. At the output ceiling, narrow the
range or increase the viewer interval; filtering/sorting only affects returned
rows. Queries remain subject to Core's file envelope and control-data retention.
The tables expose no coordinates or driver identity.

`viewer/driver-presentation.json` is a separate, manually refreshed lookup with a
blank initial driver ID. It uses only the authenticated driver presentation API,
with URL-encoded company/driver IDs. The table projects only `displayName` and
optional `cpfLast4`, labeled as the last four digits; it never reconstructs a full
CPF or joins identity to telemetry. The pseudonymous driver ID participates in
Grafana variable URL synchronization so saved selections/render links work. This
is an explicit authorized identity disclosure, not an anonymous/public panel.
The organization credential needs independent `driver.read` permission and the
requested company scope, in addition to `telemetry.read` for fleet/health views.
Provision these permissions server-side; importing a dashboard grants none.

Driver enrichment must be configured using the [optional adapter](../../../src/CentralMobilidade.Telemetry.Infrastructure/Cadastral/README.md).
Unavailable/missing identities yield 404, unauthorized identity requests 403, and
an unavailable audit commit 503 with no identity. Every successful API lookup
passes the durable audit barrier, including when the optional adapter caches an
identity. Attribution is to the organization/service credential, not an individual
Grafana user. Automatic refresh is disabled to avoid repeated background lookups;
changing inputs or manually refreshing performs a new audited request. Authorized
results are visible to members of that organization in their Grafana session.

The three production files are loaded through the existing YAML/reconciler. The
isolated test fixture uses real storage, API security/handlers, Grafana/Infinity
and renderer; only the external cadastral resolver is synthetic. It checks sparse
counts, replay/company/time bounds, viewer limits, minimal identity, committed
service-attributed audits, missing/forbidden identities and a real database trigger
refusing audit inserts. Reports and visually reviewed PNGs are ignored under
`artifacts/validation/T074/`; production Compose gains no renderer or new datasource.

- [InfluxDB SQL date bins](https://docs.influxdata.com/influxdb3/core/reference/sql/functions/time-and-date/#date_bin)
- [Infinity backend JSON projection](https://grafana.com/docs/plugins/yesoreyeram-infinity-datasource/latest/query/backend/)
