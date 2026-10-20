#requires -Version 7.4
param([string]$TrivyPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$canaryTrivy = $TrivyPath
. "$PSScriptRoot/Invoke-SecurityChecks.ps1"
$root = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/validation/T096/controls-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $root)
$count = 0
function Assert([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Control failed: $Name" }
    $script:count++
}
function Assert-Throws([scriptblock]$Action, [string]$Name) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert $failed $Name
}
foreach ($version in @('10.0.12', '1.19.1-beta.1')) { Assert (Test-ExactVersion $version) "exact:$version" }
foreach ($version in @('10.*', '[10.0.0,11.0.0)', 'latest', '$(Version)', '10.0')) { Assert (-not (Test-ExactVersion $version)) "floating:$version" }
foreach ($image in @('postgres:17.11-bookworm', 'registry:5000/a:v1.2.3', ('a@sha256:' + ('a' * 64)))) { Assert (Test-PinnedImage $image) 'pinned image' }
foreach ($image in @('a', 'a:latest', 'a:dev', 'a:1', 'a:1.2.x', 'a:${TAG}', 'a@sha256:abc')) { Assert (-not (Test-PinnedImage $image)) 'floating image' }
[IO.File]::WriteAllText("$root/Dockerfile", "FROM a:1.2.3 AS build`nFROM build AS final`n")
Assert ((Get-SecurityInventory $root @('Dockerfile')).Issues.Count -eq 0) 'stage reference'
[IO.File]::WriteAllText("$root/Dockerfile", "FROM a:latest`n")
Assert ((Get-SecurityInventory $root @('Dockerfile')).Issues.Count -eq 1) 'unpinned base'
[IO.File]::WriteAllText("$root/test.csproj", '<Project><ItemGroup><PackageReference Include="a"><Version>1.*</Version></PackageReference></ItemGroup></Project>')
Assert ((Get-SecurityInventory $root @('test.csproj')).Issues -contains 'package-version:test.csproj') 'child version'
[IO.File]::WriteAllText("$root/compose.yaml", "services:`n  new-service:`n    image: new:latest`n")
Assert ((Get-SecurityInventory $root @('compose.yaml')).Issues.Count -eq 1) 'new compose image discovered'
[IO.File]::WriteAllText("$root/test.csproj", '<Project/>')
$obj = New-Item -ItemType Directory "$root/obj"
$package = New-Item -ItemType Directory "$root/cache/example/1.0.0" -Force
$assets = @{ libraries = @{ 'Example/1.0.0' = @{ type = 'package'; path = 'example/1.0.0' } }; packageFolders = @{ "$root/cache/" = @{} } }
$assets | ConvertTo-Json -Depth 10 | Set-Content "$obj/project.assets.json"
[IO.File]::WriteAllText("$package/example.nuspec", '<package><metadata><license type="expression">MIT</license></metadata></package>')
Assert ((Get-NuGetLicenseInventory $root @('test.csproj')).ReviewRequired.Count -eq 0) 'restored permissive license'
foreach ($license in @('<license type="file">LICENSE</license>', '<license type="expression">GPL-3.0-only</license>', '<license type="expression">MIT OR GPL-3.0-only</license>', '')) {
    [IO.File]::WriteAllText("$package/example.nuspec", "<package><metadata>$license</metadata></package>")
    Assert ((Get-NuGetLicenseInventory $root @('test.csproj')).ReviewRequired.Count -eq 1) 'unknown/restricted license review'
}
Assert-Throws { Get-NuGetLicenseInventory $root @('missing/project.csproj') } 'missing restore fails'
$plugin = @{ id = 'yesoreyeram-infinity-datasource'; info = @{ version = '4.1.1' }; signature = 'valid'; signatureType = 'grafana' }
Assert-GrafanaPlugin $plugin
Assert $true 'valid plugin'
foreach ($change in @(@{ signature = 'unsigned' }, @{ signature = 'modified' }, @{ signatureType = 'community' }, @{ info = @{ version = '4.1.2' } })) {
    $invalid = $plugin.Clone()
    foreach ($key in $change.Keys) { $invalid[$key] = $change[$key] }
    Assert-Throws { Assert-GrafanaPlugin $invalid } 'plugin refusal'
}
Assert-Throws { Assert-GrafanaPlugin @{} } 'missing signature fails'
$fake = Join-Path $root 'failure.ps1'
[IO.File]::WriteAllText($fake, "Write-Output 'synthetic-private-output'; exit 7")
Assert-Throws { Invoke-SecurityProcess (Get-Process -Id $PID).Path @('-NoProfile', '-File', $fake) "$root/native.log" } 'nonzero tool fails'
Assert ((Get-Content "$root/native.log" -Raw) -match 'synthetic-private-output') 'native output captured privately'
$clean = @{ version = 1; sources = @('https://api.nuget.org/v3/index.json'); projects = @(@{ path = 'example.csproj' }) }
Assert-NuGetAudit $clean 1
Assert $true 'NuGet clean project omits frameworks'
Assert-Throws { Assert-NuGetAudit $clean 2 } 'NuGet missing project'
$problem = $clean.Clone(); $problem.problems = @('source unavailable')
Assert-Throws { Assert-NuGetAudit $problem 1 } 'NuGet source failure'
foreach ($kind in @('topLevelPackages', 'transitivePackages')) {
    $vulnerable = $clean.Clone()
    $vulnerable.projects = @(@{ frameworks = @(@{ $kind = @(@{ vulnerabilities = @(@{ severity = 'Low' }) }) }) })
    Assert-Throws { Assert-NuGetAudit $vulnerable 1 } "NuGet $kind advisory"
}
Assert-Throws { Assert-NuGetAudit @{} 1 } 'malformed NuGet JSON'
if ($canaryTrivy) {
    $canary = New-Item -ItemType Directory "$root/canary"
    # Deliberately invalid generated PAT, no real credential; don't store the complete token in source.
    [IO.File]::WriteAllText("$canary/example.md", ('token = "ghp_' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(18)).ToLowerInvariant() + '"'))
    "disable-allow-rules:`n  - markdown`n  - tests`n  - examples" | Set-Content "$root/secret.yaml"
    Assert-Throws {
        Invoke-SecurityProcess $canaryTrivy @('fs', '--scanners', 'secret', '--secret-config', "$root/secret.yaml", '--format', 'json', '--output', "$root/canary.json", '--exit-code', '1', $canary.FullName) "$root/canary.log"
    } 'real Trivy secret canary returns nonzero'
    $detected = Get-Content "$root/canary.json" -Raw | ConvertFrom-Json -AsHashtable
    Assert (@($detected.Results | ForEach-Object { $_.Secrets } | Where-Object RuleID -eq 'github-pat').Count -gt 0) 'real Trivy canary detected in Markdown'
}
# Missing tools must fail the overall run and still produce a full summary.
function Invoke-SecurityProcess { throw 'Synthetic tool unavailable.' }
$fakeDocker = Join-Path $root 'cleanup.ps1'
[IO.File]::WriteAllText($fakeDocker, 'exit 0')
$exitCode = Invoke-SecurityChecks $root 'absent-trivy' $fakeDocker
Assert ($exitCode -eq 1) 'overall unavailable-tools exit'
$summary = Get-ChildItem "$root/artifacts/validation/T096/*/summary.json" | Select-Object -First 1 | Get-Content -Raw | ConvertFrom-Json
Assert (-not $summary.Passed -and @($summary.Checks | Where-Object Status -eq 'FAIL').Count -eq 6) 'all independent gates reported'
Write-Host "$count security negative/positive controls passed. Evidence: $root"
