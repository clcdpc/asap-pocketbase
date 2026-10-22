[CmdletBinding()]
param(
    [string]$Commit = 'HEAD',

    [ValidateRange(1, 65535)]
    [int]$Port = 18080,

    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (& git -C $scriptRoot rev-parse --show-toplevel 2>$null).Trim()
if (-not $repoRoot) {
    throw 'Run this script from a clone of the ASAP repository.'
}

$launcher = Join-Path $repoRoot 'scripts/review/Invoke-AsapReview.ps1'
$tlsInitializer = Join-Path $repoRoot 'scripts/review/Initialize-AsapReviewTls.ps1'
$statePath = Join-Path $repoRoot '.review/current.json'
if (-not (Test-Path -LiteralPath $launcher)) {
    throw "ASAP review launcher was not found: $launcher"
}
if (-not (Test-Path -LiteralPath $tlsInitializer)) {
    throw "ASAP review TLS initializer was not found: $tlsInitializer"
}
if (Test-Path -LiteralPath $statePath) {
    throw 'A review environment is already recorded. Clean it before running the smoke test.'
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("asap-review-smoke-" + [guid]::NewGuid().ToString('N'))
$configPath = Join-Path $tempRoot 'review.smoke.json'
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

@{
    Entra = @{
        ClientId = '11111111-1111-1111-1111-111111111111'
        AllowedTenantIds = @('22222222-2222-2222-2222-222222222222')
        InitialSuperAdminEmail = 'reviewer@example.org'
        InitialSuperAdminDisplayName = 'ASAP Smoke Test'
    }
    Seed = 'Review'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM

$started = $false
$tlsInitialized = $false
try {
    & pwsh -NoProfile -File $tlsInitializer -Workspace $tempRoot -ReviewStatePath $statePath
    if ($LASTEXITCODE -ne 0) {
        throw "ASAP review smoke-test TLS initialization exited with code $LASTEXITCODE."
    }
    $tlsInitialized = $true
    $arguments = @(
        '-NoProfile',
        '-File', $launcher,
        '-Commit', $Commit,
        '-Config', $configPath,
        '-Port', [string]$Port,
        '-NoBrowser'
    )
    if ($Rebuild) {
        $arguments += '-Rebuild'
    }

    & pwsh @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ASAP review launcher exited with code $LASTEXITCODE."
    }
    $started = $true

    $ready = Invoke-RestMethod -Uri "https://localhost:$Port/health/ready" -TimeoutSec 5
    if ($ready.status -ne 'healthy') {
        throw "ASAP review readiness returned '$($ready.status)' instead of 'healthy'."
    }

    $tlsCertificatePath = Join-Path $tempRoot '.tls/localhost.pfx'
    $tlsHashBefore = (Get-FileHash -LiteralPath $tlsCertificatePath -Algorithm SHA256).Hash
    & pwsh -NoProfile -File $tlsInitializer -Workspace $tempRoot -ReviewStatePath $statePath -Force *> $null
    $rotationExitCode = $LASTEXITCODE
    if ($rotationExitCode -eq 0) {
        throw 'ASAP review TLS rotation was not blocked while the review environment was active.'
    }
    $tlsHashAfter = (Get-FileHash -LiteralPath $tlsCertificatePath -Algorithm SHA256).Hash
    if ($tlsHashAfter -ne $tlsHashBefore) {
        throw 'ASAP review TLS material changed even though active-environment rotation was blocked.'
    }

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $signInUrl = "https://localhost:$Port/api/asap/staff/sign-in?returnUrl=%2Fstaff%2F"
        $signInResponse = $client.GetAsync($signInUrl).GetAwaiter().GetResult()
        if ([int]$signInResponse.StatusCode -lt 300 -or [int]$signInResponse.StatusCode -ge 400) {
            throw "ASAP review OIDC challenge returned HTTP $([int]$signInResponse.StatusCode) instead of a redirect."
        }

        $location = $signInResponse.Headers.Location
        if ($null -eq $location) {
            throw 'ASAP review OIDC challenge did not return a Location header.'
        }

        $expectedRedirect = [Uri]::EscapeDataString("https://localhost:$Port/signin-oidc")
        if (-not $location.ToString().Contains("redirect_uri=$expectedRedirect", [StringComparison]::OrdinalIgnoreCase)) {
            throw "ASAP review OIDC challenge did not use the expected HTTPS callback. Location: $location"
        }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }

    $simulatedFailureMessage = 'Smoke-test simulated startup failure.'
    $recordedState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $recordedState.LifecycleState = 'Failed'
    $recordedState.LastError = $simulatedFailureMessage
    $recordedState | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8NoBOM

    & pwsh -NoProfile -File $launcher -Action Stop
    if ($LASTEXITCODE -ne 0) {
        throw "ASAP review stop recovery check exited with code $LASTEXITCODE."
    }

    $stoppedState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ([string]$stoppedState.LifecycleState -ne 'Stopped') {
        throw "ASAP review stop recovery left lifecycle state '$($stoppedState.LifecycleState)' instead of 'Stopped'."
    }
    if ([string]$stoppedState.LastError -ne $simulatedFailureMessage) {
        throw 'ASAP review stop recovery did not retain the failed-state LastError.'
    }

    & pwsh -NoProfile -File $tlsInitializer -Workspace $tempRoot -ReviewStatePath $statePath -Force
    if ($LASTEXITCODE -ne 0) {
        throw "ASAP review TLS rotation remained blocked after Stop exited with code $LASTEXITCODE."
    }
    $tlsHashAfterStop = (Get-FileHash -LiteralPath $tlsCertificatePath -Algorithm SHA256).Hash
    if ($tlsHashAfterStop -eq $tlsHashBefore) {
        throw 'ASAP review TLS material was not rotated after the environment transitioned to Stopped.'
    }

    Write-Host 'ASAP review Compose smoke test passed.'
    Write-Host "  Commit: $Commit"
    Write-Host "  Health: https://localhost:$Port/health/ready"
}
finally {
    if ($started -or (Test-Path -LiteralPath $statePath)) {
        & pwsh -NoProfile -File $launcher -Action Clean
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'Smoke-test cleanup failed. Run the review launcher with -Action Clean.'
        }
    }

    if ($tlsInitialized) {
        & pwsh -NoProfile -File $tlsInitializer -Workspace $tempRoot -Action Remove
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'Smoke-test localhost certificate cleanup failed.'
        }
    }

    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
