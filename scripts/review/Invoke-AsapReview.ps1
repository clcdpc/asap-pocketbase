#requires -Version 7.0
<#
.SYNOPSIS
Run unattended acceptance in one disposable WSLC container.
.PARAMETER ArtifactsRoot
Optional absolute output root. Defaults to the current user's profile directory
under ASAP-Acceptance. Each run is retained in its own timestamp/run-ID directory.
.EXAMPLE
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite smoke -AllowReviewWrites
.EXAMPLE
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite pr410 -AllowReviewWrites -ArtifactsRoot 'D:\ASAP Acceptance'
.NOTES
Requires PowerShell 7, Git and working WSLC (WSL 2.9.3+). All application,
SQL, .NET, Node and browser dependencies run inside the acceptance container.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 2147483647)][int]$Pr,
    [string]$Commit,
    [string]$Suite = 'smoke',
    [switch]$AllowReviewWrites,
    [switch]$ReadOnly,
    [switch]$Rebuild,
    [string]$ArtifactsRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Review-Environment.ps1')
$repoRoot = (Invoke-ReviewNative git @('-C', $PSScriptRoot, 'rev-parse', '--show-toplevel') -Capture).Output
$toolingSha = (Invoke-ReviewNative git @('-C', $repoRoot, 'rev-parse', 'HEAD') -Capture).Output
$toolingDirty = [bool](Invoke-ReviewNative git (@('-C', $repoRoot, 'status', '--porcelain', '--untracked-files=normal', '--') + (Get-ReviewToolingPaths)) -Capture).Output
$runId = [guid]::NewGuid().ToString('N').Substring(0, 24)
$buildRoot = Join-Path $repoRoot '.review/build'
$context = Join-Path $buildRoot $runId
$containerName = "asap-acceptance-$runId"
$resolved = $null
$containerCreated = $false
$completed = $false
$launcherError = $null
$exitCode = 2
$artifactOptions = @{ RunId = $runId }
if ($PSBoundParameters.ContainsKey('ArtifactsRoot')) { $artifactOptions.ArtifactsRoot = $ArtifactsRoot }
try { $artifactPath = New-ReviewArtifactDirectory @artifactOptions }
catch {
    Write-Host "BLOCKED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Artifacts: unavailable; output directory could not be created or written.'
    Write-Host 'Automated: BLOCKED; exit 2'
    exit 2
}
Write-Host "Artifacts:   $artifactPath"

function Write-LauncherBlocker {
    param([string]$Detail)
    $reportFile = Join-Path $artifactPath 'results.json'
    $report = if (Test-Path -LiteralPath $reportFile) { Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json -AsHashtable } else {
        @{ schemaVersion = 1; suite = $Suite; expectedApplicationCommit = $resolved; applicationCommit = $null
            toolingCommit = $toolingSha; toolingDirty = $toolingDirty; startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            mode = if ($AllowReviewWrites) { 'mutating' } else { 'read-only' }
            authentication = @{ mode = 'local'; status = 'NOT RUN' }; scenarios = @() }
    }
    $report.environment = @{ status = 'BLOCKED'; detail = $Detail }
    $report.expectedApplicationCommit = $resolved
    $report.automatedFunctionalAcceptance = 'BLOCKED'
    $report.overallAcceptance = 'INCOMPLETE'
    $report.actualExitCode = 2
    $report.scenarios = @($report.scenarios | Where-Object id -ne 'launcher') + @(
        @{ id = 'launcher'; requirements = @('Acceptance startup and lifecycle'); category = 'functional'; status = 'BLOCKED'; executed = $false; detail = $Detail })
    $report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $reportFile -Encoding utf8NoBOM
    @("# ASAP acceptance: $Suite", '', "Requested application: $resolved", "Tooling: $toolingSha", '',
        "Automated: **BLOCKED** — $Detail", 'Human visual/screen-reader review: REQUIRED',
        'Overall acceptance: INCOMPLETE', '', 'See results.json and diagnostics for any completed browser checks.') |
        Set-Content -LiteralPath (Join-Path $artifactPath 'report.md') -Encoding utf8NoBOM
}

try {
    Write-LauncherBlocker 'Environment startup has not completed; browser scenarios have not run.'
    if ($Pr -and $Commit) { throw '-Pr and -Commit are mutually exclusive revision selectors.' }
    if ($Commit.StartsWith('-')) { throw '-Commit must be a Git revision, without Git command options.' }
    if ($ReadOnly -and $AllowReviewWrites) { throw '-ReadOnly and -AllowReviewWrites are mutually exclusive.' }
    $registered = @(Get-Content (Join-Path $repoRoot 'tests/acceptance/suites/registry.json') -Raw | ConvertFrom-Json)
    if ($Suite -notin $registered) { throw "Unknown suite. Registered acceptance suites: $($registered -join ', ')." }
    if (-not (Get-Command wslc -ErrorAction SilentlyContinue)) { throw 'WSLC is unavailable. A working WSL 2.9.3+ installation is required.' }
    Invoke-ReviewNative wslc @('version')
    $resolved = Resolve-ApplicationCommit -Repository $repoRoot -PullRequest $Pr -Revision $Commit
    Write-LauncherBlocker 'Building or selecting the acceptance image; browser scenarios have not run.'
    Write-Host "Application: $resolved"
    Write-Host "Tooling:     $toolingSha$(if ($toolingDirty) { ' (working tree changes)' })"
    $tooling = Join-Path $context 'tooling'
    Copy-ReviewTooling -Repository $repoRoot -Destination $tooling
    $digest = Get-ReviewToolingDigest $tooling
    $image = "asap-acceptance:$resolved-$toolingSha-$($digest.Substring(0, 16))$(if ($toolingDirty) { '-dirty' })"
    $cached = Invoke-ReviewNative wslc @('image', 'inspect', $image) -Capture -AllowFailure
    if ($Rebuild -or $cached.Code -ne 0) {
        $archive = Join-Path $context 'application.zip'
        Invoke-ReviewNative git @('-C', $repoRoot, 'archive', '--format=zip', "--output=$archive", $resolved)
        Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $context 'app')
        Remove-Item -LiteralPath $archive
        $version = (Get-Content (Join-Path $tooling 'dev/review/VERSION') -Raw).Trim()
        $build = @('build', '--file', (Join-Path $tooling 'dev/review/Containerfile'), '--tag', $image, '--progress', 'plain',
            '--build-arg', "REVIEW_COMMIT_SHA=$resolved", '--build-arg', "REVIEW_TOOLING_SHA=$toolingSha",
            '--build-arg', "REVIEW_TOOLING_DIRTY=$($toolingDirty.ToString().ToLowerInvariant())", '--build-arg', "REVIEW_TOOLING_VERSION=$version")
        if ($Rebuild) { $build += '--no-cache' }
        Write-Host 'Building the acceptance image through WSLC...'
        $built = Invoke-ReviewNative wslc ($build + $context) -Capture -AllowFailure -TimeoutSeconds 2400
        ($built.Output + "`n" + $built.ErrorText) | Set-Content -LiteralPath (Join-Path $artifactPath 'build.log') -Encoding utf8NoBOM
        if ($built.Code -ne 0) { throw 'WSLC image build failed. See build.log.' }
    }
    else { Write-Host 'Reusing the acceptance image.' }
    Remove-ReviewBuildContext $context $buildRoot
    $arguments = @('--suite', $Suite, '--expected-commit', $resolved)
    if ($AllowReviewWrites) { $arguments += '--allow-review-writes' } else { $arguments += '--read-only' }
    # One artifact mount, no published ports, repository mounts or persistent volumes.
    # Mark intent before run so interruption during container creation still cleans up.
    $containerCreated = $true
    Invoke-ReviewNative wslc (@('run', '--detach', '--name', $containerName, '--shm-size', '1g',
        '--mount', "type=bind,source=$artifactPath,target=/artifacts", $image) + $arguments)
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(30)
    do {
        $observed = (Invoke-ReviewNative wslc @('inspect', '--format', 'json', $containerName) -Capture).Output | ConvertFrom-Json
        $state = @($observed)[0].State
        if (-not $state.Running) { break }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Acceptance exceeded the 30 minute runtime limit.' }
        Start-Sleep -Seconds 2
    } while ($true)
    $exitCode = [int]$state.ExitCode
    if ($exitCode -notin @(0, 1, 2)) { throw "Acceptance container terminated unexpectedly (exit $exitCode)." }
    $results = Get-Content -LiteralPath (Join-Path $artifactPath 'results.json') -Raw | ConvertFrom-Json
    if (-not $results.PSObject.Properties['actualExitCode'] -or $results.actualExitCode -ne $exitCode) {
        throw 'Container exit and exported report disagree or diagnostics did not finish.'
    }
    if ($exitCode -eq 0 -and (-not $results.PSObject.Properties['requiredScenarioIds'] -or
        @($results.requiredScenarioIds).Count -eq 0 -or
        @($results.requiredScenarioIds | Where-Object { $id = $_; @($results.scenarios | Where-Object id -eq $id).Count -ne 1 }).Count -or
        $results.environment.status -ne 'PASS' -or $results.authentication.status -ne 'PASS' -or
        @($results.scenarios | Where-Object { $_.category -eq 'functional' -and $_.status -notin @('PASS', 'NOT RUN') }).Count)) {
        throw 'Container exited successfully without successful automated checks.'
    }
    $completed = $true
}
catch {
    $exitCode = 2
    $launcherError = $_.Exception.Message
    Write-LauncherBlocker $launcherError
    Write-Host "BLOCKED: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if ($containerCreated) {
        # Always attempt removal, even if stopping or collecting logs fails.
        try { $null = Invoke-ReviewNative wslc @('stop', '--time', '20', $containerName) -Capture -AllowFailure }
        catch { Write-Warning "Could not stop $containerName gracefully; forcing removal." }
        try {
            $logs = Invoke-ReviewNative wslc @('logs', $containerName) -Capture -AllowFailure
            ($logs.Output + "`n" + $logs.ErrorText) | Set-Content -LiteralPath (Join-Path $artifactPath 'container.log') -Encoding utf8NoBOM
        }
        catch { Write-Warning 'Container console log export failed; inspect the exported diagnostics.' }
        try { $null = Invoke-ReviewNative wslc @('remove', '--force', $containerName) -Capture }
        catch {
            $exitCode = 2
            $launcherError = "Container cleanup failed. Remove $containerName through WSLC."
            Write-LauncherBlocker $launcherError
            Write-Warning "Container cleanup failed: $containerName"
        }
    }
    Remove-ReviewBuildContext $context $buildRoot
    if (-not $completed) {
        $exitCode = 2
        Write-LauncherBlocker $(if ($launcherError) { $launcherError } else { 'Acceptance launcher was interrupted.' })
    }
    $summary = Get-Content -LiteralPath (Join-Path $artifactPath 'results.json') -Raw | ConvertFrom-Json
    Write-Host "Automated: $($summary.automatedFunctionalAcceptance); exit $exitCode"
    foreach ($status in @('PASS', 'FAIL', 'BLOCKED', 'NOT RUN')) {
        Write-Host "$status`: $(@($summary.scenarios | Where-Object { $_.category -eq 'functional' -and $_.status -eq $status }).Count)"
    }
    Write-Host "Artifacts: $artifactPath"
    Write-Host "Report: $(Join-Path $artifactPath 'report.md')"
    Write-Host 'Human visual/screen-reader review remains REQUIRED; overall acceptance INCOMPLETE.'
    # Ctrl+C unwinds through finally and skips statements after it.
    exit $exitCode
}
