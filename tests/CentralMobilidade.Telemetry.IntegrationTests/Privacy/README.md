# Presentation authorization and audit (T064)

## Automated observability privacy gate (T079)

`ObservabilityLeakTests` exercises the production `AddPrivacyLogging` and
`AddTelemetryObservability` registrations for both API and worker. Synthetic plate,
latitude, longitude, accented driver name, CPF (digits and formatted), and opaque
bearer token are injected into request path/query/body/headers, logger category,
scope/properties/exception, metric tags and span tags/baggage/name/status.

The scanner checks structured application logs, the real Prometheus scrape
(including labels), HTTP/protobuf OTLP export bytes, and real Loki query JSON plus
decoded lines. Nonempty captures, a measured canary counter, preserved batch ID,
operation/service and exact Loki line round-trip prevent empty-output passes.
Twenty-eight scanner controls cover seven seeds in literal, JSON escaped, URL
encoded and base64 forms on every surface; another case rejects empty captures.
A real Loki negative control stores each seed deliberately and requires detection.
Scanner errors identify only category/surface, without echoing captured content.
Together with the two service roles these are 32 executable cases, with no skips.

Run from the repository root with Docker running:

```powershell
dotnet test tests/CentralMobilidade.Telemetry.IntegrationTests --filter 'FullyQualifiedName~ObservabilityLeakTests|FullyQualifiedName~PrivacyCanaryScannerTests'
```

One disposable `grafana/loki:3.7.0` container uses the current repository `loki-config.yaml`,
ephemeral data and a random host port. The test uses root only to make that
disposable filesystem writable; it does not test production filesystem hardening.
Testcontainers owns cleanup. No existing deployment or volume is modified. The
test explicitly pushes the production logger's output through Loki's HTTP API;
native pushes use `service_name` and one synthetic run-isolation label, within the
T083 count budget. This negative-control API intentionally bypasses OTLP attribute
filtering, allowing the scanner to prove detection of deliberately unsafe bodies.
It does not verify a deployed log shipper, Prometheus server configuration, Loki
retention or alerts (separate tasks). The worker uses its production metrics
listener; its HTTP probe is a test stimulus, not a production worker endpoint.
The loopback collector receives actual OTLP HTTP/protobuf; string values are
scanned in its UTF-8 byte representation without a separate tracing backend.
This gate covers the seeded scenarios and four listed encodings, not arbitrary
transformations, all application workflows or authorized presentation responses.
Ignored evidence is under `artifacts/validation/T079/`.

`PresentationAuthorizationTests` contains 100 executable cases. The existing security
foundation is exercised separately from the future production presentation API. No
tests are skipped, and no presentation handler or data endpoint is implemented here.

## Existing production security

Twenty-five cases exercise real bearer authentication, StoredOperatorAuthorizer,
authorization middleware, and PostgresAuditWriter against a migrated PostgreSQL
database. One class fixture owns one Testcontainers PostgreSQL instance; each audit
assertion uses its unique event ID and a fresh context to verify committed data.

The `/_policy/...` routes are explicit middleware probes returning a sentinel. They
prove permission/scope decisions and that denied requests never execute the resource;
they do not prove telemetry query results or audited successful identity disclosure.

Coverage includes humans and organization-scoped service accounts, every requested
company in scope, explicit human cross-company permission, refusal of cross-company
privileges in a malicious service profile, independent resource permissions, and
matching organization attribution. Spoofed actor, organization and company headers
cannot replace the server-side credential/profile. Producer, invalid and missing
credentials receive a generic Bearer challenge and an unidentified denial audit.

Audit probes wrap the real durable writer. Denials are checked in PostgreSQL with
trusted actor/service-account/organization, action, outcome, scope and trace. Failure,
wrong event receipt, absent commit time and non-UTC receipt fail closed with sanitized
503 AUDIT_UNAVAILABLE. A controlled barrier proves the response stays pending and no
audit row is visible until the writer is released and its real transaction commits.

## Audit persistence implemented in T068

T068 validates identity/cross-company metadata, attribution and scope in the real
writer, redacts diagnostic record formatting and preserves durable append-only
storage. Dedicated contract/PostgreSQL tests are described in
[the audit policy](../../../src/CentralMobilidade.Telemetry.Infrastructure/Auditing/README.md).
Successful disclosure still needs the actual T069/T070 handlers/routes; this does
not make the production presentation cases below pass.

## Active TDD cases for presentation handlers/routes (T069–T070)

Seventy-five cases target production presentation registration and routes. They are
tagged `PendingImplementation` and currently fail with the explicit missing
`AddTelemetryPresentation` assertion. They remain part of the default integration
suite. Missing registration is an intended failure, not evidence that later endpoint
or backend assertions have executed successfully.

The routes follow the existing OpenAPI contract:

- `/api/v1/presentation/telemetry/latest`
- `/api/v1/presentation/telemetry/trips/{vehicleId}`
- `/api/v1/presentation/telemetry/ingestion-health`
- `/api/v1/presentation/drivers/{driverId}`

The test composition locates public static production methods
`AddTelemetryPresentation(IServiceCollection, IConfiguration)` and
`MapPresentationEndpoints(IEndpointRouteBuilder)` in the API/Application/Infrastructure
assemblies. If T069/T070 choose another composition shape, update this fixture to
call that production wiring while retaining the behavior assertions. Never map fake
presentation routes or substitute production handlers to obtain passing tests.

The harness supplies the real control-plane database/audit writer, security and
TelemetryOptions; a company-aware IDriverResolver returns a synthetic minimal identity.
HttpClient/IHttpClientFactory provide a controlled empty Influx JSON query response.
Adapt dependency registration to the eventual production query port/HTTP client as
needed. This suite verifies authorization, validation and disclosure ordering; real
Influx event-time queries/Core query envelopes remain T065/T069. It makes no claim
about registry-independent disk persistence or populated presentation query results.

Production assertions cover producer rejection on all four routes, unauthorized
company non-disclosure with no resolver/storage call, allowed scoped human/service
queries without a company registry, and query defaults. Telemetry scope denials use
404; driver scope denial follows the OpenAPI 403 contract. No company/identity data
or private diagnostics may appear in error responses.

Numeric cases cover lower and inclusive upper limits, one beyond each ceiling,
integer overflow and fractional limits, all three point/vehicle limits, health
granularity below/at/above 60 seconds, nonpositive and minimally positive windows,
and exact/one-tick-over 24-hour and seven-day windows. All six configurable viewer
limits are tested at a tighter configured boundary and just outside it. Invalid
requests must return 400 REQUEST_INVALID before backend IO.

Driver success must return only driverId, displayName and CPF suffix, with no-store,
the correct company/driver resolver arguments, and a committed identity audit with
the trusted human or service-account/organization attribution. Cross-company human
disclosure also requires its own ReadAcrossCompanies audit. The same failure/receipt
matrix and a commit barrier prohibit successful disclosure before durable audit.
Audit rows and application logs are scanned for synthetic identity, CPF, plate,
coordinates, token, raw-content and private-diagnostic canaries. Framework request
logs are filtered at Warning, matching the intended privacy-safe test surface.

Ignored validation evidence is stored under `artifacts/validation/T064/`. This is a
test-only task; T067 still owns optional enrichment, T068 audit policy, T069 handlers,
and T070 HTTP composition/validation. Capacity approval remains a separate gate.

T069 now supplies the production registration and application handlers, tested
directly in UnitTests/Presentation and IntegrationTests/Queries. The T064 HTTP
assertions remain active: their missing seam is `MapPresentationEndpoints`, owned
by T070. No sentinel route replaces the production presentation endpoints.

T070 completes that mapping: all original 75 production HTTP cases now pass and
their obsolete PendingImplementation traits were removed. Additional production
route cases cover audited/no-store challenges, missing/duplicate/empty/unsupported
parameters, malformed/non-UTC dates, SQL rejection, audit failure on producer
denial and populated company-scoped health JSON from real PostgreSQL rows.
