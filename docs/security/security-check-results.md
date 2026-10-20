# T096 security scan validation — 2026-10-20

**Automation implemented; security gate failed. No release/security approval.**

The [runner](../../scripts/Invoke-SecurityChecks.ps1) implements dependency,
container, secret, license, Grafana signature and version-pin checks. Usage,
policy and coverage limits are in the [guide](../../scripts/SECURITY-CHECKS.md).
T096 is the delivery of those scans; resolving their findings is separate work.

Final real run: `artifacts/validation/T096/6a151e812ebb46cba97bbbe3078c7d9d/`.
Exit 1, 23 individual checks: **5 PASS / 18 FAIL**. Reports are local and ignored,
not attached here because some contain possible secret values. Trivy 0.76.0 was
obtained from the official immutable release and its Windows ZIP checked against
the published SHA-256:
`7b4762a5a5326cb4b83dfd2bb5d9aaf15a1ed867c75cad7dd8b38840a28e7be6`.
This checksum comparison is not independent publisher-signature verification.

| Gate | Actual result |
| --- | --- |
| Pinned declarations | PASS; includes the documented local API/worker `:dev` exceptions, not immutable release aliases |
| NuGet advisory audit | PASS; 11 projects, direct/transitive audit, no advisories returned by configured feeds |
| Scanner version | PASS; Trivy 0.76.0 |
| Working-tree secret scan | PASS; built-in rules with test/example/Markdown exclusions disabled; not a history/ignored-file scan |
| Shipped Infinity signature | PASS; real Grafana 12.2.5, Infinity 4.1.1, valid Grafana signature, healthy database |
| NuGet license policy | FAIL; four of 137 restored package identities require review |
| Available local images | FAIL; 14 images scanned with findings, including HIGH/CRITICAL advisory candidates |
| Missing local base images | FAIL/incomplete; three references unavailable locally, never silently pulled or treated as scanned |

NuGet review items:

| Package | License metadata |
| --- | --- |
| Npgsql/10.0.3 | PostgreSQL expression, outside initial allowlist |
| Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3 | PostgreSQL expression, outside initial allowlist |
| RabbitMQ.Client/7.2.2 | Apache-2.0 OR MPL-2.0, compound expression requires review |
| xunit.abstractions/2.0.3 | No supported expression metadata; manual review required |

These are review triggers, not findings of license violation. The task does not
change package versions or claim legal approval.

Image advisory counts below are scanner occurrences, **not unique CVEs** and not
confirmed exploitability. Shared bases duplicate findings. Raw JSON records each
inspected image ID, installed version, advisory/fix and license/secret evidence.
Scanned local `:dev` images have not been rebuilt by this task and are not proven
to represent the current source checkout.

| Local image | Vulnerability occurrences | HIGH | CRITICAL | Secret candidates |
| --- | ---: | ---: | ---: | ---: |
| alpine:3.23.4 | 51 | 4 | 0 | 0 |
| centralmobilidade-api:dev | 40 | 10 | 2 | 0 |
| centralmobilidade-worker:dev | 40 | 10 | 2 | 0 |
| centralmobilidade-grafana:12.2.5-infinity-4.1.1 | 401 | 148 | 6 | 0 |
| centralmobilidade-loki:3.7.0-busybox-1.37.0 | 98 | 50 | 1 | 0 |
| gcr.io/google.com/cloudsdktool/google-cloud-cli:588.0.0-emulators | 467 | 123 | 0 | 29 |
| grafana/grafana:12.2.5 | 382 | 144 | 6 | 0 |
| grafana/loki:3.7.0 | 98 | 50 | 1 | 0 |
| influxdb:3.11.6-core | 51 | 4 | 0 | 0 |
| postgres:17.11-bookworm | 472 | 95 | 4 | 1 |
| prom/node-exporter:v1.12.1 | 31 | 14 | 0 | 0 |
| prom/prometheus:v3.15.0-busybox | 38 | 8 | 0 | 0 |
| quay.io/prometheuscommunity/postgres-exporter:v0.20.1 | 35 | 17 | 0 | 0 |
| rabbitmq:4.3.6-management-alpine | 1 | 0 | 0 | 0 |

Unavailable: `busybox:1.37.0-musl`,
`mcr.microsoft.com/dotnet/aspnet:10.0.12-noble`,
`mcr.microsoft.com/dotnet/sdk:10.0.401-noble`.
Their containing application images were scanned; that does not substitute for
scanning the missing exact bases/build-stage SDK.

Next review: triage HIGH/CRITICAL advisories against actual versions/runtime
exposure, inspect secret candidates privately (they may be bundled test material),
review the four license expressions/files, obtain/build reviewed exact images and
repeat all gates. Do not post raw matches or suppress entire directories. No claim
is made that a candidate is a leaked live credential or an exploitable CVE.

Validation of the automation: **45 positive/negative controls passed**, including
native nonzero exits, missing-tool orchestration, direct/transitive advisory
rejection, signature refusal and a real Trivy-generated invalid PAT detection in
Markdown. PowerShell syntax and whitespace checks passed. No application source
changed; .NET functional/load suites were not repeated. Actual scans were repeated
after fixing clean-project NuGet JSON handling and expanding secret coverage.
UUID Grafana fixtures were removed; no existing deployment/data volume changed.
T094 remains pending and no capacity/retention/LGPD approval follows from T096.
