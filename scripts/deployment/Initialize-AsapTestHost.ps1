#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SqlServer,

    [ValidatePattern('^Asap[A-Za-z0-9_]+$')]
    [string] $DatabaseName = 'AsapTest',

    [Parameter(Mandatory = $true)]
    [string] $ReadinessUrl,

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
$root = 'C:\ProgramData\clc-asap'

function Add-Result([string] $Status, [string] $Item, [string] $Detail) {
    $results.Add([pscustomobject]@{ Status = $Status; Item = $Item; Detail = $Detail })
}

function Test-Administrator {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-JsonValue([object] $Object, [string] $Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Write-Json([string] $Path, [object] $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}

function Test-NonEmptyGuid([object] $Value) {
    $parsed = [guid]::Empty
    return $null -ne $Value -and [guid]::TryParse([string]$Value, [ref]$parsed) -and $parsed -ne [guid]::Empty
}

function Assert-Guid([string] $Name, [string] $Value) {
    if (-not $Value) { return }
    if (-not (Test-NonEmptyGuid $Value)) { throw "$Name must be a non-empty GUID." }
}

function Test-Email([object] $Value) {
    if ([string]::IsNullOrWhiteSpace([string]$Value)) { return $false }
    try {
        $mail = [Net.Mail.MailAddress]::new([string]$Value)
        return $mail.Address.Equals([string]$Value, [StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

function Test-RecipientDomain([object] $Value) {
    $domain = [string]$Value
    if ([string]::IsNullOrWhiteSpace($domain) -or $domain -ne $domain.Trim() -or $domain.Length -gt 253) { return $false }
    if ($domain.Contains('*') -or $domain.Contains('@') -or $domain.Contains('://') -or $domain.StartsWith('.') -or $domain.EndsWith('.') -or $domain.Contains('..')) { return $false }
    try { $ascii = [Globalization.IdnMapping]::new().GetAscii($domain) } catch { return $false }
    foreach ($label in $ascii.Split('.')) {
        if ($label.Length -lt 1 -or $label.Length -gt 63 -or $label -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?$') { return $false }
    }
    return $true
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
    try { $sid = ([Security.Principal.NTAccount]::new($account)).Translate([Security.Principal.SecurityIdentifier]) }
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

function Test-AclRights([Security.AccessControl.DirectorySecurity] $Acl, [Security.Principal.SecurityIdentifier] $Sid, [Security.AccessControl.FileSystemRights] $Rights) {
    $combined = [Security.AccessControl.FileSystemRights]0
    foreach ($rule in $Acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -eq $Sid.Value) {
            $combined = $combined -bor $rule.FileSystemRights
        }
    }
    return ($combined -band $Rights) -eq $Rights
}

function Test-RootPermissions([string] $Path, [Security.Principal.SecurityIdentifier] $ServiceSid) {
    $errors = [System.Collections.Generic.List[string]]::new()
    try { $acl = Get-Acl -LiteralPath $Path } catch { $errors.Add($_.Exception.Message); return $errors }
    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $adminSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')

    if (-not $acl.AreAccessRulesProtected) { $errors.Add('ACL inheritance is not protected.') }
    try { $ownerSid = ([Security.Principal.NTAccount]::new($acl.Owner)).Translate([Security.Principal.SecurityIdentifier]) }
    catch { $ownerSid = $null }
    if ($null -eq $ownerSid -or $ownerSid.Value -ne $adminSid.Value) { $errors.Add('Owner is not BUILTIN\Administrators.') }
    if (-not (Test-AclRights $acl $systemSid ([Security.AccessControl.FileSystemRights]::FullControl))) { $errors.Add('SYSTEM lacks FullControl.') }
    if (-not (Test-AclRights $acl $adminSid ([Security.AccessControl.FileSystemRights]::FullControl))) { $errors.Add('BUILTIN\Administrators lacks FullControl.') }
    if (-not (Test-AclRights $acl $ServiceSid ([Security.AccessControl.FileSystemRights]::Modify))) { $errors.Add('Service identity lacks Modify.') }

    $allowed = @($systemSid.Value, $adminSid.Value, $ServiceSid.Value)
    foreach ($rule in $acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $allowed -notcontains $rule.IdentityReference.Value) {
            $errors.Add("Unexpected explicit ACL entry for $($rule.IdentityReference.Value).")
        }
    }
    return $errors
}

function Ensure-RootPermissions([string] $Path, [string] $Account, [Security.Principal.SecurityIdentifier] $ServiceSid) {
    if (-not $ValidateOnly) {
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $propagation = [Security.AccessControl.PropagationFlags]::None
        $allow = [Security.AccessControl.AccessControlType]::Allow
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner([Security.Principal.NTAccount]::new('BUILTIN\Administrators'))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('NT AUTHORITY\SYSTEM', [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('BUILTIN\Administrators', [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow))
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($Account, [Security.AccessControl.FileSystemRights]::Modify, $inheritance, $propagation, $allow))
        Set-Acl -LiteralPath $Path -AclObject $acl
    }

    $errors = @(Test-RootPermissions $Path $ServiceSid)
    if ($errors.Count -eq 0) { Add-Result 'PASS' 'Filesystem permissions' "Restricted ACL is correct under $Path." }
    else { Add-Result 'NEEDS ATTENTION' 'Filesystem permissions' ($errors -join ' ') }
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

function Get-CertificatePrivateKeyPath([Security.Cryptography.X509Certificates.X509Certificate2] $Certificate) {
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if ($null -eq $rsa) { throw 'The Data Protection certificate does not expose an RSA private key.' }
    try {
        if ($rsa -is [Security.Cryptography.RSACng]) { return Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($rsa.Key.UniqueName)" }
        if ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) { return Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($rsa.CspKeyContainerInfo.UniqueKeyContainerName)" }
        throw 'Unsupported Data Protection private-key provider.'
    }
    finally { $rsa.Dispose() }
}

function Test-CertificateRead([Security.Cryptography.X509Certificates.X509Certificate2] $Certificate, [Security.Principal.SecurityIdentifier] $ServiceSid) {
    $keyPath = Get-CertificatePrivateKeyPath $Certificate
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) { return $false }
    $acl = Get-Acl -LiteralPath $keyPath
    $combined = [Security.AccessControl.FileSystemRights]0
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -eq $ServiceSid.Value) {
            $combined = $combined -bor $rule.FileSystemRights
        }
    }
    return ($combined -band [Security.AccessControl.FileSystemRights]::Read) -eq [Security.AccessControl.FileSystemRights]::Read
}

function Ensure-CertificateRead([Security.Cryptography.X509Certificates.X509Certificate2] $Certificate, [string] $Account, [Security.Principal.SecurityIdentifier] $ServiceSid) {
    $hasRead = Test-CertificateRead $Certificate $ServiceSid
    if (-not $hasRead -and -not $ValidateOnly) {
        $keyPath = Get-CertificatePrivateKeyPath $Certificate
        $acl = Get-Acl -LiteralPath $keyPath
        $acl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new($Account, [Security.AccessControl.FileSystemRights]::Read, [Security.AccessControl.AccessControlType]::Allow))
        Set-Acl -LiteralPath $keyPath -AclObject $acl
        $hasRead = Test-CertificateRead $Certificate $ServiceSid
    }

    if ($hasRead) { Add-Result 'PASS' 'Data Protection certificate' "Using LocalMachine\\My $($Certificate.Thumbprint); $Account has private-key Read." }
    else { Add-Result 'NEEDS ATTENTION' 'Data Protection certificate' "$Account does not have private-key Read for LocalMachine\\My $($Certificate.Thumbprint)." }
}

function Get-SqlConnectionInfo([string] $ConnectionString) {
    try {
        $builder = [Data.Common.DbConnectionStringBuilder]::new()
        $builder.ConnectionString = $ConnectionString
    }
    catch { return $null }

    $server = $null
    foreach ($key in @('Data Source', 'Server', 'Address', 'Addr', 'Network Address')) { if ($builder.ContainsKey($key)) { $server = [string]$builder[$key]; break } }
    $database = $null
    foreach ($key in @('Initial Catalog', 'Database')) { if ($builder.ContainsKey($key)) { $database = [string]$builder[$key]; break } }
    $integrated = $false
    foreach ($key in @('Integrated Security', 'Trusted_Connection')) { if ($builder.ContainsKey($key)) { $integrated = [string]$builder[$key] -match '^(?i:true|yes|sspi|1)$'; break } }
    $encrypt = $builder.ContainsKey('Encrypt') -and [string]$builder['Encrypt'] -match '^(?i:true|yes|1|mandatory|strict)$'
    $trust = $builder.ContainsKey('TrustServerCertificate') -and [string]$builder['TrustServerCertificate'] -match '^(?i:true|yes|1)$'
    return [pscustomobject]@{ Server = $server; Database = $database; Integrated = $integrated; Encrypt = $encrypt; Trust = $trust }
}

function Test-EquivalentSqlConnection([object] $Actual, [object] $Expected) {
    if ($null -eq $Actual -or $null -eq $Expected -or [string]::IsNullOrWhiteSpace([string]$Actual.Server) -or [string]::IsNullOrWhiteSpace([string]$Actual.Database) -or [string]::IsNullOrWhiteSpace([string]$Expected.Server) -or [string]::IsNullOrWhiteSpace([string]$Expected.Database)) { return $false }
    return $Actual.Server.Equals($Expected.Server, [StringComparison]::OrdinalIgnoreCase) -and
        $Actual.Database.Equals($Expected.Database, [StringComparison]::OrdinalIgnoreCase) -and
        $Actual.Integrated -eq $Expected.Integrated -and
        $Actual.Encrypt -eq $Expected.Encrypt -and
        $Actual.Trust -eq $Expected.Trust
}

function Test-CronShape([object] $Value) {
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $false }
    $fields = @($text.Trim() -split '\s+')
    if ($fields.Count -ne 5) { return $false }
    return $null -eq ($fields | Where-Object { $_ -notmatch '^[0-9A-Za-z*/?,#LW-]+$' } | Select-Object -First 1)
}

function Test-IntegerRange([object] $Value, [int] $Minimum, [int] $Maximum, [bool] $AllowNull) {
    if ($null -eq $Value) { return $AllowNull }
    $parsed = 0
    if (-not [int]::TryParse([string]$Value, [ref]$parsed)) { return $false }
    return $parsed -ge $Minimum -and $parsed -le $Maximum
}

function Test-Limit([object] $Limit, [bool] $Required) {
    if ($null -eq $Limit) { return $false }
    $page = Get-JsonValue $Limit 'PageSize'
    $max = Get-JsonValue $Limit 'MaxPerRun'
    if ($Required -and ($null -eq $page -or $null -eq $max)) { return $false }
    return (Test-IntegerRange $page 1 500 (-not $Required)) -and (Test-IntegerRange $max 1 5000 (-not $Required))
}

function Test-AppConfiguration([object] $Configuration, [string] $ExpectedConnectionString, [string] $ExpectedKeysPath, [string] $ExpectedLogsPath, [Security.Cryptography.X509Certificates.X509Certificate2] $Certificate) {
    $errors = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $Configuration) { $errors.Add('Configuration is missing or invalid JSON.'); return $errors }

    $environment = Get-JsonValue $Configuration 'Environment'
    $connections = Get-JsonValue $Configuration 'ConnectionStrings'
    $authentication = Get-JsonValue $Configuration 'Authentication'
    $entra = Get-JsonValue $authentication 'Entra'
    $application = Get-JsonValue $Configuration 'Application'
    $emailSafety = Get-JsonValue $Configuration 'EmailSafety'
    $rateLimit = Get-JsonValue $Configuration 'PatronLoginRateLimit'
    $hangfire = Get-JsonValue $Configuration 'Hangfire'
    $processing = Get-JsonValue $hangfire 'ProcessingLimits'
    if ($null -in @($environment, $connections, $entra, $application, $emailSafety, $rateLimit, $processing)) {
        $errors.Add('One or more required configuration sections are missing.')
        return $errors
    }

    if ([string]::IsNullOrWhiteSpace([string](Get-JsonValue $environment 'Name')) -or $null -eq (Get-JsonValue $environment 'IsNonProduction')) { $errors.Add('Environment settings are incomplete.') }

    $expectedSql = Get-SqlConnectionInfo $ExpectedConnectionString
    foreach ($name in @('AsapDatabase', 'HangfireDatabase')) {
        $actualSql = Get-SqlConnectionInfo ([string](Get-JsonValue $connections $name))
        if (-not (Test-EquivalentSqlConnection $actualSql $expectedSql)) { $errors.Add("ConnectionStrings.$name does not match the bootstrap SQL target/security settings.") }
    }

    $clientId = Get-JsonValue $entra 'ClientId'
    $clientSecret = [string](Get-JsonValue $entra 'ClientSecret')
    if (-not (Test-NonEmptyGuid $clientId)) { $errors.Add('Authentication.Entra.ClientId is not a non-empty GUID.') }
    if ([string]::IsNullOrWhiteSpace($clientSecret) -or $clientSecret.StartsWith('REPLACE-', [StringComparison]::OrdinalIgnoreCase)) { $errors.Add('Authentication.Entra.ClientSecret is missing or a placeholder.') }

    $tenantIds = @((Get-JsonValue $entra 'AllowedTenantIds'))
    if ($tenantIds.Count -eq 0 -or $null -eq $tenantIds[0]) { $errors.Add('Authentication.Entra.AllowedTenantIds is empty.') }
    else {
        $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($tenant in $tenantIds) {
            if (-not (Test-NonEmptyGuid $tenant) -or ([string]$tenant).StartsWith('REPLACE-', [StringComparison]::OrdinalIgnoreCase)) { $errors.Add('Authentication.Entra.AllowedTenantIds contains an invalid tenant GUID.') }
            elseif (-not $seen.Add([string]$tenant)) { $errors.Add('Authentication.Entra.AllowedTenantIds contains a duplicate tenant.') }
        }
    }

    $admin = Get-JsonValue $entra 'InitialSuperAdmin'
    if ($null -eq $admin) { $errors.Add('Authentication.Entra.InitialSuperAdmin is missing.') }
    else {
        $adminTenant = Get-JsonValue $admin 'TenantId'
        $adminObject = Get-JsonValue $admin 'ObjectId'
        $adminUpn = Get-JsonValue $admin 'UserPrincipalName'
        $adminDisplay = Get-JsonValue $admin 'DisplayName'
        $adminEmail = Get-JsonValue $admin 'NotificationEmail'
        if (-not (Test-NonEmptyGuid $adminTenant)) { $errors.Add('InitialSuperAdmin.TenantId is invalid.') }
        if (-not (Test-NonEmptyGuid $adminObject)) { $errors.Add('InitialSuperAdmin.ObjectId is invalid.') }
        if (-not (Test-Email $adminUpn) -or -not (Test-Email $adminEmail)) { $errors.Add('InitialSuperAdmin UPN/notification email is invalid.') }
        if ([string]::IsNullOrWhiteSpace([string]$adminDisplay)) { $errors.Add('InitialSuperAdmin.DisplayName is missing.') }
        if ((Test-NonEmptyGuid $adminTenant) -and $tenantIds.Count -gt 0 -and -not (@($tenantIds | ForEach-Object { [string]$_ }) -contains [string]$adminTenant)) { $errors.Add('InitialSuperAdmin.TenantId is not in AllowedTenantIds.') }
    }

    $timeZone = [string](Get-JsonValue $application 'BusinessTimeZone')
    if ([string]::IsNullOrWhiteSpace($timeZone)) { $errors.Add('Application.BusinessTimeZone is missing.') }
    else { try { [void][TimeZoneInfo]::FindSystemTimeZoneById($timeZone) } catch { $errors.Add('Application.BusinessTimeZone is invalid.') } }

    $keysPath = [string](Get-JsonValue $application 'DataProtectionKeysPath')
    $logsPath = [string](Get-JsonValue $application 'LogPath')
    if (-not [IO.Path]::IsPathFullyQualified($keysPath) -or -not (Test-Path -LiteralPath $keysPath -PathType Container) -or -not ([IO.Path]::GetFullPath($keysPath).TrimEnd('\').Equals($ExpectedKeysPath, [StringComparison]::OrdinalIgnoreCase))) { $errors.Add('Application.DataProtectionKeysPath is not the usable canonical keys path.') }
    if (-not [IO.Path]::IsPathFullyQualified($logsPath) -or -not (Test-Path -LiteralPath $logsPath -PathType Container) -or -not ([IO.Path]::GetFullPath($logsPath).TrimEnd('\').Equals($ExpectedLogsPath, [StringComparison]::OrdinalIgnoreCase))) { $errors.Add('Application.LogPath is not the usable canonical logs path.') }
    $thumbprint = [string](Get-JsonValue $application 'DataProtectionKeyEncryptionCertificateThumbprint')
    if ([string]::IsNullOrWhiteSpace($thumbprint) -or $null -eq $Certificate -or -not $thumbprint.Replace(' ', '').Equals($Certificate.Thumbprint.Replace(' ', ''), [StringComparison]::OrdinalIgnoreCase)) { $errors.Add('Application Data Protection certificate reference is missing or unusable.') }

    $domains = @((Get-JsonValue $emailSafety 'AllowedRecipientDomains'))
    $domainSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($domain in $domains) {
        if ($null -eq $domain -or -not (Test-RecipientDomain $domain)) { $errors.Add('EmailSafety.AllowedRecipientDomains contains an invalid domain.') }
        elseif (-not $domainSet.Add([string]$domain)) { $errors.Add('EmailSafety.AllowedRecipientDomains contains a duplicate domain.') }
    }

    $permit = Get-JsonValue $rateLimit 'PermitLimit'
    $window = Get-JsonValue $rateLimit 'WindowSeconds'
    if (-not (Test-IntegerRange $permit 1 10000 $false)) { $errors.Add('PatronLoginRateLimit.PermitLimit is invalid.') }
    if (-not (Test-IntegerRange $window 1 86400 $false)) { $errors.Add('PatronLoginRateLimit.WindowSeconds is invalid.') }

    $requiredSchedules = @('WorkflowProcessing', 'IdentifierProcessing', 'OrganizationRefresh', 'WeeklyStaffSummary', 'EmailOutboxSweep', 'PatronSessionCleanup', 'EmailPayloadCleanup')
    $schedules = Get-JsonValue $hangfire 'Schedules'
    $scheduleNames = if ($null -eq $schedules) { @() } else { @($schedules.PSObject.Properties.Name) }
    $actualScheduleShape = (@($scheduleNames | Sort-Object) -join '|')
    $requiredScheduleShape = (@($requiredSchedules | Sort-Object) -join '|')
    if ($actualScheduleShape -ne $requiredScheduleShape) { $errors.Add('Hangfire.Schedules keys do not match the required shape.') }
    else { foreach ($name in $requiredSchedules) { if (-not (Test-CronShape (Get-JsonValue $schedules $name))) { $errors.Add("Hangfire.Schedules.$name is structurally invalid.") } } }

    $defaultLimit = Get-JsonValue $processing 'Default'
    $timeoutLimit = Get-JsonValue $processing 'Timeouts'
    if (-not (Test-Limit $defaultLimit $true)) { $errors.Add('Hangfire.ProcessingLimits.Default is invalid.') }
    if (-not (Test-Limit $timeoutLimit $false)) { $errors.Add('Hangfire.ProcessingLimits.Timeouts is invalid.') }
    $requiredQueues = @('IdentifierProcessing', 'PurchasePromotion', 'HoldPlacement', 'FulfillmentTracking', 'OutstandingTimeout', 'PendingHoldTimeout', 'HoldPickupTimeout', 'AdditionalCopyTimeout')
    $queues = Get-JsonValue $processing 'Queues'
    $queueNames = if ($null -eq $queues) { @() } else { @($queues.PSObject.Properties.Name) }
    $actualQueueShape = (@($queueNames | Sort-Object) -join '|')
    $requiredQueueShape = (@($requiredQueues | Sort-Object) -join '|')
    if ($actualQueueShape -ne $requiredQueueShape) { $errors.Add('Hangfire.ProcessingLimits.Queues keys do not match the required shape.') }
    else { foreach ($name in $requiredQueues) { if (-not (Test-Limit (Get-JsonValue $queues $name) $false)) { $errors.Add("Hangfire.ProcessingLimits.Queues.$name is invalid.") } } }

    return $errors
}

function New-AppConfig([string] $ConnectionString, [string] $KeysPath, [string] $LogsPath, [string] $CertificateThumbprint) {
    Assert-Guid 'EntraClientId' $EntraClientId
    Assert-Guid 'EntraTenantId' $EntraTenantId
    Assert-Guid 'EntraObjectId' $EntraObjectId
    if ($AdminEmail -and -not (Test-Email $AdminEmail)) { throw 'AdminEmail must be an email address.' }

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

function Compare-PathValue([object] $Actual, [string] $Expected) {
    try {
        if ([string]::IsNullOrWhiteSpace([string]$Actual) -or -not [IO.Path]::IsPathFullyQualified([string]$Actual)) { return $false }
        return [IO.Path]::GetFullPath([string]$Actual).TrimEnd('\').Equals([IO.Path]::GetFullPath($Expected).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

function Test-EquivalentToolPath([string] $Actual, [string] $Expected) {
    if ([string]::IsNullOrWhiteSpace($Actual) -or [string]::IsNullOrWhiteSpace($Expected)) { return $false }
    $actualResolved = Resolve-Tool $Actual @($Actual) @()
    $expectedResolved = Resolve-Tool $Expected @($Expected) @()
    if ([IO.Path]::IsPathRooted($actualResolved) -and [IO.Path]::IsPathRooted($expectedResolved)) { return Compare-PathValue $actualResolved $expectedResolved }
    return $actualResolved.Equals($expectedResolved, [StringComparison]::OrdinalIgnoreCase)
}

function Test-DeploymentConfiguration([string] $Path, [object] $Expected) {
    $errors = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $errors.Add("$Path does not exist."); return $errors }
    try { $actual = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { $errors.Add("$Path is not valid JSON."); return $errors }

    foreach ($name in @('IisSiteName', 'IisAppPoolName')) {
        if (-not ([string](Get-JsonValue $actual $name)).Equals([string](Get-JsonValue $Expected $name), [StringComparison]::OrdinalIgnoreCase)) { $errors.Add("$name is stale or missing.") }
    }
    foreach ($name in @('DeploymentPath', 'StagingRoot', 'BackupRoot', 'ExternalApplicationConfigPath')) {
        if (-not (Compare-PathValue (Get-JsonValue $actual $name) ([string](Get-JsonValue $Expected $name)))) { $errors.Add("$name is stale or missing.") }
    }
    try {
        $actualUri = [Uri]::new([string](Get-JsonValue $actual 'ReadinessUrl'), [UriKind]::Absolute)
        $expectedUri = [Uri]::new([string](Get-JsonValue $Expected 'ReadinessUrl'), [UriKind]::Absolute)
        if (-not $actualUri.AbsoluteUri.Equals($expectedUri.AbsoluteUri, [StringComparison]::OrdinalIgnoreCase)) { $errors.Add('ReadinessUrl is stale or missing.') }
    }
    catch { $errors.Add('ReadinessUrl is stale or malformed.') }

    $expectedSql = Get-SqlConnectionInfo ([string](Get-JsonValue $Expected 'AsapDatabaseConnectionString'))
    foreach ($name in @('AsapDatabaseConnectionString', 'HangfireDatabaseConnectionString')) {
        if (-not (Test-EquivalentSqlConnection (Get-SqlConnectionInfo ([string](Get-JsonValue $actual $name))) $expectedSql)) { $errors.Add("$name is stale, malformed, or has different security settings.") }
    }
    foreach ($name in @('SqlPackagePath', 'SqlCmdPath')) {
        $actualTool = [string](Get-JsonValue $actual $name)
        $expectedTool = [string](Get-JsonValue $Expected $name)
        if (-not (Test-EquivalentToolPath $actualTool $expectedTool)) { $errors.Add("$name is stale or missing.") }
    }
    return $errors
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
if (Test-Path -LiteralPath $root -PathType Container) { Ensure-RootPermissions $root $identity.Name $identity.Sid }
else { Add-Result 'NEEDS ATTENTION' 'Filesystem permissions' "$root does not exist." }

try {
    Ensure-Group 'S-1-5-32-568' 'IIS_IUSRS membership' $identity.Name $identity.Sid $false
    Ensure-Group 'S-1-5-32-544' 'IIS lifecycle permission' $identity.Name $identity.Sid ([bool]$SkipLocalAdministrator)
}
catch { Add-Result 'NEEDS ATTENTION' 'Windows group permissions' $_.Exception.Message }

$resolvedSqlPackage = Resolve-Tool $SqlPackagePath @('SqlPackage.exe', 'SqlPackage') @('C:\Program Files\Microsoft SQL Server\170\DAC\bin\SqlPackage.exe', 'C:\Program Files\Microsoft SQL Server\160\DAC\bin\SqlPackage.exe', 'C:\Program Files\Microsoft SQL Server\150\DAC\bin\SqlPackage.exe')
$resolvedSqlCmd = Resolve-Tool $SqlCmdPath @('sqlcmd.exe', 'sqlcmd') @('C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE', 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE', 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\160\Tools\Binn\SQLCMD.EXE')
foreach ($tool in @(@{ Name = 'PowerShell 7'; Path = 'pwsh.exe' }, @{ Name = 'SqlPackage'; Path = $resolvedSqlPackage }, @{ Name = 'sqlcmd'; Path = $resolvedSqlCmd })) {
    $found = Test-Tool $tool.Path
    Add-Result $(if ($found) { 'PASS' } else { 'NEEDS ATTENTION' }) $tool.Name $(if ($found) { "$($tool.Path) is available." } else { "$($tool.Path) was not found." })
}

$trust = if ($TrustServerCertificate) { 'True' } else { 'False' }
$connectionString = "Server=$SqlServer;Database=$DatabaseName;Integrated Security=True;Encrypt=True;TrustServerCertificate=$trust"
$appConfigExists = Test-Path -LiteralPath $appConfigPath -PathType Leaf
$appConfig = $null
$appConfigReadError = $null
if ($appConfigExists) {
    try { $appConfig = Get-Content -LiteralPath $appConfigPath -Raw | ConvertFrom-Json }
    catch { $appConfigReadError = $_.Exception.Message }
}

$configuredThumbprint = if ($null -ne $appConfig) { [string](Get-JsonValue (Get-JsonValue $appConfig 'Application') 'DataProtectionKeyEncryptionCertificateThumbprint') } else { $null }
$certificate = Get-DataProtectionCertificate $configuredThumbprint
if ((-not $appConfigExists -or $ForceApplicationConfig) -and -not $certificate -and -not $ValidateOnly) {
    $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=CLC ASAP Test Data Protection' -KeyAlgorithm RSA -KeyLength 2048 -KeyUsage KeyEncipherment,DataEncipherment -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddYears(5)
}
if ($certificate) { Ensure-CertificateRead $certificate $identity.Name $identity.Sid }
else { Add-Result 'NEEDS ATTENTION' 'Data Protection certificate' 'No usable configured LocalMachine certificate with a private key is available.' }

if (-not $appConfigExists -or $ForceApplicationConfig) {
    if ($ValidateOnly) { Add-Result 'NEEDS ATTENTION' 'Application configuration' "$appConfigPath is absent or would be replaced." }
    elseif (-not $certificate) { Add-Result 'NEEDS ATTENTION' 'Application configuration' 'Not written because no Data Protection certificate is available.' }
    else {
        Write-Json $appConfigPath (New-AppConfig $connectionString $keysRoot $logsRoot $certificate.Thumbprint)
        $appConfig = Get-Content -LiteralPath $appConfigPath -Raw | ConvertFrom-Json
        $appConfigReadError = $null
    }
}
elseif ($appConfigReadError) {
    Add-Result 'NEEDS ATTENTION' 'Application configuration' "$appConfigPath is invalid JSON: $appConfigReadError"
}

if ($null -ne $appConfig) {
    $appErrors = @(Test-AppConfiguration $appConfig $connectionString $keysRoot $logsRoot $certificate)
    if ($appErrors.Count -eq 0) {
        Add-Result 'PASS' 'Application configuration' "$appConfigPath satisfies the current ASP.NET bootstrap contract."
        Add-Result 'PASS' 'Entra configuration' 'Entra tenant, client, secret, and initial administrator values are valid.'
    }
    else {
        Add-Result 'NEEDS ATTENTION' 'Application configuration' ($appErrors -join ' ')
        Add-Result 'NEEDS ATTENTION' 'Entra configuration' 'Application configuration is not startup-valid; resolve the reported configuration errors before activation.'
    }
}

$deploymentConfig = [ordered]@{ IisSiteName = $IisSiteName; IisAppPoolName = $IisAppPoolName; DeploymentPath = $webRoot; StagingRoot = $stagingRoot; BackupRoot = $backupRoot; ExternalApplicationConfigPath = $appConfigPath; ReadinessUrl = $readyUri.AbsoluteUri; AsapDatabaseConnectionString = $connectionString; HangfireDatabaseConnectionString = $connectionString; SqlPackagePath = $resolvedSqlPackage; SqlCmdPath = $resolvedSqlCmd }
if (-not $ValidateOnly) { Write-Json $deploymentConfigPath $deploymentConfig }
$deploymentErrors = @(Test-DeploymentConfiguration $deploymentConfigPath $deploymentConfig)
if ($deploymentErrors.Count -eq 0) { Add-Result 'PASS' 'Deployment configuration' "$deploymentConfigPath matches the requested host contract." }
else { Add-Result 'NEEDS ATTENTION' 'Deployment configuration' ($deploymentErrors -join ' ') }

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
