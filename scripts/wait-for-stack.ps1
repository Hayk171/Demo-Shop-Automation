<#
.SYNOPSIS
  Blocks until the whole Shop stack is ready to be tested.

.DESCRIPTION
  A cold `docker compose up` is not instant: MSSQL takes 20-30 seconds to accept connections,
  Product.Api and Customer.Api then run EF Core migrations and seed, Order.Api seeds Mongo, and
  Keycloak imports the realm. Starting the suite before all of that finishes produces a wall of
  connection errors that look like test failures.

  Polls each service's /health plus Keycloak's OpenID discovery document. A fixed `sleep` is not
  a substitute: it is either too short on a cold machine or wasted time on a warm one.

.EXAMPLE
  pwsh tests/scripts/wait-for-stack.ps1
  pwsh tests/scripts/wait-for-stack.ps1 -TimeoutSeconds 300
#>
[CmdletBinding()]
param(
    [string] $GatewayUrl  = 'http://localhost:5000',
    [string] $ProductUrl  = 'http://localhost:5001',
    [string] $CustomerUrl = 'http://localhost:5002',
    [string] $OrderUrl    = 'http://localhost:5003',
    [string] $KeycloakUrl = 'http://localhost:8081',
    [string] $Realm       = 'shop',
    [int]    $TimeoutSeconds = 180,
    [int]    $PollSeconds    = 3
)

$ErrorActionPreference = 'Stop'

$endpoints = @(
    @{ Name = 'gateway-api';  Url = "$GatewayUrl/health" }
    @{ Name = 'product-api';  Url = "$ProductUrl/health" }
    @{ Name = 'customer-api'; Url = "$CustomerUrl/health" }
    @{ Name = 'order-api';    Url = "$OrderUrl/health" }
    @{ Name = 'keycloak';     Url = "$KeycloakUrl/realms/$Realm/.well-known/openid-configuration" }
)

function Test-Endpoint {
    param([string] $Url)
    try {
        $response = Invoke-WebRequest -Uri $Url -Method Get -TimeoutSec 5 -SkipHttpErrorCheck
        return $response.StatusCode -eq 200
    } catch {
        return $false
    }
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$pending  = [System.Collections.Generic.List[object]]::new()
$endpoints | ForEach-Object { $pending.Add($_) }

Write-Host "Waiting up to $TimeoutSeconds s for the Shop stack..."

while ($pending.Count -gt 0) {
    if ((Get-Date) -gt $deadline) {
        $names = ($pending | ForEach-Object { $_.Name }) -join ', '
        Write-Host ''
        Write-Error "Timed out after $TimeoutSeconds s. Still not ready: $names"
        Write-Host 'Try: docker compose ps   and   docker compose logs --tail=50 <service>'
        exit 1
    }

    $ready = $pending | Where-Object { Test-Endpoint -Url $_.Url }

    foreach ($item in $ready) {
        Write-Host "  ready: $($item.Name)"
        $pending.Remove($item) | Out-Null
    }

    if ($pending.Count -gt 0) {
        Start-Sleep -Seconds $PollSeconds
    }
}

Write-Host 'Stack is ready.'
exit 0
