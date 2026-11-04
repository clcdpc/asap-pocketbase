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
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $script:fixtureMachineName = 'DEVWEB'
    function Get-DeploymentMachineName { return $script:fixtureMachineName }
    $script:nativeCalls = [Collections.Generic.List[object]]::new()
    $script:sqlIdentity = 'ASAP_TARGET|fixture-sql\instance|AsapTest'
    $script:binding = '*:443:devasap.clcohio.org'
    $script:nativeFailure = ''
    $script:legacyReport = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fixtures/deployment/legacy-retirement-report.xml') -Raw
    foreach ($configuration in @([pscustomobject]@{}, [pscustomobject]@{ AllowSqlDataLoss = $false })) {
        Assert-True (-not (Get-DevSqlOverrideSettings -Configuration $configuration).Enabled) 'Missing/false host policy must remain protected.'
    }
    foreach ($value in @('true', 'false', 'TRUE', '', 0, 1, $null)) {
        Assert-Failure { Get-DevSqlOverrideSettings -Configuration ([pscustomobject]@{ AllowSqlDataLoss = $value }) } '*JSON boolean*'
    }
    Assert-Failure { Get-DevSqlOverrideSettings -Configuration ([pscustomobject]@{ AllowSqlDataLoss = $true }) } '*verified SqlDataLossTarget*'
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
            else { [IO.File]::WriteAllText($path, $script:legacyReport) }
        }
        if ($script:nativeFailure -eq 'plan') { $script:LASTEXITCODE = 1 }
    }
    $connection = 'Server=fixture-alias;Database=AsapTest;Integrated Security=True;Encrypt=True'
    $config = [pscustomobject]@{
        IisSiteName = 'ASAP'; IisAppPoolName = 'ASAP'; DeploymentPath = Join-Path $fixture 'web'
        BackupRoot = Join-Path $fixture 'backups'; StatePath = Join-Path $fixture 'state.json'
        ExternalApplicationConfigPath = Join-Path $fixture 'application.json'
        ReadinessUrl = 'https://devasap.clcohio.org/health/ready'
        AsapDatabaseConnectionString = $connection; HangfireDatabaseConnectionString = $connection
        SqlCmdPath = 'Invoke-FixtureSql'; SqlPackagePath = 'Invoke-FixturePackage'
        AllowSqlDataLoss = $false
        ExternalConfiguration = [pscustomobject]@{
            Environment = [pscustomobject]@{ Name = 'Test'; IsNonProduction = $true }
            ConnectionStrings = [pscustomobject]@{ AsapDatabase = $connection }
        }
    }
    $pins = [ordered]@{ machineName = 'DEVWEB'; iisHostName = 'devasap.clcohio.org'; sqlServerName = 'fixture-sql\instance'; databaseName = 'AsapTest' }
    $config | Add-Member -NotePropertyName SqlDataLossTarget -NotePropertyValue ([pscustomobject] $pins)
    # Exercise actual IIS preflight against disposable management boundaries.
    function Import-Module { [CmdletBinding()] param($Name) Assert-True ($Name -eq 'WebAdministration') 'IIS preflight may import only its management module.' }
    $script:sitePath = $config.DeploymentPath; $script:sitePool = 'ASAP'; $script:poolPresent = $true
    function Get-Website { [CmdletBinding()] param($Name) [pscustomobject]@{ PhysicalPath = $script:sitePath; ApplicationPool = $script:sitePool } }
    function Get-WebAppPoolState { [CmdletBinding()] param($Name) if ($script:poolPresent) { return [pscustomobject]@{ Value = 'Started' } } }
    Test-IisConfiguration -Config $config
    $script:sitePath = Join-Path $fixture 'wrong-site'
    Assert-Failure { Test-IisConfiguration -Config $config } '*physical path*'
    $script:sitePath = $config.DeploymentPath; $script:sitePool = 'Other'
    Assert-Failure { Test-IisConfiguration -Config $config } '*application pool*'
    $script:sitePool = 'ASAP'; $script:poolPresent = $false
    Assert-Failure { Test-IisConfiguration -Config $config } '*not found*'
    $script:poolPresent = $true
    Assert-True (Get-DevSqlOverrideSettings -Configuration ([pscustomobject]@{ AllowSqlDataLoss = $true; SqlDataLossTarget = [pscustomobject] $pins })).Enabled 'The persistent setting must enable without a per-run approval input.'
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
    $script:fixtureMachineName = 'PRODWEB'; $pins.machineName = 'PRODWEB'
    Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*pinned non-production*'
    $script:fixtureMachineName = 'DEVWEB'; $pins.machineName = 'DEVWEB'
    $script:binding = '*:443:'
    Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*HTTPS IIS binding*'
    $script:binding = '*:443:devasap.clcohio.org'
    $config.ExternalConfiguration.ConnectionStrings.AsapDatabase = 'Server=production;Database=AsapTest;Integrated Security=True'
    Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*runtime and deployment*'
    $config.ExternalConfiguration.ConnectionStrings.AsapDatabase = $connection
    foreach ($routing in @('Failover Partner=production', 'AttachDBFilename=other.mdf', 'User Instance=True')) {
        $config.ExternalConfiguration.ConnectionStrings.AsapDatabase = "$connection;$routing"
        Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*failover or file-attachment*'
    }
    $config.ExternalConfiguration.ConnectionStrings.AsapDatabase = $connection
    $config.ReadinessUrl = 'https://devasap.clcohio.org:4443/health/ready'
    Assert-Failure { Assert-SqlDataLossTarget -Config $config -ExpectedTarget ($pins | ConvertTo-Json) } '*pinned non-production*'
    $config.ReadinessUrl = 'https://devasap.clcohio.org/health/ready'
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
    foreach ($name in @('PreDeployment', 'PostDeployment')) {
        $source = [IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) "database/Asap.Database/Scripts/$name.sql")).Replace("`r`n", "`n")
        # Microsoft.Build.Sql appends a batch terminator to packaged scripts.
        $entryName = if ($name -eq 'PreDeployment') { 'predeploy.sql' } else { 'postdeploy.sql' }
        [IO.File]::WriteAllText((Join-Path $dacpacRoot $entryName), ($source + "GO`n"))
    }
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
    Assert-DevSqlPlan -Plan $plan

    # Test.27's actual target report must be accepted with the DEV setting on,
    # including all 39 check-constraint replacements. False must still reject.
    $actualReport = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fixtures/deployment/test27-check-constraint-replacements.xml') -Raw
    [IO.File]::WriteAllText($plan.ReportPath, $actualReport)
    $plan.Hash = Get-SqlPlanSha256 -Context $plan.Context -ScriptPath $plan.ScriptPath -ReportPath $plan.ReportPath
    Assert-DevSqlPlan -Plan $plan
    $plan.Context.allowSqlDataLoss = $false
    $plan.Hash = Get-SqlPlanSha256 -Context $plan.Context -ScriptPath $plan.ScriptPath -ReportPath $plan.ReportPath
    Assert-Failure { Assert-DevSqlPlan -Plan $plan } '*invalid or changed*'
    $plan.Context.allowSqlDataLoss = $true
    [IO.File]::WriteAllText($plan.ReportPath, $script:legacyReport)
    $plan.Hash = Get-SqlPlanSha256 -Context $plan.Context -ScriptPath $plan.ScriptPath -ReportPath $plan.ReportPath
    Assert-Failure { Assert-DevSqlPlan -Plan $protected } '*invalid or changed*'
    foreach ($path in @($plan.ScriptPath, $plan.ReportPath, $plan.DacpacPath)) {
        $bytes = [IO.File]::ReadAllBytes($path)
        [IO.File]::AppendAllText($path, 'unreviewed broader drop')
        Assert-Failure { Assert-DevSqlPlan -Plan $plan } '*invalid or changed*'
        [IO.File]::WriteAllBytes($path, $bytes)
    }
    foreach ($key in @('commitSha', 'schemaVersion')) {
        $old = $plan.Context[$key]; $plan.Context[$key] = 'changed'
        Assert-Failure { Assert-DevSqlPlan -Plan $plan } '*invalid or changed*'
        $plan.Context[$key] = $old
    }
    $plan.Context.target.databaseName = 'Other'
    Assert-Failure { Assert-DevSqlPlan -Plan $plan } '*invalid or changed*'
    $plan.Context.target.databaseName = 'AsapTest'
    $originalHash = $plan.Hash
    $noLoss = '<DeploymentReport xmlns="http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02"><Alerts /><Operations /></DeploymentReport>'
    $authorizedReports = @(
        $noLoss,
        $noLoss.Replace('<Operations />', '<Operations><Operation Name="Create"><Item Value="[asap].[NewTable]" Type="SqlTable" /></Operation></Operations>'),
        $noLoss.Replace('<Operations />', '<Operations><Operation Name="Drop"><Item Value="[asap].[LiveTable]" Type="SqlTable" /></Operation></Operations>'),
        $noLoss.Replace('<Alerts />', '<Alerts><Alert Name="DataMotion"><Issue Value="Table data will be moved." /></Alert></Alerts>'),
        $actualReport.Replace('[asap].[TitleRequest].[LegacyId]', '[asap].[TitleRequest].[LiveColumn]')
    )
    foreach ($report in $authorizedReports) {
        [IO.File]::WriteAllText($plan.ReportPath, $report)
        $plan.Hash = Get-SqlPlanSha256 -Context $plan.Context -ScriptPath $plan.ScriptPath -ReportPath $plan.ReportPath
        Assert-DevSqlPlan -Plan $plan
    }
    $badReports = @(
        $noLoss.Replace('DeployReport/2012/02', 'unknown-report-version'),
        $noLoss.Replace('<Alerts />', '<Alerts /><Alerts />'),
        $noLoss.Replace('<Operations />', ''),
        $noLoss.Replace('<Operations />', '<Operations /><Unexpected />')
    )
    foreach ($report in $badReports) {
        [IO.File]::WriteAllText($plan.ReportPath, $report)
        $plan.Hash = Get-SqlPlanSha256 -Context $plan.Context -ScriptPath $plan.ScriptPath -ReportPath $plan.ReportPath
        Assert-Failure { Assert-DevSqlPlan -Plan $plan } '*unrecognized*'
    }
    [IO.File]::WriteAllText($plan.ReportPath, '<!DOCTYPE DeploymentReport [<!ENTITY forbidden "x">]>' + $noLoss)
    $plan.Hash = Get-SqlPlanSha256 -Context $plan.Context -ScriptPath $plan.ScriptPath -ReportPath $plan.ReportPath
    Assert-Failure { Assert-DevSqlPlan -Plan $plan } '*DTD*'
    [IO.File]::WriteAllText($plan.ReportPath, $script:legacyReport); $plan.Hash = $originalHash
    $changedRoot = Join-Path $fixture 'changed-scripts'
    New-Item -ItemType Directory -Path $changedRoot | Out-Null
    Copy-Item -Path (Join-Path $dacpacRoot '*') -Destination $changedRoot
    [IO.File]::AppendAllText((Join-Path $changedRoot 'postdeploy.sql'), 'DROP TABLE [asap].[LiveTable];')
    $changedDacpac = Join-Path $fixture 'changed.dacpac'
    [IO.Compression.ZipFile]::CreateFromDirectory($changedRoot, $changedDacpac)
    $plan.DacpacPath = $changedDacpac
    Assert-Failure { Assert-DevSqlPlan -Plan $plan } '*invalid or changed*'
    $plan.Context.dacpacSha256 = (Get-FileHash -LiteralPath $changedDacpac -Algorithm SHA256).Hash.ToLowerInvariant()
    $plan.Hash = Get-SqlPlanSha256 -Context $plan.Context -ScriptPath $plan.ScriptPath -ReportPath $plan.ReportPath
    Assert-DevSqlPlan -Plan $plan
    $plan.DacpacPath = $dacpac
    $plan.Context.dacpacSha256 = $archive.DacpacHash
    $plan.Hash = $originalHash
    $publish = @{ SqlCmdPath = $config.SqlCmdPath; SqlPackagePath = $config.SqlPackagePath; ConnectionString = $connection; DacpacPath = $dacpac }
    $script:nativeCalls.Clear()
    Invoke-DacpacPublish @publish
    Assert-True ($script:nativeCalls.Count -eq 2 -and $script:nativeCalls[1] -contains '/Action:Publish' -and $script:nativeCalls[1] -contains '/p:BlockOnPossibleDataLoss=True') 'Default publication must retain the schema guard and protected SqlPackage publish.'
    $script:nativeCalls.Clear()
    Invoke-DacpacPublish @publish -AllowSqlDataLoss -Plan $plan
    Assert-True ($script:nativeCalls.Count -eq 2 -and $script:nativeCalls[1][-1] -eq $plan.ScriptPath) 'Opt-in must run the exact reviewed SQL bytes after the DACPAC schema guard.'
    Assert-True (-not @($script:nativeCalls | Where-Object { $_ -contains '/Action:Publish' }).Count) 'Opt-in must never replan with an unprotected Publish action.'
    $script:nativeFailure = 'guard'
    $script:nativeCalls.Clear()
    Assert-Failure { Invoke-DacpacPublish @publish -AllowSqlDataLoss -Plan $plan } '*pre-deployment contract failed*'
    Assert-True ($script:nativeCalls.Count -eq 1) 'Schema rejection must prevent executing the approved plan.'
    $script:nativeFailure = 'execute'
    Assert-Failure { Invoke-DacpacPublish @publish -AllowSqlDataLoss -Plan $plan } '*database state may have changed*'
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
function Read-HostConfiguration {
    param($Path)
    Event 'host'
    $policy = Get-DevSqlOverrideSettings -Configuration $data.Config
    $data.Config.AllowSqlDataLoss = $policy.Enabled
    return $data.Config
}
function Test-DeploymentArchive { param($ZipPath, $ExpectedHash, $ExpectedCommit, $ExpectedLabel, $StagingPath) Event 'archive'; return $data.Archive }
function Test-PublishedApplicationConfigPath { param($WebRoot, $ExternalApplicationConfigPath) Event 'pointer' }
function Test-IisConfiguration { param($Config) Event 'iis' }
function Assert-SqlDataLossTarget { param($Config, $ExpectedTarget) Event 'target'; return $data.Plan.Context.target }
function New-DacpacPlan { param($Config, $Archive, $Target, $OutputDirectory, [switch] $AllowSqlDataLoss) Event "plan:$([bool] $AllowSqlDataLoss)"; return $data.Plan }
function Read-DeploymentState { param($StatePath) Event 'state-read'; return $null }
function Get-HangfireSchemaVersion { param($SqlCmdPath, $ConnectionString) Event 'hangfire-read'; return 9 }
function Set-NonProductionDatabaseRecovery { param($ExternalConfiguration, $SqlCmdPath, $ConnectionString) Event 'recovery-write' }
function Invoke-TestDeployment { param($Config, $Archive, $NeedsDacpac, $NeedsHangfire, [switch] $AllowSqlDataLoss, $SqlPlan, $Source, $TimeoutSeconds, $PollSeconds) Event "activation:$([bool] $AllowSqlDataLoss)" }
$data.Config | Add-Member -NotePropertyName StagingRoot -NotePropertyValue ([IO.Path]::GetDirectoryName($EventsPath))
$ValidateOnly = $Mode -eq 'validate'
$SqlPlanOnly = $Mode -in @('candidate', 'candidate-enabled')
$data.Config.AllowSqlDataLoss = $Mode -in @('enabled', 'enabled-again', 'candidate-enabled', 'invalid-plan', 'production-tag')
if ($Mode -eq 'invalid-plan') { $data.Plan.Hash = '0' * 64 }
if ($Mode -eq 'malformed-setting') { $data.Config.AllowSqlDataLoss = 'true' }
$SqlPlanOutputDirectory = [IO.Path]::GetDirectoryName($EventsPath)
$ConfigPath = 'fixture'; $DeploymentZip = 'fixture'; $ExpectedZipSha256 = 'b' * 64
$env:GITHUB_OUTPUT = "$EventsPath.outputs"
$ExpectedCommitSha = 'a' * 40
$ExpectedDeploymentLabel = if ($Mode -eq 'production-tag') { 'v1.0.0' } else { 'v1.0.0-test.27' }
$ReadinessTimeoutSeconds = 1; $ReadinessPollSeconds = 1
'@
    [IO.File]::WriteAllText($harness, $preamble + "`n" + $main)
    foreach ($mode in @('validate', 'candidate', 'candidate-enabled', 'enabled', 'enabled-again', 'invalid-plan', 'default', 'disabled-again', 'production-tag', 'malformed-setting')) {
        $eventsPath = Join-Path $fixture "$mode-events.txt"
        $output = & (Get-Process -Id $PID).Path -NoLogo -NoProfile -File $harness -Mode $mode -ScriptPath $scriptPath -EncodedData $encodedData -EventsPath $eventsPath 2>&1
        $exitCode = $LASTEXITCODE
        $events = @(Get-Content -LiteralPath $eventsPath)
        if ($mode -in @('invalid-plan', 'production-tag', 'malformed-setting')) {
            Assert-True ($exitCode -ne 0) "Entrypoint $mode must fail closed."
        }
        else { Assert-True ($exitCode -eq 0) "Entrypoint $mode failed: $output" }
        switch ($mode) {
            'validate' { Assert-True (($events -join ',') -eq 'archive') 'ValidateOnly must never read host configuration.' }
            'candidate' { Assert-True (($events -join ',') -eq 'host,archive,pointer,iis,target,plan:True') 'PlanOnly must return before state/recovery/activation.' }
            'candidate-enabled' { Assert-True (($events -join ',') -eq 'host,archive,pointer,iis,target,plan:True') 'PlanOnly must remain non-mutating even while the persistent setting is true.' }
            'invalid-plan' { Assert-True (-not ($events -contains 'recovery-write') -and -not ($events -like 'activation:*')) 'Changed plans must fail before any activation or recovery mutation.' }
            { $_ -in @('enabled', 'enabled-again') } { Assert-True ($events -contains 'activation:True') 'The same persistent true setting must permit successive scoped deployments without per-run input.' }
            'default' { Assert-True ($events -contains 'plan:False' -and $events -contains 'activation:False' -and $events -notcontains 'target') 'The default must remain protected without requiring exception pins.' }
            'disabled-again' { Assert-True ($events -contains 'plan:False' -and $events -contains 'activation:False') 'Setting false again must restore the protected path.' }
            { $_ -in @('production-tag', 'malformed-setting') } { Assert-True (($events -join ',') -eq 'host') 'Unsafe labels and malformed settings must fail before archive/IIS/SQL access.' }
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
        param($SqlCmdPath, $SqlPackagePath, $ConnectionString, $DacpacPath, [switch] $AllowSqlDataLoss, $Plan)
        $script:events.Add('dacpac'); if ($script:fault -in @('dacpac', 'cleanup')) { throw 'fixture dacpac failure' }
    }
    function Invoke-HangfireSchemaInstall { param($SqlCmdPath, $ConnectionString, $InstallScriptPath) $script:events.Add('hangfire'); if ($script:fault -eq 'hangfire') { throw 'fixture hangfire failure' } }
    function Get-HangfireSchemaVersion { param($SqlCmdPath, $ConnectionString) if ($script:fault -eq 'version') { return 8 }; return 9 }
    function Replace-WebPayload { param($StagedWebRoot, $DeploymentPath) $script:events.Add('replace'); if ($script:fault -eq 'replace') { throw 'fixture replace failure' } }
    function Test-Ready { param($Uri, $TimeoutSeconds, $PollSeconds) $script:events.Add('ready'); return $script:fault -ne 'ready' }
    function Write-DeploymentState { param($Path, $Manifest, $ZipHash, $Source) $script:events.Add('state'); if ($script:fault -eq 'state') { throw 'fixture state failure' } }
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
    $plan.Hash = '0' * 64
    Assert-Failure { Invoke-TestDeployment @activation -AllowSqlDataLoss -SqlPlan $plan } '*invalid or changed*'
    $plan.Hash = $originalHash
    Assert-True ($script:events.Count -eq 0) 'Plan integrity must be rechecked before backup or pool mutation.'
    Invoke-TestDeployment @activation -AllowSqlDataLoss -SqlPlan $plan
    Assert-True ($script:events.Contains('state')) 'A scoped DEV override must retain the same readiness/state success path.'
    Write-Host 'SQL plan identity, opt-in, target guards and activation recovery tests passed (18 activation scenarios).'
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if (-not $resolvedFixture.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force -ErrorAction SilentlyContinue
}
