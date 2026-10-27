#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Suite,
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [switch]$AllowReviewWrites,
    [switch]$ReadOnly,
    [switch]$CheckOnly,
    [ValidateRange(10, 1800)][int]$AuthenticationTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Error 'Acceptance testing requires optional Node 24 and the pinned npm development dependencies. See tests/acceptance/README.md.'
    exit 2
}
$arguments = @('--use-system-ca', (Join-Path $repoRoot 'tests/acceptance/run.cjs'),
    '--suite', $Suite, '--expected-commit', $ExpectedCommit,
    '--auth-timeout-seconds', [string]$AuthenticationTimeoutSeconds)
if ($AllowReviewWrites) {
    $arguments += '--allow-review-writes'
}
if ($ReadOnly) {
    $arguments += '--read-only'
}
if ($CheckOnly) {
    $arguments += '--check-only'
}
& node @arguments
exit $LASTEXITCODE
