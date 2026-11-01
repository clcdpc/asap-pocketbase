# Development checks for actual launcher helpers; no container runtime required.
param([ValidateSet('revision', 'snapshot', 'native', 'artifacts', 'selection')][string]$Case)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
. (Join-Path $repository 'scripts/review/Review-Environment.ps1')
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock]$Operation, [string]$Pattern) {
    try { & $Operation } catch { if ($_.Exception.Message -match $Pattern) { return }; throw }
    throw "Expected rejection: $Pattern"
}
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ('asap-launcher-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    switch ($Case) {
        'revision' {
            $remote = Join-Path $temporary 'remote.git'
            $source = Join-Path $temporary 'source'
            $clone = Join-Path $temporary 'clone'
            Invoke-ReviewNative git @('init', '--bare', $remote)
            Invoke-ReviewNative git @('init', $source)
            Invoke-ReviewNative git @('-C', $source, 'config', 'user.email', 'test@example.org')
            Invoke-ReviewNative git @('-C', $source, 'config', 'user.name', 'Acceptance launcher test')
            'application' | Set-Content (Join-Path $source 'identity.txt')
            Invoke-ReviewNative git @('-C', $source, 'add', 'identity.txt')
            Invoke-ReviewNative git @('-C', $source, 'commit', '-m', 'application')
            $application = (Invoke-ReviewNative git @('-C', $source, 'rev-parse', 'HEAD') -Capture).Output
            Invoke-ReviewNative git @('-C', $source, 'push', $remote, 'HEAD:refs/pull/410/head')
            'merge' | Set-Content (Join-Path $source 'identity.txt')
            Invoke-ReviewNative git @('-C', $source, 'commit', '-am', 'synthetic merge')
            Invoke-ReviewNative git @('-C', $source, 'push', $remote, 'HEAD:refs/pull/410/merge')
            Invoke-ReviewNative git @('clone', '--no-checkout', $remote, $clone)
            $resolved = Resolve-ApplicationCommit -Repository $clone -PullRequest 410
            Assert ($resolved -eq $application) 'PR resolution selected something other than HEAD.'
            Assert ((Resolve-ApplicationCommit -Repository $clone -Revision $application) -eq $application) 'Direct revision selection failed.'
            $archive = Join-Path $temporary 'application.zip'
            Invoke-ReviewNative git @('-C', $clone, 'archive', '--format=zip', "--output=$archive", $resolved)
            $expanded = Join-Path $temporary 'expanded'
            Expand-Archive -LiteralPath $archive -DestinationPath $expanded
            Assert ((Get-Content (Join-Path $expanded 'identity.txt') -Raw).Trim() -eq 'application') 'Build input is not the selected application.'
        }
        'snapshot' {
            $snapshot = Join-Path $temporary 'tooling'
            Copy-ReviewTooling $repository $snapshot
            $digest = Get-ReviewToolingDigest $snapshot
            Assert ($digest -match '^[a-f0-9]{64}$') 'Invalid tooling content digest.'
            Assert ($digest -eq (Get-ReviewToolingDigest $snapshot)) 'Unchanged snapshot is unstable.'
            Assert (Test-Path (Join-Path $snapshot 'tests/browser/native-zoom-extension/manifest.json')) 'Native zoom extension missing.'
            Assert (-not (Test-Path (Join-Path $snapshot 'dev/review/review.local.json'))) 'Snapshot contains reviewer configuration.'
            Assert (@(Get-ChildItem $snapshot -Recurse -File | Where-Object FullName -match '[\\/](bin|obj|node_modules)[\\/]').Count -eq 0) 'Snapshot contains generated output.'
            Add-Content (Join-Path $snapshot 'dev/review/runtime.cjs') '// changed tooling'
            Assert ($digest -ne (Get-ReviewToolingDigest $snapshot)) 'Working tree changes reuse a stale cache identity.'
        }
        'native' {
            $outcome = Invoke-ReviewNative pwsh @('-NoProfile', '-Command', 'Write-Output canary; exit 1') -Capture -AllowFailure
            Assert ($outcome.Code -eq 1 -and $outcome.Output -eq 'canary') 'Native exit/output propagation failed.'
            Reject { Invoke-ReviewNative pwsh @('-NoProfile', '-Command', 'Write-Output secret-canary; exit 2') -Capture } 'failed \(exit 2\)'
            $log = Join-Path $temporary 'timeout.log'
            Reject { Invoke-ReviewNative pwsh @('-NoProfile', '-Command', 'Write-Output before-timeout; Start-Sleep -Seconds 10') -TimeoutSeconds 2 -LogPath $log } 'timed out'
            Assert ((Get-Content -LiteralPath $log -Raw) -match 'before-timeout') 'Timed-out build lost its diagnostics.'
            $buildRoot = Join-Path $temporary 'build'
            $build = Join-Path $buildRoot 'run'
            New-Item -ItemType Directory -Path $build -Force | Out-Null
            Reject { Remove-ReviewBuildContext $temporary $buildRoot } 'outside'
            Remove-ReviewBuildContext $build $buildRoot
            Assert (-not (Test-Path $build)) 'Build context cleanup failed.'
        }
        'artifacts' {
            $root = Join-Path $temporary 'custom output [literal]'
            $selected = Join-Path $root '../custom output [literal]'
            $options = @{ ApplicationCommit = 'a' * 40; Suite = 's1'; Timestamp = [DateTimeOffset]::new(2026, 11, 1, 13, 30, 0, [TimeSpan]::Zero) }
            $expectedStamp = $options.Timestamp.ToLocalTime().ToString('yyyy-MM-dd_HH-mm-ss')
            $directory = New-ReviewArtifactDirectory -ArtifactsRoot $selected -PullRequest 415 @options
            Assert ([System.IO.Path]::GetFileName($directory) -ceq "pr-415_s1_$expectedStamp") 'PR or host-local timestamp naming is wrong.'
            Assert ([System.IO.Path]::GetDirectoryName($directory) -eq [System.IO.Path]::GetFullPath($root)) 'Custom root was not normalized literally.'
            Assert (Test-Path -LiteralPath $directory -PathType Container) 'Artifact directory was not created.'
            Assert (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) 'Write probe was left in the artifacts.'
            'previous evidence' | Set-Content -LiteralPath (Join-Path $directory 'report.md')
            'unrelated file' | Set-Content -LiteralPath (Join-Path $root 'keep.txt')
            $next = New-ReviewArtifactDirectory -ArtifactsRoot $root -PullRequest 415 @options
            Assert ([System.IO.Path]::GetFileName($next) -ceq "pr-415_s1_$expectedStamp-02") 'Second collision suffix is wrong.'
            $third = New-ReviewArtifactDirectory -ArtifactsRoot $root -PullRequest 415 @options
            Assert ([System.IO.Path]::GetFileName($third) -ceq "pr-415_s1_$expectedStamp-03") 'Third collision suffix is wrong.'
            $commit = New-ReviewArtifactDirectory -ArtifactsRoot $root @options
            Assert ([System.IO.Path]::GetFileName($commit) -ceq "commit-aaaaaaaaaaaa_s1_$expectedStamp") 'Commit naming is wrong.'
            Assert ($directory -ne $next) 'Artifact runs collided.'
            Assert ((Get-Content -LiteralPath (Join-Path $directory 'report.md') -Raw).Trim() -eq 'previous evidence') 'A previous run was modified.'
            Assert ((Get-Content -LiteralPath (Join-Path $root 'keep.txt') -Raw).Trim() -eq 'unrelated file') 'An unrelated file was modified.'
            foreach ($relative in @('', 'relative', '../relative')) {
                Reject { New-ReviewArtifactDirectory -ArtifactsRoot $relative @options } 'absolute filesystem path'
            }
            $file = Join-Path $temporary 'file.txt'
            'keep' | Set-Content -LiteralPath $file
            Reject { New-ReviewArtifactDirectory -ArtifactsRoot $file @options } 'Cannot create or write acceptance artifacts'
            foreach ($unsafe in @('../s1', 's1/smoke', 's1,smoke', 's1\\smoke', 's1:smoke', 's1 ', '..')) {
                Reject { New-ReviewArtifactDirectory -ArtifactsRoot $root -Suite $unsafe -ApplicationCommit ('a' * 40) } 'pattern'
            }
            if ($IsWindows) {
                Reject { New-ReviewArtifactDirectory -ArtifactsRoot 'C:relative' @options } 'absolute filesystem path'
                Reject { New-ReviewArtifactDirectory -ArtifactsRoot (Join-Path $temporary 'bad<path') @options } 'Cannot create or write acceptance artifacts'
                $denied = [System.IO.Directory]::CreateDirectory((Join-Path $temporary 'denied')).FullName
                $original = Get-Acl -LiteralPath $denied
                $acl = Get-Acl -LiteralPath $denied
                $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
                $rule = [System.Security.AccessControl.FileSystemAccessRule]::new($identity,
                    [System.Security.AccessControl.FileSystemRights]::Write, [System.Security.AccessControl.AccessControlType]::Deny)
                $acl.AddAccessRule($rule)
                try {
                    Set-Acl -LiteralPath $denied -AclObject $acl
                    Reject { New-ReviewArtifactDirectory -ArtifactsRoot $denied @options } 'Cannot create or write acceptance artifacts'
                }
                finally { Set-Acl -LiteralPath $denied -AclObject $original }
            }
        }
        'selection' {
            $registered = @('d1', 'pr410', 'q1', 's1', 'smoke')
            Assert ((@(Get-ReviewSuiteSelection -Suite s1,d1,q1 -Registered $registered) -join ',') -ceq 's1,d1,q1') 'Native comma-array binding or order failed.'
            Assert ((@(Get-ReviewSuiteSelection -Suite @('s1', 'D1', 'q1') -Registered $registered) -join ',') -ceq 's1,d1,q1') 'Explicit array/canonicalization failed.'
            Assert ((@(Get-ReviewSuiteSelection -Suite s1 -Registered $registered) -join ',') -ceq 's1') 'Single suite failed.'
            foreach ($invalid in @(@(), @('s1', 'S1'), @('s1', ''), @('s1', 'missing'), @('s1,d1'), @('../s1'))) {
                Reject { Get-ReviewSuiteSelection -Suite $invalid -Registered $registered } 'Available suites:.*local tooling checkout.*updating'
            }
        }
    }
    Write-Host "PASS: $Case"
}
finally {
    # This exact temporary directory was created above; never accept cleanup paths as input.
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
