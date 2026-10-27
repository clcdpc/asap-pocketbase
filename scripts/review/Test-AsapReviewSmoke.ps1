#requires -Version 7.0
<# Review-only Compose smoke check; no Entra credentials, host TLS trust or Node. #>
[CmdletBinding()]
param([string]$Commit = 'HEAD', [ValidateRange(1, 65535)][int]$Port = 18080, [switch]$Rebuild)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$launcher = Join-Path $PSScriptRoot 'Invoke-AsapReview.ps1'
$statePath = Join-Path $repoRoot '.review/current.json'
if (Test-Path -LiteralPath $statePath) { throw 'A review is recorded. Preserve or explicitly clean it before running the smoke check.' }
. (Join-Path $PSScriptRoot 'Review-Environment.ps1')
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ('asap-review-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$configPath = Join-Path $temporary 'review.json'
@{ Entra = @{ ClientId = '11111111-1111-1111-1111-111111111111'; AllowedTenantIds = @('22222222-2222-2222-2222-222222222222')
    InitialSuperAdminEmail = 'reviewer@example.org'; InitialSuperAdminDisplayName = 'ASAP Smoke Test' }; Seed = 'Review' } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM
try {
    $arguments = @('-NoProfile', '-File', $launcher, '-Commit', $Commit, '-Config', $configPath, '-Port', [string]$Port, '-NoBrowser')
    if ($Rebuild) { $arguments += '-Rebuild' }
    & pwsh @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Review-only startup failed.' }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $containers = @(Invoke-Docker @('ps', '-aq', '--filter', "label=com.docker.compose.project=$($state.ProjectName)") -Capture) -split '\s+'
    if ($containers.Count -ne 3) { throw 'Review-only launch must contain exactly sql, db-init and web.' }
    $webId = Invoke-Docker @('ps', '-q', '--filter', "label=com.docker.compose.project=$($state.ProjectName)", '--filter', 'label=com.docker.compose.service=web') -Capture
    Invoke-Docker @('exec', $webId, 'dotnet', '/review/bootstrap/Asap.ReviewBootstrap.dll', '--health')
    Invoke-Docker @('exec', $webId, 'dotnet', '/review/bootstrap/Asap.ReviewBootstrap.dll', '--challenge', [string]$Port)
    foreach ($action in @('Status', 'Stop')) {
        & pwsh -NoProfile -File $launcher -Action $action
        if ($LASTEXITCODE -ne 0) { throw "Review $action failed." }
    }
    Write-Host 'Review-only Compose startup, private SQL, trusted HTTPS, OIDC challenge and Status/Stop passed.'
    Write-Host 'Real Entra form POST and acceptance viewer remain separate live checks.'
}
finally {
    if (Test-Path -LiteralPath $statePath) { & pwsh -NoProfile -File $launcher -Action Clean }
    $target = [System.IO.Path]::GetFullPath($temporary)
    if ($target.StartsWith([System.IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $target) -like 'asap-review-smoke-*') {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
