# Security checks (T096)

Run from PowerShell 7.4+ with the .NET 10 SDK, Git, Docker Desktop/Linux engine and
the official **Trivy 0.76.0** executable available. The script checks the scanner
version; install it through a trusted channel and verify the published checksum
and release provenance before running it. It does not install tools automatically.

```powershell
./scripts/Invoke-SecurityChecks.ps1 -TrivyPath /absolute/path/to/trivy
./scripts/Test-SecurityChecks.ps1 -TrivyPath /absolute/path/to/trivy
```

On Windows the executable may be `trivy.exe`; `-DockerPath` accepts an explicit
Docker CLI path. Exit **0** means all gates passed; **1** means at least one gate
failed, found review items, or could not complete. Every independent gate runs
even when another fails. There is no skip/ignore/accept-risk switch.

## Gates and evidence

| Gate | Scope and decision |
| --- | --- |
| Dependency vulnerabilities | Fresh restore, followed by NuGet's JSON audit of every solution project, direct and transitive packages. Every returned advisory, source problem, invalid JSON, missing project or tool error fails. Restore alone disables its duplicate audit to permit inventory generation under warnings-as-errors; the subsequent audit is mandatory. |
| Container images | Discover every `image:` in tracked deployment Compose files and every Dockerfile `FROM`, including build stages and optional emulator/monitoring images. Inspect existing local images and scan immutable image IDs with Trivy `vuln,secret,license`. No pull or rebuild occurs. Any vulnerability (including LOW/UNKNOWN and unfixed), secret or Trivy policy license finding fails. Missing images and scanner errors also fail. Local image IDs/digests and raw reports are recorded. |
| Secrets | Snapshot current Git tracked and non-ignored untracked files, including test/example/Markdown files. Scan with Trivy's built-in rules. Ignored runtime credentials, artifacts and `.git` history are excluded. Missing files/symlinks fail. Binary/archive exclusions and other built-in allow rules still apply; this is pattern detection, not proof that arbitrary secrets are absent. |
| NuGet licenses | Read actual restored `project.assets.json` graphs and cached `.nuspec` metadata for direct/transitive packages. MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, ISC, 0BSD, Unlicense and CC0-1.0 expressions pass the initial technical policy. Other expressions, compound expressions, file/URL/missing licenses require review and fail. Missing metadata/empty inventory fails. Image packages are additionally evaluated by Trivy's license policy. Neither gate is legal approval or a complete source-license audit. |
| Grafana plugin signature | Start only a new UUID-named container from the shipped local Grafana image, with a generated admin credential in a private file and a random loopback port. Query Grafana health/version and the loaded Infinity plugin API: Grafana 12.2.5, Infinity 4.1.1, `signature=valid`, `signatureType=grafana`. A manifest file alone is insufficient. Always remove only that fixture container; no deployment/volume operations. |
| Pinned versions | Reject floating/range/variable NuGet versions, tool versions/roll-forward, Docker base/Compose image references and external GitHub Actions without commit SHAs. Check explicit plugin installation versions. Versioned tags and SHA-256 digests pass. Existing locally built API/worker `:dev` aliases are explicitly reported development exceptions; scan their actual image IDs. This does not prove tag immutability or lock transitive resolutions. |

Compose inspection supports block-form `image:` declarations used by this repo;
inline image fields are refused. Stage references inside Dockerfiles are recognized.
The scan covers deployment definitions, not image strings embedded in arbitrary
test code or documentation. Changes to image definitions require rerunning checks.
Before a release, build the application/Grafana/Loki images from reviewed source
and populate all exact base/optional image references locally. Scanning a previously
built `:dev` image does not establish its correspondence with the current checkout.

All reports, source snapshots, stdout/stderr and generated credentials live under
ignored `artifacts/validation/T096/<uuid>/`. The console prints only gate names,
PASS/FAIL and the evidence path. **Do not upload the raw directory**: scanner
findings may contain secret material. `summary.json` records every gate;
`failures.log` and per-tool stderr distinguish findings from unavailable tools,
images or databases. `pins.json` inventories references; `nuget-licenses.json`
lists package licenses; `image-*.json` records image identities and `scan-*.json`
holds raw Trivy findings. `provenance.json` records the script hash/start time.
Fresh executions use new directories, so old reports cannot produce a PASS.

Trivy uses its vulnerability database update mechanism and records scanner/database
metadata in its reports/logs. This requires network access; unavailable required
data is not waived. Advisory data evolves; repeat checks before each release.
NuGet audit coverage depends on configured advisory feeds; Trivy coverage depends
on supported OS/package analyzers. Runtime exploits, Git history, host hardening,
LGPD/legal review and operational release approval remain separate gates.

## Validation

`Test-SecurityChecks.ps1` exercises version ranges, implicit/latest tags, registry
ports, digests, stage references, newly added Compose images, missing restore,
restricted/unknown licenses, invalid plugin signatures/versions, direct/transitive
NuGet advisories, source failure, malformed reports and native process failures.
The orchestrator control deliberately makes every tool unavailable and asserts
nonzero exit plus all independent failures in the summary. Supplying `-TrivyPath`
also scans a generated invalid PAT in Markdown and verifies actual detection and
nonzero exit. No real credential is used and no complete canary is stored in source.

Official references:

- [NuGet vulnerability audit JSON](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list)
- [Trivy .NET coverage and license metadata](https://trivy.dev/docs/v0.76/guide/coverage/language/dotnet/)
- [Trivy secret detection and default exclusions](https://trivy.dev/docs/v0.76/guide/scanner/secret/)
- [Grafana plugin signature verification](https://grafana.com/docs/grafana/latest/administration/plugin-management/plugin-sign/)
- [Pinned Trivy release](https://github.com/aquasecurity/trivy/releases/tag/v0.76.0)
