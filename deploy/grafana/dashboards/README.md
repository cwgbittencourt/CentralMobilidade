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
