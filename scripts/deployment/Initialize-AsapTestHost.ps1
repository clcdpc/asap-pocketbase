#Requires -Version 7.0

[CmdletBinding()]
param(
    [switch] $Initialize,
    [switch] $ContractSelfTest,

    [Parameter(DontShow = $true)]
    [string] $RootPath = 'C:\ProgramData\clc-asap'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:ValidationErrors = [System.Collections.Generic.List[string]]::new()
$script:InitializationRequired = $false

$applicationTemplateJson = @'
{
  "Environment": {
    "Name": "Test",
    "IsNonProduction": true,
    "UiBannerText": "NON-PRODUCTION"
  },
  "ConnectionStrings": {
    "AsapDatabase": "Server=REPLACE-SQL-SERVER;Database=REPLACE-ASAP-DATABASE;Integrated Security=True;Encrypt=True;TrustServerCertificate=False",
    "HangfireDatabase": "Server=REPLACE-SQL-SERVER;Database=REPLACE-HANGFIRE-DATABASE;Integrated Security=True;Encrypt=True;TrustServerCertificate=False"
  },
  "Authentication": {
    "Entra": {
      "ClientId": "REPLACE-CLIENT-ID",
      "ClientSecret": "REPLACE-CLIENT-SECRET",
      "AllowedTenantIds": [
        "REPLACE-TENANT-ID"
      ],
      "InitialSuperAdmin": {
        "TenantId": "REPLACE-TENANT-ID",
        "ObjectId": "REPLACE-OBJECT-ID",
        "UserPrincipalName": "REPLACE-ADMIN-UPN",
        "DisplayName": "ASAP Initial Administrator",
        "NotificationEmail": "REPLACE-ADMIN-EMAIL"
      }
    }
  },
  "Application": {
    "BusinessTimeZone": "America/New_York",
    "DataProtectionKeysPath": "REPLACE-DATA-PROTECTION-KEYS-PATH",
    "DataProtectionKeyEncryptionCertificateThumbprint": "REPLACE-DATA-PROTECTION-CERTIFICATE-THUMBPRINT",
    "LogPath": "REPLACE-LOG-PATH"
  },
  "EmailSafety": {
    "AllowedRecipientDomains": [
      "REPLACE-ALLOWED-DOMAIN"
    ]
  },
  "PatronLoginRateLimit": {
    "PermitLimit": 20,
    "WindowSeconds": 300
  },
  "Hangfire": {
    "Schedules": {
      "WorkflowProcessing": "0 * * * *",
      "IdentifierProcessing": "*/5 * * * *",
      "OrganizationRefresh": "0 2 * * *",
      "WeeklyStaffSummary": "0 20 * * 0",
      "EmailOutboxSweep": "*/5 * * * *",
      "PatronSessionCleanup": "0 3 * * *",
      "EmailPayloadCleanup": "30 3 * * *"
    },
    "ProcessingLimits": {
      "Default": {
        "PageSize": 50,
        "MaxPerRun": 500
      },
      "Timeouts": {
        "PageSize": null,
        "MaxPerRun": null
      },
      "Queues": {
        "IdentifierProcessing": {
          "PageSize": null,
          "MaxPerRun": null
        },
        "PurchasePromotion": {
          "PageSize": null,
          "MaxPerRun": null
        },
        "HoldPlacement": {
          "PageSize": null,
          "MaxPerRun": null
        },
        "FulfillmentTracking": {
          "PageSize": null,
          "MaxPerRun": null
        },
        "OutstandingTimeout": {
          "PageSize": null,
          "MaxPerRun": null
        },
        "PendingHoldTimeout": {
          "PageSize": null,
          "MaxPerRun": null
        },
        "HoldPickupTimeout": {
          "PageSize": null,
          "MaxPerRun": null
        },
        "AdditionalCopyTimeout": {
          "PageSize": null,
          "MaxPerRun": null
        }
      }
    }
  }
}
'@

function Get-Value {
    param([object] $Object, [string] $Name)

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-NestedValue {
    param([object] $Object, [string[]] $Names)

    $value = $Object
    foreach ($name in $Names) {
        $value = Get-Value $value $name
        if ($null -eq $value) {
            return $null
        }
    }

    return $value
}

function Add-ValidationError {
    param([string] $Message)
    $script:ValidationErrors.Add($Message)
}

function Require-Text {
    param([object] $Value, [string] $Name)

    $text = [string] $Value
    if ([string]::IsNullOrWhiteSpace($text) -or $text -match 'REPLACE-') {
        Add-ValidationError "$Name requires a non-placeholder value."
        return $null
    }

    return $text
}

function Test-EquivalentPath {
    param([object] $Actual, [string] $Expected)

    try {
        if ([string]::IsNullOrWhiteSpace([string] $Actual)) {
            return $false
        }

        $actualPath = [IO.Path]::GetFullPath([string] $Actual).TrimEnd('\', '/')
        $expectedPath = [IO.Path]::GetFullPath($Expected).TrimEnd('\', '/')
        return $actualPath.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Write-TemplateIfMissing {
    param([string] $Path, [object] $Value)

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Write-Host "Preserved existing operator configuration: $Path"
        return
    }

    $temporaryPath = Join-Path (Split-Path -Parent $Path) ('.' + [IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporaryPath, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporaryPath, $Path, $false)
        Write-Host "Created template configuration: $Path"
    }
    catch [IO.IOException] {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            throw
        }
        Write-Host "Preserved configuration created concurrently: $Path"
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

function New-ApplicationTemplate {
    param([string] $HostRoot)

    $template = $applicationTemplateJson | ConvertFrom-Json
    $template.Application.DataProtectionKeysPath = Join-Path $HostRoot 'DataProtection-Keys'
    $template.Application.LogPath = Join-Path $HostRoot 'Logs'
    return $template
}

function New-DeploymentTemplate {
    param([string] $HostRoot)

    return [ordered]@{
        IisSiteName = 'ASAP'
        IisAppPoolName = 'ASAP'
        DeploymentPath = 'D:\Sites\ASAP'
        StagingRoot = Join-Path $HostRoot 'Staging'
        BackupRoot = Join-Path $HostRoot 'Backups'
        ExternalApplicationConfigPath = Join-Path $HostRoot 'Config\application.json'
        ReadinessUrl = 'https://REPLACE-HOST.example.invalid/health/ready'
        AsapDatabaseConnectionString = 'Server=REPLACE-SQL-SERVER;Database=REPLACE-ASAP-DATABASE;Integrated Security=True;Encrypt=True;TrustServerCertificate=False'
        HangfireDatabaseConnectionString = 'Server=REPLACE-SQL-SERVER;Database=REPLACE-HANGFIRE-DATABASE;Integrated Security=True;Encrypt=True;TrustServerCertificate=False'
        SqlPackagePath = 'SqlPackage.exe'
        SqlCmdPath = 'sqlcmd.exe'
    }
}

function Initialize-HostFiles {
    param([string] $HostRoot)

    foreach ($name in @('', 'Config', 'DataProtection-Keys', 'Logs', 'Staging', 'Backups')) {
        $directory = if ($name) { Join-Path $HostRoot $name } else { $HostRoot }
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            [void] [IO.Directory]::CreateDirectory($directory)
            Write-Host "Created ASAP-owned directory: $directory"
        }
    }

    $configRoot = Join-Path $HostRoot 'Config'
    Write-TemplateIfMissing (Join-Path $configRoot 'application.json') (New-ApplicationTemplate $HostRoot)
    Write-TemplateIfMissing (Join-Path $configRoot 'deployment.json') (New-DeploymentTemplate $HostRoot)
}

function Read-JsonConfiguration {
    param([string] $Path, [string] $Name)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $script:InitializationRequired = $true
        Add-ValidationError "$Name does not exist at $Path."
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        Add-ValidationError "$Name is not valid JSON: $($_.Exception.Message)"
        return $null
    }
}

function Test-SqlConnectionString {
    param([object] $Value, [string] $Name)

    $text = Require-Text $Value $Name
    if (-not $text) {
        return
    }

    try {
        $builder = [Data.Common.DbConnectionStringBuilder]::new()
        $builder.ConnectionString = $text
        $values = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($key in $builder.Keys) {
            $values[[string] $key] = [string] $builder[$key]
        }

        $server = if ($values.ContainsKey('Server')) { $values['Server'] } elseif ($values.ContainsKey('Data Source')) { $values['Data Source'] } else { $null }
        $database = if ($values.ContainsKey('Database')) { $values['Database'] } elseif ($values.ContainsKey('Initial Catalog')) { $values['Initial Catalog'] } else { $null }
        $integrated = if ($values.ContainsKey('Integrated Security')) { $values['Integrated Security'] } elseif ($values.ContainsKey('Trusted_Connection')) { $values['Trusted_Connection'] } else { $null }
        if (-not $server -or -not $database) {
            throw 'server and database are required'
        }
        if ($integrated -notmatch '^(?i:true|yes|sspi|1)$') {
            throw 'Windows Integrated Security is required'
        }
        if ($values.ContainsKey('User ID') -or $values.ContainsKey('Password')) {
            throw 'embedded SQL credentials are forbidden'
        }
    }
    catch {
        Add-ValidationError "$Name is invalid: $($_.Exception.Message)."
    }
}

function Test-Tool {
    param([object] $ConfiguredPath, [string] $Name)

    $path = Require-Text $ConfiguredPath $Name
    if (-not $path) {
        return
    }

    $found = if ([IO.Path]::IsPathRooted($path)) {
        Test-Path -LiteralPath $path -PathType Leaf
    }
    else {
        $null -ne (Get-Command $path -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
    }
    if (-not $found) {
        Add-ValidationError "$Name was not found at or through '$path'."
    }
}

function Test-Certificate {
    param([object] $Thumbprint)

    $normalized = (Require-Text $Thumbprint 'Application.DataProtectionKeyEncryptionCertificateThumbprint')
    if (-not $normalized) {
        return
    }
    $normalized = $normalized.Replace(' ', '')

    $store = 'Cert:\LocalMachine\My'
    if (Test-Path -LiteralPath $store) {
        $certificate = Get-ChildItem -LiteralPath $store | Where-Object {
            $_.Thumbprint.Replace(' ', '').Equals($normalized, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($certificate -and $certificate.HasPrivateKey) {
            return
        }
    }

    Add-ValidationError "Data Protection certificate '$normalized' did not resolve with a private key in LocalMachine\My."
}

function Test-DotNetHosting {
    $dotnet = Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dotnet) {
        Add-ValidationError 'dotnet was not found.'
    }
    elseif (-not (@(& $dotnet.Source --list-runtimes 2>$null) -match '^Microsoft\.AspNetCore\.App 10\.')) {
        Add-ValidationError 'Microsoft.AspNetCore.App 10 is not installed.'
    }

    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $aspNetCoreModule = if ($programFiles) { Join-Path $programFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll' } else { $null }
    if (-not $aspNetCoreModule -or -not (Test-Path -LiteralPath $aspNetCoreModule -PathType Leaf)) {
        Add-ValidationError 'The .NET 10 ASP.NET Core Hosting Bundle/IIS module is not installed.'
    }
}

function Test-IisConfiguration {
    param([object] $Deployment)

    if (-not $IsWindows) {
        Add-ValidationError 'IIS validation must run on the provisioned Windows host.'
        return
    }
    try {
        Import-Module WebAdministration -ErrorAction Stop
    }
    catch {
        Add-ValidationError "WebAdministration is unavailable: $($_.Exception.Message)"
        return
    }

    $siteName = Require-Text (Get-Value $Deployment 'IisSiteName') 'IisSiteName'
    $poolName = Require-Text (Get-Value $Deployment 'IisAppPoolName') 'IisAppPoolName'
    $deploymentPath = Require-Text (Get-Value $Deployment 'DeploymentPath') 'DeploymentPath'
    if (-not $siteName -or -not $poolName -or -not $deploymentPath) {
        return
    }

    $site = Get-Website -Name $siteName -ErrorAction SilentlyContinue
    if (-not $site) {
        Add-ValidationError "IIS site '$siteName' does not exist."
    }
    else {
        if (-not (Test-EquivalentPath ([Environment]::ExpandEnvironmentVariables([string] $site.PhysicalPath)) $deploymentPath)) {
            Add-ValidationError "IIS site '$siteName' does not point at '$deploymentPath'."
        }
        if (-not ([string] $site.ApplicationPool).Equals($poolName, [StringComparison]::OrdinalIgnoreCase)) {
            Add-ValidationError "IIS site '$siteName' does not use app pool '$poolName'."
        }
        $readinessText = [string] (Get-Value $Deployment 'ReadinessUrl')
        $readinessUri = $null
        $httpsBindings = @(Get-WebBinding -Name $siteName -Protocol 'https' -ErrorAction SilentlyContinue)
        $hasExpectedBinding = $false
        if ([Uri]::TryCreate($readinessText, [UriKind]::Absolute, [ref] $readinessUri)) {
            $bindingSuffix = ":$($readinessUri.Port):$($readinessUri.DnsSafeHost)"
            $hasExpectedBinding = $null -ne ($httpsBindings | Where-Object {
                ([string] $_.bindingInformation).EndsWith($bindingSuffix, [StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1)
        }
        if (-not $hasExpectedBinding) {
            Add-ValidationError "IIS site '$siteName' has no HTTPS binding for the configured readiness host and port."
        }
    }

    if ($null -eq (Get-WebAppPoolState -Name $poolName -ErrorAction SilentlyContinue)) {
        Add-ValidationError "IIS app pool '$poolName' does not exist."
    }
}

function Test-HostPrerequisites {
    param([string] $HostRoot)

    $configRoot = Join-Path $HostRoot 'Config'
    $applicationPath = Join-Path $configRoot 'application.json'
    $keysPath = Join-Path $HostRoot 'DataProtection-Keys'
    $logsPath = Join-Path $HostRoot 'Logs'

    foreach ($name in @('Config', 'DataProtection-Keys', 'Logs', 'Staging', 'Backups')) {
        $directory = Join-Path $HostRoot $name
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            $script:InitializationRequired = $true
            Add-ValidationError "Required ASAP directory is missing: $directory"
        }
    }

    $application = Read-JsonConfiguration $applicationPath 'application.json'
    $deployment = Read-JsonConfiguration (Join-Path $configRoot 'deployment.json') 'deployment.json'

    if ($application) {
        if ((Get-Content -LiteralPath $applicationPath -Raw) -match 'REPLACE-') {
            Add-ValidationError 'application.json contains an unchanged REPLACE-* placeholder.'
        }
        if (-not (Test-EquivalentPath (Get-NestedValue $application @('Application', 'DataProtectionKeysPath')) $keysPath)) {
            Add-ValidationError "Application.DataProtectionKeysPath must be '$keysPath'."
        }
        if (-not (Test-EquivalentPath (Get-NestedValue $application @('Application', 'LogPath')) $logsPath)) {
            Add-ValidationError "Application.LogPath must be '$logsPath'."
        }
        Test-Certificate (Get-NestedValue $application @('Application', 'DataProtectionKeyEncryptionCertificateThumbprint'))
    }

    if ($deployment) {
        foreach ($contract in @(
            @{ Name = 'StagingRoot'; Expected = Join-Path $HostRoot 'Staging' },
            @{ Name = 'BackupRoot'; Expected = Join-Path $HostRoot 'Backups' },
            @{ Name = 'ExternalApplicationConfigPath'; Expected = $applicationPath }
        )) {
            if (-not (Test-EquivalentPath (Get-Value $deployment $contract.Name) $contract.Expected)) {
                Add-ValidationError "$($contract.Name) must be '$($contract.Expected)'."
            }
        }

        $deploymentDirectory = Require-Text (Get-Value $deployment 'DeploymentPath') 'DeploymentPath'
        if ($deploymentDirectory -and -not (Test-Path -LiteralPath $deploymentDirectory -PathType Container)) {
            Add-ValidationError "DeploymentPath '$deploymentDirectory' does not exist; the operator must create it with the IIS site."
        }
        $readinessText = Require-Text (Get-Value $deployment 'ReadinessUrl') 'ReadinessUrl'
        $readinessUri = $null
        if ($readinessText -and (-not [Uri]::TryCreate($readinessText, [UriKind]::Absolute, [ref] $readinessUri) -or
            $readinessUri.Scheme -ne 'https' -or $readinessUri.AbsolutePath.TrimEnd('/') -ne '/health/ready')) {
            Add-ValidationError 'ReadinessUrl must be an absolute HTTPS /health/ready URL for deployment-time use.'
        }
        Test-SqlConnectionString (Get-Value $deployment 'AsapDatabaseConnectionString') 'AsapDatabaseConnectionString'
        Test-SqlConnectionString (Get-Value $deployment 'HangfireDatabaseConnectionString') 'HangfireDatabaseConnectionString'
        Test-Tool (Get-Value $deployment 'SqlPackagePath') 'SqlPackage'
        Test-Tool (Get-Value $deployment 'SqlCmdPath') 'sqlcmd'
    }

    if ($PSVersionTable.PSVersion.Major -lt 7) {
        Add-ValidationError 'Validation requires PowerShell 7 or later.'
    }
    Test-DotNetHosting
    if ($deployment) {
        Test-IisConfiguration $deployment
    }

    return $script:ValidationErrors.Count -eq 0
}

function Invoke-ContractSelfTest {
    $testRoot = Join-Path ([IO.Path]::GetTempPath()) ('asap-host-bootstrap-' + [guid]::NewGuid().ToString('N'))
    try {
        Initialize-HostFiles $testRoot
        foreach ($name in @('Config', 'DataProtection-Keys', 'Logs', 'Staging', 'Backups')) {
            if (-not (Test-Path -LiteralPath (Join-Path $testRoot $name) -PathType Container)) {
                throw "Initialization did not create $name."
            }
        }
        $applicationPath = Join-Path $testRoot 'Config\application.json'
        $original = Get-Content -LiteralPath $applicationPath -Raw
        Initialize-HostFiles $testRoot
        if ((Get-Content -LiteralPath $applicationPath -Raw) -cne $original) {
            throw 'Initialization overwrote existing application configuration.'
        }
        Write-Host 'ASAP test-host bootstrap self-test passed.'
    }
    finally {
        if ([IO.Directory]::Exists($testRoot)) {
            [IO.Directory]::Delete($testRoot, $true)
        }
    }
}

if ($ContractSelfTest -and $Initialize) {
    throw '-ContractSelfTest and -Initialize cannot be combined.'
}
if ($ContractSelfTest) {
    Invoke-ContractSelfTest
    return
}

$resolvedRoot = [IO.Path]::GetFullPath($RootPath)
if ($Initialize) {
    Initialize-HostFiles $resolvedRoot
    Write-Host ''
    Write-Host 'ASAP-owned files and directories are initialized.'
    Write-Host '1. Edit application.json and deployment.json.'
    Write-Host '2. Manually provision IIS, certificates, identities, ACLs, SQL, Entra, and the GitHub Actions runner.'
    Write-Host '3. Rerun this script with no flags to perform read-only validation.'
    return
}

$valid = Test-HostPrerequisites $resolvedRoot
if (-not $valid) {
    $message = "ASAP test host validation failed:`n - " + ($script:ValidationErrors -join "`n - ")
    if ($script:InitializationRequired) {
        $message += "`nRun 'pwsh -File .\Initialize-AsapTestHost.ps1 -Initialize' to create missing ASAP-owned files and directories."
    }
    Write-Error $message
    exit 1
}

Write-Host 'ASAP test host validation passed. The deployment gate may now be enabled.'
