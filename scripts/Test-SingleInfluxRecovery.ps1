param([switch] $SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = 'centralmobilidade-t112-' + [guid]::NewGuid().ToString('N').Substring(0, 10)
$output = Join-Path $root "artifacts/validation/foundation-single-influx/$project"
[IO.Directory]::CreateDirectory($output) | Out-Null
$keys = @('POSTGRES_PASSWORD_FILE','RABBITMQ_PASSWORD','GRAFANA_ADMIN_PASSWORD_FILE','INFLUXDB_GRAFANA_READ_TOKEN_FILE','INFLUXDB_QUERYABLE_ADMIN_TOKEN_FILE','GRAFANA_PORT')
$saved = @{}
foreach ($key in $keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
$log = Join-Path $output 'private-docker.log'
function Invoke-Docker {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
    $result = & docker @Arguments 2>&1
    $code = $LASTEXITCODE
    $result | Out-File -LiteralPath $log -Append -Encoding utf8
    if ($code -ne 0) { throw "Docker validation failed (exit $code). Inspect private log locally." }
    return $result
}
function Compose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
    Invoke-Docker ($script:compose + $Arguments)
}
function Check($condition, $message) { if (-not $condition) { throw $message } }
$started = $false
try {
    [Environment]::SetEnvironmentVariable('RABBITMQ_PASSWORD',[guid]::NewGuid().ToString('N'),'Process')
    foreach ($key in @('POSTGRES_PASSWORD_FILE','GRAFANA_ADMIN_PASSWORD_FILE','INFLUXDB_GRAFANA_READ_TOKEN_FILE')) {
        $secretPath = Join-Path $output ($key + '.txt')
        [IO.File]::WriteAllText($secretPath, [guid]::NewGuid().ToString('N'))
        [Environment]::SetEnvironmentVariable($key,$secretPath,'Process')
    }
    [Environment]::SetEnvironmentVariable('GRAFANA_PORT','0','Process')
    $tokenPath = Join-Path $output 'queryable.json'
    [Environment]::SetEnvironmentVariable('INFLUXDB_QUERYABLE_ADMIN_TOKEN_FILE',$tokenPath,'Process')
    Invoke-Docker @('run','--rm','--network','none','--mount',"type=bind,source=$output,target=/tokens",'influxdb:3.11.6-core','influxdb3','create','token','--admin','--offline','--name','t112-bootstrap','--output-file','/tokens/queryable.json') | Out-Null
    $shell = @'
#!/bin/sh
set -eu
INFLUXDB3_AUTH_TOKEN=$(sed -n 's/.*"token": *"\([^"]*\)".*/\1/p' /run/secrets/influxdb_admin_token)
export INFLUXDB3_AUTH_TOKEN
if [ "$1" = seed ]; then
    printf '%s\n' 'telemetria,empresa_id=t112-a,veiculo_id=shared latitude=1.0,longitude=2.0,velocidade=0.0,rpm=0i,ignicao=false 1792022400123' 'telemetria,empresa_id=t112-b,veiculo_id=shared latitude=3.0,longitude=4.0,velocidade=0.0,rpm=0i,ignicao=false 1792022400123' > /tmp/t112-proof.lp
    influxdb3 write --quiet --database telemetry --precision ms --gzip --file /tmp/t112-proof.lp
fi
influxdb3 query --database telemetry --format csv 'SELECT empresa_id,veiculo_id,latitude,longitude,time FROM telemetria ORDER BY empresa_id'
'@
    [IO.File]::WriteAllText((Join-Path $output 'influx-proof.sh'), $shell.Replace("`r`n","`n") + "`n")
    $mount = (($output.Replace('\','/') + ':/validation:ro') | ConvertTo-Json -Compress)
    [IO.File]::WriteAllText((Join-Path $output 'override.yaml'), "services:`n  influxdb:`n    volumes:`n      - $mount`n")
    $script:compose = @('compose','--project-name',$project,'--env-file',(Join-Path $root '.env.example'),'-f',(Join-Path $root 'deploy/compose.yaml'),'-f',(Join-Path $output 'override.yaml'))
    $config = (Compose @('config','--format','json') | Out-String) | ConvertFrom-Json
    $volumes = @($config.volumes.PSObject.Properties.Value | ForEach-Object name)
    Check ($volumes.Count -eq 7) 'Expected seven persistent volumes.'
    $started = $true
    $up = @('up','-d','--wait','--wait-timeout','240')
    if (-not $SkipBuild) { $up += '--build' } else { $up += '--no-build' }
    Compose $up | Out-Null
    function Verify-Services {
        $rows = @(Compose @('ps','-a','--format','json') | ForEach-Object { $_ | ConvertFrom-Json })
        Check (@($rows | Where-Object { $_.State -eq 'running' -and $_.Health -eq 'healthy' }).Count -eq 6) 'Expected six healthy dependency servers.'
        foreach ($job in @('influxdb-init','quarantine-init')) {
            $row = $rows | Where-Object Service -eq $job
            Check ($row.State -eq 'exited' -and $row.ExitCode -eq 0) 'Initializer did not complete successfully.'
        }
        Check (@($rows | Where-Object Service -like 'influxdb*').Count -eq 2) 'Unexpected Influx containers.'
    }
    Verify-Services
    $marker = [guid]::NewGuid().ToString('N')
    $volumeArgs = @('run','--rm','--network','none')
    foreach ($volume in $volumes) { Check ($volume.StartsWith($project + '_')) 'Volume escaped isolated project.'; $volumeArgs += @('--mount',"type=volume,source=$volume,target=/proof/$volume") }
    $writeMarkers = 'for d in /proof/*; do printf "%s" "__MARKER__" > "$d/t112-proof"; done'.Replace('__MARKER__', $marker)
    $checkMarkers = 'for d in /proof/*; do test "$(cat "$d/t112-proof")" = "__MARKER__"; done'.Replace('__MARKER__', $marker)
    Invoke-Docker ($volumeArgs + @('alpine:3.23.4','sh','-ec',$writeMarkers)) | Out-Null
    Compose @('exec','-T','postgres','psql','-U','telemetry','-d','telemetry_control','-v','ON_ERROR_STOP=1','-c',"CREATE TABLE t112_proof(value text PRIMARY KEY); INSERT INTO t112_proof VALUES ('$marker');") | Out-Null
    $original = (Compose @('exec','-T','influxdb','sh','/validation/influx-proof.sh','seed') | Out-String).Trim()
    Check ($original.Contains('t112-a') -and $original.Contains('t112-b')) 'Both synthetic companies must be queryable.'
    function Verify-Data {
        $pg = (Compose @('exec','-T','postgres','psql','-U','telemetry','-d','telemetry_control','-Atc','SELECT value FROM t112_proof') | Out-String).Trim()
        Check ($pg -eq $marker) 'PostgreSQL row did not survive.'
        $points = (Compose @('exec','-T','influxdb','sh','/validation/influx-proof.sh','verify') | Out-String).Trim()
        Check ($points -eq $original) 'Influx identities, values or millisecond timestamps changed.'
        Invoke-Docker ($volumeArgs + @('alpine:3.23.4','sh','-ec',$checkMarkers)) | Out-Null
    }
    Compose @('restart','--timeout','30','postgres','rabbitmq','influxdb','prometheus','loki','grafana') | Out-Null
    Compose @('up','-d','--wait','--wait-timeout','240') | Out-Null
    Verify-Services
    Verify-Data
    Write-Output 'PASS: restart, PostgreSQL row, two Influx identities and seven volume markers.'
    Compose @('down','--timeout','30') | Out-Null
    Compose @('up','-d','--wait','--wait-timeout','240') | Out-Null
    Verify-Services
    Verify-Data
    $summary = [ordered]@{ utc=[DateTimeOffset]::UtcNow.ToString('o'); project=$project; healthyServers=6; successfulJobs=2; volumes=$volumes; influxRows=2; postgresRows=1; restart='pass'; recreate='pass'; volumeDeletion=$false; query=$original }
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'result.json')
    Write-Output "PASS: recreation and repeated initialization. Evidence: $output/result.json"
} catch {
    if ($started) {
        try { Compose @('logs','--no-color','--tail','120','rabbitmq') | Out-Null } catch { }
    }
    throw
} finally {
    try { if ($started) { Compose @('down','--timeout','30') | Out-Null } }
    finally { foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key],'Process') } }
    # Preserve the isolated volumes, token and private diagnostic log; never delete any volume.
}
