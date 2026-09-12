[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https?://')]
    [string] $Uri,

    [ValidateRange(1, 60)]
    [int] $TimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'

function Write-PrtgResult {
    param(
        [Parameter(Mandatory = $true)]
        [int] $ErrorValue,

        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    $escapedMessage = [System.Security.SecurityElement]::Escape($Message)
    [Console]::Out.WriteLine(
        "<prtg><error>$ErrorValue</error><text>$escapedMessage</text></prtg>"
    )
}

try {
    $response = Invoke-RestMethod -Uri $Uri -TimeoutSec $TimeoutSeconds
    if ($response.status -eq 'healthy') {
        Write-PrtgResult -ErrorValue 0 -Message 'ASAP is ready.'
        exit 0
    }

    Write-PrtgResult -ErrorValue 1 -Message 'ASAP is not ready.'
    exit 0
}
catch {
    Write-PrtgResult -ErrorValue 1 -Message 'ASAP readiness request failed.'
    exit 0
}
