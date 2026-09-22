[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Workspace,

    [ValidateSet('Ensure', 'Remove')]
    [string]$Action = 'Ensure',

    [string]$ReviewStatePath,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'ASAP review TLS initialization currently requires Windows because it trusts the localhost certificate in the CurrentUser certificate store.'
}

$Workspace = [System.IO.Path]::GetFullPath($Workspace)
$tlsDirectory = Join-Path $Workspace '.tls'
$pfxPath = Join-Path $tlsDirectory 'localhost.pfx'
$certificatePath = Join-Path $tlsDirectory 'localhost.cer'
$secretPath = Join-Path $tlsDirectory 'tls.json'
$metadataPath = Join-Path $tlsDirectory 'certificate.json'
$managedBy = 'ASAP Docker Review'


function Get-ActiveReviewState {
    param([string]$Path)

    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        $state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        $lifecycleState = if ($state.PSObject.Properties['LifecycleState']) {
            [string]$state.LifecycleState
        }
        else {
            'Ready'
        }

        if ($lifecycleState -in @('Starting', 'Ready', 'Failed')) {
            return [pscustomobject]@{
                LifecycleState = $lifecycleState
                ShortSha = if ($state.PSObject.Properties['ShortSha']) { [string]$state.ShortSha } else { $null }
            }
        }
    }
    catch {
        throw "ASAP review state is unreadable: $Path"
    }

    return $null
}

function Read-JsonFile {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Remove-TrustedCertificate {
    param([string]$Thumbprint)

    if (-not $Thumbprint) {
        return
    }

    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::Root,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $matches = $store.Certificates.Find(
            [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $Thumbprint,
            $false)
        foreach ($match in $matches) {
            $store.Remove($match)
            $match.Dispose()
        }
    }
    finally {
        $store.Dispose()
    }
}

function Test-TrustedCertificate {
    param([Parameter(Mandatory)][string]$Thumbprint)

    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::Root,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $matches = $store.Certificates.Find(
            [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $Thumbprint,
            $false)
        foreach ($match in $matches) {
            $match.Dispose()
        }
        return $matches.Count -gt 0
    }
    finally {
        $store.Dispose()
    }
}

function Add-TrustedCertificate {
    param([Parameter(Mandatory)][byte[]]$CertificateBytes)

    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificateBytes)
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::Root,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $store.Add($certificate)
    }
    finally {
        $store.Dispose()
        $certificate.Dispose()
    }
}

function New-StrongPassword {
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)
    return "$([Convert]::ToHexString($bytes))aA1!"
}

$metadata = Read-JsonFile $metadataPath
$activeReviewState = Get-ActiveReviewState -Path $ReviewStatePath

if ($Action -eq 'Remove') {
    if ($null -ne $activeReviewState -and -not $Force) {
        $revision = if ($activeReviewState.ShortSha) { " for $($activeReviewState.ShortSha)" } else { '' }
        throw "The ASAP review localhost certificate cannot be removed while the recorded environment$revision is '$($activeReviewState.LifecycleState)'. Stop or clean the environment first, or use -Force only if you intentionally want to invalidate HTTPS for the active environment."
    }

    if ($null -ne $metadata -and
        $metadata.PSObject.Properties['ManagedBy'] -and
        [string]$metadata.ManagedBy -eq $managedBy -and
        $metadata.PSObject.Properties['Thumbprint']) {
        Remove-TrustedCertificate -Thumbprint ([string]$metadata.Thumbprint)
    }

    Remove-Item -LiteralPath $tlsDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Removed the ASAP review localhost TLS certificate and local certificate files.'
    exit 0
}

if ($Force -and $null -ne $activeReviewState) {
    $revision = if ($activeReviewState.ShortSha) { " for $($activeReviewState.ShortSha)" } else { '' }
    throw "The ASAP review localhost certificate cannot be forcibly replaced while the recorded environment$revision is '$($activeReviewState.LifecycleState)'. Stop or clean the environment first."
}

if (-not $Force -and
    (Test-Path -LiteralPath $pfxPath) -and
    (Test-Path -LiteralPath $certificatePath) -and
    (Test-Path -LiteralPath $secretPath) -and
    $null -ne $metadata -and
    $metadata.PSObject.Properties['ManagedBy'] -and
    [string]$metadata.ManagedBy -eq $managedBy -and
    $metadata.PSObject.Properties['Thumbprint']) {

    try {
        $secret = Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json
        $password = [string]$secret.Password
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $pfxPath,
            $password,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
        try {
            $metadataMatches = [string]::Equals(
                $certificate.Thumbprint,
                [string]$metadata.Thumbprint,
                [StringComparison]::OrdinalIgnoreCase)
            $currentlyUsable = $certificate.HasPrivateKey -and
                $certificate.NotAfter.ToUniversalTime() -gt [DateTime]::UtcNow -and
                $metadataMatches
            $rotationNeeded = -not $currentlyUsable -or
                $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow.AddDays(30)

            if (-not $rotationNeeded) {
                if (-not (Test-TrustedCertificate -Thumbprint $certificate.Thumbprint)) {
                    Add-TrustedCertificate -CertificateBytes ([System.IO.File]::ReadAllBytes($certificatePath))
                }

                Write-Host "ASAP review localhost TLS certificate is ready: $($certificate.Thumbprint)"
                exit 0
            }

            if ($currentlyUsable -and $null -ne $activeReviewState) {
                if (-not (Test-TrustedCertificate -Thumbprint $certificate.Thumbprint)) {
                    Add-TrustedCertificate -CertificateBytes ([System.IO.File]::ReadAllBytes($certificatePath))
                }

                $revision = if ($activeReviewState.ShortSha) { " for $($activeReviewState.ShortSha)" } else { '' }
                Write-Warning "The ASAP review localhost certificate expires on $($certificate.NotAfter.ToUniversalTime().ToString('u')) but was not rotated because the recorded environment$revision is '$($activeReviewState.LifecycleState)'. Stop or clean the environment, then rerun bootstrap to rotate it."
                exit 0
            }
        }
        finally {
            $certificate.Dispose()
        }
    }
    catch {
        Write-Verbose "Existing review TLS material could not be reused: $($_.Exception.Message)"
    }
}

if ($null -ne $activeReviewState) {
    $revision = if ($activeReviewState.ShortSha) { " for $($activeReviewState.ShortSha)" } else { '' }
    throw "The ASAP review localhost certificate requires replacement, but the recorded environment$revision is '$($activeReviewState.LifecycleState)'. Stop or clean the environment before replacing TLS material."
}

if ($null -ne $metadata -and
    $metadata.PSObject.Properties['ManagedBy'] -and
    [string]$metadata.ManagedBy -eq $managedBy -and
    $metadata.PSObject.Properties['Thumbprint']) {
    Remove-TrustedCertificate -Thumbprint ([string]$metadata.Thumbprint)
}

New-Item -ItemType Directory -Path $tlsDirectory -Force | Out-Null

$password = New-StrongPassword
$rsa = [System.Security.Cryptography.RSA]::Create()
$rsa.KeySize = 2048
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=localhost',
        $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

    $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('localhost')
    $san.AddIpAddress([System.Net.IPAddress]::Parse('127.0.0.1'))
    $san.AddIpAddress([System.Net.IPAddress]::Parse('::1'))
    [void]$request.CertificateExtensions.Add($san.Build())

    $keyUsage = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
    [void]$request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($keyUsage, $true))

    $enhancedKeyUsages = [System.Security.Cryptography.OidCollection]::new()
    [void]$enhancedKeyUsages.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1', 'Server Authentication'))
    [void]$request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($enhancedKeyUsages, $false))

    [void]$request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))

    $certificate = $request.CreateSelfSigned(
        [DateTimeOffset]::UtcNow.AddMinutes(-5),
        [DateTimeOffset]::UtcNow.AddDays(365))
    try {
        [System.IO.File]::WriteAllBytes(
            $pfxPath,
            $certificate.Export(
                [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
                $password))
        [System.IO.File]::WriteAllBytes(
            $certificatePath,
            $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

        @{ Password = $password } |
            ConvertTo-Json |
            Set-Content -LiteralPath $secretPath -Encoding utf8NoBOM

        @{
            ManagedBy = $managedBy
            Thumbprint = $certificate.Thumbprint
            Subject = $certificate.Subject
            NotAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
        } |
            ConvertTo-Json |
            Set-Content -LiteralPath $metadataPath -Encoding utf8NoBOM

        Add-TrustedCertificate -CertificateBytes ([System.IO.File]::ReadAllBytes($certificatePath))

        Write-Host "Created and trusted ASAP review localhost TLS certificate: $($certificate.Thumbprint)"
        Write-Host "  Certificate: $pfxPath"
        Write-Host '  Trust store: CurrentUser\\Root'
    }
    finally {
        $certificate.Dispose()
    }
}
finally {
    $rsa.Dispose()
}
