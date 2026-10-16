# Grafana access operations — T075

This runbook covers the implemented Grafana OSS 12.2.5 / signed Infinity 4.1.1
deployment and presentation API. Use it together with the [provisioning contract](../../deploy/grafana/README.md),
[dashboard behavior](../../deploy/grafana/dashboards/README.md) and
[API credential configuration](../../src/CentralMobilidade.Telemetry.Api/Security/README.md).
Commands below assume the repository root. Secret values must already be supplied
by a protected secret provider; no example contains a usable credential.

## Credential boundaries and storage

| Credential | Purpose and minimum privilege in this implementation | Storage and lifetime |
| --- | --- | --- |
| `GrafanaBootstrapAdminCredential` | Grafana server administrator username/password using Basic authentication for server organization/membership operations and bootstrap of dedicated organization accounts/tokens. Never sent to the presentation API or stored in a datasource. | Operator secret store, injected into the reconciler's named process environment only when needed. Initial Compose password uses `GF_SECURITY_ADMIN_PASSWORD__FILE`. Rotate the actual Grafana account separately. |
| `GrafanaOrganizationServiceAccount` | One dedicated account per viewer organization, role `Admin` within that organization. This pinned OSS workflow requires org Admin for datasource management; it is not a server administrator. Its Bearer token performs org-local datasource/dashboard reconciliation. | Grafana stores the account/token records. The script keeps the newly issued plaintext token in process memory only, with a one-hour TTL; it never writes it to its state directory. Do not share the managed account with other tools. |
| `ViewerPresentationServiceCredential` | Distinct API Bearer credential per viewer organization. Only `telemetry.read`, plus `driver.read` when identity lookup is intended, and exactly that organization's permitted companies. No ingestion, raw quarantine, batch administration or `cross_company.read`. | Plaintext in the operator secret store and Grafana Infinity `secureJsonData.bearerToken`; API stores its typed hash record under `Authentication:ViewerServices`. Explicit expiry/revocation is configured independently of Grafana token TTL. |

Use different secret values for all three roles and for every viewer organization.
Bootstrap credentials and Grafana tokens are not API credentials. The internal
`INFLUXDB_GRAFANA_READ_TOKEN` is a fourth, read-only telemetry token, separate from
Influx admin/worker tokens; it belongs only to internal organization 1 provisioning.
Never copy a direct source or privileged internal membership into a viewer org.

Protect secret sources, private config, state, database/volume backups and Grafana's
encryption configuration. `secureJsonData` suppresses ordinary secret export; it
does not make a Grafana database backup public data. Git ignore rules are not file
permissions. Keep private configuration under restricted ignored `data/`, outside
shared folders; keep credentials out of shell history, command arguments, transcripts,
HTTP dumps, screenshots, exported dashboards and support tickets. Run reconciliation
in a dedicated process and release its secret environment when it exits. Use HTTPS
outside the trusted local development network; Compose publishes Grafana on loopback.

## Provision an organization and its company scope

1. Verify the pinned image, healthy API/control database and internal-only YAML
   sources in organization 1. Supply the internal read token privately. Existing
   persistent volumes and catalog identities must be preserved.
2. Copy [the secret-free organization example](../../deploy/grafana/viewer-organizations.example.json)
   to `data/grafana-viewers.json`. Set the intended organization name, existing
   Viewer user logins, exact company IDs, dedicated Admin service-account name,
   datasource UID, API base URL and secret environment reference. Dashboard paths
   resolve relative to this config file. The example path works directly under `data/`.
3. Resolve the actual Grafana organization ID with authorized administration.
   For a new organization, create/identify it through the Admin Organizations API
   first if necessary, then configure the API before exposing members. Never guess
   the next ID or repoint an existing credential at a different organization.
4. Generate a typed viewer token with `OpaqueBearerTokens.Generate` and its
   `BearerTokenHash.Create` record through trusted provisioning as described in
   the API security guide. Set a unique `CredentialId`, stable service `ActorId`,
   hash, enabled state/expiry and matching `GrafanaOrganizationId` under
   `Authentication:ViewerServices`. Set that actor's enabled profile under
   `Authentication:Profiles`, kind `ViewerPresentationService`, the same organization,
   exact `CompanyIds` and minimal `Permissions`. Store/deliver plaintext privately.
   No interactive token-generation CLI is provided by this runbook.
5. Load/restart the API according to its configuration provider. Preflight a scoped
   API request with the intended credential and a known forbidden company using
   trusted tooling that keeps headers and identity responses private. Config-file
   `companyIds` and `permissions` describe intent; the reconciler neither creates
   API credentials nor checks equality with the API profile.
6. Inject the environment secrets named by the private config, then run:

```powershell
./scripts/Provision-GrafanaOrganizations.ps1 -GrafanaUrl http://localhost:3000 `
  -ConfigurationPath ./data/grafana-viewers.json -StateDirectory ./data/grafana-reconciler
```

7. Run the same command a second time. Expect exit zero and
   `Grafana organizations reconciled.` Verify stable org IDs, datasource/dashboard
   UIDs and memberships; token rotation on each run is intentional. Confirm the
   actual viewer inventory contains only its expected Infinity datasource. Test
   real dashboard data, a forbidden company, and driver lookup if enabled. A healthy
   datasource alone proves connectivity, not company scope or identity authorization.

Members must exist and must not belong to internal organization 1. All members of
a viewer organization share its service credential's complete scope; use separate
organizations when audiences must see different company sets. Dashboard filters,
hidden variables and folder permissions cannot replace API company checks.

To **add** a company, approve the audience, update its API profile and matching
private organization config, reload/restart and reconcile; verify allowed/forbidden
reads afterward. To **remove** access, reduce/disable the API profile first and
verify denial, then reconcile the descriptive config. Remove omitted managed
members through reconciliation; separately review unmanaged memberships, which the
script deliberately preserves. A Grafana-only scope edit grants or revokes no API access.

## Rotate and revoke independently

**Bootstrap account.** Rotate the actual Grafana account password using authenticated
account administration, update its secret-store entry and reconciler environment,
then verify a successful reconciliation and rejection of the old password through
private tooling. Grafana's initial `admin_password` is set on first run: changing
only the Compose secret does not rotate an account already in the persistent database.
Keep a controlled recovery administrator; never delete the volume to reset access.
Coordinate the change with other authorized uses of that account. On compromise,
revoke the account's access using trusted server administration and review its changes.

**Organization account.** Each successful reconciler run creates a uniquely named
one-hour token, verifies its organization, updates the Infinity source/dashboards,
then revokes all older tokens on that managed account. The new token survives until
expiry or the next run; its expiry does not stop viewer queries, which use the separate
API credential. On failure, the script attempts to revoke the new token; interrupted
processes or cleanup failures can leave it until TTL. Retry and inspect active tokens.
For an incident, revoke affected tokens/disable the account via trusted administration;
restore/recreate the expected enabled dedicated account before retrying. Reconciliation
refuses an existing disabled or wrong-role account rather than silently enabling it.

**Presentation credential.** Create a new credential ID/hash for the same actor/org
and minimal profile, optionally with a short controlled overlap. Load it into the
API, replace only that organization's secret-store plaintext/environment reference,
then reconcile Infinity and verify real scoped queries. Disable/expire the old API
credential and confirm old-token authentication fails. Merely rotating the Grafana
organization token or deleting the secret-store copy does not revoke an API token.
Environment configuration needs an API restart; supported file/provider change
notifications can reload profiles/hashes. For urgent compromise, disable the affected
profile or credential first, accept the temporary dashboard outage, and replace it.
Do not grant broader companies/permissions to recover a failed rotation.

Record UTC change time, administrator, organization ID, service actor, old/new
credential IDs, approved scopes and verification outcome in a protected administrative
change record, without token values/hashes or identity responses.

## Idempotency, partial failure and recovery

Keep the same dedicated state directory between runs. Owner-only ACLs and an
exclusive `reconcile.lock` protect `managed.json`, which pins the Grafana server URL,
organization IDs and managed member logins. It contains no passwords/tokens, but is
still access-control metadata. Preserve a protected backup and the original URL;
changing origin, deleting state or losing it is not a routine retry procedure.

Reconciliation validates version/signature/readiness before Grafana mutation, but
the subsequent HTTP operations are not transactional. Earlier organizations,
memberships, sources or dashboards can already have changed when a later call fails.
Retry the same desired configuration before changing membership intent. Metadata
is committed after the full run; first-run/partial membership changes can therefore
become unmanaged if state is lost or intent changes before retry. Review actual
membership/token inventory after recovery; do not infer rollback from a nonzero exit.

Stop on an unexpected datasource, org ID, internal membership or secret collision.
Investigate using protected configuration and authorized inventories; do not bypass
guards or dump request bodies to shared logs. Default failures give a script line
and generic message. Unexpected state files, links or a leftover `managed.json.tmp`
also require investigation: after confirming no active run, preserve evidence and
restore a verified state backup under an explicit recovery procedure. Do not erase
state just to make the guard pass. Whole-organization removal/renaming and global
user deletion are separate decommissioning work, not script convergence behavior.

## Company recognition and optional identity

Every valid producer-supplied `empresa_id` is recognized without company-registry
lookup. One internal producer credential covers all company IDs. Missing/invalid IDs
still fail the closed Line Protocol contract. All telemetry stays in one database;
no pending company store, promotion or per-company ingestion token is required.
Recognition does not grant presentation access: viewer company scopes remain explicit.

[Optional driver enrichment](../../src/CentralMobilidade.Telemetry.Infrastructure/Cadastral/README.md)
cannot gate ingestion, storage or authorized fleet visibility. Configure the facade
and its separate authentication only when needed. It returns a minimal name and
optional CPF suffix through the authorized driver API; full CPF/name never enters
telemetry, logs or audit payloads. Its bounded positive cache is scoped by company
and driver, invalidated on configuration changes; it does not monitor source record
changes. Source absence/failure produces unavailable identity, not an alternative lookup.

The driver panel starts with a blank ID and no automatic refresh. A lookup needs
`driver.read` and company scope; adapter cache hits still require durable audit
before API disclosure. Forbidden identity returns 403, missing/unavailable identity
404, and failed audit commit 503 without identity. Successful results are visible
to the authorized Grafana audience; the driver variable can appear in its URL.
Do not publish/shared-export identity panels or treat a displayed suffix as a full CPF.

## Audit attribution and review

Assign an audit reviewer and a deployment-specific cadence/retention policy; also
review after access changes, rotations and incidents. Use a DBA-provisioned SELECT-only
review role on `audit_events`, a bounded UTC window and a statement timeout. Do not
borrow the application writer or grant UPDATE/DELETE/TRUNCATE to make review easier.
For an authorized `psql` session with connection credentials supplied privately:

```sql
\prompt 'Inclusive UTC start: ' from_utc
\prompt 'Exclusive UTC end: ' to_utc
BEGIN READ ONLY;
SET LOCAL statement_timeout = '5s';
SELECT "EventId", "OccurredAt", "CommittedAt", "ActorId", "ActorKind",
       "GrafanaOrganizationId", "Action", "ResourceCategory", "CompanyScope",
       "Outcome", "TraceId"
FROM audit_events
WHERE "OccurredAt" >= :'from_utc'::timestamptz
  AND "OccurredAt" < :'to_utc'::timestamptz
ORDER BY "OccurredAt", "EventId"
LIMIT 200;
COMMIT;
```

Require explicit UTC offsets and a narrow positive window. Page/reduce the window
when 200 rows are returned; this sample is not a complete-export guarantee. Keep
review results restricted because technical actors, companies and access times
remain sensitive metadata. Do not append driver names, CPF, coordinates, full URLs,
raw telemetry or bearer values to audit records or incident summaries.

Review `ResolveDriver` Allowed/Missing/Denied and `ReadAcrossCompanies` events,
commit timestamps, trace correlation and unexpected denial bursts. Allowed driver
disclosure must have exactly one permitted company and a durable receipt. Viewer
cross-company success is forbidden; an Allowed cross-company event must be a
human action with the explicit permission. Verify service actors against the
protected organization/profile inventory and the scope effective at the event time.
Successful ordinary in-scope telemetry reads are not all individually audited;
this table is not a complete Grafana browsing history. Audit outages can also
prevent an attempted denial from being persisted; no row is not proof of no attempt.

The schema records the authenticated service `ActorId`/kind and trusted Grafana
organization, not a human Grafana login. It also does **not** store `CredentialId`
or a token fingerprint: overlapping rotated tokens for the same actor cannot be
distinguished from these rows alone. Correlate administrative rotation history
without claiming exact token-version attribution. The trusted API does not accept
a browser header to invent a human identity. Individual attribution requires a
future authenticated identity-propagation design. Direct internal Influx queries
likewise do not traverse the API audit path. PostgreSQL protects committed audit
rows against mutation; this is not a host-loss backup or tamper-evidence guarantee.

## Upgrade the pinned plugin/runtime

Assess release/security notes for the intended exact Grafana and Infinity versions
in staging. Back up Grafana data, its encryption configuration and protected
reconciler metadata before an upgrade; test recovery on isolated copies. Change
the Dockerfile pins, Compose image tag, script version/signature gates and matching
test expectations together. A mismatch intentionally prevents reconciliation.
Keep the signed plugin baked outside persistent `/var/lib/grafana`; never enable
unsigned plugins, runtime auto-installation or substitute `latest` to pass a guard.

Build an isolated candidate and run the existing Grafana protocol/runtime and
production query/render tests, plus presentation/privacy tests. Verify two-run
resource convergence, token revocation, secret non-export, viewer-only Infinity
inventory, actual company denial and fail-closed driver audit. Review every panel's
real data and PNG; an error PNG can still be HTTP 200. Check SQL/Infinity model and
time-variable compatibility, not just plugin health. The renderer is test-only.
Promote only after these checks pass. If rollback is needed, use the version-compatible
database/config backup; an older image is not guaranteed to read an upgraded database.
Preserve all telemetry/control volumes; plugin upgrade is not a retention/capacity gate.

## Evidence and references

T071-T074 already provide isolated reconciliation, scope, API audit, query and
visually reviewed render evidence; T074 passed 730 functional integration tests
(excluding the separate ADR measurement probe), 273 unit, 146 contract and 47
architecture tests. That is historical implementation evidence, not proof that a
particular deployment has correct secrets/scopes or passed the later load/100k gate.
T075 changes documentation only and does not rotate live credentials or reconcile
the user's running stack. The [task record](../../specs/001-telemetry-ingestion/tasks.md)
tracks validation and remaining operational work.

- [Grafana Admin Organizations API: Basic/server-admin boundary](https://grafana.com/docs/grafana/latest/developer-resources/api-reference/http-api/api-legacy/org/)
- [Grafana service accounts](https://grafana.com/docs/grafana/latest/administration/service-accounts/)
- [Initial administrator password behavior](https://grafana.com/docs/grafana/latest/setup-grafana/configure-grafana/#admin_password)
- [Grafana upgrade guidance](https://grafana.com/docs/grafana/latest/upgrade-guide/)
- [Application audit projection and durability](../../src/CentralMobilidade.Telemetry.Infrastructure/Auditing/README.md)

Current upstream docs may describe newer Grafana APIs; this runbook's commands and
guards follow the repository's pinned 12.2.5 implementation.
