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
if ($IsWindows) {
    # Actual-token access probes, plus a security-only handle acquired before
    # restricting OWNER RIGHTS. It grants no data/delete access that could
    # conflict with ReplaceFile's unshared replacement-file open.
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
public static class ReceiptNativeAccess
{
    public const uint WriteDac = 0x00040000;
    public const uint Delete = 0x00010000;
    public const uint ReplacementAccess = 0xc0150000; // GENERIC_READ/WRITE, SYNCHRONIZE, DELETE, WRITE_DAC
    public const uint DestinationAccess = 0x80110000; // GENERIC_READ, SYNCHRONIZE, DELETE
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, uint objectType, uint information,
        IntPtr owner, IntPtr group, byte[] dacl, IntPtr sacl);
    public static int Probe(string path, uint access, uint share, bool directory)
    {
        using (SafeFileHandle handle = CreateFileW(path, access, share, IntPtr.Zero, 3,
            directory ? 0x02000000u : 0u, IntPtr.Zero))
        {
            return handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        }
    }
    public static SafeFileHandle HoldDaclRestore(string path)
    {
        SafeFileHandle handle = CreateFileW(path, WriteDac | 0x00020000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }
    public static void RestoreDacl(SafeFileHandle handle, byte[] descriptor)
    {
        RawSecurityDescriptor security = new RawSecurityDescriptor(descriptor, 0);
        byte[] dacl = new byte[security.DiscretionaryAcl.BinaryLength];
        security.DiscretionaryAcl.GetBinaryForm(dacl, 0);
        uint protection = (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0
            ? 0x80000000u : 0x20000000u;
        uint error = SetSecurityInfo(handle, 1, 4 | protection, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
        if (error != 0)
        {
            throw new Win32Exception((int)error);
        }
    }
}
'@
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('asap-receipt-test-' + [guid]::NewGuid().ToString('N'))
$script:publisher = (Get-Command Publish-DeploymentStateFile).ScriptBlock
$script:fault = ''
$script:lastTemporary = ''; $script:lastBackup = ''
$script:nativePublicationFailure = $null
$script:daclRecovery = $null
function Publish-DeploymentStateFile {
    param($TemporaryPath, $Path, $BackupPath)
    $script:lastTemporary = $TemporaryPath; $script:lastBackup = $BackupPath
    $script:nativePublicationFailure = $null
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
            $script:daclRecovery = [pscustomobject]@{
                Handle = [ReceiptNativeAccess]::HoldDaclRestore($TemporaryPath)
                Descriptor = $acl.GetSecurityDescriptorBinaryForm()
            }
            Assert-True ([ReceiptNativeAccess]::Probe($TemporaryPath, [ReceiptNativeAccess]::ReplacementAccess, 0, $false) -eq 0) 'Recovery handle must not obstruct an unshared replacement open before the fault.'
            # Owners normally receive implicit WRITE_DAC despite a user deny.
            # OWNER RIGHTS suppresses that grant; keep READ_CONTROL available.
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new('S-1-3-4'), [Security.AccessControl.FileSystemRights]::ChangePermissions,
                [Security.AccessControl.AccessControlType]::Deny))
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.AccessControl.FileSystemRights]::ChangePermissions,
                [Security.AccessControl.AccessControlType]::Deny))
            Set-FixtureAcl $TemporaryPath $acl
            Assert-True ([ReceiptNativeAccess]::Probe($TemporaryPath, [ReceiptNativeAccess]::WriteDac, 7, $false) -eq 5) 'Replacement WRITE_DAC must be effectively denied with ERROR_ACCESS_DENIED (5), including owner rights.'
            Assert-True ([ReceiptNativeAccess]::Probe($TemporaryPath, ([ReceiptNativeAccess]::ReplacementAccess -bxor [ReceiptNativeAccess]::WriteDac), 0, $false) -eq 0) 'All other replacement rights and its unshared open must still succeed.'
            Assert-True ([ReceiptNativeAccess]::Probe($Path, [ReceiptNativeAccess]::DestinationAccess, 7, $false) -eq 0) 'Destination read/delete rights must remain available during the isolated WRITE_DAC fault.'
        }
    }
    try { & $script:publisher -TemporaryPath $TemporaryPath -Path $Path -BackupPath $BackupPath }
    catch { $script:nativePublicationFailure = $_.Exception.GetBaseException(); throw }
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
        $originalDacl = (Get-Acl -LiteralPath $aclPath).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
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
        Assert-True ([ReceiptNativeAccess]::Probe($aclRoot, 0x40, 7, $true) -eq 5) 'Parent must not provide DeleteChild as an alternative to receipt Delete.'
        Assert-True ([ReceiptNativeAccess]::Probe($aclPath, [ReceiptNativeAccess]::Delete, 7, $false) -eq 5) 'Receipt Delete must be effectively denied with ERROR_ACCESS_DENIED (5).'
        $old = [IO.File]::ReadAllBytes($aclPath)
        $message = Get-Failure { Write-DeploymentState @aclArguments }
        Assert-True ($message -like '*Could not publish receipt*' -and [IO.File]::Exists($script:lastTemporary)) 'Native missing delete right must fail with recovery path.'
        Assert-True ($null -ne $script:nativePublicationFailure -and $script:nativePublicationFailure.HResult -eq -2147024891) 'Delete-denial failure must originate in the native publication adapter with HRESULT 0x80070005, not fixture setup.'
        Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($aclPath)) -ceq [Convert]::ToBase64String($old)) 'Missing delete permission must preserve old receipt bytes.'
        Assert-True ((Read-DeploymentState -StatePath $script:lastTemporary).versionOrLabel -ceq $manifest.versionOrLabel) 'Delete-denial failure must retain a complete replacement receipt.'
        Write-Host 'Native Delete denial: receipt DELETE and parent DELETE_CHILD probes returned 5; publication HRESULT 0x80070005; old receipt and complete temporary receipt retained.'

        # Persist clears modification flags. Rehydrate the saved DACL so this
        # write actually removes the denial instead of reusing $receiptAcl.
        $restoredAcl = [Security.AccessControl.FileSecurity]::new()
        $restoredAcl.SetSecurityDescriptorSddlForm($originalDacl, [Security.AccessControl.AccessControlSections]::Access)
        Set-FixtureAcl $aclPath $restoredAcl
        Assert-True ((Get-Acl -LiteralPath $aclPath).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access) -ceq $originalDacl) 'Destination DACL must read back exactly after removing Delete denial.'
        Assert-True ([ReceiptNativeAccess]::Probe($aclPath, [ReceiptNativeAccess]::DestinationAccess, 7, $false) -eq 0) 'Restored destination must again grant native read/delete access.'
        $manifest.versionOrLabel = 'test-restored-control'
        Write-DeploymentState @aclArguments
        Assert-True ((Read-DeploymentState -StatePath $aclPath).versionOrLabel -ceq $manifest.versionOrLabel -and
            -not [IO.File]::Exists($script:lastTemporary) -and -not [IO.File]::Exists($script:lastBackup)) 'Control publication after DACL restoration must succeed and clean its own siblings.'
        Assert-True ((Get-Acl -LiteralPath $aclPath).Sddl -ceq $originalSddl) 'Control publication must preserve the restored protected receipt DACL.'
        Write-Host 'Destination DACL restored and read back exactly; native read/delete probe returned 0; control publication and sibling cleanup succeeded.'

        $old = [IO.File]::ReadAllBytes($aclPath)
        $manifest.versionOrLabel = 'test-isolated-write-dac'
        $script:fault = 'no-write-dac'
        try { $message = Get-Failure { Write-DeploymentState @aclArguments } }
        finally { $script:fault = '' }
        Assert-True ($message -like '*Could not publish receipt*' -and $message.Contains($script:lastTemporary) -and
            $message.Contains($script:lastBackup) -and $message.Contains($aclPath) -and [IO.File]::Exists($script:lastTemporary)) 'Native missing replacement WRITE_DAC must fail and identify retained recovery candidates.'
        Assert-True ($null -ne $script:nativePublicationFailure -and $script:nativePublicationFailure.HResult -eq -2147024891) 'WRITE_DAC failure must originate in the native publication adapter with HRESULT 0x80070005, not fixture setup or a sharing violation.'
        Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($aclPath)) -ceq [Convert]::ToBase64String($old)) 'Isolated WRITE_DAC failure must preserve exact old receipt bytes.'
        Assert-True ((Read-DeploymentState -StatePath $script:lastTemporary).versionOrLabel -ceq $manifest.versionOrLabel -and
            -not [IO.File]::Exists($script:lastBackup)) 'Isolated WRITE_DAC failure must retain a complete new temporary receipt without renaming the destination.'
        Assert-True ((Get-Acl -LiteralPath $aclPath).Sddl -ceq $originalSddl) 'WRITE_DAC failure must leave the destination protected DACL unchanged.'
        Write-Host 'Native WRITE_DAC denial with OWNER RIGHTS: WRITE_DAC probe returned 5; other replacement rights (unshared) and destination read/delete probes returned 0; publication HRESULT 0x80070005; both receipts retained.'

        # Paired native control: identical files, paths, and recovery handle;
        # restore only the temporary DACL, then retry the real adapter.
        $replacementBytes = [IO.File]::ReadAllBytes($script:lastTemporary)
        [ReceiptNativeAccess]::RestoreDacl($script:daclRecovery.Handle, $script:daclRecovery.Descriptor)
        $savedAcl = [Security.AccessControl.FileSecurity]::new()
        $savedAcl.SetSecurityDescriptorBinaryForm($script:daclRecovery.Descriptor)
        Assert-True ((Get-Acl -LiteralPath $script:lastTemporary).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access) -ceq
            $savedAcl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)) 'Replacement DACL must read back exactly after handle-based restoration.'
        Assert-True ([ReceiptNativeAccess]::Probe($script:lastTemporary, [ReceiptNativeAccess]::ReplacementAccess, 0, $false) -eq 0) 'Restored replacement must grant every native replacement right with the same recovery handle open.'
        & $script:publisher -TemporaryPath $script:lastTemporary -Path $aclPath -BackupPath $script:lastBackup
        Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($aclPath)) -ceq [Convert]::ToBase64String($replacementBytes) -and
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($script:lastBackup)) -ceq [Convert]::ToBase64String($old) -and
            -not [IO.File]::Exists($script:lastTemporary)) 'Same-files retry must publish successfully after restoring only replacement WRITE_DAC.'
        [IO.File]::Delete($script:lastBackup)
        Write-Host 'WRITE_DAC paired control: restored replacement DACL read back exactly; unshared full-rights probe returned 0; same-files native retry succeeded.'

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
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedFixture) -cnotmatch '^asap-receipt-test-[0-9a-f]{32}$') { throw 'Unexpected fixture cleanup path.' }
    $cleanupFailures = [Collections.Generic.List[string]]::new()
    if ($null -ne $script:daclRecovery) {
        # This preauthorized handle can restore WRITE_DAC even when OWNER RIGHTS
        # denies reopening the file. Run this independently of all assertions.
        try { [ReceiptNativeAccess]::RestoreDacl($script:daclRecovery.Handle, $script:daclRecovery.Descriptor) }
        catch { $cleanupFailures.Add("Handle-based DACL restoration: $($_.Exception.Message)") }
        finally { $script:daclRecovery.Handle.Dispose() }
    }
    if ($IsWindows -and (Test-Path -LiteralPath $resolvedFixture)) {
        # Restore only disposable fixture ACLs so failure cases cannot obstruct
        # independent cleanup. No application/host ACL is ever touched.
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        foreach ($item in @(Get-Item -LiteralPath $resolvedFixture) + @(Get-ChildItem -LiteralPath $resolvedFixture -Recurse -Force)) {
            try {
                $security = Get-Acl -LiteralPath $item.FullName
                $security.SetAccessRuleProtection($true, $false)
                $security.PurgeAccessRules($sid)
                $security.PurgeAccessRules([Security.Principal.SecurityIdentifier]::new('S-1-3-4'))
                $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow))
                Set-FixtureAcl $item.FullName $security
            }
            catch { $cleanupFailures.Add("ACL cleanup at '$($item.FullName)': $($_.Exception.Message)") }
        }
    }
    try { if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force } }
    catch { $cleanupFailures.Add("File cleanup: $($_.Exception.Message)") }
    if (Test-Path -LiteralPath $resolvedFixture) { $cleanupFailures.Add("Fixture remains at '$resolvedFixture'.") }
    if ($cleanupFailures.Count) { throw ($cleanupFailures -join [Environment]::NewLine) }
    Write-Host 'Independent fixture DACL restoration and file cleanup succeeded.'
}
