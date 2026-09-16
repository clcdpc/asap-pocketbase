#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SqlServer,

    [ValidatePattern('^Asap[A-Za-z0-9_]+$')]
    [string] $DatabaseName = 'AsapTest',

    [Parameter(Mandatory = $true)]
    [string] $ReadinessUrl,

    [string] $RootPath = 'C:\ProgramData\clc-asap',
    [string] $IisSiteName = 'ASAP-Test',
    [string] $IisAppPoolName = 'ASAP-Test',
    [string] $RunnerServiceName,
    [string] $ServiceAccount,
    [PSCredential] $ServiceCredential,
    [string] $SqlPackagePath,
    [string] $SqlCmdPath,
    [string] $HttpsCertificateThumbprint,
    [string] $EntraClientId,
    [string] $EntraTenantId,
    [string] $EntraObjectId,
    [string] $AdminEmail,
    [SecureString] $EntraClientSecret,
    [string[]] $AllowedRecipientDomains,
    [switch] $TrustServerCertificate,
    [switch] $ForceApplicationConfig,
    [switch] $SkipLocalAdministrator,
    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string] $Status, [string] $Item, [string] $Detail) {
    $results.Add([pscustomobject]@{ Status = $Status; Item = $Item; Detail = $Detail })
}

function Test-Administrator {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-Tool([string] $ConfiguredPath, [string[]] $Names, [string[]] $Candidates) {
    if ($ConfiguredPath) {
        if ([IO.Path]::IsPathRooted($ConfiguredPath)) { return [IO.Path]::GetFullPath($ConfiguredPath) }
        $command = Get-Command $ConfiguredPath -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { return $command.Source }
        return $ConfiguredPath
    }

    foreach ($name in $Names) {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { return $command.Source }
    }
    foreach ($candidate in $Candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [IO.Path]::GetFullPath($candidate) }
    }
    return $Names[0]
}

function Test-Tool([string] $Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return Test-Path -LiteralPath $Path -PathType Leaf }
    return $null -ne (Get-Command $Path -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
}

function Write-Json([string] $Path, [object] $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}

function Assert-Guid([string] $Name, [string] $Value) {
    if (-not $Value) { return }
    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($Value, [ref]$parsed) -or $parsed -eq [guid]::Empty) { throw "$Name must be a non-empty GUID." }
}

function Get-RunnerService {
    $services = @(Get-CimInstance Win32_Service | Where-Object Name -Like 'actions.runner.*')
    if ($RunnerServiceName) { $services = @($services | Where-Object Name -EQ $RunnerServiceName) }
    if ($services.Count -eq 0) { throw 'No matching GitHub Actions runner service (actions.runner.*) was found.' }
    if ($services.Count -gt 1) { throw "Multiple runner services were found: $(($services.Name | Sort-Object) -join ', '). Re-run with -RunnerServiceName." }
    return $services[0]
}

function Get-ServiceIdentity([object] $RunnerService) {
    $detected = [string]$RunnerService.StartName
    $account = if ($ServiceAccount) { $ServiceAccount } else { $detected }
    if ($account -match '^(?i:LocalSystem|NT AUTHORITY\\(?:SYSTEM|LOCAL SERVICE|NETWORK SERVICE))$') { throw "Runner identity '$account' is a built-in account; configure the dedicated AD service account first." }
    try {
        $sid = ([Security.Principal.NTAccount]::new($account)).Translate([Security.Principal.SecurityIdentifier])
    }
    catch { throw "Windows account '$account' could not be resolved." }

    if ($ServiceAccount -and -not $detected.Equals($ServiceAccount, [StringComparison]::OrdinalIgnoreCase)) {
        Add-Result 'NEEDS ATTENTION' 'Runner identity' "Runner uses '$detected' but override specifies '$ServiceAccount'."
    }
    else { Add-Result 'PASS' 'Runner identity' "$($RunnerService.Name) runs as $account." }
    return [pscustomobject]@{ Name = $account; Sid = $sid }
}

function Ensure-Directory([string] $Path) {
    if (Test-Path -LiteralPath $Path -PathType Container) { return }
    if ($ValidateOnly) { Add-Result 'NEEDS ATTENTION' 'Directory' "$Path does not exist."; return }
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Set-RootPermissions([string] $Path, [string] $Account) {
    if ($ValidateOnly) { return }
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.NTAccount]::new('BUILTIN\Administrators'))
    foreach ($entry in @(
        @{ Name = 'NT AUTHORITY\SYSTEM'; Rights = 'FullControl' },
        @{ Name = 'BUILTIN\Administrators'; Rights = 'FullControl' },
        @{ Name = $Account; Rights = 'Modify' }
    )) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($entry.Name, $entry.Rights, $inheritance, 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Ensure-Group([string] $GroupSid, [string] $Label, [string] $Account, [Security.Principal.SecurityIdentifier] $AccountSid, [bool] $ReportOnly) {
    $group = Get-LocalGroup -SID $GroupSid
    $members = @(Get-LocalGroupMember -Group $group -ErrorAction Stop)
    if ($members.SID.Value -contains $AccountSid.Value) { Add-Result 'PASS' $Label "$Account is a member of $($group.Name)."; return }
    if ($ValidateOnly -or $ReportOnly) { Add-Result 'NEEDS ATTENTION' $Label "$Account is not a member of $($group.Name)."; return }
    Add-LocalGroupMember -Group $group -Member $Account
    Add-Result 'PASS' $Label "Added $Account to $($group.Name)."
}

function Get-DataProtectionCertificate([string] $Thumbprint) {
    $certificates = @(Get-ChildItem Cert:\LocalMachine\My)
    if ($Thumbprint -and -not $Thumbprint.StartsWith('REPLACE-', [StringComparison]::OrdinalIgnoreCase)) {
        return $certificates | Where-Object { $_.HasPrivateKey -and $_.Thumbprint.Replace(' ', '').Equals($Thumbprint.Replace(' ', ''), [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
    }
    return $certificates | Where-Object { $_.HasPrivateKey -and $_.Subject -eq 'CN=CLC ASAP Test Data Protection' -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Sort-Object NotAfter -Descending | Select-Object -First 1
}

function Grant-CertificateRead([Security.Cryptography.X509Certificates.X509Certificate2] $Certificate, [string] $Account) {
    if ($ValidateOnly) { return }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    try {
        if ($rsa -is [Security.Cryptography.RSACng]) { $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($rsa.Key.UniqueName)" }
        elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) { $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($rsa.CspKeyContainerInfo.UniqueKeyContainerName)" }
        else { throw 'Unsupported Data Protection private-key provider.' }
        if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) { throw "Private-key file was not found at $keyPath." }
        $acl = Get-Acl -LiteralPath $keyPath
        $acl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new($Account, 'Read', 'Allow'))
        Set-Acl -LiteralPath $keyPath -AclObject $acl
    }
    finally { if ($rsa) { $rsa.Dispose() } }
}

function New-AppConfig([string] $ConnectionString, [string] $KeysPath, [string] $LogsPath, [string] $CertificateThumbprint) {
    Assert-Guid 'EntraClientId' $EntraClientId
    Assert-Guid 'EntraTenantId' $EntraTenantId
    Assert-Guid 'EntraObjectId' $EntraObjectId
    if ($AdminEmail -and $AdminEmail -notmatch '^[^@\s]+@[^@\s]+$') { throw 'AdminEmail must be an email address.' }

    $clientId = if ($EntraClientId) { $EntraClientId } else { 'REPLACE-LOCALLY' }
    $tenantId = if ($EntraTenantId) { $EntraTenantId } else { 'REPLACE-LOCALLY' }
    $objectId = if ($EntraObjectId) { $EntraObjectId } else { 'REPLACE-LOCALLY' }
    $email = if ($AdminEmail) { $AdminEmail } else { 'admin@example.org' }
    $secret = if ($EntraClientSecret) { ([Net.NetworkCredential]::new('', $EntraClientSecret)).Password } else { 'REPLACE-LOCALLY' }
    $domains = if ($AllowedRecipientDomains) { @($AllowedRecipientDomains) } elseif ($AdminEmail) { @($AdminEmail.Split('@')[-1]) } else { @('example.org') }

    try {
        return [ordered]@{
            Environment = [ordered]@{ Name = 'Test'; IsNonProduction = $true; UiBannerText = 'NON-PRODUCTION' }
            ConnectionStrings = [ordered]@{ AsapDatabase = $ConnectionString; HangfireDatabase = $ConnectionString }
            Authentication = [ordered]@{ Entra = [ordered]@{
                ClientId = $clientId; ClientSecret = $secret; AllowedTenantIds = @($tenantId)
                InitialSuperAdmin = [ordered]@{ TenantId = $tenantId; ObjectId = $objectId; UserPrincipalName = $email; DisplayName = 'ASAP Test Administrator'; NotificationEmail = $email }
            }}
            Application = [ordered]@{ BusinessTimeZone = 'America/New_York'; DataProtectionKeysPath = $KeysPath; DataProtectionKeyEncryptionCertificateThumbprint = $CertificateThumbprint; LogPath = $LogsPath }
            EmailSafety = [ordered]@{ AllowedRecipientDomains = $domains }
            PatronLoginRateLimit = [ordered]@{ PermitLimit = 20; WindowSeconds = 300 }
            Hangfire = [ordered]@{
                Schedules = [ordered]@{ WorkflowProcessing = '0 * * * *'; IdentifierProcessing = '*/5 * * * *'; OrganizationRefresh = '0 2 * * *'; WeeklyStaffSummary = '0 20 * * 0'; EmailOutboxSweep = '*/5 * * * *'; PatronSessionCleanup = '0 3 * * *'; EmailPayloadCleanup = '30 3 * * *' }
                ProcessingLimits = [ordered]@{
                    Default = [ordered]@{ PageSize = 50; MaxPerRun = 500 }
                    Timeouts = [ordered]@{ PageSize = $null; MaxPerRun = $null }
                    Queues = [ordered]@{
                        IdentifierProcessing = [ordered]@{ PageSize = $null; MaxPerRun = $null }; PurchasePromotion = [ordered]@{ PageSize = $null; MaxPerRun = $null }; HoldPlacement = [ordered]@{ PageSize = $null; MaxPerRun = $null }; FulfillmentTracking = [ordered]@{ PageSize = $null; MaxPerRun = $null }; OutstandingTimeout = [ordered]@{ PageSize = $null; MaxPerRun = $null }; PendingHoldTimeout = [ordered]@{ PageSize = $null; MaxPerRun = $null }; HoldPickupTimeout = [ordered]@{ PageSize = $null; MaxPerRun = $null }; AdditionalCopyTimeout = [ordered]@{ PageSize = $null; MaxPerRun = $null }
                    }
                }
            }
        }
    }
    finally { $secret = $null }
}

function Set-AppPoolConfig([string] $PoolName, [string] $ConfigPath) {
    $collection = "system.applicationHost/applicationPools/add[@name='$PoolName']/environmentVariables"
    $item = "$collection/add[@name='Asap__ConfigFile']"
    $current = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $item -Name 'value' -ErrorAction SilentlyContinue
    if ($current -and [string]$current.Value -eq $ConfigPath) { Add-Result 'PASS' 'IIS config pointer' "Asap__ConfigFile is $ConfigPath."; return }
    if ($ValidateOnly) { Add-Result 'NEEDS ATTENTION' 'IIS config pointer' "Asap__ConfigFile is not $ConfigPath."; return }
    if ($current) { Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $collection -Name '.' -AtElement @{ name = 'Asap__ConfigFile' } }
    Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $collection -Name '.' -Value @{ name = 'Asap__ConfigFile'; value = $ConfigPath }
    Add-Result 'PASS' 'IIS config pointer' "Set Asap__ConfigFile to $ConfigPath."
}

function Ensure-AppPool([string] $PoolName, [string] $Account, [string] $ConfigPath) {
    $poolPath = "IIS:\AppPools\$PoolName"
    if (-not (Test-Path -LiteralPath $poolPath)) {
        if ($ValidateOnly) { Add-Result 'NEEDS ATTENTION' 'IIS app pool' "$PoolName does not exist."; return }
        New-WebAppPool -Name $PoolName | Out-Null
    }

    $pool = Get-Item -LiteralPath $poolPath
    $identityMatches = [int]$pool.processModel.identityType -eq 3 -and ([string]$pool.processModel.userName).Equals($Account, [StringComparison]::OrdinalIgnoreCase)
    if (-not $identityMatches) {
        if ($ValidateOnly) { Add-Result 'NEEDS ATTENTION' 'IIS app pool identity' "$PoolName is not configured as $Account."; return }
        $password = ''
        if (-not $Account.EndsWith('$', [StringComparison]::Ordinal)) {
            if (-not $script:ServiceCredential) { $script:ServiceCredential = Get-Credential -UserName $Account -Message "Password for IIS app pool identity $Account" }
            $password = ([Net.NetworkCredential]::new('', $script:ServiceCredential.Password)).Password
        }
        try {
            Set-ItemProperty -LiteralPath $poolPath -Name processModel.identityType -Value 3
            Set-ItemProperty -LiteralPath $poolPath -Name processModel.userName -Value $Account
            Set-ItemProperty -LiteralPath $poolPath -Name processModel.password -Value $password
        }
        finally { $password = $null }
    }
    if (-not $ValidateOnly) { Set-ItemProperty -LiteralPath $poolPath -Name managedRuntimeVersion -Value '' }
    Add-Result 'PASS' 'IIS app pool identity' "$PoolName runs as $Account."
    Set-AppPoolConfig $PoolName $ConfigPath
}

function Ensure-Site([string] $SiteName, [string] $PoolName, [string] $PhysicalPath, [Uri] $ReadyUri) {
    $site = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
    if (-not $site) {
        if (-not $HttpsCertificateThumbprint) { Add-Result 'NEEDS ATTENTION' 'IIS site' "$SiteName does not exist. Supply -HttpsCertificateThumbprint or create its HTTPS binding manually."; return }
        $certificate = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint.Replace(' ', '').Equals($HttpsCertificateThumbprint.Replace(' ', ''), [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if (-not $certificate) { throw "HTTPS certificate '$HttpsCertificateThumbprint' was not found in LocalMachine\\My." }
        if ($ValidateOnly) { Add-Result 'NEEDS ATTENTION' 'IIS site' "$SiteName does not exist."; return }
        $port = if ($ReadyUri.IsDefaultPort) { 443 } else { $ReadyUri.Port }
        New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $PoolName -Port $port -HostHeader $ReadyUri.DnsSafeHost -Ssl | Out-Null
        (Get-WebBinding -Name $SiteName -Protocol https | Select-Object -First 1).AddSslCertificate($certificate.Thumbprint, 'My')
        $site = Get-Website -Name $SiteName
    }

    $actualPath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables([string]$site.PhysicalPath)).TrimEnd('\')
    if (-not $actualPath.Equals($PhysicalPath, [StringComparison]::OrdinalIgnoreCase)) { throw "$SiteName points to '$actualPath', not '$PhysicalPath'." }
    if (-not ([string]$site.ApplicationPool).Equals($PoolName, [StringComparison]::OrdinalIgnoreCase)) { throw "$SiteName uses app pool '$($site.ApplicationPool)', not '$PoolName'." }
    Add-Result 'PASS' 'IIS site' "$SiteName -> $PhysicalPath using $PoolName."
}

if (-not $ValidateOnly -and -not (Test-Administrator)) { throw 'Run this bootstrap from an elevated PowerShell 7 session.' }
try { $readyUri = [Uri]::new($ReadinessUrl, [UriKind]::Absolute) } catch { throw 'ReadinessUrl must be an absolute HTTPS /health/ready URL.' }
if ($readyUri.Scheme -ne 'https' -or $readyUri.AbsolutePath.TrimEnd('/') -ne '/health/ready') { throw 'ReadinessUrl must be an absolute HTTPS /health/ready URL.' }
if (-not [IO.Path]::IsPathFullyQualified($RootPath)) { throw 'RootPath must be absolute.' }

$root = [IO.Path]::GetFullPath($RootPath).TrimEnd('\')
$webRoot = Join-Path $root 'web'
$stagingRoot = Join-Path $root 'staging'
$backupRoot = Join-Path $root 'backups'
$configRoot = Join-Path $root 'config'
$keysRoot = Join-Path $root 'keys'
$logsRoot = Join-Path $root 'logs'
$deploymentConfigPath = Join-Path $configRoot 'test-deployment.json'
$appConfigPath = Join-Path $configRoot 'test-app.json'

$runner = Get-RunnerService
$identity = Get-ServiceIdentity $runner
Add-Result $(if ([string]$runner.State -eq 'Running') { 'PASS' } else { 'NEEDS ATTENTION' }) 'GitHub Actions runner' "$($runner.Name) state is $($runner.State)."

foreach ($directory in @($root, $webRoot, $stagingRoot, $backupRoot, $configRoot, $keysRoot, $logsRoot)) { Ensure-Directory $directory }
if (Test-Path -LiteralPath $root -PathType Container) { Set-RootPermissions $root $identity.Name; Add-Result 'PASS' 'Filesystem permissions' "SYSTEM/Administrators have FullControl; $($identity.Name) has Modify under $root." }

try {
    Ensure-Group 'S-1-5-32-568' 'IIS_IUSRS membership' $identity.Name $identity.Sid $false
    Ensure-Group 'S-1-5-32-544' 'IIS lifecycle permission' $identity.Name $identity.Sid ([bool]$SkipLocalAdministrator)
}
catch { Add-Result 'NEEDS ATTENTION' 'Windows group permissions' $_.Exception.Message }

$resolvedSqlPackage = Resolve-Tool $SqlPackagePath @('SqlPackage.exe', 'SqlPackage') @('C:\Program Files\Microsoft SQL Server\170\DAC\bin\SqlPackage.exe', 'C:\Program Files\Microsoft SQL Server\160\DAC\bin\SqlPackage.exe', 'C:\Program Files\Microsoft SQL Server\150\DAC\bin\SqlPackage.exe')
$resolvedSqlCmd = Resolve-Tool $SqlCmdPath @('sqlcmd.exe', 'sqlcmd') @('C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE', 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE', 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\160\Tools\Binn\SQLCMD.EXE')
foreach ($tool in @(@{ Name = 'PowerShell 7'; Path = 'pwsh.exe' }, @{ Name = 'SqlPackage'; Path = $resolvedSqlPackage }, @{ Name = 'sqlcmd'; Path = $resolvedSqlCmd })) {
    Add-Result $(if (Test-Tool $tool.Path) { 'PASS' } else { 'NEEDS ATTENTION' }) $tool.Name $(if (Test-Tool $tool.Path) { "$($tool.Path) is available." } else { "$($tool.Path) was not found." })
}

$trust = if ($TrustServerCertificate) { 'True' } else { 'False' }
$connectionString = "Server=$SqlServer;Database=$DatabaseName;Integrated Security=True;Encrypt=True;TrustServerCertificate=$trust"
$appConfig = if (Test-Path -LiteralPath $appConfigPath -PathType Leaf) { Get-Content -LiteralPath $appConfigPath -Raw | ConvertFrom-Json } else { $null }
$certificate = Get-DataProtectionCertificate $(if ($appConfig) { [string]$appConfig.Application.DataProtectionKeyEncryptionCertificateThumbprint } else { $null })
if (($null -eq $appConfig -or $ForceApplicationConfig) -and -not $certificate -and -not $ValidateOnly) {
    $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=CLC ASAP Test Data Protection' -KeyAlgorithm RSA -KeyLength 2048 -KeyUsage KeyEncipherment,DataEncipherment -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddYears(5)
}
if ($certificate) { Grant-CertificateRead $certificate $identity.Name; Add-Result 'PASS' 'Data Protection certificate' "Using LocalMachine\\My $($certificate.Thumbprint)." }
else { Add-Result 'NEEDS ATTENTION' 'Data Protection certificate' 'No usable certificate with a private key is available.' }

if ($null -eq $appConfig -or $ForceApplicationConfig) {
    if ($ValidateOnly) { Add-Result 'NEEDS ATTENTION' 'Application configuration' "$appConfigPath is absent or would be replaced." }
    elseif (-not $certificate) { Add-Result 'NEEDS ATTENTION' 'Application configuration' 'Not written because no Data Protection certificate is available.' }
    else { Write-Json $appConfigPath (New-AppConfig $connectionString $keysRoot $logsRoot $certificate.Thumbprint); $appConfig = Get-Content -LiteralPath $appConfigPath -Raw | ConvertFrom-Json; Add-Result 'PASS' 'Application configuration' "Created $appConfigPath." }
}
else { Add-Result 'PASS' 'Application configuration' "Preserved existing $appConfigPath." }

if ($appConfig) {
    $requiredEntra = @([string]$appConfig.Authentication.Entra.ClientId, [string]$appConfig.Authentication.Entra.ClientSecret, [string]$appConfig.Authentication.Entra.InitialSuperAdmin.TenantId, [string]$appConfig.Authentication.Entra.InitialSuperAdmin.ObjectId)
    if ($requiredEntra | Where-Object { $_.StartsWith('REPLACE-', [StringComparison]::OrdinalIgnoreCase) }) { Add-Result 'NEEDS ATTENTION' 'Entra configuration' "Complete REPLACE-LOCALLY values in $appConfigPath." }
    else { Add-Result 'PASS' 'Entra configuration' 'Required Entra fields are populated.' }
}

$deploymentConfig = [ordered]@{ IisSiteName = $IisSiteName; IisAppPoolName = $IisAppPoolName; DeploymentPath = $webRoot; StagingRoot = $stagingRoot; BackupRoot = $backupRoot; ExternalApplicationConfigPath = $appConfigPath; ReadinessUrl = $readyUri.AbsoluteUri; AsapDatabaseConnectionString = $connectionString; HangfireDatabaseConnectionString = $connectionString; SqlPackagePath = $resolvedSqlPackage; SqlCmdPath = $resolvedSqlCmd }
if ($ValidateOnly) {
    Add-Result $(if (Test-Path -LiteralPath $deploymentConfigPath -PathType Leaf) { 'PASS' } else { 'NEEDS ATTENTION' }) 'Deployment configuration' "$deploymentConfigPath $(if (Test-Path -LiteralPath $deploymentConfigPath -PathType Leaf) { 'exists' } else { 'does not exist' })."
}
else { Write-Json $deploymentConfigPath $deploymentConfig; Add-Result 'PASS' 'Deployment configuration' "Wrote $deploymentConfigPath." }

try {
    Import-Module WebAdministration -ErrorAction Stop
    Ensure-AppPool $IisAppPoolName $identity.Name $appConfigPath
    Ensure-Site $IisSiteName $IisAppPoolName $webRoot $readyUri
}
catch { Add-Result 'NEEDS ATTENTION' 'IIS configuration' $_.Exception.Message }

Add-Result 'INFO' 'SQL authorization' "Grant $($identity.Name) the test-only SQL rights required for DACPAC publish, Hangfire schema preparation, and ASAP runtime access to $SqlServer/$DatabaseName. This script does not grant SQL privileges."
Add-Result 'INFO' 'Repository activation' 'Leave ASAP_TEST_DEPLOYMENT_ENABLED unset/false until all blocking NEEDS ATTENTION items and SQL authorization are resolved.'

Write-Host ''
Write-Host 'ASAP test host bootstrap summary'
$results | Format-Table -AutoSize -Wrap
$attention = @($results | Where-Object Status -EQ 'NEEDS ATTENTION').Count
if ($attention) { Write-Warning "$attention item(s) still need attention before the first live test deployment." }
else { Write-Host 'Host bootstrap checks passed. Confirm SQL authorization before enabling the GitHub deployment gate.' }
