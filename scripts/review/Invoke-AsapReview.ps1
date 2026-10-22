[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Clean', 'Status')]
    [string]$Action = 'Start',

    [string]$Commit = 'HEAD',

    [string]$Config,

    [ValidateRange(1, 65535)]
    [int]$Port = 8080,

    [switch]$KeepData,

    [switch]$Rebuild,

    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (& git -C $scriptRoot rev-parse --show-toplevel 2>$null).Trim()
if (-not $repoRoot) {
    throw 'Run this script from a clone of the ASAP repository.'
}

$versionPath = Join-Path $repoRoot 'dev/review/VERSION'
if (-not (Test-Path -LiteralPath $versionPath)) {
    throw "ASAP review tooling version file was not found: $versionPath"
}
$toolingVersion = (Get-Content -LiteralPath $versionPath -Raw).Trim()
if (-not $toolingVersion) {
    throw "ASAP review tooling version file is empty: $versionPath"
}
Write-Host "ASAP review tooling $toolingVersion"

$reviewRoot = Join-Path $repoRoot '.review'
$statePath = Join-Path $reviewRoot 'current.json'
$composeFile = Join-Path $repoRoot 'dev/review/docker/compose.yml'
$prerequisiteScript = Join-Path $repoRoot 'scripts/review/Test-AsapReviewPrerequisites.ps1'
$tlsInitializerScript = Join-Path $repoRoot 'scripts/review/Initialize-AsapReviewTls.ps1'

function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

function Try-Read-State {
    if (-not (Test-Path -LiteralPath $statePath)) {
        return $null
    }

    try {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if (-not $state.PSObject.Properties['LifecycleState']) {
            $state | Add-Member -NotePropertyName LifecycleState -NotePropertyValue 'Ready'
        }
        if (-not $state.PSObject.Properties['LastError']) {
            $state | Add-Member -NotePropertyName LastError -NotePropertyValue $null
        }
        return $state
    }
    catch {
        throw "ASAP review state is unreadable: $statePath"
    }
}

function Read-State {
    $state = Try-Read-State
    if ($null -eq $state) {
        throw 'No current ASAP review environment is recorded.'
    }

    return $state
}

function Write-State {
    param([Parameter(Mandatory)]$State)

    New-Item -ItemType Directory -Path $reviewRoot -Force | Out-Null
    $State | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8NoBOM
}

function Set-ComposeEnvironment {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$SaPassword,
        [Parameter(Mandatory)][string]$TlsCertificatePath,
        [Parameter(Mandatory)][string]$TlsPassword
    )

    $env:ASAP_REVIEW_SOURCE = $SourcePath
    $env:ASAP_REVIEW_SHA = [string]$State.Sha
    $env:ASAP_REVIEW_SHORT_SHA = [string]$State.ShortSha
    $env:ASAP_REVIEW_PORT = [string]$State.Port
    $env:ASAP_REVIEW_CONFIG = [string]$State.ConfigPath
    $env:ASAP_REVIEW_LOGS = [string]$State.LogPath
    $env:ASAP_REVIEW_EMAIL = [string]$State.EmailPath
    $env:ASAP_REVIEW_SA_PASSWORD = $SaPassword
    $env:ASAP_REVIEW_TLS_CERTIFICATE = $TlsCertificatePath
    $env:ASAP_REVIEW_TLS_PASSWORD = $TlsPassword
    $env:ASAP_REVIEW_TOOLING_VERSION = $toolingVersion
}

function Get-Secret {
    param([Parameter(Mandatory)][string]$SecretPath)

    if (-not (Test-Path $SecretPath)) {
        throw "Review secret file is missing: $SecretPath"
    }

    (Get-Content $SecretPath -Raw | ConvertFrom-Json).SaPassword
}

function New-StrongPassword {
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)
    return "$([Convert]::ToHexString($bytes))aA1!"
}

function Get-TlsMaterial {
    param([Parameter(Mandatory)][string]$ConfigPath)

    $workspace = Split-Path -Parent ([System.IO.Path]::GetFullPath($ConfigPath))
    $tlsDirectory = Join-Path $workspace '.tls'
    $certificatePath = Join-Path $tlsDirectory 'localhost.pfx'
    $secretPath = Join-Path $tlsDirectory 'tls.json'

    if (-not (Test-Path -LiteralPath $certificatePath)) {
        throw "ASAP review localhost certificate was not found: $certificatePath. Run bootstrap again."
    }
    if (-not (Test-Path -LiteralPath $secretPath)) {
        throw "ASAP review localhost certificate password file was not found: $secretPath. Run bootstrap again."
    }

    $secret = Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json
    $password = [string]$secret.Password
    if (-not $password) {
        throw "ASAP review localhost certificate password file is invalid: $secretPath"
    }

    return [pscustomobject]@{
        Workspace = $workspace
        CertificatePath = $certificatePath
        SecretPath = $secretPath
        Password = $password
    }
}

function Test-PortAvailable {
    param([Parameter(Mandatory)][int]$PortNumber)

    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $PortNumber)
    try {
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        $listener.Stop()
    }
}

function Remove-TemporaryWorktree {
    param([string]$Path)

    if (-not $Path) {
        return
    }

    & git -C $repoRoot worktree remove --force $Path *> $null
    if ($LASTEXITCODE -ne 0 -and (Test-Path $Path)) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
    & git -C $repoRoot worktree prune *> $null
}

if ($Action -ne 'Start') {
    $state = Read-State
    $secretPath = Join-Path ([string]$state.StatePath) 'secrets.json'
    $saPassword = 'review-lifecycle-placeholder'
    if (Test-Path -LiteralPath $secretPath) {
        $saPassword = Get-Secret $secretPath
    }

    $tlsCertificatePath = $null
    if ($state.PSObject.Properties['TlsCertificatePath']) {
        $tlsCertificatePath = [string]$state.TlsCertificatePath
    }
    if (-not $tlsCertificatePath -or -not (Test-Path -LiteralPath $tlsCertificatePath)) {
        $placeholderDirectory = [string]$state.StatePath
        New-Item -ItemType Directory -Path $placeholderDirectory -Force | Out-Null
        $tlsCertificatePath = Join-Path $placeholderDirectory 'lifecycle-placeholder.pfx'
        if (-not (Test-Path -LiteralPath $tlsCertificatePath)) {
            New-Item -ItemType File -Path $tlsCertificatePath -Force | Out-Null
        }
    }

    $tlsPassword = 'review-lifecycle-placeholder'
    if ($state.PSObject.Properties['TlsSecretPath'] -and (Test-Path -LiteralPath ([string]$state.TlsSecretPath))) {
        try {
            $tlsSecret = Get-Content -LiteralPath ([string]$state.TlsSecretPath) -Raw | ConvertFrom-Json
            if ($tlsSecret.Password) {
                $tlsPassword = [string]$tlsSecret.Password
            }
        }
        catch {
            # Lifecycle commands do not need the TLS private key; keep the placeholder.
        }
    }

    Set-ComposeEnvironment -State $state -SourcePath $repoRoot -SaPassword $saPassword -TlsCertificatePath $tlsCertificatePath -TlsPassword $tlsPassword
    $composeArgs = @('compose', '--project-name', [string]$state.ProjectName, '--file', $composeFile)

    switch ($Action) {
        'Stop' {
            $previousLifecycleState = [string]$state.LifecycleState
            Invoke-Native docker ($composeArgs + @('stop'))
            $state.LifecycleState = 'Stopped'
            if ($previousLifecycleState -ne 'Failed') {
                $state.LastError = $null
            }
            Write-State $state
            Write-Host "Stopped ASAP review $($state.ShortSha). Data volumes were retained."
        }
        'Clean' {
            Invoke-Native docker ($composeArgs + @('down', '--volumes', '--remove-orphans'))
            Remove-Item -LiteralPath ([string]$state.StatePath) -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
            Write-Host "Removed ASAP review $($state.ShortSha), including its disposable database."
        }
        'Status' {
            Invoke-Native docker ($composeArgs + @('ps'))
            Write-Host "Lifecycle: $($state.LifecycleState)"
            if ($state.LastError) {
                Write-Host "Last error: $($state.LastError)"
            }
            Write-Host "Staff:     https://localhost:$($state.Port)/staff/"
            Write-Host "Patron:    https://localhost:$($state.Port)/patron/"
        }
    }

    exit 0
}

& $prerequisiteScript
if ($LASTEXITCODE -ne 0) {
    throw 'ASAP review prerequisites are not satisfied.'
}

if (-not $Config) {
    $Config = Join-Path $repoRoot 'dev/review/review.local.json'
}
$Config = [System.IO.Path]::GetFullPath($Config)
if (-not (Test-Path $Config)) {
    throw "Review configuration was not found: $Config"
}

$tlsWorkspace = Split-Path -Parent $Config
& pwsh -NoProfile -File $tlsInitializerScript -Workspace $tlsWorkspace -ReviewStatePath $statePath
if ($LASTEXITCODE -ne 0) {
    throw 'ASAP review localhost HTTPS certificate setup failed.'
}
$tlsMaterial = Get-TlsMaterial -ConfigPath $Config

$resolved = (& git -C $repoRoot rev-parse --verify "$Commit^{commit}" 2>$null).Trim()
if (-not $resolved) {
    Write-Host "Fetching revision $Commit from origin..."
    & git -C $repoRoot fetch origin $Commit
    if ($LASTEXITCODE -ne 0) {
        throw "Could not fetch revision '$Commit' from origin."
    }
    $resolved = (& git -C $repoRoot rev-parse --verify 'FETCH_HEAD^{commit}' 2>$null).Trim()
}
if (-not $resolved) {
    throw "Could not resolve '$Commit' to a commit."
}

$existingState = Try-Read-State
if ($null -ne $existingState -and [string]$existingState.Sha -ne $resolved) {
    throw "Another ASAP review environment is recorded for $($existingState.ShortSha). Run -Action Clean before reviewing a different revision."
}
if ($null -ne $existingState -and $existingState.LifecycleState -in @('Starting', 'Failed')) {
    throw "The recorded ASAP review environment is in state '$($existingState.LifecycleState)'. Run -Action Status or -Action Clean before retrying."
}

if (-not (Test-PortAvailable $Port)) {
    throw "Port $Port is already in use. Stop the process using it, or use -Port only if the matching Entra redirect URI is registered."
}
if ($Port -ne 8080) {
    Write-Warning "Staff sign-in requires https://localhost:$Port/signin-oidc to be registered on the review Entra application."
}

$shortSha = $resolved.Substring(0, 12)
$projectName = "asap-review-$shortSha"
$stateDirectory = Join-Path $reviewRoot $shortSha
$worktreeRoot = Join-Path $reviewRoot 'worktrees'
$worktreePath = Join-Path $worktreeRoot "$shortSha-$PID"
$logPath = Join-Path $stateDirectory 'logs'
$emailPath = Join-Path $stateDirectory 'email'
$secretPath = Join-Path $stateDirectory 'secrets.json'

New-Item -ItemType Directory -Path $stateDirectory, $worktreeRoot, $logPath, $emailPath -Force | Out-Null

$saPassword = $null
if ($KeepData -and (Test-Path $secretPath)) {
    $saPassword = Get-Secret $secretPath
}
if (-not $saPassword) {
    $saPassword = New-StrongPassword
    @{ SaPassword = $saPassword } | ConvertTo-Json | Set-Content -LiteralPath $secretPath -Encoding utf8NoBOM
}

$state = [ordered]@{
    Sha = $resolved
    ShortSha = $shortSha
    ProjectName = $projectName
    Port = $Port
    ConfigPath = $Config
    LogPath = $logPath
    EmailPath = $emailPath
    StatePath = $stateDirectory
    TlsCertificatePath = $tlsMaterial.CertificatePath
    TlsSecretPath = $tlsMaterial.SecretPath
    ToolingVersion = $toolingVersion
    LifecycleState = 'Starting'
    LastError = $null
}
Write-State $state

$temporaryWorktreeCreated = $false
try {
    Write-Host "Materializing commit $resolved..."
    Invoke-Native git @('-C', $repoRoot, 'worktree', 'add', '--detach', $worktreePath, $resolved)
    $temporaryWorktreeCreated = $true

    $harnessDestination = Join-Path $worktreePath '.review-harness'
    New-Item -ItemType Directory -Path $harnessDestination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'dev/review/docker') -Destination (Join-Path $harnessDestination 'docker') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'dev/review/src') -Destination (Join-Path $harnessDestination 'src') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'dev/review/VERSION') -Destination (Join-Path $harnessDestination 'VERSION') -Force

    Set-ComposeEnvironment -State $state -SourcePath $worktreePath -SaPassword $saPassword -TlsCertificatePath $tlsMaterial.CertificatePath -TlsPassword $tlsMaterial.Password
    $composeArgs = @('compose', '--project-name', $projectName, '--file', $composeFile)

    if (-not $KeepData) {
        & docker @($composeArgs + @('down', '--volumes', '--remove-orphans')) *> $null
        Remove-Item -LiteralPath $secretPath -Force -ErrorAction SilentlyContinue
        $saPassword = New-StrongPassword
        @{ SaPassword = $saPassword } | ConvertTo-Json | Set-Content -LiteralPath $secretPath -Encoding utf8NoBOM
        $env:ASAP_REVIEW_SA_PASSWORD = $saPassword
    }

    $buildArgs = $composeArgs + @('build')
    if ($Rebuild) {
        $buildArgs += '--no-cache'
    }
    Invoke-Native docker $buildArgs

    $webImage = "asap-review-web:$shortSha"
    $imageRevision = (& docker image inspect $webImage --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}').Trim()
    if ($LASTEXITCODE -ne 0 -or $imageRevision -ne $resolved) {
        throw "Built image revision '$imageRevision' does not match requested commit '$resolved'."
    }

    $imageToolingVersion = (& docker image inspect $webImage --format '{{ index .Config.Labels "org.opencontainers.image.version" }}').Trim()
    if ($LASTEXITCODE -ne 0 -or $imageToolingVersion -ne $toolingVersion) {
        throw "Built image tooling version '$imageToolingVersion' does not match launcher tooling version '$toolingVersion'."
    }

    Invoke-Native docker ($composeArgs + @('up', '--detach'))

    $readyUrl = "https://localhost:$Port/health/ready"
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    $ready = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-RestMethod -Uri $readyUrl -TimeoutSec 3
            if ($response.status -eq 'healthy') {
                $ready = $true
                break
            }
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }

    if (-not $ready) {
        Write-Host ''
        Write-Host 'Container status:'
        & docker @($composeArgs + @('ps'))
        Write-Host ''
        Write-Host 'db-init logs:'
        & docker @($composeArgs + @('logs', '--no-color', 'db-init'))
        Write-Host ''
        Write-Host 'web logs:'
        & docker @($composeArgs + @('logs', '--no-color', '--tail', '200', 'web'))
        throw 'ASAP did not become ready before the review startup timeout.'
    }

    $state['LifecycleState'] = 'Ready'
    $state['LastError'] = $null
    Write-State $state

    Write-Host ''
    Write-Host 'ASAP Review Environment'
    Write-Host ''
    Write-Host "Tooling:      $toolingVersion"
    Write-Host "Commit:       $resolved"
    Write-Host 'Database:     fresh/isolated SQL Server 2022 Developer container'
    Write-Host 'Providers:    deterministic Testing provider'
    Write-Host 'Staff auth:   real Entra OIDC'
    Write-Host "Staff:        https://localhost:$Port/staff/"
    Write-Host "Patron:       https://localhost:$Port/patron/"
    Write-Host "Health:       https://localhost:$Port/health/ready"
    Write-Host "Logs:         $logPath"
    Write-Host "Email output: $emailPath"
    Write-Host ''
    Write-Host 'Stub patron credentials:'
    Write-Host '  Barcode: any non-empty barcode (for example REVIEW1001)'
    Write-Host '  PIN:     1234'

    if (-not $NoBrowser) {
        try {
            Start-Process "https://localhost:$Port/staff/"
        }
        catch {
            Write-Warning 'ASAP is running, but the staff URL could not be opened automatically.'
        }
    }
}
catch {
    $state['LifecycleState'] = 'Failed'
    $state['LastError'] = $_.Exception.Message
    Write-State $state
    throw
}
finally {
    if ($temporaryWorktreeCreated) {
        Remove-TemporaryWorktree $worktreePath
    }
}
