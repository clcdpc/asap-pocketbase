$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$deploymentScriptPath = Join-Path $repositoryRoot 'scripts/deployment/Deploy-AsapTest.ps1'
$tokens = $null
$parseErrors = $null
$scriptAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $deploymentScriptPath,
    [ref] $tokens,
    [ref] $parseErrors)
if ($parseErrors.Count -ne 0) {
    throw 'Deploy-AsapTest.ps1 has PowerShell parse errors.'
}

$functionNames = @(
    'Get-Sha256Hex',
    'Get-RequiredText',
    'Get-ManifestText',
    'Get-FullPath',
    'Test-PathWithin',
    'Get-WebPayloadIdentity',
    'Test-DeploymentArchive',
    'Test-PublishedApplicationConfigPath',
    'Get-SqlConnectionDetails',
    'Get-SqlCmdArguments',
    'Read-ExternalConfiguration',
    'Assert-NonProductionConfiguration',
    'Get-DatabaseRecoveryModel',
    'Set-NonProductionDatabaseRecovery',
    'Wait-AppPoolState',
    'Stop-TestAppPool',
    'Start-TestAppPool'
)
foreach ($functionName in $functionNames) {
    $functionAst = $scriptAst.Find(
        {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $functionName
        },
        $true)
    if ($null -eq $functionAst) {
        throw "Deployment function $functionName was not found."
    }

    Invoke-Expression $functionAst.Extent.Text
}

function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock] $Action,

        [Parameter(Mandatory = $true)]
        [string] $Pattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike $Pattern) {
            throw "Expected error like '$Pattern', got '$($_.Exception.Message)'."
        }

        return
    }

    throw "Expected an error like '$Pattern'."
}

$sqlArguments = @(
    Get-SqlCmdArguments `
        -ConnectionString 'Server=sql.example;Database=Asap;Integrated Security=True;TrustServerCertificate=False'
)
$expectedSqlArguments = @('-S', 'sql.example', '-d', 'Asap', '-E', '-b', '-I', '-h', '-1', '-W')
if (($sqlArguments -join "`n") -ne ($expectedSqlArguments -join "`n")) {
    throw "Unexpected sqlcmd arguments: $($sqlArguments -join ' ')"
}

$trustedSqlArguments = @(
    Get-SqlCmdArguments `
        -ConnectionString 'Server=sql.example;Database=Asap;Integrated Security=True;TrustServerCertificate=True'
)
$expectedTrustedSqlArguments = @($expectedSqlArguments) + '-C'
if (($trustedSqlArguments -join "`n") -ne ($expectedTrustedSqlArguments -join "`n")) {
    throw "Unexpected trusted sqlcmd arguments: $($trustedSqlArguments -join ' ')"
}

$script:appPoolState = 'Stopped'
$script:appPoolStateReads = 0
$script:stopWebAppPoolCalls = 0
function Get-WebAppPoolState {
    [CmdletBinding()]
    param([string] $Name)

    $script:appPoolStateReads++
    return [pscustomobject]@{ Value = $script:appPoolState }
}
function Stop-WebAppPool {
    [CmdletBinding()]
    param([string] $Name)

    $script:stopWebAppPoolCalls++
}

Stop-TestAppPool -AppPoolName 'ASAP'
if ($script:stopWebAppPoolCalls -ne 0) {
    throw 'Stop-TestAppPool must not stop an application pool that is already stopped.'
}
if ($script:appPoolStateReads -ne 2) {
    throw 'Stop-TestAppPool must read the current state and verify the final stopped state.'
}

$script:appPoolState = 'Started'
$script:appPoolStateReads = 0
$script:startWebAppPoolCalls = 0
function Start-WebAppPool {
    [CmdletBinding()]
    param([string] $Name)

    $script:startWebAppPoolCalls++
}

Start-TestAppPool -AppPoolName 'ASAP'
if ($script:startWebAppPoolCalls -ne 0) {
    throw 'Start-TestAppPool must not start an application pool that is already started.'
}
if ($script:appPoolStateReads -ne 2) {
    throw 'Start-TestAppPool must read the current state and verify the final started state.'
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("asap-deployment-config-test-" + [guid]::NewGuid().ToString('N'))
$webRoot = Join-Path $fixtureRoot 'web'
$externalRoot = Join-Path $fixtureRoot 'External'
try {
    New-Item -ItemType Directory -Force -Path $webRoot, $externalRoot | Out-Null
    $externalPath = Join-Path $externalRoot 'application.json'
    $keysPath = Join-Path $externalRoot 'keys'
    New-Item -ItemType Directory -Path $keysPath | Out-Null
    foreach ($environment in @(@{}, @{ IsNonProduction = $false }, @{ IsNonProduction = 'true' }, @{ IsNonProduction = 1 })) {
        $external = @{ Environment = $environment; Application = @{ DataProtectionKeysPath = $keysPath } }
        [IO.File]::WriteAllText($externalPath, ($external | ConvertTo-Json -Depth 4))
        Assert-ThrowsLike -Action {
            Read-ExternalConfiguration -ApplicationConfigPath $externalPath -DeploymentPath $webRoot
        } -Pattern '*Environment.IsNonProduction to be exactly true*'
    }
    $external = @{ Environment = @{ IsNonProduction = $true }; Application = @{ DataProtectionKeysPath = $keysPath } }
    [IO.File]::WriteAllText($externalPath, ($external | ConvertTo-Json -Depth 4))
    $validated = Read-ExternalConfiguration -ApplicationConfigPath $externalPath -DeploymentPath $webRoot
    if ($validated.Environment.IsNonProduction -ne $true) {
        throw 'Preflight must retain the validated external configuration.'
    }
    $normalizedVariant = [IO.Path]::Combine($externalRoot, 'nested', '..', 'APPLICATION.json')
    $appsettings = @{
        Asap = @{
            ConfigFile = $normalizedVariant
        }
    } | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText(
        (Join-Path $webRoot 'appsettings.json'),
        $appsettings,
        [Text.UTF8Encoding]::new($false))

    Test-PublishedApplicationConfigPath `
        -WebRoot $webRoot `
        -ExternalApplicationConfigPath $externalPath

    Assert-ThrowsLike `
        -Action {
            Test-PublishedApplicationConfigPath `
                -WebRoot $webRoot `
                -ExternalApplicationConfigPath (Join-Path $externalRoot 'different.json')
        } `
        -Pattern 'Published Asap:ConfigFile * does not match ExternalApplicationConfigPath *'

    if (Test-PathWithin -CandidatePath $externalPath -DirectoryPath $webRoot) {
        throw 'The external application configuration fixture must remain outside the replaceable web path.'
    }
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$archiveFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("asap-deployment-schema-test-" + [guid]::NewGuid().ToString('N'))
try {
    $packageRoot = Join-Path $archiveFixtureRoot 'package'
    $webRoot = Join-Path $packageRoot 'web'
    $databaseRoot = Join-Path $webRoot 'Database'
    $deploymentRoot = Join-Path $packageRoot 'deployment'
    $hangfireRoot = Join-Path $packageRoot 'hangfire\1.8.25'
    New-Item -ItemType Directory -Force -Path $databaseRoot, $deploymentRoot, $hangfireRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $webRoot 'Asap.Web.dll'), 'web', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $databaseRoot 'Asap.Database.dacpac'), 'dacpac', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $deploymentRoot 'Deploy-AsapTest.ps1'), 'deployment', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $hangfireRoot 'install.sql'), 'hangfire', [Text.UTF8Encoding]::new($false))

    $webIdentity = Get-WebPayloadIdentity -WebRoot $webRoot
    $manifest = [ordered]@{
        schemaVersion = 1
        deploymentKind = 'asap-test-iis'
        versionOrLabel = 'schema-contract-test'
        commitSha = '1111111111111111111111111111111111111111'
        buildUtc = '2026-09-17T00:00:00.000Z'
        applicationSchemaVersion = 12
        hangfireSchemaVersion = 9
        dacpacSha256 = (Get-FileHash -LiteralPath (Join-Path $databaseRoot 'Asap.Database.dacpac') -Algorithm SHA256).Hash.ToLowerInvariant()
        webPayloadSha256 = $webIdentity.Hash
        webFileCount = $webIdentity.FileCount
        webPayloadBytes = $webIdentity.Bytes
        hangfireInstallSha256 = (Get-FileHash -LiteralPath (Join-Path $hangfireRoot 'install.sql') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifestPath = Join-Path $packageRoot 'manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

    $validZip = Join-Path $archiveFixtureRoot 'valid.zip'
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $validZip
    $accepted = Test-DeploymentArchive `
        -ZipPath $validZip `
        -ExpectedHash (Get-FileHash -LiteralPath $validZip -Algorithm SHA256).Hash `
        -ExpectedCommit $manifest.commitSha `
        -ExpectedLabel $manifest.versionOrLabel `
        -StagingPath (Join-Path $archiveFixtureRoot 'valid-staging')
    if ([int] $accepted.Manifest.applicationSchemaVersion -ne 12) {
        throw 'Schema 12 deployment manifest was not accepted.'
    }

    $manifest.applicationSchemaVersion = 6
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $invalidZip = Join-Path $archiveFixtureRoot 'invalid.zip'
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $invalidZip
    Assert-ThrowsLike `
        -Action {
            Test-DeploymentArchive `
                -ZipPath $invalidZip `
                -ExpectedHash (Get-FileHash -LiteralPath $invalidZip -Algorithm SHA256).Hash `
                -ExpectedCommit $manifest.commitSha `
                -ExpectedLabel $manifest.versionOrLabel `
                -StagingPath (Join-Path $archiveFixtureRoot 'invalid-staging')
        } `
        -Pattern 'Deployment manifest contains an unsupported schema contract.'
}
finally {
    Remove-Item -LiteralPath $archiveFixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$script:recoveryModel = 'FULL'
$script:recoveryCalls = [Collections.Generic.List[object]]::new()
$script:recoveryFailure = ''
function Invoke-TestSqlCmd {
    $script:recoveryCalls.Add(@($args))
    $script:LASTEXITCODE = 0
    if ($args -notcontains 'asap-fixture' -or $args -notcontains '-E' -or $args -contains 'hangfire-fixture') {
        throw 'Recovery commands must use only the configured ASAP application database and integrated authentication.'
    }
    $isAlter = $args[-1] -eq 'ALTER DATABASE CURRENT SET RECOVERY SIMPLE;'
    if (($script:recoveryFailure -eq 'query' -and -not $isAlter) -or ($script:recoveryFailure -eq 'alter' -and $isAlter)) {
        $script:LASTEXITCODE = 1
        return 'sqlcmd failed'
    }
    if ($isAlter) {
        if ($script:recoveryFailure -ne 'verify') {
            $script:recoveryModel = 'SIMPLE'
        }
    }
    else {
        return $script:recoveryModel
    }
}
$recoveryParameters = @{
    ExternalConfiguration = [pscustomobject]@{ Environment = [pscustomobject]@{ IsNonProduction = $true } }
    SqlCmdPath = 'Invoke-TestSqlCmd'
    ConnectionString = 'Server=fixture;Database=asap-fixture;Integrated Security=True;TrustServerCertificate=True'
}
Set-NonProductionDatabaseRecovery @recoveryParameters
if ($script:recoveryCalls.Count -ne 3 -or $script:recoveryModel -ne 'SIMPLE') {
    throw 'FULL recovery must be queried, changed once and verified as SIMPLE.'
}
$script:recoveryCalls.Clear()
Set-NonProductionDatabaseRecovery @recoveryParameters
if ($script:recoveryCalls.Count -ne 1) {
    throw 'Already-SIMPLE recovery must require only a query.'
}
foreach ($failure in @('query', 'alter', 'verify')) {
    $script:recoveryModel = 'FULL'
    $script:recoveryFailure = $failure
    Assert-ThrowsLike -Action { Set-NonProductionDatabaseRecovery @recoveryParameters } -Pattern '*recovery*'
}
$script:recoveryFailure = ''
$script:recoveryModel = 'unusable'
Assert-ThrowsLike -Action { Set-NonProductionDatabaseRecovery @recoveryParameters } -Pattern '*no unique usable model*'
$script:recoveryCalls.Clear()
$recoveryParameters.ExternalConfiguration = [pscustomobject]@{ Environment = [pscustomobject]@{ IsNonProduction = $false } }
Assert-ThrowsLike -Action { Set-NonProductionDatabaseRecovery @recoveryParameters } -Pattern '*Environment.IsNonProduction to be exactly true*'
if ($script:recoveryCalls.Count -ne 0) {
    throw 'Production-marked configuration must fail before any recovery query or mutation.'
}

Write-Host 'Deployment application-configuration and recovery preflight tests passed.'
