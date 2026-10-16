# Presentation HTTP endpoints (T070)

`MapPresentationEndpoints(IEndpointRouteBuilder)` maps the production GET routes:

| Route suffix under `/api/v1/presentation` | Query parameters | Permission |
|---|---|---|
| `/telemetry/latest` | required empresaId; optional vehicleId, limit, cursor | telemetry.read |
| `/telemetry/trips/{vehicleId}` | required empresaId, fromUtc, toUtc; optional limit | telemetry.read |
| `/telemetry/ingestion-health` | required empresaId, fromUtc, toUtc; optional limit, granularitySeconds | telemetry.read |
| `/drivers/{driverId}` | required empresaId | driver.read |

All routes use the separate operator authentication scheme: enabled human operator
or viewer presentation service credentials. Producer credentials never grant
presentation access. Scope/resource permissions and cross-company decisions are
enforced by the T069 handlers against the current server-side profile. Request
headers cannot choose the actor, Grafana organization or permitted companies.

Query names are case-sensitive and closed. Duplicate keys, empty supplied values,
unknown parameters (including SQL), integer overflow/fractions, missing company
or malformed time are `400 REQUEST_INVALID` before query/resolver IO. UTC timestamps
require an explicit zone and seconds, with optional fractional seconds up to seven
digits. Bounds, IDs, cursor format and configurable limits are checked by the
handlers. Windows are inclusive start / exclusive end. No automatic parameter
binding response bypasses the stable Problem JSON envelope.

Latest returns an array and `X-Next-Cursor` only when another page exists; trips
and health also return arrays. An authorized empty query returns `200 []`.
Driver success exposes only driverId, displayName and optional CPF suffix. Every
response, including authentication failures and audit errors, sets `Cache-Control:
no-store` and `X-Content-Type-Options: nosniff`.

Unauthenticated, invalid and producer requests are audited as unidentified before
the `401` Bearer challenge. Endpoint metadata supplies the audit action/category;
no company, driver ID, query or spoofed header is copied into that denial record.
Authenticated denials and identity/cross-company reads are audited in the
application handlers. Telemetry scope denial is the same generic `404` as an
unavailable resource; driver scope denial is `403` and missing identity is `404`.
An unconfirmed audit yields `503 AUDIT_UNAVAILABLE`; other backend failures yield
generic `500 INTERNAL_ERROR`. No provider exception, SQL, token or partial result
is substituted into an error body. Caller cancellation propagates and a response
whose transfer already started is not replaced with Problem JSON.

Program maps the routes and registers presentation handlers alongside configured
control persistence. Deploy with `ConnectionStrings:TelemetryControl`, operator
profiles, durable audit permissions and `InfluxDb:Queryable:{Url,Database,Token}`;
identity enrichment is optional. Missing dependencies fail closed. No runtime
database migration or Grafana provisioning is performed by these routes.

Evidence: 79 original T064/T065 HTTP failures were reproduced before mapping the
routes; the original 110 authorization/query controls pass afterward. Additional
HTTP cases cover challenge/audit failures, duplicate/empty/unknown query keys,
explicit UTC parsing, populated PostgreSQL health JSON and pagination over real
historical Influx Parquet data. These tests exercise the production routes and
handlers. Grafana provisioning/rendering remains T071-T074; no capacity claim is
made. Reports are ignored under `artifacts/validation/T070/`.
