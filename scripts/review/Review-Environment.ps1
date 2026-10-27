# Internal helpers for the single Compose launcher. No host Node/.NET tooling.
function Invoke-Docker {
    param([string[]]$Arguments, [switch]$Capture, [int]$TimeoutSeconds = 30)
    $start = [System.Diagnostics.ProcessStartInfo]::new((Get-Command docker -ErrorAction Stop).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw 'Docker did not respond before the timeout. Check Docker Desktop and retry.'
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            # inspect/config output can contain credentials: never include it in errors.
            if (-not $Capture) { Write-Host $errorText }
            throw "Docker $($Arguments[0]) failed (exit $($process.ExitCode)). Check Docker Desktop and review Status."
        }
        if ($Capture) { return $output.Trim() }
        if ($output) { Write-Host $output.TrimEnd() }
        if ($errorText) { Write-Host $errorText.TrimEnd() }
    }
    finally { $process.Dispose() }
}

function Assert-LocalDocker {
    $localEndpoint = { param($Value) $Value -match '^unix:///' -or $Value -match '^npipe:////\./pipe/' }
    if ($env:DOCKER_HOST -and -not (& $localEndpoint $env:DOCKER_HOST)) {
        throw 'Remote Docker engines are not supported.'
    }
    $endpoint = Invoke-Docker @('context', 'inspect', '--format', '{{json .Endpoints.docker.Host}}') -Capture | ConvertFrom-Json
    if (-not (& $localEndpoint $endpoint)) { throw 'Remote Docker contexts are not supported.' }
    return $endpoint
}

function Test-ReviewGuid {
    param($Value)
    $parsed = [guid]::Empty
    return [guid]::TryParse([string]$Value, [ref]$parsed) -and $parsed -ne [guid]::Empty
}

function Get-ReviewConfiguration {
    param([string]$Path, [switch]$Explicit)
    $inputConfig = @{}
    $existingFile = Test-Path -LiteralPath $Path
    if ($existingFile) {
        try { $inputConfig = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable }
        catch { throw "Review configuration is invalid JSON; it was preserved: $Path" }
        if ($inputConfig -isnot [System.Collections.IDictionary]) { throw 'Review configuration must be a JSON object.' }
    }
    elseif ($Explicit) { throw "Review configuration was not found: $Path" }
    if (-not $inputConfig['Entra']) { $inputConfig['Entra'] = @{} }
    $entra = $inputConfig['Entra']
    if ($entra -isnot [System.Collections.IDictionary]) { throw 'Review Entra configuration must be a JSON object.' }
    $changed = $false
    foreach ($field in @('ClientId', 'AllowedTenantIds', 'InitialSuperAdminEmail', 'InitialSuperAdminDisplayName')) {
        $valid = switch ($field) {
            'ClientId' { Test-ReviewGuid $entra[$field] }
            'AllowedTenantIds' {
                $tenants = @($entra[$field])
                $entra[$field] -is [array] -and $tenants.Count -gt 0 -and @($tenants | Where-Object { -not (Test-ReviewGuid $_) }).Count -eq 0 -and
                    @($tenants | Sort-Object -Unique).Count -eq $tenants.Count
            }
            'InitialSuperAdminEmail' {
                $address = $null
                [System.Net.Mail.MailAddress]::TryCreate([string]$entra[$field], [ref]$address) -and $address.Address -eq $entra[$field]
            }
            'InitialSuperAdminDisplayName' { -not [string]::IsNullOrWhiteSpace([string]$entra[$field]) }
        }
        if ($valid) { continue }
        if ($field -eq 'InitialSuperAdminDisplayName') {
            if ($Explicit -or $existingFile) { $entra[$field] = 'ASAP Reviewer'; continue }
            $value = Read-Host 'Review display name [ASAP Reviewer]'
            $entra[$field] = if ($value.Trim()) { $value.Trim() } else { 'ASAP Reviewer' }
            $changed = $true
            continue
        }
        if ($Explicit) { throw "Review configuration needs Entra.$field in $Path" }
        $label = switch ($field) {
            'ClientId' { 'Entra application/client ID' }
            'AllowedTenantIds' { 'Allowed Entra tenant ID(s), separated by commas' }
            'InitialSuperAdminEmail' { 'Initial review super-admin email' }
        }
        $value = Read-Host $label
        $entra[$field] = if ($field -eq 'AllowedTenantIds') { @($value -split '[,;\s]+' | Where-Object { $_ }) } else { $value.Trim() }
        $changed = $true
        # Validate the supplied value before saving or asking for subsequent fields.
        $invalid = switch ($field) {
            'ClientId' { -not (Test-ReviewGuid $entra[$field]) }
            'AllowedTenantIds' { @($entra[$field] | Where-Object { -not (Test-ReviewGuid $_) }).Count -gt 0 -or @($entra[$field]).Count -eq 0 -or @($entra[$field] | Sort-Object -Unique).Count -ne @($entra[$field]).Count }
            'InitialSuperAdminEmail' {
                $address = $null
                -not ([System.Net.Mail.MailAddress]::TryCreate([string]$entra[$field], [ref]$address) -and $address.Address -eq $entra[$field])
            }
        }
        if ($invalid) { throw "Invalid $label. Rerun the command to enter a valid value; existing configuration was preserved." }
    }
    if (-not $inputConfig['Seed']) { $inputConfig['Seed'] = 'Review' }
    if ($inputConfig.Seed -notin @('Review', 'None')) { throw 'Review Seed must be Review or None.' }
    if ($changed) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
        $inputConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
        Write-Host "Saved external review configuration: $Path"
    }
    else { Write-Host "Reusing review configuration: $Path" }
    return $inputConfig
}

function Assert-ReviewContainers {
    param($State, [object[]]$Containers, [switch]$WithAcceptance)
    $expectedServices = @('sql', 'db-init', 'web')
    if ($WithAcceptance) { $expectedServices += 'acceptance' }
    if ($Containers.Count -ne $expectedServices.Count) { throw 'Managed review containers are missing or ambiguous.' }
    foreach ($name in $expectedServices) {
        $matches = @($Containers | Where-Object { $_.Config.Labels.'com.docker.compose.service' -eq $name })
        if ($matches.Count -ne 1) { throw "Expected exactly one managed $name container." }
    }
    foreach ($container in $Containers) {
        $labels = $container.Config.Labels
        $name = $labels.'com.docker.compose.service'
        if ($labels.'com.docker.compose.project' -ne $State.ProjectName -or $container.HostConfig.Privileged -or
            $container.HostConfig.NetworkMode -ne "$($State.ProjectName)_review" -or @($container.HostConfig.Binds | Where-Object { $_ -match 'docker\.sock|docker_engine|/pipe/' }).Count -gt 0 -or
            @($container.Mounts | Where-Object { $_.Source -match 'docker\.sock|docker_engine|/pipe/' }).Count -gt 0 -or
            @($container.NetworkSettings.Networks.PSObject.Properties.Name).Count -ne 1 -or
            @($container.NetworkSettings.Networks.PSObject.Properties.Name)[0] -ne "$($State.ProjectName)_review") {
            throw 'Unsafe review project/network/socket isolation.'
        }
        if ($name -eq 'db-init') {
            if ($container.State.Status -ne 'exited' -or $container.State.ExitCode -ne 0) { throw 'Review database initialization failed.' }
        }
        elseif (-not $container.State.Running) { throw "Review $name is stopped." }
        if ($name -in @('web', 'db-init')) {
            if ($labels.'org.opencontainers.image.revision' -ne $State.Sha -or
                $labels.'org.opencontainers.image.version' -ne $State.ToolingVersion -or
                $labels.'org.asap.review.tooling-revision' -ne $State.ToolingSha) { throw 'Running application/tooling image identity mismatch.' }
        }
        if ($name -eq 'acceptance' -and ($labels.'org.opencontainers.image.revision' -ne $State.ToolingSha -or
            $labels.'org.opencontainers.image.version' -ne $State.ToolingVersion)) { throw 'Acceptance tooling image identity mismatch.' }
        $published = @($container.NetworkSettings.Ports.PSObject.Properties | Where-Object { $null -ne $_.Value -and @($_.Value).Count -gt 0 })
        $configured = @($container.HostConfig.PortBindings.PSObject.Properties | Where-Object { $null -ne $_.Value -and @($_.Value).Count -gt 0 })
        if ($name -in @('web', 'acceptance')) {
            $portKey = if ($name -eq 'web') { '8443/tcp' } else { '6080/tcp' }
            $portValue = if ($name -eq 'web') { $State.Port } else { $State.ViewerPort }
            foreach ($bindings in @($published, $configured)) {
                if ($bindings.Count -ne 1 -or $bindings[0].Name -ne $portKey -or @($bindings[0].Value).Count -ne 1 -or
                    $bindings[0].Value[0].HostIp -ne '127.0.0.1' -or $bindings[0].Value[0].HostPort -ne [string]$portValue) {
                    throw 'Review HTTPS/viewer must be published only on IPv4 loopback.'
                }
            }
        }
        elseif ($published.Count -gt 0 -or $configured.Count -gt 0) { throw 'Review SQL/initializer must not publish host ports.' }
        foreach ($contract in @(
            @('web', '/review/config', 'runtime-config', $false), @('db-init', '/review/config', 'runtime-config', $true),
            @('sql', '/var/opt/mssql', 'sql-data', $true), @('web', '/review/tls', 'tls-private', $false),
            @('acceptance', '/review/tls-public', 'tls-public', $false))) {
            if ($name -ne $contract[0]) { continue }
            $mounts = @($container.Mounts | Where-Object Destination -eq $contract[1])
            if ($mounts.Count -ne 1 -or $mounts[0].Type -ne 'volume' -or $mounts[0].Name -ne "$($State.ProjectName)_$($contract[2])" -or
                $mounts[0].RW -ne $contract[3]) { throw 'Review database/config/certificate volume identity mismatch.' }
        }
        if ($name -eq 'acceptance') {
            $allowed = @('/review/tls-public', '/run/review/control', '/run/review/viewer', '/review/evidence/token', '/artifacts', '/tmp')
            if (@($container.Mounts | Where-Object { $_.Destination -notin $allowed -or
                ($_.Destination -notin @('/artifacts', '/tmp') -and $_.RW) }).Count -gt 0) {
                throw 'Acceptance has unexpected access to private configuration or host mounts.'
            }
        }
    }
    $web = $Containers | Where-Object { $_.Config.Labels.'com.docker.compose.service' -eq 'web' }
    $envMap = @{}
    foreach ($entry in $web.Config.Env) { $key, $value = $entry -split '=', 2; $envMap[$key] = $value }
    if ($envMap.ASPNETCORE_ENVIRONMENT -ne 'Testing' -or $envMap.Testing__UseDeterministicPatronProvider -ne 'true' -or
        $envMap.ASPNETCORE_HOSTINGSTARTUPASSEMBLIES -ne 'Asap.ReviewProviders' -or $envMap.REVIEW_PROJECT_NAME -ne $State.ProjectName -or
        @($envMap.Keys | Where-Object { $_ -match '^(ConnectionStrings__|EmailSafety__|Asap__|Testing__Staff|Authentication__|DOTNET_ADDITIONAL_DEPS$)' }).Count -gt 0 -or
        $web.Path -ne '/review/web-entrypoint.sh' -or $web.Config.WorkingDir -ne '/app' -or $web.State.Health.Status -ne 'healthy') {
        throw 'Healthy deterministic review runtime is required without configuration overrides.'
    }
}

function Write-ReviewProof {
    param($State, [switch]$WithAcceptance)
    $observed = [DateTimeOffset]::UtcNow
    $endpoint = Assert-LocalDocker
    $ids = @(Invoke-Docker @('ps', '-aq', '--filter', "label=com.docker.compose.project=$($State.ProjectName)") -Capture) -split '\s+' | Where-Object { $_ }
    if ($ids.Count -eq 0) { throw 'Managed review containers are missing.' }
    $containers = @(Invoke-Docker (@('inspect') + $ids) -Capture | ConvertFrom-Json)
    Assert-ReviewContainers $State $containers -WithAcceptance:$WithAcceptance
    if (-not (Get-Variable verifiedReviewImages -Scope Script -ErrorAction SilentlyContinue)) { $script:verifiedReviewImages = @{} }
    foreach ($container in $containers | Where-Object { $_.Config.Labels.'com.docker.compose.service' -ne 'sql' }) {
        if ($script:verifiedReviewImages.ContainsKey($container.Image)) { continue }
        $image = @(Invoke-Docker @('image', 'inspect', $container.Image) -Capture | ConvertFrom-Json)[0]
        $keys = @('org.opencontainers.image.revision', 'org.opencontainers.image.version')
        if ($container.Config.Labels.'com.docker.compose.service' -ne 'acceptance') { $keys += 'org.asap.review.tooling-revision' }
        foreach ($key in $keys) {
            if ($image.Config.Labels.$key -ne $container.Config.Labels.$key) { throw 'Container labels differ from the actual image identity.' }
        }
        $script:verifiedReviewImages[$container.Image] = $true
    }
    # Retain only the fields used by the runner. SQL passwords, input config and
    # unrelated environment values never leave this host process.
    $safeContainers = foreach ($container in $containers) {
        $safeEnv = @($container.Config.Env | Where-Object { $_ -match '^(ASPNETCORE_ENVIRONMENT|Testing__UseDeterministicPatronProvider|ASPNETCORE_HOSTINGSTARTUPASSEMBLIES|REVIEW_PROJECT_NAME)=' })
        [ordered]@{
            Id = $container.Id; Image = $container.Image; Path = $container.Path
            Config = @{ Labels = $container.Config.Labels; WorkingDir = $container.Config.WorkingDir; Hostname = $container.Config.Hostname; Env = $safeEnv }
            State = $container.State
            HostConfig = @{ NetworkMode = $container.HostConfig.NetworkMode; Privileged = $container.HostConfig.Privileged; PortBindings = $container.HostConfig.PortBindings }
            NetworkSettings = @{ Networks = $container.NetworkSettings.Networks; Ports = $container.NetworkSettings.Ports }
            Mounts = @($container.Mounts | Select-Object Type,Name,Destination,RW)
        }
    }
    $safeState = [ordered]@{
        Sha = $State.Sha; ShortSha = $State.ShortSha; ProjectName = $State.ProjectName; Port = $State.Port
        ToolingVersion = $State.ToolingVersion; ToolingSha = $State.ToolingSha; LifecycleState = $State.LifecycleState
        StatePath = '/tmp/asap-review'; Kind = $State.Kind; RunId = $State.RunId; ViewerPort = $State.ViewerPort
    }
    if (([DateTimeOffset]::UtcNow - $observed).TotalSeconds -gt 10) { throw 'Docker isolation verification took too long; stale evidence cannot authorize mutations.' }
    $proof = @{ schemaVersion = 1; observedUtc = $observed.ToString('O'); dockerEndpoint = $endpoint
        state = $safeState; containers = @($safeContainers); acceptanceRunning = [bool]$WithAcceptance }
    $path = Join-Path $State.StatePath 'control/proof.json'
    $proof | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath "$path.next" -Encoding utf8NoBOM
    [System.IO.File]::Move("$path.next", $path, $true)
}
