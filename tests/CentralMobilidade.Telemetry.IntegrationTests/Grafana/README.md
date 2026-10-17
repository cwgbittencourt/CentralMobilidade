# Grafana provisioning and rendering tests (T066)

The suite adds 24 executable cases, without skips or a substitute production
reconciler. Six cases exercise existing image/runtime behavior. Eighteen active
TDD cases await T071–T074: missing YAML, missing production PowerShell script, and
the seven missing production dashboard files are explicit assertion failures.

## Existing image and real runtime

The static control verifies the repository's pinned Grafana 12.2.5 / signed
Infinity 4.1.1 Docker build, plugin path outside the persistent Grafana directory,
disabled runtime plugin preinstallation and disabled anonymous access.

Five runtime cases build the actual `deploy/grafana/Dockerfile` with Testcontainers
and run a disposable Grafana container on a random port. They check actual Grafana
and plugin versions/signature, successful custom Infinity backend health against
Grafana's own `/api/health`, and detection of a real `/api/...` 404. A newly created
organization-local Admin service account can access its own organization but
cannot call the Basic-only Admin Organizations API. A presentation bearer stored
in secureJsonData is omitted from exported datasource JSON while secureJsonFields
reports its presence.

On this pinned runtime, a datasource plugin can report `enabled=false` in plugin
settings and still run correctly. That app-plugin flag is not used as a datasource
availability assertion. Backend health and signed metadata are tested instead.
The health check tests status handling, not a successful production dashboard query.

The fixture builds a uniquely tagged test image, creates no Compose volume, uses
synthetic per-run credentials and disposes its own container/image. It never
reconfigures an existing Grafana. Plugin acquisition happens in the real Docker
build, not through an unsigned plugin bypass or runtime installer.

## Active provisioning tests

The internal YAML test loads the real provisioning directory into a real Grafana,
requires only the default organization, read-only InfluxDB/Prometheus/Loki sources,
no viewer Infinity source, and exactly the internal dashboard UIDs. Internal
dashboards are mapped to `/var/lib/grafana/dashboards/internal`. When production
YAML chooses another mount or secret wiring, adapt fixture wiring to that layout;
do not replace the production YAML with a test YAML to obtain passing results.

Ten reconciler cases execute the future production
`scripts/Provision-GrafanaOrganizations.ps1` against an isolated Kestrel API double.
They currently fail before starting it because that production script is absent.
The API double supplies organizations, memberships, scoped service accounts,
rotatable/revocable tokens, datasources and dashboards, and captures requests.
It is a controlled protocol test, not evidence of real script reconciliation on
Grafana until that implementation exists and the runtime fixture is connected.

Assertions require two runs to converge without duplicate resources/memberships
or accumulated active tokens, Basic authentication on Admin Organizations calls,
organization-local service tokens for datasource/dashboard calls, and a third
distinct presentation bearer only in each Infinity secureJsonData. Viewer sources
must be Infinity only and point to the corresponding configuration UID. Bootstrap
passwords and Grafana service tokens cannot become datasource secrets; presentation
tokens cannot authenticate Grafana calls. Child output is scanned for secret
canaries. Wrong version, missing/unsigned plugin, health failure, reused bootstrap
or cross-organization presentation credential, bootstrap rejection and mismatching
organization fail closed without datasource disclosure/mutation.

### T071 invocation seam

The harness invokes PowerShell 7 (`pwsh`, or `TELEMETRY_TEST_POWERSHELL`) with:

```text
-NoProfile -NonInteractive -File <production-script>
-GrafanaUrl <loopback-double> -ConfigurationPath <private-json>
-StateDirectory <private-temp-state>
```

No secret is passed on the command line. The JSON contains `bootstrap.usernameEnv`
and `bootstrap.passwordEnv`, a two-second readiness budget, and two organizations.
Each organization defines name, companyIds, Viewer members, an organization-local
serviceAccount name/role, presentation baseUrl/credentialEnv/permissions,
datasourceUid and dashboardDirectory. Environment references supply synthetic
credentials. The tiny temporary fixture dashboard isolates T071 reconciliation
from T072–T074 and is never claimed as production rendering evidence. State and
configuration are removed with the uniquely created temporary directory.

If the production script uses another public configuration shape or API order,
adapt the harness/double to the real contract while retaining credential,
authorization, readiness, rotation and idempotency assertions. The controlled
double is intentionally not a Grafana server implementation.

## Seven active production rendering cases

The rendering matrix names all three internal and all four viewer dashboards.
It recursively checks every non-row panel, unique IDs and real query targets.
The cases currently fail at the missing production dashboard file, before any
render or frame assertion executes. No PNG or visual success is claimed now.

To execute the later rendering stage, connect the isolated fully provisioned
T071/T069–T074 stack through private `TELEMETRY_TEST_GRAFANA_RENDER_STATE` JSON.
It has separate `internal` and `viewer` objects with grafanaUrl, orgId,
serviceToken and variables (including from/to and all target placeholders).
The viewer orgId must exceed one and its actual datasource inventory must contain
only Infinity; the internal inventory must contain InfluxDB. This state must
come from the isolated acceptance fixture, never an ordinary production account.
The stack needs a configured real Grafana rendering service and seeded query
data; this task does not add a renderer to production Compose or pretend that
Grafana's ordinary health endpoint renders panels. Adapt this connection to the
eventual automated stack fixture rather than skipping tests when it is absent.

Tests interpolate supplied fixture variables into the production query models,
require nonempty query frames without provider errors, and request every panel
through real `/render/d-solo/...`. HTTP success, PNG content type/signature and
nontrivial bytes are required; images go only to ignored
`artifacts/validation/T066/render/`. These checks are stronger than importing
JSON or obtaining an HTML shell, but do not replace visual review, a data-to-pixel
oracle, or T101/T102's full 100,000-event acceptance proof.

## References and evidence

Official documentation checked on 2026-10-15:

- [Grafana Organizations API](https://grafana.com/docs/grafana/latest/developer-resources/api-reference/http-api/api-legacy/org/)
- [Grafana service accounts API](https://grafana.com/docs/grafana/latest/developer-resources/api-reference/http-api/api-legacy/serviceaccount/)
- [Infinity configuration and custom health](https://grafana.com/docs/plugins/yesoreyeram-infinity-datasource/latest/configure/)
- [Image rendering service](https://grafana.com/docs/grafana/latest/setup-grafana/image-rendering/)

Runtime tests validate the pinned versions independently of current documentation.
Ignored logs/TRX are under `artifacts/validation/T066/`. No production script,
dashboard, YAML, token rotation or user membership is implemented by this test task.

Final functional integration regression (separate ADR measurement probe excluded):
538 passed, 97 expected failures (75 T064, four T065, 18 T066), 635 total,
zero skips and no unexpected failures. Unit 244/244 and architecture 47/47 passed;
contracts retained 86 passes and the 16 existing T067 failures. Build and whitespace
verification passed without warnings/errors. No capacity benchmark was run.

## T071 implementation evidence

The production internal YAML/provider, Compose read-only mounts, secret-file image
wrapper and `Provision-GrafanaOrganizations.ps1` are implemented. The original ten
reconciler cases pass and no longer carry PendingImplementation. The harness now
keeps token attribution history separately from active tokens: a rotated/revoked
token must still be attributable in old captured requests. This preserves the
original reuse-or-revoke requirement while checking active-token convergence.

Eight additional protocol cases verify managed-member removal and refusal of
privileged presentation/member permissions, insufficient service-account role,
empty company scope, unsafe viewer SQL dashboards, internal-org viewer membership
and contamination by a direct Influx datasource. Two real-container tests load
the production YAML and execute the production script twice against pinned Grafana,
proving scoped resource convergence, actual token replacement, datasource-secret
non-export and authenticated Infinity backend health against a synthetic local API.
These are not business-dashboard rendering or API company-scope claims.

The combined original internal-loading test now reaches the absent three business
dashboards; its pending dependency is T072-T074. It and the seven production
panel/frame/render cases remain active. No dummy business dashboards were added.
Configuration/runtime constraints are documented in `deploy/grafana/README.md`;
reports are ignored under `artifacts/validation/T071/`.

## T072 latest-position query/render fixture

The two latest-position rendering cases now start `LatestDashboardFixture` lazily,
without requiring a manually supplied render-state file. It creates a unique
Docker network, pinned InfluxDB 3.11.6, migrated PostgreSQL, production T069/T070
API composition on Kestrel, pinned Grafana 12.2.5/Infinity 4.1.1 and a test-only
`grafana/grafana-image-renderer:v4.0.17`. It exposes the API host port before creating
Grafana, imports internal JSON through the production YAML and viewer JSON through
the production T071 reconciler, and queries/renders with org-local Viewer accounts.
No test datasource data or substitute dashboard query handler replaces production.

The dataset has unordered 28-day-old points, two vehicles and another company.
Semantic assertions prove the latest event is chosen before the 500-row limit,
old last-known positions remain visible, company separation holds, viewer limit
one yields one row, limit 501 is rejected by the API, and the viewer cannot discover
the internal datasource. Denial checks inspect the actual upstream 404/400 codes
and empty frame data, allowing Infinity's diagnostic-only empty frame.

Every latest-position panel must produce real frames before requesting its real
PNG. Images go under ignored `artifacts/validation/T072/render/` and receive visual
review in addition to PNG signature/size checks. This establishes the two latest
views, not capacity/retention approval or the later trip/health/driver views.
The remaining five rendering cases still require their production JSON/fixture
wiring; the combined internal loading case still expects all three internal files.
Their pending dependency is T073-T074. No case is skipped or replaced with a mock.

The fixture exposed two real provider requirements: Influx SQL targets use
`rawSql`/`format: table`, and Infinity base URLs belong to the datasource's top-level
`url`. Custom absolute-URL health alone had not tested relative query resolution.
Both production models/provisioning were corrected and covered through real queries.

## T073 trip query/render evidence

The two original trip rendering cases now use the same lazy isolated fixture,
with a historical range and one vehicle. They no longer require private external
render state or carry PendingImplementation. Production global time variables are
interpolated for direct `/api/ds/query` assertions; real browser rendering also
tests Grafana's own interpolation. Both PNGs were visually reviewed and display
the same two historical points in ascending event-time order.

Fifteen additional semantic cases cover internal/viewer historical ordering,
company/vehicle separation, inclusive start/exclusive end, exactly 24 hours,
empty/reversed/over-24-hour ranges, viewer point limits and unauthorized company.
Invalid internal windows return no points; viewer failures must retain the real
upstream 400/404 and no business data. The allowlisted field assertion also
prevents identity columns from appearing. Point-limit one must return the earliest
event, despite later arrival order. The fixture imports the actual new JSON files
through production YAML/reconciliation and uses Viewer-role Grafana credentials.

Ignored reports/PNGs are under `artifacts/validation/T073/`. Four original cases
remain pending T074: combined three-internal-dashboard loading and the three
health/driver render cases. No case is skipped. Historical T066/T071/T072 evidence
above describes those stages, not the current number of missing dashboards.

## T074 health/driver query and render fixture

All three remaining production render cases now use the lazy isolated fixture;
manual render-state wiring and PendingImplementation traits have been removed.
The original internal YAML test loads all three internal production dashboards.
No existing datasource, API route or authorization handler is replaced for these
tests. An external cadastral resolver supplies only a synthetic display name and
four-digit suffix; authorization, projection and audit persistence use production
code and real PostgreSQL. This is not proof of a deployment's cadastral service.

Sixteen new cases cover native Influx per-company stored counts, API Accepted/
Rejected processing-time counts, excluded replay/other-company/end-boundary rows,
empty/reversed/over-seven-day ranges, viewer granularity/point limits and forbidden
company selection. Driver cases inspect exactly the two projected frame fields,
the committed service/organization/company audit, missing identity and a real
database trigger refusing audit inserts. That trigger is scoped to the unique test
database and removed in finally; the resulting upstream 503 must carry no identity.

Every production panel produces real query frames and a PNG. Driver rendering
additionally requires a new successful audit after the preflight query: an error
PNG cannot satisfy the test. Visual review caught an initially blank driver render
caused by skipUrlSync ignoring its URL variable, despite a successful preflight.
Variable synchronization was corrected and the render assertion strengthened.
Images/reports are private under ignored `artifacts/validation/T074/`. Historical
pending-count statements above describe their original task stages; T074 closes
the four original remaining Grafana failures. Production Compose gains no renderer,
new datasource, company metric labels or identity-bearing telemetry.

## Platform operations (T084)

The platform dashboard is a separate read-only file provider for internal org 1.
`PlatformOperationsTests` uses real pinned Grafana/Prometheus/renderer, production
application instruments and explicitly synthetic infrastructure gauges. It checks
all PromQL targets, missing-data behavior, filesystem arithmetic, seconds-based
histogram quantiles and rendered panels/overview. Its exclusive metrics collection
prevents interference from process-global SDK instruments created by other hosts.
The Grafana helper waits for datasource/dashboard provisioning beyond HTTP health.
See [panel semantics](../../../deploy/grafana/dashboards/platform-operations.md).
Raw images and reports remain ignored under `artifacts/validation/T084/`.
