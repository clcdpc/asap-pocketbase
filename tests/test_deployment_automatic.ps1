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

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('asap-automatic-test-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $commit = '8f3c2a1d92b456789012345678901234567890ab'
    $label = 'test-8f3c2a1d92b4'
    Assert-CommitDeploymentLabel -Label $label -Commit $commit
    foreach ($bad in @('test-8f3c2a1d92b5', 'test-8F3C2A1D92B4', 'TEST-8f3c2a1d92b4', 'test-anything', 'test-8f3c2a1d92b4-extra', 'test-')) {
        Assert-Failure { Assert-CommitDeploymentLabel -Label $bad -Commit $commit } '*first 12 lowercase*'
    }
    Assert-Failure { Assert-CommitDeploymentLabel -Label $label -Commit $commit.ToUpperInvariant() } '*first 12 lowercase*'
    foreach ($tag in @('v1.2.0-test.28', 'v1.2.0')) {
        Assert-CommitDeploymentLabel -Label $tag -Commit $commit
    }

    # Exercise the real HTTP adapter with a disposable API boundary. No live
    # GitHub/DEVWEB request is made, and API errors must not echo credentials.
    $env:GH_TOKEN = 'fixture-read-only-token'
    $script:apiFailure = $false
    function Invoke-RestMethod {
        param($Uri, $Method, $Headers, $TimeoutSec)
        Assert-True ($Uri -ceq 'https://api.github.com/repos/clcdpc/asap-pocketbase/git/ref/heads/main' -and $Method -eq 'Get' -and $TimeoutSec -eq 30) 'Freshness must query the pinned repository using bounded read-only HTTP.'
        Assert-True ($Headers.Authorization -ceq 'Bearer fixture-read-only-token') 'GitHub token must be a header, never a URL or log field.'
        if ($script:apiFailure) { throw 'failure containing fixture-read-only-token' }
        return [pscustomobject]@{ object = [pscustomobject]@{ sha = $commit } }
    }
    Assert-True ((Get-GitHubDeploymentData -Path 'git/ref/heads/main').object.sha -ceq $commit) 'Freshness adapter must return the full GitHub commit.'
    $script:apiFailure = $true
    Assert-Failure { Get-GitHubDeploymentData -Path 'git/ref/heads/main' } 'Could not verify automatic deployment freshness with GitHub; no activation is authorized.'
    $env:GH_TOKEN = ''
    Assert-Failure { Get-GitHubDeploymentData -Path 'git/ref/heads/main' } '*read-only GitHub token*'

    $legacyStatePath = Join-Path $fixture 'legacy-state.json'
    [IO.File]::WriteAllText($legacyStatePath, (@{
        schemaVersion = 1; versionOrLabel = 'v1.2.0-test.28'; commitSha = $commit
        deploymentZipSha256 = 'b' * 64; dacpacSha256 = 'd' * 64; deployedUtc = '2026-11-02T00:00:00.000Z'
    } | ConvertTo-Json))
    Assert-True ((Read-DeploymentState -StatePath $legacyStatePath).commitSha -ceq $commit) 'Existing schema-1 deployment state must remain readable without new metadata.'

    # Use real archive validation to prove manifest/full-SHA/label integrity.
    $package = Join-Path $fixture 'package'
    foreach ($directory in @('web/Database', 'deployment', 'hangfire/1.8.25')) {
        New-Item -ItemType Directory -Path (Join-Path $package $directory) -Force | Out-Null
    }
    foreach ($file in @('web/Asap.Web.dll', 'web/Database/Asap.Database.dacpac', 'deployment/Deploy-AsapTest.ps1', 'hangfire/1.8.25/install.sql')) {
        [IO.File]::WriteAllText((Join-Path $package $file), $file)
    }
    $identity = Get-WebPayloadIdentity -WebRoot (Join-Path $package 'web')
    $manifest = [ordered]@{
        schemaVersion = 1; deploymentKind = 'asap-test-iis'; versionOrLabel = $label; commitSha = $commit
        buildUtc = '2026-11-03T00:00:00.000Z'; applicationSchemaVersion = 13; hangfireSchemaVersion = 9
        dacpacSha256 = (Get-FileHash (Join-Path $package 'web/Database/Asap.Database.dacpac')).Hash.ToLowerInvariant()
        hangfireInstallSha256 = (Get-FileHash (Join-Path $package 'hangfire/1.8.25/install.sql')).Hash.ToLowerInvariant()
        webPayloadSha256 = $identity.Hash; webFileCount = $identity.FileCount; webPayloadBytes = $identity.Bytes
    }
    $number = 0
    foreach ($case in @(
        @{ Label = $label; Commit = $commit; Valid = $true },
        @{ Label = 'v1.2.0-test.28'; Commit = $commit; Valid = $true },
        @{ Label = 'test-8f3c2a1d92b5'; Commit = $commit; Valid = $false },
        @{ Label = $label; Commit = '0' * 40; Valid = $false },
        @{ Label = $label; Commit = $commit.Substring(0, 12) + ('0' * 28); Valid = $false }
    )) {
        $number++
        $manifest.versionOrLabel = $case.Label
        $manifest.commitSha = $case.Commit
        [IO.File]::WriteAllText((Join-Path $package 'manifest.json'), ($manifest | ConvertTo-Json))
        $zip = Join-Path $fixture "package-$number.zip"
        Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip
        $arguments = @{ ZipPath = $zip; ExpectedHash = (Get-FileHash $zip).Hash; ExpectedCommit = $commit; ExpectedLabel = $case.Label; StagingPath = Join-Path $fixture "stage-$number" }
        if ($case.Valid) {
            $archive = Test-DeploymentArchive @arguments
            Assert-True ($archive.Manifest.commitSha -ceq $commit -and $archive.Manifest.versionOrLabel -ceq $case.Label) 'Archive must preserve full commit and exact label independently.'
        }
        else { Assert-Failure { Test-DeploymentArchive @arguments } '*' }
    }

    # Run the actual entrypoint statements in isolated processes. Only archive,
    # host/SQL/IIS and GitHub HTTP boundaries are replaced; authorization,
    # freshness, dispatch/plan gates, outcomes and durable state writing are real.
    $main = ($ast.EndBlock.Statements | Where-Object { $_ -isnot [Management.Automation.Language.FunctionDefinitionAst] } |
        ForEach-Object { $_.Extent.Text }) -join "`n"
    $harness = Join-Path $fixture 'entrypoint.ps1'
    $preamble = @'
param([string] $Mode, [string] $ScriptPath, [string] $EventsPath)
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref] $tokens, [ref] $errors)
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) { Invoke-Expression $definition.Extent.Text }
function Event([string] $Text) { Add-Content -LiteralPath $EventsPath -Value $Text }
$ExpectedCommitSha = '8f3c2a1d92b456789012345678901234567890ab'
$ExpectedDeploymentLabel = 'test-8f3c2a1d92b4'
$ExpectedZipSha256 = 'b' * 64; $DeploymentZip = 'fixture'; $ConfigPath = 'fixture'
$ReadinessTimeoutSeconds = 1; $ReadinessPollSeconds = 1
$ValidateOnly = $false; $SqlPlanOnly = $Mode -eq 'plan-main'
$SqlPlanOutputDirectory = Split-Path -Parent $EventsPath
$env:GITHUB_ACTIONS = 'true'; $env:GITHUB_REPOSITORY = 'clcdpc/asap-pocketbase'
$env:GITHUB_SHA = $ExpectedCommitSha; $env:GITHUB_REF = 'refs/heads/main'; $env:GITHUB_EVENT_NAME = 'push'
$env:ASAP_TEST_DEPLOYMENT_ENABLED = 'true'; $env:EXPLICIT_TEST_DEPLOYMENT = 'false'
$env:GITHUB_RUN_ID = '123'; $env:GITHUB_RUN_ATTEMPT = '2'; $env:GH_TOKEN = 'fixture-read-only-token'
$env:GITHUB_OUTPUT = "$EventsPath.outputs"
$env:GITHUB_EVENT_PATH = "$EventsPath.event.json"
$receipt = @{ repository = @{ full_name = $env:GITHUB_REPOSITORY }; ref = 'refs/heads/main'; after = $ExpectedCommitSha; deleted = $false }
switch ($Mode) {
    'pr' { $env:GITHUB_EVENT_NAME = 'pull_request' }
    'branch' { $env:GITHUB_REF = 'refs/heads/other'; $receipt.ref = $env:GITHUB_REF }
    'wrong-repository' { $receipt.repository.full_name = 'outsider/asap-pocketbase' }
    'disabled' { $env:ASAP_TEST_DEPLOYMENT_ENABLED = 'false' }
    'outside-actions' { $env:GITHUB_ACTIONS = 'false' }
    'wrong-sha' { $env:GITHUB_SHA = 'c' * 40 }
    'wrong-event-sha' { $receipt.after = 'c' * 40 }
    'wrong-event-type' { $receipt.after = $true }
    'wrong-receipt-ref' { $receipt.ref = $true }
    'deleted' { $receipt.deleted = $true }
    'mismatched-label' { $ExpectedDeploymentLabel = 'test-8f3c2a1d92b5' }
    'malformed-label' { $ExpectedDeploymentLabel = 'test-whatever' }
    'manual-main' { $env:GITHUB_EVENT_NAME = 'workflow_dispatch'; $env:EXPLICIT_TEST_DEPLOYMENT = 'true'; $receipt.ref = 'main'; $receipt.inputs = @{ deploy_test_iis = 'true' } }
    'manual-not-requested' { $env:GITHUB_EVENT_NAME = 'workflow_dispatch'; $receipt.ref = 'main'; $receipt.inputs = @{ deploy_test_iis = 'false' } }
    'tagged' { $ExpectedDeploymentLabel = 'v1.2.0-test.28'; $env:GITHUB_REF = "refs/tags/$ExpectedDeploymentLabel" }
    'manual-branch' { $ExpectedDeploymentLabel = 'manual-other'; $env:GITHUB_REF = 'refs/heads/other'; $env:GITHUB_EVENT_NAME = 'workflow_dispatch' }
}
[IO.File]::WriteAllText($env:GITHUB_EVENT_PATH, ($receipt | ConvertTo-Json -Depth 5))
if ($Mode -eq 'bad-receipt') { [IO.File]::WriteAllText($env:GITHUB_EVENT_PATH, '{broken') }
$script:statePath = "$EventsPath.state.json"
$script:archive = [pscustomobject]@{
    Manifest = [pscustomobject]@{ versionOrLabel = $ExpectedDeploymentLabel; commitSha = $ExpectedCommitSha; dacpacSha256 = 'd' * 64 }
    DacpacHash = 'd' * 64; ZipHash = 'b' * 64; WebRoot = 'fixture'; DacpacPath = 'fixture'; HangfireScriptPath = 'fixture'
}
$script:state = if ($Mode -in @('already-deployed', 'unhealthy', 'stale', 'newer-state', 'diverged-state', 'ancestor-state')) {
    [pscustomobject]@{ schemaVersion = 1; versionOrLabel = $ExpectedDeploymentLabel; commitSha = $ExpectedCommitSha; dacpacSha256 = 'd' * 64; deploymentZipSha256 = 'b' * 64; deployedUtc = '2026-11-02T00:00:00.000Z' }
} else { $null }
if ($Mode -in @('newer-state', 'diverged-state', 'ancestor-state')) { $script:state.commitSha = 'c' * 40 }
$script:headReads = 0
function Get-GitHubDeploymentData {
    param($Path)
    Event "github:$Path"
    if ($Mode -eq 'api-failure') { throw 'fixture API unavailable' }
    if ($Path -eq 'git/ref/heads/main') {
        $script:headReads++
        $sha = if ($Mode -eq 'stale' -or ($Mode -eq 'head-changed' -and $script:headReads -eq 2)) { 'e' * 40 } else { $ExpectedCommitSha }
        return [pscustomobject]@{ object = [pscustomobject]@{ sha = $sha } }
    }
    if ($Path -cne "compare/$('c' * 40)...$ExpectedCommitSha") { throw "Wrong ancestry request $Path" }
    $status = switch ($Mode) { 'newer-state' { 'behind' }; 'diverged-state' { 'diverged' }; default { 'ahead' } }
    return [pscustomobject]@{ status = $status; merge_base_commit = [pscustomobject]@{ sha = 'c' * 40 } }
}
function Read-HostConfiguration {
    param($Path)
    Event 'host'
    return [pscustomobject]@{
        AllowSqlDataLoss = $Mode -in @('automatic', 'automatic-again', 'manual-main', 'tagged')
        SqlDataLossTarget = [pscustomobject]@{ machineName = 'DEVWEB' }
        StagingRoot = Split-Path -Parent $EventsPath; StatePath = $script:statePath
        ExternalApplicationConfigPath = 'fixture'; IisAppPoolName = 'ASAP'; ReadinessUrl = 'https://devasap.clcohio.org/health/ready'
        ExternalConfiguration = 'fixture'; SqlCmdPath = 'fixture'; SqlPackagePath = 'fixture'
        AsapDatabaseConnectionString = 'fixture'; HangfireDatabaseConnectionString = 'fixture'
    }
}
function Test-DeploymentArchive { param($ZipPath, $ExpectedHash, $ExpectedCommit, $ExpectedLabel, $StagingPath) Event 'archive'; return $script:archive }
function Test-PublishedApplicationConfigPath { param($WebRoot, $ExternalApplicationConfigPath) Event 'pointer' }
function Test-IisConfiguration { param($Config) Event 'iis' }
function Read-DeploymentState { param($StatePath) Event 'state-read'; return $script:state }
function Assert-SqlDataLossTarget { param($Config, $ExpectedTarget) Event 'target'; return $Config.SqlDataLossTarget }
function New-DacpacPlan { param($Config, $Archive, $Target, $OutputDirectory, [switch] $AllowSqlDataLoss) Event "plan:$([bool] $AllowSqlDataLoss)"; return 'fixture' }
function Assert-DevSqlPlan { param($Plan) Event 'plan-checked' }
function Get-HangfireSchemaVersion { param($SqlCmdPath, $ConnectionString) Event 'hangfire'; return 9 }
function Set-NonProductionDatabaseRecovery { param($ExternalConfiguration, $SqlCmdPath, $ConnectionString) Event 'recovery-write' }
function Test-Ready { param($Uri, $TimeoutSeconds, $PollSeconds) Event 'ready'; return $Mode -ne 'unhealthy' }
function Invoke-TestDeployment {
    param($Config, $Archive, $NeedsDacpac, $NeedsHangfire, [switch] $AllowSqlDataLoss, $SqlPlan, $Source, $TimeoutSeconds, $PollSeconds)
    Event "activation:$Source/$([bool] $AllowSqlDataLoss)"
    Write-DeploymentState -Path $Config.StatePath -Manifest $Archive.Manifest -ZipHash $Archive.ZipHash -Source $Source
}
'@
    [IO.File]::WriteAllText($harness, $preamble + "`n" + $main)
    $denied = @('pr', 'branch', 'wrong-repository', 'disabled', 'outside-actions', 'wrong-sha', 'wrong-event-sha', 'wrong-event-type', 'wrong-receipt-ref', 'deleted', 'mismatched-label', 'malformed-label', 'bad-receipt', 'manual-not-requested', 'plan-main')
    foreach ($mode in @('automatic', 'automatic-again', 'automatic-default', 'manual-main', 'tagged', 'manual-branch', 'stale', 'newer-state', 'diverged-state', 'ancestor-state', 'head-changed', 'api-failure', 'already-deployed', 'unhealthy') + $denied) {
        $eventsPath = Join-Path $fixture "$mode.events"
        $output = & (Get-Process -Id $PID).Path -NoLogo -NoProfile -File $harness -Mode $mode -ScriptPath $scriptPath -EventsPath $eventsPath 2>&1
        $exitCode = $LASTEXITCODE
        $events = @(if (Test-Path $eventsPath) { Get-Content $eventsPath })
        $outputs = if (Test-Path "$eventsPath.outputs") { Get-Content "$eventsPath.outputs" -Raw } else { '' }
        if ($mode -in $denied) {
            Assert-True ($exitCode -ne 0 -and $events.Count -eq 0) "Untrusted $mode must fail before host/archive/SQL access: $output"
            continue
        }
        if ($mode -eq 'api-failure') {
            Assert-True ($exitCode -ne 0 -and $events -notcontains 'recovery-write' -and -not ($events -like 'activation:*')) 'Unverifiable freshness must fail before mutation.'
            continue
        }
        Assert-True ($exitCode -eq 0) "Entrypoint $mode failed: $output"
        if ($mode -in @('stale', 'newer-state', 'diverged-state', 'head-changed')) {
            Assert-True ($events -notcontains 'recovery-write' -and -not ($events -like 'activation:*')) "$mode must skip before any recovery/IIS/SQL/web mutation."
            Assert-True ($outputs -match 'deployment_status=skipped' -and $outputs -match 'deployment_reason=Superseded:') 'Skipped workflows must record why, without claiming activation.'
            Assert-True (-not (Test-Path "$eventsPath.state.json")) 'Skipped work must not overwrite successful state.'
            if ($mode -ne 'head-changed') { Assert-True (-not ($events -like 'plan:*')) 'Known stale work must skip even SQL plan generation.' }
        }
        elseif ($mode -eq 'already-deployed') {
            Assert-True ($outputs -match 'deployment_status=already-deployed' -and $events -contains 'ready' -and -not ($events -like 'activation:*')) 'Exact healthy artifact must retain idempotency without a new success record.'
        }
        else {
            Assert-True ($outputs -match 'deployment_status=deployed') 'Success must be explicit and follow durable state.'
            $state = Get-Content "$eventsPath.state.json" -Raw | ConvertFrom-Json -DateKind String
            Assert-True ((Read-DeploymentState -StatePath "$eventsPath.state.json").commitSha -ceq $commit) 'New metadata must preserve existing deployment-state schema compatibility.'
            $source = switch ($mode) { 'manual-main' { 'manual' }; 'manual-branch' { 'manual' }; 'tagged' { 'tagged' }; default { 'automatic' } }
            Assert-True ($state.source -ceq $source -and $state.outcome -ceq 'success' -and $state.targetEnvironment -ceq 'DEVWEB') 'State must record deployment source/outcome/environment.'
            Assert-True ($state.commitSha -ceq $commit -and $state.githubRunId -eq '123' -and $state.githubRunAttempt -eq '2' -and $state.githubRunUrl -ceq 'https://github.com/clcdpc/asap-pocketbase/actions/runs/123' -and $state.deployedUtc.EndsWith('Z')) 'State must retain full commit, run link and UTC timestamp.'
            if ($mode -in @('manual-main', 'manual-branch', 'tagged')) {
                Assert-True (-not ($events -like 'github:*')) 'Intentional manual/tag deployment must bypass automatic freshness.'
            }
            else { Assert-True (@($events | Where-Object { $_ -eq 'github:git/ref/heads/main' }).Count -eq 2) 'Automatic deployment must check live main before planning and again before mutation.' }
            if ($mode -in @('automatic', 'automatic-again', 'manual-main')) {
                Assert-True ($events -contains "activation:$source/True") 'Persistent DEV SQL override must accept each authorized main deployment without per-run opt-in.'
            }
            if ($mode -eq 'automatic-default') {
                Assert-True ($events -contains 'plan:False' -and $events -contains 'activation:automatic/False') 'Automatic deployment must retain default-off SQL data-loss protection.'
            }
        }
    }
    Write-Host 'Automatic deployment identity, event authorization, freshness, idempotency and traceability entrypoint fixtures passed.'
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if (-not $resolvedFixture.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force -ErrorAction SilentlyContinue
}
