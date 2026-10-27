#requires -Version 7.0
<# Compatibility entry point; the main review launcher owns all orchestration. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Suite,
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [switch]$AllowReviewWrites,
    [switch]$ReadOnly,
    [switch]$CheckOnly,
    [string]$Config,
    [switch]$NoBrowser,
    [ValidateRange(10, 1800)][int]$AuthenticationTimeoutSeconds = 300
)
$forward = @{} + $PSBoundParameters
[void]$forward.Remove('ExpectedCommit')
$forward.Commit = $ExpectedCommit
& (Join-Path $PSScriptRoot 'Invoke-AsapReview.ps1') @forward
exit $LASTEXITCODE
