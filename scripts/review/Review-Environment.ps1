# Internal helpers for the disposable WSLC acceptance command.
function Get-ReviewSuiteSelection {
    param([string[]]$Suite, [string[]]$Registered)
    $help = "Available suites: $($Registered -join ', '). Use a local tooling checkout containing the requested suites; it may need updating."
    if (-not $Suite -or -not $Suite.Count) { throw "Select at least one suite. $help" }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $Suite) {
        if ([string]::IsNullOrWhiteSpace($name) -or $name -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{0,63}$') {
            throw "Empty or invalid suite identifier. $help"
        }
        $canonical = @($Registered | Where-Object { $_ -ieq $name })
        if ($canonical.Count -ne 1) { throw "Unknown suite '$name'. $help" }
        if (-not $seen.Add($name)) { throw "Duplicate suite '$name' (case-insensitive). $help" }
        if ($canonical[0] -cnotmatch '^[a-z0-9][a-z0-9-]{0,63}$') { throw "Unsafe identifier in local suite registry. $help" }
    }
    # Emit only after validating the entire selection; preserve caller order with canonical identifiers.
    foreach ($name in $Suite) { $Registered | Where-Object { $_ -ieq $name } }
}

function Get-ReviewReportExitCode {
    param([hashtable]$Report)
    if (-not $Report.ContainsKey('requiredScenarioIds')) { return 2 }
    $functional = @($Report.scenarios | Where-Object category -eq 'functional')
    $required = @($Report.requiredScenarioIds)
    if ($Report.environment.status -ne 'PASS' -or $Report.authentication.status -ne 'PASS' -or
        -not $required.Count -or -not $functional.Count -or @($required | Select-Object -Unique).Count -ne $required.Count -or
        @($functional | Where-Object status -notin @('PASS', 'FAIL')).Count -or
        @($required | Where-Object { $id = $_; @($functional | Where-Object id -eq $id).Count -ne 1 }).Count) { return 2 }
    if (@($functional | Where-Object status -eq 'FAIL').Count) { return 1 }
    return 0
}

function Get-ReviewArtifactRoot {
    param(
        [string]$ArtifactsRoot = [System.IO.Path]::Combine([Environment]::GetFolderPath('UserProfile'), 'ASAP-Acceptance')
    )
    if ([string]::IsNullOrWhiteSpace($ArtifactsRoot) -or -not [System.IO.Path]::IsPathFullyQualified($ArtifactsRoot)) {
        throw '-ArtifactsRoot must be an absolute filesystem path.'
    }
    try {
        $root = [System.IO.Path]::GetFullPath($ArtifactsRoot)
        $null = [System.IO.Directory]::CreateDirectory($root)
        return $root
    }
    catch { throw "Cannot create or write acceptance artifacts at '$ArtifactsRoot': $($_.Exception.Message)" }
}

function New-ReviewArtifactDirectory {
    param(
        [string]$ArtifactsRoot = [System.IO.Path]::Combine([Environment]::GetFolderPath('UserProfile'), 'ASAP-Acceptance'),
        [int]$PullRequest,
        [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$ApplicationCommit,
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$Suite,
        [DateTimeOffset]$Timestamp = [DateTimeOffset]::Now
    )
    $root = Get-ReviewArtifactRoot -ArtifactsRoot $ArtifactsRoot
    $target = if ($PullRequest) { "pr-$PullRequest" } else { "commit-$($ApplicationCommit.Substring(0, 12))" }
    $name = "${target}_${Suite}_$($Timestamp.ToLocalTime().ToString('yyyy-MM-dd_HH-mm-ss', [Globalization.CultureInfo]::InvariantCulture))"
    $staging = Join-Path $root ('.allocate-' + [guid]::NewGuid().ToString('N'))
    $destination = Join-Path $root $name
    try {
        $null = [System.IO.Directory]::CreateDirectory($staging)
        $probe = Join-Path $staging '.write-check'
        $stream = [System.IO.File]::Open($probe, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
        try { $stream.WriteByte(0) }
        finally { $stream.Dispose() }
        [System.IO.File]::Delete($probe)
        for ($attempt = 1; ; $attempt++) {
            $suffix = if ($attempt -eq 1) { '' } else { '-' + $attempt.ToString('D2') }
            $destination = Join-Path $root ($name + $suffix)
            try {
                # Same-filesystem rename is atomic and refuses an existing file/directory.
                # CreateDirectory alone would silently reuse another caller's evidence directory.
                [System.IO.Directory]::Move($staging, $destination)
                return $destination
            }
            catch [System.IO.IOException] {
                if (-not ([System.IO.Directory]::Exists($destination) -or [System.IO.File]::Exists($destination))) { throw }
            }
        }
    }
    catch { throw "Cannot create or write acceptance artifacts at '$destination': $($_.Exception.Message)" }
    finally {
        # Delete only this owned empty staging directory; never remove retained evidence.
        if ([System.IO.Directory]::Exists($staging)) { [System.IO.Directory]::Delete($staging) }
    }
}

function Invoke-ReviewNative {
    param([string]$File, [string[]]$Arguments, [switch]$Capture, [switch]$AllowFailure, [int]$TimeoutSeconds = 60, [string]$LogPath)
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
        $waitClock = [System.Diagnostics.Stopwatch]::StartNew()
        # Return to PowerShell regularly so Ctrl+C/Stop can unwind through finally
        # during image builds or container creation, rather than waiting up to 40 minutes.
        while (-not $process.WaitForExit(200)) {
            if ($waitClock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw "$File $($Arguments[0]) timed out." }
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
        $process.WaitForExit()
        if ($LogPath) {
            ($stdout.GetAwaiter().GetResult() + "`n" + $stderr.GetAwaiter().GetResult()) |
                Set-Content -LiteralPath $LogPath -Encoding utf8NoBOM
        }
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
        'dev/review/VERSION', 'tests/acceptance', 'tests/browser/confirmation.cjs', 'tests/browser/native-zoom-extension',
        'tests/browser/settings-format-rule-drafts.cjs', 'tests/browser/settings-inheritance-interactions.cjs',
        'tests/browser/settings-draft-blockers.cjs')
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
