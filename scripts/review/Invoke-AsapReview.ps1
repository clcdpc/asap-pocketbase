#requires -Version 7.0
<#
.SYNOPSIS
Run selected acceptance suites sequentially in fresh disposable WSLC containers.
.PARAMETER Suite
Suite names from the local tooling registry, in execution order. Defaults to smoke.
Use native arrays when invoking directly from PowerShell 7; comma syntax is not
portable across arbitrary shell or pwsh -File argument boundaries.
.PARAMETER AllowReviewWrites
Deprecated compatibility switch. Synthetic writes are permitted by default.
.PARAMETER ReadOnly
Prevent mutations. Required scenarios skipped by this restriction make acceptance incomplete (exit 2).
.PARAMETER ArtifactsRoot
Absolute output root, defaulting to the user's profile under ASAP-Acceptance.
Each suite retains a flat pr-N_suite_local-timestamp or commit-SHA_suite_local-timestamp directory.
.EXAMPLE
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite s1,d1,q1
.EXAMPLE
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite @('s1', 'd1', 'q1')
.EXAMPLE
.\scripts\review\Invoke-AsapReview.ps1 -Commit HEAD -Suite smoke -ReadOnly
.NOTES
Requires PowerShell 7, Git and working WSLC (WSL 2.9.3+). Dependencies run in
the acceptance image. Application code is archived, never executed on the host.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 2147483647)][int]$Pr,
    [string]$Commit,
    [string[]]$Suite = @('smoke'),
    [switch]$AllowReviewWrites,
    [switch]$ReadOnly,
    [switch]$Rebuild,
    [string]$ArtifactsRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Review-Environment.ps1')

function Save-ReviewSuiteReport {
    param($Run, $Execution, [string]$Blocker)
    $file = Join-Path $Run.ArtifactPath 'results.json'
    $report = $null
    if (Test-Path -LiteralPath $file) {
        try {
            $report = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
            if ($report -isnot [System.Collections.IDictionary]) { throw 'Incomplete results document.' }
        }
        catch {
            $report = $null
            Copy-Item -LiteralPath $file -Destination (Join-Path $Run.ArtifactPath 'results-incomplete.json')
        }
    }
    if (-not $report) {
        $report = @{ schemaVersion = 1; applicationCommit = $null; authentication = @{ mode = 'local'; status = 'NOT RUN' }; scenarios = @() }
    }
    if ($Blocker) {
        $report.environment = @{ status = 'BLOCKED'; detail = $Blocker }
        $report.automatedFunctionalAcceptance = 'BLOCKED'
        $report.actualExitCode = 2
        $report.scenarios = @($report.scenarios | Where-Object id -ne 'launcher') + @(
            @{ id = 'launcher'; requirements = @('Acceptance startup and lifecycle'); category = 'functional';
                status = 'BLOCKED'; executed = $false; detail = $Blocker })
    }
    $report.suite = $Run.Suite
    $report.expectedApplicationCommit = $Execution.ApplicationCommit
    if (-not $report.Contains('toolingCommit')) { $report.toolingCommit = $Execution.ToolingCommit }
    $report.expectedToolingCommit = $Execution.ToolingCommit
    $report.toolingDirty = $Execution.ToolingDirty
    $report.toolingDigest = $Execution.ToolingDigest
    $report.toolingVersion = $Execution.ToolingVersion
    $report.acceptanceImage = $Execution.Image
    $report.invocationId = $Execution.InvocationId
    $report.runId = $Run.RunId
    $report.containerName = $Run.ContainerName
    $report.requestedPullRequest = $Pr
    $report.mode = if ($ReadOnly) { 'read-only' } else { 'mutating' }
    $report.startedUtc = $Run.Started.ToUniversalTime().ToString('O')
    $report.startedLocal = $Run.Started.ToString('O')
    $report.finishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $report.finishedLocal = [DateTimeOffset]::Now.ToString('O')
    $report.durationSeconds = $Run.Duration.TotalSeconds
    $report.launcherStatus = $Run.Status
    $report.overallAcceptance = 'INCOMPLETE'
    $report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $file -Encoding utf8NoBOM
    $markdown = Join-Path $Run.ArtifactPath 'report.md'
    if (-not (Test-Path -LiteralPath $markdown)) {
        "# ASAP acceptance: $($Run.Suite)" | Set-Content -LiteralPath $markdown -Encoding utf8NoBOM
    }
    @('', '## Launcher execution', '', "Requested application: $($Execution.ApplicationCommit)",
        "Tooling: $($Execution.ToolingCommit); version $($Execution.ToolingVersion); digest $($Execution.ToolingDigest)",
        "Mode: $($report.mode)", "Started (host local): $($report.startedLocal)", "Started (UTC): $($report.startedUtc)",
        "Container: $($Run.ContainerName)", "Outcome: **$($Run.Status)**", $Blocker,
        'Human visual/screen-reader review: REQUIRED; overall acceptance: INCOMPLETE.') |
        Add-Content -LiteralPath $markdown -Encoding utf8NoBOM
}

function Invoke-ReviewSuite {
    param($Run, $Execution)
    $Run.Started = [DateTimeOffset]::Now
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $Run.RunId = [guid]::NewGuid().ToString('N').Substring(0, 24)
    $Run.ContainerName = "asap-acceptance-$($Run.RunId)"
    $Run.Status = 'BLOCKED'
    $containerAttempted = $false
    $completed = $false
    $blocker = $null
    try {
        $Run.ArtifactPath = New-ReviewArtifactDirectory -ArtifactsRoot $Execution.ArtifactsRoot -PullRequest $Pr `
            -ApplicationCommit $Execution.ApplicationCommit -Suite $Run.Suite -Timestamp $Run.Started
        Write-Host "Artifacts: $($Run.ArtifactPath)"
        Write-Host "Container: $($Run.ContainerName)"
        Save-ReviewSuiteReport $Run $Execution 'Environment startup has not completed; browser scenarios have not run.'
        if (Test-Path -LiteralPath $Execution.BuildLog) {
            Copy-Item -LiteralPath $Execution.BuildLog -Destination (Join-Path $Run.ArtifactPath 'build.log')
        }
        $arguments = @('--suite', $Run.Suite, '--expected-commit', $Execution.ApplicationCommit)
        if ($ReadOnly) { $arguments += '--read-only' }
        # Only the evidence directory is mounted. No host repository, ports or persistent volumes.
        # Record intent first so interruption during creation still attempts cleanup.
        $containerAttempted = $true
        Invoke-ReviewNative wslc (@('run', '--detach', '--name', $Run.ContainerName, '--shm-size', '1g',
            '--mount', "type=bind,source=$($Run.ArtifactPath),target=/artifacts", $Execution.Image) + $arguments)
        $deadline = [DateTimeOffset]::UtcNow.AddMinutes(30)
        do {
            $observed = (Invoke-ReviewNative wslc @('inspect', '--format', 'json', $Run.ContainerName) -Capture).Output | ConvertFrom-Json
            $state = @($observed)[0].State
            if (-not $state.Running) { break }
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Acceptance exceeded the 30 minute runtime limit.' }
            Start-Sleep -Seconds 2
        } while ($true)
        $code = [int]$state.ExitCode
        if ($code -notin @(0, 1, 2)) { throw "Acceptance container terminated unexpectedly (exit $code)." }
        $report = Get-Content -LiteralPath (Join-Path $Run.ArtifactPath 'results.json') -Raw | ConvertFrom-Json -AsHashtable
        if (-not $report.ContainsKey('actualExitCode') -or $report.actualExitCode -ne $code) {
            throw 'Container exit and exported report disagree or diagnostics did not finish.'
        }
        if ($report.suite -ne $Run.Suite -or $report.expectedApplicationCommit -ne $Execution.ApplicationCommit -or
            $report.toolingCommit -ne $Execution.ToolingCommit -or
            ($code -ne 2 -and $report.applicationCommit -ne $Execution.ApplicationCommit)) {
            throw 'Container report does not match the selected application, tooling or suite.'
        }
        if ((Get-ReviewReportExitCode $report) -ne $code) {
            throw 'Container exit does not match its required automated checks; acceptance is incomplete.'
        }
        $Run.Status = switch ($code) { 0 { 'PASS' }; 1 { 'FAIL' }; 2 { 'BLOCKED' } }
        $completed = $true
    }
    catch {
        $blocker = $_.Exception.Message
        Write-Host "BLOCKED ($($Run.Suite)): $blocker" -ForegroundColor Red
    }
    finally {
        if ($containerAttempted) {
            # Always attempt removal, even after failed stop/log collection or Ctrl+C.
            try { $null = Invoke-ReviewNative wslc @('stop', '--time', '20', $Run.ContainerName) -Capture -AllowFailure }
            catch { Write-Warning "Could not stop $($Run.ContainerName) gracefully; forcing removal." }
            try {
                $logs = Invoke-ReviewNative wslc @('logs', $Run.ContainerName) -Capture -AllowFailure
                ($logs.Output + "`n" + $logs.ErrorText) | Set-Content -LiteralPath (Join-Path $Run.ArtifactPath 'container.log') -Encoding utf8NoBOM
            }
            catch { Write-Warning 'Container console log export failed; inspect the exported diagnostics.' }
            try { $null = Invoke-ReviewNative wslc @('remove', '--force', $Run.ContainerName) -Capture }
            catch {
                # Creation can fail before a container exists. A successful empty listing
                # confirms absence; a broken daemon or a remaining container cannot do so.
                $absent = $false
                try {
                    $remaining = Invoke-ReviewNative wslc @('list', '--all', '--filter', "name=$($Run.ContainerName)", '--quiet') -Capture -AllowFailure
                    $absent = $remaining.Code -eq 0 -and [string]::IsNullOrWhiteSpace($remaining.Output)
                }
                catch { $absent = $false }
                if (-not $absent) {
                    $Run.CleanupFailed = $true
                    $blocker = "Container cleanup failed. Run: wslc remove --force $($Run.ContainerName)."
                    Write-Warning $blocker
                }
            }
        }
        if (-not $completed -and -not $blocker) { $blocker = 'Acceptance launcher was interrupted.' }
        if ($blocker) { $Run.Status = 'BLOCKED'; $Run.Detail = $blocker }
        $clock.Stop()
        $Run.Duration = $clock.Elapsed
        if ($Run.ArtifactPath) {
            # Stop/export finishes before modifying the report, so late container writes cannot erase this outcome.
            try {
                Save-ReviewSuiteReport $Run $Execution $blocker
                Write-Host "Report: $(Join-Path $Run.ArtifactPath 'report.md')"
            }
            catch {
                $Run.Status = 'BLOCKED'
                $Run.Detail = "Could not finalize retained suite evidence: $($_.Exception.Message)"
                Write-Warning $Run.Detail
            }
        }
    }
}

$invocationId = [guid]::NewGuid().ToString('N')
$runs = @()
$context = $null
$buildRoot = $null
$sharedLog = $null
$sharedError = $null
$finished = $false
try {
    $repoRoot = (Invoke-ReviewNative git @('-C', $PSScriptRoot, 'rev-parse', '--show-toplevel') -Capture).Output
    $registered = @(Get-Content -LiteralPath (Join-Path $repoRoot 'tests/acceptance/suites/registry.json') -Raw | ConvertFrom-Json)
    $selected = @(Get-ReviewSuiteSelection -Suite $Suite -Registered $registered)
    $runs = @($selected | ForEach-Object {
        [pscustomobject]@{ Suite = $_; Status = 'NOT RUN'; Duration = [TimeSpan]::Zero; ArtifactPath = $null;
            Started = $null; RunId = $null; ContainerName = $null; CleanupFailed = $false; Detail = '' }
    })
    if ($Pr -and $Commit) { throw '-Pr and -Commit are mutually exclusive revision selectors.' }
    if ($Commit.StartsWith('-')) { throw '-Commit must be a Git revision, without Git command options.' }
    if ($ReadOnly -and $AllowReviewWrites) { throw '-ReadOnly and -AllowReviewWrites are mutually exclusive.' }
    if ($AllowReviewWrites) { Write-Warning '-AllowReviewWrites is deprecated; disposable synthetic writes are permitted by default.' }
    $rootOptions = @{}
    if ($PSBoundParameters.ContainsKey('ArtifactsRoot')) { $rootOptions.ArtifactsRoot = $ArtifactsRoot }
    $artifactRoot = Get-ReviewArtifactRoot @rootOptions
    $sharedLog = Join-Path $artifactRoot "invocation-$invocationId.log"
    $stream = [System.IO.File]::Open($sharedLog, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
    $stream.Dispose()
    Write-Host "Shared diagnostics: $sharedLog"
    if (-not (Get-Command wslc -ErrorAction SilentlyContinue)) { throw 'WSLC is unavailable. A working WSL 2.9.3+ installation is required.' }
    Invoke-ReviewNative wslc @('version')
    $resolved = Resolve-ApplicationCommit -Repository $repoRoot -PullRequest $Pr -Revision $Commit
    $toolingSha = (Invoke-ReviewNative git @('-C', $repoRoot, 'rev-parse', 'HEAD') -Capture).Output
    $toolingDirty = [bool](Invoke-ReviewNative git (@('-C', $repoRoot, 'status', '--porcelain', '--untracked-files=normal', '--') + (Get-ReviewToolingPaths)) -Capture).Output
    $buildRoot = Join-Path $repoRoot '.review/build'
    $context = Join-Path $buildRoot $invocationId
    $tooling = Join-Path $context 'tooling'
    Copy-ReviewTooling -Repository $repoRoot -Destination $tooling
    $digest = Get-ReviewToolingDigest $tooling
    $version = (Get-Content -LiteralPath (Join-Path $tooling 'dev/review/VERSION') -Raw).Trim()
    $image = "asap-acceptance:$resolved-$toolingSha-$($digest.Substring(0, 16))$(if ($toolingDirty) { '-dirty' })"
    @("Application: $resolved", "Tooling: $toolingSha; dirty: $toolingDirty; version: $version; digest: $digest",
        "Image: $image", "Suites: $($selected -join ', ')") | Add-Content -LiteralPath $sharedLog -Encoding utf8NoBOM
    Write-Host "Application: $resolved"
    Write-Host "Tooling: $toolingSha$(if ($toolingDirty) { ' (working tree changes)' }); digest $digest"
    $buildLog = Join-Path $artifactRoot "build-$invocationId.log"
    $cached = Invoke-ReviewNative wslc @('image', 'inspect', $image) -Capture -AllowFailure
    if ($Rebuild -or $cached.Code -ne 0) {
        $archive = Join-Path $context 'application.zip'
        Invoke-ReviewNative git @('-C', $repoRoot, 'archive', '--format=zip', "--output=$archive", $resolved)
        Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $context 'app')
        Remove-Item -LiteralPath $archive
        $build = @('build', '--file', (Join-Path $tooling 'dev/review/Containerfile'), '--tag', $image, '--progress', 'plain',
            '--build-arg', "REVIEW_COMMIT_SHA=$resolved", '--build-arg', "REVIEW_TOOLING_SHA=$toolingSha",
            '--build-arg', "REVIEW_TOOLING_DIRTY=$($toolingDirty.ToString().ToLowerInvariant())", '--build-arg', "REVIEW_TOOLING_VERSION=$version")
        if ($Rebuild) { $build += '--no-cache' }
        Write-Host "Building the shared acceptance image through WSLC... Diagnostics: $buildLog"
        Add-Content -LiteralPath $sharedLog -Value "Build diagnostics: $buildLog"
        $built = Invoke-ReviewNative wslc ($build + $context) -Capture -AllowFailure -TimeoutSeconds 2400 -LogPath $buildLog
        if ($built.Code -ne 0) { throw "Shared WSLC image build failed. See $buildLog. No suites started." }
    }
    else { Write-Host 'Reusing the shared acceptance image.' }
    Remove-ReviewBuildContext $context $buildRoot
    $execution = @{ ApplicationCommit = $resolved; ToolingCommit = $toolingSha; ToolingDirty = $toolingDirty;
        ToolingDigest = $digest; ToolingVersion = $version; Image = $image; InvocationId = $invocationId;
        ArtifactsRoot = $artifactRoot; BuildLog = $buildLog }
    foreach ($run in $runs) {
        Invoke-ReviewSuite $run $execution
        if ($run.CleanupFailed) { throw 'Container cleanup could not be confirmed; remaining suites will not start.' }
    }
    $finished = $true
}
catch {
    $sharedError = $_.Exception.Message
    Write-Host "BLOCKED: $sharedError" -ForegroundColor Red
}
finally {
    if ($context) {
        try { Remove-ReviewBuildContext $context $buildRoot }
        catch { $sharedError = "Build context cleanup failed: $($_.Exception.Message)"; Write-Warning $sharedError }
    }
    if (-not $finished -and -not $sharedError) { $sharedError = 'Acceptance invocation was interrupted.' }
    foreach ($run in $runs | Where-Object Status -eq 'NOT RUN') { $run.Detail = $sharedError }
    $counts = @{}
    foreach ($status in @('PASS', 'FAIL', 'BLOCKED', 'NOT RUN')) { $counts[$status] = @($runs | Where-Object Status -eq $status).Count }
    $exitCode = if ($sharedError -or $counts.BLOCKED -or $counts.'NOT RUN' -or -not $runs.Count) { 2 } elseif ($counts.FAIL) { 1 } else { 0 }
    $overall = switch ($exitCode) { 0 { 'PASS' }; 1 { 'FAIL' }; 2 { 'BLOCKED' } }
    if ($sharedLog) {
        try {
            if ($sharedError) { Add-Content -LiteralPath $sharedLog -Value "BLOCKED: $sharedError" -Encoding utf8NoBOM }
            @{ invocationId = $invocationId; overall = $overall; exitCode = $exitCode; counts = $counts; detail = $sharedError;
                suites = @($runs | Select-Object Suite, Status, @{ Name = 'durationSeconds'; Expression = { $_.Duration.TotalSeconds } }, ArtifactPath, Detail) } |
                ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ([System.IO.Path]::ChangeExtension($sharedLog, '.json')) -Encoding utf8NoBOM
            Write-Host "Shared diagnostics: $sharedLog"
        }
        catch { Write-Warning "Could not save invocation summary: $($_.Exception.Message)"; $exitCode = 2; $overall = 'BLOCKED' }
    }
    Write-Host ($runs | Select-Object Suite, Status, @{ Name = 'Duration'; Expression = { $_.Duration.ToString('hh\:mm\:ss') } } | Format-Table -AutoSize | Out-String)
    Write-Host "Overall: $overall; exit $exitCode"
    Write-Host "Passed: $($counts.PASS)`nFailed: $($counts.FAIL)`nBlocked: $($counts.BLOCKED)`nNot run: $($counts.'NOT RUN')"
    foreach ($run in $runs) { Write-Host "$($run.Suite): $(if ($run.ArtifactPath) { $run.ArtifactPath } else { 'Artifacts: unavailable (suite did not start)' })" }
    Write-Host 'Human visual/screen-reader review remains REQUIRED; overall acceptance INCOMPLETE.'
    # Ctrl+C unwinds through finally and skips statements after it.
    exit $exitCode
}
