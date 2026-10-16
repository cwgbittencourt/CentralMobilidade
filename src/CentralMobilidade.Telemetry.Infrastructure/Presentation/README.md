# Presentation queries (T069)

`AddTelemetryPresentation(services, configuration)` registers the four application
handlers and closed query ports. The API composes these services when its control
database is configured, alongside durable audit persistence. Authentication,
`IAuthenticationCredentialStore`, `IOperatorAuthorizer` and `IAuditWriter` must be
provided by the host. Custom query ports and the optional driver resolver can be
registered first; registration preserves them. T070 maps the HTTP routes through
`Api/Endpoints/V1/Presentation/PresentationEndpoints.cs`.

Every operation checks the enabled server-side profile and its independent
`telemetry.read` or `driver.read` permission. A company absent from the allowlist
requires an explicitly authorized human with `cross_company.read`; viewer service
credentials cannot elevate their scope. Authorization is checked again after IO.
Company and vehicle IDs need no cadastral registration. Unauthorized telemetry
and unknown data do not reveal company existence; identity scope denial follows
the 403 contract. Invalid query bounds fail before backend IO.

Limits come from `Telemetry:Viewer`: latest defaults to 500 vehicles, trip to
24 hours / 10,000 points, health to seven days / minimum 60-second buckets /
10,000 points. Configured limits may tighten these ceilings. Windows are UTC,
inclusive start / exclusive end. Latest uses one-row lookahead with an opaque
base64url vehicle continuation; the cursor grants no access and every page is
authorized independently. Rows are ordered by UTF-8 vehicle ID; trips by event
time before applying the limit. Handlers reject out-of-scope or malformed adapter
results instead of disclosing a partial response.

The Influx reader uses `InfluxDb:Queryable:{Url,Database,Token}`, fixed projection
and fixed table names. Company, vehicle, cursor and time values are HTTP query
parameters; only validated integer limits enter SQL text because Influx parameters
are supported in WHERE clauses. See the official
[parameterized-query documentation](https://docs.influxdata.com/influxdb3/core/query-data/sql/parameterized-queries/).
Schema discovery projects absent optional speed/ignition/driver columns as null;
it never fetches plate, CPF or raw data. HTTP responses are bounded to 8 MiB and
JSON depth eight, with a ten-second deadline, no redirects/cookies/decompression
or default request logging. Provider failures become a generic error without
provider bodies, SQL or credentials. Caller cancellation propagates.

The latest query scans retained telemetry without an artificial recent-time
cutoff. Influx Core's file-scan limit can therefore reject large retained scans;
the adapter returns a safe failure rather than silently omitting older vehicles.
This implementation does not establish capacity or long-retention approval.

Health reads `line_processing_results` in PostgreSQL and groups by `OccurredAt`,
the ingestion receipt time recorded by processing, rather than vehicle event
time. It counts Accepted and Rejected separately, excludes IdenticalReplay and
unattributable null-company rows, and returns sparse buckets (no invented zeros).
The query is parameterized, bounded and cancellable. PostgreSQL microsecond
precision is handled explicitly so a one-tick half-open window remains valid.
This series describes persisted line outcomes; it does not measure queue backlog
or unprocessed lines. Existing operational retention covers the seven-day window.

Driver lookup returns only the matching driver ID, bounded display name and an
optional four-digit CPF suffix. Missing/invalid/unavailable enrichment is audited
as Missing. ResolveDriver Allowed/Missing and any human cross-company read require
a matching durable receipt before disclosure; identity access is checked again
after the audit commit. Failed audit/invalid receipt blocks the result. Audit
metadata contains the trusted actor/organization, trace, company, action and
outcome, without driver name, CPF, coordinates or driver resource identifiers.

Validation is in `UnitTests/Presentation` and
`IntegrationTests/Queries/PresentationReaderTests.cs`: permission separation,
revocation, audit barrier/failures, bounds, cursor, query injection and malformed
provider controls; real Influx 3.11.6 historical Parquet/optional-column queries;
real PostgreSQL buckets and DI-composed identity disclosure using the durable
writer. T070 now maps the production routes and resolves the T064/T065 HTTP cases.
