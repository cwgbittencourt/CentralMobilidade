#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GrafanaUrl,
    [Parameter(Mandatory)][string]$ConfigurationPath,
    [Parameter(Mandatory)][string]$StateDirectory
)
$ErrorActionPreference = 'Stop'
$VerbosePreference = 'SilentlyContinue'
$DebugPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

function Assert-Configuration([bool]$Condition) { if (-not $Condition) { throw 'Invalid configuration.' } }
function Read-Secret([string]$Name) {
    Assert-Configuration ($Name -cmatch '^[A-Za-z_][A-Za-z0-9_]*$')
    $value = [Environment]::GetEnvironmentVariable($Name, 'Process')
    Assert-Configuration (-not [string]::IsNullOrWhiteSpace($value) -and $value.Length -le 8192 -and $value -notmatch '[\r\n\x00]')
    return $value
}
function Read-Url([string]$Text) {
    $uri = [uri]::new($Text, [UriKind]::Absolute)
    Assert-Configuration ($uri.Scheme -in @('http', 'https') -and -not $uri.UserInfo -and -not $uri.Query -and -not $uri.Fragment)
    return $uri.AbsoluteUri.TrimEnd('/')
}
function Invoke-Grafana([string]$Method, [string]$Path, [string]$Authorization, [int]$Organization = 0, $Body = $null, [switch]$AllowMissing) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $script:BaseUrl + $Path)
    try {
        $request.Headers.Add('Authorization', $Authorization)
        if ($Organization -gt 0) { $request.Headers.Add('X-Grafana-Org-Id', $Organization.ToString([cultureinfo]::InvariantCulture)) }
        if ($null -ne $Body) { $request.Content = [System.Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 64 -Compress), [Text.Encoding]::UTF8, 'application/json') }
        $response = $script:Client.SendAsync($request).GetAwaiter().GetResult()
        try {
            if ($AllowMissing -and [int]$response.StatusCode -eq 404) { return $null }
            if (-not $response.IsSuccessStatusCode) { throw 'Grafana operation failed.' }
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([string]::IsNullOrWhiteSpace($text)) { return $null }
            return ConvertFrom-Json -InputObject $text -AsHashtable -Depth 64
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}
function Protect-State([string]$Path) {
    if ($IsWindows) {
        $acl = [System.Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.SetOwner($sid)
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
        [System.IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path), $acl)
    } else { [IO.File]::SetUnixFileMode($Path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) }
}

$client = $null
$lock = $null
try {
    $script:BaseUrl = Read-Url $GrafanaUrl
    Assert-Configuration ((Get-Item -LiteralPath $ConfigurationPath).Length -le 1048576)
    $config = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json -AsHashtable -Depth 64
    $user = Read-Secret $config.bootstrap.usernameEnv
    $password = Read-Secret $config.bootstrap.passwordEnv
    Assert-Configuration (-not $user.Contains(':'))
    $basic = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($user + ':' + $password))
    $secretSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [void]$secretSet.Add($password)
    [void]$secretSet.Add($basic)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $uids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $plans = @()
    Assert-Configuration ($config.organizations.Count -gt 0 -and $config.organizations.Count -le 100)
    foreach ($organization in $config.organizations) {
        Assert-Configuration (-not [string]::IsNullOrWhiteSpace($organization.name) -and $organization.name.Length -le 128 -and $names.Add($organization.name))
        Assert-Configuration ($organization.datasourceUid -cmatch '^[A-Za-z0-9_-]{1,40}$' -and $uids.Add($organization.datasourceUid))
        Assert-Configuration ($organization.serviceAccount.role -ceq 'Admin' -and -not [string]::IsNullOrWhiteSpace($organization.serviceAccount.name))
        Assert-Configuration ($organization.companyIds.Count -gt 0 -and $organization.companyIds.Count -le 500)
        $companies = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($company in $organization.companyIds) { Assert-Configuration (-not [string]::IsNullOrWhiteSpace($company) -and $company.Length -le 128 -and $company -notmatch '\p{Cc}' -and $companies.Add($company) -and [Text.UTF8Encoding]::new($false, $true).GetByteCount($company) -le 256) }
        Assert-Configuration ($organization.presentation.permissions.Count -gt 0 -and 'telemetry.read' -cin $organization.presentation.permissions)
        foreach ($permission in $organization.presentation.permissions) { Assert-Configuration ($permission -cin @('telemetry.read', 'driver.read')) }
        $logins = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($member in $organization.members) { Assert-Configuration ($member.role -ceq 'Viewer' -and -not [string]::IsNullOrWhiteSpace($member.login) -and $logins.Add($member.login)) }
        $presentationSecret = Read-Secret $organization.presentation.credentialEnv
        Assert-Configuration ($secretSet.Add($presentationSecret))
        $apiUrl = Read-Url $organization.presentation.baseUrl
        $dashboardPath = [IO.Path]::GetFullPath($organization.dashboardDirectory, (Split-Path ([IO.Path]::GetFullPath($ConfigurationPath))))
        $dashboards = @()
        $dashboardUids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in @(Get-ChildItem -LiteralPath $dashboardPath -Filter '*.json' -File)) {
            Assert-Configuration ($file.Length -le 1048576)
            $dashboard = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable -Depth 64
            Assert-Configuration ($dashboard.uid -cmatch '^[A-Za-z0-9_-]{1,40}$' -and $dashboardUids.Add($dashboard.uid))
            # Viewer dashboard templates carry a fixed datasource placeholder, never credentials.
            $text = $dashboard | ConvertTo-Json -Depth 64 -Compress
            foreach ($secret in $secretSet) { Assert-Configuration (-not $text.Contains($secret, [StringComparison]::Ordinal)) }
            Assert-Configuration ($text -notmatch '"(?:rawSql|secureJsonData|bearerToken)"' -and $text -notmatch '"type":"(?:influxdb|prometheus|loki)"')
            $text = $text.Replace('${VIEWER_DATASOURCE_UID}', $organization.datasourceUid)
            $dashboards += ,($text | ConvertFrom-Json -AsHashtable -Depth 64)
        }
        Assert-Configuration ($dashboards.Count -gt 0)
        $plans += @{ Organization = $organization; Secret = $presentationSecret; ApiUrl = $apiUrl; Dashboards = $dashboards }
    }
    foreach ($plan in $plans) {
        foreach ($dashboard in $plan.Dashboards) {
            $text = $dashboard | ConvertTo-Json -Depth 64 -Compress
            foreach ($secret in $secretSet) { Assert-Configuration (-not $text.Contains($secret, [StringComparison]::Ordinal)) }
        }
    }
    $timeout = [int]$config.readinessTimeoutSeconds
    Assert-Configuration ($timeout -ge 1 -and $timeout -le 120)
    $handler = [Net.Http.SocketsHttpHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseProxy = $false
    $script:Client = [Net.Http.HttpClient]::new($handler)
    $script:Client.Timeout = [TimeSpan]::FromSeconds([Math]::Min(10, $timeout))
    $script:Client.MaxResponseContentBufferSize = 2097152
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        try { $health = Invoke-Grafana GET '/api/health' $basic; if ($health.database -ceq 'ok') { break } } catch { }
        if ($clock.Elapsed.TotalSeconds -ge $timeout) { throw 'Readiness unavailable.' }
        Start-Sleep -Milliseconds 200
    } while ($true)
    Assert-Configuration ($health.version -ceq '12.2.5')
    $plugin = Invoke-Grafana GET '/api/plugins/yesoreyeram-infinity-datasource/settings' $basic
    Assert-Configuration ($plugin.info.version -ceq '4.1.1' -and $plugin.signature -ceq 'valid')

    if (Test-Path -LiteralPath $StateDirectory) {
        Assert-Configuration (-not ((Get-Item -LiteralPath $StateDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint))
        Assert-Configuration (@(Get-ChildItem -LiteralPath $StateDirectory -Force | Where-Object { $_.Name -cnotin @('reconcile.lock', 'managed.json', 'managed.json.tmp') }).Count -eq 0)
        Assert-Configuration (@(Get-ChildItem -LiteralPath $StateDirectory -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -eq 0)
    }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($StateDirectory))
    Protect-State ([IO.Path]::GetFullPath($StateDirectory))
    $lock = [IO.File]::Open((Join-Path $StateDirectory 'reconcile.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $statePath = Join-Path $StateDirectory 'managed.json'
    $previous = @()
    if (Test-Path -LiteralPath $statePath) {
        Assert-Configuration (-not ((Get-Item -LiteralPath $statePath).Attributes -band [IO.FileAttributes]::ReparsePoint) -and (Get-Item -LiteralPath $statePath).Length -le 1048576)
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable -Depth 8
        Assert-Configuration ($state.server -ceq $script:BaseUrl)
        $previous = @($state.organizations)
        # Whole-organization decommissioning is a separate explicit operation, not a silent orphan.
        foreach ($oldOrganization in $previous) { Assert-Configuration ($names.Contains($oldOrganization.name)) }
    }
    $managed = @()
    $internalMembers = @(Invoke-Grafana GET '/api/orgs/1/users' $basic)
    foreach ($plan in $plans) {
        $organization = $plan.Organization
        foreach ($member in $organization.members) {
            Assert-Configuration (@($internalMembers | Where-Object { $_.login -ieq $member.login -or $_.email -ieq $member.login }).Count -eq 0)
        }
    }
    foreach ($plan in $plans) {
        $organization = $plan.Organization
        $found = Invoke-Grafana GET ('/api/orgs/name/' + [uri]::EscapeDataString($organization.name)) $basic -AllowMissing
        if ($null -eq $found) { $found = Invoke-Grafana POST '/api/orgs' $basic -Body @{name = $organization.name}; $orgId = [int]$found.orgId }
        else { $orgId = [int]$found.id }
        Assert-Configuration ($orgId -gt 1)
        $prior = @($previous | Where-Object { $_.name -ceq $organization.name })
        Assert-Configuration ($prior.Count -le 1 -and ($prior.Count -eq 0 -or [int]$prior[0].id -eq $orgId))
        $current = Invoke-Grafana GET '/api/org' $basic $orgId
        Assert-Configuration ([int]$current.id -eq $orgId)
        $members = @(Invoke-Grafana GET "/api/orgs/$orgId/users" $basic)
        if ($prior.Count -eq 1) {
            foreach ($oldMember in $prior[0].members) {
                if (@($organization.members | Where-Object { $_.login -ieq $oldMember }).Count -eq 0) {
                    foreach ($entry in @($members | Where-Object { $_.login -ieq $oldMember -or $_.email -ieq $oldMember })) {
                        $null = Invoke-Grafana DELETE "/api/orgs/$orgId/users/$($entry.userId)" $basic
                    }
                }
            }
        }
        foreach ($member in $organization.members) {
            $matches = @($members | Where-Object { $_.login -ieq $member.login -or $_.email -ieq $member.login })
            Assert-Configuration ($matches.Count -le 1)
            if ($matches.Count -eq 0) { $null = Invoke-Grafana POST "/api/orgs/$orgId/users" $basic -Body @{loginOrEmail = $member.login; role = 'Viewer'} }
            elseif ($matches[0].role -cne 'Viewer') { $null = Invoke-Grafana PATCH "/api/orgs/$orgId/users/$($matches[0].userId)" $basic -Body @{role = 'Viewer'} }
        }
        # Basic is used only for server reconciliation and bootstrap of the dedicated organization-local account/token.
        $search = Invoke-Grafana GET ('/api/serviceaccounts/search?perpage=100&page=1&query=' + [uri]::EscapeDataString($organization.serviceAccount.name)) $basic $orgId
        Assert-Configuration ([int]$search.totalCount -le 100)
        $accounts = @($search.serviceAccounts | Where-Object { $_.name -ceq $organization.serviceAccount.name })
        Assert-Configuration ($accounts.Count -le 1)
        if ($accounts.Count -eq 0) { $account = Invoke-Grafana POST '/api/serviceaccounts' $basic $orgId @{name = $organization.serviceAccount.name; role = 'Admin'; isDisabled = $false} }
        else { $account = $accounts[0] }
        Assert-Configuration ($account.role -ceq 'Admin' -and [int]$account.orgId -eq $orgId -and -not $account.isDisabled)
        $accountId = [int]$account.id
        $oldTokens = @(Invoke-Grafana GET "/api/serviceaccounts/$accountId/tokens" $basic $orgId)
        $issued = Invoke-Grafana POST "/api/serviceaccounts/$accountId/tokens" $basic $orgId @{name = ('centralmobilidade-' + [guid]::NewGuid().ToString('N')); secondsToLive = 3600}
        $tokenId = [int]$issued.id
        $completed = $false
        try {
            Assert-Configuration (-not [string]::IsNullOrWhiteSpace($issued.key) -and $secretSet.Add($issued.key))
            $bearer = 'Bearer ' + $issued.key
            $current = Invoke-Grafana GET '/api/org' $bearer $orgId
            Assert-Configuration ([int]$current.id -eq $orgId)
            $sources = @(Invoke-Grafana GET '/api/datasources' $bearer $orgId)
            foreach ($source in $sources) { Assert-Configuration ($source.type -ceq 'yesoreyeram-infinity-datasource' -and $source.uid -ceq $organization.datasourceUid) }
            $body = @{uid = $organization.datasourceUid; name = $organization.datasourceUid; type = 'yesoreyeram-infinity-datasource'; access = 'proxy'; isDefault = $true
                jsonData = @{auth_method = 'bearerToken'; url = $plan.ApiUrl; allowedHosts = @($plan.ApiUrl); customHealthCheckEnabled = $true; customHealthCheckUrl = $plan.ApiUrl + '/health/ready'}
                secureJsonData = @{bearerToken = $plan.Secret}}
            $existing = Invoke-Grafana GET ('/api/datasources/uid/' + $organization.datasourceUid) $bearer $orgId -AllowMissing
            if ($null -eq $existing) { $null = Invoke-Grafana POST '/api/datasources' $bearer $orgId $body }
            else { $null = Invoke-Grafana PUT ('/api/datasources/uid/' + $organization.datasourceUid) $bearer $orgId $body }
            $sourceHealth = Invoke-Grafana GET ('/api/datasources/uid/' + $organization.datasourceUid + '/health') $bearer $orgId
            Assert-Configuration ($sourceHealth.status -ceq 'OK')
            foreach ($dashboard in $plan.Dashboards) {
                $dashboard.id = $null
                $null = Invoke-Grafana POST '/api/dashboards/db' $bearer $orgId @{dashboard = $dashboard; overwrite = $true; message = 'Managed presentation dashboard'}
            }
            foreach ($old in $oldTokens) { $null = Invoke-Grafana DELETE "/api/serviceaccounts/$accountId/tokens/$($old.id)" $basic $orgId }
            $completed = $true
            $managed += @{name = $organization.name; id = $orgId; members = @($organization.members | ForEach-Object { $_.login })}
        } finally {
            if (-not $completed -and $tokenId -gt 0) { try { $null = Invoke-Grafana DELETE "/api/serviceaccounts/$accountId/tokens/$tokenId" $basic $orgId } catch { } }
            $issued = $null; $bearer = $null
        }
    }
    $metadata = @{server = $script:BaseUrl; organizations = $managed} | ConvertTo-Json -Depth 8 -Compress
    $temporary = $statePath + '.tmp'
    Assert-Configuration (-not (Test-Path -LiteralPath $temporary))
    [IO.File]::WriteAllText($temporary, $metadata, [Text.UTF8Encoding]::new($false))
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($temporary, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) }
    [IO.File]::Move($temporary, $statePath, $true)
    Write-Output 'Grafana organizations reconciled.'
} catch {
    # Never emit request bodies, configuration, exception strings or server diagnostics.
    Write-Output ('Grafana reconciliation failed at script line ' + $_.InvocationInfo.ScriptLineNumber + '. Check private configuration, readiness and access.')
    exit 1
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    if ($null -ne $client) { $client.Dispose() }
}
