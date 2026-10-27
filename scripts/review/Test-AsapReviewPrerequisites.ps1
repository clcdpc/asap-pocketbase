[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Review-Environment.ps1')

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
    Test-Command git
    Test-Command docker

    [void](Assert-LocalDocker)
    Invoke-Docker @('compose', 'version')
    $dockerPlatform = Invoke-Docker @('info', '--format', '{{.OSType}}/{{.Architecture}}') -Capture

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
