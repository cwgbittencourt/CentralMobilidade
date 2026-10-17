param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$keys = @('POSTGRES_PASSWORD_FILE', 'RABBITMQ_PASSWORD', 'GRAFANA_ADMIN_PASSWORD_FILE', 'INFLUXDB_GRAFANA_READ_TOKEN_FILE', 'INFLUXDB_QUERYABLE_ADMIN_TOKEN_FILE', 'INFLUXDB_PENDING_ADMIN_TOKEN_FILE')
$saved = @{}
foreach ($key in $keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
function Assert-Topology($condition, $message) {
    if (-not $condition) { throw $message }
}
try {
    # Resolve configuration only. Never load the user's .env, print resolved secrets, or start services.
    foreach ($key in $keys[0..3]) { [Environment]::SetEnvironmentVariable($key, [guid]::NewGuid().ToString('N'), 'Process') }
    foreach ($key in @('POSTGRES_PASSWORD_FILE', 'GRAFANA_ADMIN_PASSWORD_FILE', 'INFLUXDB_GRAFANA_READ_TOKEN_FILE')) { [Environment]::SetEnvironmentVariable($key, (Join-Path $root '.env.example'), 'Process') }
    [Environment]::SetEnvironmentVariable('INFLUXDB_QUERYABLE_ADMIN_TOKEN_FILE', (Join-Path $root '.env.example'), 'Process')
    [Environment]::SetEnvironmentVariable('INFLUXDB_PENDING_ADMIN_TOKEN_FILE', $null, 'Process')
    foreach ($development in @($false, $true)) {
        $arguments = @('compose', '--env-file', (Join-Path $root '.env.example'), '-f', (Join-Path $root 'deploy/compose.yaml'))
        if ($development) { $arguments += @('-f', (Join-Path $root 'deploy/compose.override.yaml'), '--profile', 'app', '--profile', 'pubsub-emulator') }
        $json = & docker @arguments config --format json --no-env-resolution 2>$null
        Assert-Topology ($LASTEXITCODE -eq 0) 'Compose configuration failed; no resolved configuration was printed.'
        $config = ($json -join "`n") | ConvertFrom-Json
        $services = @($config.services.PSObject.Properties.Name)
        Assert-Topology ($config.name -eq 'centralmobilidade') 'Default project name changed.'
        Assert-Topology ((@($services | Where-Object { $_ -like 'influxdb*' }) -join ',') -eq 'influxdb,influxdb-init') 'Expected exactly influxdb and influxdb-init.'
        $server = $config.services.influxdb
        Assert-Topology (($server.command -join ' ') -match '--node-id queryable ') 'Existing catalog node-id must remain queryable.'
        $mount = @($server.volumes | Where-Object { $_.type -eq 'volume' })
        Assert-Topology ($mount.Count -eq 1 -and $mount[0].source -eq 'influxdb_queryable_data' -and $mount[0].target -eq '/var/lib/influxdb3') 'Existing Influx volume mount changed.'
        Assert-Topology ($config.volumes.influxdb_queryable_data.name -eq 'centralmobilidade_influxdb_queryable_data') 'Existing generated volume identity changed.'
        Assert-Topology ($config.services.'influxdb-init'.environment.INFLUXDB3_HOST_URL -eq 'http://influxdb:8181') 'Initializer DNS is incorrect.'
        Assert-Topology ($config.services.'influxdb-init'.environment.INFLUXDB3_DATABASE_NAME -eq 'telemetry') 'Unexpected telemetry database.'
        Assert-Topology ($config.services.'influxdb-init'.restart -eq 'no') 'Initializer must terminate.'
        Assert-Topology ($config.services.'influxdb-init'.depends_on.influxdb.condition -eq 'service_healthy') 'Initializer must wait for server health.'
        Assert-Topology ($config.services.grafana.depends_on.'influxdb-init'.condition -eq 'service_completed_successfully') 'Grafana must wait for initialization.'
        Assert-Topology (-not ($json -match 'influxdb-pending|influxdb_pending|telemetry_pending|8182|influxdb-queryable|container_name')) 'Obsolete storage or fixed container names remain.'
        Assert-Topology (-not ($config.networks.PSObject.Properties.Name -contains 'pending')) 'Pending network remains.'
        foreach ($service in $config.services.PSObject.Properties.Value) {
            foreach ($dependency in $service.depends_on.PSObject.Properties.Name) {
                Assert-Topology ($services -contains $dependency) 'Dangling service dependency.'
            }
        }
        if ($development) {
            Assert-Topology ($config.services.worker.depends_on.'influxdb-init'.condition -eq 'service_completed_successfully') 'Worker must wait for initialization.'
            Assert-Topology ($server.ports.Count -eq 1 -and $server.ports[0].host_ip -eq '127.0.0.1') 'Development Influx port must use loopback.'
        } else { Assert-Topology ($server.ports.Count -eq 0) 'Base Influx must have no host port.' }
    }
    foreach ($project in @('Api', 'Worker')) {
        $settings = Get-Content (Join-Path $root "src/CentralMobilidade.Telemetry.$project/appsettings.json") -Raw | ConvertFrom-Json
        Assert-Topology ($settings.InfluxDb.Queryable.Url -eq 'http://influxdb:8181') 'Application Influx DNS is incorrect.'
        Assert-Topology (($settings.InfluxDb.PSObject.Properties.Name -join ',') -eq 'Queryable') 'Application must configure only Queryable.'
    }
    Write-Output 'PASS: base/development topology, profiles, DNS, dependencies, volume/node identity and single initialization.'
} finally {
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
}
