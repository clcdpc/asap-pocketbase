[CmdletBinding()]
param(
    [string] $SqlServer = 'localhost',

    [ValidatePattern('^Asap[A-Za-z0-9_]+$')]
    [string] $DatabaseName = 'AsapDevelopment',

    [Parameter(Mandatory = $true)]
    [ValidateScript({ try { [void][guid]$_; $true } catch { $false } })]
    [string] $EntraClientId,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ try { [void][guid]$_; $true } catch { $false } })]
    [string] $EntraTenantId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[^@\s]+@[^@\s]+$')]
    [string] $AdminEmail,

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$webProject = Join-Path $repoRoot 'src\Asap.Web'
$configPath = Join-Path $webProject 'Development.local.json'

if ((Test-Path -LiteralPath $configPath) -and -not $Force) {
    throw "Development.local.json already exists. Re-run with -Force to replace it."
}

$localRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CLC\ASAP\Development'
$keysPath = Join-Path $localRoot 'DataProtection-Keys'
$logPath = Join-Path $localRoot 'Logs'
New-Item -ItemType Directory -Force -Path $keysPath, $logPath | Out-Null

$certificate = New-SelfSignedCertificate `
    -Type Custom `
    -Subject 'CN=ASAP Development Data Protection' `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -KeyExportPolicy Exportable `
    -KeyUsage KeyEncipherment, DataEncipherment `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears(5)

$connectionString = "Server=$SqlServer;Database=$DatabaseName;Integrated Security=True;TrustServerCertificate=True"

$configuration = [ordered]@{
    Environment = [ordered]@{
        Name = 'Development'
        IsNonProduction = $true
        UiBannerText = 'NON-PRODUCTION'
    }
    ConnectionStrings = [ordered]@{
        AsapDatabase = $connectionString
        HangfireDatabase = $connectionString
    }
    Authentication = [ordered]@{
        Entra = [ordered]@{
            ClientId = $EntraClientId
            AllowedTenantIds = @($EntraTenantId)
            InitialSuperAdmin = [ordered]@{
                UserPrincipalName = $AdminEmail
                DisplayName = 'ASAP Development Administrator'
                NotificationEmail = $AdminEmail
            }
        }
    }
    Application = [ordered]@{
        BusinessTimeZone = 'America/New_York'
        DataProtectionKeysPath = $keysPath
        DataProtectionKeyEncryptionCertificateThumbprint = $certificate.Thumbprint
        LogPath = $logPath
    }
    EmailSafety = [ordered]@{
        AllowedRecipientDomains = @($AdminEmail.Split('@')[-1])
    }
    Hangfire = [ordered]@{
        Schedules = [ordered]@{
            WorkflowProcessing = '0 * * * *'
            IdentifierProcessing = '*/5 * * * *'
            OrganizationRefresh = '0 2 * * *'
            WeeklyStaffSummary = '0 20 * * 0'
            EmailOutboxSweep = '*/5 * * * *'
            PatronSessionCleanup = '0 3 * * *'
            EmailPayloadCleanup = '30 3 * * *'
        }
        ProcessingLimits = [ordered]@{
            Default = [ordered]@{ PageSize = 50; MaxPerRun = 500 }
            Timeouts = [ordered]@{ PageSize = $null; MaxPerRun = $null }
            Queues = [ordered]@{
                IdentifierProcessing = [ordered]@{ PageSize = $null; MaxPerRun = $null }
                PurchasePromotion = [ordered]@{ PageSize = $null; MaxPerRun = $null }
                HoldPlacement = [ordered]@{ PageSize = $null; MaxPerRun = $null }
                FulfillmentTracking = [ordered]@{ PageSize = $null; MaxPerRun = $null }
                OutstandingTimeout = [ordered]@{ PageSize = $null; MaxPerRun = $null }
                PendingHoldTimeout = [ordered]@{ PageSize = $null; MaxPerRun = $null }
                HoldPickupTimeout = [ordered]@{ PageSize = $null; MaxPerRun = $null }
                AdditionalCopyTimeout = [ordered]@{ PageSize = $null; MaxPerRun = $null }
            }
        }
    }
}

$configuration | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8
$plainSecret = $null

Write-Host "Created $configPath"
Write-Host "Created certificate $($certificate.Thumbprint) in Cert:\CurrentUser\My"
Write-Host 'F5 will create the missing dedicated database and publish the DACPAC with data-loss blocking.'
