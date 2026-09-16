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

    [string] $ConfigPath = 'C:\ProgramData\ASAP\test-deployment.json',

    [ValidateRange(1, 600)]
    [int] $ReadinessTimeoutSeconds = 120,

    [ValidateRange(1, 30)]
    [int] $ReadinessPollSeconds = 2,

    [switch] $ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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

        if ($relative -match '(?i)(^|/)(node_modules|pb_data|\.git)(/|$)|pocketbase|Asap\.Migration|dev-email|\.(pfx|p12|key|pem)$') {
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

    if ([int] $manifest.applicationSchemaVersion -ne 5 -or
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

    if (-not (Test-Path -LiteralPath $keysPath -PathType Container)) {
        throw 'The external Data Protection key directory is unavailable.'
    }

    if ((Test-PathWithin -CandidatePath $ApplicationConfigPath -DirectoryPath $DeploymentPath) -or
        (Test-PathWithin -CandidatePath $keysPath -DirectoryPath $DeploymentPath)) {
        throw 'External application configuration and Data Protection keys must be outside the replaceable web path.'
    }

    return $configuration
}

function Get-SqlConnectionDetails {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ConnectionString
    )

    try {
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $builder.ConnectionString = $ConnectionString
    }
    catch {
        throw 'SQL deployment connection string is not valid.'
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
    $statePath = Join-Path (Split-Path -Parent $configPath) 'test-deployment-state.json'

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

    [void] (Read-ExternalConfiguration -ApplicationConfigPath $applicationConfigPath -DeploymentPath $deploymentPath)

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
        '-h', '-1',
        '-W'
    )
    if ($connection.TrustServerCertificate) {
        $arguments += '-C'
    }

    return $arguments
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
        [string] $SqlPackagePath,

        [Parameter(Mandatory = $true)]
        [string] $ConnectionString,

        [Parameter(Mandatory = $true)]
        [string] $DacpacPath
    )

    $arguments = @(
        '/Action:Publish',
        "/SourceFile:$DacpacPath",
        "/TargetConnectionString:$ConnectionString",
        '/p:BlockOnPossibleDataLoss=True',
        '/p:CreateNewDatabase=False',
        '/p:DropObjectsNotInSource=False'
    )
    & $SqlPackagePath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Application DACPAC publish failed.'
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

    Stop-WebAppPool -Name $AppPoolName -ErrorAction Stop
    Wait-AppPoolState -AppPoolName $AppPoolName -ExpectedState 'Stopped'
}

function Start-TestAppPool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $AppPoolName
    )

    Start-WebAppPool -Name $AppPoolName -ErrorAction Stop
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
    $null = Test-IisConfiguration -Config $hostConfig
    $state = Read-DeploymentState -StatePath $hostConfig.StatePath
    $hangfireVersion = Get-HangfireSchemaVersion `
        -SqlCmdPath $hostConfig.SqlCmdPath `
        -ConnectionString $hostConfig.HangfireDatabaseConnectionString
    if ($hangfireVersion -gt 9) {
        throw "Hangfire schema version $hangfireVersion is newer than the tested version 9."
    }

    if ($null -ne $state -and
        (Get-ManifestText -Manifest $state -Name 'deploymentZipSha256').Equals($archive.ZipHash, [StringComparison]::OrdinalIgnoreCase) -and
        (Get-ManifestText -Manifest $state -Name 'commitSha').Equals($ExpectedCommitSha, [StringComparison]::OrdinalIgnoreCase) -and
        (Get-ManifestText -Manifest $state -Name 'versionOrLabel').Equals($ExpectedDeploymentLabel, [StringComparison]::Ordinal) -and
        (Test-Ready -Uri $hostConfig.ReadinessUrl -TimeoutSeconds $ReadinessTimeoutSeconds -PollSeconds $ReadinessPollSeconds)) {
        Write-Host 'The exact test-IIS artifact is already recorded and ready; deployment is idempotently complete.'
        exit 0
    }

    $needsDacpac = $null -eq $state -or
        -not (Get-ManifestText -Manifest $state -Name 'dacpacSha256').Equals($archive.DacpacHash, [StringComparison]::OrdinalIgnoreCase)
    $needsHangfire = $hangfireVersion -lt 9

    Stop-TestAppPool -AppPoolName $hostConfig.IisAppPoolName
    if ($needsDacpac) {
        Invoke-DacpacPublish `
            -SqlPackagePath $hostConfig.SqlPackagePath `
            -ConnectionString $hostConfig.AsapDatabaseConnectionString `
            -DacpacPath $archive.DacpacPath
    }
    if ($needsHangfire) {
        Invoke-HangfireSchemaInstall `
            -SqlCmdPath $hostConfig.SqlCmdPath `
            -ConnectionString $hostConfig.HangfireDatabaseConnectionString `
            -InstallScriptPath $archive.HangfireScriptPath
        $installedHangfireVersion = Get-HangfireSchemaVersion `
            -SqlCmdPath $hostConfig.SqlCmdPath `
            -ConnectionString $hostConfig.HangfireDatabaseConnectionString
        if ($installedHangfireVersion -ne 9) {
            throw "Hangfire schema preparation completed without expected version 9 (actual $installedHangfireVersion)."
        }
    }

    $backupPath = Backup-WebPayload -DeploymentPath $hostConfig.DeploymentPath -BackupRoot $hostConfig.BackupRoot
    Write-Host "Retained previous web payload at $backupPath."
    Replace-WebPayload -StagedWebRoot $archive.WebRoot -DeploymentPath $hostConfig.DeploymentPath
    Start-TestAppPool -AppPoolName $hostConfig.IisAppPoolName
    if (-not (Test-Ready -Uri $hostConfig.ReadinessUrl -TimeoutSeconds $ReadinessTimeoutSeconds -PollSeconds $ReadinessPollSeconds)) {
        throw 'Test-IIS /health/ready did not return HTTP 200 with status healthy within the bounded timeout.'
    }

    Write-DeploymentState -Path $hostConfig.StatePath -Manifest $archive.Manifest -ZipHash $archive.ZipHash
    Write-Host "Test-IIS deployment succeeded for $($archive.Manifest.versionOrLabel) at $($archive.Manifest.commitSha)."
}
finally {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
}
