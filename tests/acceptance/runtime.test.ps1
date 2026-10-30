# Opt-in real WSLC failure checks against a built acceptance image.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Image,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$ApplicationSha
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
. (Join-Path $repository 'scripts/review/Review-Environment.ps1')
foreach ($case in @('assertion', 'initialization', 'termination', 'child-exit')) {
    $runId = [guid]::NewGuid().ToString('N').Substring(0, 24)
    $name = "asap-acceptance-$runId"
    $directory = New-ReviewArtifactDirectory -RunId $runId
    $setup = switch ($case) {
        'assertion' { @'
cat >> /runner/tests/acceptance/suites/smoke.cjs <<'NODE'
module.exports.scenarios[0].run = async () => require('node:assert/strict').fail('Intentional runtime verification failure');
NODE
exec /review/acceptance-entrypoint.sh "$@"
'@ }
        'initialization' { 'rm /app/Database/Asap.Database.dacpac; exec /review/acceptance-entrypoint.sh "$@"' }
        default { 'exec /review/acceptance-entrypoint.sh "$@"' }
    }
    try {
        $null = Invoke-ReviewNative wslc @('run', '--detach', '--name', $name, '--shm-size', '1g',
            '--mount', "type=bind,source=$directory,target=/artifacts",
            '--entrypoint', '/bin/bash', $Image, '-c', $setup.Replace("`r`n", "`n"), '--',
            '--suite', 'smoke', '--expected-commit', $ApplicationSha, '--read-only') -Capture
        $deadline = [DateTimeOffset]::UtcNow.AddMinutes(6)
        $interrupted = $false
        do {
            $observed = (Invoke-ReviewNative wslc @('inspect', '--format', 'json', $name) -Capture).Output | ConvertFrom-Json
            $state = @($observed)[0].State
            if (-not $state.Running) { break }
            if ($case -in @('termination', 'child-exit') -and -not $interrupted) {
                $logs = (Invoke-ReviewNative wslc @('logs', $name) -Capture).Output
                if ($logs -match 'Deploying DACPAC') {
                    if ($case -eq 'termination') {
                        $null = Invoke-ReviewNative wslc @('stop', '--time', '20', $name) -Capture
                    }
                    else {
                        $null = Invoke-ReviewNative wslc @('exec', $name, 'pkill', '-KILL', '-x', 'sqlservr') -Capture
                    }
                    $interrupted = $true
                }
            }
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw "$case timed out." }
            Start-Sleep -Seconds 1
        } while ($true)
        $expected = if ($case -eq 'assertion') { 1 } else { 2 }
        if ($state.ExitCode -ne $expected) { throw "$case returned $($state.ExitCode), expected $expected." }
        $report = Get-Content (Join-Path $directory 'results.json') -Raw | ConvertFrom-Json
        if ($report.actualExitCode -ne $expected) { throw "$case report lost the actual exit code." }
        $status = if ($case -eq 'assertion') { 'FAIL' } else { 'BLOCKED' }
        if ($report.automatedFunctionalAcceptance -ne $status) { throw "$case falsely reported automated success." }
        if (-not (Test-Path (Join-Path $directory 'diagnostics/sql.log'))) { throw "$case did not export SQL diagnostics." }
        if ($case -eq 'assertion' -and -not (Test-Path (Join-Path $directory 'smoke-authenticated-review-failure-trace.json'))) {
            throw 'Assertion failure trace was not exported.'
        }
        if ($case -eq 'initialization' -and -not (Test-Path (Join-Path $directory 'diagnostics/initializer.log'))) {
            throw 'Initialization diagnostics were not exported.'
        }
        Write-Host "PASS: $case (actual exit $expected; $status); $directory"
    }
    finally {
        $null = Invoke-ReviewNative wslc @('remove', '--force', $name) -Capture
        $removed = Invoke-ReviewNative wslc @('inspect', $name) -Capture -AllowFailure
        if ($removed.Code -eq 0) { throw "Container cleanup failed: $name" }
    }
}
