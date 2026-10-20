#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$TrivyPath = 'trivy',
    [string]$DockerPath = 'docker'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-SecurityProcess([string]$File, [string[]]$Arguments, [string]$OutputPath) {
    # Native output can contain secrets. Never forward it to the console.
    & $File @Arguments > $OutputPath 2> "$OutputPath.stderr"
    if ($LASTEXITCODE -ne 0) { throw "Tool failed (exit $LASTEXITCODE); inspect private evidence." }
}

function Test-ExactVersion([string]$Value) {
    return $Value -cmatch '^\d+\.\d+\.\d+(?:[.-][A-Za-z0-9]+)*$'
}

function Test-PinnedImage([string]$Image) {
    return $Image -cmatch '^[a-zA-Z0-9./_-]+@sha256:[a-f0-9]{64}$' -or
        $Image -cmatch '^[a-zA-Z0-9./_:-]+:v?\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?$'
}

function Get-SecurityInventory([string]$Root, [string[]]$Files) {
    $issues = [System.Collections.Generic.List[string]]::new()
    $images = [System.Collections.Generic.HashSet[string]]::new()
    $localBuilds = @('centralmobilidade-api:dev', 'centralmobilidade-worker:dev')
    foreach ($file in $Files) {
        $path = Join-Path $Root $file
        if ($file -match '(?:\.csproj|Packages\.props)$') {
            [xml]$xml = Get-Content -LiteralPath $path -Raw
            foreach ($node in $xml.SelectNodes('//PackageVersion|//PackageReference')) {
                $version = $node.GetAttribute('Version')
                if ($node.SelectSingleNode('Version')) { $version = $node.SelectSingleNode('Version').InnerText }
                if ($node.HasAttribute('VersionOverride') -or ($version -and -not (Test-ExactVersion $version))) {
                    $issues.Add("package-version:$file")
                }
            }
        }
        if ($file -match '(?:^|/)dotnet-tools\.json$') {
            $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
            foreach ($tool in $manifest.tools.Values) {
                if (-not (Test-ExactVersion $tool.version) -or $tool.rollForward -ne $false) { $issues.Add("tool-version:$file") }
            }
        }
        if ($file -match '(?:Dockerfile|\.Dockerfile)$') {
            $stages = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($line in Get-Content -LiteralPath $path) {
                if ($line -match 'plugins\s+install\s+(\S+)\s+(\S+)') {
                    if (-not (Test-ExactVersion $Matches[2])) { $issues.Add("plugin-version:$file") }
                }
                if ($line -match '^\s*FROM\s+(?:--platform=\S+\s+)?(\S+)(?:\s+AS\s+(\S+))?') {
                    $image = $Matches[1]; $stage = if ($Matches.ContainsKey(2)) { $Matches[2] } else { $null }
                    if (-not $stages.Contains($image)) {
                        if (-not (Test-PinnedImage $image)) { $issues.Add("base-image:$file") }
                        [void]$images.Add($image)
                    }
                    if ($stage) { [void]$stages.Add($stage) }
                }
            }
        }
        if ($file -match '(?:^|/)compose[^/]*\.ya?ml$') {
            foreach ($line in Get-Content -LiteralPath $path) {
                if ($line -notmatch '^\s*#' -and $line -match '\bimage\s*:' -and $line -notmatch '^\s*image:\s*\S+') {
                    $issues.Add("unsupported-compose-image-syntax:$file")
                }
                if ($line -match '^\s*image:\s*["'']?([^\s"''#]+)') {
                    $image = $Matches[1]
                    if (-not (Test-PinnedImage $image) -and $image -notin $localBuilds) { $issues.Add("compose-image:$file") }
                    [void]$images.Add($image)
                }
            }
        }
        if ($file -match '^\.github/workflows/.*\.ya?ml$') {
            foreach ($line in Get-Content -LiteralPath $path) {
                if ($line -match '^\s*(?:-\s*)?uses:\s*(\S+)' -and $Matches[1] -notmatch '^\./' -and
                    $Matches[1] -notmatch '@[a-f0-9]{40}$') { $issues.Add("action-version:$file") }
            }
        }
    }
    if ($images.Count -eq 0) { $issues.Add('no-images-discovered') }
    return @{ Issues = @($issues); Images = @($images | Sort-Object) }
}

function Get-NuGetLicenseInventory([string]$Root, [string[]]$Files) {
    # Inventory actual restored direct AND transitive versions, not only central declarations.
    $packages = @{}
    $issues = [System.Collections.Generic.List[string]]::new()
    $allowed = @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', '0BSD', 'Unlicense', 'CC0-1.0')
    foreach ($project in @($Files | Where-Object { $_ -match '\.csproj$' })) {
        $assetsPath = Join-Path (Split-Path (Join-Path $Root $project)) 'obj/project.assets.json'
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($entry in $assets.libraries.GetEnumerator()) {
            if ($entry.Value.type -ne 'package' -or $packages.ContainsKey($entry.Key)) { continue }
            $specs = @(foreach ($folder in $assets.packageFolders.Keys) {
                $directory = Join-Path $folder $entry.Value.path
                if (Test-Path -LiteralPath $directory) { Get-ChildItem -LiteralPath $directory -Filter '*.nuspec' }
            })
            if ($specs.Count -eq 0) { throw 'Restored package license metadata missing.' }
            [xml]$spec = Get-Content -LiteralPath $specs[0].FullName -Raw
            $license = $spec.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="license"]')
            $expression = if ($license -and $license.GetAttribute('type') -eq 'expression') { $license.InnerText.Trim() } else { 'REVIEW_REQUIRED' }
            $packages[$entry.Key] = $expression
            # Conservative: compound expressions and license files need explicit review, never guessed permissive.
            if ($expression -cnotin $allowed) { $issues.Add($entry.Key) }
        }
    }
    if ($packages.Count -eq 0) { throw 'Empty restored package inventory.' }
    return @{ Packages = $packages; ReviewRequired = @($issues) }
}

function Assert-GrafanaPlugin($Plugin) {
    if ($Plugin.id -cne 'yesoreyeram-infinity-datasource' -or $Plugin.info.version -cne '4.1.1' -or
        $Plugin.signature -cne 'valid' -or $Plugin.signatureType -cne 'grafana') {
        throw 'Grafana plugin version/signature refused.'
    }
}

function Assert-NuGetAudit($Report, [int]$ProjectCount) {
    if ($Report.version -ne 1 -or $Report.sources.Count -eq 0 -or
        ($Report.ContainsKey('problems') -and $Report.problems.Count -gt 0) -or
        -not $Report.ContainsKey('projects') -or $Report.projects.Count -ne $ProjectCount -or $ProjectCount -eq 0) {
        throw 'NuGet audit incomplete.'
    }
    foreach ($project in $Report.projects) {
        # NuGet omits frameworks entirely for projects with no vulnerable packages.
        if (-not $project.ContainsKey('frameworks')) { continue }
        foreach ($framework in $project.frameworks) {
            foreach ($kind in @('topLevelPackages', 'transitivePackages')) {
                if ($framework.ContainsKey($kind)) {
                    foreach ($package in $framework[$kind]) {
                        if ($package.ContainsKey('vulnerabilities') -and $package.vulnerabilities.Count -gt 0) { throw 'NuGet advisories require review.' }
                    }
                }
            }
        }
    }
}

function Invoke-SecurityChecks([string]$Root, [string]$Trivy, [string]$Docker) {
    $Root = (Resolve-Path -LiteralPath $Root).Path
    $evidence = Join-Path $Root ("artifacts/validation/T096/" + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $evidence -Force)
    $checks = [System.Collections.Generic.List[object]]::new()
    $context = @{ Files = @(); Inventory = @{ Images = @(); Issues = @() } }
    @{ ScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash; StartedUtc = [DateTimeOffset]::UtcNow.ToString('O') } |
        ConvertTo-Json | Set-Content "$evidence/provenance.json"
    '{}' | Set-Content "$evidence/trivy.yaml"
    '' | Set-Content "$evidence/ignore"
    "disable-allow-rules:`n  - markdown`n  - tests`n  - examples" | Set-Content "$evidence/secret.yaml"
    $scanOptions = @('--config', "$evidence/trivy.yaml", '--ignorefile', "$evidence/ignore", '--secret-config', "$evidence/secret.yaml", '--timeout', '15m')
    function Check([string]$Name, [scriptblock]$Action) {
        try { & $Action; $checks.Add(@{ Name = $Name; Status = 'PASS' }) }
        catch {
            $checks.Add(@{ Name = $Name; Status = 'FAIL'; Reason = $_.Exception.GetType().Name })
            # Full diagnostics stay private, especially HTTP/tool errors.
            $_ | Out-String | Add-Content "$evidence/failures.log"
        }
        Write-Host ("{0}: {1}" -f $Name, $checks[$checks.Count - 1].Status)
    }
    Push-Location $Root
    try {
        Check 'pinned-versions' {
            Invoke-SecurityProcess git @('-c', 'core.quotepath=false', 'ls-files', '--cached', '--others', '--exclude-standard') "$evidence/tracked.txt"
            $context.Files = @(Get-Content "$evidence/tracked.txt")
            $context.Inventory = Get-SecurityInventory $Root $context.Files
            $context.Inventory | ConvertTo-Json -Depth 10 | Set-Content "$evidence/pins.json"
            if ($context.Inventory.Issues.Count -gt 0) { throw 'Unpinned versions.' }
        }
        Check 'dependency-vulnerabilities' {
            Invoke-SecurityProcess dotnet @('restore', 'CentralMobilidade.slnx', '--force-evaluate', '-p:NuGetAudit=false') "$evidence/restore.log"
            Invoke-SecurityProcess dotnet @('list', 'CentralMobilidade.slnx', 'package', '--vulnerable', '--include-transitive', '--format', 'json') "$evidence/nuget.json"
            $report = Get-Content "$evidence/nuget.json" -Raw | ConvertFrom-Json -AsHashtable
            Assert-NuGetAudit $report @($context.Files | Where-Object { $_ -match '\.csproj$' }).Count
        }
        Check 'nuget-licenses' {
            $licenses = Get-NuGetLicenseInventory $Root $context.Files
            $licenses | ConvertTo-Json -Depth 10 | Set-Content "$evidence/nuget-licenses.json"
            if ($licenses.ReviewRequired.Count -gt 0) { throw 'License review required.' }
        }
        Check 'scanner-version' {
            Invoke-SecurityProcess $Trivy @('--version', '--format', 'json') "$evidence/trivy-version.json"
            $version = Get-Content "$evidence/trivy-version.json" -Raw | ConvertFrom-Json
            if ($version.Version -cne '0.76.0') { throw 'Scanner version mismatch.' }
            $context.ScannerReady = $true
        }
        Check 'tracked-secrets' {
            if (-not $context.ContainsKey('ScannerReady')) { throw 'Scanner unavailable.' }
            # Snapshot tracked working-tree contents only. No .git history, ignored secrets or raw artifacts.
            $snapshot = Join-Path $evidence 'source'
            [void](New-Item -ItemType Directory -Path $snapshot)
            foreach ($file in $context.Files) {
                $source = Join-Path $Root $file
                if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).LinkType) { throw 'Tracked file missing or symbolic link.' }
                $destination = Join-Path $snapshot $file
                [void](New-Item -ItemType Directory -Path (Split-Path $destination) -Force)
                Copy-Item -LiteralPath $source -Destination $destination
            }
            Invoke-SecurityProcess $Trivy (@('fs', '--scanners', 'secret', '--format', 'json', '--output', "$evidence/secrets.json", '--exit-code', '1', $snapshot) + $scanOptions) "$evidence/secrets.log"
            $secretReport = Get-Content "$evidence/secrets.json" -Raw | ConvertFrom-Json
            if ($secretReport.SchemaVersion -ne 2 -or -not $secretReport.ArtifactName) { throw 'Invalid scanner report.' }
        }
        foreach ($image in $context.Inventory.Images) {
            $id = [Guid]::NewGuid().ToString('N')
            Check "container:$image" {
                if (-not $context.ContainsKey('ScannerReady')) { throw 'Scanner unavailable.' }
                # Inspect first: scan existing exact local image, never silently pull a replacement.
                Invoke-SecurityProcess $Docker @('image', 'inspect', $image) "$evidence/image-$id.json"
                $inspection = Get-Content "$evidence/image-$id.json" -Raw | ConvertFrom-Json
                Invoke-SecurityProcess $Trivy (@('image', '--image-src', 'docker', '--scanners', 'vuln,secret,license', '--severity', 'UNKNOWN,LOW,MEDIUM,HIGH,CRITICAL', '--format', 'json', '--output', "$evidence/scan-$id.json", '--exit-code', '1', $inspection[0].Id) + $scanOptions) "$evidence/scan-$id.log"
                $imageReport = Get-Content "$evidence/scan-$id.json" -Raw | ConvertFrom-Json
                if ($imageReport.SchemaVersion -ne 2 -or $imageReport.ArtifactType -cne 'container_image' -or
                    $imageReport.Metadata.ImageID -cne $inspection[0].Id -or $imageReport.Results.Count -eq 0) { throw 'Invalid image scan report.' }
            }
        }
        Check 'grafana-plugin-signature' {
            $name = 'cm-security-' + [Guid]::NewGuid().ToString('N')
            $passwordFile = Join-Path $evidence 'grafana-password'
            $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
            [IO.File]::WriteAllText($passwordFile, $password)
            try {
                Invoke-SecurityProcess $Docker @('run', '--detach', '--pull=never', '--name', $name, '--publish', '127.0.0.1::3000', '--mount', "type=bind,source=$passwordFile,target=/run/secrets/admin,readonly", '--env', 'GF_SECURITY_ADMIN_PASSWORD__FILE=/run/secrets/admin', 'centralmobilidade-grafana:12.2.5-infinity-4.1.1') "$evidence/grafana-run.log"
                Invoke-SecurityProcess $Docker @('port', $name, '3000/tcp') "$evidence/grafana-port.log"
                $address = (Get-Content "$evidence/grafana-port.log" -Raw).Trim()
                if ($address -notmatch '^127\.0\.0\.1:\d+$') { throw 'Non-loopback Grafana.' }
                $headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("admin:$password")) }
                $plugin = $null
                for ($attempt = 0; $attempt -lt 60; $attempt++) {
                    try { $plugin = Invoke-RestMethod "http://$address/api/plugins/yesoreyeram-infinity-datasource/settings" -Headers $headers -TimeoutSec 2 -MaximumRedirection 0; break }
                    catch { Start-Sleep -Seconds 1 }
                }
                if (-not $plugin) { throw 'Grafana did not become ready.' }
                $health = Invoke-RestMethod "http://$address/api/health" -TimeoutSec 5 -MaximumRedirection 0
                if ($health.version -cne '12.2.5' -or $health.database -cne 'ok') { throw 'Grafana version/health refused.' }
                $plugin | ConvertTo-Json -Depth 15 | Set-Content "$evidence/grafana-plugin.json"
                Assert-GrafanaPlugin $plugin
            }
            finally {
                # Only the UUID container created here. No deployment or volume operations.
                & $Docker rm --force $name > "$evidence/grafana-cleanup.log" 2>&1
                if ($LASTEXITCODE -ne 0) { throw 'Grafana fixture cleanup failed.' }
            }
        }
    }
    finally {
        Pop-Location
        $summary = @{ SchemaVersion = 1; Utc = [DateTimeOffset]::UtcNow.ToString('O'); Checks = @($checks); Passed = ($checks.Count -gt 0 -and @($checks | Where-Object Status -ne 'PASS').Count -eq 0) }
        $summary | ConvertTo-Json -Depth 10 | Set-Content "$evidence/summary.json"
        Write-Host "Private evidence: $evidence"
    }
    return [int](-not $summary.Passed)
}

# Dot-sourcing loads helpers for negative-control tests; normal execution always runs every gate.
if ($MyInvocation.InvocationName -ne '.') { exit (Invoke-SecurityChecks $RepositoryRoot $TrivyPath $DockerPath) }
