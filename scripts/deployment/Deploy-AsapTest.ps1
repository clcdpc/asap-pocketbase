[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $DeploymentZip,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $ExpectedZipSha256,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string] $ExpectedCommitSha,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ExpectedDeploymentLabel,

    [string] $ConfigPath = 'C:\ProgramData\clc-asap\Config\deployment.json',

    [ValidateRange(1, 600)]
    [int] $ReadinessTimeoutSeconds = 120,

    [ValidateRange(1, 30)]
    [int] $ReadinessPollSeconds = 2,

    [switch] $ValidateOnly,

    [switch] $SqlPlanOnly,

    [switch] $AllowSqlDataLoss,

    [string] $ReviewedSqlPlanSha256 = '',

    [string] $ExpectedSqlDataLossTarget = '',

    [string] $SqlPlanOutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Run-scoped controls, never application settings. Reject ambiguous combinations
# before even reading host configuration (including during package validation).
if ($ValidateOnly -and ($SqlPlanOnly -or $AllowSqlDataLoss -or $ReviewedSqlPlanSha256)) {
    throw 'ValidateOnly cannot be combined with SQL plan or data-loss controls.'
}
if ($SqlPlanOnly -and $AllowSqlDataLoss) {
    throw 'Choose SqlPlanOnly or AllowSqlDataLoss, not both.'
}
if ($AllowSqlDataLoss -and $ReviewedSqlPlanSha256 -cnotmatch '^[0-9a-fA-F]{64}$') {
    throw 'AllowSqlDataLoss requires a reviewed SQL plan SHA-256.'
}
if (-not $AllowSqlDataLoss -and $ReviewedSqlPlanSha256) {
    throw 'ReviewedSqlPlanSha256 requires AllowSqlDataLoss.'
}
if (($SqlPlanOnly -or $AllowSqlDataLoss) -and
    ([string]::IsNullOrWhiteSpace($ExpectedSqlDataLossTarget) -or [string]::IsNullOrWhiteSpace($SqlPlanOutputDirectory))) {
    throw 'SQL plan/data-loss controls require independently pinned target identity and a plan output directory.'
}

function Get-Sha256Hex {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]] $Bytes
    )

    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($hasher.ComputeHash($Bytes)).ToLowerInvariant()
    }
    finally {
        $hasher.Dispose()
    }
}

function Get-RequiredText {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string] $property.Value)) {
        throw "Host configuration is missing $Name."
    }

    return [string] $property.Value
}

function Get-ManifestText {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Manifest,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $property = $Manifest.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string] $property.Value)) {
        throw "Deployment manifest is missing $Name."
    }

    return [string] $property.Value
}

function Get-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if (-not [System.IO.Path]::IsPathFullyQualified($Path)) {
        throw "$Name must be an absolute path."
    }

    try {
        return [System.IO.Path]::GetFullPath($Path).TrimEnd([char] '\', [char] '/')
    }
    catch {
        throw "$Name is not a valid path."
    }
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CandidatePath,

        [Parameter(Mandatory = $true)]
        [string] $DirectoryPath
    )

    $candidate = [System.IO.Path]::GetFullPath($CandidatePath).TrimEnd([char] '\', [char] '/')
    $directory = [System.IO.Path]::GetFullPath($DirectoryPath).TrimEnd([char] '\', [char] '/')
    return $candidate.Equals($directory, [StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartsWith($directory + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartsWith($directory + [System.IO.Path]::AltDirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-ToolPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ConfiguredPath,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ([System.IO.Path]::IsPathRooted($ConfiguredPath)) {
        if (-not (Test-Path -LiteralPath $ConfiguredPath -PathType Leaf)) {
            throw "$Name was not found at the configured path."
        }

        return [System.IO.Path]::GetFullPath($ConfiguredPath)
    }

    $command = Get-Command $ConfiguredPath -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $command) {
        throw "$Name '$ConfiguredPath' was not found on PATH."
    }

    return $command.Source
}

function Get-WebPayloadIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string] $WebRoot
    )

    $entries = @(
        Get-ChildItem -LiteralPath $WebRoot -Recurse -File -Force |
            ForEach-Object {
                [pscustomobject]@{
                    RelativePath = [System.IO.Path]::GetRelativePath($WebRoot, $_.FullName).Replace([System.IO.Path]::DirectorySeparatorChar, '/')
                    Length = [long] $_.Length
                    Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            } |
            Sort-Object RelativePath
    )

    if ($entries.Count -eq 0) {
        throw 'The deployment ZIP contains an empty web payload.'
    }

    $identityText = ($entries | ForEach-Object { "$($_.RelativePath)|$($_.Length)|$($_.Hash)" }) -join "`n"
    [pscustomobject]@{
        Hash = Get-Sha256Hex -Bytes ([Text.Encoding]::UTF8.GetBytes($identityText))
        FileCount = $entries.Count
        Bytes = [long](($entries | Measure-Object -Property Length -Sum).Sum)
    }
}

function Test-DeploymentArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ZipPath,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedHash,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedCommit,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedLabel,

        [Parameter(Mandatory = $true)]
        [string] $StagingPath
    )

    $actualZipHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $actualZipHash.Equals($ExpectedHash.ToLowerInvariant(), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Deployment ZIP SHA-256 mismatch."
    }

    New-Item -ItemType Directory -Force -Path $StagingPath | Out-Null
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $StagingPath -Force

    $files = @(Get-ChildItem -LiteralPath $StagingPath -Recurse -File -Force)
    foreach ($file in $files) {
        $relative = [System.IO.Path]::GetRelativePath($StagingPath, $file.FullName).Replace([System.IO.Path]::DirectorySeparatorChar, '/')
        $allowed = $relative.Equals('manifest.json', [StringComparison]::OrdinalIgnoreCase) -or
            $relative.StartsWith('web/', [StringComparison]::OrdinalIgnoreCase) -or
            $relative.StartsWith('deployment/', [StringComparison]::OrdinalIgnoreCase) -or
            $relative.StartsWith('hangfire/1.8.25/', [StringComparison]::OrdinalIgnoreCase)
        if (-not $allowed) {
            throw "Deployment ZIP contains an unexpected file: $relative"
        }

        if ($relative -match '(?i)(^|/)(node_modules|pb_data|\.git)(/|$)|(^|/)(application|deployment)\.json$|pocketbase|Asap\.Migration|dev-email|\.(pfx|p12|key|pem)$') {
            throw "Deployment ZIP contains a forbidden deployment file: $relative"
        }
    }

    $manifestPath = Join-Path $StagingPath 'manifest.json'
    $webRoot = Join-Path $StagingPath 'web'
    $deploymentScriptPath = Join-Path $StagingPath 'deployment\Deploy-AsapTest.ps1'
    $hangfireScriptPath = Join-Path $StagingPath 'hangfire\1.8.25\install.sql'
    $dacpacPath = Join-Path $webRoot 'Database\Asap.Database.dacpac'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $webRoot -PathType Container) -or
        -not (Test-Path -LiteralPath $deploymentScriptPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $hangfireScriptPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dacpacPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $webRoot 'Asap.Web.dll') -PathType Leaf)) {
        throw 'Deployment ZIP is missing a required web, DACPAC, deployment, or Hangfire payload.'
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -DateKind String
    }
    catch {
        throw 'Deployment manifest is not valid JSON.'
    }

    if ([int] $manifest.schemaVersion -ne 1 -or
        -not (Get-ManifestText -Manifest $manifest -Name 'deploymentKind').Equals('asap-test-iis', [StringComparison]::Ordinal)) {
        throw 'Deployment manifest schema or kind is unsupported.'
    }

    $manifestLabel = Get-ManifestText -Manifest $manifest -Name 'versionOrLabel'
    $manifestCommit = Get-ManifestText -Manifest $manifest -Name 'commitSha'
    if (-not $manifestLabel.Equals($ExpectedLabel, [StringComparison]::Ordinal) -or
        -not $manifestCommit.Equals($ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Deployment manifest identity does not match the expected workflow identity.'
    }

    $buildUtc = Get-ManifestText -Manifest $manifest -Name 'buildUtc'
    $parsedBuildUtc = [DateTimeOffset]::MinValue
    if (-not $buildUtc.EndsWith('Z', [StringComparison]::Ordinal) -or
        -not [DateTimeOffset]::TryParse(
            $buildUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref] $parsedBuildUtc)) {
        throw 'Deployment manifest buildUtc is not a UTC timestamp.'
    }

    if ([int] $manifest.applicationSchemaVersion -ne 13 -or
        [int] $manifest.hangfireSchemaVersion -ne 9) {
        throw 'Deployment manifest contains an unsupported schema contract.'
    }

    $dacpacHash = (Get-FileHash -LiteralPath $dacpacPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $dacpacHash.Equals((Get-ManifestText -Manifest $manifest -Name 'dacpacSha256'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'DACPAC SHA-256 does not match the deployment manifest.'
    }

    $hangfireHash = (Get-FileHash -LiteralPath $hangfireScriptPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $hangfireHash.Equals((Get-ManifestText -Manifest $manifest -Name 'hangfireInstallSha256'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Hangfire schema asset SHA-256 does not match the deployment manifest.'
    }

    $webIdentity = Get-WebPayloadIdentity -WebRoot $webRoot
    if (-not $webIdentity.Hash.Equals((Get-ManifestText -Manifest $manifest -Name 'webPayloadSha256'), [StringComparison]::OrdinalIgnoreCase) -or
        [int] $manifest.webFileCount -ne $webIdentity.FileCount -or
        [long] $manifest.webPayloadBytes -ne $webIdentity.Bytes) {
        throw 'Published web payload identity does not match the deployment manifest.'
    }

    [pscustomobject]@{
        StagingPath = $StagingPath
        WebRoot = $webRoot
        DacpacPath = $dacpacPath
        HangfireScriptPath = $hangfireScriptPath
        DeploymentScriptPath = $deploymentScriptPath
        Manifest = $manifest
        ZipHash = $actualZipHash
        DacpacHash = $dacpacHash
    }
}

function Test-PublishedApplicationConfigPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $WebRoot,

        [Parameter(Mandatory = $true)]
        [string] $ExternalApplicationConfigPath
    )

    $appsettingsPath = Join-Path $WebRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $appsettingsPath -PathType Leaf)) {
        throw 'Published web payload is missing appsettings.json.'
    }

    try {
        $bootstrapConfiguration = Get-Content -LiteralPath $appsettingsPath -Raw | ConvertFrom-Json
    }
    catch {
        throw 'Published appsettings.json is not valid JSON.'
    }

    $asapProperty = $bootstrapConfiguration.PSObject.Properties['Asap']
    if ($null -eq $asapProperty -or $null -eq $asapProperty.Value) {
        throw 'Published appsettings.json is missing Asap.ConfigFile.'
    }

    $publishedConfigPath = Get-FullPath `
        -Path (Get-RequiredText -Object $asapProperty.Value -Name 'ConfigFile') `
        -Name 'Published Asap.ConfigFile'
    $deploymentConfigPath = Get-FullPath `
        -Path $ExternalApplicationConfigPath `
        -Name 'ExternalApplicationConfigPath'

    if (-not $publishedConfigPath.Equals($deploymentConfigPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Published Asap:ConfigFile '$publishedConfigPath' does not match ExternalApplicationConfigPath '$deploymentConfigPath'."
    }
}

function Read-DeploymentState {
    param(
        [Parameter(Mandatory = $true)]
        [string] $StatePath
    )

    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        return $null
    }

    try {
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    }
    catch {
        throw 'The previous test deployment state file is not valid JSON.'
    }

    if ([int] $state.schemaVersion -ne 1) {
        throw 'The previous test deployment state file has an unsupported schema.'
    }

    foreach ($name in @('versionOrLabel', 'commitSha', 'deploymentZipSha256', 'dacpacSha256', 'deployedUtc')) {
        [void] (Get-ManifestText -Manifest $state -Name $name)
    }

    return $state
}

function Read-ExternalConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ApplicationConfigPath,

        [Parameter(Mandatory = $true)]
        [string] $DeploymentPath
    )

    try {
        $configuration = Get-Content -LiteralPath $ApplicationConfigPath -Raw | ConvertFrom-Json
        $keysPath = Get-RequiredText -Object $configuration.Application -Name 'DataProtectionKeysPath'
        $keysPath = Get-FullPath -Path $keysPath -Name 'Application.DataProtectionKeysPath'
    }
    catch {
        throw 'The external application configuration is missing or invalid.'
    }

    Assert-NonProductionConfiguration -Configuration $configuration

    if (-not (Test-Path -LiteralPath $keysPath -PathType Container)) {
        throw 'The external Data Protection key directory is unavailable.'
    }

    if ((Test-PathWithin -CandidatePath $ApplicationConfigPath -DirectoryPath $DeploymentPath) -or
        (Test-PathWithin -CandidatePath $keysPath -DirectoryPath $DeploymentPath)) {
        throw 'External application configuration and Data Protection keys must be outside the replaceable web path.'
    }

    return $configuration
}

function Assert-NonProductionConfiguration {
    param([Parameter(Mandatory = $true)] [object] $Configuration)

    $environment = $Configuration.PSObject.Properties['Environment']
    $marker = if ($null -ne $environment -and $null -ne $environment.Value) {
        $environment.Value.PSObject.Properties['IsNonProduction']
    }
    if ($null -eq $marker -or $marker.Value -isnot [bool] -or $marker.Value -ne $true) {
        throw 'Test deployment requires external Environment.IsNonProduction to be exactly true.'
    }
}

function Get-SqlConnectionDetails {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ConnectionString
    )

    try {
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $builder.PSObject.Properties['ConnectionString'].Value = $ConnectionString
    }
    catch {
        throw 'SQL deployment connection string is not valid.'
    }

    foreach ($aliases in @(
        @('Data Source', 'Server', 'Address', 'Addr', 'Network Address'),
        @('Initial Catalog', 'Database'),
        @('Integrated Security', 'Trusted_Connection')
    )) {
        if (@($aliases | Where-Object { $builder.ContainsKey($_) }).Count -gt 1) {
            throw 'SQL connection string contains ambiguous aliases.'
        }
    }

    $dataSource = $null
    foreach ($name in @('Data Source', 'Server', 'Address', 'Addr', 'Network Address')) {
        if ($builder.ContainsKey($name)) {
            $dataSource = [string] $builder[$name]
            break
        }
    }
    $database = $null
    foreach ($name in @('Initial Catalog', 'Database')) {
        if ($builder.ContainsKey($name)) {
            $database = [string] $builder[$name]
            break
        }
    }
    $integratedValue = $null
    foreach ($name in @('Integrated Security', 'Trusted_Connection')) {
        if ($builder.ContainsKey($name)) {
            $integratedValue = [string] $builder[$name]
            break
        }
    }
    $trustValue = $null
    if ($builder.ContainsKey('TrustServerCertificate')) {
        $trustValue = [string] $builder['TrustServerCertificate']
    }

    if ([string]::IsNullOrWhiteSpace($dataSource) -or [string]::IsNullOrWhiteSpace($database)) {
        throw 'SQL deployment connection string must specify a server and database.'
    }

    [pscustomobject]@{
        DataSource = $dataSource
        Database = $database
        IntegratedSecurity = $integratedValue -match '^(?i:true|yes|sspi|1)$'
        TrustServerCertificate = $trustValue -match '^(?i:true|yes|1)$'
    }
}

function Read-HostConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $configPath = Get-FullPath -Path $Path -Name 'ConfigPath'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "Host deployment configuration was not found at $configPath."
    }

    try {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    }
    catch {
        throw 'Host deployment configuration is not valid JSON.'
    }

    $deploymentPath = Get-FullPath -Path (Get-RequiredText -Object $config -Name 'DeploymentPath') -Name 'DeploymentPath'
    $stagingRoot = Get-FullPath -Path (Get-RequiredText -Object $config -Name 'StagingRoot') -Name 'StagingRoot'
    $backupRoot = Get-FullPath -Path (Get-RequiredText -Object $config -Name 'BackupRoot') -Name 'BackupRoot'
    $applicationConfigPath = Get-FullPath -Path (Get-RequiredText -Object $config -Name 'ExternalApplicationConfigPath') -Name 'ExternalApplicationConfigPath'
    $statePath = Join-Path (Split-Path -Parent $configPath) 'deployment-state.json'

    foreach ($directory in @(
        @{ Path = $deploymentPath; Name = 'DeploymentPath' },
        @{ Path = $stagingRoot; Name = 'StagingRoot' },
        @{ Path = $backupRoot; Name = 'BackupRoot' }
    )) {
        if (-not (Test-Path -LiteralPath $directory.Path -PathType Container)) {
            throw "$($directory.Name) does not exist."
        }
    }

    if (-not (Test-Path -LiteralPath $applicationConfigPath -PathType Leaf)) {
        throw 'ExternalApplicationConfigPath does not exist.'
    }

    if ((Test-PathWithin -CandidatePath $configPath -DirectoryPath $deploymentPath) -or
        (Test-PathWithin -CandidatePath $statePath -DirectoryPath $deploymentPath) -or
        (Test-PathWithin -CandidatePath $stagingRoot -DirectoryPath $deploymentPath) -or
        (Test-PathWithin -CandidatePath $backupRoot -DirectoryPath $deploymentPath)) {
        throw 'Host configuration, state, staging and backup paths must be outside the replaceable web path.'
    }

    $externalConfiguration = Read-ExternalConfiguration -ApplicationConfigPath $applicationConfigPath -DeploymentPath $deploymentPath

    $readinessText = Get-RequiredText -Object $config -Name 'ReadinessUrl'
    try {
        $readinessUri = [Uri]::new($readinessText, [UriKind]::Absolute)
    }
    catch {
        throw 'ReadinessUrl is not an absolute HTTPS URL.'
    }
    if ($readinessUri.Scheme -ne 'https' -or
        $readinessUri.AbsolutePath.TrimEnd('/') -ne '/health/ready') {
        throw 'ReadinessUrl must be an HTTPS /health/ready URL.'
    }

    foreach ($connectionName in @('AsapDatabaseConnectionString', 'HangfireDatabaseConnectionString')) {
        $connectionString = Get-RequiredText -Object $config -Name $connectionName
        try {
            $connection = Get-SqlConnectionDetails -ConnectionString $connectionString
        }
        catch {
            throw "$connectionName is not a valid SQL connection string."
        }
        if (-not $connection.IntegratedSecurity) {
            throw "$connectionName must use Windows Integrated Security."
        }
    }

    $sqlPackagePath = Resolve-ToolPath -ConfiguredPath (Get-RequiredText -Object $config -Name 'SqlPackagePath') -Name 'SqlPackage'
    $sqlCmdPath = Resolve-ToolPath -ConfiguredPath (Get-RequiredText -Object $config -Name 'SqlCmdPath') -Name 'sqlcmd'

    [pscustomobject]@{
        ConfigPath = $configPath
        StatePath = $statePath
        IisSiteName = Get-RequiredText -Object $config -Name 'IisSiteName'
        IisAppPoolName = Get-RequiredText -Object $config -Name 'IisAppPoolName'
        DeploymentPath = $deploymentPath
        StagingRoot = $stagingRoot
        BackupRoot = $backupRoot
        ExternalApplicationConfigPath = $applicationConfigPath
        ExternalConfiguration = $externalConfiguration
        ReadinessUrl = $readinessUri.AbsoluteUri
        AsapDatabaseConnectionString = Get-RequiredText -Object $config -Name 'AsapDatabaseConnectionString'
        HangfireDatabaseConnectionString = Get-RequiredText -Object $config -Name 'HangfireDatabaseConnectionString'
        SqlPackagePath = $sqlPackagePath
        SqlCmdPath = $sqlCmdPath
    }
}

function Get-SqlCmdArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ConnectionString
    )

    $connection = Get-SqlConnectionDetails -ConnectionString $ConnectionString
    if (-not $connection.IntegratedSecurity) {
        throw 'SQL deployment connections must use Windows Integrated Security.'
    }

    $arguments = @(
        '-S', [string] $connection.DataSource,
        '-d', [string] $connection.Database,
        '-E',
        '-b',
        '-I',
        '-h', '-1',
        '-W'
    )
    if ($connection.TrustServerCertificate) {
        $arguments += '-C'
    }

    return $arguments
}

function Get-DatabaseRecoveryModel {
    param(
        [Parameter(Mandatory = $true)] [string] $SqlCmdPath,
        [Parameter(Mandatory = $true)] [string] $ConnectionString
    )

    $arguments = @(Get-SqlCmdArguments -ConnectionString $ConnectionString)
    $output = & $SqlCmdPath @arguments '-Q' 'SET NOCOUNT ON; SELECT recovery_model_desc FROM sys.databases WHERE database_id = DB_ID();' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'ASAP database recovery-model query failed.'
    }
    $models = @($output | ForEach-Object { ([string] $_).Trim() } | Where-Object { $_ -cmatch '^(SIMPLE|FULL|BULK_LOGGED)$' })
    if ($models.Count -ne 1) {
        throw 'ASAP database recovery-model query returned no unique usable model.'
    }
    return $models[0]
}

function Set-NonProductionDatabaseRecovery {
    param(
        [Parameter(Mandatory = $true)] [object] $ExternalConfiguration,
        [Parameter(Mandatory = $true)] [string] $SqlCmdPath,
        [Parameter(Mandatory = $true)] [string] $ConnectionString
    )

    Assert-NonProductionConfiguration -Configuration $ExternalConfiguration
    $model = Get-DatabaseRecoveryModel -SqlCmdPath $SqlCmdPath -ConnectionString $ConnectionString
    if ($model -eq 'SIMPLE') {
        Write-Host 'Non-production ASAP database recovery is already SIMPLE; no change required.'
        return
    }

    $arguments = @(Get-SqlCmdArguments -ConnectionString $ConnectionString)
    $null = & $SqlCmdPath @arguments '-Q' 'ALTER DATABASE CURRENT SET RECOVERY SIMPLE;' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not change the non-production ASAP database recovery model from $model to SIMPLE."
    }
    $verified = Get-DatabaseRecoveryModel -SqlCmdPath $SqlCmdPath -ConnectionString $ConnectionString
    if ($verified -ne 'SIMPLE') {
        throw "Non-production ASAP database recovery must be SIMPLE after enforcement (actual $verified)."
    }
    Write-Host "Changed non-production ASAP database recovery from $model to SIMPLE and verified it."
}

function Get-HangfireSchemaVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SqlCmdPath,

        [Parameter(Mandatory = $true)]
        [string] $ConnectionString
    )

    $query = "IF OBJECT_ID(N'[HangFire].[Schema]', N'U') IS NULL SELECT 0; ELSE SELECT ISNULL(MAX([Version]), 0) FROM [HangFire].[Schema];"
    $arguments = @(Get-SqlCmdArguments -ConnectionString $ConnectionString)
    $output = & $SqlCmdPath @arguments '-Q' $query 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'The Hangfire schema preflight query failed.'
    }

    $values = @(
        $output |
            ForEach-Object { [string] $_ } |
            Where-Object { $_ -match '^\s*\d+\s*$' } |
            Select-Object -Last 1
    )
    if ($values.Count -ne 1) {
        throw 'The Hangfire schema preflight returned no usable version.'
    }

    return [int] $values[0].Trim()
}

function Invoke-HangfireSchemaInstall {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SqlCmdPath,

        [Parameter(Mandatory = $true)]
        [string] $ConnectionString,

        [Parameter(Mandatory = $true)]
        [string] $InstallScriptPath
    )

    $arguments = @(Get-SqlCmdArguments -ConnectionString $ConnectionString)
    & $SqlCmdPath @arguments '-i' $InstallScriptPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Hangfire schema 9 preparation failed.'
    }
}

function Invoke-DacpacPublish {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SqlCmdPath,

        [Parameter(Mandatory = $true)]
        [string] $SqlPackagePath,

        [Parameter(Mandatory = $true)]
        [string] $ConnectionString,

        [Parameter(Mandatory = $true)]
        [string] $DacpacPath,

        [switch] $AllowSqlDataLoss,

        [object] $Plan,

        [string] $ReviewedPlanSha256 = ''
    )

    if ($AllowSqlDataLoss) {
        Assert-ReviewedSqlPlan -Plan $Plan -ReviewedHash $ReviewedPlanSha256
    }

    # Execute the validated DACPAC's own idempotent schema guards/retirements
    # before any publish. The opt-in never bypasses schema identity/version checks.
    $package = [IO.Compression.ZipFile]::OpenRead($DacpacPath)
    $preflightPath = [IO.Path]::GetTempFileName()
    try {
        $entry = $package.GetEntry('predeploy.sql')
        if ($null -eq $entry) {
            throw 'Application DACPAC has no pre-deployment contract.'
        }
        $reader = [IO.StreamReader]::new($entry.Open())
        try {
            [IO.File]::WriteAllText($preflightPath, $reader.ReadToEnd(), [Text.UTF8Encoding]::new($false))
        }
        finally {
            $reader.Dispose()
        }
        $sqlArguments = @(Get-SqlCmdArguments -ConnectionString $ConnectionString)
        & $SqlCmdPath @sqlArguments '-i' $preflightPath
        if ($LASTEXITCODE -ne 0) {
            throw 'Application DACPAC pre-deployment contract failed.'
        }
    }
    finally {
        $package.Dispose()
        Remove-Item -LiteralPath $preflightPath -Force -ErrorAction SilentlyContinue
    }

    if ($AllowSqlDataLoss) {
        # Execute the reviewed bytes, rather than asking Publish to replan with
        # all data-loss checks disabled against a possibly changed database.
        Assert-ReviewedSqlPlan -Plan $Plan -ReviewedHash $ReviewedPlanSha256
        $sqlArguments = @(Get-SqlCmdArguments -ConnectionString $ConnectionString)
        & $SqlCmdPath @sqlArguments '-i' $Plan.ScriptPath
        if ($LASTEXITCODE -ne 0) {
            throw 'Reviewed application SQL plan execution failed; database state may have changed.'
        }
        return
    }

    $arguments = @(
        '/Action:Publish',
        "/SourceFile:$DacpacPath",
        "/TargetConnectionString:$ConnectionString",
        '/p:BlockOnPossibleDataLoss=True',
        '/p:CreateNewDatabase=False',
        '/p:DropObjectsNotInSource=False',
        '/p:ScriptDatabaseOptions=False'
    )
    & $SqlPackagePath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Application DACPAC publish failed.'
    }
}

function Assert-SqlDataLossTarget {
    param(
        [Parameter(Mandatory = $true)] [object] $Config,
        [Parameter(Mandatory = $true)] [string] $ExpectedTarget
    )

    Assert-NonProductionConfiguration -Configuration $Config.ExternalConfiguration
    try {
        $target = $ExpectedTarget | ConvertFrom-Json
        foreach ($name in @('machineName', 'iisHostName', 'sqlServerName', 'databaseName')) {
            [void] (Get-RequiredText -Object $target -Name $name)
        }
        $environmentName = Get-RequiredText -Object $Config.ExternalConfiguration.Environment -Name 'Name'
    }
    catch {
        throw 'SQL data-loss target identity is missing or malformed.'
    }
    $readiness = [Uri] $Config.ReadinessUrl
    if ($environmentName -notin @('Test', 'Development', 'Dev') -or
        -not $target.machineName.Equals($env:COMPUTERNAME, [StringComparison]::OrdinalIgnoreCase) -or
        $readiness.Scheme -ne 'https' -or $readiness.Port -ne 443 -or $readiness.UserInfo -ne '' -or
        -not $target.iisHostName.Equals($readiness.DnsSafeHost, [StringComparison]::OrdinalIgnoreCase) -or
        $target.databaseName -in @('master', 'model', 'msdb', 'tempdb')) {
        throw 'SQL data-loss target does not match the pinned non-production machine/site/database.'
    }
    $bindings = @(Get-WebBinding -Name $Config.IisSiteName -Protocol 'https' -ErrorAction Stop)
    if (-not @($bindings | Where-Object {
        ([string] $_.bindingInformation).Equals("*:443:$($target.iisHostName)", [StringComparison]::OrdinalIgnoreCase) -or
        ([string] $_.bindingInformation) -match (':443:' + [regex]::Escape($target.iisHostName) + '$')
    }).Count) {
        throw 'SQL data-loss target host has no matching HTTPS IIS binding.'
    }

    $runtimeConnectionString = Get-RequiredText -Object $Config.ExternalConfiguration.ConnectionStrings -Name 'AsapDatabase'
    foreach ($text in @($Config.AsapDatabaseConnectionString, $runtimeConnectionString)) {
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $builder.PSObject.Properties['ConnectionString'].Value = $text
        foreach ($key in @('Failover Partner', 'AttachDBFilename', 'Initial File Name', 'Extended Properties', 'User Instance')) {
            if ($builder.ContainsKey($key)) {
                throw 'SQL data-loss connections cannot use failover or file-attachment routing.'
            }
        }
    }
    $deploymentSql = Get-SqlConnectionDetails -ConnectionString $Config.AsapDatabaseConnectionString
    $runtimeSql = Get-SqlConnectionDetails -ConnectionString $runtimeConnectionString
    if (-not $runtimeSql.IntegratedSecurity -or -not $deploymentSql.IntegratedSecurity -or
        -not $runtimeSql.DataSource.Equals($deploymentSql.DataSource, [StringComparison]::OrdinalIgnoreCase) -or
        -not $runtimeSql.Database.Equals($deploymentSql.Database, [StringComparison]::OrdinalIgnoreCase) -or
        -not $deploymentSql.Database.Equals($target.databaseName, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'SQL data-loss runtime and deployment database targets do not match.'
    }

    # The connection's server text may be an alias. Verify the server/database
    # actually reached, rather than treating a label/boolean as proof of DEV.
    $arguments = @(Get-SqlCmdArguments -ConnectionString $Config.AsapDatabaseConnectionString)
    $query = "SET NOCOUNT ON; SELECT CONCAT('ASAP_TARGET|', CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')), '|', DB_NAME());"
    $output = & $Config.SqlCmdPath @arguments '-w' '65535' '-Q' $query 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'SQL data-loss target identity query failed.'
    }
    $identities = @($output | ForEach-Object { ([string] $_).Trim() } | Where-Object { $_ -cmatch '^ASAP_TARGET\|[^|]+\|[^|]+$' })
    $expectedIdentity = "ASAP_TARGET|$($target.sqlServerName)|$($target.databaseName)"
    if ($identities.Count -ne 1 -or -not $identities[0].Equals($expectedIdentity, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'SQL data-loss connected server/database does not match the independently pinned target.'
    }
    return [ordered]@{
        machineName = $target.machineName
        iisHostName = $target.iisHostName
        sqlServerName = $target.sqlServerName
        databaseName = $target.databaseName
    }
}

function Get-SqlPlanSha256 {
    param(
        [Parameter(Mandatory = $true)] [object] $Context,
        [Parameter(Mandatory = $true)] [string] $ScriptPath,
        [Parameter(Mandatory = $true)] [string] $ReportPath
    )
    $identity = [ordered]@{
        context = $Context
        scriptSha256 = (Get-FileHash -LiteralPath $ScriptPath -Algorithm SHA256).Hash.ToLowerInvariant()
        reportSha256 = (Get-FileHash -LiteralPath $ReportPath -Algorithm SHA256).Hash.ToLowerInvariant()
    } | ConvertTo-Json -Depth 5 -Compress
    return Get-Sha256Hex -Bytes ([Text.Encoding]::UTF8.GetBytes($identity))
}

function New-DacpacPlan {
    param(
        [Parameter(Mandatory = $true)] [object] $Config,
        [Parameter(Mandatory = $true)] [object] $Archive,
        [Parameter(Mandatory = $true)] [string] $OutputDirectory,
        [object] $Target,
        [switch] $AllowSqlDataLoss
    )
    $directory = Get-FullPath -Path $OutputDirectory -Name 'SqlPlanOutputDirectory'
    if ((Test-PathWithin -CandidatePath $directory -DirectoryPath $Config.DeploymentPath) -or
        (Test-PathWithin -CandidatePath $directory -DirectoryPath $Archive.StagingPath) -or
        (Test-Path -LiteralPath $directory)) {
        throw 'SQL plan output must be a new directory outside web and temporary package staging.'
    }
    New-Item -ItemType Directory -Path $directory | Out-Null
    $scriptPath = Join-Path $directory 'deployment.sql'
    $reportPath = Join-Path $directory 'deployment.xml'
    $blockOption = if ($AllowSqlDataLoss) { '/p:BlockOnPossibleDataLoss=False' } else { '/p:BlockOnPossibleDataLoss=True' }
    $options = @(
        "/SourceFile:$($Archive.DacpacPath)",
        "/TargetConnectionString:$($Config.AsapDatabaseConnectionString)",
        $blockOption,
        '/p:CreateNewDatabase=False',
        '/p:DropObjectsNotInSource=False',
        '/p:ScriptDatabaseOptions=False'
    )
    # Script/DeployReport only read the target. Neither publishes nor runs the
    # DACPAC's pre/post-deployment SQL. A candidate plan may describe ANY loss.
    foreach ($action in @(
        @{ Name = 'Script'; Path = $scriptPath },
        @{ Name = 'DeployReport'; Path = $reportPath }
    )) {
        $null = & $Config.SqlPackagePath "/Action:$($action.Name)" @options "/OutputPath:$($action.Path)" 2>&1
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $action.Path -PathType Leaf) -or
            (Get-Item -LiteralPath $action.Path).Length -eq 0) {
            throw "Application DACPAC $($action.Name) preflight failed; IIS and database were not changed."
        }
    }
    $context = [ordered]@{
        schemaVersion = 1
        commitSha = $Archive.Manifest.commitSha
        allowSqlDataLoss = [bool] $AllowSqlDataLoss
        target = $Target
    }
    $hash = Get-SqlPlanSha256 -Context $context -ScriptPath $scriptPath -ReportPath $reportPath
    # Origin.xml contains build metadata, so a same-source rebuild can change
    # the DACPAC's binary digest. Keep it for audit, outside the approval hash.
    # Exact tested ZIP/manifest/DACPAC integrity is independently checked above;
    # the approved SQL/report bytes describe the effects we actually execute.
    $receipt = [ordered]@{ context = $context; dacpacSha256 = $Archive.DacpacHash; planSha256 = $hash }
    [IO.File]::WriteAllText((Join-Path $directory 'review.json'), ($receipt | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Host "SQL preflight plan: $directory; review SHA-256: $hash."
    return [pscustomobject]@{ Context = $context; ScriptPath = $scriptPath; ReportPath = $reportPath; Hash = $hash }
}

function Assert-ReviewedSqlPlan {
    param([object] $Plan, [string] $ReviewedHash)
    if ($null -eq $Plan -or $ReviewedHash -cnotmatch '^[0-9a-fA-F]{64}$' -or
        $Plan.Context.allowSqlDataLoss -ne $true -or $null -eq $Plan.Context.target -or
        -not $Plan.Hash.Equals($ReviewedHash, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Get-SqlPlanSha256 -Context $Plan.Context -ScriptPath $Plan.ScriptPath -ReportPath $Plan.ReportPath).Equals(
            $ReviewedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'SQL plan is unreviewed or changed. Review the complete new SQL script/report and explicitly authorize its new hash.'
    }
}

function Test-IisConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Config
    )

    try {
        Import-Module WebAdministration -ErrorAction Stop
        $site = Get-Website -Name $Config.IisSiteName -ErrorAction Stop
        $configuredPath = [System.IO.Path]::GetFullPath($Config.DeploymentPath)
        $sitePath = [System.IO.Path]::GetFullPath(
            [Environment]::ExpandEnvironmentVariables([string] $site.PhysicalPath))
        if (-not $configuredPath.Equals($sitePath, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Configured DeploymentPath does not match the IIS site physical path.'
        }
        if (-not ([string] $site.ApplicationPool).Equals($Config.IisAppPoolName, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Configured IisAppPoolName does not match the IIS site application pool.'
        }

        $poolState = Get-WebAppPoolState -Name $Config.IisAppPoolName -ErrorAction Stop
        if ($null -eq $poolState) {
            throw 'Configured IIS application pool was not found.'
        }
    }
    catch {
        throw "IIS preflight failed: $($_.Exception.Message)"
    }
}

function Wait-AppPoolState {
    param(
        [Parameter(Mandatory = $true)]
        [string] $AppPoolName,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedState
    )

    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $state = (Get-WebAppPoolState -Name $AppPoolName -ErrorAction Stop).Value
        if ($state -eq $ExpectedState) {
            return
        }
        Start-Sleep -Seconds 1
    }

    throw "IIS application pool did not reach state $ExpectedState."
}

function Stop-TestAppPool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $AppPoolName
    )

    if ((Get-WebAppPoolState -Name $AppPoolName -ErrorAction Stop).Value -ne 'Stopped') {
        Stop-WebAppPool -Name $AppPoolName -ErrorAction Stop
    }

    Wait-AppPoolState -AppPoolName $AppPoolName -ExpectedState 'Stopped'
}

function Start-TestAppPool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $AppPoolName
    )

    if ((Get-WebAppPoolState -Name $AppPoolName -ErrorAction Stop).Value -ne 'Started') {
        Start-WebAppPool -Name $AppPoolName -ErrorAction Stop
    }

    Wait-AppPoolState -AppPoolName $AppPoolName -ExpectedState 'Started'
}

function Backup-WebPayload {
    param(
        [Parameter(Mandatory = $true)]
        [string] $DeploymentPath,

        [Parameter(Mandatory = $true)]
        [string] $BackupRoot
    )

    $timestamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ')
    $backupPath = Join-Path $BackupRoot "deployment-$timestamp"
    New-Item -ItemType Directory -Force -Path $backupPath | Out-Null
    foreach ($child in Get-ChildItem -LiteralPath $DeploymentPath -Force) {
        Copy-Item -LiteralPath $child.FullName -Destination $backupPath -Recurse -Force
    }

    return $backupPath
}

function Replace-WebPayload {
    param(
        [Parameter(Mandatory = $true)]
        [string] $StagedWebRoot,

        [Parameter(Mandatory = $true)]
        [string] $DeploymentPath
    )

    foreach ($child in Get-ChildItem -LiteralPath $DeploymentPath -Force) {
        Remove-Item -LiteralPath $child.FullName -Recurse -Force
    }
    foreach ($child in Get-ChildItem -LiteralPath $StagedWebRoot -Force) {
        Copy-Item -LiteralPath $child.FullName -Destination $DeploymentPath -Recurse -Force
    }
}

function Test-Ready {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Uri,

        [Parameter(Mandatory = $true)]
        [int] $TimeoutSeconds,

        [Parameter(Mandatory = $true)]
        [int] $PollSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $remaining = [math]::Max(1, [int] (($deadline - (Get-Date)).TotalSeconds))
            $response = Invoke-WebRequest -Uri $Uri -Method Get -TimeoutSec ([math]::Min(10, $remaining)) -UseBasicParsing
            if ($response.StatusCode -eq 200) {
                $payload = $response.Content | ConvertFrom-Json
                if ([string] $payload.status -eq 'healthy') {
                    return $true
                }
            }
        }
        catch {
            # The next bounded poll supplies the retry; the final error is reported by the caller.
        }

        if ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds $PollSeconds
        }
    } while ((Get-Date) -lt $deadline)

    return $false
}

function Write-DeploymentState {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [object] $Manifest,

        [Parameter(Mandatory = $true)]
        [string] $ZipHash
    )

    $state = [ordered]@{
        schemaVersion = 1
        versionOrLabel = Get-ManifestText -Manifest $Manifest -Name 'versionOrLabel'
        commitSha = Get-ManifestText -Manifest $Manifest -Name 'commitSha'
        deploymentZipSha256 = $ZipHash
        dacpacSha256 = Get-ManifestText -Manifest $Manifest -Name 'dacpacSha256'
        deployedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    }
    $json = $state | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Invoke-TestDeployment {
    param(
        [Parameter(Mandatory = $true)] [object] $Config,
        [Parameter(Mandatory = $true)] [object] $Archive,
        [bool] $NeedsDacpac,
        [bool] $NeedsHangfire,
        [switch] $AllowSqlDataLoss,
        [object] $SqlPlan,
        [string] $ReviewedPlanSha256,
        [int] $TimeoutSeconds,
        [int] $PollSeconds
    )
    if ($AllowSqlDataLoss) {
        Assert-ReviewedSqlPlan -Plan $SqlPlan -ReviewedHash $ReviewedPlanSha256
        $null = Assert-SqlDataLossTarget -Config $Config -ExpectedTarget ($SqlPlan.Context.target | ConvertTo-Json -Compress)
    }
    $priorState = (Get-WebAppPoolState -Name $Config.IisAppPoolName -ErrorAction Stop).Value
    if ($priorState -notin @('Started', 'Stopped')) {
        throw "Application pool must be stably Started or Stopped before deployment (actual $priorState)."
    }
    # Failure to retain recovery material must leave IIS and the database alone.
    $backupPath = Backup-WebPayload -DeploymentPath $Config.DeploymentPath -BackupRoot $Config.BackupRoot
    Write-Host "Retained previous web payload at $backupPath."
    $mutationStarted = $false
    try {
        Stop-TestAppPool -AppPoolName $Config.IisAppPoolName
        if ($NeedsDacpac) {
            # A failed native process can have partially committed. Never infer
            # rollback or old-web compatibility from its exit code/message.
            $mutationStarted = $true
            Invoke-DacpacPublish -SqlCmdPath $Config.SqlCmdPath -SqlPackagePath $Config.SqlPackagePath `
                -ConnectionString $Config.AsapDatabaseConnectionString -DacpacPath $Archive.DacpacPath `
                -AllowSqlDataLoss:$AllowSqlDataLoss -Plan $SqlPlan -ReviewedPlanSha256 $ReviewedPlanSha256
        }
        if ($NeedsHangfire) {
            $mutationStarted = $true
            Invoke-HangfireSchemaInstall -SqlCmdPath $Config.SqlCmdPath `
                -ConnectionString $Config.HangfireDatabaseConnectionString -InstallScriptPath $Archive.HangfireScriptPath
            $installedVersion = Get-HangfireSchemaVersion -SqlCmdPath $Config.SqlCmdPath `
                -ConnectionString $Config.HangfireDatabaseConnectionString
            if ($installedVersion -ne 9) {
                throw "Hangfire schema preparation completed without expected version 9 (actual $installedVersion)."
            }
        }
        $mutationStarted = $true
        Replace-WebPayload -StagedWebRoot $Archive.WebRoot -DeploymentPath $Config.DeploymentPath
        Start-TestAppPool -AppPoolName $Config.IisAppPoolName
        if (-not (Test-Ready -Uri $Config.ReadinessUrl -TimeoutSeconds $TimeoutSeconds -PollSeconds $PollSeconds)) {
            throw 'Test-IIS /health/ready did not return HTTP 200 with status healthy within the bounded timeout.'
        }
        Write-DeploymentState -Path $Config.StatePath -Manifest $Archive.Manifest -ZipHash $Archive.ZipHash
    }
    catch {
        $failure = $_
        try {
            if (-not $mutationStarted -and $priorState -eq 'Started') {
                Start-TestAppPool -AppPoolName $Config.IisAppPoolName
                Write-Warning 'Restored the previously running pool: no database or web mutation was attempted.'
            }
            else {
                Stop-TestAppPool -AppPoolName $Config.IisAppPoolName
                Write-Warning "Pool retained Stopped. Database/web compatibility requires operator review; backup: $backupPath. No automatic database rollback or web restore."
            }
        }
        catch {
            Write-Warning "Pool recovery failed; current state is uncertain: $($_.Exception.Message). Backup: $backupPath."
        }
        throw $failure
    }
}

if ($ValidateOnly) {
    $validationStagingPath = Join-Path ([IO.Path]::GetTempPath()) ("asap-test-package-" + [guid]::NewGuid().ToString('N'))
    try {
        $archive = Test-DeploymentArchive `
            -ZipPath $DeploymentZip `
            -ExpectedHash $ExpectedZipSha256 `
            -ExpectedCommit $ExpectedCommitSha `
            -ExpectedLabel $ExpectedDeploymentLabel `
            -StagingPath $validationStagingPath
        Write-Host "Validated test-IIS artifact $($archive.Manifest.versionOrLabel) for commit $($archive.Manifest.commitSha)."
        exit 0
    }
    finally {
        Remove-Item -LiteralPath $validationStagingPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$hostConfig = Read-HostConfiguration -Path $ConfigPath
$stagingPath = Join-Path $hostConfig.StagingRoot ("deployment-" + [guid]::NewGuid().ToString('N'))
try {
    $archive = Test-DeploymentArchive `
        -ZipPath $DeploymentZip `
        -ExpectedHash $ExpectedZipSha256 `
        -ExpectedCommit $ExpectedCommitSha `
        -ExpectedLabel $ExpectedDeploymentLabel `
        -StagingPath $stagingPath
    Test-PublishedApplicationConfigPath `
        -WebRoot $archive.WebRoot `
        -ExternalApplicationConfigPath $hostConfig.ExternalApplicationConfigPath
    $null = Test-IisConfiguration -Config $hostConfig
    $sqlPlan = $null
    if ($SqlPlanOnly -or $AllowSqlDataLoss) {
        $target = Assert-SqlDataLossTarget -Config $hostConfig -ExpectedTarget $ExpectedSqlDataLossTarget
        $sqlPlan = New-DacpacPlan -Config $hostConfig -Archive $archive -Target $target `
            -OutputDirectory $SqlPlanOutputDirectory -AllowSqlDataLoss
        if ($SqlPlanOnly) {
            Write-Host 'SQL PLAN ONLY: review deployment.sql, deployment.xml and review.json. No SQL, recovery option, IIS state, web payload or deployment state was changed.'
            exit 0
        }
        Assert-ReviewedSqlPlan -Plan $sqlPlan -ReviewedHash $ReviewedSqlPlanSha256
    }
    $state = Read-DeploymentState -StatePath $hostConfig.StatePath
    $hangfireVersion = Get-HangfireSchemaVersion `
        -SqlCmdPath $hostConfig.SqlCmdPath `
        -ConnectionString $hostConfig.HangfireDatabaseConnectionString
    if ($hangfireVersion -gt 9) {
        throw "Hangfire schema version $hangfireVersion is newer than the tested version 9."
    }

    $needsDacpac = $null -eq $state -or
        -not (Get-ManifestText -Manifest $state -Name 'dacpacSha256').Equals($archive.DacpacHash, [StringComparison]::OrdinalIgnoreCase)
    $needsHangfire = $hangfireVersion -lt 9
    if ($needsDacpac -and -not $AllowSqlDataLoss) {
        # Detect planning failures before stopping the existing application.
        $protectedPlanDirectory = Join-Path ([IO.Path]::GetTempPath()) ("asap-protected-sql-plan-" + [guid]::NewGuid().ToString('N'))
        try {
            $null = New-DacpacPlan -Config $hostConfig -Archive $archive -OutputDirectory $protectedPlanDirectory
        }
        finally {
            Remove-Item -LiteralPath $protectedPlanDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Set-NonProductionDatabaseRecovery `
        -ExternalConfiguration $hostConfig.ExternalConfiguration `
        -SqlCmdPath $hostConfig.SqlCmdPath `
        -ConnectionString $hostConfig.AsapDatabaseConnectionString

    if ($null -ne $state -and
        (Get-ManifestText -Manifest $state -Name 'deploymentZipSha256').Equals($archive.ZipHash, [StringComparison]::OrdinalIgnoreCase) -and
        (Get-ManifestText -Manifest $state -Name 'commitSha').Equals($ExpectedCommitSha, [StringComparison]::OrdinalIgnoreCase) -and
        (Get-ManifestText -Manifest $state -Name 'versionOrLabel').Equals($ExpectedDeploymentLabel, [StringComparison]::Ordinal) -and
        (Test-Ready -Uri $hostConfig.ReadinessUrl -TimeoutSeconds $ReadinessTimeoutSeconds -PollSeconds $ReadinessPollSeconds)) {
        Write-Host 'The exact test-IIS artifact is already recorded and ready; deployment is idempotently complete.'
        exit 0
    }

    Invoke-TestDeployment -Config $hostConfig -Archive $archive -NeedsDacpac $needsDacpac -NeedsHangfire $needsHangfire `
        -AllowSqlDataLoss:$AllowSqlDataLoss -SqlPlan $sqlPlan -ReviewedPlanSha256 $ReviewedSqlPlanSha256 `
        -TimeoutSeconds $ReadinessTimeoutSeconds -PollSeconds $ReadinessPollSeconds
    Write-Host "Test-IIS deployment succeeded for $($archive.Manifest.versionOrLabel) at $($archive.Manifest.commitSha)."
}
finally {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
}
