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

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$')]
    [string] $IisSiteName = 'ASAP-Test',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$')]
    [string] $IisAppPoolName = 'ASAP-Test',

    [string] $RunnerServiceName,

    [string] $ServiceAccount,

    [string] $SqlPackagePath = 'SqlPackage.exe',

    [string] $SqlCmdPath = 'sqlcmd.exe',

    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string] $HttpsCertificateThumbprint,

    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string] $DataProtectionCertificateThumbprint,

    [ValidatePattern('^[0-9A-Fa-f-]{36}$')]
    [string] $EntraClientId,

    [securestring] $EntraClientSecret,

    [string] $EntraTenantId,

    [string[]] $AllowedTenantIds,

    [string] $EntraObjectId,

    [string] $EntraAdminUpn,

    [string] $EntraAdminDisplayName,

    [string] $EntraNotificationEmail,

    [string[]] $AllowedRecipientDomains,

    [securestring] $ServiceAccountPassword,

    [switch] $TrustServerCertificate,

    [switch] $Force,

    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    Write-Error 'Initialize-AsapTestHost.ps1 requires PowerShell 7 on Windows.'
    exit 1
}

$CanonicalRoot = 'C:\ProgramData\clc-asap'
$CanonicalPaths = [ordered]@{
    Root = 'C:\ProgramData\clc-asap'
    Web = 'C:\ProgramData\clc-asap\web'
    Staging = 'C:\ProgramData\clc-asap\staging'
    Backups = 'C:\ProgramData\clc-asap\backups'
    Config = 'C:\ProgramData\clc-asap\config'
    Keys = 'C:\ProgramData\clc-asap\keys'
    Logs = 'C:\ProgramData\clc-asap\logs'
}
$DeploymentConfigPath = 'C:\ProgramData\clc-asap\config\test-deployment.json'
$ApplicationConfigPath = 'C:\ProgramData\clc-asap\config\test-app.json'
$StatePath = 'C:\ProgramData\clc-asap\config\test-deployment-state.json'
$ExpectedEnvironmentName = 'Testing'
$ExpectedBannerText = 'NON-PRODUCTION'
$ExpectedBusinessTimeZone = 'America/New_York'
$ExpectedPermitLimit = 20
$ExpectedWindowSeconds = 300
$ExpectedDefaultPageSize = 50
$ExpectedDefaultMaxPerRun = 500
$AllowedRecipientDomainsWasSupplied = $PSBoundParameters.ContainsKey('AllowedRecipientDomains')
$EffectiveAllowedRecipientDomains = if ($AllowedRecipientDomainsWasSupplied) {
    @($AllowedRecipientDomains)
}
else {
    @('example.org')
}

$ExpectedSchedules = [ordered]@{
    WorkflowProcessing = '0 * * * *'
    IdentifierProcessing = '*/5 * * * *'
    OrganizationRefresh = '0 2 * * *'
    WeeklyStaffSummary = '0 20 * * 0'
    EmailOutboxSweep = '*/5 * * * *'
    PatronSessionCleanup = '0 3 * * *'
    EmailPayloadCleanup = '30 3 * * *'
}
$ExpectedQueueNames = @(
    'IdentifierProcessing',
    'PurchasePromotion',
    'HoldPlacement',
    'FulfillmentTracking',
    'OutstandingTimeout',
    'PendingHoldTimeout',
    'HoldPickupTimeout',
    'AdditionalCopyTimeout'
)
$Results = [System.Collections.Generic.List[object]]::new()

function Add-Check {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('PASS', 'NEEDS ATTENTION', 'INFO')]
        [string] $Status,

        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $Detail,

        [switch] $Blocking
    )

    [void] $Results.Add([pscustomobject]@{
            Status = $Status
            Name = $Name
            Detail = $Detail
            Blocking = [bool] $Blocking
        })
}

function Get-ObjectProperty {
    param(
        [AllowNull()]
        [object] $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($null -eq $Object) {
        return $null
    }

    if ($Object -is [System.Collections.IDictionary] -and $Object.Contains($Name)) {
        return $Object[$Name]
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Test-ObjectProperty {
    param(
        [AllowNull()]
        [object] $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($null -eq $Object) {
        return $false
    }
    if ($Object -is [System.Collections.IDictionary]) {
        return $Object.Contains($Name)
    }

    return $null -ne $Object.PSObject.Properties[$Name]
}

function Test-Placeholder {
    param(
        [AllowNull()]
        [string] $Value
    )

    return [string]::IsNullOrWhiteSpace($Value) -or
        $Value.StartsWith('REPLACE-', [StringComparison]::OrdinalIgnoreCase)
}

function Get-FullPathSafe {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    try {
        $fullPath = [IO.Path]::GetFullPath($Path)
        if ($fullPath.Length -gt 3) {
            $fullPath = $fullPath.TrimEnd([char] '\', [char] '/')
        }

        return $fullPath
    }
    catch {
        return $null
    }
}

function Test-SamePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Left,

        [Parameter(Mandatory = $true)]
        [string] $Right
    )

    $leftFull = Get-FullPathSafe -Path $Left
    $rightFull = Get-FullPathSafe -Path $Right
    return $null -ne $leftFull -and
        $null -ne $rightFull -and
        $leftFull.Equals($rightFull, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-ExecutablePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ConfiguredPath,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ([string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        return $null
    }

    if ([IO.Path]::IsPathRooted($ConfiguredPath)) {
        if (-not (Test-Path -LiteralPath $ConfiguredPath -PathType Leaf)) {
            return $null
        }

        return (Get-FullPathSafe -Path $ConfiguredPath)
    }

    $command = Get-Command -Name $ConfiguredPath -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $command) {
        return (Get-FullPathSafe -Path $command.Source)
    }

    $commonPaths = @()
    if ($Name.Equals('SqlPackage', [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $commonPaths += Join-Path $env:ProgramFiles 'Microsoft SQL Server\170\DAC\bin\SqlPackage.exe'
        $commonPaths += Join-Path $env:ProgramFiles 'Microsoft SQL Server\DAC\170\SqlPackage.exe'
    }
    if ($Name.Equals('sqlcmd', [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $commonPaths += Join-Path $env:ProgramFiles 'Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE'
    }
    foreach ($candidate in $commonPaths) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Get-FullPathSafe -Path $candidate)
        }
    }

    return $null
}

function Get-BooleanFromText {
    param(
        [AllowNull()]
        [object] $Value
    )

    if ($null -eq $Value) {
        return $null
    }

    $text = ([string] $Value).Trim()
    if ($text -match '^(?i:true|yes|sspi|1|mandatory)$') {
        return $true
    }
    if ($text -match '^(?i:false|no|0|optional)$') {
        return $false
    }

    return $null
}

function Get-SqlContract {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ConnectionString
    )

    try {
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $builder.ConnectionString = $ConnectionString
    }
    catch {
        return $null
    }

    $dataSource = $null
    foreach ($name in @('Data Source', 'Server', 'Address', 'Addr', 'Network Address')) {
        if ($builder.ContainsKey($name)) {
            $dataSource = [string] $builder[$name]
            break
        }
    }

    $database = $null
    foreach ($name in @('Initial Catalog', 'Database')) {
        if ($builder.ContainsKey($name)) {
            $database = [string] $builder[$name]
            break
        }
    }

    $integrated = $null
    foreach ($name in @('Integrated Security', 'Trusted_Connection')) {
        if ($builder.ContainsKey($name)) {
            $integrated = Get-BooleanFromText -Value $builder[$name]
            break
        }
    }

    $encrypt = $null
    if ($builder.ContainsKey('Encrypt')) {
        $encrypt = Get-BooleanFromText -Value $builder['Encrypt']
    }

    $trustServerCertificate = $false
    if ($builder.ContainsKey('TrustServerCertificate')) {
        $parsedTrust = Get-BooleanFromText -Value $builder['TrustServerCertificate']
        if ($null -eq $parsedTrust) {
            return $null
        }

        $trustServerCertificate = $parsedTrust
    }

    if ([string]::IsNullOrWhiteSpace($dataSource) -or
        [string]::IsNullOrWhiteSpace($database) -or
        $null -eq $integrated -or
        $null -eq $encrypt) {
        return $null
    }

    return [pscustomobject]@{
        DataSource = $dataSource
        Database = $database
        IntegratedSecurity = $integrated
        Encrypt = $encrypt
        TrustServerCertificate = $trustServerCertificate
    }
}

function New-SqlConnectionString {
    $trust = if ($TrustServerCertificate) { 'True' } else { 'False' }
    return "Server=$SqlServer;Database=$DatabaseName;Integrated Security=True;Encrypt=True;TrustServerCertificate=$trust"
}

function Test-GuidValue {
    param(
        [AllowNull()]
        [string] $Value
    )

    $parsed = [guid]::Empty
    return [guid]::TryParse($Value, [ref] $parsed) -and $parsed -ne [guid]::Empty
}

function Test-EmailValue {
    param(
        [AllowNull()]
        [string] $Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    try {
        $address = [System.Net.Mail.MailAddress]::new($Value)
        return $address.Address.Equals($Value, [StringComparison]::OrdinalIgnoreCase)
    }
    catch [FormatException] {
        return $false
    }
}

function Test-RecipientDomain {
    param(
        [AllowNull()]
        [string] $Domain
    )

    if ([string]::IsNullOrWhiteSpace($Domain) -or
        $Domain -ne $Domain.Trim() -or
        $Domain.Length -gt 253 -or
        $Domain.Contains('*', [StringComparison]::Ordinal) -or
        $Domain.Contains('@', [StringComparison]::Ordinal) -or
        $Domain.Contains('://', [StringComparison]::Ordinal) -or
        $Domain.StartsWith('.', [StringComparison]::Ordinal) -or
        $Domain.EndsWith('.', [StringComparison]::Ordinal) -or
        $Domain.Contains('..', [StringComparison]::Ordinal)) {
        return $false
    }

    try {
        $ascii = [Globalization.IdnMapping]::new().GetAscii($Domain)
    }
    catch [ArgumentException] {
        return $false
    }

    foreach ($label in $ascii.Split('.')) {
        if ($label.Length -lt 1 -or $label.Length -gt 63 -or
            $label -notmatch '^[A-Za-z0-9-]+$' -or
            $label.StartsWith('-') -or $label.EndsWith('-')) {
            return $false
        }
    }

    return $true
}

function Test-ExactKeySet {
    param(
        [AllowNull()]
        [object] $Object,

        [Parameter(Mandatory = $true)]
        [string[]] $ExpectedKeys
    )

    if ($null -eq $Object) {
        return $false
    }

    $actualKeys = @($Object.PSObject.Properties | ForEach-Object { [string] $_.Name })
    if ($actualKeys.Count -ne $ExpectedKeys.Count) {
        return $false
    }

    $expectedSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($key in $ExpectedKeys) {
        [void] $expectedSet.Add($key)
    }

    foreach ($key in $actualKeys) {
        if (-not $expectedSet.Contains($key)) {
            return $false
        }
    }

    return $true
}

function Test-IntegerEquals {
    param(
        [AllowNull()]
        [object] $Value,

        [Parameter(Mandatory = $true)]
        [int] $Expected
    )

    if ($null -eq $Value) {
        return $false
    }

    $parsed = 0
    return [int]::TryParse(
        ([string] $Value),
        [Globalization.NumberStyles]::Integer,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref] $parsed) -and $parsed -eq $Expected
}

function Test-SameStringSet {
    param(
        [AllowNull()]
        [object] $Left,

        [AllowNull()]
        [object] $Right
    )

    $leftValues = @($Left | ForEach-Object { [string] $_ })
    $rightValues = @($Right | ForEach-Object { [string] $_ })
    if ($leftValues.Count -ne $rightValues.Count) {
        return $false
    }

    $leftSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $rightSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in $leftValues) {
        [void] $leftSet.Add($value)
    }
    foreach ($value in $rightValues) {
        [void] $rightSet.Add($value)
    }

    return $leftSet.SetEquals($rightSet)
}

function Test-StringCollectionValue {
    param(
        [AllowNull()]
        [object] $Value
    )

    return $null -ne $Value -and -not ($Value -is [string])
}

function Get-RunnerServiceInventory {
    try {
        $serviceNames = @(Get-Service -Name 'actions.runner.*' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Name })
        if ($serviceNames.Count -eq 0) {
            return @()
        }

        $services = @(Get-CimInstance -ClassName Win32_Service -ErrorAction Stop |
            Where-Object { $serviceNames -contains $_.Name })
        return $services
    }
    catch {
        return @()
    }
}

function Test-BuiltInServiceIdentity {
    param(
        [AllowNull()]
        [string] $Identity
    )

    $normalized = if ($null -eq $Identity) { '' } else { $Identity.Trim() }
    return @(
        'LocalSystem',
        'NT AUTHORITY\SYSTEM',
        'SYSTEM',
        'LocalService',
        'NT AUTHORITY\LOCALSERVICE',
        'NT AUTHORITY\LOCAL SERVICE',
        'NetworkService',
        'NT AUTHORITY\NETWORKSERVICE',
        'NT AUTHORITY\NETWORK SERVICE'
    ) | Where-Object {
        $normalized.Equals($_, [StringComparison]::OrdinalIgnoreCase)
    } | Measure-Object | Select-Object -ExpandProperty Count
}

function Resolve-IdentitySid {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Identity
    )

    try {
        $account = [System.Security.Principal.NTAccount]::new($Identity)
        return $account.Translate([System.Security.Principal.SecurityIdentifier])
    }
    catch {
        return $null
    }
}

function Test-LocalGroupContainsIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string] $GroupName,

        [Parameter(Mandatory = $true)]
        [string] $Identity
    )

    $expectedSid = Resolve-IdentitySid -Identity $Identity
    if ($null -eq $expectedSid) {
        return $false
    }

    try {
        foreach ($member in @(Get-LocalGroupMember -Group $GroupName -ErrorAction Stop)) {
            try {
                $memberSid = $member.SID
                if ($null -eq $memberSid) {
                $memberSid = Resolve-IdentitySid -Identity ([string] $member.Name)
                }

                if ($null -ne $memberSid -and
                    ([string] $memberSid).Equals([string] $expectedSid, [StringComparison]::OrdinalIgnoreCase)) {
                    return $true
                }
            }
            catch {
                continue
            }
        }
    }
    catch {
        return $false
    }

    return $false
}

function Get-LocalGroupMembershipStatus {
    param(
        [Parameter(Mandatory = $true)]
        [string] $GroupName,

        [Parameter(Mandatory = $true)]
        [string] $Identity
    )

    try {
        $exists = Get-LocalGroup -Name $GroupName -ErrorAction Stop
        if ($null -eq $exists) {
            return [pscustomobject]@{ Exists = $false; Member = $false; Detail = 'Local group was not found.' }
        }

        return [pscustomobject]@{
            Exists = $true
            Member = Test-LocalGroupContainsIdentity -GroupName $GroupName -Identity $Identity
            Detail = "Membership in $GroupName was inspected."
        }
    }
    catch {
        return [pscustomobject]@{ Exists = $false; Member = $false; Detail = $_.Exception.Message }
    }
}

function Ensure-LocalGroupMembership {
    param(
        [Parameter(Mandatory = $true)]
        [string] $GroupName,

        [Parameter(Mandatory = $true)]
        [string] $Identity
    )

    if (Test-LocalGroupContainsIdentity -GroupName $GroupName -Identity $Identity) {
        return $false
    }

    Add-LocalGroupMember -Group $GroupName -Member $Identity -ErrorAction Stop
    return $true
}

function Get-LocalMachineCertificate {
    param(
        [AllowNull()]
        [string] $Thumbprint,

        [Parameter(Mandatory = $true)]
        [string] $Subject
    )

    $normalizedThumbprint = if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        $null
    }
    else {
        $Thumbprint.Replace(' ', '', [StringComparison]::Ordinal).ToUpperInvariant()
    }

    try {
        $certificates = @(Get-ChildItem -Path 'Cert:\LocalMachine\My' -ErrorAction Stop |
            Where-Object {
                $_.HasPrivateKey -and
                $_.NotAfter -gt (Get-Date) -and
                (($null -ne $normalizedThumbprint -and
                    ([string] $_.Thumbprint).Replace(' ', '').ToUpperInvariant() -eq $normalizedThumbprint) -or
                 ($null -eq $normalizedThumbprint -and
                    ([string] $_.Subject).Equals($Subject, [StringComparison]::OrdinalIgnoreCase)))
            } |
            Sort-Object NotAfter -Descending)
        return $certificates | Select-Object -First 1
    }
    catch {
        return $null
    }
}

function Get-PrivateKeyFilePath {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate
    )

    try {
        $rsa = $Certificate.GetRSAPrivateKey()
        if ($rsa -is [System.Security.Cryptography.RSACng]) {
            $keyName = $rsa.Key.KeyName
            if (-not [string]::IsNullOrWhiteSpace($keyName)) {
                return Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$keyName"
            }
        }

        if ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            $containerName = $rsa.CspKeyContainerInfo.UniqueKeyContainerName
            if (-not [string]::IsNullOrWhiteSpace($containerName)) {
                return Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$containerName"
            }
        }
    }
    catch {
        return $null
    }

    return $null
}

function Test-PrivateKeyReadRule {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Identity
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $expectedSid = Resolve-IdentitySid -Identity $Identity
    if ($null -eq $expectedSid) {
        return $false
    }

    try {
        $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
        foreach ($rule in @($acl.Access)) {
            if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) {
                continue
            }

            $ruleSid = $null
            try {
                $ruleSid = $rule.IdentityReference.Translate(
                    [System.Security.Principal.SecurityIdentifier])
            }
            catch {
                continue
            }

            $hasRead = (([int] $rule.FileSystemRights -band
                    [int] [System.Security.AccessControl.FileSystemRights]::Read) -eq
                [int] [System.Security.AccessControl.FileSystemRights]::Read)
            if ($hasRead -and ([string] $ruleSid).Equals(
                    [string] $expectedSid,
                    [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
    }
    catch {
        return $false
    }

    return $false
}

function Grant-PrivateKeyReadRule {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Identity
    )

    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $Identity,
        [System.Security.AccessControl.FileSystemRights]::Read,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl -ErrorAction Stop
}

function Test-CertificatePrivateKeyProbe {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate
    )

    try {
        if (-not $Certificate.HasPrivateKey) {
            return [pscustomobject]@{ Success = $false; Detail = 'The certificate has no private key.' }
        }

        $rsa = $Certificate.GetRSAPrivateKey()
        if ($null -eq $rsa) {
            return [pscustomobject]@{ Success = $false; Detail = 'The RSA private key could not be opened.' }
        }

        $data = [Text.Encoding]::UTF8.GetBytes('ASAP test host Data Protection private-key probe')
        $signature = $rsa.SignData(
            $data,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $verified = $rsa.VerifyData(
            $data,
            $signature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        if (-not $verified) {
            return [pscustomobject]@{ Success = $false; Detail = 'Private-key signing verification failed.' }
        }

        return [pscustomobject]@{
            Success = $true
            Detail = "A sign/verify probe succeeded under $([Security.Principal.WindowsIdentity]::GetCurrent().Name)."
        }
    }
    catch {
        return [pscustomobject]@{ Success = $false; Detail = $_.Exception.Message }
    }
}

function Get-DirectoryAccessRule {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Identity,

        [Parameter(Mandatory = $true)]
        [System.Security.AccessControl.FileSystemRights] $Rights
    )

    return [System.Security.AccessControl.FileSystemAccessRule]::new(
        $Identity,
        $Rights,
        [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [System.Security.AccessControl.InheritanceFlags]::ObjectInherit,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
}

function Set-CanonicalRootAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RootPath,

        [Parameter(Mandatory = $true)]
        [string] $ServiceIdentity
    )

    $acl = Get-Acl -LiteralPath $RootPath -ErrorAction Stop
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) {
        [void] $acl.RemoveAccessRuleSpecific($rule)
    }

    foreach ($rule in @(
            (Get-DirectoryAccessRule -Identity 'NT AUTHORITY\SYSTEM' -Rights FullControl),
            (Get-DirectoryAccessRule -Identity 'BUILTIN\Administrators' -Rights FullControl),
            (Get-DirectoryAccessRule -Identity $ServiceIdentity -Rights Modify)
        )) {
        $acl.AddAccessRule($rule)
    }

    Set-Acl -LiteralPath $RootPath -AclObject $acl -ErrorAction Stop
}

function Set-ManagedChildAclInheritance {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    foreach ($rule in @($acl.Access | Where-Object { -not $_.IsInherited })) {
        [void] $acl.RemoveAccessRuleSpecific($rule)
    }
    $acl.SetAccessRuleProtection($false, $false)
    Set-Acl -LiteralPath $Path -AclObject $acl -ErrorAction Stop
}

function Get-SidForAclRule {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Rule
    )

    try {
        return $Rule.IdentityReference.Translate(
            [System.Security.Principal.SecurityIdentifier])
    }
    catch {
        return $null
    }
}

function Test-CanonicalAsapAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RootPath,

        [Parameter(Mandatory = $true)]
        [string] $ServiceIdentity
    )

    $issues = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $RootPath -PathType Container)) {
        [void] $issues.Add('The canonical ASAP root does not exist.')
        return @($issues)
    }

    $expected = @(
        [pscustomobject]@{ Identity = 'NT AUTHORITY\SYSTEM'; Rights = [System.Security.AccessControl.FileSystemRights]::FullControl },
        [pscustomobject]@{ Identity = 'BUILTIN\Administrators'; Rights = [System.Security.AccessControl.FileSystemRights]::FullControl },
        [pscustomobject]@{ Identity = $ServiceIdentity; Rights = [System.Security.AccessControl.FileSystemRights]::Modify }
    )
    $expectedWithSids = @($expected | ForEach-Object {
            [pscustomobject]@{
                Identity = $_.Identity
                Rights = $_.Rights
                Sid = Resolve-IdentitySid -Identity $_.Identity
            }
        })

    if ($expectedWithSids | Where-Object { $null -eq $_.Sid }) {
        [void] $issues.Add('One or more canonical ACL identities could not be resolved.')
        return @($issues)
    }

    try {
        $rootAcl = Get-Acl -LiteralPath $RootPath -ErrorAction Stop
        if (-not $rootAcl.AreAccessRulesProtected) {
            [void] $issues.Add('The canonical root inherits an uncontrolled parent ACL.')
        }

        $rootRules = @($rootAcl.Access)
        foreach ($rule in $rootRules) {
            $sid = Get-SidForAclRule -Rule $rule
            $match = $expectedWithSids | Where-Object {
                $null -ne $sid -and ([string] $_.Sid).Equals([string] $sid, [StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1
            $inheritsToChildren = (($rule.InheritanceFlags -band
                    ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                        [System.Security.AccessControl.InheritanceFlags]::ObjectInherit)) -eq
                ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                    [System.Security.AccessControl.InheritanceFlags]::ObjectInherit))
            if ($null -eq $match -or
                $rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or
                $rule.IsInherited -or
                -not $inheritsToChildren) {
                [void] $issues.Add("Unexpected or inherited access rule exists on the canonical root for $($rule.IdentityReference).")
            }
        }

        foreach ($expectedRule in $expectedWithSids) {
            $matchingRules = @($rootRules | Where-Object {
                    $sid = Get-SidForAclRule -Rule $_
                    $null -ne $sid -and
                        ([string] $sid).Equals([string] $expectedRule.Sid, [StringComparison]::OrdinalIgnoreCase) -and
                        $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and
                        -not $_.IsInherited -and
                        $_.FileSystemRights -eq $expectedRule.Rights -and
                        (($_.InheritanceFlags -band
                            ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                                [System.Security.AccessControl.InheritanceFlags]::ObjectInherit)) -eq
                            ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                                [System.Security.AccessControl.InheritanceFlags]::ObjectInherit))
                })
            if ($matchingRules.Count -ne 1) {
                [void] $issues.Add("The canonical root is missing inheritable $($expectedRule.Rights) for $($expectedRule.Identity).")
            }
        }

        $children = @(Get-ChildItem -LiteralPath $RootPath -Recurse -Force -ErrorAction Stop)
        foreach ($child in $children) {
            $childAcl = Get-Acl -LiteralPath $child.FullName -ErrorAction Stop
            if ($childAcl.AreAccessRulesProtected) {
                [void] $issues.Add("Child ACL inheritance is protected: $($child.FullName)")
            }

            $explicitRules = @($childAcl.Access | Where-Object { -not $_.IsInherited })
            if ($explicitRules.Count -gt 0) {
                [void] $issues.Add("Child ACL contains explicit overriding rules: $($child.FullName)")
            }

            foreach ($expectedRule in $expectedWithSids) {
                $matchingRules = @($childAcl.Access | Where-Object {
                        $sid = Get-SidForAclRule -Rule $_
                        $null -ne $sid -and
                            ([string] $sid).Equals([string] $expectedRule.Sid, [StringComparison]::OrdinalIgnoreCase) -and
                            $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and
                            $_.IsInherited -and
                            $_.FileSystemRights -eq $expectedRule.Rights
                    })
                if ($matchingRules.Count -ne 1) {
                    [void] $issues.Add("Child ACL inheritance is missing $($expectedRule.Rights) for $($expectedRule.Identity): $($child.FullName)")
                }
            }
        }
    }
    catch {
        [void] $issues.Add("ACL audit failed: $($_.Exception.Message)")
    }

    return @($issues)
}

function Get-PlainTextSecureString {
    param(
        [Parameter(Mandatory = $true)]
        [securestring] $Value
    )

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Get-EntraBootstrapValues {
    $tenantValues = @()
    if ($null -ne $AllowedTenantIds -and $AllowedTenantIds.Count -gt 0) {
        $tenantValues = @($AllowedTenantIds)
    }
    elseif (-not [string]::IsNullOrWhiteSpace($EntraTenantId)) {
        $tenantValues = @($EntraTenantId)
    }
    else {
        $tenantValues = @('REPLACE-SET-ENTRA-TENANT-ID')
    }

    $initialTenant = if (-not [string]::IsNullOrWhiteSpace($EntraTenantId)) {
        $EntraTenantId
    }
    else {
        [string] $tenantValues[0]
    }
    $adminUpn = if ([string]::IsNullOrWhiteSpace($EntraAdminUpn)) {
        'REPLACE-SET-INITIAL-ADMIN-UPN'
    }
    else {
        $EntraAdminUpn
    }
    $notificationEmail = if ([string]::IsNullOrWhiteSpace($EntraNotificationEmail)) {
        if ($adminUpn -notlike 'REPLACE-*') { $adminUpn } else { 'REPLACE-SET-NOTIFICATION-EMAIL' }
    }
    else {
        $EntraNotificationEmail
    }
    $secret = if ($null -eq $EntraClientSecret) {
        'REPLACE-SET-ENTRA-CLIENT-SECRET'
    }
    else {
        Get-PlainTextSecureString -Value $EntraClientSecret
    }

    return [pscustomobject]@{
        ClientId = if ([string]::IsNullOrWhiteSpace($EntraClientId)) { 'REPLACE-SET-ENTRA-CLIENT-ID' } else { $EntraClientId }
        ClientSecret = $secret
        AllowedTenantIds = $tenantValues
        InitialTenantId = $initialTenant
        ObjectId = if ([string]::IsNullOrWhiteSpace($EntraObjectId)) { 'REPLACE-SET-INITIAL-ADMIN-OBJECT-ID' } else { $EntraObjectId }
        UserPrincipalName = $adminUpn
        DisplayName = if ([string]::IsNullOrWhiteSpace($EntraAdminDisplayName)) { 'ASAP Test Administrator' } else { $EntraAdminDisplayName }
        NotificationEmail = $notificationEmail
    }
}

function New-ApplicationConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [object] $EntraValues,

        [Parameter(Mandatory = $true)]
        [string] $AsapConnectionString,

        [Parameter(Mandatory = $true)]
        [string] $HangfireConnectionString,

        [Parameter(Mandatory = $true)]
        [string] $CertificateThumbprint
    )

    $schedules = [ordered]@{}
    foreach ($entry in $ExpectedSchedules.GetEnumerator()) {
        $schedules[$entry.Key] = $entry.Value
    }

    $queues = [ordered]@{}
    foreach ($queue in $ExpectedQueueNames) {
        $queues[$queue] = [ordered]@{ PageSize = $null; MaxPerRun = $null }
    }

    return [ordered]@{
        Environment = [ordered]@{
            Name = $ExpectedEnvironmentName
            IsNonProduction = $true
            UiBannerText = $ExpectedBannerText
        }
        ConnectionStrings = [ordered]@{
            AsapDatabase = $AsapConnectionString
            HangfireDatabase = $HangfireConnectionString
        }
        Authentication = [ordered]@{
            Entra = [ordered]@{
                ClientId = $EntraValues.ClientId
                ClientSecret = $EntraValues.ClientSecret
                AllowedTenantIds = @($EntraValues.AllowedTenantIds)
                InitialSuperAdmin = [ordered]@{
                    TenantId = $EntraValues.InitialTenantId
                    ObjectId = $EntraValues.ObjectId
                    UserPrincipalName = $EntraValues.UserPrincipalName
                    DisplayName = $EntraValues.DisplayName
                    NotificationEmail = $EntraValues.NotificationEmail
                }
            }
        }
        Application = [ordered]@{
            BusinessTimeZone = $ExpectedBusinessTimeZone
            DataProtectionKeysPath = $CanonicalPaths.Keys
            DataProtectionKeyEncryptionCertificateThumbprint = $CertificateThumbprint
            LogPath = $CanonicalPaths.Logs
        }
        EmailSafety = [ordered]@{
            AllowedRecipientDomains = @($EffectiveAllowedRecipientDomains)
        }
        PatronLoginRateLimit = [ordered]@{
            PermitLimit = $ExpectedPermitLimit
            WindowSeconds = $ExpectedWindowSeconds
        }
        Hangfire = [ordered]@{
            Schedules = $schedules
            ProcessingLimits = [ordered]@{
                Default = [ordered]@{ PageSize = $ExpectedDefaultPageSize; MaxPerRun = $ExpectedDefaultMaxPerRun }
                Timeouts = [ordered]@{ PageSize = $null; MaxPerRun = $null }
                Queues = $queues
            }
        }
    }
}

function Write-JsonAtomically {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [object] $Value
    )

    $directory = Split-Path -Parent $Path
    $temporaryPath = Join-Path $directory ('.' + [IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $json = $Value | ConvertTo-Json -Depth 12
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Read-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 20
    }
    catch {
        return $null
    }
}

function Compare-ConnectionStrings {
    param(
        [AllowNull()]
        [object] $Actual,

        [Parameter(Mandatory = $true)]
        [object] $Expected
    )

    $issues = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $Actual) {
        [void] $issues.Add('connection string is missing')
        return @($issues)
    }

    $actualContract = Get-SqlContract -ConnectionString ([string] $Actual)
    $expectedContract = Get-SqlContract -ConnectionString ([string] $Expected)
    if ($null -eq $actualContract -or $null -eq $expectedContract) {
        [void] $issues.Add('connection string is invalid or does not require encrypted Windows Integrated Security')
        return @($issues)
    }

    if (-not ([string] $actualContract.DataSource).Equals(
            [string] $expectedContract.DataSource,
            [StringComparison]::OrdinalIgnoreCase)) {
        [void] $issues.Add("server is '$($actualContract.DataSource)', expected '$($expectedContract.DataSource)'")
    }
    if (-not ([string] $actualContract.Database).Equals(
            [string] $expectedContract.Database,
            [StringComparison]::OrdinalIgnoreCase)) {
        [void] $issues.Add("database is '$($actualContract.Database)', expected '$($expectedContract.Database)'")
    }
    if (-not $actualContract.IntegratedSecurity) {
        [void] $issues.Add('Windows Integrated Security is not enabled')
    }
    if (-not $actualContract.Encrypt) {
        [void] $issues.Add('SQL encryption is not required')
    }
    if ($actualContract.TrustServerCertificate -ne $expectedContract.TrustServerCertificate) {
        [void] $issues.Add("TrustServerCertificate is $($actualContract.TrustServerCertificate), expected $($expectedContract.TrustServerCertificate)")
    }

    return @($issues)
}

function Test-DeploymentConfigurationFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [object] $Expected
    )

    $issues = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        [void] $issues.Add('deployment configuration file is missing')
        return @($issues)
    }

    $actual = Read-JsonFile -Path $Path
    if ($null -eq $actual) {
        [void] $issues.Add('deployment configuration is not valid JSON')
        return @($issues)
    }

    foreach ($name in @(
            'IisSiteName',
            'IisAppPoolName',
            'DeploymentPath',
            'StagingRoot',
            'BackupRoot',
            'ExternalApplicationConfigPath',
            'ReadinessUrl',
            'AsapDatabaseConnectionString',
            'HangfireDatabaseConnectionString',
            'SqlPackagePath',
            'SqlCmdPath'
        )) {
        if (-not (Test-ObjectProperty -Object $actual -Name $name)) {
            [void] $issues.Add("$name is missing")
            continue
        }

        $actualValue = Get-ObjectProperty -Object $actual -Name $name
        $expectedValue = Get-ObjectProperty -Object $Expected -Name $name
        if ($name -in @('DeploymentPath', 'StagingRoot', 'BackupRoot', 'ExternalApplicationConfigPath')) {
            if (-not (Test-SamePath -Left ([string] $actualValue) -Right ([string] $expectedValue))) {
                [void] $issues.Add("$name is '$actualValue', expected '$expectedValue'")
            }
        }
        elseif ($name -in @('AsapDatabaseConnectionString', 'HangfireDatabaseConnectionString')) {
            foreach ($issue in @(Compare-ConnectionStrings -Actual $actualValue -Expected $expectedValue)) {
                [void] $issues.Add("$name $issue")
            }
        }
        elseif ($name -in @('SqlPackagePath', 'SqlCmdPath')) {
            $actualTool = Resolve-ExecutablePath -ConfiguredPath ([string] $actualValue) -Name $name
            $expectedTool = Resolve-ExecutablePath -ConfiguredPath ([string] $expectedValue) -Name $name
            if ($null -eq $actualTool -or $null -eq $expectedTool -or
                -not $actualTool.Equals($expectedTool, [StringComparison]::OrdinalIgnoreCase)) {
                [void] $issues.Add("$name does not resolve to the expected executable")
            }
        }
        elseif (-not ([string] $actualValue).Equals([string] $expectedValue, [StringComparison]::Ordinal)) {
            [void] $issues.Add("$name is '$actualValue', expected '$expectedValue'")
        }
    }

    return @($issues)
}

function Test-ExternalApplicationConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [object] $Expected,

        [Parameter(Mandatory = $true)]
        [object] $EntraInputs,

        [Parameter(Mandatory = $true)]
        [string] $CertificateThumbprint
    )

    $issues = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        [void] $issues.Add('external application configuration file is missing')
        return @($issues)
    }

    $actual = Read-JsonFile -Path $Path
    if ($null -eq $actual) {
        [void] $issues.Add('external application configuration is not valid JSON')
        return @($issues)
    }

    $environment = Get-ObjectProperty -Object $actual -Name 'Environment'
    $connections = Get-ObjectProperty -Object $actual -Name 'ConnectionStrings'
    $authentication = Get-ObjectProperty -Object $actual -Name 'Authentication'
    $application = Get-ObjectProperty -Object $actual -Name 'Application'
    $emailSafety = Get-ObjectProperty -Object $actual -Name 'EmailSafety'
    $rateLimit = Get-ObjectProperty -Object $actual -Name 'PatronLoginRateLimit'
    $hangfire = Get-ObjectProperty -Object $actual -Name 'Hangfire'
    if ($null -eq $environment -or $null -eq $connections -or $null -eq $authentication -or
        $null -eq $application -or $null -eq $emailSafety -or $null -eq $rateLimit -or $null -eq $hangfire) {
        [void] $issues.Add('one or more required external configuration sections are missing')
        return @($issues)
    }

    if (-not ([string] (Get-ObjectProperty $environment 'Name')).Equals($ExpectedEnvironmentName, [StringComparison]::Ordinal)) {
        [void] $issues.Add('Environment.Name must be Testing')
    }
    if ((Get-ObjectProperty $environment 'IsNonProduction') -ne $true) {
        [void] $issues.Add('Environment.IsNonProduction must be true')
    }
    if (-not ([string] (Get-ObjectProperty $environment 'UiBannerText')).Equals($ExpectedBannerText, [StringComparison]::Ordinal)) {
        [void] $issues.Add('Environment.UiBannerText must be NON-PRODUCTION')
    }

    $expectedAsap = Get-ObjectProperty $Expected.ConnectionStrings 'AsapDatabase'
    $expectedHangfire = Get-ObjectProperty $Expected.ConnectionStrings 'HangfireDatabase'
    foreach ($pair in @(
            [pscustomobject]@{ Name = 'ConnectionStrings.AsapDatabase'; Actual = Get-ObjectProperty $connections 'AsapDatabase'; Expected = $expectedAsap },
            [pscustomobject]@{ Name = 'ConnectionStrings.HangfireDatabase'; Actual = Get-ObjectProperty $connections 'HangfireDatabase'; Expected = $expectedHangfire }
        )) {
        foreach ($issue in @(Compare-ConnectionStrings -Actual $pair.Actual -Expected $pair.Expected)) {
            [void] $issues.Add("$($pair.Name) $issue")
        }
    }

    foreach ($pair in @(
            [pscustomobject]@{ Name = 'BusinessTimeZone'; Actual = Get-ObjectProperty $application 'BusinessTimeZone'; Expected = $ExpectedBusinessTimeZone },
            [pscustomobject]@{ Name = 'DataProtectionKeysPath'; Actual = Get-ObjectProperty $application 'DataProtectionKeysPath'; Expected = $CanonicalPaths.Keys },
            [pscustomobject]@{ Name = 'LogPath'; Actual = Get-ObjectProperty $application 'LogPath'; Expected = $CanonicalPaths.Logs },
            [pscustomobject]@{ Name = 'DataProtectionKeyEncryptionCertificateThumbprint'; Actual = Get-ObjectProperty $application 'DataProtectionKeyEncryptionCertificateThumbprint'; Expected = $CertificateThumbprint }
        )) {
        $matches = if ($pair.Name -in @('DataProtectionKeysPath', 'LogPath')) {
            Test-SamePath -Left ([string] $pair.Actual) -Right ([string] $pair.Expected)
        }
        elseif ($pair.Name -eq 'DataProtectionKeyEncryptionCertificateThumbprint') {
            ([string] $pair.Actual).Replace(' ', '', [StringComparison]::Ordinal).Equals(
                ([string] $pair.Expected).Replace(' ', '', [StringComparison]::Ordinal),
                [StringComparison]::OrdinalIgnoreCase)
        }
        else {
            ([string] $pair.Actual).Equals([string] $pair.Expected, [StringComparison]::OrdinalIgnoreCase)
        }
        if (-not $matches) {
            [void] $issues.Add("Application.$($pair.Name) is '$($pair.Actual)', expected '$($pair.Expected)'")
        }
    }

    if (-not (Test-Path -LiteralPath $CanonicalPaths.Keys -PathType Container)) {
        [void] $issues.Add('canonical Data Protection key directory is unavailable')
    }
    if (-not (Test-Path -LiteralPath $CanonicalPaths.Logs -PathType Container)) {
        [void] $issues.Add('canonical log directory is unavailable')
    }

    $entra = Get-ObjectProperty $authentication 'Entra'
    if ($null -eq $entra) {
        [void] $issues.Add('Authentication.Entra is missing')
    }
    else {
        $clientId = Get-ObjectProperty $entra 'ClientId'
        if (-not (Test-GuidValue -Value ([string] $clientId))) {
            [void] $issues.Add('Authentication.Entra.ClientId is not a non-empty GUID')
        }
        $clientSecret = [string] (Get-ObjectProperty $entra 'ClientSecret')
        if (Test-Placeholder -Value $clientSecret) {
            [void] $issues.Add('Authentication.Entra.ClientSecret is missing or still a placeholder')
        }

        $tenants = Get-ObjectProperty $entra 'AllowedTenantIds'
        $tenantValues = @($tenants)
        if ($null -eq $tenants -or $tenantValues.Count -eq 0) {
            [void] $issues.Add('Authentication.Entra.AllowedTenantIds is missing')
        }
        else {
            if (-not (Test-StringCollectionValue -Value $tenants)) {
                [void] $issues.Add('Authentication.Entra.AllowedTenantIds must be a JSON array')
            }
            $seenTenants = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($tenant in $tenantValues) {
                if (-not (Test-GuidValue -Value ([string] $tenant))) {
                    [void] $issues.Add('Authentication.Entra.AllowedTenantIds contains an invalid GUID')
                }
                if (-not $seenTenants.Add([string] $tenant)) {
                    [void] $issues.Add('Authentication.Entra.AllowedTenantIds contains a duplicate')
                }
            }
        }

        $admin = Get-ObjectProperty $entra 'InitialSuperAdmin'
        if ($null -eq $admin) {
            [void] $issues.Add('Authentication.Entra.InitialSuperAdmin is missing')
        }
        else {
            if (-not (Test-GuidValue -Value ([string] (Get-ObjectProperty $admin 'TenantId')))) {
                [void] $issues.Add('initial super-admin tenant ID is invalid')
            }
            if (-not (Test-GuidValue -Value ([string] (Get-ObjectProperty $admin 'ObjectId')))) {
                [void] $issues.Add('initial super-admin object ID is invalid')
            }
            if ([string]::IsNullOrWhiteSpace([string] (Get-ObjectProperty $admin 'UserPrincipalName')) -or
                -not (Test-EmailValue -Value ([string] (Get-ObjectProperty $admin 'UserPrincipalName')))) {
                [void] $issues.Add('initial super-admin UPN is missing or invalid')
            }
            if ([string]::IsNullOrWhiteSpace([string] (Get-ObjectProperty $admin 'DisplayName'))) {
                [void] $issues.Add('initial super-admin display name is missing')
            }
            if (-not (Test-EmailValue -Value ([string] (Get-ObjectProperty $admin 'NotificationEmail')))) {
                [void] $issues.Add('initial super-admin notification email is missing or invalid')
            }
            $adminTenant = [string] (Get-ObjectProperty $admin 'TenantId')
            if ($tenantValues.Count -gt 0 -and
                -not ($tenantValues | Where-Object { ([string] $_).Equals($adminTenant, [StringComparison]::OrdinalIgnoreCase) })) {
                [void] $issues.Add('initial super-admin tenant is not in AllowedTenantIds')
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($EntraInputs.ClientId) -and
            -not ([string] $clientId).Equals($EntraInputs.ClientId, [StringComparison]::OrdinalIgnoreCase)) {
            [void] $issues.Add('configured Entra client ID does not match the supplied bootstrap value')
        }
        if ($null -ne $EntraInputs.AllowedTenantIds -and
            -not (Test-SameStringSet -Left $tenantValues -Right $EntraInputs.AllowedTenantIds)) {
            [void] $issues.Add('configured allowed tenant IDs do not match the supplied bootstrap values')
        }
        if (-not [string]::IsNullOrWhiteSpace($EntraInputs.TenantId) -and
            -not ([string] (Get-ObjectProperty $admin 'TenantId')).Equals($EntraInputs.TenantId, [StringComparison]::OrdinalIgnoreCase)) {
            [void] $issues.Add('configured initial admin tenant does not match the supplied bootstrap tenant')
        }
        if (-not [string]::IsNullOrWhiteSpace($EntraInputs.ObjectId) -and
            -not ([string] (Get-ObjectProperty $admin 'ObjectId')).Equals($EntraInputs.ObjectId, [StringComparison]::OrdinalIgnoreCase)) {
            [void] $issues.Add('configured initial admin object ID does not match the supplied bootstrap value')
        }
        if (-not [string]::IsNullOrWhiteSpace($EntraInputs.AdminUpn) -and
            -not ([string] (Get-ObjectProperty $admin 'UserPrincipalName')).Equals($EntraInputs.AdminUpn, [StringComparison]::OrdinalIgnoreCase)) {
            [void] $issues.Add('configured initial admin UPN does not match the supplied bootstrap value')
        }
        if (-not [string]::IsNullOrWhiteSpace($EntraInputs.AdminDisplayName) -and
            -not ([string] (Get-ObjectProperty $admin 'DisplayName')).Equals($EntraInputs.AdminDisplayName, [StringComparison]::Ordinal)) {
            [void] $issues.Add('configured initial admin display name does not match the supplied bootstrap value')
        }
        if (-not [string]::IsNullOrWhiteSpace($EntraInputs.NotificationEmail) -and
            -not ([string] (Get-ObjectProperty $admin 'NotificationEmail')).Equals($EntraInputs.NotificationEmail, [StringComparison]::OrdinalIgnoreCase)) {
            [void] $issues.Add('configured notification email does not match the supplied bootstrap value')
        }
        if ($null -ne $EntraInputs.ClientSecretPlainText -and
            -not ([string] (Get-ObjectProperty $entra 'ClientSecret')).Equals($EntraInputs.ClientSecretPlainText, [StringComparison]::Ordinal)) {
            [void] $issues.Add('configured Entra client secret does not match the supplied secure bootstrap value')
        }
    }

    $domains = Get-ObjectProperty $emailSafety 'AllowedRecipientDomains'
    $domainValues = @($domains)
    if ($null -eq $domains -or $domainValues.Count -eq 0) {
        [void] $issues.Add('EmailSafety.AllowedRecipientDomains is missing')
    }
    else {
        if (-not (Test-StringCollectionValue -Value $domains)) {
            [void] $issues.Add('EmailSafety.AllowedRecipientDomains must be a JSON array')
        }
        $seenDomains = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($domain in $domainValues) {
            if (-not (Test-RecipientDomain -Domain ([string] $domain))) {
                [void] $issues.Add("invalid recipient domain '$domain'")
            }
            if (-not $seenDomains.Add([string] $domain)) {
                [void] $issues.Add("duplicate recipient domain '$domain'")
            }
        }
        if ($EntraInputs.AllowedRecipientDomainsWasSupplied -and
            ($domainValues -join '|') -ne (@($EntraInputs.AllowedRecipientDomains) -join '|')) {
            [void] $issues.Add('allowed recipient domains do not match the supplied bootstrap values')
        }
    }

    if (-not (Test-IntegerEquals -Value (Get-ObjectProperty $rateLimit 'PermitLimit') -Expected $ExpectedPermitLimit) -or
        -not (Test-IntegerEquals -Value (Get-ObjectProperty $rateLimit 'WindowSeconds') -Expected $ExpectedWindowSeconds)) {
        [void] $issues.Add('PatronLoginRateLimit must be 20 permits per 300 seconds for the test host')
    }

    $schedules = Get-ObjectProperty $hangfire 'Schedules'
    if (-not (Test-ExactKeySet -Object $schedules -ExpectedKeys @($ExpectedSchedules.Keys))) {
        [void] $issues.Add('Hangfire.Schedules has the wrong exact case-sensitive key set')
    }
    else {
        foreach ($entry in $ExpectedSchedules.GetEnumerator()) {
            if (-not ([string] (Get-ObjectProperty $schedules $entry.Key)).Equals($entry.Value, [StringComparison]::Ordinal)) {
                [void] $issues.Add("Hangfire.Schedules.$($entry.Key) is not the canonical test-host schedule")
            }
        }
    }

    $processing = Get-ObjectProperty $hangfire 'ProcessingLimits'
    if ($null -eq $processing) {
        [void] $issues.Add('Hangfire.ProcessingLimits is missing')
    }
    else {
        $defaultLimit = Get-ObjectProperty $processing 'Default'
        if ($null -eq $defaultLimit -or
            -not (Test-IntegerEquals -Value (Get-ObjectProperty $defaultLimit 'PageSize') -Expected $ExpectedDefaultPageSize) -or
            -not (Test-IntegerEquals -Value (Get-ObjectProperty $defaultLimit 'MaxPerRun') -Expected $ExpectedDefaultMaxPerRun)) {
            [void] $issues.Add('Hangfire.ProcessingLimits.Default must be PageSize 50 and MaxPerRun 500')
        }

        $timeouts = Get-ObjectProperty $processing 'Timeouts'
        if ($null -eq $timeouts -or
            (Test-ObjectProperty -Object $timeouts -Name 'PageSize' -and $null -ne (Get-ObjectProperty $timeouts 'PageSize')) -or
            (Test-ObjectProperty -Object $timeouts -Name 'MaxPerRun' -and $null -ne (Get-ObjectProperty $timeouts 'MaxPerRun'))) {
            [void] $issues.Add('Hangfire.ProcessingLimits.Timeouts must contain nullable PageSize and MaxPerRun values')
        }

        $queues = Get-ObjectProperty $processing 'Queues'
        if (-not (Test-ExactKeySet -Object $queues -ExpectedKeys $ExpectedQueueNames)) {
            [void] $issues.Add('Hangfire.ProcessingLimits.Queues has the wrong exact case-sensitive key set')
        }
        else {
            foreach ($queue in $ExpectedQueueNames) {
                $limit = Get-ObjectProperty $queues $queue
                if ($null -eq $limit -or
                    (Test-ObjectProperty -Object $limit -Name 'PageSize' -and $null -ne (Get-ObjectProperty $limit 'PageSize')) -or
                    (Test-ObjectProperty -Object $limit -Name 'MaxPerRun' -and $null -ne (Get-ObjectProperty $limit 'MaxPerRun'))) {
                    [void] $issues.Add("Hangfire.ProcessingLimits.Queues.$queue must contain nullable values")
                }
            }
        }
    }

    return @($issues)
}

function Get-IisEnvironmentValue {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PoolName
    )

    $filter = "system.applicationHost/applicationPools/add[@name='$PoolName']/environmentVariables"
    try {
        $section = Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -ErrorAction Stop
        foreach ($entry in @($section.Collection)) {
            if ([string] $entry.name -ceq 'Asap__ConfigFile') {
                return [string] $entry.value
            }
        }
    }
    catch {
        return $null
    }

    return $null
}

function Get-IisConfigurationAudit {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SiteName,

        [Parameter(Mandatory = $true)]
        [string] $PoolName,

        [Parameter(Mandatory = $true)]
        [string] $PhysicalPath,

        [Parameter(Mandatory = $true)]
        [string] $ServiceIdentity,

        [Parameter(Mandatory = $true)]
        [Uri] $Readiness,

        [AllowNull()]
        [string] $ExpectedHttpsThumbprint
    )

    $audit = [ordered]@{
        SiteExists = $false
        PoolExists = $false
        SitePathMatches = $false
        SitePoolMatches = $false
        PoolConfigurationMatches = $false
        ConfigPointerMatches = $false
        HttpsBindingMatches = $false
        Site = $null
        Pool = $null
        Issues = [System.Collections.Generic.List[string]]::new()
    }

    try {
        $audit.Site = Get-Website -Name $SiteName -ErrorAction Stop
        $audit.SiteExists = $true
    }
    catch {
        if ($_.Exception.Message -notmatch '(?i)not found|cannot find') {
            [void] $audit.Issues.Add("IIS site lookup failed: $($_.Exception.Message)")
        }
    }

    try {
        $audit.Pool = Get-Item "IIS:\AppPools\$PoolName" -ErrorAction Stop
        $audit.PoolExists = $true
    }
    catch {
        if ($_.Exception.Message -notmatch '(?i)not found|cannot find') {
            [void] $audit.Issues.Add("IIS app-pool lookup failed: $($_.Exception.Message)")
        }
    }
    if (-not $audit.PoolExists) {
        [void] $audit.Issues.Add("IIS app pool '$PoolName' does not exist.")
    }

    if ($audit.SiteExists) {
        $sitePath = [Environment]::ExpandEnvironmentVariables([string] $audit.Site.PhysicalPath)
        $audit.SitePathMatches = Test-SamePath -Left $sitePath -Right $PhysicalPath
        if (-not $audit.SitePathMatches) {
            [void] $audit.Issues.Add("IIS site physical path is '$sitePath', expected '$PhysicalPath'.")
        }
        $audit.SitePoolMatches = ([string] $audit.Site.ApplicationPool).Equals($PoolName, [StringComparison]::OrdinalIgnoreCase)
        if (-not $audit.SitePoolMatches) {
            [void] $audit.Issues.Add("IIS site app pool is '$($audit.Site.ApplicationPool)', expected '$PoolName'.")
        }

        try {
            $bindings = @(Get-WebBinding -Name $SiteName -Protocol https -ErrorAction Stop)
            foreach ($binding in $bindings) {
                $parts = ([string] $binding.bindingInformation) -split ':', 3
                if ($parts.Count -ne 3 -or [int] $parts[1] -ne $Readiness.Port) {
                    continue
                }

                $bindingHost = [string] $parts[2]
                $hostMatches = [string]::IsNullOrWhiteSpace($bindingHost) -or
                    $bindingHost.Equals($Readiness.Host, [StringComparison]::OrdinalIgnoreCase)
                $certificateHash = $null
                if ($null -ne $binding.CertificateHash) {
                    if ($binding.CertificateHash -is [byte[]]) {
                        $certificateHash = ([BitConverter]::ToString($binding.CertificateHash)).Replace('-', '')
                    }
                    else {
                        $certificateHash = ([string] $binding.CertificateHash).Replace(' ', '')
                    }
                }
                $certificateMatches = -not [string]::IsNullOrWhiteSpace($certificateHash)
                if ($certificateMatches) {
                    $boundCertificate = Get-LocalMachineCertificate `
                        -Thumbprint $certificateHash `
                        -Subject 'CN=ASAP Test HTTPS'
                    $certificateMatches = $null -ne $boundCertificate
                }
                if (-not [string]::IsNullOrWhiteSpace($ExpectedHttpsThumbprint)) {
                    $certificateMatches = $certificateMatches -and
                        $certificateHash.Equals(
                            $ExpectedHttpsThumbprint.Replace(' ', ''),
                            [StringComparison]::OrdinalIgnoreCase)
                }
                if ($hostMatches -and $certificateMatches) {
                    $audit.HttpsBindingMatches = $true
                    break
                }
            }
        }
        catch {
            [void] $audit.Issues.Add("HTTPS binding lookup failed: $($_.Exception.Message)")
        }
        if (-not $audit.HttpsBindingMatches) {
            [void] $audit.Issues.Add('IIS site does not have the expected HTTPS binding and certificate.')
        }
    }

    if ($audit.PoolExists) {
        try {
            $poolProperties = Get-ItemProperty "IIS:\AppPools\$PoolName" -ErrorAction Stop
            $processModel = Get-ObjectProperty $poolProperties 'processModel'
            $identityType = [int] (Get-ObjectProperty $processModel 'identityType')
            $username = [string] (Get-ObjectProperty $processModel 'userName')
            $managedRuntime = [string] (Get-ObjectProperty $poolProperties 'managedRuntimeVersion')
            $pipeline = [string] (Get-ObjectProperty $poolProperties 'managedPipelineMode')
            $startMode = [string] (Get-ObjectProperty $poolProperties 'startMode')
            $identityMatches = $identityType -eq 3 -and
                $username.Equals($ServiceIdentity, [StringComparison]::OrdinalIgnoreCase)
            $audit.PoolConfigurationMatches = $identityMatches -and
                $managedRuntime -eq '' -and
                $pipeline.Equals('Integrated', [StringComparison]::OrdinalIgnoreCase) -and
                $startMode.Equals('AlwaysRunning', [StringComparison]::OrdinalIgnoreCase)
            if (-not $audit.PoolConfigurationMatches) {
                [void] $audit.Issues.Add('IIS app pool is not configured for framework-dependent ASP.NET Core hosting (No Managed Code, Integrated, AlwaysRunning, SpecificUser).')
            }
        }
        catch {
            [void] $audit.Issues.Add("IIS app-pool settings lookup failed: $($_.Exception.Message)")
        }

        try {
            $pointer = Get-IisEnvironmentValue -PoolName $PoolName
            $audit.ConfigPointerMatches = Test-SamePath -Left $pointer -Right $ApplicationConfigPath
            if (-not $audit.ConfigPointerMatches) {
                [void] $audit.Issues.Add("IIS app pool Asap__ConfigFile is '$pointer', expected '$ApplicationConfigPath'.")
            }
        }
        catch {
            [void] $audit.Issues.Add("IIS app-pool environment lookup failed: $($_.Exception.Message)")
        }
    }

    return [pscustomobject] $audit
}

function Set-IisConfigPointer {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PoolName,

        [Parameter(Mandatory = $true)]
        [string] $ConfigPath
    )

    $baseFilter = "system.applicationHost/applicationPools/add[@name='$PoolName']/environmentVariables"
    $entryFilter = "$baseFilter/add[@name='Asap__ConfigFile']"
    $current = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $entryFilter -Name 'value' -ErrorAction SilentlyContinue
    if ($null -eq $current -or [string]::IsNullOrWhiteSpace([string] $current)) {
        Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $baseFilter -Name '.' -Value @{
            name = 'Asap__ConfigFile'
            value = $ConfigPath
        } -ErrorAction Stop
    }
    else {
        Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $entryFilter -Name 'value' -Value $ConfigPath -ErrorAction Stop
    }
}

function Get-AppPoolIdentityProperties {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PoolName
    )

    try {
        $properties = Get-ItemProperty "IIS:\AppPools\$PoolName" -ErrorAction Stop
        $processModel = Get-ObjectProperty $properties 'processModel'
        return [pscustomobject]@{
            Exists = $true
            IdentityType = [int] (Get-ObjectProperty $processModel 'identityType')
            UserName = [string] (Get-ObjectProperty $processModel 'userName')
        }
    }
    catch {
        return [pscustomobject]@{ Exists = $false; IdentityType = $null; UserName = $null }
    }
}

function Ensure-IisAppPool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PoolName,

        [Parameter(Mandatory = $true)]
        [string] $Identity,

        [AllowNull()]
        [securestring] $Password
    )

    $identityProperties = Get-AppPoolIdentityProperties -PoolName $PoolName
    $created = $false
    if (-not $identityProperties.Exists) {
        New-WebAppPool -Name $PoolName -ErrorAction Stop | Out-Null
        $created = $true
    }

    Set-ItemProperty "IIS:\AppPools\$PoolName" -Name managedRuntimeVersion -Value '' -ErrorAction Stop
    Set-ItemProperty "IIS:\AppPools\$PoolName" -Name managedPipelineMode -Value 'Integrated' -ErrorAction Stop
    Set-ItemProperty "IIS:\AppPools\$PoolName" -Name startMode -Value 'AlwaysRunning' -ErrorAction Stop
    Set-ItemProperty "IIS:\AppPools\$PoolName" -Name processModel.identityType -Value 3 -ErrorAction Stop
    Set-ItemProperty "IIS:\AppPools\$PoolName" -Name processModel.userName -Value $Identity -ErrorAction Stop

    $isGmsa = $Identity.TrimEnd().EndsWith('$', [StringComparison]::Ordinal)
    $identityMatches = $identityProperties.Exists -and
        $identityProperties.IdentityType -eq 3 -and
        ([string] $identityProperties.UserName).Equals($Identity, [StringComparison]::OrdinalIgnoreCase)
    if (-not $isGmsa -and ($created -or -not $identityMatches)) {
        $effectivePassword = $Password
        if ($null -eq $effectivePassword) {
            $effectivePassword = Read-Host "Password for IIS app-pool identity $Identity" -AsSecureString
        }
        $plainPassword = Get-PlainTextSecureString -Value $effectivePassword
        try {
            Set-ItemProperty "IIS:\AppPools\$PoolName" -Name processModel.password -Value $plainPassword -ErrorAction Stop
        }
        finally {
            $plainPassword = $null
        }
    }

    Set-IisConfigPointer -PoolName $PoolName -ConfigPath $ApplicationConfigPath
}

function Ensure-IisSite {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Audit,

        [Parameter(Mandatory = $true)]
        [string] $SiteName,

        [Parameter(Mandatory = $true)]
        [string] $PoolName,

        [Parameter(Mandatory = $true)]
        [string] $PhysicalPath,

        [Parameter(Mandatory = $true)]
        [Uri] $Readiness,

        [AllowNull()]
        [string] $CertificateThumbprint
    )

    if ($Audit.SiteExists) {
        if (-not $Audit.SitePathMatches -or -not $Audit.SitePoolMatches -or -not $Audit.HttpsBindingMatches) {
            throw 'The existing IIS site failed physical-path, app-pool or HTTPS binding validation and was not repointed.'
        }
        return
    }

    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        throw 'The IIS site is missing; supply -HttpsCertificateThumbprint before creating it.'
    }

    $certificate = Get-LocalMachineCertificate -Thumbprint $CertificateThumbprint -Subject 'CN=ASAP Test HTTPS'
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
        throw 'The supplied HTTPS certificate thumbprint was not found with a private key in LocalMachine\\My.'
    }

    New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $PoolName -Port $Readiness.Port -HostHeader $Readiness.Host -Ssl -Force -ErrorAction Stop | Out-Null
    $binding = @(Get-WebBinding -Name $SiteName -Protocol https -ErrorAction Stop |
        Where-Object { ([string] $_.bindingInformation) -match ':443:' } |
        Select-Object -First 1)
    if ($binding.Count -ne 1) {
        throw 'The new IIS site did not expose an HTTPS port 443 binding.'
    }

    $binding[0].AddSslCertificate($CertificateThumbprint.Replace(' ', ''), 'My')
}

function Test-ReadinessEndpoint {
    param(
        [Parameter(Mandatory = $true)]
        [Uri] $Uri
    )

    try {
        $response = Invoke-WebRequest -Uri $Uri.AbsoluteUri -Method Get -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
        $payload = $response.Content | ConvertFrom-Json -Depth 5
        if ($response.StatusCode -eq 200 -and [string] $payload.status -eq 'healthy') {
            return [pscustomobject]@{ Success = $true; Detail = 'HTTPS /health/ready returned HTTP 200 with status healthy.' }
        }

        return [pscustomobject]@{ Success = $false; Detail = "Readiness returned HTTP $($response.StatusCode) without status healthy." }
    }
    catch {
        return [pscustomobject]@{ Success = $false; Detail = "Readiness probe failed: $($_.Exception.Message)" }
    }
}

function Test-HostingBundle {
    $modulePaths = @()
    foreach ($programFilesRoot in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not [string]::IsNullOrWhiteSpace($programFilesRoot)) {
            $modulePaths += Join-Path $programFilesRoot 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
        }
    }
    $fileFound = $modulePaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    $registryFound = @(
        'HKLM:\SOFTWARE\Microsoft\IIS Extensions\IIS AspNetCore Module V2',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\IIS Extensions\IIS AspNetCore Module V2'
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    $registeredModule = $false
    try {
        $registeredModule = $null -ne (Get-WebGlobalModule -Name 'AspNetCoreModuleV2' -ErrorAction Stop)
    }
    catch {
        $registeredModule = $false
    }

    if ($registeredModule -or ($null -ne $fileFound -and $null -ne $registryFound)) {
        return [pscustomobject]@{ Success = $true; Detail = 'ASP.NET Core Module V2 / IIS Hosting Bundle support is present.' }
    }

    return [pscustomobject]@{ Success = $false; Detail = 'ASP.NET Core Module V2 was not found; install the .NET 10 IIS Hosting Bundle outside this script.' }
}

function Test-DotNet10Runtime {
    $dotnet = Resolve-ExecutablePath -ConfiguredPath 'dotnet.exe' -Name 'dotnet'
    if ($null -eq $dotnet) {
        return [pscustomobject]@{ Success = $false; Detail = 'dotnet.exe was not found on PATH.' }
    }

    try {
        $runtimeLines = @(& $dotnet --list-runtimes 2>&1)
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{ Success = $false; Detail = 'dotnet --list-runtimes failed.' }
        }

        $aspNet = $runtimeLines | Where-Object { [string] $_ -match '^Microsoft\.AspNetCore\.App\s+10\.' } | Select-Object -First 1
        if ($null -eq $aspNet) {
            return [pscustomobject]@{ Success = $false; Detail = '.NET 10 Microsoft.AspNetCore.App runtime is missing.' }
        }

        return [pscustomobject]@{ Success = $true; Detail = "Found $aspNet" }
    }
    catch {
        return [pscustomobject]@{ Success = $false; Detail = "Unable to inspect .NET runtimes: $($_.Exception.Message)" }
    }
}

function Write-Summary {
    param(
        [Parameter(Mandatory = $true)]
        [bool] $ValidationOnly
    )

    Write-Host ''
    Write-Host "ASAP test-IIS host bootstrap summary $(if ($ValidationOnly) { '(ValidateOnly)' } else { '' })"
    foreach ($result in $Results) {
        Write-Host "[$($result.Status)] $($result.Name): $($result.Detail)"
    }

    $attention = @($Results | Where-Object { $_.Status -eq 'NEEDS ATTENTION' })
    if ($attention.Count -eq 0) {
        Write-Host 'Overall status: PASS'
        return $false
    }

    Write-Host "Overall status: NEEDS ATTENTION ($($attention.Count) item(s))"
    return $true
}

$readiness = $null
try {
    $readiness = [Uri]::new($ReadinessUrl, [UriKind]::Absolute)
    if ($readiness.Scheme -ne 'https' -or
        $readiness.AbsolutePath.TrimEnd('/') -ne '/health/ready' -or
        [string]::IsNullOrWhiteSpace($readiness.Host)) {
        Add-Check -Status 'NEEDS ATTENTION' -Name 'Readiness URL' -Detail 'ReadinessUrl must be an HTTPS URL ending in /health/ready.' -Blocking
        $readiness = $null
    }
    else {
        Add-Check -Status 'PASS' -Name 'Readiness URL' -Detail $readiness.AbsoluteUri
    }
}
catch {
    Add-Check -Status 'NEEDS ATTENTION' -Name 'Readiness URL' -Detail 'ReadinessUrl is not an absolute URL.' -Blocking
}

if ($SqlServer.Contains(';', [StringComparison]::Ordinal) -or
    $SqlServer.Contains("`r", [StringComparison]::Ordinal) -or
    $SqlServer.Contains("`n", [StringComparison]::Ordinal)) {
    Add-Check -Status 'NEEDS ATTENTION' -Name 'SQL server input' -Detail 'SqlServer contains connection-string delimiter or newline characters.' -Blocking
}
else {
    Add-Check -Status 'PASS' -Name 'SQL contract' -Detail "Windows Integrated Security, Encrypt=True, Database=$DatabaseName, TrustServerCertificate=$TrustServerCertificate"
}

$asapConnectionString = New-SqlConnectionString
$hangfireConnectionString = New-SqlConnectionString
$runnerServices = @(Get-RunnerServiceInventory)
$selectedRunner = $null
if ($runnerServices.Count -eq 0) {
    Add-Check -Status 'NEEDS ATTENTION' -Name 'GitHub Actions runner service' -Detail 'No installed Windows service matching actions.runner.* was found.' -Blocking
}
elseif (-not [string]::IsNullOrWhiteSpace($RunnerServiceName)) {
    $selectedRunner = $runnerServices | Where-Object {
        ([string] $_.Name).Equals($RunnerServiceName, [StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1
    if ($null -eq $selectedRunner) {
        Add-Check -Status 'NEEDS ATTENTION' -Name 'GitHub Actions runner service' -Detail "Runner service '$RunnerServiceName' was not found among actions.runner.* services." -Blocking
    }
    else {
        Add-Check -Status 'PASS' -Name 'GitHub Actions runner service' -Detail "Selected $($selectedRunner.Name)."
    }
}
elseif ($runnerServices.Count -gt 1) {
    Add-Check -Status 'NEEDS ATTENTION' -Name 'GitHub Actions runner service' -Detail "Found $($runnerServices.Count) matching services; supply -RunnerServiceName explicitly." -Blocking
}
else {
    $selectedRunner = $runnerServices[0]
    Add-Check -Status 'PASS' -Name 'GitHub Actions runner service' -Detail "Selected $($selectedRunner.Name) automatically."
}

$selectedIdentity = $null
$selectedIdentityCandidate = $null
if ($null -ne $selectedRunner) {
    $runnerIdentity = [string] $selectedRunner.StartName
    if ([string]::IsNullOrWhiteSpace($runnerIdentity)) {
        Add-Check -Status 'NEEDS ATTENTION' -Name 'Dedicated service identity' -Detail "Runner service '$($selectedRunner.Name)' does not report a Windows service identity." -Blocking
    }
    elseif (Test-BuiltInServiceIdentity -Identity $runnerIdentity) {
        Add-Check -Status 'NEEDS ATTENTION' -Name 'Dedicated service identity' -Detail "Runner service '$($selectedRunner.Name)' uses built-in identity '$runnerIdentity'. Use a dedicated domain account or gMSA." -Blocking
    }
    else {
        $selectedIdentityCandidate = if ([string]::IsNullOrWhiteSpace($ServiceAccount)) { $runnerIdentity } else { $ServiceAccount }
        if (-not [string]::IsNullOrWhiteSpace($ServiceAccount) -and
            -not $runnerIdentity.Equals($ServiceAccount, [StringComparison]::OrdinalIgnoreCase)) {
            Add-Check -Status 'INFO' -Name 'Service identity override' -Detail "Using explicitly supplied '$ServiceAccount' instead of runner service StartName '$runnerIdentity'; the override must be the same effective identity used by the runner."
        }

        if ([string]::IsNullOrWhiteSpace($selectedIdentityCandidate)) {
            Add-Check -Status 'NEEDS ATTENTION' -Name 'Dedicated service identity' -Detail 'The selected service identity is empty.' -Blocking
        }
        elseif (Test-BuiltInServiceIdentity -Identity $selectedIdentityCandidate) {
            Add-Check -Status 'NEEDS ATTENTION' -Name 'Dedicated service identity' -Detail "'$selectedIdentityCandidate' is a built-in Windows service identity. Use a dedicated domain account or gMSA." -Blocking
        }
        elseif ($null -eq (Resolve-IdentitySid -Identity $selectedIdentityCandidate)) {
            Add-Check -Status 'NEEDS ATTENTION' -Name 'Dedicated service identity' -Detail "Windows identity '$selectedIdentityCandidate' could not be resolved." -Blocking
        }
        else {
            $selectedIdentity = $selectedIdentityCandidate
            Add-Check -Status 'PASS' -Name 'Dedicated service identity' -Detail "Filesystem, IIS and certificate permissions will use '$selectedIdentity'."
        }
    }
}

if ($PSVersionTable.PSVersion.Major -ge 7) {
    Add-Check -Status 'PASS' -Name 'PowerShell 7' -Detail "Running PowerShell $($PSVersionTable.PSVersion)."
}
else {
    Add-Check -Status 'NEEDS ATTENTION' -Name 'PowerShell 7' -Detail 'Run this script with pwsh (PowerShell 7 or later).' -Blocking
}

$resolvedSqlPackagePath = Resolve-ExecutablePath -ConfiguredPath $SqlPackagePath -Name 'SqlPackage'
if ($null -eq $resolvedSqlPackagePath) {
    Add-Check -Status 'NEEDS ATTENTION' -Name 'SqlPackage' -Detail "SqlPackage '$SqlPackagePath' was not found." -Blocking
}
else {
    Add-Check -Status 'PASS' -Name 'SqlPackage' -Detail $resolvedSqlPackagePath
}

$resolvedSqlCmdPath = Resolve-ExecutablePath -ConfiguredPath $SqlCmdPath -Name 'sqlcmd'
if ($null -eq $resolvedSqlCmdPath) {
    Add-Check -Status 'NEEDS ATTENTION' -Name 'sqlcmd' -Detail "sqlcmd '$SqlCmdPath' was not found." -Blocking
}
else {
    Add-Check -Status 'PASS' -Name 'sqlcmd' -Detail $resolvedSqlCmdPath
}

$dotnetRuntime = Test-DotNet10Runtime
Add-Check -Status $(if ($dotnetRuntime.Success) { 'PASS' } else { 'NEEDS ATTENTION' }) -Name '.NET 10 ASP.NET Core runtime' -Detail $dotnetRuntime.Detail -Blocking:(!$dotnetRuntime.Success)

$hostingBundle = Test-HostingBundle
Add-Check -Status $(if ($hostingBundle.Success) { 'PASS' } else { 'NEEDS ATTENTION' }) -Name 'ASP.NET Core Module V2 / IIS Hosting Bundle' -Detail $hostingBundle.Detail -Blocking:(!$hostingBundle.Success)

$webAdministrationAvailable = $false
try {
    Import-Module WebAdministration -ErrorAction Stop
    $webAdministrationAvailable = $true
    Add-Check -Status 'PASS' -Name 'IIS WebAdministration' -Detail 'IIS administration cmdlets are available.'
}
catch {
    Add-Check -Status 'NEEDS ATTENTION' -Name 'IIS WebAdministration' -Detail "WebAdministration could not be loaded: $($_.Exception.Message)" -Blocking
}

$expectedDeploymentConfiguration = [ordered]@{
    IisSiteName = $IisSiteName
    IisAppPoolName = $IisAppPoolName
    DeploymentPath = $CanonicalPaths.Web
    StagingRoot = $CanonicalPaths.Staging
    BackupRoot = $CanonicalPaths.Backups
    ExternalApplicationConfigPath = $ApplicationConfigPath
    ReadinessUrl = if ($null -ne $readiness) { $readiness.AbsoluteUri } else { $ReadinessUrl }
    AsapDatabaseConnectionString = $asapConnectionString
    HangfireDatabaseConnectionString = $hangfireConnectionString
    SqlPackagePath = if ($null -ne $resolvedSqlPackagePath) { $resolvedSqlPackagePath } else { $SqlPackagePath }
    SqlCmdPath = if ($null -ne $resolvedSqlCmdPath) { $resolvedSqlCmdPath } else { $SqlCmdPath }
}

$entraClientSecretPlainText = $null
if ($null -ne $EntraClientSecret) {
    $entraClientSecretPlainText = Get-PlainTextSecureString -Value $EntraClientSecret
}
$entraInputs = [pscustomobject]@{
    ClientId = $EntraClientId
    TenantId = $EntraTenantId
    AllowedTenantIds = if ($null -ne $AllowedTenantIds) {
        @($AllowedTenantIds)
    }
    elseif (-not [string]::IsNullOrWhiteSpace($EntraTenantId)) {
        @($EntraTenantId)
    }
    else {
        $null
    }
    ObjectId = $EntraObjectId
    AdminUpn = $EntraAdminUpn
    AdminDisplayName = $EntraAdminDisplayName
    NotificationEmail = $EntraNotificationEmail
    ClientSecretPlainText = $entraClientSecretPlainText
    AllowedRecipientDomains = $EffectiveAllowedRecipientDomains
    AllowedRecipientDomainsWasSupplied = $AllowedRecipientDomainsWasSupplied
}
$entraValues = Get-EntraBootstrapValues

$certificateSubject = 'CN=ASAP Test Data Protection'
$certificateLookupThumbprint = $DataProtectionCertificateThumbprint
if ([string]::IsNullOrWhiteSpace($certificateLookupThumbprint) -and
    (Test-Path -LiteralPath $ApplicationConfigPath -PathType Leaf)) {
    $existingApplicationConfiguration = Read-JsonFile -Path $ApplicationConfigPath
    $existingApplicationSection = Get-ObjectProperty -Object $existingApplicationConfiguration -Name 'Application'
    $configuredCertificate = [string] (Get-ObjectProperty `
        -Object $existingApplicationSection `
        -Name 'DataProtectionKeyEncryptionCertificateThumbprint')
    if (-not (Test-Placeholder -Value $configuredCertificate)) {
        $certificateLookupThumbprint = $configuredCertificate
    }
}
$dataProtectionCertificate = Get-LocalMachineCertificate -Thumbprint $certificateLookupThumbprint -Subject $certificateSubject
if ($null -eq $dataProtectionCertificate) {
    if ($ValidateOnly) {
        Add-Check -Status 'NEEDS ATTENTION' -Name 'Data Protection certificate' -Detail 'No valid LocalMachine certificate with private key was found for the test Data Protection ring.' -Blocking
    }
    elseif ($null -eq $selectedIdentity) {
        Add-Check -Status 'NEEDS ATTENTION' -Name 'Data Protection certificate' -Detail 'A valid dedicated service identity is required before creating the Data Protection certificate.' -Blocking
    }
    else {
        try {
            $dataProtectionCertificate = New-SelfSignedCertificate `
                -Type Custom `
                -Subject $certificateSubject `
                -KeyAlgorithm RSA `
                -KeyLength 2048 `
                -KeyExportPolicy NonExportable `
                -KeyUsage KeyEncipherment, DataEncipherment `
                -CertStoreLocation 'Cert:\LocalMachine\My' `
                -NotAfter (Get-Date).AddYears(5) `
                -ErrorAction Stop
            Add-Check -Status 'INFO' -Name 'Data Protection certificate' -Detail "Created and stored a LocalMachine certificate with thumbprint $($dataProtectionCertificate.Thumbprint)."
        }
        catch {
            Add-Check -Status 'NEEDS ATTENTION' -Name 'Data Protection certificate' -Detail "Could not create the LocalMachine certificate: $($_.Exception.Message)" -Blocking
        }
    }
}
else {
    Add-Check -Status 'PASS' -Name 'Data Protection certificate' -Detail "Using LocalMachine certificate $($dataProtectionCertificate.Thumbprint)."
}

$certificateThumbprint = if ($null -ne $dataProtectionCertificate) {
    ([string] $dataProtectionCertificate.Thumbprint).Replace(' ', '').ToUpperInvariant()
}
else {
    'REPLACE-SET-DATA-PROTECTION-CERTIFICATE'
}
$expectedApplicationConfiguration = New-ApplicationConfiguration `
    -EntraValues $entraValues `
    -AsapConnectionString $asapConnectionString `
    -HangfireConnectionString $hangfireConnectionString `
    -CertificateThumbprint $certificateThumbprint

if ($null -ne $dataProtectionCertificate) {
    $probe = Test-CertificatePrivateKeyProbe -Certificate $dataProtectionCertificate
    Add-Check -Status $(if ($probe.Success) { 'PASS' } else { 'NEEDS ATTENTION' }) -Name 'Data Protection private-key probe' -Detail $probe.Detail -Blocking:(!$probe.Success)
    $privateKeyPath = Get-PrivateKeyFilePath -Certificate $dataProtectionCertificate
    if ($null -eq $privateKeyPath -or -not (Test-Path -LiteralPath $privateKeyPath -PathType Leaf)) {
        Add-Check -Status 'NEEDS ATTENTION' -Name 'Data Protection private-key ACL' -Detail 'The certificate private-key file could not be located for selected service-identity ACL validation.' -Blocking
    }
    elseif ($null -ne $selectedIdentity -and (Test-PrivateKeyReadRule -Path $privateKeyPath -Identity $selectedIdentity)) {
        Add-Check -Status 'PASS' -Name 'Data Protection private-key ACL' -Detail "Selected service identity '$selectedIdentity' has read access to the private key."
    }
    elseif ($null -ne $selectedIdentity -and $ValidateOnly) {
        Add-Check -Status 'NEEDS ATTENTION' -Name 'Data Protection private-key ACL' -Detail "Selected service identity '$selectedIdentity' does not have read access to the private key." -Blocking
    }
    elseif ($null -ne $selectedIdentity) {
        Add-Check -Status 'INFO' -Name 'Data Protection private-key ACL' -Detail "Selected service identity '$selectedIdentity' will receive read access to the private key during normal bootstrap."
    }
}

foreach ($directory in $CanonicalPaths.GetEnumerator()) {
    if (Test-Path -LiteralPath $directory.Value -PathType Container) {
        Add-Check -Status 'PASS' -Name "Directory $($directory.Key)" -Detail $directory.Value
    }
    elseif ($ValidateOnly) {
        Add-Check -Status 'NEEDS ATTENTION' -Name "Directory $($directory.Key)" -Detail "Missing canonical directory $($directory.Value)." -Blocking
    }
}

if (-not $ValidateOnly -and $null -ne $selectedIdentity) {
    try {
        foreach ($directory in $CanonicalPaths.Values) {
            New-Item -ItemType Directory -Force -Path $directory | Out-Null
        }
        Add-Check -Status 'PASS' -Name 'Canonical directories' -Detail "Ensured $CanonicalRoot, web, staging, backups, config, keys and logs exist."
    }
    catch {
        Add-Check -Status 'NEEDS ATTENTION' -Name 'Canonical directories' -Detail "Could not create the canonical host tree: $($_.Exception.Message)" -Blocking
    }
}

if ($null -ne $selectedIdentity -and (Test-Path -LiteralPath $CanonicalRoot -PathType Container)) {
    if ($ValidateOnly) {
        $aclIssues = @(Test-CanonicalAsapAcl -RootPath $CanonicalRoot -ServiceIdentity $selectedIdentity)
        if ($aclIssues.Count -eq 0) {
            Add-Check -Status 'PASS' -Name 'ASAP filesystem ACL inheritance' -Detail 'Root access is restricted and all managed child ACLs inherit the canonical contract.'
        }
        else {
            foreach ($issue in $aclIssues) {
                Add-Check -Status 'NEEDS ATTENTION' -Name 'ASAP filesystem ACL inheritance' -Detail $issue -Blocking
            }
        }
    }
    else {
        try {
            Set-CanonicalRootAcl -RootPath $CanonicalRoot -ServiceIdentity $selectedIdentity
            foreach ($child in @(Get-ChildItem -LiteralPath $CanonicalRoot -Recurse -Force -ErrorAction Stop)) {
                Set-ManagedChildAclInheritance -Path $child.FullName
            }
            $aclIssues = @(Test-CanonicalAsapAcl -RootPath $CanonicalRoot -ServiceIdentity $selectedIdentity)
            if ($aclIssues.Count -eq 0) {
                Add-Check -Status 'PASS' -Name 'ASAP filesystem ACL inheritance' -Detail 'Applied and audited canonical SYSTEM/Administrators/service-identity inheritance.'
            }
            else {
                foreach ($issue in $aclIssues) {
                    Add-Check -Status 'NEEDS ATTENTION' -Name 'ASAP filesystem ACL inheritance' -Detail $issue -Blocking
                }
            }
        }
        catch {
            Add-Check -Status 'NEEDS ATTENTION' -Name 'ASAP filesystem ACL inheritance' -Detail "Could not apply or audit the canonical ACL: $($_.Exception.Message)" -Blocking
        }
    }
}

if ($null -ne $selectedIdentity) {
    foreach ($groupName in @('IIS_IUSRS', 'Administrators')) {
        $groupStatus = Get-LocalGroupMembershipStatus -GroupName $groupName -Identity $selectedIdentity
        if (-not $groupStatus.Exists) {
            Add-Check -Status 'NEEDS ATTENTION' -Name "${groupName} membership" -Detail "Could not inspect ${groupName}: $($groupStatus.Detail)" -Blocking
        }
        elseif ($groupStatus.Member) {
            Add-Check -Status 'PASS' -Name "${groupName} membership" -Detail "'$selectedIdentity' is a member of $groupName."
        }
        elseif ($ValidateOnly) {
            Add-Check -Status 'NEEDS ATTENTION' -Name "${groupName} membership" -Detail "'$selectedIdentity' is not a member of $groupName." -Blocking
        }
        else {
            try {
                [void] (Ensure-LocalGroupMembership -GroupName $groupName -Identity $selectedIdentity)
                Add-Check -Status 'PASS' -Name "${groupName} membership" -Detail "Added '$selectedIdentity' to $groupName."
            }
            catch {
                Add-Check -Status 'NEEDS ATTENTION' -Name "${groupName} membership" -Detail "Could not add '$selectedIdentity' to ${groupName}: $($_.Exception.Message)" -Blocking
            }
        }
    }
    Add-Check -Status 'INFO' -Name 'IIS lifecycle authorization' -Detail "The reduced host contract uses local Administrators for '$selectedIdentity'; the runner service must still be included in the first controlled stop/start verification."
}

if ($null -ne $dataProtectionCertificate -and $null -ne $selectedIdentity) {
    $privateKeyPath = Get-PrivateKeyFilePath -Certificate $dataProtectionCertificate
    if ($null -ne $privateKeyPath -and (Test-Path -LiteralPath $privateKeyPath -PathType Leaf) -and -not $ValidateOnly) {
        try {
            if (-not (Test-PrivateKeyReadRule -Path $privateKeyPath -Identity $selectedIdentity)) {
                Grant-PrivateKeyReadRule -Path $privateKeyPath -Identity $selectedIdentity
            }
            if (Test-PrivateKeyReadRule -Path $privateKeyPath -Identity $selectedIdentity) {
                Add-Check -Status 'PASS' -Name 'Data Protection private-key ACL repair' -Detail "Verified read access for '$selectedIdentity'."
            }
            else {
                Add-Check -Status 'NEEDS ATTENTION' -Name 'Data Protection private-key ACL repair' -Detail "Read access for '$selectedIdentity' could not be verified after repair." -Blocking
            }
        }
        catch {
            Add-Check -Status 'NEEDS ATTENTION' -Name 'Data Protection private-key ACL repair' -Detail "Could not grant private-key read access: $($_.Exception.Message)" -Blocking
        }
    }
}

if ($ValidateOnly) {
    if (Test-Path -LiteralPath $DeploymentConfigPath -PathType Leaf) {
        $deploymentIssues = @(Test-DeploymentConfigurationFile -Path $DeploymentConfigPath -Expected $expectedDeploymentConfiguration)
        if ($deploymentIssues.Count -eq 0) {
            Add-Check -Status 'PASS' -Name 'Deployment configuration' -Detail "Effective values match $DeploymentConfigPath."
        }
        else {
            foreach ($issue in $deploymentIssues) {
                Add-Check -Status 'NEEDS ATTENTION' -Name 'Deployment configuration' -Detail $issue -Blocking
            }
        }
    }
    else {
        Add-Check -Status 'NEEDS ATTENTION' -Name 'Deployment configuration' -Detail "Missing $DeploymentConfigPath." -Blocking
    }
}
else {
    try {
        Write-JsonAtomically -Path $DeploymentConfigPath -Value $expectedDeploymentConfiguration
        Add-Check -Status 'PASS' -Name 'Deployment configuration' -Detail "Wrote canonical deployment settings to $DeploymentConfigPath."
    }
    catch {
        Add-Check -Status 'NEEDS ATTENTION' -Name 'Deployment configuration' -Detail "Could not write ${DeploymentConfigPath}: $($_.Exception.Message)" -Blocking
    }
}

$applicationExists = Test-Path -LiteralPath $ApplicationConfigPath -PathType Leaf
if (-not $applicationExists -or $Force) {
    if ($ValidateOnly) {
        if (-not $applicationExists) {
            Add-Check -Status 'NEEDS ATTENTION' -Name 'External application configuration' -Detail "Missing $ApplicationConfigPath." -Blocking
        }
        else {
            Add-Check -Status 'INFO' -Name 'External application configuration' -Detail '-Force was ignored because -ValidateOnly never rewrites operator configuration.'
        }
    }
    else {
        try {
            Write-JsonAtomically -Path $ApplicationConfigPath -Value $expectedApplicationConfiguration
            Add-Check -Status 'PASS' -Name 'External application configuration' -Detail "$(if ($Force -and $applicationExists) { 'Regenerated' } else { 'Created' }) $ApplicationConfigPath."
        }
        catch {
            Add-Check -Status 'NEEDS ATTENTION' -Name 'External application configuration' -Detail "Could not write ${ApplicationConfigPath}: $($_.Exception.Message)" -Blocking
        }
    }
}
else {
    Add-Check -Status 'INFO' -Name 'External application configuration' -Detail "Preserved existing operator-edited $ApplicationConfigPath."
}

if (Test-Path -LiteralPath $ApplicationConfigPath -PathType Leaf) {
    $applicationIssues = @(Test-ExternalApplicationConfiguration `
        -Path $ApplicationConfigPath `
        -Expected $expectedApplicationConfiguration `
        -EntraInputs $entraInputs `
        -CertificateThumbprint $certificateThumbprint)
    if ($applicationIssues.Count -eq 0) {
        Add-Check -Status 'PASS' -Name 'External configuration validation' -Detail 'Configuration matches the ASP.NET model, canonical schedules/queues, encrypted SQL contract, and Data Protection paths.'
    }
    else {
        foreach ($issue in $applicationIssues) {
            Add-Check -Status 'NEEDS ATTENTION' -Name 'External configuration validation' -Detail $issue -Blocking
        }
    }
}

$iisAudit = $null
if ($webAdministrationAvailable -and $null -ne $readiness -and $null -ne $selectedIdentity) {
    $iisAudit = Get-IisConfigurationAudit `
        -SiteName $IisSiteName `
        -PoolName $IisAppPoolName `
        -PhysicalPath $CanonicalPaths.Web `
        -ServiceIdentity $selectedIdentity `
        -Readiness $readiness `
        -ExpectedHttpsThumbprint $HttpsCertificateThumbprint
    $siteImmutableConfigurationMatches = $iisAudit.SiteExists -and
        $iisAudit.SitePathMatches -and
        $iisAudit.SitePoolMatches -and
        $iisAudit.HttpsBindingMatches

    if ($ValidateOnly) {
        if ($iisAudit.SiteExists -and $iisAudit.Issues.Count -eq 0) {
            Add-Check -Status 'PASS' -Name 'IIS site and app pool' -Detail "Validated site '$IisSiteName', physical path, HTTPS binding, app pool '$IisAppPoolName' and Asap__ConfigFile."
        }
        else {
            if (-not $iisAudit.SiteExists) {
                Add-Check -Status 'NEEDS ATTENTION' -Name 'IIS site and app pool' -Detail "IIS site '$IisSiteName' does not exist." -Blocking
            }
            foreach ($issue in $iisAudit.Issues) {
                Add-Check -Status 'NEEDS ATTENTION' -Name 'IIS site and app pool' -Detail $issue -Blocking
            }
        }
    }
    elseif ($iisAudit.SiteExists -and -not $siteImmutableConfigurationMatches) {
        if ($iisAudit.Issues.Count -eq 0) {
            Add-Check -Status 'NEEDS ATTENTION' -Name 'IIS site and app pool' -Detail 'The existing IIS site failed immutable path, app-pool or HTTPS binding validation and was not repointed.' -Blocking
        }
        else {
            foreach ($issue in $iisAudit.Issues) {
                Add-Check -Status 'NEEDS ATTENTION' -Name 'IIS site and app pool' -Detail $issue -Blocking
            }
        }
    }
    else {
        try {
            Ensure-IisAppPool -PoolName $IisAppPoolName -Identity $selectedIdentity -Password $ServiceAccountPassword
            Ensure-IisSite `
                -Audit $iisAudit `
                -SiteName $IisSiteName `
                -PoolName $IisAppPoolName `
                -PhysicalPath $CanonicalPaths.Web `
                -Readiness $readiness `
                -CertificateThumbprint $HttpsCertificateThumbprint
            $iisAudit = Get-IisConfigurationAudit `
                -SiteName $IisSiteName `
                -PoolName $IisAppPoolName `
                -PhysicalPath $CanonicalPaths.Web `
                -ServiceIdentity $selectedIdentity `
                -Readiness $readiness `
                -ExpectedHttpsThumbprint $HttpsCertificateThumbprint
            if ($iisAudit.SiteExists -and $iisAudit.Issues.Count -eq 0) {
                Add-Check -Status 'PASS' -Name 'IIS configuration repair' -Detail 'Configured and audited the app pool, HTTPS site and external-config pointer; an existing site was never repointed.'
            }
            else {
                if (-not $iisAudit.SiteExists) {
                    Add-Check -Status 'NEEDS ATTENTION' -Name 'IIS configuration repair' -Detail "IIS site '$IisSiteName' was not found after configuration." -Blocking
                }
                foreach ($issue in $iisAudit.Issues) {
                    Add-Check -Status 'NEEDS ATTENTION' -Name 'IIS configuration repair' -Detail $issue -Blocking
                }
            }
        }
        catch {
            Add-Check -Status 'NEEDS ATTENTION' -Name 'IIS configuration repair' -Detail $_.Exception.Message -Blocking
        }
    }
}
elseif ($webAdministrationAvailable -and $null -ne $readiness -and $null -eq $selectedIdentity) {
    Add-Check -Status 'NEEDS ATTENTION' -Name 'IIS site and app pool' -Detail 'IIS audit is deferred until a valid dedicated runner/service identity is selected.' -Blocking
}

$identityForSummary = if ([string]::IsNullOrWhiteSpace($selectedIdentity)) { '<unresolved>' } else { $selectedIdentity }
Add-Check -Status 'INFO' -Name 'Deployment state path' -Detail "Successful deployment state remains adjacent at $StatePath."
Add-Check -Status 'INFO' -Name 'SQL authorization boundary' -Detail "No SQL permissions were granted. An operator must authorize '$identityForSummary' for the AsapTest application/Hangfire runtime contract and separately authorize deployment DACPAC/Hangfire schema operations."

if ($null -ne $readiness) {
    $readinessResult = Test-ReadinessEndpoint -Uri $readiness
    Add-Check -Status $(if ($readinessResult.Success) { 'PASS' } else { 'NEEDS ATTENTION' }) -Name 'HTTPS readiness probe' -Detail $readinessResult.Detail -Blocking:(!$readinessResult.Success)
}

$entraClientSecretPlainText = $null
$hasBlocking = Write-Summary -ValidationOnly ([bool] $ValidateOnly)
if ($hasBlocking) {
    exit 1
}

exit 0
