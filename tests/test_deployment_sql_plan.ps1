$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/deployment/Deploy-AsapTest.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref] $tokens, [ref] $errors)
if ($errors.Count) { throw 'Deployment script must parse.' }
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    Invoke-Expression $definition.Extent.Text
}
function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Failure([scriptblock] $Action, [string] $Pattern) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike $Pattern) { throw "Expected '$Pattern', got '$($_.Exception.Message)'." }
        return
    }
    throw "Expected failure '$Pattern'."
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('asap-sql-plan-test-' + [guid]::NewGuid().ToString('N'))
$originalMachine = $env:COMPUTERNAME
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $env:COMPUTERNAME = 'fixture-iis'
    $script:nativeCalls = [Collections.Generic.List[object]]::new()
    $script:sqlIdentity = 'ASAP_TARGET|fixture-sql\instance|AsapTest'
    $script:binding = '*:443:dev.example.invalid'
    $script:nativeFailure = ''
    function Get-WebBinding {
        [CmdletBinding()] param([string] $Name, [string] $Protocol)
        Assert-True ($Name -eq 'ASAP' -and $Protocol -eq 'https') 'Must inspect the configured HTTPS site.'
        [pscustomobject]@{ bindingInformation = $script:binding }
    }
    function Invoke-FixtureSql {
        $script:nativeCalls.Add(@($args))
        $script:LASTEXITCODE = 0
        if ($args -contains '-Q') {
            Assert-True ($args[-1] -match "SERVERPROPERTY\('ServerName'\).*DB_NAME\(\)") 'Target query must use connected SQL identity.'
            if ($script:nativeFailure -eq 'identity') { $script:LASTEXITCODE = 1 }
            return $script:sqlIdentity
        }
        Assert-True ($args -contains '-i' -and $args -contains '-E' -and $args -contains '-b') 'SQL execution must retain integrated authentication and fail on SQL errors.'
        $sql = Get-Content -LiteralPath $args[-1] -Raw
        if ($sql -match 'schema identity') {
            Assert-True ($sql -match 'Version\] < 13' -and $sql -match 'Version\] > 13') 'Actual DACPAC schema guards must remain.'
            if ($script:nativeFailure -eq 'guard') { $script:LASTEXITCODE = 1 }
        }
        elseif ($script:nativeFailure -eq 'execute') { $script:LASTEXITCODE = 1 }
    }
    function Invoke-FixturePackage {
        $script:nativeCalls.Add(@($args))
        $script:LASTEXITCODE = 0
        foreach ($option in @('/p:CreateNewDatabase=False', '/p:DropObjectsNotInSource=False', '/p:ScriptDatabaseOptions=False')) {
            Assert-True ($args -contains $option) "Must retain $option."
        }
        $output = @($args | Where-Object { $_ -like '/OutputPath:*' })
        if ($output.Count) {
            $path = $output[0].Substring('/OutputPath:'.Length)
            if ($args -contains '/Action:Script') {
                [IO.File]::WriteAllText($path, 'ALTER TABLE [asap].[TitleRequest] DROP COLUMN [LegacyId];')
            }
            else { [IO.File]::WriteAllText($path, '<DeploymentReport><Operations /></DeploymentReport>') }
        }
        if ($script:nativeFailure -eq 'plan') { $script:LASTEXITCODE = 1 }
    }
    $connection = 'Server=fixture-alias;Database=AsapTest;Integrated Security=True;Encrypt=True'
    $config = [pscustomobject]@{
        IisSiteName = 'ASAP'; IisAppPoolName = 'ASAP'; DeploymentPath = Join-Path $fixture 'web'
        BackupRoot = Join-Path $fixture 'backups'; StatePath = Join-Path $fixture 'state.json'
        ExternalApplicationConfigPath = Join-Path $fixture 'application.json'
        ReadinessUrl = 'https://dev.example.invalid/health/ready'
        AsapDatabaseConnectionString = $connection; HangfireDatabaseConnectionString = $connection
        SqlCmdPath = 'Invoke-FixtureSql'; SqlPackagePath = 'Invoke-FixturePackage'
        ExternalConfiguration = [pscustomobject]@{
            Environment = [pscustomobject]@{ Name = 'Test'; IsNonProduction = $true }
            ConnectionStrings = [pscustomobject]@{ AsapDatabase = $connection }
        }
    }
    $pins = [ordered]@{ machineName = 'fixture-iis'; iisHostName = 'dev.example.invalid'; sqlServerName = 'fixture-sql\instance'; databaseName = 'AsapTest' }
    $target = Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json)
    Assert-True ($target.sqlServerName -eq $pins.sqlServerName) 'Validated target must bind canonical SQL server, not the connection alias.'
    foreach ($key in @('machineName', 'iisHostName', 'sqlServerName', 'databaseName')) {
        $changed = [ordered]@{}; foreach ($entry in $pins.GetEnumerator()) { $changed[$entry.Key] = $entry.Value }
        $changed[$key] = 'wrong-target'
        Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($changed | ConvertTo-Json) } '*target*'
    }
    foreach ($badJson in @('', '{}', 'null', '[]', '{broken')) {
        Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget $badJson } '*'
    }
    foreach ($marker in @($false, 'true', 1)) {
        $config.ExternalConfiguration.Environment.IsNonProduction = $marker
        Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*IsNonProduction*'
    }
    $config.ExternalConfiguration.Environment.IsNonProduction = $true
    $config.ExternalConfiguration.Environment.Name = 'Production'
    Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*pinned non-production*'
    $config.ExternalConfiguration.Environment.Name = 'Test'
    $script:binding = '*:443:'
    Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*HTTPS IIS binding*'
    $script:binding = '*:443:dev.example.invalid'
    $config.ExternalConfiguration.ConnectionStrings.AsapDatabase = 'Server=production;Database=AsapTest;Integrated Security=True'
    Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*runtime and deployment*'
    $config.ExternalConfiguration.ConnectionStrings.AsapDatabase = $connection
    foreach ($routing in @('Failover Partner=production', 'AttachDBFilename=other.mdf', 'User Instance=True')) {
        $config.ExternalConfiguration.ConnectionStrings.AsapDatabase = "$connection;$routing"
        Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*failover or file-attachment*'
    }
    $config.ExternalConfiguration.ConnectionStrings.AsapDatabase = $connection
    $config.ReadinessUrl = 'https://dev.example.invalid:4443/health/ready'
    Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*pinned non-production*'
    $config.ReadinessUrl = 'https://dev.example.invalid/health/ready'
    $pins.databaseName = 'master'
    Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*pinned non-production*'
    $pins.databaseName = 'AsapTest'
    foreach ($identity in @('ASAP_TARGET|production|AsapTest', 'ASAP_TARGET|fixture-sql\instance|Other', '', "ASAP_TARGET|fixture-sql\instance|AsapTest`nASAP_TARGET|fixture-sql\instance|AsapTest")) {
        $script:sqlIdentity = $identity
        Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*connected server/database*'
    }
    $script:sqlIdentity = 'ASAP_TARGET|fixture-sql\instance|AsapTest'
    $script:nativeFailure = 'identity'
    Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*identity query failed*'
    $script:nativeFailure = ''
    Assert-Failure { Get-SqlConnectionDetails 'Server=dev;Data Source=production;Database=AsapTest;Integrated Security=True' } '*ambiguous aliases*'

    $dacpacRoot = Join-Path $fixture 'dacpac'
    New-Item -ItemType Directory -Path $dacpacRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'database/Asap.Database/Scripts/PreDeployment.sql') -Destination (Join-Path $dacpacRoot 'predeploy.sql')
    $dacpac = Join-Path $fixture 'fixture.dacpac'
    [IO.Compression.ZipFile]::CreateFromDirectory($dacpacRoot, $dacpac)
    $archive = [pscustomobject]@{
        DacpacPath = $dacpac; DacpacHash = (Get-FileHash $dacpac).Hash.ToLowerInvariant()
        StagingPath = Join-Path $fixture 'staging'; WebRoot = Join-Path $fixture 'staging/web'
        Manifest = [pscustomobject]@{ commitSha = 'a' * 40; versionOrLabel = 'fixture' }; ZipHash = 'b' * 64
        HangfireScriptPath = Join-Path $fixture 'hangfire.sql'
    }
    $script:nativeCalls.Clear()
    $protected = New-DacpacPlan -Config $config -Archive $archive -OutputDirectory (Join-Path $fixture 'protected')
    Assert-True ($script:nativeCalls.Count -eq 2) 'Preflight must generate script and report only.'
    foreach ($call in $script:nativeCalls) { Assert-True ($call -contains '/p:BlockOnPossibleDataLoss=True' -and $call -notcontains '/Action:Publish') 'Default preflight must remain protected and non-mutating.' }
    $script:nativeCalls.Clear()
    $plan = New-DacpacPlan -Config $config -Archive $archive -Target $target -AllowSqlDataLoss -OutputDirectory (Join-Path $fixture 'candidate')
    foreach ($call in $script:nativeCalls) { Assert-True ($call -contains '/p:BlockOnPossibleDataLoss=False' -and $call -notcontains '/Action:Publish') 'Candidate preflight must describe loss without publishing.' }
    Assert-ReviewedSqlPlan -Plan $plan -ReviewedHash $plan.Hash
    foreach ($badHash in @('', 'true', ('0' * 64), ($plan.Hash + ' '))) {
        Assert-Failure { Assert-ReviewedSqlPlan -Plan $plan -ReviewedHash $badHash } '*unreviewed or changed*'
    }
    Assert-Failure { Assert-ReviewedSqlPlan -Plan $protected -ReviewedHash $protected.Hash } '*unreviewed or changed*'
    foreach ($path in @($plan.ScriptPath, $plan.ReportPath)) {
        $bytes = [IO.File]::ReadAllBytes($path)
        [IO.File]::AppendAllText($path, 'unreviewed broader drop')
        Assert-Failure { Assert-ReviewedSqlPlan -Plan $plan -ReviewedHash $plan.Hash } '*unreviewed or changed*'
        [IO.File]::WriteAllBytes($path, $bytes)
    }
    foreach ($key in @('commitSha', 'schemaVersion')) {
        $old = $plan.Context[$key]; $plan.Context[$key] = 'changed'
        Assert-Failure { Assert-ReviewedSqlPlan -Plan $plan -ReviewedHash $plan.Hash } '*unreviewed or changed*'
        $plan.Context[$key] = $old
    }
    $plan.Context.target.databaseName = 'Other'
    Assert-Failure { Assert-ReviewedSqlPlan -Plan $plan -ReviewedHash $plan.Hash } '*unreviewed or changed*'
    $plan.Context.target.databaseName = 'AsapTest'
    $publish = @{ SqlCmdPath = $config.SqlCmdPath; SqlPackagePath = $config.SqlPackagePath; ConnectionString = $connection; DacpacPath = $dacpac }
    $script:nativeCalls.Clear()
    Invoke-DacpacPublish @publish
    Assert-True ($script:nativeCalls.Count -eq 2 -and $script:nativeCalls[1] -contains '/Action:Publish' -and $script:nativeCalls[1] -contains '/p:BlockOnPossibleDataLoss=True') 'Default publication must retain the schema guard and protected SqlPackage publish.'
    $script:nativeCalls.Clear()
    Invoke-DacpacPublish @publish -AllowSqlDataLoss -Plan $plan -ReviewedPlanSha256 $plan.Hash
    Assert-True ($script:nativeCalls.Count -eq 2 -and $script:nativeCalls[1][-1] -eq $plan.ScriptPath) 'Opt-in must run the exact reviewed SQL bytes after the DACPAC schema guard.'
    Assert-True (-not @($script:nativeCalls | Where-Object { $_ -contains '/Action:Publish' }).Count) 'Opt-in must never replan with an unprotected Publish action.'
    $script:nativeFailure = 'guard'
    $script:nativeCalls.Clear()
    Assert-Failure { Invoke-DacpacPublish @publish -AllowSqlDataLoss -Plan $plan -ReviewedPlanSha256 $plan.Hash } '*pre-deployment contract failed*'
    Assert-True ($script:nativeCalls.Count -eq 1) 'Schema rejection must prevent executing the approved plan.'
    $script:nativeFailure = 'execute'
    Assert-Failure { Invoke-DacpacPublish @publish -AllowSqlDataLoss -Plan $plan -ReviewedPlanSha256 $plan.Hash } '*database state may have changed*'
    $script:nativeFailure = 'plan'
    Assert-Failure { New-DacpacPlan -Config $config -Archive $archive -OutputDirectory (Join-Path $fixture 'failed-plan') } '*preflight failed*'
    $script:nativeFailure = ''
    Assert-Failure { New-DacpacPlan -Config $config -Archive $archive -OutputDirectory $config.DeploymentPath } '*outside web*'

    # Run the actual top-level statements in isolated child processes, replacing
    # only external boundaries. An exit in PlanOnly/ValidateOnly is real, and
    # event receipts prove that neither can reach activation or recovery writes.
    $main = ($ast.EndBlock.Statements | Where-Object { $_ -isnot [Management.Automation.Language.FunctionDefinitionAst] } |
        ForEach-Object { $_.Extent.Text }) -join "`n"
    $data = @{ Config = $config; Archive = $archive; Plan = $plan } | ConvertTo-Json -Depth 10 -Compress
    $encodedData = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($data))
    $harness = Join-Path $fixture 'entrypoint.ps1'
    $preamble = @'
param([string] $Mode, [string] $ScriptPath, [string] $EncodedData, [string] $EventsPath)
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref] $tokens, [ref] $errors)
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) { Invoke-Expression $definition.Extent.Text }
$data = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($EncodedData)) | ConvertFrom-Json
function Event([string] $Text) { Add-Content -LiteralPath $EventsPath -Value $Text }
function Read-HostConfiguration { param($Path) Event 'host'; return $data.Config }
function Test-DeploymentArchive { param($ZipPath, $ExpectedHash, $ExpectedCommit, $ExpectedLabel, $StagingPath) Event 'archive'; return $data.Archive }
function Test-PublishedApplicationConfigPath { param($WebRoot, $ExternalApplicationConfigPath) Event 'pointer' }
function Test-IisConfiguration { param($Config) Event 'iis' }
function Assert-SqlDataLossTarget { param($Config, $ExpectedTarget) Event 'target'; return $data.Plan.Context.target }
function New-DacpacPlan { param($Config, $Archive, $Target, $OutputDirectory, [switch] $AllowSqlDataLoss) Event "plan:$([bool] $AllowSqlDataLoss)"; return $data.Plan }
function Read-DeploymentState { param($StatePath) Event 'state-read'; return $null }
function Get-HangfireSchemaVersion { param($SqlCmdPath, $ConnectionString) Event 'hangfire-read'; return 9 }
function Set-NonProductionDatabaseRecovery { param($ExternalConfiguration, $SqlCmdPath, $ConnectionString) Event 'recovery-write' }
function Invoke-TestDeployment { param($Config, $Archive, $NeedsDacpac, $NeedsHangfire, [switch] $AllowSqlDataLoss, $SqlPlan, $ReviewedPlanSha256, $TimeoutSeconds, $PollSeconds) Event "activation:$([bool] $AllowSqlDataLoss)" }
$data.Config | Add-Member -NotePropertyName StagingRoot -NotePropertyValue ([IO.Path]::GetDirectoryName($EventsPath))
$ValidateOnly = $Mode -eq 'validate'
$SqlPlanOnly = $Mode -eq 'candidate'
$AllowSqlDataLoss = $Mode -in @('approved', 'unreviewed')
$ReviewedSqlPlanSha256 = if ($Mode -eq 'approved') { $data.Plan.Hash } elseif ($Mode -eq 'unreviewed') { '0' * 64 } else { '' }
$ExpectedSqlDataLossTarget = '{}'; $SqlPlanOutputDirectory = [IO.Path]::GetDirectoryName($EventsPath)
$ConfigPath = 'fixture'; $DeploymentZip = 'fixture'; $ExpectedZipSha256 = 'b' * 64
$ExpectedCommitSha = 'a' * 40; $ExpectedDeploymentLabel = 'fixture'
$ReadinessTimeoutSeconds = 1; $ReadinessPollSeconds = 1
'@
    [IO.File]::WriteAllText($harness, $preamble + "`n" + $main)
    foreach ($mode in @('validate', 'candidate', 'unreviewed', 'approved', 'default')) {
        $eventsPath = Join-Path $fixture "$mode-events.txt"
        $output = & (Get-Process -Id $PID).Path -NoLogo -NoProfile -File $harness -Mode $mode -ScriptPath $scriptPath -EncodedData $encodedData -EventsPath $eventsPath 2>&1
        $exitCode = $LASTEXITCODE
        $events = @(Get-Content -LiteralPath $eventsPath)
        if ($mode -eq 'unreviewed') {
            Assert-True ($exitCode -ne 0 -and "$output" -like '*unreviewed or changed*') 'Changed/unreviewed plans must fail at the entrypoint.'
        }
        else { Assert-True ($exitCode -eq 0) "Entrypoint $mode failed: $output" }
        switch ($mode) {
            'validate' { Assert-True (($events -join ',') -eq 'archive') 'ValidateOnly must never read host configuration.' }
            'candidate' { Assert-True (($events -join ',') -eq 'host,archive,pointer,iis,target,plan:True') 'PlanOnly must return before state/recovery/activation.' }
            'unreviewed' { Assert-True (-not ($events -contains 'recovery-write') -and -not ($events -like 'activation:*')) 'Unreviewed plans must fail before any activation or recovery mutation.' }
            'approved' { Assert-True ($events -contains 'activation:True') 'Only a hash-approved plan may reach opt-in activation.' }
            'default' { Assert-True ($events -contains 'plan:False' -and $events -contains 'activation:False' -and $events -notcontains 'target') 'The default must remain protected without requiring exception pins.' }
        }
    }

    # Recovery tests exercise the real activation control flow with narrow host
    # boundaries. No IIS module, SQL connection or real application is used.
    $script:events = [Collections.Generic.List[string]]::new()
    $script:fault = ''; $script:poolState = 'Started'; $script:stopCalls = 0
    function Get-WebAppPoolState { [CmdletBinding()] param([string] $Name) [pscustomobject]@{ Value = $script:poolState } }
    function Backup-WebPayload { param($DeploymentPath, $BackupRoot) $script:events.Add('backup'); if ($script:fault -eq 'backup') { throw 'fixture backup failure' }; return 'retained-backup' }
    function Stop-TestAppPool {
        param($AppPoolName)
        $script:events.Add('stop'); $script:poolState = 'Stopped'; $script:stopCalls++
        if ($script:fault -eq 'stop' -and $script:stopCalls -eq 1) { throw 'fixture stop failure' }
        if ($script:fault -eq 'cleanup' -and $script:stopCalls -gt 1) { throw 'fixture cleanup failure' }
    }
    function Start-TestAppPool { param($AppPoolName) $script:events.Add('start'); $script:poolState = 'Started' }
    function Invoke-DacpacPublish {
        param($SqlCmdPath, $SqlPackagePath, $ConnectionString, $DacpacPath, [switch] $AllowSqlDataLoss, $Plan, $ReviewedPlanSha256)
        $script:events.Add('dacpac'); if ($script:fault -in @('dacpac', 'cleanup')) { throw 'fixture dacpac failure' }
    }
    function Invoke-HangfireSchemaInstall { param($SqlCmdPath, $ConnectionString, $InstallScriptPath) $script:events.Add('hangfire'); if ($script:fault -eq 'hangfire') { throw 'fixture hangfire failure' } }
    function Get-HangfireSchemaVersion { param($SqlCmdPath, $ConnectionString) if ($script:fault -eq 'version') { return 8 }; return 9 }
    function Replace-WebPayload { param($StagedWebRoot, $DeploymentPath) $script:events.Add('replace'); if ($script:fault -eq 'replace') { throw 'fixture replace failure' } }
    function Test-Ready { param($Uri, $TimeoutSeconds, $PollSeconds) $script:events.Add('ready'); return $script:fault -ne 'ready' }
    function Write-DeploymentState { param($Path, $Manifest, $ZipHash) $script:events.Add('state'); if ($script:fault -eq 'state') { throw 'fixture state failure' } }
    $activation = @{ Config = $config; Archive = $archive; NeedsDacpac = $true; NeedsHangfire = $true; TimeoutSeconds = 1; PollSeconds = 1 }
    foreach ($initial in @('Started', 'Stopped')) {
        foreach ($fault in @('', 'backup', 'stop', 'dacpac', 'hangfire', 'version', 'replace', 'ready', 'state')) {
            $script:poolState = $initial; $script:fault = $fault; $script:events.Clear(); $script:stopCalls = 0
            if ($fault -eq '') {
                Invoke-TestDeployment @activation
                Assert-True (($script:events -join ',') -eq 'backup,stop,dacpac,hangfire,replace,start,ready,state') 'Success requires ordered schema/web activation, readiness and durable state.'
                Assert-True ($script:poolState -eq 'Started') 'Successful deployment must be running and ready.'
            }
            else {
                Assert-Failure { Invoke-TestDeployment @activation -WarningAction SilentlyContinue } '*'
                $expectedState = if ($fault -in @('backup', 'stop')) { $initial } else { 'Stopped' }
                Assert-True ($script:poolState -eq $expectedState) "Unsafe recovery for initial=$initial, fault=$fault."
                if ($fault -in @('backup', 'stop')) { Assert-True (-not $script:events.Contains('dacpac')) 'No SQL mutation may follow a backup/stop failure.' }
                if ($fault -notin @('ready', 'state')) { Assert-True (-not $script:events.Contains('state')) 'A failed deployment must not record success.' }
            }
        }
    }
    $script:fault = 'cleanup'; $script:poolState = 'Started'; $script:events.Clear(); $script:stopCalls = 0
    Assert-Failure { Invoke-TestDeployment @activation -WarningAction SilentlyContinue } '*fixture dacpac failure*'
    Assert-True ($script:stopCalls -eq 2 -and -not $script:events.Contains('start')) 'Cleanup failure must preserve the original SQL failure and never restart uncertain web/schema.'
    $script:fault = ''; $script:poolState = 'Starting'; $script:events.Clear()
    Assert-Failure { Invoke-TestDeployment @activation } '*stably Started or Stopped*'
    Assert-True ($script:events.Count -eq 0) 'A transitional pool must fail before backup/stop/mutation.'
    $script:poolState = 'Started'
    Assert-Failure { Invoke-TestDeployment @activation -AllowSqlDataLoss -SqlPlan $plan -ReviewedPlanSha256 ('0' * 64) } '*unreviewed or changed*'
    Assert-True ($script:events.Count -eq 0) 'Opt-in approval must be rechecked before backup or pool mutation.'
    Invoke-TestDeployment @activation -AllowSqlDataLoss -SqlPlan $plan -ReviewedPlanSha256 $plan.Hash
    Assert-True ($script:events.Contains('state')) 'A fully reviewed opt-in must retain the same readiness/state success path.'
    Write-Host 'SQL plan identity, opt-in, target guards and activation recovery tests passed (18 activation scenarios).'
}
finally {
    $env:COMPUTERNAME = $originalMachine
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if (-not $resolvedFixture.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force -ErrorAction SilentlyContinue
}
