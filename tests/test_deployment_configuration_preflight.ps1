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
    'Get-RequiredText',
    'Get-FullPath',
    'Test-PathWithin',
    'Test-PublishedApplicationConfigPath'
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

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("asap-deployment-config-test-" + [guid]::NewGuid().ToString('N'))
$webRoot = Join-Path $fixtureRoot 'web'
$externalRoot = Join-Path $fixtureRoot 'External'
try {
    New-Item -ItemType Directory -Force -Path $webRoot, $externalRoot | Out-Null
    $externalPath = Join-Path $externalRoot 'application.json'
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

Write-Host 'Deployment application-configuration preflight tests passed.'
