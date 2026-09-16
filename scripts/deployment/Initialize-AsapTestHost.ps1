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

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $IisSiteName = 'ASAP-Test',

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
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

function Get-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if (-not [IO.Path]::IsPathFullyQualified($Path)) {
        throw "$Name must be an absolute path."
    }

    return [IO.Path]::GetFullPath($Path).TrimEnd([char] '\', [char] '/')
}

function Get-RunnerService {
    $services = @(
        Get-CimInstance Win32_Service |
            Where-Object Name -Like 'actions.runner.*'
    )

    if (-not [string]::IsNullOrWhiteSpace($RunnerServiceName)) {
        $services = @($services | Where-Object Name -EQ $RunnerServiceName)
        if ($services.Count -eq 0) {
            throw "GitHub Actions runner service '$RunnerServiceName' was not found."
        }
    }

    if ($services.Count -eq 0) {
        throw 'No installed GitHub Actions runner Windows service (actions.runner.*) was found.'
    }

    if ($services.Count -gt 1) {
        $names = ($services.Name | Sort-Object) -join ', '
        throw "Multiple GitHub Actions runner services were found ($names). Re-run with -RunnerServiceName."
    }

    return $services[0]
}

function Resolve-ServiceIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [object] $RunnerService
    )

    $detected = [string] $RunnerService.StartName
    $effective = if ([string]::IsNullOrWhiteSpace($ServiceAccount)) { $detected } else { $ServiceAccount }

    if ([string]::IsNullOrWhiteSpace($effective)) {
        throw 'The GitHub Actions runner service account could not be determined.'
    }

    if ($effective -match '^(?i:LocalSystem|NT AUTHORITY\\(?:SYSTEM|LOCAL SERVICE|NETWORK SERVICE))$') {
        throw "The runner uses built-in identity '$effective'. Configure the runner with the dedicated AD service account before bootstrapping ASAP."
    }

    try {
        $account = [Security.Principal.NTAccount]::new($effective)
        $sid = $account.Translate([Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "Windows account '$effective' could not be resolved."
    }

    if (-not [string]::IsNullOrWhiteSpace($ServiceAccount) -and
        -not $detected.Equals($ServiceAccount, [StringComparison]::OrdinalIgnoreCase)) {
        Add-Result -Status 'NEEDS ATTENTION' -Item 'Runner service identity' -Detail "Runner is configured as '$detected' but -ServiceAccount specified '$ServiceAccount'."
    }
    else {
        Add-Result -Status 'PASS' -Item 'Runner service identity' -Detail "$($RunnerService.Name) runs as $effective."
    }

    return [pscustomobject]@{
        Name = $effective
        Sid = $sid
    }
}

function Ensure-Directory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (Test-Path -LiteralPath $Path -PathType Container) {
        return
    }

    if ($ValidateOnly) {
        Add-Result -Status 'NEEDS ATTENTION' -Item 'Directory' -Detail "$Path does not exist."
        return
    }

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Set-RootAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Account
    )

    if ($ValidateOnly) {
        return
    }

    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.NTAccount]::new('BUILTIN\Administrators'))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        'NT AUTHORITY\SYSTEM',
        [Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        $propagation,
        $allow))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        'BUILTIN\Administrators',
        [Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        $propagation,
        $allow))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $Account,
        [Security.AccessControl.FileSystemRights]::Modify,
        $inheritance,
        $propagation,
        $allow))

    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Test-DirectLocalGroupMember {
    param(
        [Parameter(Mandatory = $true)]
        [string] $GroupSid,

        [Parameter(Mandatory = $true)]
        [Security.Principal.SecurityIdentifier] $MemberSid
    )

    $group = Get-LocalGroup -SID $GroupSid
    $members = @(Get-LocalGroupMember -Group $group -ErrorAction Stop)
    return $members.SID.Value -contains $MemberSid.Value
}

function Ensure-LocalGroupMember {
    param(
        [Parameter(Mandatory = $true)]
        [string] $GroupSid,

        [Parameter(Mandatory = $true)]
        [string] $GroupLabel,

        [Parameter(Mandatory = $true)]
        [string] $Account,

        [Parameter(Mandatory = $true)]
        [Security.Principal.SecurityIdentifier] $AccountSid,

        [switch] $ReportOnly
    )

    $group = Get-LocalGroup -SID $GroupSid
    if (Test-DirectLocalGroupMember -GroupSid $GroupSid -MemberSid $AccountSid) {
        Add-Result -Status 'PASS' -Item $GroupLabel -Detail "$Account is a direct member of $($group.Name)."
        return
    }

    if ($ValidateOnly -or $ReportOnly) {
        Add-Result -Status 'NEEDS ATTENTION' -Item $GroupLabel -Detail "$Account is not a direct member of $($group.Name)."
        return
    }

    Add-LocalGroupMember -Group $group -Member $Account
    Add-Result -Status 'PASS' -Item $GroupLabel -Detail "Added $Account to $($group.Name)."
}

function Resolve-Tool {
    param(
        [string] $ConfiguredPath,

        [Parameter(Mandatory = $true)]
        [string[]] $CommandNames,

        [Parameter(Mandatory = $true)]
        [string[]] $CandidatePaths
    )

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        if ([IO.Path]::IsPathRooted($ConfiguredPath)) {
            if (Test-Path -LiteralPath $ConfiguredPath -PathType Leaf) {
                return [IO.Path]::GetFullPath($ConfiguredPath)
            }
            return $ConfiguredPath
        }

        $command = Get-Command $ConfiguredPath -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }
        return $ConfiguredPath
    }

    foreach ($name in $CommandNames) {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }
    }

    foreach ($candidate in $CandidatePaths) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    return $CommandNames[0]
}

function Test-ResolvedTool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ConfiguredPath
    )

    if ([IO.Path]::IsPathRooted($ConfiguredPath)) {
        return Test-Path -LiteralPath $ConfiguredPath -PathType Leaf
    }

    return $null -ne (Get-Command $ConfiguredPath -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [object] $Value
    )

    $json = $Value | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Find-DataProtectionCertificate {
    param(
        [string] $Thumbprint
    )

    $certificates = @(Get-ChildItem Cert:\LocalMachine\My)
    if (-not [string]::IsNullOrWhiteSpace($Thumbprint) -and
        -not $Thumbprint.StartsWith('REPLACE-', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $Thumbprint.Replace(' ', '').ToUpperInvariant()
        return $certificates |
            Where-Object { $_.Thumbprint.Replace(' ', '').ToUpperInvariant() -eq $normalized -and $_.HasPrivateKey } |
            Select-Object -First 1
    }

    return $certificates |
        Where-Object {
            $_.Subject -eq 'CN=CLC ASAP Test Data Protection' -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date).AddDays(30)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function Grant-CertificatePrivateKeyRead {
    param(
        [Parameter(Mandatory = $true)]
        [Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,

        [Parameter(Mandatory = $true)]
        [string] $Account
    )

    if ($ValidateOnly) {
        return
    }

    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if ($null -eq $rsa) {
        throw 'The Data Protection certificate does not expose an RSA private key.'
    }

    try {
        $keyPath = $null
        if ($rsa -is [Security.Cryptography.RSACng]) {
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($rsa.Key.UniqueName)"
        }
        elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) {
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($rsa.CspKeyContainerInfo.UniqueKeyContainerName)"
        }

        if ([string]::IsNullOrWhiteSpace($keyPath) -or -not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
            throw 'The Data Protection certificate private-key file could not be located.'
        }

        $acl = Get-Acl -LiteralPath $keyPath
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $Account,
            [Security.AccessControl.FileSystemRights]::Read,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.SetAccessRule($rule)
        Set-Acl -LiteralPath $keyPath -AclObject $acl
    }
    finally {
        $rsa.Dispose()
    }
}

function Get-ExistingApplicationConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

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

function New-ApplicationConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ConnectionString,

        [Parameter(Mandatory = $true)]
        [string] $KeysPath,

        [Parameter(Mandatory = $true)]
        [string] $LogsPath,

        [Parameter(Mandatory = $true)]
        [string] $CertificateThumbprint
    )

    foreach ($pair in @(
        @{ Name = 'EntraClientId'; Value = $EntraClientId },
        @{ Name = 'EntraTenantId'; Value = $EntraTenantId },
        @{ Name = 'EntraObjectId'; Value = $EntraObjectId }
    )) {
        if (-not [string]::IsNullOrWhiteSpace($pair.Value) -and -not [guid]::TryParse($pair.Value, [ref] ([guid]::Empty))) {
            throw "$($pair.Name) must be a GUID when supplied."
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($AdminEmail) -and $AdminEmail -notmatch '^[^@\s]+@[^@\s]+$') {
        throw 'AdminEmail must be an email address when supplied.'
    }

    $clientId = if ([string]::IsNullOrWhiteSpace($EntraClientId)) { 'REPLACE-LOCALLY' } else { $EntraClientId }
    $tenantId = if ([string]::IsNullOrWhiteSpace($EntraTenantId)) { 'REPLACE-LOCALLY' } else { $EntraTenantId }
    $objectId = if ([string]::IsNullOrWhiteSpace($EntraObjectId)) { 'REPLACE-LOCALLY' } else { $EntraObjectId }
    $email = if ([string]::IsNullOrWhiteSpace($AdminEmail)) { 'admin@example.org' } else { $AdminEmail }
    $secretText = if ($null -eq $EntraClientSecret) {
        'REPLACE-LOCALLY'
    }
    else {
        ([Net.NetworkCredential]::new('', $EntraClientSecret)).Password
    }

    $recipientDomains = if ($null -ne $AllowedRecipientDomains -and $AllowedRecipientDomains.Count -gt 0) {
        @($AllowedRecipientDomains)
    }
    elseif (-not [string]::IsNullOrWhiteSpace($AdminEmail)) {
        @($AdminEmail.Split('@')[-1])
    }
    else {
        @('example.org')
    }

    try {
        return [ordered]@{
            Environment = [ordered]@{
                Name = 'Test'
                IsNonProduction = $true
                UiBannerText = 'NON-PRODUCTION'
            }
            ConnectionStrings = [ordered]@{
                AsapDatabase = $ConnectionString
                HangfireDatabase = $ConnectionString
            }
            Authentication = [ordered]@{
                Entra = [ordered]@{
                    ClientId = $clientId
                    ClientSecret = $secretText
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
            EmailSafety = [ordered]@{
                AllowedRecipientDomains = $recipientDomains
            }
            PatronLoginRateLimit = [ordered]@{
                PermitLimit = 20
                WindowSeconds = 300
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
    }
    finally {
        $secretText = $null
    }
}

function Ensure-AppPoolConfigVariable {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PoolName,

        [Parameter(Mandatory = $true)]
        [string] $ConfigPath
    )

    $baseFilter = "system.applicationHost/applicationPools/add[@name='$PoolName']/environmentVariables"
    $itemFilter = "$baseFilter/add[@name='Asap__ConfigFile']"
    $existing = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $itemFilter -Name 'value' -ErrorAction SilentlyContinue
    $existingValue = if ($null -eq $existing) { $null } else { [string] $existing.Value }

    if ($existingValue -eq $ConfigPath) {
        Add-Result -Status 'PASS' -Item 'IIS application config pointer' -Detail "Asap__ConfigFile is $ConfigPath."
        return
    }

    if ($ValidateOnly) {
        Add-Result -Status 'NEEDS ATTENTION' -Item 'IIS application config pointer' -Detail "Asap__ConfigFile is not set to $ConfigPath."
        return
    }

    if ($null -eq $existing) {
        Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $baseFilter -Name '.' -Value @{ name = 'Asap__ConfigFile'; value = $ConfigPath }
    }
    else {
        Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $itemFilter -Name 'value' -Value $ConfigPath
    }

    Add-Result -Status 'PASS' -Item 'IIS application config pointer' -Detail "Set Asap__ConfigFile to $ConfigPath."
}

function Ensure-AppPool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PoolName,

        [Parameter(Mandatory = $true)]
        [string] $Account,

        [Parameter(Mandatory = $true)]
        [string] $ConfigPath
    )

    $poolPath = "IIS:\AppPools\$PoolName"
    if (-not (Test-Path -LiteralPath $poolPath)) {
        if ($ValidateOnly) {
            Add-Result -Status 'NEEDS ATTENTION' -Item 'IIS application pool' -Detail "$PoolName does not exist."
            return
        }
        New-WebAppPool -Name $PoolName | Out-Null
    }

    $pool = Get-Item -LiteralPath $poolPath
    $identityMatches = [int] $pool.processModel.identityType -eq 3 -and
        ([string] $pool.processModel.userName).Equals($Account, [StringComparison]::OrdinalIgnoreCase)

    if (-not $identityMatches) {
        if ($ValidateOnly) {
            Add-Result -Status 'NEEDS ATTENTION' -Item 'IIS application pool identity' -Detail "$PoolName is not configured to run as $Account."
        }
        else {
            if ($null -eq $ServiceCredential) {
                $script:ServiceCredential = Get-Credential -UserName $Account -Message "Password for IIS app pool identity $Account"
            }
            $plainPassword = ([Net.NetworkCredential]::new('', $script:ServiceCredential.Password)).Password
            try {
                Set-ItemProperty -LiteralPath $poolPath -Name processModel.identityType -Value 3
                Set-ItemProperty -LiteralPath $poolPath -Name processModel.userName -Value $Account
                Set-ItemProperty -LiteralPath $poolPath -Name processModel.password -Value $plainPassword
            }
            finally {
                $plainPassword = $null
            }
            Add-Result -Status 'PASS' -Item 'IIS application pool identity' -Detail "$PoolName runs as $Account."
        }
    }
    else {
        Add-Result -Status 'PASS' -Item 'IIS application pool identity' -Detail "$PoolName runs as $Account."
    }

    if (-not $ValidateOnly) {
        Set-ItemProperty -LiteralPath $poolPath -Name managedRuntimeVersion -Value ''
    }

    Ensure-AppPoolConfigVariable -PoolName $PoolName -ConfigPath $ConfigPath
}

function Ensure-IisSite {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SiteName,

        [Parameter(Mandatory = $true)]
        [string] $PoolName,

        [Parameter(Mandatory = $true)]
        [string] $PhysicalPath,

        [Parameter(Mandatory = $true)]
        [Uri] $ReadyUri
    )

    $site = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
    if ($null -eq $site) {
        if ([string]::IsNullOrWhiteSpace($HttpsCertificateThumbprint)) {
            Add-Result -Status 'NEEDS ATTENTION' -Item 'IIS site' -Detail "$SiteName does not exist. Create its HTTPS binding/certificate, or re-run with -HttpsCertificateThumbprint to let the bootstrap create it."
            return
        }

        $certificate = Find-DataProtectionCertificate -Thumbprint $HttpsCertificateThumbprint
        if ($null -eq $certificate) {
            $certificate = Get-ChildItem Cert:\LocalMachine\My |
                Where-Object { $_.Thumbprint.Replace(' ', '').Equals($HttpsCertificateThumbprint.Replace(' ', ''), [StringComparison]::OrdinalIgnoreCase) } |
                Select-Object -First 1
        }
        if ($null -eq $certificate) {
            throw "HTTPS certificate '$HttpsCertificateThumbprint' was not found in Cert:\LocalMachine\My."
        }

        if ($ValidateOnly) {
            Add-Result -Status 'NEEDS ATTENTION' -Item 'IIS site' -Detail "$SiteName does not exist."
            return
        }

        $port = if ($ReadyUri.IsDefaultPort) { 443 } else { $ReadyUri.Port }
        New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $PoolName -Port $port -HostHeader $ReadyUri.DnsSafeHost -Ssl | Out-Null
        $binding = Get-WebBinding -Name $SiteName -Protocol https | Select-Object -First 1
        $binding.AddSslCertificate($certificate.Thumbprint, 'My')
        $site = Get-Website -Name $SiteName
    }

    $sitePath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables([string] $site.PhysicalPath)).TrimEnd('\')
    if (-not $sitePath.Equals($PhysicalPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "IIS site '$SiteName' points to '$sitePath', not '$PhysicalPath'. Refusing to repoint an existing site automatically."
    }
    if (-not ([string] $site.ApplicationPool).Equals($PoolName, [StringComparison]::OrdinalIgnoreCase)) {
        throw "IIS site '$SiteName' uses app pool '$($site.ApplicationPool)', not '$PoolName'. Refusing to repoint an existing site automatically."
    }

    Add-Result -Status 'PASS' -Item 'IIS site' -Detail "$SiteName -> $PhysicalPath using $PoolName."
}

function Test-ApplicationConfigPlaceholders {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Configuration
    )

    $values = @(
        [string] $Configuration.Authentication.Entra.ClientId,
        [string] $Configuration.Authentication.Entra.ClientSecret,
        [string] $Configuration.Authentication.Entra.InitialSuperAdmin.TenantId,
        [string] $Configuration.Authentication.Entra.InitialSuperAdmin.ObjectId
    )
    return $values | Where-Object { $_.StartsWith('REPLACE-', [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
}

if (-not $ValidateOnly -and -not (Test-IsAdministrator)) {
    throw 'Run this bootstrap from an elevated PowerShell 7 session.'
}

$readyUri = $null
try {
    $readyUri = [Uri]::new($ReadinessUrl, [UriKind]::Absolute)
}
catch {
    throw 'ReadinessUrl must be an absolute HTTPS /health/ready URL.'
}
if ($readyUri.Scheme -ne 'https' -or $readyUri.AbsolutePath.TrimEnd('/') -ne '/health/ready') {
    throw 'ReadinessUrl must be an absolute HTTPS /health/ready URL.'
}

$root = Get-FullPath -Path $RootPath -Name 'RootPath'
$webRoot = Join-Path $root 'web'
$stagingRoot = Join-Path $root 'staging'
$backupRoot = Join-Path $root 'backups'
$configRoot = Join-Path $root 'config'
$keysRoot = Join-Path $root 'keys'
$logsRoot = Join-Path $root 'logs'
$deploymentConfigPath = Join-Path $configRoot 'test-deployment.json'
$applicationConfigPath = Join-Path $configRoot 'test-app.json'

$runner = Get-RunnerService
$identity = Resolve-ServiceIdentity -RunnerService $runner
if ([string] $runner.State -eq 'Running') {
    Add-Result -Status 'PASS' -Item 'GitHub Actions runner' -Detail "$($runner.Name) is running."
}
else {
    Add-Result -Status 'NEEDS ATTENTION' -Item 'GitHub Actions runner' -Detail "$($runner.Name) state is $($runner.State)."
}

foreach ($directory in @($root, $webRoot, $stagingRoot, $backupRoot, $configRoot, $keysRoot, $logsRoot)) {
    Ensure-Directory -Path $directory
}

if (Test-Path -LiteralPath $root -PathType Container) {
    Set-RootAcl -Path $root -Account $identity.Name
    Add-Result -Status 'PASS' -Item 'Filesystem permissions' -Detail "SYSTEM and Administrators have FullControl; $($identity.Name) has Modify under $root."
}

if (Get-Command Get-LocalGroup -ErrorAction SilentlyContinue) {
    try {
        Ensure-LocalGroupMember -GroupSid 'S-1-5-32-568' -GroupLabel 'IIS_IUSRS membership' -Account $identity.Name -AccountSid $identity.Sid
    }
    catch {
        Add-Result -Status 'NEEDS ATTENTION' -Item 'IIS_IUSRS membership' -Detail $_.Exception.Message
    }

    try {
        if ($SkipLocalAdministrator) {
            Ensure-LocalGroupMember -GroupSid 'S-1-5-32-544' -GroupLabel 'IIS lifecycle permission' -Account $identity.Name -AccountSid $identity.Sid -ReportOnly
        }
        else {
            Ensure-LocalGroupMember -GroupSid 'S-1-5-32-544' -GroupLabel 'IIS lifecycle permission' -Account $identity.Name -AccountSid $identity.Sid
        }
    }
    catch {
        Add-Result -Status 'NEEDS ATTENTION' -Item 'IIS lifecycle permission' -Detail $_.Exception.Message
    }
}
else {
    Add-Result -Status 'NEEDS ATTENTION' -Item 'Local group management' -Detail 'Microsoft.PowerShell.LocalAccounts is unavailable.'
}

$resolvedSqlPackage = Resolve-Tool -ConfiguredPath $SqlPackagePath -CommandNames @('SqlPackage.exe', 'SqlPackage') -CandidatePaths @(
    'C:\Program Files\Microsoft SQL Server\170\DAC\bin\SqlPackage.exe',
    'C:\Program Files\Microsoft SQL Server\160\DAC\bin\SqlPackage.exe',
    'C:\Program Files\Microsoft SQL Server\150\DAC\bin\SqlPackage.exe',
    'C:\Program Files\Microsoft SQL Server\DAC\170\SqlPackage.exe'
)
$resolvedSqlCmd = Resolve-Tool -ConfiguredPath $SqlCmdPath -CommandNames @('sqlcmd.exe', 'sqlcmd') -CandidatePaths @(
    'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE',
    'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE',
    'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\160\Tools\Binn\SQLCMD.EXE'
)

foreach ($tool in @(
    @{ Name = 'PowerShell 7'; Path = 'pwsh.exe' },
    @{ Name = 'SqlPackage'; Path = $resolvedSqlPackage },
    @{ Name = 'sqlcmd'; Path = $resolvedSqlCmd }
)) {
    if (Test-ResolvedTool -ConfiguredPath $tool.Path) {
        Add-Result -Status 'PASS' -Item $tool.Name -Detail "$($tool.Path) is available."
    }
    else {
        Add-Result -Status 'NEEDS ATTENTION' -Item $tool.Name -Detail "$($tool.Path) was not found. Install it or supply an explicit path."
    }
}

$trustText = if ($TrustServerCertificate) { 'True' } else { 'False' }
$connectionString = "Server=$SqlServer;Database=$DatabaseName;Integrated Security=True;Encrypt=True;TrustServerCertificate=$trustText"

$existingApplicationConfig = Get-ExistingApplicationConfiguration -Path $applicationConfigPath
$configuredCertificateThumbprint = if ($null -eq $existingApplicationConfig) {
    $null
}
else {
    [string] $existingApplicationConfig.Application.DataProtectionKeyEncryptionCertificateThumbprint
}
$certificate = Find-DataProtectionCertificate -Thumbprint $configuredCertificateThumbprint

if (($null -eq $existingApplicationConfig -or $ForceApplicationConfig) -and $null -eq $certificate -and -not $ValidateOnly) {
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject 'CN=CLC ASAP Test Data Protection' `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -KeyUsage KeyEncipherment, DataEncipherment `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -NotAfter (Get-Date).AddYears(5)
}

if ($null -ne $certificate) {
    Grant-CertificatePrivateKeyRead -Certificate $certificate -Account $identity.Name
    Add-Result -Status 'PASS' -Item 'Data Protection certificate' -Detail "Using LocalMachine\\My certificate $($certificate.Thumbprint); private-key Read is granted to $($identity.Name)."
}
else {
    Add-Result -Status 'NEEDS ATTENTION' -Item 'Data Protection certificate' -Detail 'No usable configured CLC ASAP Test Data Protection certificate with a private key was found.'
}

if ($null -eq $existingApplicationConfig -or $ForceApplicationConfig) {
    if ($ValidateOnly) {
        Add-Result -Status 'NEEDS ATTENTION' -Item 'Application configuration' -Detail "$applicationConfigPath does not exist or would be replaced by -ForceApplicationConfig."
    }
    elseif ($null -eq $certificate) {
        Add-Result -Status 'NEEDS ATTENTION' -Item 'Application configuration' -Detail 'Application configuration was not written because no Data Protection certificate is available.'
    }
    else {
        $newApplicationConfig = New-ApplicationConfiguration `
            -ConnectionString $connectionString `
            -KeysPath $keysRoot `
            -LogsPath $logsRoot `
            -CertificateThumbprint $certificate.Thumbprint
        Write-JsonFile -Path $applicationConfigPath -Value $newApplicationConfig
        $existingApplicationConfig = Get-ExistingApplicationConfiguration -Path $applicationConfigPath
        Add-Result -Status 'PASS' -Item 'Application configuration' -Detail "Created $applicationConfigPath."
    }
}
else {
    Add-Result -Status 'PASS' -Item 'Application configuration' -Detail "Preserved existing $applicationConfigPath."
}

if ($null -ne $existingApplicationConfig -and (Test-ApplicationConfigPlaceholders -Configuration $existingApplicationConfig)) {
    Add-Result -Status 'NEEDS ATTENTION' -Item 'Entra configuration' -Detail "Complete the REPLACE-LOCALLY values in $applicationConfigPath before the first deployment."
}
elseif ($null -ne $existingApplicationConfig) {
    Add-Result -Status 'PASS' -Item 'Entra configuration' -Detail 'Application configuration contains no REPLACE- placeholders in the required Entra fields.'
}

$deploymentConfiguration = [ordered]@{
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
        Add-Result -Status 'PASS' -Item 'Deployment configuration' -Detail "$deploymentConfigPath exists."
    }
    else {
        Add-Result -Status 'NEEDS ATTENTION' -Item 'Deployment configuration' -Detail "$deploymentConfigPath does not exist."
    }
}
else {
    Write-JsonFile -Path $deploymentConfigPath -Value $deploymentConfiguration
    Add-Result -Status 'PASS' -Item 'Deployment configuration' -Detail "Wrote $deploymentConfigPath."
}

try {
    Import-Module WebAdministration -ErrorAction Stop
    Ensure-AppPool -PoolName $IisAppPoolName -Account $identity.Name -ConfigPath $applicationConfigPath
    Ensure-IisSite -SiteName $IisSiteName -PoolName $IisAppPoolName -PhysicalPath $webRoot -ReadyUri $readyUri
}
catch {
    Add-Result -Status 'NEEDS ATTENTION' -Item 'IIS configuration' -Detail $_.Exception.Message
}

Add-Result -Status 'INFO' -Item 'SQL authorization' -Detail "Grant $($identity.Name) the test-only SQL permissions needed to publish the DACPAC, prepare Hangfire, and run ASAP against $SqlServer/$DatabaseName. This bootstrap does not grant SQL privileges."
Add-Result -Status 'INFO' -Item 'Repository activation' -Detail 'Keep ASAP_TEST_DEPLOYMENT_ENABLED unset/false until the host configuration, Entra values, SQL permissions, IIS HTTPS binding, and readiness prerequisites are complete.'

Write-Host ''
Write-Host 'ASAP test host bootstrap summary'
$results | Format-Table -AutoSize -Wrap

$attentionCount = @($results | Where-Object Status -EQ 'NEEDS ATTENTION').Count
if ($attentionCount -gt 0) {
    Write-Warning "$attentionCount item(s) still need attention before the first live test deployment."
}
else {
    Write-Host 'Host bootstrap checks passed. The GitHub deployment gate can be enabled after SQL authorization is independently confirmed.'
}
