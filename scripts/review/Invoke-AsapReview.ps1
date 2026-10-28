#requires -Version 7.0
<#
.SYNOPSIS
Start a Docker review, or run an unattended containerized acceptance suite.
.EXAMPLE
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite smoke -AllowReviewWrites
.EXAMPLE
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite smoke -Auth Entra -AllowReviewWrites
.NOTES
Requires Docker Desktop (Linux x64), Compose v2, Git and PowerShell 7.
Suites default to Local authentication with a synthetic SQL-backed reviewer;
no Entra configuration or interaction is required. -ShowBrowser opens the viewer.
Manual review defaults to Entra. -Auth Entra uses the external configuration in
~/ASAP-Review/review.local.json and opens the viewer for real sign-in/MFA.
Acceptance uses fresh volumes and container TLS trust. No host Node, Chromium,
.NET SDK or certificate trust is needed. Local mode does not validate OIDC/MFA.
Lifecycle commands target the last environment; -Project selects a retained one.
#>
[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Clean', 'Status')][string]$Action = 'Start',
    [ValidateRange(1, 2147483647)][int]$Pr,
    [string]$Commit,
    [string]$Suite,
    [ValidateSet('Local', 'Entra')][string]$Auth,
    [switch]$ShowBrowser,
    [switch]$AllowReviewWrites,
    [switch]$ReadOnly,
    [switch]$CheckOnly,
    [ValidateRange(10, 1800)][int]$AuthenticationTimeoutSeconds = 300,
    [string]$Config,
    [ValidateRange(1, 65535)][int]$Port = 8080,
    [switch]$KeepData,
    [switch]$Rebuild,
    [switch]$NoBrowser,
    [switch]$TrustReviewCertificate,
    [string]$Project
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$reviewRoot = Join-Path $repoRoot '.review'
$statePath = Join-Path $reviewRoot 'current.json'
$composeFile = Join-Path $repoRoot 'dev/review/docker/compose.yml'
$toolingVersion = (Get-Content (Join-Path $repoRoot 'dev/review/VERSION') -Raw).Trim()
. (Join-Path $PSScriptRoot 'Review-Environment.ps1')
$Auth = Resolve-ReviewAuthentication $Auth $Suite

function Assert-ManagedPath {
    param([string]$Path)
    $absolute = [System.IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith([System.IO.Path]::GetFullPath($reviewRoot) + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) { throw 'Review path is outside this checkout .review directory.' }
    return $absolute
}
function Read-State {
    if ($Project) {
        if ($Project -notmatch '^asap-(review-[a-f0-9]{12}|acceptance-[a-f0-9]{24})$') { throw 'Invalid managed project name.' }
        $matches = @(Get-ChildItem -LiteralPath $reviewRoot -Recurse -Filter state.json -File | Where-Object {
            (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).ProjectName -eq $Project
        })
        if ($matches.Count -ne 1) { throw 'Selected review project is missing or ambiguous.' }
        return Complete-State (Get-Content -LiteralPath $matches[0].FullName -Raw | ConvertFrom-Json -AsHashtable)
    }
    if (Test-Path -LiteralPath $statePath) { return Complete-State (Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable) }
    return $null
}
function Complete-State {
    param($State)
    foreach ($key in @('Kind', 'ToolingSha', 'ToolingDirty', 'ViewerPort', 'ArtifactPath', 'RunId', 'LastError', 'InputConfigPath')) {
        if (-not $State.Contains($key)) { $State[$key] = $null }
    }
    if (-not $State.Kind) { $State.Kind = 'Manual' }
    if (-not $State.Contains('Auth')) { $State.Auth = 'Entra' }
    if (-not $State.Contains('LifecycleState')) { $State.LifecycleState = 'Ready' }
    return $State
}
function Write-State {
    param($State)
    $directory = Assert-ManagedPath $State.StatePath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $json = $State | ConvertTo-Json -Depth 10
    $json | Set-Content -LiteralPath (Join-Path $directory 'state.json') -Encoding utf8NoBOM
    $json | Set-Content -LiteralPath $statePath -Encoding utf8NoBOM
}
function Assert-StateIdentity {
    param($State)
    if ($State.Sha -notmatch '^[a-f0-9]{40}$' -or $State.ShortSha -ne $State.Sha.Substring(0, 12)) { throw 'Invalid recorded review SHA.' }
    $expectedProject = if ($State.Kind -eq 'Acceptance' -and $State.RunId -match '^[a-f0-9]{24}$') { "asap-acceptance-$($State.RunId)" } else { "asap-review-$($State.ShortSha)" }
    $expectedPath = if ($State.Kind -eq 'Acceptance') { Join-Path $reviewRoot "acceptance/$($State.RunId)" } else { Join-Path $reviewRoot $State.ShortSha }
    if ($State.ProjectName -ne $expectedProject -or [System.IO.Path]::GetFullPath($State.StatePath) -ne [System.IO.Path]::GetFullPath($expectedPath)) {
        throw 'Recorded review project/path identity is invalid.'
    }
}
function Remove-AcceptanceBrowser {
    param($State)
    Remove-Item -LiteralPath (Join-Path $State.StatePath 'control/proof.json') -Force -ErrorAction SilentlyContinue
    Clear-ReviewAuthenticationCredential $State
    $ids = @(Invoke-Docker @('ps', '-aq', '--filter', "label=com.docker.compose.project=$($State.ProjectName)", '--filter', 'label=com.docker.compose.service=acceptance') -Capture) -split '\s+' | Where-Object { $_ }
    if ($ids) { Invoke-Docker (@('rm', '--force') + @($ids)) }
}
function Clear-ReviewAuthenticationCredential {
    param($State, [string]$Action = 'Start')
    if ($Action -eq 'Status') { return }
    $credentialPath = Join-Path $State.StatePath 'auth-credential.txt'
    if (Test-Path -LiteralPath $credentialPath) {
        # Revoke the mounted file contents before unlinking the host path.
        [System.IO.File]::WriteAllText($credentialPath, '')
        Remove-Item -LiteralPath $credentialPath -Force
    }
}
function Open-ManualReview {
    param($State, [string]$WebId)
    if ($NoBrowser) { return }
    if ($IsWindows) {
        $publicRoot = Join-Path $State.StatePath 'root.crt'
        Invoke-Docker @('cp', "$($WebId):/review/tls-public/root.crt", $publicRoot)
        & (Join-Path $PSScriptRoot 'Initialize-AsapReviewTls.ps1') -Workspace $State.StatePath -PublicCertificatePath $publicRoot -Trust:$TrustReviewCertificate
    }
    Start-Process "https://localhost:$($State.Port)/staff/"
}
function New-StrongPassword {
    return [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) + 'aA1!'
}
function Set-ComposeEnvironment {
    param($State, [string]$SourcePath = $repoRoot)
    $directory = Assert-ManagedPath $State.StatePath
    foreach ($name in @('control', 'viewer', 'logs', 'email')) { New-Item -ItemType Directory -Path (Join-Path $directory $name) -Force | Out-Null }
    $tokenPath = Join-Path $directory 'evidence-token.txt'
    if (-not (Test-Path -LiteralPath $tokenPath)) { New-StrongPassword | Set-Content -LiteralPath $tokenPath -NoNewline }
    $authPath = Join-Path $directory 'auth-credential.txt'
    # Lifecycle Compose parsing needs a file, but only Start grants a fresh credential.
    if (-not (Test-Path -LiteralPath $authPath)) { '' | Set-Content -LiteralPath $authPath -NoNewline }
    $secretPath = Join-Path $directory 'secrets.json'
    $saPassword = if (Test-Path -LiteralPath $secretPath) { (Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json).SaPassword } else { 'lifecycle-placeholder-aA1!' }
    $values = @{
        ASAP_REVIEW_SOURCE = $SourcePath; ASAP_REVIEW_TOOLING_SOURCE = $repoRoot
        ASAP_REVIEW_SHA = $State.Sha; ASAP_REVIEW_SHORT_SHA = $State.ShortSha
        ASAP_REVIEW_TOOLING_SHA = if ($State.ToolingSha) { $State.ToolingSha } else { 'legacy' }; ASAP_REVIEW_TOOLING_VERSION = $State.ToolingVersion
        ASAP_REVIEW_TOOLING_DIRTY = if ($State.ToolingDirty) { 'true' } else { 'false' }
        ASAP_REVIEW_PORT = [string]$State.Port; ASAP_REVIEW_CONFIG = if ($State['InputConfigPath']) { $State.InputConfigPath } else { $State.ConfigPath }
        ASAP_REVIEW_PROJECT = $State.ProjectName; ASAP_REVIEW_LOGS = Join-Path $directory 'logs'
        ASAP_REVIEW_EMAIL = Join-Path $directory 'email'; ASAP_REVIEW_SA_PASSWORD = $saPassword
        ASAP_REVIEW_EVIDENCE_TOKEN = $tokenPath; ASAP_REVIEW_CONTROL = Join-Path $directory 'control'
        ASAP_REVIEW_AUTH_CREDENTIAL = $authPath; ASAP_REVIEW_AUTH_MODE = $State.Auth
        ASAP_REVIEW_VIEWER = Join-Path $directory 'viewer'
        ASAP_REVIEW_VIEWER_PORT = if ($State.ViewerPort) { [string]$State.ViewerPort } else { '6080' }
        ASAP_REVIEW_ARTIFACTS = if ($State.ArtifactPath) { $State.ArtifactPath } else { $directory }
    }
    foreach ($entry in $values.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process') }
}
function Get-ComposeArguments {
    param($State)
    $arguments = @('compose', '--project-name', $State.ProjectName, '--file', $composeFile)
    if ($State.Kind -eq 'Acceptance') { $arguments += @('--profile', 'acceptance') }
    return $arguments
}
function Test-PortAvailable {
    param([int]$Number)
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Number)
    try { $listener.Start(); return $true }
    catch { return $false }
    finally { $listener.Stop() }
}
function Resolve-ApplicationCommit {
    if ($Pr) {
        Write-Host "Fetching PR #$Pr HEAD from origin..."
        $revision = "refs/asap-review/pull/$Pr"
        & git -C $repoRoot fetch --no-tags origin "+refs/pull/$Pr/head:$revision"
        if ($LASTEXITCODE -ne 0) { throw "Could not fetch PR #$Pr HEAD from the existing origin remote." }
    }
    else {
        $revision = if ($Commit) { $Commit } else { 'HEAD' }
        $resolved = & git -C $repoRoot rev-parse --verify --end-of-options "$revision^{commit}" 2>$null
        if ($LASTEXITCODE -eq 0) { return ([string]$resolved).Trim() }
        & git -C $repoRoot fetch --no-tags origin $revision
        if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the selected application revision.' }
        $revision = 'FETCH_HEAD'
    }
    $resolved = & git -C $repoRoot rev-parse --verify --end-of-options "$revision^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0 -or $resolved -notmatch '^[a-f0-9]{40}$') { throw 'Could not resolve a full application commit SHA.' }
    return ([string]$resolved).Trim()
}
function Write-BlockedReport {
    param([string]$Directory, [string]$Detail, [string]$ApplicationSha, [string]$ToolingSha)
    $result = @{
        schemaVersion = 1; suite = $Suite; expectedApplicationCommit = $ApplicationSha; applicationCommit = $null
        mode = if ($CheckOnly) { 'check-only' } elseif ($AllowReviewWrites) { 'mutating' } else { 'read-only' }
        toolingCommit = $ToolingSha; toolingDirty = $toolingDirty; startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        environment = @{ status = 'BLOCKED'; detail = $Detail }; authentication = @{ mode = $Auth.ToLowerInvariant(); status = 'NOT RUN' }
        automatedFunctionalAcceptance = 'BLOCKED'; overallAcceptance = 'INCOMPLETE'
        scenarios = @(@{ id = 'launcher'; requirements = @('Container environment startup and safety evidence'); category = 'functional'; status = 'BLOCKED'; executed = $false; detail = $Detail },
            @{ id = 'human-review'; requirements = @('#398: visual and screen-reader review'); category = 'human'; status = 'HUMAN REVIEW REQUIRED'; executed = $false; detail = 'Human observation remains required.' })
    }
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Directory 'results.json') -Encoding utf8NoBOM
    @("# ASAP acceptance: $Suite", '', "Requested application: $ApplicationSha", "Tooling: $ToolingSha", '',
        "Authentication ($($Auth.ToLowerInvariant())): NOT RUN",
        "Automated: BLOCKED — $Detail", 'Browser scenarios: NOT RUN', 'Human visual/screen-reader review: REQUIRED',
        'Overall acceptance: INCOMPLETE', '', 'No authentication, credentials or network trace were recorded.') |
        Set-Content -LiteralPath (Join-Path $Directory 'report.md') -Encoding utf8NoBOM
}

$state = $null
$worktreePath = $null
$artifactPath = $null
$acceptanceName = $null
$launcherLease = $null
$toolingSha = (& git -C $repoRoot rev-parse HEAD).Trim()
$toolingDirty = [bool](@(& git -C $repoRoot status --porcelain --untracked-files=normal).Count)
$exitCode = 0
try {
    if ($Pr -and $Commit) { throw '-Pr and -Commit are mutually exclusive revision selectors.' }
    if ($Commit.StartsWith('-')) { throw '-Commit must be a Git revision, without Git command options.' }
    if ($ReadOnly -and $AllowReviewWrites) { throw '-ReadOnly and -AllowReviewWrites are mutually exclusive.' }
    if (($AllowReviewWrites -or $ReadOnly -or $CheckOnly) -and -not $Suite) { throw 'Acceptance options require -Suite.' }
    $registeredSuites = @(Get-Content (Join-Path $repoRoot 'tests/acceptance/suites/registry.json') -Raw | ConvertFrom-Json)
    if ($Suite -and $Suite -notin $registeredSuites) { throw "Unknown suite. Registered acceptance suites: $($registeredSuites -join ', ')." }
    if ($Suite -and $Port -ne 8080) { throw 'Container acceptance uses the registered https://localhost:8080/signin-oidc callback.' }
    if ($Project -and $Action -eq 'Start') { throw '-Project selects a retained environment for Status/Stop/Clean.' }
    if ($Action -ne 'Start') {
        $state = Read-State
        if (-not $state) { throw 'No current ASAP review environment is recorded.' }
        Assert-StateIdentity $state
        Set-ComposeEnvironment $state
        $compose = Get-ComposeArguments $state
        [void](Assert-LocalDocker)
        switch ($Action) {
            'Status' {
                Invoke-Docker ($compose + @('ps', '-a'))
                Write-Host "Lifecycle: $($state.LifecycleState); application $($state.Sha)"
                Write-Host "Project: $($state.ProjectName); staff https://localhost:$($state.Port)/staff/"
                if ($state.ArtifactPath) { Write-Host "Report: $($state.ArtifactPath)/report.md" }
                if ($state.LastError) { Write-Host "Last error: $($state.LastError)" }
            }
            'Stop' {
                Remove-AcceptanceBrowser $state
                Invoke-Docker ($compose + @('stop')) -TimeoutSeconds 120
                $state.LifecycleState = 'Stopped'; Write-State $state
                Write-Host "Stopped $($state.ProjectName). Data and reports were retained."
            }
            'Clean' {
                Remove-AcceptanceBrowser $state
                Invoke-Docker ($compose + @('down', '--volumes', '--remove-orphans')) -TimeoutSeconds 120
                $publicRoot = Join-Path $state.StatePath 'root.crt'
                if ($IsWindows -and (Test-Path -LiteralPath $publicRoot)) {
                    & (Join-Path $PSScriptRoot 'Initialize-AsapReviewTls.ps1') -Workspace $state.StatePath -PublicCertificatePath $publicRoot -Action Remove
                }
                $target = Assert-ManagedPath $state.StatePath
                Remove-Item -LiteralPath $target -Recurse -Force
                if ((Test-Path -LiteralPath $statePath) -and (Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).ProjectName -eq $state.ProjectName) { Remove-Item -LiteralPath $statePath -Force }
                Write-Host "Removed $($state.ProjectName) and its disposable database. Reports were retained."
            }
        }
        exit 0
    }

    New-Item -ItemType Directory -Path $reviewRoot -Force | Out-Null
    try { $launcherLease = [System.IO.File]::Open((Join-Path $reviewRoot 'launcher.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw 'Another review launcher is active in this checkout. Finish or cancel that run before starting another.' }

    if ($Suite) {
        $runId = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(12)).ToLowerInvariant()
        $artifactPath = Join-Path $repoRoot ".artifacts/acceptance/$([DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH-mm-ss'))-$runId"
        New-Item -ItemType Directory -Path $artifactPath -Force | Out-Null
    }
    $resolved = Resolve-ApplicationCommit
    if ($artifactPath) { Write-BlockedReport $artifactPath 'Environment startup has not completed; browser scenarios have not run.' $resolved $toolingSha }
    Write-Host "Application: $resolved"
    Write-Host "Tooling:     $toolingSha ($toolingVersion)"
    [void](Assert-LocalDocker)
    $platform = Invoke-Docker @('info', '--format', '{{.OSType}}/{{.Architecture}}') -Capture
    if ($platform -notmatch '^linux/(amd64|x86_64)$') { throw 'Review requires Docker Linux x64 containers for SQL Server 2022.' }
    Invoke-Docker @('compose', 'version')

    $explicitConfig = [bool]$Config
    if ($Auth -eq 'Local') {
        $configuration = Get-ReviewConfiguration -Auth Local
        $Config = $null
    }
    elseif (-not $Config) {
        $Config = Join-Path $HOME 'ASAP-Review/review.local.json'
        foreach ($candidate in @((Join-Path (Split-Path -Parent $repoRoot) 'review.local.json'), (Join-Path $repoRoot 'dev/review/review.local.json'))) {
            if (-not (Test-Path -LiteralPath $Config) -and (Test-Path -LiteralPath $candidate)) {
                try { [void](Get-ReviewConfiguration $candidate -Explicit); $Config = $candidate }
                catch {
                    # Complete partial legacy input outside Git, preserving its source.
                    New-Item -ItemType Directory -Path (Split-Path -Parent $Config) -Force | Out-Null
                    Copy-Item -LiteralPath $candidate -Destination $Config
                }
                break
            }
        }
    }
    if ($Auth -eq 'Entra') {
        $Config = [System.IO.Path]::GetFullPath($Config)
        $configuration = Get-ReviewConfiguration $Config -Explicit:$explicitConfig
        Write-Host "Entra may reuse your existing registration. Required Web redirect URI: https://localhost:$Port/signin-oidc (ID tokens; no client secret)."
    }
    Write-Host "Authentication: $Auth"

    $existing = Read-State
    if ($existing) {
        Assert-StateIdentity $existing
        # Preserve old current-only lifecycle records before selecting a fresh run.
        Write-State $existing
        if ($Suite -and $existing.Kind -eq 'Acceptance' -and $existing.LifecycleState -ne 'Stopped') {
            Set-ComposeEnvironment $existing
            Remove-AcceptanceBrowser $existing
            if ($existing.LifecycleState -eq 'Ready') { Write-ReviewProof $existing }
            Invoke-Docker ((Get-ComposeArguments $existing) + @('stop')) -TimeoutSeconds 120
            $existing.LifecycleState = 'Stopped'; Write-State $existing
        }
        elseif (-not $Suite) {
            if ($existing.Sha -ne $resolved -or $existing.ToolingSha -ne $toolingSha -or $existing.Auth -ne $Auth) {
                throw 'A different review environment is recorded. Use Status/Stop/Clean; retained data is never implicitly reset.'
            }
            if ($existing.LifecycleState -eq 'Ready') {
                Set-ComposeEnvironment $existing
                Write-ReviewProof $existing
                Write-Host "Review remains running: https://localhost:$($existing.Port)/staff/"
                $existingWebId = Invoke-Docker ((Get-ComposeArguments $existing) + @('ps', '-q', 'web')) -Capture
                Open-ManualReview $existing $existingWebId
                exit 0
            }
            if ($existing.LifecycleState -ne 'Stopped') { throw 'Review is Starting or Failed. Use Status or explicit Clean before retrying.' }
        }
    }
    if (-not (Test-PortAvailable $Port)) { throw "Port $Port is in use. Stop the existing review before starting this environment; its data will be retained." }
    $short = $resolved.Substring(0, 12)
    $projectName = if ($Suite) { "asap-acceptance-$runId" } else { "asap-review-$short" }
    $directory = if ($Suite) { Join-Path $reviewRoot "acceptance/$runId" } else { Join-Path $reviewRoot $short }
    $viewerPort = 6080
    if ($Suite) {
        $socket = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        try { $socket.Start(); $viewerPort = $socket.LocalEndpoint.Port } finally { $socket.Stop() }
    }
    $state = @{ Sha = $resolved; ShortSha = $short; ProjectName = $projectName; Port = $Port; ConfigPath = $Config
        StatePath = $directory; LogPath = Join-Path $directory 'logs'; EmailPath = Join-Path $directory 'email'
        ToolingVersion = $toolingVersion; ToolingSha = $toolingSha; ToolingDirty = $toolingDirty; Auth = $Auth
        LifecycleState = 'Starting'; LastError = $null; Kind = if ($Suite) { 'Acceptance' } else { 'Manual' }
        RunId = if ($Suite) { $runId } else { $null }; ViewerPort = $viewerPort; ArtifactPath = $artifactPath }
    Write-State $state
    # Snapshot validated defaults without rewriting the external configuration.
    # Acceptance fixtures belong to this fresh run, independent of Seed=None.
    $runInput = @{} + $configuration
    if ($Suite) { $runInput.Seed = 'Review' }
    $state.InputConfigPath = Join-Path $directory 'input.json'
    $runInput | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $state.InputConfigPath -Encoding utf8NoBOM
    if ($Auth -eq 'Local') { $state.ConfigPath = $state.InputConfigPath }
    Write-State $state
    $secret = Join-Path $directory 'secrets.json'
    if (-not (Test-Path -LiteralPath $secret)) { @{ SaPassword = New-StrongPassword } | ConvertTo-Json | Set-Content -LiteralPath $secret -Encoding utf8NoBOM }
    $worktreePath = Assert-ManagedPath (Join-Path $reviewRoot "worktrees/$projectName-$PID")
    & git -C $repoRoot worktree add --detach $worktreePath $resolved
    if ($LASTEXITCODE -ne 0) { throw 'Could not materialize the selected application commit.' }
    $harness = Join-Path $worktreePath '.review-harness'
    New-Item -ItemType Directory -Path $harness -Force | Out-Null
    foreach ($item in @('docker', 'src', 'VERSION')) { Copy-Item -LiteralPath (Join-Path $repoRoot "dev/review/$item") -Destination $harness -Recurse -Force }
    Set-ComposeEnvironment $state $worktreePath
    if ($Auth -eq 'Local') {
        [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant() |
            Set-Content -LiteralPath (Join-Path $directory 'auth-credential.txt') -NoNewline -Encoding utf8NoBOM
    }
    $compose = Get-ComposeArguments $state
    $build = $compose + @('build')
    if ($Rebuild) { $build += '--no-cache' }
    Invoke-Docker ($build + @('db-init', 'web')) -TimeoutSeconds 1800
    if ($Suite) { Invoke-Docker ($build + @('acceptance')) -TimeoutSeconds 900 }
    # Compose waits for SQL/DACPAC/Hangfire initialization before starting web.
    Invoke-Docker ($compose + @('up', '--detach', '--no-build', 'web')) -TimeoutSeconds 600
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(4)
    do {
        $webId = Invoke-Docker ($compose + @('ps', '-q', 'web')) -Capture
        $health = if ($webId) { Invoke-Docker @('inspect', '--format', '{{.State.Health.Status}}', $webId) -Capture } else { '' }
        if ($health -eq 'healthy') { break }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($health -ne 'healthy') { throw 'ASAP did not become ready. Inspect review Status and local db-init/web logs.' }
    $state.LifecycleState = 'Ready'; Write-State $state
    Write-ReviewProof $state

    if ($Suite) {
        $viewerToken = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
        "$($viewerToken): 127.0.0.1:5900" | Set-Content -LiteralPath (Join-Path $directory 'viewer/tokens') -Encoding utf8NoBOM
        $runnerArguments = @('--suite', $Suite, '--expected-commit', $resolved, '--auth', $Auth.ToLowerInvariant(), '--auth-timeout-seconds', [string]$AuthenticationTimeoutSeconds)
        if ($AllowReviewWrites) { $runnerArguments += '--allow-review-writes' } else { $runnerArguments += '--read-only' }
        if ($CheckOnly) { $runnerArguments += '--check-only' }
        $acceptanceName = "$projectName-browser"
        Invoke-Docker ($compose + @('run', '--detach', '--no-deps', '--service-ports', '--name', $acceptanceName, 'acceptance') + $runnerArguments)
        $opened = $false
        try {
            while ($true) {
                $runner = @(Invoke-Docker @('inspect', $acceptanceName) -Capture | ConvertFrom-Json)[0]
                if (-not $runner.State.Running) { $exitCode = $runner.State.ExitCode; break }
                try { Write-ReviewProof $state -WithAcceptance }
                catch {
                    # A completed one-off container can exit between inspect and
                    # the next host proof. Preserve its real test exit status.
                    $finished = @(Invoke-Docker @('inspect', $acceptanceName) -Capture | ConvertFrom-Json)[0]
                    if (-not $finished.State.Running) { $exitCode = $finished.State.ExitCode; break }
                    throw
                }
                if (-not $opened -and -not $CheckOnly -and ($Auth -eq 'Entra' -or $ShowBrowser)) {
                    $ready = Invoke-Docker @('exec', $acceptanceName, 'sh', '-c', 'if test -f /tmp/viewer-ready; then echo ready; fi') -Capture
                    if ($ready -eq 'ready') {
                        $viewerUrl = "http://127.0.0.1:$viewerPort/vnc.html#autoconnect=true&resize=scale&path=websockify%3Ftoken%3D$viewerToken"
                        if (-not $NoBrowser) {
                            try { Start-Process $viewerUrl }
                            catch { throw 'The browser viewer could not be opened. Check the default browser association and rerun.' }
                        }
                        if ($Auth -eq 'Entra') { Write-Host 'Browser viewer ready. Complete normal Entra sign-in/MFA; the suite will continue automatically.' }
                        else { Write-Host 'Browser viewer ready. Local authentication and scenarios continue automatically.' }
                        $opened = $true
                    }
                }
                Start-Sleep -Seconds 2
            }
        }
        finally {
            Remove-Item -LiteralPath (Join-Path $directory 'control/proof.json') -Force -ErrorAction SilentlyContinue
            Invoke-Docker @('rm', '--force', $acceptanceName)
            Clear-ReviewAuthenticationCredential $state
            $acceptanceName = $null
        }
        $resultsPath = Join-Path $artifactPath 'results.json'
        if (-not (Test-Path -LiteralPath $resultsPath)) { throw 'Acceptance container exited before writing its report.' }
        $results = Get-Content -LiteralPath $resultsPath -Raw | ConvertFrom-Json
        $counts = @($results.scenarios | Where-Object category -eq 'functional') | Group-Object status
        Write-Host "Automated: $($results.automatedFunctionalAcceptance)"
        foreach ($status in @('PASS', 'FAIL', 'BLOCKED', 'NOT RUN')) {
            $count = @($counts | Where-Object Name -eq $status | ForEach-Object Count)
            Write-Host "$status`: $(if ($count.Count) { $count[0] } else { 0 })"
        }
        Write-Host "Tested application: $(if ($results.applicationCommit) { $results.applicationCommit } else { 'not verified' })"
        Write-Host "Report: $(Join-Path $artifactPath 'report.md')"
        Write-Host 'Human visual/screen-reader review: REQUIRED. Overall acceptance remains INCOMPLETE.'
    }
    else {
        Open-ManualReview $state $webId
    }
    Write-Host "Review environment remains running: $projectName; https://localhost:$Port/staff/"
    Write-Host "Retained state: $directory; logs: $($state.LogPath); captured email: $($state.EmailPath)"
}
catch {
    $exitCode = 2
    $detail = $_.Exception.Message
    if ($state -and $Action -eq 'Start') {
        $saved = Join-Path $state.StatePath 'state.json'
        if (Test-Path -LiteralPath $saved) {
            $state = Complete-State (Get-Content -LiteralPath $saved -Raw | ConvertFrom-Json -AsHashtable)
            $state.LastError = $detail
            if ($state.LifecycleState -eq 'Starting') { $state.LifecycleState = 'Failed' }
            Write-State $state
        }
    }
    if ($artifactPath -and -not (Test-Path -LiteralPath (Join-Path $artifactPath 'results.json'))) {
        Write-BlockedReport $artifactPath $detail $(if (Get-Variable resolved -ErrorAction SilentlyContinue) { $resolved } else { $null }) $toolingSha
    }
    elseif ($artifactPath) {
        # A supervision failure must not leave a partial PASS report behind.
        $reportFile = Join-Path $artifactPath 'results.json'
        $partial = Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json -AsHashtable
        if ($partial.environment.status -ne 'PASS') {
            $partial.environment = @{ status = 'BLOCKED'; detail = $detail }
            foreach ($row in $partial.scenarios | Where-Object id -eq 'launcher') { $row.detail = $detail }
        }
        else { $partial.scenarios += @{ id = 'host-supervision'; requirements = @('Live host isolation and browser lifecycle'); category = 'functional'; status = 'BLOCKED'; executed = $false; detail = $detail } }
        if ($partial.automatedFunctionalAcceptance -ne 'FAIL') { $partial.automatedFunctionalAcceptance = 'BLOCKED' }
        $partial.overallAcceptance = 'INCOMPLETE'
        $partial | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $reportFile -Encoding utf8NoBOM
        Add-Content -LiteralPath (Join-Path $artifactPath 'report.md') -Value "`nHost supervision: **BLOCKED** — $detail. Overall acceptance remains INCOMPLETE."
    }
    Write-Host "BLOCKED: $detail" -ForegroundColor Red
    if ($artifactPath) {
        $summary = Get-Content -LiteralPath (Join-Path $artifactPath 'results.json') -Raw | ConvertFrom-Json
        $rows = @($summary.scenarios | Where-Object category -eq 'functional')
        Write-Host "Automated: $($summary.automatedFunctionalAcceptance); PASS: $(@($rows | Where-Object status -eq 'PASS').Count); FAIL: $(@($rows | Where-Object status -eq 'FAIL').Count); BLOCKED: $(@($rows | Where-Object status -eq 'BLOCKED').Count); NOT RUN: $(@($rows | Where-Object status -eq 'NOT RUN').Count)"
        Write-Host "Tested application: $(if ($summary.applicationCommit) { $summary.applicationCommit } else { 'not verified' })"
        Write-Host "Report: $(Join-Path $artifactPath 'report.md'); human review remains REQUIRED."
        if (-not $state) { Write-Host 'Review environment was not started by this invocation.' }
    }
    if ($state) { Write-Host "Retained environment: $($state.ProjectName) ($($state.LifecycleState)). Use -Action Status." }
}
finally {
    if ($acceptanceName) { try { Invoke-Docker @('rm', '--force', $acceptanceName) } catch { Write-Warning 'Browser container cleanup failed. Run -Action Stop or Clean to remove its ephemeral authentication profile.' } }
    if ($worktreePath -and (Test-Path -LiteralPath $worktreePath)) {
        $target = Assert-ManagedPath $worktreePath
        & git -C $repoRoot worktree remove --force $target
        if ($LASTEXITCODE -ne 0) { Write-Warning "Temporary application checkout retained: $target" }
    }
    if ($state -and $state.Kind -eq 'Acceptance') {
        Clear-ReviewAuthenticationCredential $state -Action $Action
    }
    foreach ($variable in @(Get-ChildItem Env:ASAP_REVIEW_*)) { [Environment]::SetEnvironmentVariable($variable.Name, $null, 'Process') }
    if ($launcherLease) { $launcherLease.Dispose() }
}
exit $exitCode
