[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Test-Command {
    param([Parameter(Mandatory)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found."
    }
}

try {
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw "PowerShell 7 or later is required. Current version: $($PSVersionTable.PSVersion)."
    }
    if (-not $IsWindows) {
        throw 'ASAP review currently requires Windows because bootstrap installs a trusted localhost certificate in the CurrentUser certificate store.'
    }

    Test-Command git
    Test-Command docker

    & docker version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker is installed but the Docker daemon is not reachable. Start Docker Desktop and try again.'
    }

    & docker compose version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker Compose v2 is required.'
    }

    $dockerPlatform = (& docker info --format '{{.OSType}}/{{.Architecture}}').Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to determine Docker engine platform.'
    }

    if ($dockerPlatform -notmatch '^linux/(amd64|x86_64)$') {
        throw "ASAP review requires Linux x64 containers for SQL Server 2022. Docker reports '$dockerPlatform'."
    }

    Write-Host 'ASAP review prerequisites are available.'
    Write-Host "  PowerShell: $($PSVersionTable.PSVersion)"
    Write-Host "  Git:        $((& git --version).Trim())"
    Write-Host "  Docker:     $dockerPlatform"
    Write-Host "  Compose:    $((& docker compose version).Trim())"
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
