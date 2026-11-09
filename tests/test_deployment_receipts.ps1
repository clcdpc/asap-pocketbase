$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/deployment/Deploy-AsapTest.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref] $tokens, [ref] $errors)
if ($errors.Count) { throw 'Deployment script must parse.' }
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    Invoke-Expression $definition.Extent.Text
}
function Assert-True([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Set-FixtureAcl([string] $Path, [Security.AccessControl.FileSystemSecurity] $Acl) {
    if ([IO.Directory]::Exists($Path)) { [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path), $Acl) }
    else { [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($Path), $Acl) }
}
function Get-Failure([scriptblock] $Action) {
    try { & $Action }
    catch { return $_.Exception.Message }
    throw 'Expected receipt publication failure.'
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('asap-receipt-test-' + [guid]::NewGuid().ToString('N'))
$script:publisher = (Get-Command Publish-DeploymentStateFile).ScriptBlock
$script:fault = ''
$script:lastTemporary = ''; $script:lastBackup = ''
function Publish-DeploymentStateFile {
    param($TemporaryPath, $Path, $BackupPath)
    $script:lastTemporary = $TemporaryPath; $script:lastBackup = $BackupPath
    # These are simulated documented ReplaceFile outcomes, not native error-code
    # injection. The real publication adapter is used for ordinary/locked files.
    switch ($script:fault) {
        'before' { throw 'fixture before replacement' }
        '1175' { throw 'fixture unable to remove replaced file' }
        '1176' { throw 'fixture unable to move replacement' }
        '1177' { [IO.File]::Move($Path, $BackupPath); throw 'fixture destination renamed to backup' }
        'uncertain-success' {
            & $script:publisher -TemporaryPath $TemporaryPath -Path $Path -BackupPath $BackupPath
            throw 'fixture error after replacement'
        }
        'no-write-dac' {
            $acl = Get-Acl -LiteralPath $TemporaryPath
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.AccessControl.FileSystemRights]::ChangePermissions,
                [Security.AccessControl.AccessControlType]::Deny))
            Set-FixtureAcl $TemporaryPath $acl
        }
    }
    & $script:publisher -TemporaryPath $TemporaryPath -Path $Path -BackupPath $BackupPath
    if ($script:fault -eq 'cleanup-backup') {
        $acl = Get-Acl -LiteralPath $BackupPath
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.AccessControl.FileSystemRights]::Delete,
            [Security.AccessControl.AccessControlType]::Deny))
        Set-FixtureAcl $BackupPath $acl
    }
}
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $manifest = [pscustomobject]@{ versionOrLabel = 'test-aaaaaaaaaaaa'; commitSha = 'a' * 40; dacpacSha256 = 'd' * 64 }
    $path = Join-Path $fixture 'deployment-state.json'
    $arguments = @{ Path = $path; Manifest = $manifest; ZipHash = 'b' * 64; Source = 'automatic' }
    Write-DeploymentState @arguments
    $bytes = [IO.File]::ReadAllBytes($path)
    Assert-True (-not ($bytes[0] -eq 0xef -and $bytes[1] -eq 0xbb -and $bytes[2] -eq 0xbf)) 'Receipt must be UTF-8 without BOM.'
    $state = Read-DeploymentState -StatePath $path
    Assert-True ($state.schemaVersion -eq 1 -and $state.commitSha -ceq $manifest.commitSha -and
        $state.versionOrLabel -ceq $manifest.versionOrLabel -and $state.dacpacSha256 -ceq $manifest.dacpacSha256 -and
        $state.deploymentZipSha256 -ceq ('b' * 64) -and $state.outcome -ceq 'success' -and
        $state.source -ceq 'automatic' -and $state.targetEnvironment -ceq 'DEVWEB') 'Initial publication must preserve schema and complete metadata.'
    Assert-True (@(Get-ChildItem $fixture -Filter '*.tmp').Count -eq 0 -and @(Get-ChildItem $fixture -Filter '*.bak').Count -eq 0) 'Initial rename consumes its temporary file.'

    $old = [IO.File]::ReadAllBytes($path)
    $manifest.versionOrLabel = 'test-éééééééééééé'
    Write-DeploymentState @arguments
    Assert-True ((Read-DeploymentState -StatePath $path).versionOrLabel -ceq $manifest.versionOrLabel) 'Replacement must preserve Unicode content.'
    Assert-True (@(Get-ChildItem $fixture -Filter '*.tmp').Count -eq 0 -and @(Get-ChildItem $fixture -Filter '*.bak').Count -eq 0) 'Successful replacement consumes temp and cleans its backup.'

    $old = [IO.File]::ReadAllBytes($path)
    $bad = [pscustomobject]@{ commitSha = 'a' * 40; dacpacSha256 = 'd' * 64 }
    $null = Get-Failure { Write-DeploymentState -Path $path -Manifest $bad -ZipHash ('b' * 64) }
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ceq [Convert]::ToBase64String($old)) 'Serialization/metadata failure must preserve old bytes.'
    foreach ($caseFault in @('before', '1175', '1176', '1177', 'uncertain-success')) {
        $script:fault = ''
        Write-DeploymentState @arguments
        $old = [IO.File]::ReadAllBytes($path)
        $script:fault = $caseFault
        $message = Get-Failure { Write-DeploymentState @arguments }
        Assert-True ($message -like '*Could not publish receipt*' -and $message.Contains($script:lastTemporary) -and
            $message.Contains($script:lastBackup) -and $message.Contains($path) -and $message -like '*outcome may be uncertain*') 'Failure must identify operation and every retained recovery candidate.'
        if ($caseFault -eq '1177') {
            Assert-True (-not [IO.File]::Exists($path) -and [IO.File]::Exists($script:lastTemporary)) 'Renamed-destination failure must retain complete replacement at temporary path.'
            Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($script:lastBackup)) -ceq [Convert]::ToBase64String($old)) 'Renamed-destination failure must retain old complete backup.'
            [IO.File]::Move($script:lastBackup, $path)
        }
        elseif ($caseFault -eq 'uncertain-success') {
            Assert-True ([IO.File]::Exists($path) -and [IO.File]::Exists($script:lastBackup) -and -not [IO.File]::Exists($script:lastTemporary)) 'Uncertain successful rename must retain old backup and new receipt.'
        }
        else {
            Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ceq [Convert]::ToBase64String($old)) 'Pre-replacement failure must preserve exact old bytes.'
            Assert-True ([IO.File]::Exists($script:lastTemporary)) 'Complete temporary receipt must remain available after publication failure.'
        }
        $script:fault = ''
    }

    foreach ($badJson in @('{broken', '{"schemaVersion":2}', '{"schemaVersion":1,"versionOrLabel":"x"}')) {
        $badPath = Join-Path $fixture 'bad.json'
        [IO.File]::WriteAllText($badPath, $badJson)
        $null = Get-Failure { Read-DeploymentState -StatePath $badPath }
    }

    if ($IsWindows) {
        # Real native sharing failure: replacement cannot rename a receipt whose
        # open handle does not permit FILE_SHARE_DELETE.
        $old = [IO.File]::ReadAllBytes($path)
        $lock = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { $message = Get-Failure { Write-DeploymentState @arguments } }
        finally { $lock.Dispose() }
        Assert-True ($message -like '*Could not publish receipt*' -and [IO.File]::Exists($script:lastTemporary)) 'Native sharing failure must retain temporary receipt.'
        Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ceq [Convert]::ToBase64String($old)) 'Native sharing failure preserves old complete bytes.'

        # Disposable representative ACL: directory permits creating files but
        # has no DeleteChild; only receipt/sibling files carry delete/WRITE_DAC.
        # Unrelated configuration remains read-only to the deployment token.
        $aclRoot = Join-Path $fixture 'acl'
        New-Item -ItemType Directory -Path $aclRoot | Out-Null
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,
            ([Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::CreateFiles),
            [Security.AccessControl.AccessControlType]::Allow))
        $fileRights = [Security.AccessControl.FileSystemRights]::Modify -bor [Security.AccessControl.FileSystemRights]::ChangePermissions
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, $fileRights,
            [Security.AccessControl.InheritanceFlags]::ObjectInherit, [Security.AccessControl.PropagationFlags]::InheritOnly,
            [Security.AccessControl.AccessControlType]::Allow))
        Set-FixtureAcl $aclRoot $acl
        $configPath = Join-Path $aclRoot 'application.json'
        [IO.File]::WriteAllText($configPath, 'configuration stays read-only')
        $configAcl = [Security.AccessControl.FileSecurity]::new()
        $configAcl.SetAccessRuleProtection($true, $false)
        $configAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,
            [Security.AccessControl.FileSystemRights]::Read, [Security.AccessControl.AccessControlType]::Allow))
        Set-FixtureAcl $configPath $configAcl
        $aclPath = Join-Path $aclRoot 'deployment-state.json'
        $aclArguments = @{ Path = $aclPath; Manifest = $manifest; ZipHash = 'b' * 64 }
        Write-DeploymentState @aclArguments
        $receiptAcl = Get-Acl -LiteralPath $aclPath
        $receiptAcl.SetAccessRuleProtection($true, $true)
        Set-FixtureAcl $aclPath $receiptAcl
        $originalSddl = (Get-Acl -LiteralPath $aclPath).Sddl
        Write-DeploymentState @aclArguments
        Assert-True ((Get-Acl -LiteralPath $aclPath).Sddl -ceq $originalSddl) 'Native replacement must preserve protected receipt DACL exactly.'
        $null = Get-Failure { [IO.File]::WriteAllText($configPath, 'forbidden') }
        $null = Get-Failure { [IO.File]::Delete($configPath) }
        Assert-True ([IO.File]::ReadAllText($configPath) -ceq 'configuration stays read-only') 'Receipt rights must not grant write/delete on unrelated configuration.'
        # Prove plain read/write is insufficient: replacement additionally needs
        # delete permission on the receipt (no parent DeleteChild fallback).
        $deniedAcl = Get-Acl -LiteralPath $aclPath
        $deniedAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,
            [Security.AccessControl.FileSystemRights]::Delete, [Security.AccessControl.AccessControlType]::Deny))
        Set-FixtureAcl $aclPath $deniedAcl
        $old = [IO.File]::ReadAllBytes($aclPath)
        $message = Get-Failure { Write-DeploymentState @aclArguments }
        Assert-True ($message -like '*Could not publish receipt*' -and [IO.File]::Exists($script:lastTemporary)) 'Native missing delete right must fail with recovery path.'
        Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($aclPath)) -ceq [Convert]::ToBase64String($old)) 'Missing delete permission must preserve old receipt bytes.'
        Set-FixtureAcl $aclPath $receiptAcl
        $script:fault = 'no-write-dac'
        $message = Get-Failure { Write-DeploymentState @aclArguments }
        $script:fault = ''
        Assert-True ($message -like '*Could not publish receipt*' -and [IO.File]::Exists($script:lastTemporary)) 'Native missing replacement WRITE_DAC must fail; metadata errors must not be ignored.'
        # Failure can have merged metadata; explicitly repair this disposable
        # fixture's receipt DACL before the separate cleanup-warning scenario.
        $restoredAcl = [Security.AccessControl.FileSecurity]::new()
        $restoredAcl.SetAccessRuleProtection($true, $false)
        $restoredAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, $fileRights, [Security.AccessControl.AccessControlType]::Allow))
        Set-FixtureAcl $aclPath $restoredAcl
        $script:fault = 'cleanup-backup'
        $cleanupWarnings = @()
        Write-DeploymentState @aclArguments -WarningVariable cleanupWarnings -WarningAction SilentlyContinue
        $script:fault = ''
        Assert-True ($cleanupWarnings.Count -eq 1 -and $cleanupWarnings[0].ToString().Contains($script:lastBackup) -and
            [IO.File]::Exists($script:lastBackup) -and [IO.File]::Exists($aclPath)) 'Confirmed publication with failed backup cleanup must retain the backup and diagnose its path.'
        Write-Host 'Native Windows replacement, sharing, narrow delete/WRITE_DAC and preserved-DACL fixtures passed.'
    }
    else { Write-Host 'Windows ACL/sharing fixtures require native Windows; not covered by this platform.' }
    Write-Host 'Deployment receipt creation, replacement, encoding, failure and recovery-retention fixtures passed.'
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if (-not $resolvedFixture.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture cleanup path.' }
    if ($IsWindows -and (Test-Path -LiteralPath $resolvedFixture)) {
        # Restore only disposable fixture ACLs so failure cases cannot obstruct
        # independent cleanup. No application/host ACL is ever touched.
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        foreach ($item in @(Get-Item -LiteralPath $resolvedFixture) + @(Get-ChildItem -LiteralPath $resolvedFixture -Recurse -Force)) {
            $security = Get-Acl -LiteralPath $item.FullName
            $security.SetAccessRuleProtection($true, $false)
            $security.PurgeAccessRules($sid)
            $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow))
            Set-FixtureAcl $item.FullName $security
        }
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force -ErrorAction SilentlyContinue
}
