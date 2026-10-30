# Internal helpers for the disposable WSLC acceptance command.
function New-ReviewArtifactDirectory {
    param(
        [string]$ArtifactsRoot = [System.IO.Path]::Combine([Environment]::GetFolderPath('UserProfile'), 'ASAP-Acceptance'),
        [Parameter(Mandatory)][string]$RunId
    )
    if ([string]::IsNullOrWhiteSpace($ArtifactsRoot) -or -not [System.IO.Path]::IsPathFullyQualified($ArtifactsRoot)) {
        throw '-ArtifactsRoot must be an absolute filesystem path.'
    }
    $destination = $ArtifactsRoot
    try {
        $root = [System.IO.Path]::GetFullPath($ArtifactsRoot)
        $destination = Join-Path $root "$([DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH-mm-ss'))-$RunId"
        if (Test-Path -LiteralPath $destination) { throw 'The run directory already exists.' }
        $null = [System.IO.Directory]::CreateDirectory($destination)
        # Probe only this new run directory; never touch existing evidence or root files.
        $probe = Join-Path $destination '.write-check'
        $stream = [System.IO.File]::Open($probe, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
        try { $stream.WriteByte(0) }
        finally { $stream.Dispose() }
        [System.IO.File]::Delete($probe)
        return $destination
    }
    catch { throw "Cannot create or write acceptance artifacts at '$destination': $($_.Exception.Message)" }
}

function Invoke-ReviewNative {
    param([string]$File, [string[]]$Arguments, [switch]$Capture, [switch]$AllowFailure, [int]$TimeoutSeconds = 60)
    $start = [System.Diagnostics.ProcessStartInfo]::new((Get-Command $File -ErrorAction Stop).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "$File $($Arguments[0]) timed out."
        }
        $output = $stdout.GetAwaiter().GetResult().Trim()
        $errorText = $stderr.GetAwaiter().GetResult().Trim()
        if (-not $Capture) {
            if ($output) { Write-Host $output }
            if ($errorText) { Write-Host $errorText }
        }
        if ($process.ExitCode -ne 0 -and -not $AllowFailure) {
            throw "$File $($Arguments[0]) failed (exit $($process.ExitCode))."
        }
        if ($Capture) { return [pscustomobject]@{ Code = $process.ExitCode; Output = $output; ErrorText = $errorText } }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function Resolve-ApplicationCommit {
    param([string]$Repository, [int]$PullRequest, [string]$Revision)
    if ($PullRequest) {
        $reference = "refs/asap-review/pull/$PullRequest"
        Invoke-ReviewNative git @('-C', $Repository, 'fetch', '--no-tags', 'origin', "+refs/pull/$PullRequest/head:$reference")
    }
    else {
        $reference = if ($Revision) { $Revision } else { 'HEAD' }
        $found = Invoke-ReviewNative git @('-C', $Repository, 'rev-parse', '--verify', '--end-of-options', "$reference^{commit}") -Capture -AllowFailure
        if ($found.Code -eq 0) { return $found.Output }
        Invoke-ReviewNative git @('-C', $Repository, 'fetch', '--no-tags', 'origin', $reference)
        $reference = 'FETCH_HEAD'
    }
    $resolved = (Invoke-ReviewNative git @('-C', $Repository, 'rev-parse', '--verify', '--end-of-options', "$reference^{commit}") -Capture).Output
    if ($resolved -notmatch '^[a-f0-9]{40}$') { throw 'Could not resolve a full application commit SHA.' }
    return $resolved
}

function Get-ReviewToolingPaths {
    # Explicit inputs exclude local configuration, secrets, generated output and the repository itself.
    return @('package.json', 'package-lock.json', 'scripts/review', 'dev/review/src', 'dev/review/Containerfile',
        'dev/review/acceptance-entrypoint.sh', 'dev/review/runtime.cjs', 'dev/review/appsettings.Testing.json',
        'dev/review/VERSION', 'tests/acceptance', 'tests/browser/confirmation.cjs', 'tests/browser/native-zoom-extension')
}

function Copy-ReviewTooling {
    param([string]$Repository, [string]$Destination)
    foreach ($item in (Get-ReviewToolingPaths)) {
        $source = Join-Path $Repository $item
        foreach ($file in @(Get-Item -LiteralPath $source | ForEach-Object {
            if ($_.PSIsContainer) { Get-ChildItem -LiteralPath $_.FullName -Recurse -File } else { $_ }
        } | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules|\.artifacts)[\\/]' })) {
            $relative = [System.IO.Path]::GetRelativePath($Repository, $file.FullName)
            $target = Join-Path $Destination $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
}

function Get-ReviewToolingDigest {
    param([string]$Directory)
    $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName)) {
            $relative = [System.IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\', '/')
            $hash.AppendData([System.Text.Encoding]::UTF8.GetBytes($relative + "`n"))
            $hash.AppendData([System.IO.File]::ReadAllBytes($file.FullName))
        }
        return [BitConverter]::ToString($hash.GetHashAndReset()).Replace('-', '').ToLowerInvariant()
    }
    finally { $hash.Dispose() }
}

function Remove-ReviewBuildContext {
    param([string]$Directory, [string]$BuildRoot)
    $absolute = [System.IO.Path]::GetFullPath($Directory)
    $root = [System.IO.Path]::GetFullPath($BuildRoot) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build cleanup path is outside the review build directory.' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
