#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $RuntimeIdentity,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $PfxBackupPath,

    [ValidateNotNullOrEmpty()]
    [string] $EnvironmentName = 'Test',

    [ValidateNotNullOrEmpty()]
    [string] $ApplicationConfigPath = 'C:\ProgramData\clc-asap\Config\application.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:BootstrapPlaceholder = 'REPLACE-DATA-PROTECTION-CERTIFICATE-THUMBPRINT'
$script:CertificateStorePath = 'Cert:\LocalMachine\My'
$script:KeyStorageProvider = 'Microsoft Software Key Storage Provider'

function Assert-WindowsAdministrator {
    if (-not $IsWindows) {
        throw 'Data Protection certificate provisioning requires Windows.'
    }

    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Data Protection certificate provisioning requires an elevated administrator session.'
    }
}

function Resolve-RuntimeSid {
    param([string] $Identity)

    try {
        $account = [Security.Principal.NTAccount]::new($Identity)
        $sid = [Security.Principal.SecurityIdentifier] $account.Translate([Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "Runtime identity '$Identity' could not be resolved to a Windows SID: $($_.Exception.Message)"
    }

    $broadPrincipalSids = @(
        'S-1-1-0',
        'S-1-5-11',
        'S-1-5-32-545'
    )
    if ($broadPrincipalSids -contains $sid.Value) {
        throw "Runtime identity '$Identity' resolves to broad principal $($sid.Value); specify the dedicated ASAP runtime identity."
    }

    return $sid
}

function Read-ApplicationConfiguration {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "application.json does not exist at '$Path'. Run Initialize-AsapTestHost.ps1 -Initialize first."
    }

    try {
        $configuration = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "application.json at '$Path' is not valid JSON: $($_.Exception.Message)"
    }

    $applicationProperty = $configuration.PSObject.Properties['Application']
    if ($null -eq $applicationProperty -or $null -eq $applicationProperty.Value) {
        throw 'application.json is missing the required Application object.'
    }

    $thumbprintProperty = $applicationProperty.Value.PSObject.Properties['DataProtectionKeyEncryptionCertificateThumbprint']
    if ($null -eq $thumbprintProperty) {
        throw 'application.json is missing Application.DataProtectionKeyEncryptionCertificateThumbprint.'
    }

    $thumbprint = [string] $thumbprintProperty.Value
    if ([string]::IsNullOrWhiteSpace($thumbprint)) {
        throw 'Application.DataProtectionKeyEncryptionCertificateThumbprint must not be blank.'
    }

    return [pscustomobject]@{
        Configuration = $configuration
        Thumbprint = $thumbprint
    }
}

function Get-NormalizedThumbprint {
    param([string] $Thumbprint)

    $normalized = $Thumbprint.Replace(' ', '').ToUpperInvariant()
    if ($normalized -notmatch '^[A-F0-9]{40}$') {
        throw "Configured Data Protection certificate thumbprint '$Thumbprint' is not a 40-character hexadecimal thumbprint."
    }

    return $normalized
}

function Get-ConfiguredCertificate {
    param([string] $Thumbprint)

    $certificatePath = Join-Path $script:CertificateStorePath $Thumbprint
    $certificate = Get-Item -LiteralPath $certificatePath -ErrorAction SilentlyContinue
    if ($null -eq $certificate) {
        throw "Configured Data Protection certificate '$Thumbprint' was not found in LocalMachine\My. The existing application.json value was not changed."
    }
    if (-not $certificate.HasPrivateKey) {
        throw "Configured Data Protection certificate '$Thumbprint' in LocalMachine\My has no private key. The existing application.json value was not changed."
    }

    return $certificate
}

function Get-CngMachineKeyPath {
    param([Security.Cryptography.X509Certificates.X509Certificate2] $Certificate)

    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    if ($null -eq $rsa) {
        throw "Certificate '$($Certificate.Thumbprint)' does not have an RSA private key."
    }

    try {
        if ($rsa -isnot [Security.Cryptography.RSACng]) {
            throw "Certificate '$($Certificate.Thumbprint)' does not use a CNG RSA private key."
        }
        if (-not $rsa.Key.Provider.Provider.Equals($script:KeyStorageProvider, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Certificate '$($Certificate.Thumbprint)' does not use the required '$($script:KeyStorageProvider)' provider."
        }
        if (-not $rsa.Key.IsMachineKey) {
            throw "Certificate '$($Certificate.Thumbprint)' does not use a machine-level private key."
        }

        $keyName = $rsa.Key.UniqueName
    }
    finally {
        $rsa.Dispose()
    }

    $machineKeyRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'Microsoft\Crypto\Keys'
    $keyPath = Join-Path $machineKeyRoot $keyName
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
        throw "The machine private-key file for certificate '$($Certificate.Thumbprint)' was not found at '$keyPath'."
    }

    return $keyPath
}

function Grant-PrivateKeyReadAccess {
    param(
        [string] $KeyPath,
        [Security.Principal.SecurityIdentifier] $Sid
    )

    $acl = Get-Acl -LiteralPath $KeyPath
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $Sid,
        [Security.AccessControl.FileSystemRights]::Read,
        [Security.AccessControl.AccessControlType]::Allow
    )
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $KeyPath -AclObject $acl
}

function Assert-PrivateKeyReadAccess {
    param(
        [string] $KeyPath,
        [Security.Principal.SecurityIdentifier] $Sid
    )

    $acl = Get-Acl -LiteralPath $KeyPath
    $rules = @($acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]))
    $requiredRights = [Security.AccessControl.FileSystemRights]::Read
    $matchingRule = $rules | Where-Object {
        $_.IdentityReference.Value -eq $Sid.Value -and
        $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
        ($_.FileSystemRights -band $requiredRights) -eq $requiredRights
    } | Select-Object -First 1

    if ($null -eq $matchingRule) {
        throw "An explicit private-key read rule for runtime SID '$($Sid.Value)' was not found after ACL update."
    }
}

function Read-ConfirmedPfxPassword {
    $password = $null
    $confirmation = $null
    $passwordPointer = [IntPtr]::Zero
    $confirmationPointer = [IntPtr]::Zero
    $confirmed = $false
    try {
        $password = Read-Host 'Enter a password for the Data Protection recovery PFX' -AsSecureString
        $confirmation = Read-Host 'Confirm the Data Protection recovery PFX password' -AsSecureString
        if ($password.Length -eq 0) {
            throw 'The PFX recovery password must not be empty.'
        }
        if ($password.Length -ne $confirmation.Length) {
            throw 'The PFX recovery passwords did not match.'
        }

        $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
        $confirmationPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirmation)
        if ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer) -cne
            [Runtime.InteropServices.Marshal]::PtrToStringBSTR($confirmationPointer)) {
            throw 'The PFX recovery passwords did not match.'
        }

        $confirmed = $true
        return $password
    }
    finally {
        if ($passwordPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
        }
        if ($confirmationPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($confirmationPointer)
        }
        if ($null -ne $confirmation) {
            $confirmation.Dispose()
        }
        if (-not $confirmed -and $null -ne $password) {
            $password.Dispose()
        }
    }
}

function New-DataProtectionCertificate {
    param([string] $Name)

    $subject = "CN=ASAP $Name Data Protection"
    return New-SelfSignedCertificate `
        -Subject $subject `
        -FriendlyName "ASAP $Name Data Protection" `
        -CertStoreLocation $script:CertificateStorePath `
        -Provider $script:KeyStorageProvider `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable `
        -KeyLocation Machine `
        -KeySpec KeyExchange `
        -KeyUsage DataEncipherment, KeyEncipherment `
        -NotBefore ([DateTime]::UtcNow.AddMinutes(-5)) `
        -NotAfter ([DateTime]::UtcNow.AddYears(5)) `
        -Type Custom
}

function Export-AndVerifyPfx {
    param(
        [Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
        [string] $Path,
        [Security.SecureString] $Password
    )

    $null = Export-PfxCertificate `
        -Cert $Certificate `
        -FilePath $Path `
        -Password $Password `
        -ChainOption EndEntityCertOnly `
        -NoProperties

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -eq 0) {
        throw "The recovery PFX was not created successfully at '$Path'."
    }

    $pfxData = $null
    try {
        $pfxData = Get-PfxData -FilePath $Path -Password $Password
        $matchingCertificate = @($pfxData.EndEntityCertificates) | Where-Object {
            $_.Thumbprint.Equals($Certificate.Thumbprint, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1
        if ($null -eq $matchingCertificate -or -not $matchingCertificate.HasPrivateKey) {
            throw "The recovery PFX at '$Path' does not contain the expected certificate and private key."
        }
    }
    finally {
        if ($null -ne $pfxData) {
            foreach ($backupCertificate in @($pfxData.EndEntityCertificates) + @($pfxData.OtherCertificates)) {
                $backupCertificate.Dispose()
            }
        }
    }
}

function Set-ApplicationCertificateThumbprint {
    param(
        [string] $Path,
        [string] $ExpectedValue,
        [string] $Thumbprint
    )

    $current = Read-ApplicationConfiguration $Path
    if ($current.Thumbprint -cne $ExpectedValue) {
        throw "Application.DataProtectionKeyEncryptionCertificateThumbprint changed after validation; application.json was not overwritten."
    }

    $current.Configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = $Thumbprint
    $temporaryPath = Join-Path (Split-Path -Parent $Path) ('.' + [IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $json = $current.Configuration | ConvertTo-Json -Depth 100
        [IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        [IO.File]::Replace($temporaryPath, $Path, $null)
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

Assert-WindowsAdministrator

if ($EnvironmentName -notmatch '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$') {
    throw 'EnvironmentName must begin with a letter or digit and contain only letters, digits, spaces, periods, underscores, or hyphens.'
}

$runtimeSid = Resolve-RuntimeSid $RuntimeIdentity
$resolvedApplicationConfigPath = [IO.Path]::GetFullPath($ApplicationConfigPath)
$initialConfiguration = Read-ApplicationConfiguration $resolvedApplicationConfigPath
$initialThumbprint = $initialConfiguration.Thumbprint
$isBootstrapPlaceholder = $initialThumbprint -ceq $script:BootstrapPlaceholder

$resolvedPfxBackupPath = [IO.Path]::GetFullPath($PfxBackupPath)
$pfxParent = [IO.Path]::GetDirectoryName($resolvedPfxBackupPath)
if ([string]::IsNullOrWhiteSpace($pfxParent) -or -not (Test-Path -LiteralPath $pfxParent -PathType Container)) {
    throw "The PFX backup parent directory does not exist: '$pfxParent'. Create and secure it before provisioning."
}
if (Test-Path -LiteralPath $resolvedPfxBackupPath) {
    throw "The PFX backup path already exists and will not be overwritten: '$resolvedPfxBackupPath'."
}

$certificate = $null
$createdCertificate = $false
$configurationUpdated = $false

if (-not $isBootstrapPlaceholder) {
    $normalizedThumbprint = Get-NormalizedThumbprint $initialThumbprint
    $certificate = Get-ConfiguredCertificate $normalizedThumbprint
    $null = Get-CngMachineKeyPath $certificate
    Write-Host "Retaining configured Data Protection certificate $normalizedThumbprint."
}

$pfxPassword = Read-ConfirmedPfxPassword
try {
    if ($isBootstrapPlaceholder) {
        $certificate = New-DataProtectionCertificate $EnvironmentName
        $createdCertificate = $true
    }

    $keyPath = Get-CngMachineKeyPath $certificate
    Grant-PrivateKeyReadAccess -KeyPath $keyPath -Sid $runtimeSid
    Assert-PrivateKeyReadAccess -KeyPath $keyPath -Sid $runtimeSid
    Export-AndVerifyPfx -Certificate $certificate -Path $resolvedPfxBackupPath -Password $pfxPassword

    if ($createdCertificate) {
        Set-ApplicationCertificateThumbprint `
            -Path $resolvedApplicationConfigPath `
            -ExpectedValue $script:BootstrapPlaceholder `
            -Thumbprint $certificate.Thumbprint
        $configurationUpdated = $true
    }

    $certificateDisposition = if ($createdCertificate) { 'Created' } else { 'Retained' }
    Write-Host ''
    Write-Host 'ASAP Data Protection certificate provisioning completed.'
    Write-Host "$certificateDisposition certificate: $($certificate.Thumbprint) in LocalMachine\My"
    Write-Host "Private-key read access: $RuntimeIdentity ($($runtimeSid.Value))"
    Write-Host "Recovery PFX: $resolvedPfxBackupPath"
    Write-Host "Application configuration: $resolvedApplicationConfigPath"
    Write-Host 'ASAP startup Protect/Unprotect remains the authoritative runtime access verification.'
}
catch {
    if ([IO.File]::Exists($resolvedPfxBackupPath)) {
        [IO.File]::Delete($resolvedPfxBackupPath)
    }
    if ($createdCertificate -and -not $configurationUpdated -and $null -ne $certificate) {
        $createdCertificatePath = Join-Path $script:CertificateStorePath $certificate.Thumbprint
        Remove-Item -LiteralPath $createdCertificatePath -Force -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    $pfxPassword.Dispose()
}
