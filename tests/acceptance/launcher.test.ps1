# Development tests for actual launcher helpers; no Docker daemon or Pester.
param([ValidateSet('authentication', 'configuration', 'revision', 'isolation', 'proof', 'lifecycle', 'compose')][string]$Case)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
. (Join-Path $repository 'scripts/review/Review-Environment.ps1')
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'scripts/review/Invoke-AsapReview.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Launcher syntax errors.' }
foreach ($definition in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    Invoke-Expression $definition.Extent.Text
}
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock]$Operation, [string]$Pattern) {
    try { & $Operation } catch { if ($_.Exception.Message -match $Pattern) { return }; throw }
    throw "Expected rejection: $Pattern"
}
function Copy-Value($Value) { return $Value | ConvertTo-Json -Depth 30 | ConvertFrom-Json }
function New-State {
    return @{ Sha = 'a' * 40; ShortSha = 'a' * 12; ToolingSha = 'b' * 40; ToolingVersion = '0.3.0'; Kind = 'Acceptance'; RunId = 'c' * 24
        ProjectName = 'asap-acceptance-' + ('c' * 24); Port = 8080; ViewerPort = 6080; LifecycleState = 'Ready'; StatePath = $temporary; Auth = 'Local' }
}
function New-Containers($State) {
    foreach ($name in @('web', 'sql', 'db-init', 'acceptance')) {
        $labels = @{ 'com.docker.compose.service' = $name; 'com.docker.compose.project' = $State.ProjectName
            'org.opencontainers.image.revision' = if ($name -eq 'acceptance') { $State.ToolingSha } else { $State.Sha }
            'org.opencontainers.image.version' = $State.ToolingVersion; 'org.asap.review.tooling-revision' = $State.ToolingSha }
        $ports = if ($name -eq 'web') { @{ '8443/tcp' = @(@{ HostIp = '127.0.0.1'; HostPort = '8080' }) } }
            elseif ($name -eq 'acceptance') { @{ '6080/tcp' = @(@{ HostIp = '127.0.0.1'; HostPort = '6080' }) } } else { @{} }
        $mounts = switch ($name) {
            'web' { @(@{ Destination = '/review/config'; Type = 'volume'; Name = "$($State.ProjectName)_runtime-config"; RW = $false; Source = '/managed/config' },
                @{ Destination = '/review/tls'; Type = 'volume'; Name = "$($State.ProjectName)_tls-private"; RW = $false; Source = '/managed/tls' }) }
            'db-init' { @(@{ Destination = '/review/config'; Type = 'volume'; Name = "$($State.ProjectName)_runtime-config"; RW = $true; Source = '/managed/config' }) }
            'sql' { @(@{ Destination = '/var/opt/mssql'; Type = 'volume'; Name = "$($State.ProjectName)_sql-data"; RW = $true; Source = '/managed/sql' }) }
            'acceptance' { @(@{ Destination = '/review/tls-public'; Type = 'volume'; Name = "$($State.ProjectName)_tls-public"; RW = $false; Source = '/managed/public' }) }
        }
        if ($name -in @('web', 'acceptance')) {
            $mounts = @($mounts) + @(@{ Destination = '/review/auth/credential'; Type = 'bind'; RW = $false; Source = '/managed/auth-credential.txt' })
        }
        @{ Id = $name + '-id'; Image = 'sha256:' + $name; Path = if ($name -eq 'web') { '/review/web-entrypoint.sh' } else { '/other' }
            Config = @{ Labels = $labels; WorkingDir = '/app'; Hostname = $name + '-host'; Env = @('ASPNETCORE_ENVIRONMENT=Testing',
                'Testing__UseDeterministicPatronProvider=true', 'ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=Asap.ReviewProviders',
                "REVIEW_PROJECT_NAME=$($State.ProjectName)", "REVIEW_AUTH_MODE=$($State.Auth)", 'MSSQL_SA_PASSWORD=secret-canary') }
            State = @{ Running = $name -ne 'db-init'; Status = if ($name -eq 'db-init') { 'exited' } else { 'running' }; ExitCode = 0; Health = @{ Status = 'healthy' } }
            HostConfig = @{ NetworkMode = "$($State.ProjectName)_review"; Privileged = $false; Binds = @(); PortBindings = $ports }
            NetworkSettings = @{ Networks = @{ "$($State.ProjectName)_review" = @{} }; Ports = $ports }; Mounts = @($mounts) } | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    }
}
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ('asap-launcher-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    switch ($Case) {
        'authentication' {
            Assert ((Resolve-ReviewAuthentication '' 'smoke') -eq 'Local') 'Suite must default to Local.'
            Assert ((Resolve-ReviewAuthentication '' 'pr410') -eq 'Local') 'Feature suite must default to Local.'
            Assert ((Resolve-ReviewAuthentication '' '') -eq 'Entra') 'Manual review must default to Entra.'
            Assert ((Resolve-ReviewAuthentication 'Entra' 'smoke') -eq 'Entra') 'Explicit Entra was ignored.'
            Assert ((Resolve-ReviewAuthentication 'Local' '') -eq 'Local') 'Explicit Local was ignored.'
            function Read-Host { throw 'Local authentication must never prompt.' }
            $path = Join-Path $temporary 'external-entra.json'
            'reviewer-maintained config, deliberately not JSON' | Set-Content -LiteralPath $path
            $hash = (Get-FileHash -LiteralPath $path).Hash
            foreach ($selectedPath in @($path, (Join-Path $temporary 'absent.json'))) {
                $config = Get-ReviewConfiguration $selectedPath -Auth Local -Explicit
                Assert (Test-ReviewGuid $config.Entra.ClientId) 'Synthetic client must be a valid GUID.'
                Assert ($config.Entra.AllowedTenantIds.Count -eq 1 -and (Test-ReviewGuid $config.Entra.AllowedTenantIds[0])) 'Synthetic tenant must be a valid GUID.'
                Assert ($config.Entra.InitialSuperAdminEmail -ceq 'reviewer@example.org' -and
                    $config.Entra.InitialSuperAdminDisplayName -ceq 'ASAP Reviewer' -and $config.Seed -eq 'Review') 'Synthetic reviewer/fixture contract changed.'
            }
            Assert ((Get-FileHash -LiteralPath $path).Hash -eq $hash) 'Local mode changed reviewer-maintained config.'
            Assert (-not (Test-Path -LiteralPath (Join-Path $temporary 'absent.json'))) 'Local mode created external Entra config.'
            'bootstrap-canary' | Set-Content -LiteralPath (Join-Path $temporary 'auth-credential.txt')
            $credentialHash = (Get-FileHash -LiteralPath (Join-Path $temporary 'auth-credential.txt')).Hash
            Clear-ReviewAuthenticationCredential @{ StatePath = $temporary } -Action Status
            Assert ((Get-FileHash -LiteralPath (Join-Path $temporary 'auth-credential.txt')).Hash -eq $credentialHash) 'Status revoked an active run credential.'
            Clear-ReviewAuthenticationCredential @{ StatePath = $temporary }
            Assert (-not (Test-Path -LiteralPath (Join-Path $temporary 'auth-credential.txt'))) 'Credential cleanup failed.'
        }
        'configuration' {
            $script:answers = [System.Collections.Generic.Queue[string]]::new()
            foreach ($answer in @('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222,33333333-3333-3333-3333-333333333333', 'reviewer@example.org', '')) { $script:answers.Enqueue($answer) }
            function Read-Host { param($Prompt) return $script:answers.Dequeue() }
            $path = Join-Path $temporary 'review.json'
            $config = Get-ReviewConfiguration $path
            Assert ($config.Entra.AllowedTenantIds.Count -eq 2 -and $config.Entra.InitialSuperAdminDisplayName -eq 'ASAP Reviewer') 'First-run prompts did not initialize the expected config.'
            $hash = (Get-FileHash -LiteralPath $path).Hash
            [void](Get-ReviewConfiguration $path)
            Assert ((Get-FileHash -LiteralPath $path).Hash -eq $hash -and $script:answers.Count -eq 0) 'Valid configuration was overwritten or prompted again.'
            $config.Remove('Seed'); $config.Entra.Remove('InitialSuperAdminDisplayName')
            $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path
            $hash = (Get-FileHash -LiteralPath $path).Hash
            $defaults = Get-ReviewConfiguration $path
            Assert ($defaults.Seed -eq 'Review' -and $defaults.Entra.InitialSuperAdminDisplayName -eq 'ASAP Reviewer') 'Established optional defaults changed.'
            Assert ((Get-FileHash -LiteralPath $path).Hash -eq $hash) 'Valid configuration with omitted optional defaults was rewritten.'
            Reject { Get-ReviewConfiguration (Join-Path $temporary 'missing.json') -Explicit } 'not found'
            $config.Entra.ClientId = [guid]::Empty.ToString()
            $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path
            $hash = (Get-FileHash -LiteralPath $path).Hash
            Reject { Get-ReviewConfiguration $path -Explicit } 'ClientId'
            Assert ((Get-FileHash -LiteralPath $path).Hash -eq $hash) 'Invalid explicit configuration was changed.'
        }
        'revision' {
            $source = Join-Path $temporary 'source'; $remote = Join-Path $temporary 'remote.git'; $repoRoot = Join-Path $temporary 'tooling'
            & git init --bare $remote *> $null
            & git init --initial-branch main $source *> $null
            & git -C $source config user.email 'test@example.org'
            & git -C $source config user.name 'Acceptance infrastructure test'
            'application' | Set-Content (Join-Path $source 'identity.txt')
            & git -C $source add identity.txt; & git -C $source commit -m application *> $null
            $application = (& git -C $source rev-parse HEAD).Trim()
            & git -C $source push $remote 'HEAD:refs/pull/410/head' *> $null
            'synthetic merge' | Set-Content (Join-Path $source 'identity.txt')
            & git -C $source commit -am merge *> $null
            $merge = (& git -C $source rev-parse HEAD).Trim()
            & git -C $source push $remote 'HEAD:refs/pull/410/merge' *> $null
            & git clone --no-checkout $remote $repoRoot *> $null
            $Pr = 410; $Commit = $null
            $resolved = Resolve-ApplicationCommit
            Assert ($resolved -eq $application -and $resolved -ne $merge) 'PR resolution selected the synthetic merge instead of HEAD.'
            $Pr = 0; $Commit = $application
            Assert ((Resolve-ApplicationCommit) -eq $application) 'Direct commit selector failed.'
        }
        'isolation' {
            $state = New-State; $containers = @(New-Containers $state)
            Assert-ReviewContainers $state $containers -WithAcceptance
            Assert-ReviewContainers $state @($containers | Where-Object { $_.Config.Labels.'com.docker.compose.service' -ne 'acceptance' })
            foreach ($alter in @(
                { param($c) $c[0].NetworkSettings.Ports.'8443/tcp'[0].HostIp = '0.0.0.0' },
                { param($c) $c[3].NetworkSettings.Ports.'6080/tcp'[0].HostIp = '0.0.0.0' },
                { param($c) $c[1].HostConfig.PortBindings = @{ '1433/tcp' = @(@{ HostIp = '127.0.0.1'; HostPort = '1433' }) } },
                { param($c) $c[0].Config.Labels.'org.opencontainers.image.revision' = 'd' * 40 },
                { param($c) $c[2].State.ExitCode = 1 },
                { param($c) $c[3].Mounts += [pscustomobject]@{ Source = '/var/run/docker.sock'; Destination = '/docker.sock'; RW = $true } },
                { param($c) $c[3].Mounts[0].RW = $true },
                { param($c) $c[0].Config.Env += 'ConnectionStrings__AsapDatabase=secret-canary' },
                { param($c) $c[0].HostConfig.Privileged = $true }
                { param($c) $c[0].Config.Env = @($c[0].Config.Env | Where-Object { $_ -notmatch '^REVIEW_AUTH_MODE=' }) + 'REVIEW_AUTH_MODE=Entra' }
                { param($c) $c[0].Mounts[-1].RW = $true }
                { param($c) $c[2].Mounts += $c[0].Mounts[-1] }
            )) {
                $changed = @(Copy-Value $containers); & $alter $changed
                Reject { Assert-ReviewContainers $state $changed -WithAcceptance } 'isolat|loopback|ports|identity|initialization|configuration|mount|authentication'
            }
            Reject { Assert-ReviewContainers $state ($containers + $containers[0]) -WithAcceptance } 'ambiguous'
        }
        'proof' {
            $state = New-State; $script:containers = @(New-Containers $state)
            New-Item -ItemType Directory -Path (Join-Path $temporary 'control') | Out-Null
            function Invoke-Docker {
                param([string[]]$Arguments, [switch]$Capture)
                switch ($Arguments[0]) {
                    'context' { return '"npipe:////./pipe/dockerDesktopLinuxEngine"' }
                    'ps' { return ($script:containers.Id -join "`n") }
                    'inspect' { return ConvertTo-Json -InputObject $script:containers -Depth 30 }
                    'image' { return ConvertTo-Json -InputObject @($script:containers | Where-Object Image -eq $Arguments[2] | Select-Object Config) -Depth 30 }
                    default { throw 'Unexpected Docker command.' }
                }
            }
            Write-ReviewProof $state -WithAcceptance
            $text = Get-Content -LiteralPath (Join-Path $temporary 'control/proof.json') -Raw
            $proof = $text | ConvertFrom-Json
            Assert ($proof.containers.Count -eq 4 -and $proof.acceptanceRunning -and $proof.state.StatePath -eq '/tmp/asap-review') 'Live proof shape is incorrect.'
            Assert ($text -notmatch 'secret-canary|MSSQL_SA_PASSWORD') 'Host proof leaked a secret.'
            $env:DOCKER_HOST = 'tcp://remote:2376'
            Reject { Write-ReviewProof $state -WithAcceptance } 'Remote Docker'
            Remove-Item Env:DOCKER_HOST
        }
        'lifecycle' {
            $reviewRoot = Join-Path $temporary '.review'; $statePath = Join-Path $reviewRoot 'current.json'
            $state = New-State; $state.StatePath = Join-Path $reviewRoot "acceptance/$($state.RunId)"
            Assert-StateIdentity $state
            Write-State $state
            $second = @{} + $state; $second.RunId = 'd' * 24; $second.ProjectName = 'asap-acceptance-' + $second.RunId
            $second.StatePath = Join-Path $reviewRoot "acceptance/$($second.RunId)"
            Write-State $second
            Assert (Test-Path -LiteralPath (Join-Path $state.StatePath 'state.json')) 'New acceptance discarded retained prior data.'
            $Project = $state.ProjectName
            Assert ((Read-State).ProjectName -eq $state.ProjectName) 'Explicit project lifecycle selection failed.'
            $bad = @{} + $state; $bad.ProjectName = 'production'
            Reject { Assert-StateIdentity $bad } 'identity'
            $bad = @{} + $state; $bad.StatePath = $temporary
            Reject { Assert-StateIdentity $bad } 'identity'
            Reject { Assert-ManagedPath $temporary } 'outside'
            $legacy = Complete-State @{ Sha = 'a' * 40; ShortSha = 'a' * 12; ProjectName = 'asap-review-' + ('a' * 12); StatePath = Join-Path $reviewRoot ('a' * 12) }
            Assert-StateIdentity $legacy
        }
        'compose' {
            $repoRoot = $repository; $reviewRoot = Join-Path $temporary '.review'
            $composeFile = Join-Path $repository 'dev/review/docker/compose.yml'
            $state = New-State; $state.StatePath = Join-Path $reviewRoot "acceptance/$($state.RunId)"
            $state.ConfigPath = Join-Path $temporary 'input.json'; $state.ToolingDirty = $false; $state.ArtifactPath = $temporary
            '{}' | Set-Content -LiteralPath $state.ConfigPath
            Set-ComposeEnvironment $state
            $withAcceptance = Invoke-Docker ((Get-ComposeArguments $state) + @('config', '--format', 'json')) -Capture | ConvertFrom-Json
            Assert (@($withAcceptance.services.PSObject.Properties).Count -eq 4) 'Acceptance profile did not add exactly one service.'
            Assert ($withAcceptance.services.acceptance.ports[0].host_ip -eq '127.0.0.1' -and $withAcceptance.services.web.ports[0].host_ip -eq '127.0.0.1') 'Viewer or HTTPS publication is not loopback-only.'
            Assert ($null -eq $withAcceptance.services.sql.PSObject.Properties['ports']) 'SQL publishes a port.'
            Assert ($withAcceptance.services.acceptance.depends_on.web.condition -eq 'service_healthy') 'Runner does not wait for trusted application readiness.'
            Assert (@($withAcceptance.services.acceptance.volumes | Where-Object { $_.target -match 'docker|tls-private|/review/config' }).Count -eq 0) 'Runner has a forbidden mount.'
            foreach ($name in @('web', 'acceptance')) {
                $authMount = @($withAcceptance.services.$name.volumes | Where-Object target -eq '/review/auth/credential')
                Assert ($authMount.Count -eq 1 -and $authMount[0].read_only) 'Bootstrap credential must be mounted read-only into web and acceptance.'
            }
            Assert (@($withAcceptance.services.'db-init'.volumes | Where-Object target -eq '/review/auth/credential').Count -eq 0) 'Initializer received bootstrap credential.'
            Assert ($withAcceptance.services.web.environment.REVIEW_AUTH_MODE -eq 'Local') 'Compose lost authentication mode.'
            $state.Kind = 'Manual'
            $ordinary = Invoke-Docker ((Get-ComposeArguments $state) + @('config', '--services')) -Capture
            Assert (($ordinary -split '\s+').Count -eq 3 -and $ordinary -notmatch 'acceptance') 'Ordinary review activates the acceptance profile.'
        }
    }
    Write-Host "PASS: $Case"
}
finally {
    $target = [System.IO.Path]::GetFullPath($temporary)
    if ($target.StartsWith([System.IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $target) -like 'asap-launcher-test-*') {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
