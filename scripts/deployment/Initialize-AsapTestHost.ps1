#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $SqlServer,

    [ValidatePattern('^Asap[A-Za-z0-9_]+$')]
    [string] $DatabaseName = 'AsapTest',

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
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

function Add-Result {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('PASS', 'INFO', 'NEEDS ATTENTION')]
        [string] $Status,

        [Parameter(Mandatory = $true)]
        [string] $Item,

        [Parameter(Mandatory = $true)]
        [string] $Detail
    )

    $results.Add([pscustomobject]@{
        Status = $Status
        Item = $Item
        Detail = $Detail
    })
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-AbsolutePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if (-not [IO.Path]::IsPathFullyQualified($Path)) {
        throw "$Name must be an absolute path."
    }

    return [IO.Path]::GetFullPath($Path).TrimEnd([char]'\', [char]'/')
}

function Get-ActionsRunnerService {
    $services = @(Get-CimInstance Win32_Service | Where-Object Name -Like 'actions.runner.*')

    if (-not [string]::IsNullOrWhiteSpace($RunnerServiceName)) {
        $services = @($services | Where-Object Name -EQ $RunnerServiceName)
    }

    if ($services.Count -eq 0) {
        throw 'No matching GitHub Actions runner Windows service (actions.runner.*) was found.'
    }

    if ($services.Count -gt 1) {
        $names = ($services.Name | Sort-Object) -join ', '
        throw "Multiple GitHub Actions runner services were found ($names). Re-run with -RunnerServiceName."
    }

    return $services[0]
}

function Get-ServiceIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [object] $RunnerService
    )

    $detectedAccount = [string]$RunnerService.StartName
    $account = if ([string]::IsNullOrWhiteSpace($ServiceAccount)) { $detectedAccount } else { $ServiceAccount }

    if ($account -match '^(?i:LocalSystem|NT AUTHORITY\\(?:SYSTEM|LOCAL SERVICE|NETWORK SERVICE))$') {
        throw "The runner uses built-in identity '$account'. Configure it with the dedicated AD service account first."
    }

    try {
        $ntAccount = [Security.Principal.NTAccount]::new($account)
        $sid = $ntAccount.Translate([Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "Windows account '$account' could not be resolved."
    }

    if (-not [string]::IsNullOrWhiteSpace($ServiceAccount) -and
        -not $detectedAccount.Equals($ServiceAccount, [StringComparison]::OrdinalIgnoreCase)) {
        Add-Result 'NEEDS ATTENTION' 'Runner service identity' "Runner uses '$detectedAccount'; override specifies '$ServiceAccount'."
    }
    else {
        Add-Result 'PASS' 'Runner service identity' "$($RunnerService.Name) runs as $account."
    }

    return [pscustomobject]@{ Name = $account; Sid = $sid }
}

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (Test-Path -LiteralPath $Path -PathType Container) {
        return
    }

    if ($ValidateOnly) {
        Add-Result 'NEEDS ATTENTION' 'Directory' "$Path does not exist."
        return
    }

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Set-ApplicationRootAcl {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Account
    )

    if ($ValidateOnly) {
        return
    }

    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.NTAccount]::new('BUILTIN\Administrators'))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('NT AUTHORITY\SYSTEM', 'FullControl', $inheritance, $propagation, $allow))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('BUILTIN\Administrators', 'FullControl', $inheritance, $propagation, $allow))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($Account, 'Modify', $inheritance, $propagation, $allow))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Ensure-LocalGroupMembership {
    param(
        [Parameter(Mandatory = $true)][string] $GroupSid,
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $Account,
        [Parameter(Mandatory = $true)][Security.Principal.SecurityIdentifier] $AccountSid,
        [switch] $ReportOnly
    )

    $group = Get-LocalGroup -SID $GroupSid
    $members = @(Get-LocalGroupMember -Group $group -ErrorAction Stop)
    if ($members.SID.Value -contains $AccountSid.Value) {
        Add-Result 'PASS' $Label "$Account is a direct member of $($group.Name)."
        return
    }

    if ($ValidateOnly -or $ReportOnly) {
        Add-Result 'NEEDS ATTENTION' $Label "$Account is not a direct member of $($group.Name)."
        return
    }

    Add-LocalGroupMember -Group $group -Member $Account
    Add-Result 'PASS' $Label "Added $Account to $($group.Name)."
}

function Resolve-Executable {
    param(
        [string] $ConfiguredPath,
        [Parameter(Mandatory = $true)][string[]] $Names,
        [Parameter(Mandatory = $true)][string[]] $Candidates
    )

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        if ([IO.Path]::IsPathRooted($ConfiguredPath)) {
            return [IO.Path]::GetFullPath($ConfiguredPath)
        }

        $command = Get-Command $ConfiguredPath -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        return if ($null -eq $command) { $ConfiguredPath } else { $command.Source }
    }

    foreach ($name in $Names) {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }
    }

    foreach ($candidate in $Candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    return $Names[0]
}

function Test-Executable {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return Test-Path -LiteralPath $Path -PathType Leaf
    }

    return $null -ne (Get-Command $Path -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
}

function Write-Json {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][object] $Value
    )

    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}

function Get-ConfiguredApplication {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Existing application configuration '$Path' is not valid JSON."
    }
}

function Get-DataProtectionCertificate {
    param([string] $Thumbprint)

    $certificates = @(Get-ChildItem Cert:\LocalMachine\My)
    if (-not [string]::IsNullOrWhiteSpace($Thumbprint) -and -not $Thumbprint.StartsWith('REPLACE-', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $Thumbprint.Replace(' ', '')
        return $certificates |
            Where-Object { $_.HasPrivateKey -and $_.Thumbprint.Replace(' ', '').Equals($normalized, [StringComparison]::OrdinalIgnoreCase) } |
            Select-Object -First 1
    }

    return $certificates |
        Where-Object { $_.HasPrivateKey -and $_.Subject -eq 'CN=CLC ASAP Test Data Protection' -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function Grant-CertificatePrivateKeyRead {
    param(
        [Parameter(Mandatory = $true)][Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
        [Parameter(Mandatory = $true)][string] $Account
    )

    if ($ValidateOnly) {
        return
    }

    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if ($null -eq $rsa) {
        throw 'The Data Protection certificate does not expose an RSA private key.'
    }

    try {
        if ($rsa -is [Security.Cryptography.RSACng]) {
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($rsa.Key.UniqueName)"
        }
        elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) {
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($rsa.CspKeyContainerInfo.UniqueKeyContainerName)"
        }
        else {
            throw 'The Data Protection private-key provider is unsupported by this bootstrap.'
        }

        if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
            throw "The Data Protection certificate private-key file was not found at $keyPath."
        }

        $acl = Get-Acl -LiteralPath $keyPath
        $acl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new($Account, 'Read', 'Allow'))
        Set-Acl -LiteralPath $keyPath -AclObject $acl
    }
    finally {
        $rsa.Dispose()
    }
}

function Assert-GuidWhenSupplied {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [string] $Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($Value, [ref]$parsed) -or $parsed -eq [guid]::Empty) {
        throw "$Name must be a non-empty GUID when supplied."
    }
}

function New-TestApplicationConfiguration {
    param(
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][string] $KeysPath,
        [Parameter(Mandatory = $true)][string] $LogsPath,
        [Parameter(Mandatory = $true)][string] $CertificateThumbprint
    )

    Assert-GuidWhenSupplied 'EntraClientId' $EntraClientId
    Assert-GuidWhenSupplied 'EntraTenantId' $EntraTenantId
    Assert-GuidWhenSupplied 'EntraObjectId' $EntraObjectId
    if (-not [string]::IsNullOrWhiteSpace($AdminEmail) -and $AdminEmail -notmatch '^[^@\s]+@[^@\s]+$') {
        throw 'AdminEmail must be an email address when supplied.'
    }

    $clientId = if ($EntraClientId) { $EntraClientId } else { 'REPLACE-LOCALLY' }
    $tenantId = if ($EntraTenantId) { $EntraTenantId } else { 'REPLACE-LOCALLY' }
    $objectId = if ($EntraObjectId) { $EntraObjectId } else { 'REPLACE-LOCALLY' }
    $email = if ($AdminEmail) { $AdminEmail } else { 'admin@example.org' }
    $secret = if ($null -eq $EntraClientSecret) { 'REPLACE-LOCALLY' } else { ([Net.NetworkCredential]::new('', $EntraClientSecret)).Password }
    $domains = if ($AllowedRecipientDomains) { @($AllowedRecipientDomains) } elseif ($AdminEmail) { @($AdminEmail.Split('@')[-1]) } else { @('example.org') }

    try {
        return [ordered]@{
            Environment = [ordered]@{ Name = 'Test'; IsNonProduction = $true; UiBannerText = 'NON-PRODUCTION' }
            ConnectionStrings = [ordered]@{ AsapDatabase = $ConnectionString; HangfireDatabase = $ConnectionString }
            Authentication = [ordered]@{
                Entra = [ordered]@{
                    ClientId = $clientId
                    ClientSecret = $secret
                    AllowedTenantIds = @($tenantId)
                    InitialSuperAdmin = [ordered]@{
                        TenantId = $tenantId
                        ObjectId = $objectId
                        UserPrincipalName = $email
                        DisplayName = 'ASAP Test Administrator'
                        NotificationEmail = $email
                    }
                }
            }
            Application = [ordered]@{
                BusinessTimeZone = 'America/New_York'
                DataProtectionKeysPath = $KeysPath
                DataProtectionKeyEncryptionCertificateThumbprint = $CertificateThumbprint
                LogPath = $LogsPath
            }
            EmailSafety = [ordered]@{ AllowedRecipientDomains = $domains }
            PatronLoginRateLimit = [ordered]@{ PermitLimit = 20; WindowSeconds = 300 }
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
    }
    finally {
        $secret = $null
    }
}

function Set-AppPoolConfigPointer {
    param(
        [Parameter(Mandatory = $true)][string] $PoolName,
        [Parameter(Mandatory = $true)][string] $ConfigPath
    )

    $collectionFilter = "system.applicationHost/applicationPools/add[@name='$PoolName']/environmentVariables"
    $itemFilter = "$collectionFilter/add[@name='Asap__ConfigFile']"
    $current = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $itemFilter -Name 'value' -ErrorAction SilentlyContinue
    $currentValue = if ($null -eq $current) { $null } else { [string]$current.Value }

    if ($currentValue -eq $ConfigPath) {
        Add-Result 'PASS' 'IIS application config pointer' "Asap__ConfigFile is $ConfigPath."
        return
    }

    if ($ValidateOnly) {
        Add-Result 'NEEDS ATTENTION' 'IIS application config pointer' "Asap__ConfigFile is not set to $ConfigPath."
        return
    }

    if ($null -ne $current) {
        Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $collectionFilter -Name '.' -AtElement @{ name = 'Asap__ConfigFile' }
    }
    Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $collectionFilter -Name '.' -Value @{ name = 'Asap__ConfigFile'; value = $ConfigPath }
    Add-Result 'PASS' 'IIS application config pointer' "Set Asap__ConfigFile to $ConfigPath."
}

function Ensure-AppPool {
    param(
        [Parameter(Mandatory = $true)][string] $PoolName,
        [Parameter(Mandatory = $true)][string] $Account,
        [Parameter(Mandatory = $true)][string] $ConfigPath
    )

    $poolPath = "IIS:\AppPools\$PoolName"
    if (-not (Test-Path -LiteralPath $poolPath)) {
        if ($ValidateOnly) {
            Add-Result 'NEEDS ATTENTION' 'IIS application pool' "$PoolName does not exist."
            return
        }
        New-WebAppPool -Name $PoolName | Out-Null
    }

    $pool = Get-Item -LiteralPath $poolPath
    $matches = [int]$pool.processModel.identityType -eq 3 -and ([string]$pool.processModel.userName).Equals($Account, [StringComparison]::OrdinalIgnoreCase)
    if (-not $matches) {
        if ($ValidateOnly) {
            Add-Result 'NEEDS ATTENTION' 'IIS application pool identity' "$PoolName is not configured as $Account."
            return
        }

        $password = ''
        if (-not $Account.EndsWith('$', [StringComparison]::Ordinal)) {
            if ($null -eq $script:ServiceCredential) {
                $script:ServiceCredential = Get-Credential -UserName $Account -Message "Password for IIS app pool identity $Account"
            }
            $password = ([Net.NetworkCredential]::new('', $script:ServiceCredential.Password)).Password
        }

        try {
            Set-ItemProperty -LiteralPath $poolPath -Name processModel.identityType -Value 3
            Set-ItemProperty -LiteralPath $poolPath -Name processModel.userName -Value $Account
            Set-ItemProperty -LiteralPath $poolPath -Name processModel.password -Value $password
        }
        finally {
            $password = $null
        }
    }

    if (-not $ValidateOnly) {
        Set-ItemProperty -LiteralPath $poolPath -Name managedRuntimeVersion -Value ''
    }

    Add-Result 'PASS' 'IIS application pool identity' "$PoolName runs as $Account."
    Set-AppPoolConfigPointer -PoolName $PoolName -ConfigPath $ConfigPath
}

function Ensure-IisSite {
    param(
        [Parameter(Mandatory = $true)][string] $SiteName,
        [Parameter(Mandatory = $true)][string] $PoolName,
        [Parameter(Mandatory = $true)][string] $PhysicalPath,
        [Parameter(Mandatory = $true)][Uri] $ReadyUri
    )

    $site = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
    if ($null -eq $site) {
        if ([string]::IsNullOrWhiteSpace($HttpsCertificateThumbprint)) {
            Add-Result 'NEEDS ATTENTION' 'IIS site' "$SiteName does not exist. Supply -HttpsCertificateThumbprint to create the HTTPS site, or create the site/binding manually."
            return
        }

        $certificate = Get-ChildItem Cert:\LocalMachine\My |
            Where-Object { $_.Thumbprint.Replace(' ', '').Equals($HttpsCertificateThumbprint.Replace(' ', ''), [StringComparison]::OrdinalIgnoreCase) } |
            Select-Object -First 1
        if ($null -eq $certificate) {
            throw "HTTPS certificate '$HttpsCertificateThumbprint' was not found in Cert:\LocalMachine\My."
        }

        if ($ValidateOnly) {
            Add-Result 'NEEDS ATTENTION' 'IIS site' "$SiteName does not exist."
            return
        }

        $port = if ($ReadyUri.IsDefaultPort) { 443 } else { $ReadyUri.Port }
        New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $PoolName -Port $port -HostHeader $ReadyUri.DnsSafeHost -Ssl | Out-Null
        $binding = Get-WebBinding -Name $SiteName -Protocol https | Select-Object -First 1
        $binding.AddSslCertificate($certificate.Thumbprint, 'My')
        $site = Get-Website -Name $SiteName
    }

    $actualPath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables([string]$site.PhysicalPath)).TrimEnd('\')
    if (-not $actualPath.Equals($PhysicalPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "IIS site '$SiteName' points to '$actualPath', not '$PhysicalPath'. Refusing to repoint an existing site automatically."
    }
    if (-not ([string]$site.ApplicationPool).Equals($PoolName, [StringComparison]::OrdinalIgnoreCase)) {
        throw "IIS site '$SiteName' uses app pool '$($site.ApplicationPool)', not '$PoolName'. Refusing to repoint an existing site automatically."
    }

    Add-Result 'PASS' 'IIS site' "$SiteName -> $PhysicalPath using $PoolName."
}

function Test-ApplicationConfigPlaceholders {
    param([Parameter(Mandatory = $true)][object] $Configuration)

    $values = @(
        [string]$Configuration.Authentication.Entra.ClientId,
        [string]$Configuration.Authentication.Entra.ClientSecret,
        [string]$Configuration.Authentication.Entra.InitialSuperAdmin.TenantId,
        [string]$Configuration.Authentication.Entra.InitialSuperAdmin.ObjectId
    )
    return $null -ne ($values | Where-Object { $_.StartsWith('REPLACE-', [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)
}

if (-not $ValidateOnly -and -not (Test-IsAdministrator)) {
    throw 'Run this bootstrap from an elevated PowerShell 7 session.'
}

try {
    $readyUri = [Uri]::new($ReadinessUrl, [UriKind]::Absolute)
}
catch {
    throw 'ReadinessUrl must be an absolute HTTPS /health/ready URL.'
}
if ($readyUri.Scheme -ne 'https' -or $readyUri.AbsolutePath.TrimEnd('/') -ne '/health/ready') {
    throw 'ReadinessUrl must be an absolute HTTPS /health/ready URL.'
}

$root = Get-AbsolutePath $RootPath 'RootPath'
$webRoot = Join-Path $root 'web'
$stagingRoot = Join-Path $root 'staging'
$backupRoot = Join-Path $root 'backups'
$configRoot = Join-Path $root 'config'
$keysRoot = Join-Path $root 'keys'
$logsRoot = Join-Path $root 'logs'
$deploymentConfigPath = Join-Path $configRoot 'test-deployment.json'
$applicationConfigPath = Join-Path $configRoot 'test-app.json'

$runner = Get-ActionsRunnerService
$identity = Get-ServiceIdentity $runner
if ([string]$runner.State -eq 'Running') {
    Add-Result 'PASS' 'GitHub Actions runner' "$($runner.Name) is running."
}
else {
    Add-Result 'NEEDS ATTENTION' 'GitHub Actions runner' "$($runner.Name) state is $($runner.State)."
}

foreach ($directory in @($root, $webRoot, $stagingRoot, $backupRoot, $configRoot, $keysRoot, $logsRoot)) {
    Ensure-Directory $directory
}
if (Test-Path -LiteralPath $root -PathType Container) {
    Set-ApplicationRootAcl $root $identity.Name
    Add-Result 'PASS' 'Filesystem permissions' "SYSTEM/Administrators have FullControl; $($identity.Name) has Modify under $root."
}

try {
    Ensure-LocalGroupMembership 'S-1-5-32-568' 'IIS_IUSRS membership' $identity.Name $identity.Sid
    Ensure-LocalGroupMembership 'S-1-5-32-544' 'IIS lifecycle permission' $identity.Name $identity.Sid -ReportOnly:$SkipLocalAdministrator
}
catch {
    Add-Result 'NEEDS ATTENTION' 'Windows group permissions' $_.Exception.Message
}

$resolvedSqlPackage = Resolve-Executable $SqlPackagePath @('SqlPackage.exe', 'SqlPackage') @(
    'C:\Program Files\Microsoft SQL Server\170\DAC\bin\SqlPackage.exe',
    'C:\Program Files\Microsoft SQL Server\160\DAC\bin\SqlPackage.exe',
    'C:\Program Files\Microsoft SQL Server\150\DAC\bin\SqlPackage.exe'
)
$resolvedSqlCmd = Resolve-Executable $SqlCmdPath @('sqlcmd.exe', 'sqlcmd') @(
    'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE',
    'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE',
    'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\160\Tools\Binn\SQLCMD.EXE'
)

foreach ($tool in @(
    @{ Name = 'PowerShell 7'; Path = 'pwsh.exe' },
    @{ Name = 'SqlPackage'; Path = $resolvedSqlPackage },
    @{ Name = 'sqlcmd'; Path = $resolvedSqlCmd }
)) {
    if (Test-Executable $tool.Path) {
        Add-Result 'PASS' $tool.Name "$($tool.Path) is available."
    }
    else {
        Add-Result 'NEEDS ATTENTION' $tool.Name "$($tool.Path) was not found. Install it or supply an explicit path."
    }
}

$trustValue = if ($TrustServerCertificate) { 'True' } else { 'False' }
$connectionString = "Server=$SqlServer;Database=$DatabaseName;Integrated Security=True;Encrypt=True;TrustServerCertificate=$trustValue"

$appConfig = Get-ConfiguredApplication $applicationConfigPath
$configuredThumbprint = if ($null -eq $appConfig) { $null } else { [string]$appConfig.Application.DataProtectionKeyEncryptionCertificateThumbprint }
$dpCertificate = Get-DataProtectionCertificate $configuredThumbprint
if (($null -eq $appConfig -or $ForceApplicationConfig) -and $null -eq $dpCertificate -and -not $ValidateOnly) {
    $dpCertificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject 'CN=CLC ASAP Test Data Protection' `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -KeyUsage KeyEncipherment, DataEncipherment `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -NotAfter (Get-Date).AddYears(5)
}

if ($null -ne $dpCertificate) {
    Grant-CertificatePrivateKeyRead $dpCertificate $identity.Name
    Add-Result 'PASS' 'Data Protection certificate' "Using LocalMachine\\My $($dpCertificate.Thumbprint); $($identity.Name) has private-key Read."
}
else {
    Add-Result 'NEEDS ATTENTION' 'Data Protection certificate' 'No usable test Data Protection certificate with a private key is available.'
}

if ($null -eq $appConfig -or $ForceApplicationConfig) {
    if ($ValidateOnly) {
        Add-Result 'NEEDS ATTENTION' 'Application configuration' "$applicationConfigPath is absent or would be replaced."
    }
    elseif ($null -eq $dpCertificate) {
        Add-Result 'NEEDS ATTENTION' 'Application configuration' 'Not written because no Data Protection certificate is available.'
    }
    else {
        $newConfig = New-TestApplicationConfiguration $connectionString $keysRoot $logsRoot $dpCertificate.Thumbprint
        Write-Json $applicationConfigPath $newConfig
        $appConfig = Get-ConfiguredApplication $applicationConfigPath
        Add-Result 'PASS' 'Application configuration' "Created $applicationConfigPath."
    }
}
else {
    Add-Result 'PASS' 'Application configuration' "Preserved existing $applicationConfigPath."
}

if ($null -ne $appConfig) {
    if (Test-ApplicationConfigPlaceholders $appConfig) {
        Add-Result 'NEEDS ATTENTION' 'Entra configuration' "Complete the REPLACE-LOCALLY values in $applicationConfigPath before deployment."
    }
    else {
        Add-Result 'PASS' 'Entra configuration' 'Required Entra fields contain no REPLACE- placeholders.'
    }
}

$deploymentConfig = [ordered]@{
    IisSiteName = $IisSiteName
    IisAppPoolName = $IisAppPoolName
    DeploymentPath = $webRoot
    StagingRoot = $stagingRoot
    BackupRoot = $backupRoot
    ExternalApplicationConfigPath = $applicationConfigPath
    ReadinessUrl = $readyUri.AbsoluteUri
    AsapDatabaseConnectionString = $connectionString
    HangfireDatabaseConnectionString = $connectionString
    SqlPackagePath = $resolvedSqlPackage
    SqlCmdPath = $resolvedSqlCmd
}

if ($ValidateOnly) {
    if (Test-Path -LiteralPath $deploymentConfigPath -PathType Leaf) {
        Add-Result 'PASS' 'Deployment configuration' "$deploymentConfigPath exists."
    }
    else {
        Add-Result 'NEEDS ATTENTION' 'Deployment configuration' "$deploymentConfigPath does not exist."
    }
}
else {
    Write-Json $deploymentConfigPath $deploymentConfig
    Add-Result 'PASS' 'Deployment configuration' "Wrote $deploymentConfigPath."
}

try {
    Import-Module WebAdministration -ErrorAction Stop
    Ensure-AppPool $IisAppPoolName $identity.Name $applicationConfigPath
    Ensure-IisSite $IisSiteName $IisAppPoolName $webRoot $readyUri
}
catch {
    Add-Result 'NEEDS ATTENTION' 'IIS configuration' $_.Exception.Message
}

Add-Result 'INFO' 'SQL authorization' "Grant $($identity.Name) the test-only SQL permissions needed for DACPAC publish, Hangfire preparation, and ASAP runtime access to $SqlServer/$DatabaseName. SQL privileges are intentionally not granted by this host bootstrap."
Add-Result 'INFO' 'Repository activation' 'Leave ASAP_TEST_DEPLOYMENT_ENABLED unset/false until Entra values, SQL authorization, IIS HTTPS, and the remaining NEEDS ATTENTION items are resolved.'

Write-Host ''
Write-Host 'ASAP test host bootstrap summary'
$results | Format-Table -AutoSize -Wrap

$attentionCount = @($results | Where-Object Status -EQ 'NEEDS ATTENTION').Count
if ($attentionCount -gt 0) {
    Write-Warning "$attentionCount item(s) still need attention before the first live test deployment."
}
else {
    Write-Host 'Host bootstrap checks passed. Independently confirm SQL authorization before enabling the GitHub deployment gate.'
}
