<#
.SYNOPSIS
  Waits for the Shop stack to be reachable, then runs the suite.

.DESCRIPTION
  This repository is standalone: it knows the system under test only by its base URLs, so by
  default it starts nothing. Bring the stack up however you normally do, then run this.

  -ComposePath is a convenience for the common local case: point it at a checkout containing a
  docker-compose.yml and this script runs `docker compose up -d` there first. Opt-in and empty
  by default, so nothing here depends on that repository existing.

.EXAMPLE
  pwsh scripts/run-tests.ps1
  pwsh scripts/run-tests.ps1 -Filter "TestCategory=auth"
  pwsh scripts/run-tests.ps1 -ComposePath ..\shop-demo -TearDown
  pwsh scripts/run-tests.ps1 -GatewayUrl https://shop-staging.example.com
#>
[CmdletBinding()]
param(
    # known-issue tests document current defects on purpose, so they are out of the default run.
    [string] $Filter        = 'TestCategory!=known-issue',
    [string] $Configuration = 'Debug',
    [int]    $TimeoutSeconds = 180,

    [string] $ComposePath = '',
    [switch] $TearDown,

    [string] $GatewayUrl  = '',
    [string] $ProductUrl  = '',
    [string] $CustomerUrl = '',
    [string] $OrderUrl    = '',
    [string] $KeycloakUrl = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot

try {
    # Any URL passed here overrides appsettings.json through the SHOP_TEST_ configuration
    # provider, and is handed to the readiness poller so both agree on what they wait for.
    $waitArgs = @{ TimeoutSeconds = $TimeoutSeconds }

    if ($GatewayUrl)  { $env:SHOP_TEST_Services__Gateway  = $GatewayUrl;  $waitArgs.GatewayUrl  = $GatewayUrl }
    if ($ProductUrl)  { $env:SHOP_TEST_Services__Product  = $ProductUrl;  $waitArgs.ProductUrl  = $ProductUrl }
    if ($CustomerUrl) { $env:SHOP_TEST_Services__Customer = $CustomerUrl; $waitArgs.CustomerUrl = $CustomerUrl }
    if ($OrderUrl)    { $env:SHOP_TEST_Services__Order    = $OrderUrl;    $waitArgs.OrderUrl    = $OrderUrl }
    if ($KeycloakUrl) { $env:SHOP_TEST_Keycloak__BaseUrl  = $KeycloakUrl; $waitArgs.KeycloakUrl = $KeycloakUrl }

    if ($ComposePath) {
        $composeDir = (Resolve-Path $ComposePath).Path
        Write-Host "==> docker compose up -d  (in $composeDir)"
        Push-Location $composeDir
        try {
            docker compose up --build -d
            if ($LASTEXITCODE -ne 0) { throw "docker compose up failed with exit code $LASTEXITCODE" }
        }
        finally { Pop-Location }
    }

    Write-Host '==> waiting for the stack'
    & (Join-Path $PSScriptRoot 'wait-for-stack.ps1') @waitArgs
    if ($LASTEXITCODE -ne 0) { throw 'the stack did not become ready' }

    $artifacts = Join-Path $repoRoot 'artifacts'
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

    Write-Host "==> dotnet test (filter: $Filter)"
    dotnet test Shop.Tests/Shop.Tests.csproj `
        --configuration $Configuration `
        --filter $Filter `
        --logger "trx;LogFileName=tests.trx" `
        --logger "console;verbosity=normal" `
        --results-directory $artifacts

    $testExitCode = $LASTEXITCODE
    Write-Host "==> results in $artifacts"
    exit $testExitCode
}
finally {
    if ($TearDown -and $ComposePath) {
        Write-Host '==> docker compose down -v'
        Push-Location (Resolve-Path $ComposePath).Path
        try { docker compose down -v } finally { Pop-Location }
    }
    Pop-Location
}
