# Grafana provisioning (T071)

The pinned Grafana 12.2.5 image contains signed Infinity 4.1.1. Compose mounts
`provisioning/` and `dashboards/internal/` read-only. YAML targets only organization
1, with immutable `internal-influx` (SQL/FlightSQL), `internal-prometheus` and
`internal-loki` datasource UIDs and the internal file-dashboard provider. Business
dashboard JSON for all three internal and four viewer views is supplied by T072-T074
(see [dashboard behavior](dashboards/README.md)). An empty directory creates no placeholder
panels and does not establish rendering or query success.

Supply `INFLUXDB_GRAFANA_READ_TOKEN` privately as a read-only token for the telemetry
database, separate from the offline Influx admin token and worker write token.
Compose mounts it as a secret; the image wrapper reads the configured secret file
into the provisioning environment before invoking Grafana's original entrypoint.
Missing files fail startup with a generic message. The token is never baked into
the image/YAML or printed. Existing data volumes and catalog identities remain
unchanged. Set secrets before starting Compose; reconciliation does not start it.

For scope provisioning, separate credential rotation/revocation, recovery, plugin
upgrades and audit review, follow the [access operations runbook](../../docs/operations/grafana-access.md).

## Viewer reconciliation

Copy `viewer-organizations.example.json` into ignored `data/` and edit it privately.
Resolve dashboard paths relative to that configuration file. The shown path works
when the private file is directly under `data/`. Populate the viewer directory
with the T072-T074 templates before running: the script rejects missing/empty
dashboard directories rather than reporting finished dashboards. Viewer templates
may use `${VIEWER_DATASOURCE_UID}`, which is replaced for each organization, and
`${VIEWER_COMPANY_ID}` as an entire JSON string value for the first permitted
company's initial selection. Company replacement is JSON-escaped, not executable
template text. Infinity's base URL is the datasource's top-level `url`, separate
from its bearer/allowlist settings in `jsonData`.

Run from PowerShell 7.4+ with secret environment variables already supplied by the
operator's secret source (no secret arguments):

```powershell
./scripts/Provision-GrafanaOrganizations.ps1 -GrafanaUrl http://localhost:3000 `
  -ConfigurationPath ./data/grafana-viewers.json -StateDirectory ./data/grafana-reconciler
```

The bootstrap identity must be a Grafana server administrator using Basic auth.
It reconciles organizations and existing-user memberships and bootstraps dedicated
organization-local service accounts/tokens. Datasource and dashboard operations
use the corresponding organization token, never Basic or presentation credentials.
OSS requires organization Admin to manage datasources; it does not grant Grafana
server administration. Do not share these dedicated accounts with other tools:
each successful run rotates a uniquely named one-hour token and revokes all older
tokens on that managed account. Failure revokes the newly issued token; interruption
can leave it until expiry, and retry cleans older tokens. Tokens are not persisted
or printed. Readiness/version/signature mismatches stop before Grafana mutation.

Each organization must use a distinct secret-sourced presentation credential,
different from bootstrap and all Grafana tokens. Pre-provision the corresponding
hashed `ViewerPresentationServiceCredential` and enabled API profile with exactly
the stated company IDs, only telemetry.read/optional driver.read, and that Grafana
organization ID (see API/Security documentation). The script neither creates API
credentials nor grants company access. On a new install, coordinate the intended
organization ID with API provisioning; do not treat the descriptive companyIds in
this file as a server-side authorization grant. The API enforces scope on every
request; Grafana configuration and dashboard variables do not enforce tenant scope.

The Infinity source uses proxy bearer authentication, a fixed base URL/allowed
host and custom `/health/ready` check. The fixed configured allowlist does not allow
arbitrary destinations. The health check confirms HTTP success/connectivity; it
does not prove a credential's company scope. T070's API authorization tests provide
that separate control. Viewer organizations containing an unexpected datasource
are rejected; the script never deletes or converts an internal datasource.

Members must already exist, be Viewer users and have no membership in internal
organization 1. The script fails on internal membership rather than silently
removing privileged access. It adds/updates configured members and removes only
previously managed members omitted from the next configuration. Unmanaged members
are preserved. The protected state directory holds an exclusive lock and metadata
(server, managed organization IDs and member logins), never tokens/passwords.
Keep it between runs: it pins organization identity and records membership ownership.
It must be dedicated to this script, not a shared folder; ACLs restrict it to the
current Windows user or Unix owner. Whole-organization removal/renaming is refused
and requires a separate decommissioning operation. No organizations or global users
are deleted. After a partial failure, retry the same configuration before changing
desired membership; HTTP reconciliation is not a distributed transaction.

Default diagnostics expose only a script line number and generic failure. Redirects,
cookies and environment proxies are disabled; requests have bounded timeout and
response size. Environment secrets, API bodies and server exception messages are
not printed. Run against your intended private Grafana origin with HTTPS outside
the local Compose network. Do not pass credentials in a URL.

## Evidence

The ten original controlled-protocol tests cover idempotency, credential separation,
version/signature/health refusal and organization mismatch. Additional tests cover
managed-member removal and the real image: internal YAML loading, two reconciliations,
actual token replacement, non-export of datasource secrets and an authenticated
Infinity backend health request. The real health target is a synthetic local API,
not a claim of company authorization, dashboard rendering or Influx query capacity.
No existing user's stack is changed by tests. T072-T074 production query/render
tests now use the isolated storage/API/Grafana fixture; see the
[dashboard evidence and limits](dashboards/README.md).

Official references (pinned behavior is independently tested):

- [Organization API and Basic-only administration](https://grafana.com/docs/grafana/latest/developer-resources/api-reference/http-api/api-legacy/org/)
- [Service account API](https://grafana.com/docs/grafana/latest/developer-resources/api-reference/http-api/api-legacy/serviceaccount/)
- [File provisioning](https://grafana.com/docs/grafana/latest/administration/provisioning/)
- [Infinity authentication, allowlist and custom health](https://grafana.com/docs/plugins/yesoreyeram-infinity-datasource/latest/configure/)
